using System;
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
        private readonly GroundFeatureLayout layout;
        private readonly Vector3[] query;
        private GroundFeatureSampler sampler;

        // 为一个角色预留可重复使用的查询值，后台规划结束后才允许下一帧写入。
        internal GroundFeatureHistory(GroundFeatureLayout layout)
        {
            this.layout = layout;
            query = new Vector3[layout?.Active == true ? layout.Channels.Length : 0];
        }

        // 按与烘焙相同的骨骼和参考空间绑定环形历史。
        internal GroundFeatureSampler Bind(Animator animator, int capacity)
        {
            var bones = new GroundFeatureBone[capacity > 0 ? query.Length : 0];
            for (int i = 0; i < bones.Length; i++)
            {
                var c = layout.Channels[i];
                if (c.Path || (layout.Available & (1u << i)) == 0) continue;
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

        // 在同一历史环内按配置时刻插值，每个通道对本次所有候选共享一份有效位。
        internal GroundFeatureQuery Read(in CharacterFootObservation latest, NativeArray<CharacterFootObservation> clock)
        {
            if (query.Length == 0 || latest.Count < 2 || !sampler.Ring.IsCreated) return default;
            uint mask = layout.Available;
            for (int i = 0; i < query.Length; i++)
            {
                uint bit = 1u << i;
                var c = layout.Channels[i];
                if ((mask & bit) == 0 || c.Path) continue;
                double target = latest.Time + c.Time;
                var newer = latest;
                bool found = false;
                for (int j = 0; j < latest.Samples; j++)
                {
                    var older = clock[(latest.Cursor - j + clock.Length) % clock.Length];
                    if (older.Time <= target)
                    {
                        var left = sampler.Ring[older.Cursor * query.Length + i];
                        var right = sampler.Ring[newer.Cursor * query.Length + i];
                        found = left.Valid != 0 && right.Valid != 0;
                        if (found) query[i] = Vector3.Lerp(left.Value, right.Value, newer.Time > older.Time ?
                            (float)((target - older.Time) / (newer.Time - older.Time)) : 0f);
                        break;
                    }
                    newer = older;
                }
                if (!found) mask &= ~bit;
            }
            return new GroundFeatureQuery(layout, query, mask);
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
