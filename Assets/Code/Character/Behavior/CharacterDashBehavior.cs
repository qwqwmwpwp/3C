using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>定向冲刺行为，获准时锁定方向、速度和时长，由流程扣除体力并在本固定步开始执行。</summary>
    public sealed class CharacterDashBehavior : CharacterBehavior
    {
        private float _speed;

        public float RemainingDuration { get; private set; }
        public Vector2 Direction { get; private set; }
        public float Cooldown { get; private set; }
        public override bool IsComplete => RemainingDuration == 0f;

        internal override void CopyStateTo(CharacterBehavior target)
        {
            base.CopyStateTo(target);
            var copy = (CharacterDashBehavior)target;
            copy._speed = _speed;
            copy.RemainingDuration = RemainingDuration;
            copy.Direction = Direction;
            copy.Cooldown = Cooldown;
        }

        /// <summary>活动冲刺可继续执行，未活动时按新冲刺的条件判定。</summary>
        public override bool Evaluate(CharacterBehaviorContext context)
        {
            return IsActive || (RequestsStart(context) && GetStartRejection(context) == CharacterBehaviorRejection.None
                && context.StaminaRemaining >= GetStaminaCost(context));
        }

        public override bool RequestsStart(CharacterBehaviorContext context) => context.Input.DashPressed;

        public override float GetStaminaCost(CharacterBehaviorContext context) =>
            InputMath.NonNegative(context.Settings.DashStaminaCost, CharacterMovementSettings.DefaultDashStaminaCost);

        public override CharacterBehaviorRejection GetStartRejection(CharacterBehaviorContext context)
        {
            if (context.DashCooldownRemaining > 0f)
                return CharacterBehaviorRejection.Cooldown;

            CharacterMovementSettings settings = context.Settings;
            float duration = InputMath.NonNegative(settings.DashDuration, CharacterMovementSettings.DefaultDashDuration);
            float speed = InputMath.NonNegative(settings.DashSpeed, CharacterMovementSettings.DefaultDashSpeed);

            return duration > 0f && speed > 0f
                ? CharacterBehaviorRejection.None
                : CharacterBehaviorRejection.InvalidSettings;
        }

        /// <summary>由流程在批准后调用，锁定本次冲刺参数，依次使用输入方向、朝向和世界正 Z 作为方向来源。</summary>
        public override void Start(CharacterBehaviorContext context)
        {
            CharacterMovementSettings settings = context.Settings;
            if (!InputMath.TryNormalizeDirection(context.Input.Direction, out Vector2 direction) &&
                !InputMath.TryNormalizeDirection(context.Facing, out direction))
                direction = Vector2.up;

            Direction = direction;
            RemainingDuration = InputMath.NonNegative(settings.DashDuration, CharacterMovementSettings.DefaultDashDuration);
            _speed = InputMath.NonNegative(settings.DashSpeed, CharacterMovementSettings.DefaultDashSpeed);
            Cooldown = InputMath.NonNegative(settings.DashCooldown, CharacterMovementSettings.DefaultDashCooldown);
            IsActive = true;
        }

        /// <summary>结束冲刺并保留最近一次方向供流程结果读取。</summary>
        public override void End()
        {
            IsActive = false;
            RemainingDuration = 0f;
        }

        /// <summary>重新初始化角色时清除全部冲刺状态，包括上一次保留的方向。</summary>
        internal override void Reset()
        {
            End();
            Direction = Vector2.zero;
            _speed = 0f;
            Cooldown = 0f;
        }

        /// <summary>按本步与剩余时长的较小值输出世界 X/Z 位移，并消耗剩余时长，由流程在耗尽后结束行为。</summary>
        public override Vector2 Update(CharacterBehaviorContext context)
        {
            if (!IsActive)
                return Vector2.zero;

            float activeTime = Math.Min(context.DeltaTime, RemainingDuration);
            Vector2 displacement = Direction * (_speed * activeTime);
            RemainingDuration = InputMath.Elapsed(RemainingDuration, context.DeltaTime);
            return displacement;
        }
    }
}
