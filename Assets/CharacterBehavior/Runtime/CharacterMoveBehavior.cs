using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>基础平面移动行为，依据输入方向、速度和固定步长计算位移，执行资格由流程统一判定。</summary>
    public sealed class CharacterMoveBehavior : CharacterBehavior
    {
        /// <summary>输入方向非零且本步允许基础移动时返回 true。</summary>
        public override bool Evaluate(CharacterBehaviorContext context)
        {
            return !context.MovementBlocked && context.Input.Direction != Vector2.zero;
        }

        /// <summary>由流程在批准基础移动后激活行为。</summary>
        public override void Start(CharacterBehaviorContext context)
        {
            IsActive = true;
        }

        /// <summary>由流程在基础移动失去执行资格时清除活动状态。</summary>
        public override void End()
        {
            IsActive = false;
        }

        /// <summary>返回世界 X/Z 平面位移，单位为 Unity 单位，保留输入方向的幅度。</summary>
        public override Vector2 Update(CharacterBehaviorContext context)
        {
            if (!IsActive)
                return Vector2.zero;

            float speed = InputMath.NonNegative(context.Settings.MoveSpeed, DefaultSettings.MoveSpeed);
            return context.Input.Direction * (speed * context.DeltaTime);
        }
    }
}
