using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>单次输入快照，Vector2 的 x/y 分别对应世界 X/Z 轴。</summary>
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

    /// <summary>保存最新方向，累计冲刺按下事件并在消费快照时清除事件标记。</summary>
    public sealed class CharacterInputBlackboard
    {
        private Vector2 _direction;
        private bool _dashPressed;

        /// <summary>写入最新方向并累计按下事件，多次写入会合并为一次待消费的冲刺请求。</summary>
        public void Write(Vector2 direction, bool dashPressed)
        {
            _direction = direction;
            _dashPressed |= dashPressed;
        }

        /// <summary>返回当前快照，保留方向并清除冲刺按下标记，供固定步结束时提交输入。</summary>
        public CharacterInputSnapshot Consume()
        {
            var snapshot = new CharacterInputSnapshot(_direction, _dashPressed);
            _dashPressed = false;
            return snapshot;
        }

        /// <summary>清空方向和待消费的按下事件，用于停用、失焦或暂停等生命周期边界。</summary>
        public void Clear()
        {
            _direction = Vector2.zero;
            _dashPressed = false;
        }
    }

    /// <summary>世界 XZ 平面移动与冲刺配置，体力初值在流程首次执行时读取。</summary>
    [Serializable]
    public sealed class CharacterMovementSettings
    {
        [Min(0f), Tooltip("基础移动速度，单位为 Unity 单位/秒，最小为 0。")]
        public float MoveSpeed = 5f;
        [Min(0f), Tooltip("冲刺速度，单位为 Unity 单位/秒。大于 0 时允许发起冲刺，获准时锁定本次速度。")]
        public float DashSpeed = 16f;
        [Min(0f), Tooltip("单次冲刺持续时间，单位为秒。大于 0 时允许发起冲刺，最后一步按剩余时间计算位移。")]
        public float DashDuration = 0.2f;
        [Min(0f), Tooltip("冲刺冷却时间，单位为秒，最小为 0，从批准冲刺时开始计时。")]
        public float DashCooldown = 0.5f;
        [Min(0f), Tooltip("流程首次执行时使用的体力值，最小为 0。当前流程仅消费体力，暂停期间保留剩余值。")]
        public float InitialStamina = 100f;
        [Min(0f), Tooltip("每次批准冲刺时扣除的体力，最小为 0。剩余体力达到此值才允许冲刺。")]
        public float DashStaminaCost = 25f;
    }

    /// <summary>本固定步输出位移的来源，用于调用方识别基础移动或冲刺结果。</summary>
    public enum CharacterDisplacementSource
    {
        None,
        BasicMovement,
        Dash
    }

    /// <summary>单个固定步的提交结果，包含世界 X/Z 位移、行为判定及提交后的体力和冷却状态。</summary>
    public readonly struct CharacterBehaviorFrameResult
    {
        public long FrameNumber { get; }
        public Vector2 Displacement { get; }
        public CharacterDisplacementSource DisplacementSource { get; }
        public bool DashApproved { get; }
        public bool MoveApproved { get; }
        /// <summary>本步首次执行冲刺位移，时间上晚于批准冲刺一步。</summary>
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
    /// 纯移动计算内核，输入快照、朝向、步长和配置由调用方提供。
    /// 第 N 步批准冲刺并锁定方向，第 N+1 步开始输出冲刺位移。
    /// 每步依次判定冲刺和基础移动，全部判定完成后统一提交状态。
    /// </summary>
    public sealed class CharacterBehaviorFlow
    {
        private readonly CharacterDashBehavior _dash = new CharacterDashBehavior();
        private readonly CharacterMoveBehavior _movement = new CharacterMoveBehavior();
        private bool _initialized;

        public long FrameNumber { get; private set; }
        public float StaminaRemaining { get; private set; }
        public float DashCooldownRemaining { get; private set; }

        /// <summary>依次完成行为判定与执行，更新流程拥有的体力、冷却和帧号，返回本步结果。</summary>
        /// <param name="input">调用方已提交的输入快照，驱动器使用上一个固定步结束时提交的快照。</param>
        /// <param name="facing">世界 X/Z 平面朝向，在冲刺输入方向无效时作为后备方向。</param>
        /// <param name="deltaTime">步长，单位为秒。负数或非有限数按 0 处理。</param>
        /// <param name="settings">移动配置，传入 null 时使用默认配置。</param>
        /// <returns>本步位移和提交后的状态，位移由调用方应用到角色。</returns>
        public CharacterBehaviorFrameResult Tick(CharacterInputSnapshot input, Vector2 facing,
            float deltaTime, CharacterMovementSettings settings)
        {
            settings = settings ?? CharacterBehavior.DefaultSettings;
            float dt = InputMath.NonNegative(deltaTime, 0f);
            float stamina = _initialized
                ? StaminaRemaining
                : InputMath.NonNegative(settings.InitialStamina, CharacterBehavior.DefaultSettings.InitialStamina);
            float cooldown = InputMath.Elapsed(DashCooldownRemaining, dt);

            // 批准新冲刺之前，先固定本步已有冲刺的执行资格。
            bool dashExecutes = _dash.IsActive;
            var context = new CharacterBehaviorContext(input, facing, dt, settings, stamina, cooldown,
                dashExecutes);

            // 先完成全部只读判定，再调用任何会修改状态的生命周期方法。
            bool dashAllowed = _dash.Evaluate(context);
            bool moveApproved = _movement.Evaluate(context);
            bool dashApproved = dashAllowed && !dashExecutes;

            return ExecuteFrame(context, dashApproved, dashExecutes, moveApproved);
        }

        private CharacterBehaviorFrameResult ExecuteFrame(CharacterBehaviorContext context,
            bool dashApproved, bool dashExecutes, bool moveApproved)
        {
            // 全部判定完成后，统一执行生命周期、资源扣除和位移计算。
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
            // 通过相对容差清除浮点残余，零时间步长仍保留尚未耗尽的时间。
            return next <= deltaTime * 0.00001f ? 0f : next;
        }

        public static Vector2 ClampDirection(Vector2 direction)
        {
            if (float.IsNaN(direction.x) || float.IsInfinity(direction.x) ||
                float.IsNaN(direction.y) || float.IsInfinity(direction.y))
                return Vector2.zero;

            // 有限浮点分量的平方和仍可能溢出，因此使用双精度计算长度。
            double lengthSquared = (double)direction.x * direction.x + (double)direction.y * direction.y;
            if (lengthSquared <= 1d)
                return direction;

            double length = Math.Sqrt(lengthSquared);
            return new Vector2((float)(direction.x / length), (float)(direction.y / length));
        }

        public static bool TryNormalizeDirection(Vector2 direction, out Vector2 normalized)
        {
            double lengthSquared = (double)direction.x * direction.x + (double)direction.y * direction.y;
            // 有效性判断与归一化共用同一阈值，并统一处理等于阈值的边界。
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
