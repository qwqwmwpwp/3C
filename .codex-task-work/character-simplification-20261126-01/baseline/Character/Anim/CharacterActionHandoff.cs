using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [Serializable]
    internal sealed class CharacterActionHandoffSettings
    {
        [InspectorName("自定义接管阶段"), Tooltip("关闭时沿用招式阶段，普通单次动作沿用原播放方式。自定义覆盖区间会包含行为声明的关键出招段。")]
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

        internal CharacterActionHandoff Resolve(float duration, CharacterActionHandoff source = default, float? moveStart = null)
        {
            float full = source.Valid ? Mathf.Min(source.FullFrom, FullFrom) : FullFrom;
            float release = source.Valid ? Mathf.Max(source.ReleaseFrom, ReleaseFrom) : ReleaseFrom;
            return new CharacterActionHandoff(duration, full, release, MoveBlendSeconds, moveStart);
        }
    }

    /// <summary>本次表现的起点和采样相位，行为绑定使用最新裁决相位，独立动作可跟随片段时钟。</summary>
    internal readonly struct CharacterActionTiming
    {
        public readonly float? StartPhase, SamplePhase;
        public readonly CharacterActionHandoff Handoff;
        public readonly bool StartAtZero, Resumed;
        private readonly bool followsClip;
        public float? ClockPhase => followsClip ? null : SamplePhase; // 片段自计时只提供权重，不回写片段时钟。

        public CharacterActionTiming(float? startPhase, float samplePhase, CharacterActionHandoff handoff,
            bool startAtZero, bool resumed, bool followClip = false)
        {
            StartPhase = startPhase;
            SamplePhase = samplePhase;
            followsClip = followClip;
            Handoff = handoff;
            StartAtZero = startAtZero;
            Resumed = resumed;
        }
    }

    internal readonly struct CharacterActionHandoff
    {
        public readonly float Duration, FullFrom, ReleaseFrom;
        private readonly float moveBlendSeconds;
        public readonly float? MoveStart;
        public readonly bool UsesRouteBlend;
        public bool Valid => Duration > 0f;

        public CharacterActionHandoff(float duration, float fullFrom, float releaseFrom, float moveBlendSeconds, float? moveStart)
        {
            Duration = duration;
            FullFrom = fullFrom;
            ReleaseFrom = releaseFrom;
            this.moveBlendSeconds = moveBlendSeconds;
            MoveStart = moveStart;
            UsesRouteBlend = false;
        }

        internal CharacterActionHandoff(float duration, Vector2 routeRange, float? moveStart)
            : this(duration, routeRange.x, routeRange.y, 0f, moveStart) => UsesRouteBlend = true;

        public CharacterBehaviorPhase PhaseAt(float progress) => progress < FullFrom ? CharacterBehaviorPhase.Startup :
            progress < ReleaseFrom ? CharacterBehaviorPhase.Active : CharacterBehaviorPhase.Recovery;

        public float WeightAt(float progress, float entryWeight) => WeightAt(progress, entryWeight, out _);

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
            if (UsesRouteBlend || !MoveStart.HasValue || progress < MoveStart.Value) return weight;
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
