using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>A single input sample. Vector2 x/y represent world X/Z.</summary>
    public readonly struct CharacterInputSnapshot
    {
        public Vector2 Direction { get; }
        public bool DashPressed { get; }

        public CharacterInputSnapshot(Vector2 direction, bool dashPressed)
        {
            Direction = InputMath.ClampDirection(direction);
            DashPressed = dashPressed;
        }
    }

    /// <summary>Retains the latest direction and consumes a dash press exactly once.</summary>
    public sealed class CharacterInputBlackboard
    {
        private Vector2 _direction;
        private bool _dashPressed;

        public void Write(Vector2 direction, bool dashPressed)
        {
            _direction = direction;
            _dashPressed |= dashPressed;
        }

        public CharacterInputSnapshot Consume()
        {
            var snapshot = new CharacterInputSnapshot(_direction, _dashPressed);
            _dashPressed = false;
            return snapshot;
        }

        public void Clear()
        {
            _direction = Vector2.zero;
            _dashPressed = false;
        }
    }

    [Serializable]
    public sealed class CharacterMovementSettings
    {
        [Min(0f)] public float MoveSpeed = 5f;
        [Min(0f)] public float DashSpeed = 16f;
        [Min(0f)] public float DashDuration = 0.2f;
        [Min(0f)] public float DashCooldown = 0.5f;
        [Min(0f)] public float InitialStamina = 100f;
        [Min(0f)] public float DashStaminaCost = 25f;
    }

    public enum CharacterDisplacementSource
    {
        None,
        BasicMovement,
        Dash
    }

    public readonly struct CharacterBehaviorFrameResult
    {
        public long FrameNumber { get; }
        public Vector2 Displacement { get; }
        public CharacterDisplacementSource DisplacementSource { get; }
        public bool DashApproved { get; }
        public bool MoveApproved { get; }
        public bool DashStarted { get; }
        public bool DashEnded { get; }
        public Vector2 DashDirection { get; }
        public float StaminaRemaining { get; }
        public float DashCooldownRemaining { get; }

        internal CharacterBehaviorFrameResult(long frameNumber, Vector2 displacement,
            CharacterDisplacementSource source, bool dashApproved, bool moveApproved,
            bool dashStarted, bool dashEnded, Vector2 dashDirection,
            float staminaRemaining, float dashCooldownRemaining)
        {
            FrameNumber = frameNumber;
            Displacement = displacement;
            DisplacementSource = source;
            DashApproved = dashApproved;
            MoveApproved = moveApproved;
            DashStarted = dashStarted;
            DashEnded = dashEnded;
            DashDirection = dashDirection;
            StaminaRemaining = staminaRemaining;
            DashCooldownRemaining = dashCooldownRemaining;
        }
    }

    /// <summary>
    /// Pure movement kernel: no GameObject, Transform, clock, or input polling.
    /// Dash approval on tick N locks direction; execution begins on tick N+1.
    /// Each tick evaluates dash, then basic movement, before committing any state.
    /// </summary>
    public sealed class CharacterBehaviorFlow
    {
        private readonly CharacterDashBehavior _dash = new CharacterDashBehavior();
        private readonly CharacterMoveBehavior _movement = new CharacterMoveBehavior();
        private bool _initialized;

        public long FrameNumber { get; private set; }
        public float StaminaRemaining { get; private set; }
        public float DashCooldownRemaining { get; private set; }

        public CharacterBehaviorFrameResult Tick(CharacterInputSnapshot input, Vector2 facing,
            float deltaTime, CharacterMovementSettings settings)
        {
            settings = settings ?? CharacterBehavior.DefaultSettings;
            float dt = InputMath.NonNegative(deltaTime, 0f);
            float stamina = _initialized
                ? StaminaRemaining
                : InputMath.NonNegative(settings.InitialStamina, CharacterBehavior.DefaultSettings.InitialStamina);
            float cooldown = InputMath.Elapsed(DashCooldownRemaining, dt);

            // Freeze execution eligibility before a new dash can be approved.
            bool dashExecutes = _dash.IsActive;
            var context = new CharacterBehaviorContext(input, facing, dt, settings, stamina, cooldown,
                dashExecutes);

            // Finish all read-only decisions before invoking any lifecycle method.
            bool dashAllowed = _dash.Evaluate(context);
            bool moveApproved = _movement.Evaluate(context);
            bool dashApproved = dashAllowed && !dashExecutes;

            return ExecuteFrame(context, dashApproved, dashExecutes, moveApproved);
        }

        private CharacterBehaviorFrameResult ExecuteFrame(CharacterBehaviorContext context,
            bool dashApproved, bool dashExecutes, bool moveApproved)
        {
            // Execution starts only after every decision has completed.
            _initialized = true;
            FrameNumber++;
            StaminaRemaining = context.StaminaRemaining;
            DashCooldownRemaining = context.DashCooldownRemaining;
            Vector2 displacement = Vector2.zero;
            CharacterDisplacementSource source = CharacterDisplacementSource.None;
            bool dashStarted = dashExecutes && _dash.IsPending;
            bool dashEnded = false;

            if (dashApproved)
            {
                _dash.Start(context);
                StaminaRemaining = Math.Max(0f, StaminaRemaining - _dash.StaminaCost);
                DashCooldownRemaining = _dash.Cooldown;
            }

            if (moveApproved && !_movement.IsActive)
                _movement.Start(context);
            else if (!moveApproved && _movement.IsActive)
                _movement.End();

            if (dashExecutes)
            {
                displacement = _dash.Update(context);
                source = CharacterDisplacementSource.Dash;
                dashEnded = _dash.RemainingDuration == 0f;
                if (dashEnded)
                    _dash.End();
            }
            else if (moveApproved)
            {
                displacement = _movement.Update(context);
                source = CharacterDisplacementSource.BasicMovement;
            }

            return new CharacterBehaviorFrameResult(FrameNumber, displacement, source,
                dashApproved, moveApproved, dashStarted, dashEnded, _dash.Direction,
                StaminaRemaining, DashCooldownRemaining);
        }
    }

    internal static class InputMath
    {
        public static float NonNegative(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : Math.Max(0f, value);
        }

        public static float Elapsed(float remaining, float deltaTime)
        {
            float next = Math.Max(0f, remaining - deltaTime);
            // Relative tolerance removes float residue without ending a zero-time tick.
            return next <= deltaTime * 0.00001f ? 0f : next;
        }

        public static Vector2 ClampDirection(Vector2 direction)
        {
            if (float.IsNaN(direction.x) || float.IsInfinity(direction.x) ||
                float.IsNaN(direction.y) || float.IsInfinity(direction.y))
                return Vector2.zero;

            // Float components can be finite while their float squared magnitude overflows.
            double lengthSquared = (double)direction.x * direction.x + (double)direction.y * direction.y;
            if (lengthSquared <= 1d)
                return direction;

            double length = Math.Sqrt(lengthSquared);
            return new Vector2((float)(direction.x / length), (float)(direction.y / length));
        }

        public static bool TryNormalizeDirection(Vector2 direction, out Vector2 normalized)
        {
            double lengthSquared = (double)direction.x * direction.x + (double)direction.y * direction.y;
            // Use one threshold for both validity and normalization, including at epsilon.
            double minimumLengthSquared = (double)Vector2.kEpsilon * Vector2.kEpsilon;
            if (double.IsNaN(lengthSquared) || double.IsInfinity(lengthSquared) ||
                lengthSquared <= minimumLengthSquared)
            {
                normalized = Vector2.zero;
                return false;
            }

            double length = Math.Sqrt(lengthSquared);
            normalized = new Vector2((float)(direction.x / length), (float)(direction.y / length));
            return true;
        }
    }
}
