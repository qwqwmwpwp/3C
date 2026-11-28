using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 配置普通单次动作的接管阶段与移动交接权重。
    [Serializable]
    public sealed class CharacterActionHandoffSettings
    {
        [InspectorName("自定义接管阶段"), Tooltip("配置单次动作覆盖与释放区间。")]
        public bool OverridePhases;
        [Min(0.001f), InspectorName("无行为时钟时的时长（秒）")] public float Duration = 1f;
        [Range(0f, 1f), InspectorName("完全接管相位")] public float FullFrom = 0.24f;
        [Range(0f, 1f), InspectorName("开始交回相位")] public float ReleaseFrom = 0.58f;
        [Min(0f), InspectorName("交回期间转移动（秒）")] public float MoveBlendSeconds = 0.16f;
        [InspectorName("前摇按实速换步态"), Tooltip("关闭时在移动获准的前摇保留已有跑姿。")] public bool FollowSpeedOnEntry;
        [InspectorName("接招时从零覆盖"), Tooltip("关闭时继承同层上一动作的实际覆盖权重。")] public bool StartAtZero;

        // 冻结本次请求使用的配置。
        public CharacterActionHandoffSettings Snapshot() => (CharacterActionHandoffSettings)MemberwiseClone();

        public bool Valid => Duration > 0f && !float.IsInfinity(Duration) && FullFrom >= 0f &&
            ReleaseFrom > FullFrom && ReleaseFrom < 1f && MoveBlendSeconds >= 0f && !float.IsInfinity(MoveBlendSeconds);
    }

    // 将一次不可变的时钟样本和上一真实游标交给播放图，图重建时从已显示位置恢复。
    internal readonly struct CharacterActionTiming
    {
        internal readonly CharacterPlaybackSample Sample;
        public readonly float? StartClipPhase;
        public readonly bool Resumed;
        public float? ClipPhase => Sample.Valid ? Sample.Phase : (float?)null;
        public bool StartAtZero => Sample.Clock.StartAtZero;
        internal bool HasWeight => Sample.Valid;
        internal bool Entered => Sample.Valid && Sample.RemainingEntry <= 0f;
        internal float RemainingEntry => Sample.RemainingEntry;

        // 初次播放使用指定入点，已求值实例恢复上一次素材游标。
        internal CharacterActionTiming(CharacterPlaybackSample sample, CharacterClipProgress? cursor)
        {
            Sample = sample;
            StartClipPhase = cursor?.normalizedTime ?? sample.Clock.StartPhase;
            Resumed = cursor.HasValue || sample.Clock.StartPhase > 0f;
        }

        // 以首次真实覆盖量计算当前时刻的动作权重。
        internal float WeightAt(float entry) => Sample.Weight(entry, out _);
    }
}
