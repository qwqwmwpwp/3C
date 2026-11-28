using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/动画模块/起步与转向", fileName = "起步与转向")]
    public sealed class StartTurnModule : AnimationModule
    {
        [SerializeField, InspectorName("起步阶段（秒）"), Min(0f), Tooltip("从停止进入移动时保留起步阶段的时长，单位秒；0 表示禁用。")]
        private float locomotionStartSeconds = 0.22f;
        [InspectorName("反向转身阶段（秒）"), SerializeField, Min(0f), Tooltip("反向转身阶段的时长，单位秒；0 表示禁用。")]
        private float locomotionTurnSeconds = 0.32f;
        [InspectorName("反向转身夹角（度）"), SerializeField, Range(90f, 180f), Tooltip("触发反向转身所需的移动方向夹角，单位度。")]
        private float locomotionReverseAngle = 120f;
        [InspectorName("步行起步状态"), SerializeField, Tooltip("行走起步的完整状态路径；留空或不可用时使用普通行走。")]
        private string walkStartStateName;
        [InspectorName("步行起点（归一化）"), SerializeField, Range(0f, 1f), Tooltip("行走起步素材的归一化起点。")]
        private float walkStartNormalizedTime;
        [InspectorName("跑步起步状态"), SerializeField, Tooltip("跑步起步的完整状态路径；留空或不可用时使用普通跑步。")]
        private string runStartStateName;
        [InspectorName("跑步起步片段"), SerializeField, Tooltip("跑步起步状态使用的片段，用于把支撑脚相位换算为固定时间混合的入点。")]
        private AnimationClip runStartClip;
        [InspectorName("走跑混合（秒）"), SerializeField, Min(0f), Tooltip("普通起跑从走姿展开到跑姿的混合时间（秒）。")]
        private float walkToRunBlendSeconds = 0.14f;
        [InspectorName("跑步起点（归一化）"), SerializeField, Range(0f, 1f), Tooltip("跑步起步素材的归一化起点。")]
        private float runStartNormalizedTime;
        [InspectorName("左转状态"), SerializeField, Tooltip("向左反向转身的完整状态路径；留空或不可用时使用当前基础移动状态。")]
        private string turnLeftStateName;
        [InspectorName("右转状态"), SerializeField, Tooltip("向右反向转身的完整状态路径；留空或不可用时使用当前基础移动状态。")]
        private string turnRightStateName;

        internal override void Include(AnimationModuleAssembly assembly) => assembly.Add(this);

        // 将起步与转身的片段和时长写入动画配置。
        internal void Apply(CharacterAnimationConfiguration config)
        {
            config.LocomotionStartSeconds = locomotionStartSeconds;
            config.LocomotionTurnSeconds = locomotionTurnSeconds;
            config.LocomotionReverseAngle = locomotionReverseAngle;
            config.WalkStartState = walkStartStateName;
            config.WalkStartNormalizedTime = walkStartNormalizedTime;
            config.RunStartState = runStartStateName;
            config.RunStartClip = runStartClip;
            config.RunStartNormalizedTime = runStartNormalizedTime;
            config.WalkToRunBlendSeconds = walkToRunBlendSeconds;
            config.TurnLeftState = turnLeftStateName;
            config.TurnRightState = turnRightStateName;
        }
    }
}
