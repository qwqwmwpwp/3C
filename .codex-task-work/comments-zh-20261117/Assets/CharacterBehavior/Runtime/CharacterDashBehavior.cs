using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    public sealed class CharacterDashBehavior : CharacterBehavior
    {
        private float _speed;

        public bool IsPending { get; private set; }
        public float RemainingDuration { get; private set; }
        public Vector2 Direction { get; private set; }
        public float StaminaCost { get; private set; }
        public float Cooldown { get; private set; }

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

        public override void End()
        {
            IsActive = false;
            IsPending = false;
            RemainingDuration = 0f;
        }

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
