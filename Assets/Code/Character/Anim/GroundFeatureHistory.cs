using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 绑定一个骨骼通道以及定义其局部采样坐标的参考骨。
    internal struct GroundFeatureBone
    {
        internal GroundFeatureChannel Channel;
        internal TransformStreamHandle Bone, Reference;
        internal byte Bound;
    }

    // 保存一帧原始位置或朝向以及有效性，速度通道另外保留差分位置。
    internal struct GroundFeatureValue
    {
        internal Vector3 Raw, Value;
        internal byte Bound, Valid;
    }

    // 在基础片段混合之后按统一时间游标采集任意人形骨骼通道。
    internal struct GroundFeatureSampler
    {
        [ReadOnly] internal NativeArray<GroundFeatureBone> Bones;
        internal NativeArray<GroundFeatureValue> Ring;
        internal TransformSceneHandle Model;

        // 将有效的原始骨姿写入与双脚相同的时间槽，不读取后续程序性骨骼输出。
        internal void Capture(AnimationStream stream, in CharacterFootObservation before, in CharacterFootObservation current)
        {
            if (Bones.Length == 0 || current.Count == 0 || before.Count > 0 && before.Time == current.Time) return;
            if (!Model.IsValid(stream))
            {
                for (int i = 0; i < Bones.Length; i++) Ring[current.Cursor * Bones.Length + i] = default;
                return;
            }
            Quaternion modelBasis = Model.GetRotation(stream);
            Vector3 modelPosition = Model.GetPosition(stream);
            for (int i = 0; i < Bones.Length; i++)
            {
                var b = Bones[i];
                var value = default(GroundFeatureValue);
                var c = b.Channel;
                if (b.Bound != 0 && b.Bone.IsValid(stream) &&
                    (c.Space == GroundFeatureSpace.Model || b.Reference.IsValid(stream)))
                {
                    Vector3 origin = c.Space == GroundFeatureSpace.Model ? modelPosition : b.Reference.GetPosition(stream);
                    Quaternion basis = c.Space == GroundFeatureSpace.Bone ? b.Reference.GetRotation(stream) : modelBasis;
                    value.Raw = c.Value(b.Bone.GetPosition(stream), b.Bone.GetRotation(stream), origin, basis);
                    value.Bound = Finite(value.Raw);
                    value.Value = value.Raw;
                    value.Valid = value.Bound;
                    if (c.Kind == GroundFeatureKind.Velocity)
                    {
                        var previous = Ring[before.Cursor * Bones.Length + i];
                        value.Valid = 0;
                        if (current.Count >= 2 && previous.Bound != 0 && value.Bound != 0)
                        {
                            value.Value = (value.Raw - previous.Raw) / (float)(current.Time - before.Time);
                            value.Valid = Finite(value.Value);
                        }
                    }
                }
                Ring[current.Cursor * Bones.Length + i] = value;
            }
        }

        // 过滤动画流中非有限的采样。
        private static byte Finite(Vector3 v) => (byte)(v.sqrMagnitude < float.PositiveInfinity ? 1 : 0);
    }

    // 随图拥有特征原生缓冲，主线程在规划任务启动前将查询复制到托管值数组。
    internal sealed class GroundFeatureHistory : IDisposable
    {
        private readonly GroundFeatureLayout[] layouts;
        private readonly GroundFeatureChannel[] bones;
        private readonly (int bone, float time)[] samples;
        private readonly Vector3[] observations;
        private readonly bool[] valid;
        private readonly int[][] lookup;
        private readonly Vector3[][] values;
        private readonly GroundFeatureQuery[] queries;
        private GroundFeatureSampler sampler;

        // 合并各布局中相同骨骼与空间的原始采样，并按各库自己的通道顺序保存查询映射。
        internal GroundFeatureHistory(GroundFeatureLayout[] layouts)
        {
            this.layouts = layouts ?? Array.Empty<GroundFeatureLayout>();
            int count = this.layouts.Length;
            lookup = new int[count][];
            values = new Vector3[count][];
            queries = new GroundFeatureQuery[count];
            var sources = new List<GroundFeatureChannel>();
            var poses = new List<(int, float)>();
            for (int n = 0; n < count; n++)
            {
                var layout = this.layouts[n];
                int length = layout?.Active == true ? layout.Channels.Length : 0;
                lookup[n] = new int[length];
                values[n] = new Vector3[length];
                for (int i = 0; i < length; i++)
                {
                    var c = layout.Channels[i];
                    lookup[n][i] = -1;
                    if (c.Path || (layout.Available & (1u << i)) == 0) continue;
                    int bone = sources.FindIndex(b => b.Kind == c.Kind && b.Bone == c.Bone &&
                        b.Space == c.Space && b.Reference == c.Reference);
                    if (bone < 0) { bone = sources.Count; sources.Add(c); }
                    var slot = (bone, c.Time);
                    int sample = poses.IndexOf(slot);
                    if (sample < 0) { sample = poses.Count; poses.Add(slot); }
                    lookup[n][i] = sample;
                }
            }
            bones = sources.ToArray();
            samples = poses.ToArray();
            observations = new Vector3[samples.Length];
            valid = new bool[samples.Length];
        }

        // 按与烘焙相同的骨骼和参考空间绑定环形历史。
        internal GroundFeatureSampler Bind(Animator animator, int capacity)
        {
            var bones = new GroundFeatureBone[capacity > 0 ? this.bones.Length : 0];
            for (int i = 0; i < bones.Length; i++)
            {
                var c = this.bones[i];
                Transform bone = animator.GetBoneTransform(c.Bone);
                Transform reference = c.Space == GroundFeatureSpace.Model ? null : animator.GetBoneTransform(c.Reference);
                if (!bone || c.Space != GroundFeatureSpace.Model && !reference) continue;
                bones[i] = new GroundFeatureBone { Channel = c, Bound = 1, Bone = animator.BindStreamTransform(bone),
                    Reference = reference ? animator.BindStreamTransform(reference) : default };
            }
            sampler.Bones = new NativeArray<GroundFeatureBone>(bones, Allocator.Persistent);
            sampler.Ring = new NativeArray<GroundFeatureValue>(bones.Length * capacity, Allocator.Persistent);
            sampler.Model = animator.BindSceneTransform(animator.transform);
            return sampler;
        }

        // 每个骨通道和历史时刻只读一次，再投影到每个库的原始通道编号。
        internal GroundFeatureQuery[] Read(in CharacterFootObservation latest, NativeArray<CharacterFootObservation> clock)
        {
            for (int i = 0; i < samples.Length; i++)
                valid[i] = latest.Count >= 2 && sampler.Ring.IsCreated && TryRead(samples[i], latest, clock, out observations[i]);
            for (int n = 0; n < layouts.Length; n++)
            {
                var layout = layouts[n];
                if (layout?.Active != true) continue;
                uint mask = layout.Available;
                for (int i = 0; i < lookup[n].Length; i++)
                {
                    int sample = lookup[n][i];
                    if (sample < 0) continue;
                    if (valid[sample]) values[n][i] = observations[sample];
                    else mask &= ~(1u << i);
                }
                queries[n] = new GroundFeatureQuery(layout, values[n], mask);
            }
            return queries;
        }

        // 使用共同的历史时钟插值一个原始通道，缺少绑定或尚无历史时保留无效标记。
        private bool TryRead((int bone, float time) slot, in CharacterFootObservation latest,
            NativeArray<CharacterFootObservation> clock, out Vector3 value)
        {
            value = default;
            double target = latest.Time + slot.time;
            var newer = latest;
            for (int j = 0; j < latest.Samples; j++)
            {
                var older = clock[(latest.Cursor - j + clock.Length) % clock.Length];
                if (older.Time <= target)
                {
                    var left = sampler.Ring[older.Cursor * bones.Length + slot.bone];
                    var right = sampler.Ring[newer.Cursor * bones.Length + slot.bone];
                    if (left.Valid == 0 || right.Valid == 0) return false;
                    value = Vector3.Lerp(left.Value, right.Value, newer.Time > older.Time ?
                        (float)((target - older.Time) / (newer.Time - older.Time)) : 0f);
                    return true;
                }
                newer = older;
            }
            return false;
        }

        // 播放图断开后释放原生采样，重新装配时由新图重新绑定。
        public void Dispose()
        {
            if (sampler.Ring.IsCreated) sampler.Ring.Dispose();
            if (sampler.Bones.IsCreated) sampler.Bones.Dispose();
            sampler = default;
        }
    }
}
