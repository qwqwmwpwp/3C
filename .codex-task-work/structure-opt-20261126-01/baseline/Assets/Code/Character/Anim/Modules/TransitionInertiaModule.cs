using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 切换姿态惯性模块定义可独立装配的表现参数。
    [CreateAssetMenu(menuName = "3C/动画模块/切换姿态惯性", fileName = "切换姿态惯性")]
    public sealed class TransitionInertiaModule : AnimationModule
    {
        [SerializeField, InspectorName("参与部位")] internal TransitionRegions Regions = TransitionRegions.All;
        [SerializeField, InspectorName("躯干衰减秒数"), Range(0.02f, 0.5f)] internal float BodySeconds = 0.14f;
        [SerializeField, InspectorName("手臂衰减秒数"), Range(0.02f, 0.5f)] internal float ArmSeconds = 0.16f;
        [SerializeField, InspectorName("双腿衰减秒数"), Range(0.02f, 0.5f)] internal float LegSeconds = 0.09f;
        [SerializeField, InspectorName("局部偏角上限（度）"), Range(0f, 60f)] internal float AngleLimit = 20f;
        [SerializeField, InspectorName("脊柱局部位置偏差上限"), Range(0f, 0.05f)] internal float PositionLimit = 0.012f;

        [SerializeField, InspectorName("默认惯性淡变窗（秒）"), Min(0.001f)] internal float DefaultFadeWindow = 0.14f;
        [SerializeField, InspectorName("有序过渡策略")] internal CharacterTransitionProfile[] Profiles;

        // 将该能力登记到当前角色的表现组合。
        internal override void Include(AnimationModuleAssembly assembly) => assembly.Add(this);
    }
}
