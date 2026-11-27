using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 配置普通单次动作的接管阶段与移动交接权重。
    [Serializable]
    internal sealed class CharacterActionHandoffSettings
    {
        [InspectorName("自定义接管阶段"), Tooltip("为普通单次动作设置覆盖区间。具名攻击直接使用自身的三段时钟。")]
        public bool OverridePhases;
        [Min(0.001f), InspectorName("无行为时钟时的时长（秒）")] public float Duration = 1f;
        [Range(0f, 1f), InspectorName("完全接管相位")] public float FullFrom = 0.24f;
        [Range(0f, 1f), InspectorName("开始交回相位")] public float ReleaseFrom = 0.58f;
        [Min(0f), InspectorName("交回期间转移动（秒）")] public float MoveBlendSeconds = 0.16f;
        [InspectorName("前摇按实速换步态"), Tooltip("关闭时在移动获准的前摇保留已有跑姿。")] public bool FollowSpeedOnEntry;
        [InspectorName("接招时从零覆盖"), Tooltip("关闭时继承同层上一动作的实际覆盖权重。")] public bool StartAtZero;

        internal CharacterActionHandoffSettings Snapshot() => (CharacterActionHandoffSettings)MemberwiseClone();

        internal bool Valid => Duration > 0f && !float.IsInfinity(Duration) && FullFrom >= 0f &&
            ReleaseFrom > FullFrom && ReleaseFrom < 1f && MoveBlendSeconds >= 0f && !float.IsInfinity(MoveBlendSeconds);

        // 根据普通动作的配置和已获得移动许可的时刻生成交接权重。
        internal CharacterActionHandoff Resolve(float duration, float? moveStart = null) =>
            new CharacterActionHandoff(duration, FullFrom, ReleaseFrom, MoveBlendSeconds, moveStart);
    }

    // 统一向图提供素材位置与覆盖权重，旧式行为映射可继续提供自己的阶段采样。
    internal readonly struct CharacterActionTiming
    {
        public readonly float? StartClipPhase, WeightPhase, ClipPhase;
        public readonly CharacterActionHandoff Handoff;
        internal readonly CharacterPlaybackSample Sample;
        public readonly bool StartAtZero, Resumed;
        internal bool HasWeight => Sample.Valid || Handoff.Valid;
        internal bool Entered => Sample.Valid ? Sample.RemainingEntry <= 0f : Handoff.Valid && WeightPhase >= Handoff.FullFrom;
        internal float RemainingEntry => Sample.Valid ? Sample.RemainingEntry :
            Handoff.Valid ? Mathf.Max(0f, Handoff.FullFrom - WeightPhase.GetValueOrDefault()) * Handoff.Duration : 0f;

        // 普通行为映射继续选择随素材或由行为给出相位。
        public CharacterActionTiming(float? startClipPhase, float weightPhase, CharacterActionHandoff handoff,
            bool startAtZero, bool resumed, bool followClip = false)
        {
            this = default;
            StartClipPhase = startClipPhase;
            WeightPhase = weightPhase;
            Handoff = handoff;
            StartAtZero = startAtZero;
            Resumed = resumed;
            ClipPhase = followClip ? null : (float?)weightPhase;
        }

        // 独立计时的播放请求以句柄保存的真实游标作为图节点恢复点。
        internal CharacterActionTiming(CharacterPlaybackSample sample, CharacterClipProgress? cursor, float initialPhase)
        {
            this = default;
            Sample = sample;
            ClipPhase = sample.Phase;
            StartClipPhase = cursor?.normalizedTime ?? initialPhase;
            Resumed = cursor.HasValue || initialPhase > 0f;
        }

        // 求当前权重，使用请求的独立过渡或旧映射的相位窗口。
        internal float WeightAt(float entry) => Sample.Valid ? Sample.Weight(entry, out _) : Handoff.WeightAt(WeightPhase.GetValueOrDefault(), entry);
    }

    // 保存一次动作的覆盖时钟，按阶段计算动画权重。
    internal readonly struct CharacterActionHandoff
    {
        public readonly float Duration, FullFrom, ReleaseFrom;
        private readonly float moveBlendSeconds;
        public readonly float? MoveStart;
        public bool Valid => Duration > 0f;

        // 冻结普通动作的接管阶段和移动获准后的淡出时间。
        public CharacterActionHandoff(float duration, float fullFrom, float releaseFrom, float moveBlendSeconds, float? moveStart)
        {
            Duration = duration;
            FullFrom = fullFrom;
            ReleaseFrom = releaseFrom;
            this.moveBlendSeconds = moveBlendSeconds;
            MoveStart = moveStart;
        }

        // 按当前进度和起始覆盖量计算本次动作的显示权重。
        public float WeightAt(float progress, float entryWeight) => WeightAt(progress, entryWeight, out _);

        // 计算覆盖权重及其相位导数，零时长的边界直接切换权重。
        public float WeightAt(float progress, float entryWeight, out float phaseSlope)
        {
            if (progress < FullFrom)
            {
                float phase = Mathf.InverseLerp(0f, FullFrom, progress);
                float entering = Mathf.SmoothStep(0f, 1f, phase);
                phaseSlope = (1f - entryWeight) * CharacterBlendCurve.SmoothSlope(phase) / FullFrom;
                return Mathf.Lerp(entryWeight, 1f, entering);
            }
            if (ReleaseFrom >= 1f)
            {
                phaseSlope = 0f;
                return progress < 1f ? 1f : 0f;
            }
            float recovery = Mathf.InverseLerp(ReleaseFrom, 1f, progress);
            float weight = 1f - Mathf.SmoothStep(0f, 1f, recovery);
            phaseSlope = -CharacterBlendCurve.SmoothSlope(recovery) / (1f - ReleaseFrom);
            if (!MoveStart.HasValue || progress < MoveStart.Value) return weight;
            if (moveBlendSeconds <= 0f)
            {
                phaseSlope = 0f;
                return 0f;
            }
            float seconds = (progress - MoveStart.Value) * Duration;
            float movePhase = Mathf.Clamp01(seconds / moveBlendSeconds);
            float movement = Mathf.SmoothStep(0f, 1f, movePhase);
            phaseSlope = phaseSlope * (1f - movement) - weight * CharacterBlendCurve.SmoothSlope(movePhase) * Duration / moveBlendSeconds;
            return weight * (1f - movement);
        }
    }
}
