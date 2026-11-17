using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>固定步长角色行为的统一生命周期，由行为流程负责调度。</summary>
    public abstract class CharacterBehavior
    {
        internal static readonly CharacterMovementSettings DefaultSettings = new CharacterMovementSettings();

        /// <summary>行为已进入活动生命周期，包括冲刺获准后等待首次执行的阶段。</summary>
        public bool IsActive { get; protected set; }

        /// <summary>只读判定行为能否启动或继续，判定阶段保持行为状态。</summary>
        public abstract bool Evaluate(CharacterBehaviorContext context);

        /// <summary>获准后初始化一次，首次执行更新的时机由行为流程决定。</summary>
        public abstract void Start(CharacterBehaviorContext context);

        /// <summary>行为完成或失去执行资格时释放活动状态。</summary>
        public abstract void End();

        /// <summary>推进活动行为一个固定步长，返回世界 X/Z 平面位移，单位为 Unity 单位。</summary>
        public abstract Vector2 Update(CharacterBehaviorContext context);
    }

    /// <summary>同一固定步内供行为判定和执行共享的输入，时间步长与冷却时间以秒计。</summary>
    public readonly struct CharacterBehaviorContext
    {
        public CharacterInputSnapshot Input { get; }
        public Vector2 Facing { get; }
        public float DeltaTime { get; }
        public CharacterMovementSettings Settings { get; }
        public float StaminaRemaining { get; }
        public float DashCooldownRemaining { get; }
        public bool MovementBlocked { get; }

        public CharacterBehaviorContext(CharacterInputSnapshot input, Vector2 facing, float deltaTime,
            CharacterMovementSettings settings, float staminaRemaining, float dashCooldownRemaining,
            bool movementBlocked)
        {
            Input = input;
            Facing = facing;
            DeltaTime = InputMath.NonNegative(deltaTime, 0f);
            Settings = settings ?? CharacterBehavior.DefaultSettings;
            StaminaRemaining = staminaRemaining;
            DashCooldownRemaining = dashCooldownRemaining;
            MovementBlocked = movementBlocked;
        }
    }
}
