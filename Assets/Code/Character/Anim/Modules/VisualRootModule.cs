using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 按当前实际在播地面用途及中断条件区分视觉根阶段。
    internal enum VisualRootPhase { Cycle, Start, Stop, Turn, Blocked, Covered, Airborne, Missing }
    // 固定于模型原点、累计参考差、受限跟随或仅衰减已有偏移。
    internal enum VisualRootMode { Fixed, Accumulate, Follow, Release }

    // 每个阶段单独选择平移和偏航规则，局部上限以模块总预算的倍率表示。
    [System.Serializable] internal sealed class VisualRootPolicy
    {
        public VisualRootPhase Phase;
        public VisualRootMode Position = VisualRootMode.Follow, Rotation = VisualRootMode.Follow;
        [Range(0f, 1f)] public float DistanceShare = 1f, YawShare = 1f;
        [Min(0.01f)] public float ReleaseSeconds = 0.16f;
    }

    // 视觉根偏移模块定义可独立装配的表现参数。
    [CreateAssetMenu(menuName = "3C/动画模块/视觉根偏移", fileName = "视觉根偏移")]
    public sealed class VisualRootModule : AnimationModule
    {
        [SerializeField, InspectorName("最大水平偏移（米）"), Range(0f, 0.3f)] internal float Distance = 0.09f;
        [SerializeField, InspectorName("最大偏航（度）"), Range(0f, 30f)] internal float Yaw = 12f;
        [SerializeField, InspectorName("跟随速度（米/秒）"), Min(0f)] internal float FollowSpeed = 1.3f;
        [SerializeField, InspectorName("跟随角速度（度/秒）"), Min(0f)] internal float TurnSpeed = 100f;
        [SerializeField, InspectorName("回正时间（秒）"), Min(0.01f)] internal float Release = 0.16f;

        [SerializeField, Tooltip("相对 Animator 的内部根骨路径；空值使用人形骨盆。")] internal string RootPath;
        [SerializeField, InspectorName("各阶段根骨策略")] internal VisualRootPolicy[] Policies;

        // 未显式覆盖的阶段沿用已发布的移动跟随或异常状态释放策略。
        internal VisualRootPolicy For(VisualRootPhase phase)
        {
            if (Policies != null) foreach (var p in Policies) if (p != null && p.Phase == phase) return p;
            return null;
        }

        // 将该能力登记到当前角色的表现组合。
        internal override void Include(AnimationModuleAssembly assembly) => assembly.Add(this);
    }
}
