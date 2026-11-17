using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>Shared lifecycle for fixed-step character behaviors, owned by the behavior flow.</summary>
    public abstract class CharacterBehavior
    {
        internal static readonly CharacterMovementSettings DefaultSettings = new CharacterMovementSettings();

        public bool IsActive { get; protected set; }

        /// <summary>Read-only decision about whether this behavior may start or continue.</summary>
        public abstract bool Evaluate(CharacterBehaviorContext context);

        /// <summary>Initialize once after approval. The flow decides when the first update runs.</summary>
        public abstract void Start(CharacterBehaviorContext context);

        /// <summary>Release active state when the behavior completes or loses approval.</summary>
        public abstract void End();

        /// <summary>Advance an active behavior by one fixed step and return its world X/Z displacement.</summary>
        public abstract Vector2 Update(CharacterBehaviorContext context);
    }

    /// <summary>Inputs shared by behavior decisions and execution during one fixed step.</summary>
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
