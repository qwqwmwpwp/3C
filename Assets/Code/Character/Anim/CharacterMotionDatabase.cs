using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 控制地面匹配是否参与播放，保留原有序列化编号。
    public enum CharacterMatchMode
    {
        [InspectorName("原规则")] Rules = 0,
        [InspectorName("应用匹配")] Apply = 2
    }

    // 保存模型基准中的双脚和可选骨盆样本，旧库中的骨盆标记为零。
    [Serializable]
    internal struct CharacterFootPose
    {
        public Vector3 Left, Right;
        public Vector3 LeftVelocity, RightVelocity;
        public Vector2 Contact;
        public Vector3 Pelvis; // 相对 Animator 根，世界米按根朝向反旋。
        public byte HasPelvis;

        // 以相同坐标与时间比例插值双脚和可用的骨盆特征。
        internal static CharacterFootPose Interpolate(in CharacterFootPose a, in CharacterFootPose b, float t) =>
            new CharacterFootPose {
                Left = Vector3.Lerp(a.Left, b.Left, t), Right = Vector3.Lerp(a.Right, b.Right, t),
                LeftVelocity = Vector3.Lerp(a.LeftVelocity, b.LeftVelocity, t),
                RightVelocity = Vector3.Lerp(a.RightVelocity, b.RightVelocity, t),
                Contact = Vector2.Lerp(a.Contact, b.Contact, t), Pelvis = Vector3.Lerp(a.Pelvis, b.Pelvis, t),
                HasPelvis = (byte)(a.HasPelvis & b.HasPelvis)
            };

        public void SetVelocity(in CharacterFootPose before, in CharacterFootPose after, float seconds)
        {
            LeftVelocity = (after.Left - before.Left) / seconds;
            RightVelocity = (after.Right - before.Right) / seconds;
        }
    }

    // 描述烘焙轨道的地面动作类型，序列化编号与基础动画组一致。
    internal enum GroundMotionUse { Any = 0, Cycle = 1, Start = 2, Turn = 3, Stop = 4, Transition = 5 }
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

    // 保存用于匹配脚姿和轨迹的烘焙动作采样。
    [Serializable]
    internal sealed class CharacterMotionTrack
    {
        public AnimationClip Clip;
        [Range(0f, 1f)] public float FirstPhase, SecondPhase;
        [HideInInspector] public bool Mixed;
        [Tooltip("第一个为候选播放状态，其余为共享观测的同片段别名。填写包含层名的完整路径。")]
        public string[] States; // 同片段的起步和循环别名共用一份采样。
        [Tooltip("同一用途的多个片段会一起比较入点。")]
        public GroundMotionUse Use;
        public GroundTrackGait Gait;
        [Tooltip("换步素材的来源步态；Gait 表示目标步态。")] public GroundTrackGait FromGait;
        [Range(0f, 180f), Tooltip("参考轨迹的转角幅度，单位度。")] public float TurnDegrees;
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
        [HideInInspector] public string SampleSourceHash; // 编辑器增量采样用的素材与 Avatar 版本。
        [Min(0f), InspectorName("混合动作时长（秒）"), Tooltip("混合轨道使用此时长。直接播放的轨道在烘焙时读取基础片段长度。")]
        public float Seconds;
        [HideInInspector] public CharacterFootPose[] Poses;
        [HideInInspector] public string FeatureFingerprint;
        [HideInInspector] public uint FeatureMask;
        [HideInInspector] public Vector3[] FeatureValues; // 样帧优先、通道次序与布局相同。
        public bool IsStop => Use == GroundMotionUse.Stop;
        public bool IsLoop => Loop;
        public float ExitTime => ExitSeconds > 0f ? ExitSeconds : Seconds;
        public bool IsReady => Clip && ValidSamples && (Mixed || Mathf.Abs(Seconds - Clip.length) < 0.003f);
        private bool ValidSamples => Poses != null && Poses.Length >= 2 && Seconds > 0f;

        public CharacterMotionTrack CopyForBake()
        {
            var copy = (CharacterMotionTrack)MemberwiseClone();
            copy.States = (string[])States?.Clone();
            copy.ReferencePath = (GroundPathKey[])ReferencePath?.Clone();
            return copy;
        }

        public bool ReadyForSearch => IsReady && SearchSamplesValid;
        internal bool SearchSamplesValid => Use >= GroundMotionUse.Cycle && Use <= GroundMotionUse.Transition && ValidSamples && FirstEntrySeconds >= 0f &&
            LastEntrySeconds >= FirstEntrySeconds && LastEntrySeconds <= Seconds + 0.001f && ReferenceSpeed > 0f &&
            (PathSource == GroundPathSource.Straight || (PathSource == GroundPathSource.Authored ? ReferencePath : RootPath)?.Length >= 2);

        // 按素材秒数插值姿态，只有循环轨道允许穿过首尾边界。
        internal bool TrySamplePose(float time, out CharacterFootPose pose)
        {
            pose = default;
            if (!IsLoop && (time < 0f || time > Seconds)) return false;
            float phase = IsLoop ? Mathf.Repeat(time / Seconds, 1f) : time / Seconds;
            float cursor = phase * (Poses.Length - 1);
            int i = Mathf.Clamp(Mathf.FloorToInt(cursor), 0, Poses.Length - 1);
            pose = CharacterFootPose.Interpolate(Poses[i], Poses[Mathf.Min(i + 1, Poses.Length - 1)], cursor - i);
            return true;
        }

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

    // 保存基础动画组及其烘焙后的分层片段和地面匹配采样。
    [CreateAssetMenu(menuName = "3C/地面动作数据库")]
    public sealed class CharacterMotionDatabase : ScriptableObject
    {
        internal const int CurrentFormat = 1;
        [SerializeField, InspectorName("基础动画数据组")] internal CharacterMotionGroups Groups;
        [SerializeField] internal Avatar Avatar;
        [SerializeField, HideInInspector] internal CharacterClipLibrary Playback;
        [SerializeField, HideInInspector] internal CharacterMotionTrack[] BakedTracks;
        [SerializeField, HideInInspector] internal Avatar BakedAvatar;
        [SerializeField, HideInInspector] internal int Format;

        // 检查库是否由当前基础动画组和 Avatar 烘焙，并返回缺失或失效的资源。
        internal string ValidatePlayback()
        {
            if (!Groups) return "动画播放库缺少基础动画数据组。";
            if (Format != CurrentFormat || Playback == null || BakedTracks == null)
                return "动画播放库尚未使用当前格式烘焙，请重新烘焙基础动画数据组。";
            if (!Avatar || Avatar != BakedAvatar)
                return "动画播放库缺少有效的烘焙 Avatar，或 Avatar 已修改，请重新烘焙。";
            if (!Playback.HasBodyMasks())
                return "动画播放库需要互补的下身和上身 AvatarMask，请检查基础动画数据组并重新烘焙。";
            if (!Playback.Ready)
                return "动画播放库缺少已烘焙的片段状态或素材曲线，请重新烘焙基础动画数据组。";
            return null;
        }

        // 读取与当前片段对应的收步采样和制动时间。
        internal CharacterMotionTrack FindStopTrack(string state, AnimationClip playingClip)
        {
            if (BakedTracks == null) return null;
            foreach (var track in BakedTracks)
            {
                if (track == null || !track.IsStop || !track.IsReady || track.States == null ||
                    playingClip && track.Clip != playingClip || Array.IndexOf(track.States, state) < 0) continue;
                if (!(track.BrakeSeconds > 0f && track.BrakeSeconds <= track.Seconds)) continue;
                return track;
            }
            return null;
        }

        // 查找与烘焙片段一致的循环轨道以读取参考步频。
        internal CharacterMotionTrack FindBakedCycle(string stateName)
        {
            if (string.IsNullOrEmpty(stateName) || Format != CurrentFormat || Playback?.States == null || BakedTracks == null) return null;
            foreach (var track in BakedTracks)
            {
                if (track == null || !track.Clip || !track.IsLoop || track.States == null ||
                    !(track.ReferenceSpeed > 0f) || float.IsInfinity(track.ReferenceSpeed) || Array.IndexOf(track.States, stateName) < 0) continue;
                foreach (var state in Playback.States)
                    if (state != null && state.Path == stateName && state.MatchesTrack(track, state.Clip)) return track;
            }
            return null;
        }
    }
}
