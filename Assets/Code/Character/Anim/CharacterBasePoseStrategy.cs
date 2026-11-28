using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 基础姿态策略资产只保存配置，在主线程为每个角色创建独立运行实例。
    public abstract class CharacterBasePoseStrategy : ScriptableObject
    {
        // 在装配期间解析素材状态、登记所需骨骼，并将策略数据复制给运行实例。
        public abstract ICharacterBasePoseStrategy CreateInstance(CharacterBasePoseSetup setup);
    }

    // 每角色策略按真实固定步推进；试选可重复或被丢弃，只有最终提交才消费候选。
    public interface ICharacterBasePoseStrategy
    {
        // 消费一个已结算的固定步；各次调用按固定步序号排列。
        void AdvanceStep(in CharacterBasePoseInput input);

        // 读取已推进状态的基础语义需求，实例编号保持到该需求重新启动。
        CharacterAnimationRequest GetIntent();

        // 使用冻结值试选基础姿态；允许后台调用以及预测更新后的重复试选。
        CharacterBasePoseSelection SelectPose(in CharacterBasePoseInput input);

        // 记录宿主最终采用的候选，可用候选标记确认本次提交。
        void CommitSelection(in CharacterBasePoseSelection selection);

        // 清除该角色上一段模拟或播放连续性积累的状态。
        void Reset();
    }

    // 表示装配时成功解析的逻辑片段状态，可与播放观测中的状态直接比较。
    public readonly struct CharacterPoseHandle : IEquatable<CharacterPoseHandle>
    {
        internal readonly int Hash;
        internal readonly long CatalogId;
        public bool IsValid => Hash != 0;

        // 保存注册成功的播放图状态编号。
        internal CharacterPoseHandle(int hash, long catalogId)
        {
            Hash = hash;
            CatalogId = catalogId;
        }

        // 比较同一播放目录内的状态标识。
        public bool Equals(CharacterPoseHandle other) => Hash == other.Hash && CatalogId == other.CatalogId;

        // 供通用集合与装箱值比较同一播放状态。
        public override bool Equals(object obj) => obj is CharacterPoseHandle other && Equals(other);

        // 以播放状态编号建立查找表。
        public override int GetHashCode() => Hash ^ CatalogId.GetHashCode();
    }

    // 策略候选携带播放状态、归一化入点、过渡秒数和同状态续播决策。
    public readonly struct CharacterBasePoseSelection
    {
        public readonly CharacterPoseHandle State;
        public readonly float StartNormalizedTime, TransitionSeconds;
        public readonly bool ContinuePlayback;
        public readonly long Tag; // 策略自定的候选标识，随最终提交原样返还。
        public bool IsValid => State.IsValid && StartNormalizedTime >= 0f && StartNormalizedTime <= 1f &&
            TransitionSeconds >= 0f && !float.IsInfinity(TransitionSeconds);

        // 同状态允许续播时保留原游标，其他情况从指定入点进入候选。
        public CharacterBasePoseSelection(CharacterPoseHandle state, float startNormalizedTime = 0f,
            float transitionSeconds = 0f, bool continuePlayback = true, long tag = 0)
        {
            State = state;
            StartNormalizedTime = startNormalizedTime;
            TransitionSeconds = transitionSeconds;
            ContinuePlayback = continuePlayback;
            Tag = tag;
        }
    }

    // 在主线程装配期间提供逻辑状态解析和骨骼历史声明，完成后封存。
    public sealed class CharacterBasePoseSetup
    {
        private Func<string, int> resolve;
        private static long nextId;
        private readonly long catalogId = Interlocked.Increment(ref nextId);
        internal long CatalogId => catalogId;
        private readonly List<CharacterBoneRequest> bones = new List<CharacterBoneRequest>();
        private readonly List<float> trajectoryTimes = new List<float>();
        private CharacterBoneRequest[] frozenBones;
        private float[] frozenTimes;
        internal bool HasTrajectoryRequests => frozenTimes.Length > 0;

        // 绑定当前角色的逻辑播放目录。
        internal CharacterBasePoseSetup(Func<string, int> resolve) =>
            this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));

        // 将已装配的逻辑状态解析为可跨线程保存的值句柄。
        public bool TryResolve(string logicalState, out CharacterPoseHandle handle)
        {
            EnsureOpen();
            int hash = string.IsNullOrWhiteSpace(logicalState) ? 0 : resolve(logicalState);
            handle = BoundHandle(hash);
            return handle.IsValid;
        }

        // 为内置策略的已绑定候选取得同一装配目录中的状态句柄。
        internal CharacterPoseHandle BoundHandle(int hash) => hash == 0 ? default : new CharacterPoseHandle(hash, catalogId);

        // 检查候选状态是否来自本角色的当前装配。
        internal bool Owns(CharacterPoseHandle handle) => handle.IsValid && handle.CatalogId == catalogId;

        // 声明人形骨及相对最近姿态的历史秒数，零表示最新姿态。
        public CharacterBoneQuery RequireBone(HumanBodyBones bone, float secondsAgo = 0f)
        {
            if ((int)bone < 0 || bone >= HumanBodyBones.LastBone) throw new ArgumentOutOfRangeException(nameof(bone));
            return Register(new CharacterBoneRequest(bone, null, secondsAgo));
        }

        // 声明从 Animator 根节点开始的相对骨骼路径，可用于普通骨架。
        public CharacterBoneQuery RequireBone(string relativePath, float secondsAgo = 0f)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("骨骼路径不能为空。", nameof(relativePath));
            return Register(new CharacterBoneRequest(default, relativePath, secondsAgo));
        }

        // 声明从本次输入时钟开始的前瞻秒数，同一时刻在一次采集中只取一次。
        public CharacterTrajectoryQuery RequireTrajectorySample(float secondsAhead)
        {
            EnsureOpen();
            if (!(secondsAhead >= 0f) || float.IsInfinity(secondsAhead))
                throw new ArgumentOutOfRangeException(nameof(secondsAhead));
            int index = trajectoryTimes.IndexOf(secondsAhead);
            if (index >= 0) return new CharacterTrajectoryQuery(index, catalogId);
            trajectoryTimes.Add(secondsAhead);
            return new CharacterTrajectoryQuery(trajectoryTimes.Count - 1, catalogId);
        }

        // 按固定步或渲染时刻采集在同一装配中声明的轨迹点。
        internal CharacterTrajectoryObservations CaptureTrajectory(IAnimationTrajectorySnapshot snapshot, double time) =>
            new CharacterTrajectoryObservations(snapshot, time, catalogId, frozenTimes);

        // 合并同一骨骼和同一时刻的请求并返回稳定的读取索引。
        private CharacterBoneQuery Register(CharacterBoneRequest request)
        {
            EnsureOpen();
            if (!(request.SecondsAgo >= 0f) || float.IsInfinity(request.SecondsAgo))
                throw new ArgumentOutOfRangeException(nameof(request.SecondsAgo));
            int index = bones.IndexOf(request);
            if (index >= 0) return new CharacterBoneQuery(index, catalogId);
            bones.Add(request);
            return new CharacterBoneQuery(bones.Count - 1, catalogId);
        }

        // 将声明复制给采样管线，释放访问主线程目录的委托。
        internal CharacterBoneRequest[] Seal()
        {
            frozenTimes ??= trajectoryTimes.ToArray();
            frozenBones ??= bones.ToArray();
            resolve = null;
            return frozenBones;
        }

        // 阻止运行实例在装配结束后重新访问素材或修改采样布局。
        private void EnsureOpen()
        {
            if (resolve == null) throw new InvalidOperationException("策略装配已经结束。");
        }
    }
}
