using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 绑定已声明骨骼的动画流位置，允许单个缺失骨骼独立失效。
    internal struct CharacterStrategyBone
    {
        internal TransformStreamHandle Handle;
        internal byte Bound;
    }

    // 记录骨骼历史最近时间槽和已经连续写入的数量。
    internal struct CharacterBoneClock
    {
        internal int Cursor, Count;
    }

    // 在所有程序性修正之前采样已声明的骨骼，与地面足部采样相互独立。
    internal struct CharacterStrategyBoneSampler
    {
        [ReadOnly] internal NativeArray<CharacterStrategyBone> Bones;
        internal NativeArray<CharacterBoneClock> Clock;
        internal NativeArray<double> Times;
        internal NativeArray<CharacterBonePose> Poses;
        internal TransformSceneHandle Model;
        internal double Time, HistoryInterval;

        // 保存最近的精确样本，并在整个策略历史窗口内保留可供插值的锚点。
        internal void Capture(AnimationStream stream)
        {
            if (Bones.Length == 0) return;
            var clock = Clock[0];
            if (clock.Count > 0 && Time < Times[clock.Cursor]) clock = default;
            bool sameTime = clock.Count > 0 && Time == Times[clock.Cursor];
            int prior = (clock.Cursor + Times.Length - 1) % Times.Length;
            // 同一截止时刻可以先结算旧动作再播放新姿态，后一次求值覆盖本槽。
            if (!sameTime && (clock.Count <= 1 || Times[clock.Cursor] - Times[prior] >= HistoryInterval))
            {
                clock.Cursor = (clock.Cursor + 1) % Times.Length;
                clock.Count = Mathf.Min(clock.Count + 1, Times.Length);
            }
            Times[clock.Cursor] = Time;
            bool modelValid = Model.IsValid(stream);
            Vector3 origin = modelValid ? Model.GetPosition(stream) : default;
            Quaternion inverse = modelValid ? Quaternion.Inverse(Model.GetRotation(stream)) : default;
            for (int i = 0; i < Bones.Length; i++)
            {
                var bone = Bones[i];
                Poses[clock.Cursor * Bones.Length + i] = modelValid && bone.Bound != 0 && bone.Handle.IsValid(stream) ?
                    new CharacterBonePose(inverse * (bone.Handle.GetPosition(stream) - origin),
                        inverse * bone.Handle.GetRotation(stream)) : default;
            }
            Clock[0] = clock;
        }
    }

    // 管理策略专用的模型空间骨姿历史，并在采集阶段冻结所有声明的时间样本。
    internal sealed class CharacterStrategyBoneHistory : IDisposable
    {
        private const int HistoryCapacity = 128;
        private readonly CharacterBoneRequest[] requests;
        private readonly CharacterBoneRequest[] sources;
        private readonly int[] sourceIndices;
        private readonly double historyInterval;
        private readonly long catalogId;
        private CharacterStrategyBoneSampler sampler;
        internal bool Requested => requests.Length > 0;

        // 合并同一骨骼的多个历史请求，根据最远时刻确定环形缓冲的采样密度。
        internal CharacterStrategyBoneHistory(CharacterBasePoseSetup setup)
        {
            requests = setup?.Seal() ?? Array.Empty<CharacterBoneRequest>();
            catalogId = setup?.CatalogId ?? 0;
            sourceIndices = new int[requests.Length];
            var unique = new List<CharacterBoneRequest>();
            float maxAge = 0f;
            for (int i = 0; i < requests.Length; i++)
            {
                var request = requests[i];
                int source = unique.FindIndex(other => other.Bone == request.Bone && other.Path == request.Path);
                if (source < 0)
                {
                    source = unique.Count;
                    unique.Add(request);
                }
                sourceIndices[i] = source;
                maxAge = Mathf.Max(maxAge, request.SecondsAgo);
            }
            sources = unique.ToArray();
            historyInterval = maxAge / (HistoryCapacity - 2);
        }

        // 在当前 Avatar 或普通骨架上绑定每个源，释放时由历史拥有全部缓冲。
        internal CharacterStrategyBoneSampler Bind(Animator animator)
        {
            int capacity = Requested ? (historyInterval > 0 ? HistoryCapacity : 2) : 0;
            sampler.Bones = new NativeArray<CharacterStrategyBone>(sources.Length, Allocator.Persistent);
            sampler.Clock = new NativeArray<CharacterBoneClock>(Requested ? 1 : 0, Allocator.Persistent);
            sampler.Times = new NativeArray<double>(capacity, Allocator.Persistent);
            sampler.Poses = new NativeArray<CharacterBonePose>(sources.Length * capacity, Allocator.Persistent);
            if (!Requested) return sampler;
            sampler.HistoryInterval = historyInterval;
            sampler.Model = animator.BindSceneTransform(animator.transform);
            for (int i = 0; i < sources.Length; i++)
            {
                var source = sources[i];
                var transform = source.Path != null ? animator.transform.Find(source.Path) :
                    animator.isHuman ? animator.GetBoneTransform(source.Bone) : null;
                if (transform) sampler.Bones[i] = new CharacterStrategyBone { Bound = 1, Handle = animator.BindStreamTransform(transform) };
            }
            return sampler;
        }

        // 将声明的历史时刻从原生环形缓冲独立拷贝到当前批次。
        internal CharacterBoneObservations Read()
        {
            if (!Requested || !sampler.Clock.IsCreated) return default;
            var clock = sampler.Clock[0];
            if (clock.Count == 0) return default;
            var poses = new CharacterBonePose[requests.Length];
            double now = sampler.Times[clock.Cursor];
            for (int i = 0; i < poses.Length; i++)
                poses[i] = ReadAt(clock, now - requests[i].SecondsAgo, sourceIndices[i]);
            return new CharacterBoneObservations(poses, catalogId, now);
        }

        // 在相邻且有效的两个历史姿态间插值，超出实际覆盖时段时保持无效。
        private CharacterBonePose ReadAt(CharacterBoneClock clock, double target, int source)
        {
            int newer = clock.Cursor;
            for (int i = 0; i < clock.Count; i++)
            {
                int older = (clock.Cursor - i + sampler.Times.Length) % sampler.Times.Length;
                double start = sampler.Times[older];
                if (start <= target)
                {
                    var a = sampler.Poses[older * sources.Length + source];
                    if (start == target || older == newer) return a;
                    var b = sampler.Poses[newer * sources.Length + source];
                    if (a.Valid == 0 || b.Valid == 0) return default;
                    float t = (float)((target - start) / (sampler.Times[newer] - start));
                    return new CharacterBonePose(Vector3.Lerp(a.Position, b.Position, t), Quaternion.Slerp(a.Rotation, b.Rotation, t));
                }
                newer = older;
            }
            return default;
        }

        // 切断模拟或姿态连续性后，旧的时间槽从查询窗口立即消失。
        internal void Clear()
        {
            if (Requested && sampler.Clock.IsCreated) sampler.Clock[0] = default;
        }

        // 与当前播放图一同释放所有原生缓冲。
        public void Dispose()
        {
            if (sampler.Poses.IsCreated) sampler.Poses.Dispose();
            if (sampler.Times.IsCreated) sampler.Times.Dispose();
            if (sampler.Clock.IsCreated) sampler.Clock.Dispose();
            if (sampler.Bones.IsCreated) sampler.Bones.Dispose();
            sampler = default;
        }
    }
}
