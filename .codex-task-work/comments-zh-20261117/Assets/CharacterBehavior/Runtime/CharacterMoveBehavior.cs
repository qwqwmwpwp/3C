using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    public sealed class CharacterMoveBehavior : CharacterBehavior
    {
        public override bool Evaluate(CharacterBehaviorContext context)
        {
            return !context.MovementBlocked && context.Input.Direction != Vector2.zero;
        }

        public override void Start(CharacterBehaviorContext context)
        {
            IsActive = true;
        }

        public override void End()
        {
            IsActive = false;
        }

        public override Vector2 Update(CharacterBehaviorContext context)
        {
            if (!IsActive)
                return Vector2.zero;

            float speed = InputMath.NonNegative(context.Settings.MoveSpeed, DefaultSettings.MoveSpeed);
            return context.Input.Direction * (speed * context.DeltaTime);
        }
    }
}
