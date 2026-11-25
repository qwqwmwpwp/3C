using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>世界 XZ 平面移动与冲刺配置，体力初值在初始化或显式重置时读取。</summary>
    [Serializable]
    public sealed class CharacterMovementSettings : CharacterLocomotionSettings
    {
        // 复制移动配置供预测独立读取。
        internal CharacterMovementSettings Snapshot() => (CharacterMovementSettings)MemberwiseClone();
        internal const float DefaultDashSpeed = 16f;
        internal const float DefaultDashDuration = 0.2f;
        internal const float DefaultDashCooldown = 0.5f;
        internal const float DefaultInitialStamina = 100f;
        internal const float DefaultDashStaminaCost = 25f;

        [Min(0f), Tooltip("冲刺速度，单位为 Unity 单位/秒。大于 0 时允许发起冲刺，获准时锁定本次速度。")]
        public float DashSpeed = DefaultDashSpeed;
        [Min(0f), Tooltip("单次冲刺持续时间，单位为秒。大于 0 时允许发起冲刺，最后一步按剩余时间计算位移。")]
        public float DashDuration = DefaultDashDuration;
        [Min(0f), Tooltip("冲刺冷却时间，单位为秒，最小为 0，从批准冲刺时开始计时。")]
        public float DashCooldown = DefaultDashCooldown;
        [Min(0f), Tooltip("初始化或显式重置时使用的体力值，最小为 0。当前流程仅消费体力，暂停期间保留剩余值。")]
        public float InitialStamina = DefaultInitialStamina;
        [Min(0f), Tooltip("每次批准冲刺时扣除的体力，最小为 0。剩余体力达到此值才允许冲刺。")]
        public float DashStaminaCost = DefaultDashStaminaCost;
    }
}
