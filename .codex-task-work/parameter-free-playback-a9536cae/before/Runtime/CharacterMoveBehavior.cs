using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>默认执行的基础平面移动行为，统一处理待机与普通位移。</summary>
    public sealed class CharacterMoveBehavior : CharacterBehavior
    {
        /// <summary>判断普通移动请求是否满足方向和速度条件。</summary>
        public override bool Evaluate(CharacterBehaviorContext context)
        {
            return GetRequestedSpeed(context) > 0f;
        }

        /// <summary>返回世界 X/Z 平面位移，单位为 Unity 单位，保留输入方向的幅度。</summary>
        public override Vector2 Update(CharacterBehaviorContext context)
        {
            if (!IsActive)
                return Vector2.zero;

            return context.Input.Direction * (GetRequestedSpeed(context) * context.DeltaTime);
        }

        private static float GetRequestedSpeed(CharacterBehaviorContext context)
        {
            if (context.Input.Direction == Vector2.zero)
                return 0f;

            switch (context.Input.Gait)
            {
                case CharacterMoveGait.Walk:
                    return InputMath.NonNegative(context.Settings.WalkSpeed, CharacterMovementSettings.DefaultWalkSpeed);
                case CharacterMoveGait.Sprint:
                    return InputMath.NonNegative(context.Settings.SprintSpeed, CharacterMovementSettings.DefaultSprintSpeed);
                default:
                    return InputMath.NonNegative(context.Settings.MoveSpeed, CharacterMovementSettings.DefaultMoveSpeed);
            }
        }
    }
}
