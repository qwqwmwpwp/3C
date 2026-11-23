using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    public enum CharacterMatchMode
    {
        [InspectorName("原规则")] Rules,
        [InspectorName("旁路观察")] Observe,
        [InspectorName("应用匹配")] Apply,
        [InspectorName("旧版跑停匹配（对照）")] LegacyBaseline
    }

    [Serializable]
    internal struct CharacterFootPose
    {
        public Vector3 Left, Right;
        public Vector3 LeftVelocity, RightVelocity;
        public Vector2 Contact;

        public void SetVelocity(in CharacterFootPose before, in CharacterFootPose after, float seconds)
        {
            LeftVelocity = (after.Left - before.Left) / seconds;
            RightVelocity = (after.Right - before.Right) / seconds;
        }
    }

    internal enum CharacterPlaybackBakeMode
    {
        [InspectorName("自动（沿用控制器参数判断）")] Auto,
        [InspectorName("直接片段播放")] DirectClips,
        [InspectorName("Animator 控制器")] Controller
    }

    internal enum GroundMotionUse { Legacy, Cycle, Start, Turn, Stop }
    internal enum GroundTrackGait { Any, Walk, Run, Sprint }
    internal enum GroundTrackSide { Either, Left, Right }
    internal enum GroundPathSource { Straight, Authored, RootMotion }

    [Serializable]
    internal struct GroundPathKey
    {
        public float Seconds;
        public Vector2 Position; // 以片段起点为原点，右、前（米）。
        public float Yaw; // 连续展开的偏航角（度）。
    }

    [Serializable]
    internal sealed class CharacterMotionTrack
    {
        public AnimationClip Clip;
        [Tooltip("第一个为候选播放状态，其余为共享观测的同片段别名。填写包含层名的完整路径。")]
        public string[] States; // 同片段的起步和循环别名共用一份采样。
        [Tooltip("同一用途的多个片段会一起比较入点。")]
        public GroundMotionUse Use;
        public GroundTrackGait Gait;
        public GroundTrackSide Side; // 转向侧或收步承重脚。
        public bool Loop;
        [Tooltip("循环片段始终以素材原速播放；默认按实际速度调整。")] public bool FixedCycleSpeed;
        [Min(0f)] public float MinimumSpeed, MaximumSpeed; // 上限 0 表示不限。
        [Min(0f)] public float FirstEntrySeconds, ExitSeconds;
        public GroundPathSource PathSource;
        [Tooltip("以片段起点为原点，时间为秒、水平位置为米、连续偏航为度。")]
        public GroundPathKey[] ReferencePath;
        [HideInInspector] public GroundPathKey[] RootPath;
        [HideInInspector] public bool ScalesWithSpeed;
        [Range(0f, 1f)] public float Blend = 0.08f;
        [Min(0f)] public float ReferenceSpeed;
        [Tooltip("原地素材的参考制动时长；循环填 0。")] public float BrakeSeconds;
        [Tooltip("可进入区间的最后一秒；循环可用整个周期。")] public float LastEntrySeconds;
        [HideInInspector] public float Seconds;
        [HideInInspector] public CharacterFootPose[] Poses;
        public bool IsStop => Use == GroundMotionUse.Stop || Use == GroundMotionUse.Legacy && BrakeSeconds > 0f;
        public bool IsLoop => Use == GroundMotionUse.Legacy ? !IsStop : Loop;
        public float ExitTime => ExitSeconds > 0f ? ExitSeconds : Seconds;
        public bool Accepts(string gait) => Gait == GroundTrackGait.Any ||
            Gait == GroundTrackGait.Walk && gait == CharacterAnimationIntentConverter.WalkKey ||
            Gait == GroundTrackGait.Run && gait == CharacterAnimationIntentConverter.MoveKey ||
            Gait == GroundTrackGait.Sprint && gait == CharacterAnimationIntentConverter.SprintKey;
        public bool IsReady => Clip && Poses != null && Poses.Length >= 2 && Seconds > 0f &&
            Mathf.Abs(Seconds - Clip.length) < 0.003f;

        public CharacterMotionTrack CopyForBake()
        {
            var copy = (CharacterMotionTrack)MemberwiseClone();
            copy.States = (string[])States?.Clone();
            copy.ReferencePath = (GroundPathKey[])ReferencePath?.Clone();
            return copy;
        }

        public bool ReadyForSearch => Use != GroundMotionUse.Legacy && IsReady && FirstEntrySeconds >= 0f &&
            LastEntrySeconds >= FirstEntrySeconds && LastEntrySeconds <= Seconds + 0.001f && ReferenceSpeed > 0f &&
            (PathSource == GroundPathSource.Straight || (PathSource == GroundPathSource.Authored ? ReferencePath : RootPath)?.Length >= 2);

        public int Index(float phase)
        {
            float t = IsLoop ? Mathf.Repeat(phase, 1f) : Mathf.Clamp01(phase);
            return Mathf.RoundToInt(t * (Poses.Length - 1));
        }

        public float TimeAt(int index) => index * Seconds / (Poses.Length - 1);

        // 这些素材为原地动画。参考根沿角色前方行进，制动段按匀减速到零。
        public float Distance(float seconds, float horizon)
        {
            if (!IsStop) return ReferenceSpeed * horizon;
            float moving = Mathf.Clamp(BrakeSeconds - seconds, 0f, horizon);
            float startSpeed = ReferenceSpeed * Mathf.Clamp01(1f - seconds / BrakeSeconds);
            return startSpeed * moving - 0.5f * ReferenceSpeed / BrakeSeconds * moving * moving;
        }
    }

    [CreateAssetMenu(menuName = "3C/地面动作数据库")]
    public sealed class CharacterMotionDatabase : ScriptableObject
    {
        [SerializeField, InspectorName("烘焙播放方式"), Tooltip("重新烘焙时生效。直接播放使用片段、固定状态速度和代码过渡。")]
        internal CharacterPlaybackBakeMode PlaybackMode;
        [SerializeField, HideInInspector] internal CharacterClipLibrary Playback;
        [SerializeField, HideInInspector] internal int Format; // 0 沿用旧库，显式烘焙升级。
        [SerializeField, Min(0), InspectorName("地面动作所在层")] internal int GroundLayer;
        [SerializeField] internal Avatar Avatar;
        [SerializeField] internal RuntimeAnimatorController Controller;
        [SerializeField] internal CharacterMotionTrack[] Tracks;
        [SerializeField, HideInInspector] internal CharacterMotionTrack[] BakedTracks;
        [SerializeField, HideInInspector] internal Avatar BakedAvatar;
        [SerializeField, HideInInspector] internal RuntimeAnimatorController BakedController;

        internal bool TryGetStopMarker(RuntimeAnimatorController controller, string state, AnimationClip playingClip, out float seconds)
        {
            seconds = 0f;
            if (!controller || controller != BakedController || BakedTracks == null) return false;
            foreach (var track in BakedTracks)
            {
                if (track == null || !track.IsStop || !track.IsReady || track.States == null ||
                    playingClip && track.Clip != playingClip || Array.IndexOf(track.States, state) < 0) continue;
                if (!(track.BrakeSeconds > 0f && track.BrakeSeconds <= track.Seconds)) continue;
                seconds = track.BrakeSeconds;
                return true;
            }
            return false;
        }

        internal CharacterMotionTrack FindBakedCycle(string stateName)
        {
            if (string.IsNullOrEmpty(stateName) || BakedTracks == null) return null;
            foreach (var track in BakedTracks)
            {
                if (track == null || !track.Clip || !track.IsLoop || track.States == null ||
                    !(track.ReferenceSpeed > 0f) || float.IsInfinity(track.ReferenceSpeed) || Array.IndexOf(track.States, stateName) < 0) continue;
                if (Playback?.States == null) return BakedController ? track : null;
                foreach (var state in Playback.States)
                    if (state != null && state.Path == stateName && state.Clip == track.Clip) return track;
            }
            return null;
        }
    }
}
