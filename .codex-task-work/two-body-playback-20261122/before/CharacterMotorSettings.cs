using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [Serializable]
    public class CharacterLocomotionSettings
    {
        internal const float DefaultMoveSpeed = 5f;
        internal const float DefaultWalkSpeed = 1.55f;
        internal const float DefaultSprintSpeed = 6.52f;
        internal const float DefaultMoveTurnSpeed = 600f;

        [InspectorName("常跑目标速度（m/s）"), Min(0f), Tooltip("普通跑步的目标速度，单位为米/秒。")]
        public float MoveSpeed = DefaultMoveSpeed;
        [InspectorName("步行目标速度（m/s）"), Min(0f), Tooltip("按住步行键时的目标速度，单位为米/秒。")]
        public float WalkSpeed = DefaultWalkSpeed;
        [InspectorName("持续冲刺目标速度（m/s）"), Min(0f), Tooltip("按住持续冲刺键时的目标速度，单位为米/秒。")]
        public float SprintSpeed = DefaultSprintSpeed;
        [InspectorName("普通转向速度（°/s）"), Min(0f), Tooltip("普通移动的最大转向速度，单位为度/秒。设为 0 时禁用普通移动转向。")]
        public float MoveTurnSpeed = DefaultMoveTurnSpeed;
    }

    [Serializable]
    internal sealed class CharacterDashSettings
    {
        [InspectorName("短突进速度（m/s）"), Min(0f), Tooltip("冲刺速度，单位为 Unity 单位/秒。大于 0 时允许发起冲刺，获准时锁定本次速度。")]
        public float DashSpeed = CharacterMovementSettings.DefaultDashSpeed;
        [InspectorName("持续时间（秒）"), Min(0f), Tooltip("单次冲刺持续时间，单位为秒。大于 0 时允许发起冲刺，最后一步按剩余时间计算位移。")]
        public float DashDuration = CharacterMovementSettings.DefaultDashDuration;
        [InspectorName("冷却时间（秒）"), Min(0f), Tooltip("冲刺冷却时间，单位为秒，最小为 0，从批准冲刺时开始计时。")]
        public float DashCooldown = CharacterMovementSettings.DefaultDashCooldown;
        [InspectorName("初始体力"), Min(0f), Tooltip("初始化或显式重置时使用的体力值，最小为 0。当前流程仅消费体力，暂停期间保留剩余值。")]
        public float InitialStamina = CharacterMovementSettings.DefaultInitialStamina;
        [InspectorName("单次突进体力消耗"), Min(0f), Tooltip("每次批准冲刺时扣除的体力，最小为 0。剩余体力达到此值才允许冲刺。")]
        public float DashStaminaCost = CharacterMovementSettings.DefaultDashStaminaCost;
    }

    [Serializable]
    internal sealed class CharacterMotorSettings
    {
        internal const float DefaultFootfallDistance = 1.66f;
        internal const float MaxLeadInSeconds = 0.08f;

        [InspectorName("地面加速度（m/s²）"), SerializeField, Min(0f), Tooltip("地面每秒可修正的速度（米/秒²），同时决定受推后恢复控制的快慢。")]
        internal float groundAcceleration = 45f;
        [InspectorName("步行制动（m/s²）"), SerializeField, Min(0.01f), Tooltip("步行主动松键时的地面减速度（米/秒²）。")]
        internal float walkBraking = 15f;
        [InspectorName("常跑主制动（m/s²）"), SerializeField, Min(0.01f), Tooltip("常跑主动松键时的地面减速度（米/秒²）。")]
        internal float runBraking = 18f;
        [InspectorName("常跑轻制动最长时间（秒）"), SerializeField, Range(0f, MaxLeadInSeconds), Tooltip("稳定跑步松键后轻制动的最长时间（秒）；0 立即主制动。")]
        internal float runBrakeLeadInSeconds;
        [InspectorName("常跑轻制动减速度（m/s²）"), SerializeField, Min(0.01f), Tooltip("常跑轻制动减速度（米/秒²），不高于主制动减速度。")]
        internal float runBrakeLeadInDeceleration = 8f;
        [InspectorName("轻制动最低跑速（m/s）"), SerializeField, Min(0f), Tooltip("建立常跑落脚估算所需的实际水平前进速度（米/秒）。")]
        internal float runBrakeLeadInMinSpeed = 3.5f;
        [InspectorName("落脚间距覆盖（米）"), SerializeField, Min(0f), Tooltip("0 使用烘焙跑步循环行进距离的一半，无轨道时使用 1.66 米。正数指定左右脚交替落地的间距。")]
        internal float runBrakeLeadInFootfallDistance;
        [InspectorName("持续冲刺制动（m/s²）"), SerializeField, Min(0.01f), Tooltip("持续冲刺主动松键时的地面减速度（米/秒²）。")]
        internal float sprintBraking = 45f;
        [InspectorName("空中加速度（m/s²）"), SerializeField, Min(0f), Tooltip("滞空时有输入的水平加速度（米/秒²）。松手后保留空中惯性。")]
        internal float airAcceleration = 12f;
        [InspectorName("最大坡角（度）"), SerializeField, Range(0f, 89f), Tooltip("可行走地面相对水平面的最大坡角。")]
        internal float slopeLimit = 45f;
        [InspectorName("最大台阶高度（米）"), SerializeField, Min(0f), Tooltip("可跨越的最大台阶高度（世界米），设为 0 关闭跨阶。")]
        internal float stepHeight = 0.3f;
    }
}
