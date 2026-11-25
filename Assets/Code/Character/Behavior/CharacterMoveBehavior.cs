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
        public override Vector2 Update(CharacterBehaviorContext context) => Update(context, context.DeltaTime);

        // 按可用时间计算普通移动，并应用活动行为给出的目标速度限制。
        internal Vector2 Update(CharacterBehaviorContext context, float seconds, float speedLimit = float.PositiveInfinity)
        {
            if (!IsActive)
                return Vector2.zero;

            return TargetVelocity(context.Input, context.Settings, speedLimit) * seconds;
        }

        // 根据持续方向、步态与许可限速计算本段普通移动的世界平面速度。
        internal static Vector2 TargetVelocity(in CharacterInputSnapshot input, CharacterMovementSettings settings, float speedLimit)
        {
            if (input.Direction == Vector2.zero) return Vector2.zero;
            var velocity = input.Direction * SpeedForGait(settings, input.Gait);
            return float.IsPositiveInfinity(speedLimit) ? velocity : Vector2.ClampMagnitude(velocity, speedLimit);
        }

        // 本步没有方向输入时保持静止，否则使用当前档位的有效目标速度。
        internal static float GetRequestedSpeed(CharacterBehaviorContext context) =>
            context.Input.Direction == Vector2.zero ? 0f : SpeedForGait(context.Settings, context.Input.Gait);

        // 按步态读取非负速度，非法浮点值回退到该档默认值，供实走和预测共同使用。
        internal static float SpeedForGait(CharacterMovementSettings settings, CharacterMoveGait gait)
        {
            switch (gait)
            {
                case CharacterMoveGait.Walk:
                    return InputMath.NonNegative(settings.WalkSpeed, CharacterMovementSettings.DefaultWalkSpeed);
                case CharacterMoveGait.Sprint:
                    return InputMath.NonNegative(settings.SprintSpeed, CharacterMovementSettings.DefaultSprintSpeed);
                default:
                    return InputMath.NonNegative(settings.MoveSpeed, CharacterMovementSettings.DefaultMoveSpeed);
            }
        }
    }
}
