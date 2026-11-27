using System;
using System.Text;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 定义骨骼取样的单位和两类世界水平预测通道。
    internal enum GroundFeatureKind { Position, Velocity, Heading, TrajectoryPosition, TrajectoryYaw }
    // 选择模型根、参考骨局部或仅以参考骨为原点的模型坐标。
    internal enum GroundFeatureSpace { Model, Bone, BoneOriginModelAxes }
    // 允许候选规则选择同一烘焙布局中的特征子集。
    [Flags] internal enum GroundFeatureGroups { Feet = 1, Body = 2, Hands = 4, Trajectory = 8, All = 15 }

    // 描述一个有权重的采样通道；骨骼时间为零或过去，轨迹时间为未来。
    [Serializable] internal struct GroundFeatureChannel
    {
        public GroundFeatureKind Kind;
        public HumanBodyBones Bone, Reference;
        public GroundFeatureSpace Space;
        public GroundFeatureGroups Group;
        [Tooltip("动作时钟秒。骨骼使用当前或负数历史，未来轨迹支持两个不同的正时间窗。")] public float Time;
        [Min(0f)] public float Weight;
        [Min(0.0001f)] public float Scale;
        internal bool Path => Kind == GroundFeatureKind.TrajectoryPosition || Kind == GroundFeatureKind.TrajectoryYaw;

        // 烘焙与运行时共用同一空间变换；速度源取位置，再在同一空间中差分。
        internal Vector3 Value(Vector3 position, Quaternion rotation, Vector3 origin, Quaternion basis) =>
            Quaternion.Inverse(basis) * (Kind == GroundFeatureKind.Heading ? rotation * Vector3.forward : position - origin);
    }

    // 定义当前动作库烘焙与角色采样共享的有序特征布局。
    [CreateAssetMenu(menuName = "3C/地面匹配特征布局")]
    public sealed class GroundFeatureSchema : ScriptableObject
    {
#if UNITY_EDITOR
        // 标记已装配的角色搜索范围，使编辑器修改在下一批次重新生效。
        private void OnValidate() => GroundSearchLibraries.InvalidateBindings();
#endif
        [SerializeField, InspectorName("匹配通道")] internal GroundFeatureChannel[] Channels;
    }

    // 将布局编译为纯值定义，检查指纹和所有可搜索轨道，共享整次查询的有效维度。
    internal sealed class GroundFeatureLayout
    {
        internal const int Version = 1, MaximumChannels = 32;
        internal readonly GroundFeatureChannel[] Channels;
        internal readonly string Fingerprint;
        internal readonly Vector2 Windows;
        internal readonly uint Available;
        internal readonly bool LegacyPelvis;
        internal readonly string Status;
        internal bool Active => Available != 0;

        // 轨道为空时仅编译烘焙配方，运行时要求每条有效轨道使用兼容布局。
        internal GroundFeatureLayout(GroundFeatureSchema schema, CharacterMotionTrack[] tracks = null)
        {
            LegacyPelvis = tracks != null && Array.Exists(tracks, t => t != null && t.ReadyForSearch) &&
                Array.TrueForAll(tracks, t => t == null || !t.ReadyForSearch || Array.TrueForAll(t.Poses, p => p.HasPelvis != 0));
            Channels = schema && schema.Channels != null ? (GroundFeatureChannel[])schema.Channels.Clone() : Array.Empty<GroundFeatureChannel>();
            Windows = CharacterMatchTrajectory.DefaultWindows;
            if (Channels.Length == 0) { Status = "使用兼容脚姿布局"; return; }
            string issue = Validate(out var windows);
            if (issue != null) { Status = issue; return; }
            Windows = windows;
            var signature = new StringBuilder("ground-features/").Append(Version);
            for (int i = 0; i < Channels.Length; i++)
            {
                var c = Channels[i];
                signature.Append(FormattableString.Invariant($"/{(int)c.Kind},{(int)c.Bone},{(int)c.Reference},{(int)c.Space},{c.Time:R}"));
            }
            Fingerprint = Hash128.Compute(signature.ToString()).ToString();
            uint mask = Channels.Length == 32 ? uint.MaxValue : (1u << Channels.Length) - 1u;
            if (tracks != null)
            {
                foreach (var track in tracks)
                {
                    if (track == null || !track.ReadyForSearch) continue;
                    if (track.FeatureFingerprint != Fingerprint || track.FeatureValues?.Length != (long)Channels.Length * track.Poses.Length)
                    { Status = "布局与已烘焙采样不同，当前使用兼容脚姿；请重新烘焙动作库"; return; }
                    mask &= track.FeatureMask;
                }
            }
            Available = mask;
            Status = mask == 0 ? "没有全库共用的有效通道，使用兼容脚姿" : $"特征布局 v{Version} · {Channels.Length} 通道 · 共用掩码 {mask:X}";
        }

        // 禁止无效骨骼、非有限尺寸及超出当前预测接口容量的时间窗。
        private string Validate(out Vector2 windows)
        {
            windows = CharacterMatchTrajectory.DefaultWindows;
            if (Channels.Length > MaximumChannels) return "特征通道超过 32 项，使用兼容脚姿";
            float near = 0f, far = 0f;
            foreach (var c in Channels)
            {
                if (c.Group == 0 || (c.Group & ~GroundFeatureGroups.All) != 0 || !Enum.IsDefined(typeof(GroundFeatureKind), c.Kind) || !Enum.IsDefined(typeof(GroundFeatureSpace), c.Space) ||
                    !(c.Weight >= 0f && c.Weight < float.PositiveInfinity && c.Scale >= 0.0001f && c.Scale < float.PositiveInfinity))
                    return "特征类型、权重或尺度无效";
                if (!c.Path)
                {
                    if (!(c.Time >= -0.3f && c.Time <= 0f) || !BoneValid(c.Bone) ||
                        c.Space != GroundFeatureSpace.Model && !BoneValid(c.Reference)) return "骨骼或历史采样时刻无效";
                    continue;
                }
                if (!(c.Time >= 0.06f && c.Time <= 1f)) return "轨迹窗必须位于 0.06 至 1 秒";
                if (near == 0f || c.Time == near) near = c.Time;
                else if (far == 0f || c.Time == far) far = c.Time;
                else return "当前预测接口支持两个不同的轨迹时刻";
            }
            if (near > 0f) windows = far > 0f ? new Vector2(Mathf.Min(near, far), Mathf.Max(near, far)) : new Vector2(near, near);
            return null;
        }

        // 限定为 Animator 可以查询的人形骨骼编号。
        private static bool BoneValid(HumanBodyBones bone) => (int)bone >= 0 && bone < HumanBodyBones.LastBone;
    }
}
