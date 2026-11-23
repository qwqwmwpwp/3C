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

        internal bool Valid => Duration > 0f && !float.IsInfinity(Duration) && FullFrom >= 0f &&
            ReleaseFrom > FullFrom && ReleaseFrom < 1f && MoveBlendSeconds >= 0f && !float.IsInfinity(MoveBlendSeconds);
    }

    /// <summary>同一固定步的表现时间窗。动作进度由行为或映射推进，渲染只在相邻物理步之间采样。</summary>
    internal readonly struct CharacterActionHandoff
    {
        public readonly float Duration, FullFrom, ReleaseFrom;
        private readonly float moveBlendSeconds;
        public readonly float? MoveStart;
        public bool Valid => Duration > 0f;

        public CharacterActionHandoff(float duration, float fullFrom, float releaseFrom, float moveBlendSeconds, float? moveStart)
        {
            Duration = duration;
            FullFrom = fullFrom;
            ReleaseFrom = releaseFrom;
            this.moveBlendSeconds = moveBlendSeconds;
            MoveStart = moveStart;
        }

        public CharacterBehaviorPhase PhaseAt(float progress) => progress < FullFrom ? CharacterBehaviorPhase.Startup :
            progress < ReleaseFrom ? CharacterBehaviorPhase.Active : CharacterBehaviorPhase.Recovery;

        public float WeightAt(float progress, float entryWeight)
        {
            if (progress < FullFrom)
            {
                float entering = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, FullFrom, progress));
                return Mathf.Lerp(entryWeight, 1f, entering);
            }
            float weight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ReleaseFrom, 1f, progress));
            if (!MoveStart.HasValue || progress < MoveStart.Value) return weight;
            float seconds = (progress - MoveStart.Value) * Duration;
            float movement = moveBlendSeconds > 0f ? Mathf.SmoothStep(0f, 1f, seconds / moveBlendSeconds) : 1f;
            return weight * (1f - movement);
        }
    }
}
