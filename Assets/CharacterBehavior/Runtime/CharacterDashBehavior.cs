using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>定向冲刺行为，获准时锁定方向、速度和时长，由流程扣除体力并从下一固定步开始执行。</summary>
    public sealed class CharacterDashBehavior : CharacterBehavior
    {
        private float _speed;

        /// <summary>冲刺已初始化，等待行为流程安排首次更新。</summary>
        public bool IsPending { get; private set; }
        public float RemainingDuration { get; private set; }
        public Vector2 Direction { get; private set; }
        public float StaminaCost { get; private set; }
        public float Cooldown { get; private set; }

        /// <summary>活动冲刺继续获准，新冲刺需要按下请求、冷却结束、足够体力及有效速度和时长。</summary>
        public override bool Evaluate(CharacterBehaviorContext context)
        {
            if (IsActive)
                return true;

            if (!context.Input.DashPressed || context.DashCooldownRemaining > 0f)
                return false;

            CharacterMovementSettings settings = context.Settings;
            float cost = InputMath.NonNegative(settings.DashStaminaCost, DefaultSettings.DashStaminaCost);
            float duration = InputMath.NonNegative(settings.DashDuration, DefaultSettings.DashDuration);
            float speed = InputMath.NonNegative(settings.DashSpeed, DefaultSettings.DashSpeed);
            return context.StaminaRemaining >= cost && duration > 0f && speed > 0f;
        }

        /// <summary>由流程在批准后调用，锁定本次冲刺参数，依次使用输入方向、朝向和世界正 Z 作为方向来源。</summary>
        public override void Start(CharacterBehaviorContext context)
        {
            CharacterMovementSettings settings = context.Settings;
            if (!InputMath.TryNormalizeDirection(context.Input.Direction, out Vector2 direction) &&
                !InputMath.TryNormalizeDirection(context.Facing, out direction))
                direction = Vector2.up;

            Direction = direction;
            RemainingDuration = InputMath.NonNegative(settings.DashDuration, DefaultSettings.DashDuration);
            _speed = InputMath.NonNegative(settings.DashSpeed, DefaultSettings.DashSpeed);
            StaminaCost = InputMath.NonNegative(settings.DashStaminaCost, DefaultSettings.DashStaminaCost);
            Cooldown = InputMath.NonNegative(settings.DashCooldown, DefaultSettings.DashCooldown);
            IsPending = true;
            IsActive = true;
        }

        /// <summary>清除活动和等待状态，将剩余时长归零，保留最近一次方向供流程结果读取。</summary>
        public override void End()
        {
            IsActive = false;
            IsPending = false;
            RemainingDuration = 0f;
        }

        /// <summary>按本步与剩余时长的较小值输出世界 X/Z 位移，并消耗剩余时长，由流程在耗尽后结束行为。</summary>
        public override Vector2 Update(CharacterBehaviorContext context)
        {
            if (!IsActive)
                return Vector2.zero;

            IsPending = false;
            float activeTime = Math.Min(context.DeltaTime, RemainingDuration);
            Vector2 displacement = Direction * (_speed * activeTime);
            RemainingDuration = InputMath.Elapsed(RemainingDuration, context.DeltaTime);
            return displacement;
        }
    }
}
