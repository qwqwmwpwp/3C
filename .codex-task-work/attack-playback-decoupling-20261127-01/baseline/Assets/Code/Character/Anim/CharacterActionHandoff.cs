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

    // 分开保存当前权重相位与片段采样相位，攻击独立播放还保留上次实际提交的秒数。
    internal readonly struct CharacterActionTiming
    {
        public readonly float? StartClipPhase, WeightPhase, ClipPhase;
        public readonly CharacterActionHandoff Handoff;
        public readonly bool StartAtZero, Resumed;
        internal readonly double AttackElapsed;

        // 普通动作可随素材自计时；攻击按已经过的角色秒数转换素材相位。
        public CharacterActionTiming(float? startClipPhase, float weightPhase, CharacterActionHandoff handoff,
            bool startAtZero, bool resumed, bool followClip = false, double attackElapsed = 0d)
        {
            StartClipPhase = startClipPhase;
            WeightPhase = weightPhase;
            Handoff = handoff;
            StartAtZero = startAtZero;
            Resumed = resumed;
            AttackElapsed = attackElapsed;
            ClipPhase = handoff.IsAttack ? handoff.ClipProgress(attackElapsed) : followClip ? (float?)null : weightPhase;
        }
    }

    // 保存一次动作的覆盖时钟，按阶段计算动画权重。
    internal readonly struct CharacterActionHandoff
    {
        public readonly float Duration, FullFrom, ReleaseFrom;
        private readonly float moveBlendSeconds;
        public readonly float? MoveStart;
        public readonly float AnimationDuration;
        public bool IsAttack => AnimationDuration > 0f;
        public bool Valid => Duration > 0f;

        // 冻结普通动作的接管阶段和移动获准后的淡出时间。
        public CharacterActionHandoff(float duration, float fullFrom, float releaseFrom, float moveBlendSeconds, float? moveStart)
        {
            Duration = duration;
            FullFrom = fullFrom;
            ReleaseFrom = releaseFrom;
            this.moveBlendSeconds = moveBlendSeconds;
            MoveStart = moveStart;
            AnimationDuration = 0f;
        }

        // 冻结状态覆盖窗口与片段独立播放时长，两者从同一进入时刻开始。
        internal static CharacterActionHandoff ForAttack(float duration, float animationDuration, float fullFrom, float releaseFrom) =>
            new CharacterActionHandoff(duration, animationDuration, fullFrom, releaseFrom);

        // 攻击权重按状态边界过渡，素材进度按动画持续时间计算。
        private CharacterActionHandoff(float duration, float animationDuration, float fullFrom, float releaseFrom)
            : this(duration, fullFrom, releaseFrom, 0f, null) => AnimationDuration = animationDuration;

        // 把已过秒数换算为素材相位，只有确已走完动画时长才指定素材终点。
        internal float ClipProgress(double elapsed) => ClipProgress(elapsed, AnimationDuration);

        // 实走与预测以同一算法计算素材采样位置，缺失动画时长时没有可采样的进度。
        internal static float ClipProgress(double elapsed, float animationDuration)
        {
            if (!(animationDuration > 0f)) return 0f;
            return elapsed >= animationDuration ? 1f :
                Mathf.Min(0.99999994f, Mathf.Max(0f, (float)(elapsed / animationDuration)));
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
