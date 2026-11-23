using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>世界 XZ 平面移动与冲刺配置，体力初值在初始化或显式重置时读取。</summary>
    [Serializable]
    public sealed class CharacterMovementSettings : CharacterLocomotionSettings
    {
        internal CharacterMovementSettings Snapshot() => (CharacterMovementSettings)MemberwiseClone();
        internal const float DefaultDashSpeed = 16f;
        internal const float DefaultDashDuration = 0.2f;
        internal const float DefaultDashCooldown = 0.5f;
        internal const float DefaultInitialStamina = 100f;
        internal const float DefaultDashStaminaCost = 25f;

        [Min(0f), Tooltip("冲刺速度，单位为 Unity 单位/秒。大于 0 时允许发起冲刺，获准时锁定本次速度。")]
        public float DashSpeed = DefaultDashSpeed;
        [Min(0f), Tooltip("单次冲刺持续时间，单位为秒。大于 0 时允许发起冲刺，最后一步按剩余时间计算位移。")]
        public float DashDuration = DefaultDashDuration;
        [Min(0f), Tooltip("冲刺冷却时间，单位为秒，最小为 0，从批准冲刺时开始计时。")]
        public float DashCooldown = DefaultDashCooldown;
        [Min(0f), Tooltip("初始化或显式重置时使用的体力值，最小为 0。当前流程仅消费体力，暂停期间保留剩余值。")]
        public float InitialStamina = DefaultInitialStamina;
        [Min(0f), Tooltip("每次批准冲刺时扣除的体力，最小为 0。剩余体力达到此值才允许冲刺。")]
        public float DashStaminaCost = DefaultDashStaminaCost;
    }

    /// <summary>每个固定步先释放结束的行为，再按优先级批准请求并执行。
    /// 待机发起的基础移动优先级最低，已获位移许可的移动按有向打断规则处理。</summary>
    public sealed class CharacterBehaviorFlow
    {
        private readonly CharacterMovementSettings _fallbackSettings = new CharacterMovementSettings();
        private readonly CharacterDashBehavior _dash = new CharacterDashBehavior();
        private readonly CharacterMoveBehavior _movement = new CharacterMoveBehavior();
        private readonly CharacterBehaviorRules _rules;
        private CharacterBehaviorId _active;
        private bool _initialized;
        private double simulationSeconds;
        private Dictionary<CharacterBehaviorId, double> grantTimes;
        private bool _previousMoveApproved;
        private CharacterAttackBehavior attackBehavior;
        private bool forecasting;

        // 两个流程由同一角色规则工厂创建，拥有独立行为实例；预测不发布许可时刻。
        internal void CopyStateTo(CharacterBehaviorFlow copy)
        {
            copy._active = _active;
            copy._previousMoveApproved = _previousMoveApproved;
            copy._initialized = _initialized;
            copy.simulationSeconds = simulationSeconds;
            copy.forecasting = true;
            copy.grantTimes = null;
            copy.FrameNumber = FrameNumber;
            copy.StaminaRemaining = StaminaRemaining;
            copy.DashCooldownRemaining = DashCooldownRemaining;
            _movement.CopyStateTo(copy._movement);
            for (int i = 0; i < _rules.Actions.Length; i++)
                _rules.Actions[i].Behavior.CopyStateTo(copy._rules.Actions[i].Behavior);
        }

        /// <summary>仅在预测副本中，对上游确认已经打断的动作释放位移控制；保留体力和冷却。</summary>
        internal void ReleasePredictionActions()
        {
            if (!forecasting) throw new InvalidOperationException("需要独立的预测副本");
            foreach (var rule in _rules.Actions)
                if (rule.Behavior.IsActive) rule.Behavior.Deactivate();
            _movement.Deactivate();
            _active = CharacterBehaviorId.None;
            _previousMoveApproved = false;
            ClearAttackBuffer();
        }

        internal void ClearAttackBuffer() => attackBehavior?.ClearBuffer();

        public long FrameNumber { get; private set; }
        public float StaminaRemaining { get; private set; }
        public float DashCooldownRemaining { get; private set; }

        internal static CharacterBehaviorFlow ForCharacter(CharacterBehaviorRegistration registration, bool snapshot = false)
        {
            var rules = new List<CharacterBehaviorRule>();
            var interrupts = new List<CharacterBehaviorInterrupt>();
            if (registration.Guard)
            {
                rules.Add(new CharacterBehaviorRule(CharacterBehaviorId.Guard, new CharacterGuardBehavior(),
                    conflicts: CharacterBehaviorId.Dash));
                if (registration.DashInterruptsGuard)
                    interrupts.Add(new CharacterBehaviorInterrupt(CharacterBehaviorId.Dash, CharacterBehaviorId.Guard));
            }
            CharacterAttackBehavior combo = null;
            if (registration.Attack != null)
            {
                combo = new CharacterAttackBehavior(snapshot ? registration.Attack.Snapshot() : registration.Attack,
                    snapshot ? registration.Routes.Snapshot() : registration.Routes);
                rules.Add(new CharacterBehaviorRule(CharacterBehaviorId.Attack, combo,
                    priority: 80, conflicts: registration.Guard ? CharacterBehaviorId.Guard : CharacterBehaviorId.None,
                    providesDisplacement: true));
                if (registration.Guard && registration.AttackInterruptsGuard)
                    interrupts.Add(new CharacterBehaviorInterrupt(CharacterBehaviorId.Attack, CharacterBehaviorId.Guard));
                interrupts.Add(new CharacterBehaviorInterrupt(CharacterBehaviorId.Attack, CharacterBehaviorId.Move));
                var phases = registration.DashInterruptPhases;
                if (phases != 0)
                    interrupts.Add(new CharacterBehaviorInterrupt(CharacterBehaviorId.Dash, CharacterBehaviorId.Attack, phases));
            }
            return new CharacterBehaviorFlow(rules.ToArray(), interrupts.ToArray()) { attackBehavior = combo };
        }

        public CharacterBehaviorFlow() : this(null)
        {
        }

        /// <summary>追加行为规则。内置普通移动与冲刺保留，额外规则需要提供独立的行为实例。</summary>
        public CharacterBehaviorFlow(CharacterBehaviorRule[] additionalRules, CharacterBehaviorInterrupt[] interrupts = null)
        {
            _rules = new CharacterBehaviorRules(_movement, _dash, additionalRules, interrupts);
        }

        /// <summary>结束全部行为并按配置恢复初始资源，逻辑步编号归零；供角色创建或重生时调用。</summary>
        public void Reset(CharacterMovementSettings settings = null)
        {
            settings ??= _fallbackSettings;
            foreach (CharacterBehaviorRule rule in _rules.Actions)
                rule.Behavior.Reset();
            _movement.Reset();
            _active = CharacterBehaviorId.None;
            _previousMoveApproved = false;
            FrameNumber = 0;
            simulationSeconds = 0d;
            grantTimes = null;
            StaminaRemaining = InputMath.NonNegative(settings.InitialStamina,
                CharacterMovementSettings.DefaultInitialStamina);
            DashCooldownRemaining = 0f;
            _initialized = true;
        }

        /// <summary>用驱动器上一步末提交的输入推进裁决，更新体力、冷却和帧号。冲刺输入方向无效时使用世界 XZ 朝向。
        /// 步长以秒计，仅有限正值会推进状态。配置为空时使用默认值。</summary>
        /// <returns>步末状态和由调用方应用的位移。</returns>
        public CharacterBehaviorFrameResult Tick(CharacterInputSnapshot input, Vector2 facing,
            float deltaTime, CharacterMovementSettings settings) => Tick(input, facing, deltaTime, settings, default);

        public CharacterBehaviorFrameResult Tick(CharacterInputSnapshot input, Vector2 facing, float deltaTime,
            CharacterMovementSettings settings, Vector2 groundVelocity)
        {
            settings ??= _fallbackSettings;
            float dt = InputMath.NonNegative(deltaTime, 0f);
            float stamina = _initialized
                ? StaminaRemaining
                : InputMath.NonNegative(settings.InitialStamina, CharacterMovementSettings.DefaultInitialStamina);
            float cooldown = InputMath.Elapsed(DashCooldownRemaining, dt);
            var step = new CharacterBehaviorStep { Active = _active, OrdinarySpeedLimit = float.PositiveInfinity,
                StartSeconds = simulationSeconds, GrantTimes = grantTimes, SkipGrantHistory = forecasting };

            if (dt == 0f)
            {
                step.Attack = attackBehavior?.State ?? default;
                return new CharacterBehaviorFrameResult(FrameNumber, Vector2.zero, false, false,
                    _dash.Direction, stamina, cooldown, step);
            }

            var context = new CharacterBehaviorContext(input, facing, dt, settings, stamina, cooldown, simulationSeconds, groundVelocity);
            ReleaseBehaviorRequests(context, ref step);
            ResolveActionRequests(ref context, ref step);
            var action = AdvanceActions(context, ref step, out float movementSeconds);
            bool moveApproved = ResolveMovement(context, movementSeconds, ref step);
            Vector2 displacement = action.Displacement;
            if (moveApproved)
                displacement += ApplyOrdinaryMovement(context, movementSeconds, action.ControlsDisplacement, ref step);
            attackBehavior?.RecordStep(moveApproved, ref step);
            step.Executed = _active;
            FinishCompletedActions(ref step);
            step.Active = _active;
            if (moveApproved && !_previousMoveApproved) step.RecordGrant(CharacterBehaviorId.Move);
            grantTimes = step.GrantTimes;
            simulationSeconds += dt;

            _initialized = true;
            FrameNumber++;
            StaminaRemaining = context.StaminaRemaining;
            DashCooldownRemaining = context.DashCooldownRemaining;
            var result = new CharacterBehaviorFrameResult(FrameNumber, displacement, moveApproved,
                _previousMoveApproved, _dash.Direction, StaminaRemaining, DashCooldownRemaining, step);
            _previousMoveApproved = moveApproved;
            return result;
        }

        private bool IsActive(CharacterBehaviorId id) => (_active & id) != CharacterBehaviorId.None;

        private void ReleaseBehaviorRequests(CharacterBehaviorContext context, ref CharacterBehaviorStep step)
        {
            foreach (CharacterBehaviorRule rule in _rules.Actions)
            {
                if (IsActive(rule.Id) && !rule.Behavior.ShouldContinue(context))
                {
                    step.Withdrawn |= rule.Id;
                    EndBehavior(rule, ref step);
                }
            }
        }

        private void ResolveActionRequests(ref CharacterBehaviorContext context, ref CharacterBehaviorStep step)
        {
            foreach (CharacterBehaviorRule rule in _rules.Actions)
            {
                var request = rule.Behavior.PrepareRequest(context);
                if (!rule.Behavior.RequestsStart(request))
                    continue;

                CharacterBehaviorRejection rejection = CheckStart(rule, request, step.Started,
                    out float cost, out CharacterBehaviorId interrupted);
                if (rejection != CharacterBehaviorRejection.None)
                {
                    step.Rejections.Set(rule.Id, rejection);
                    continue;
                }

                // 全部准入检查通过后，统一打断旧行为、启动新行为并扣费。
                if (interrupted != CharacterBehaviorId.None)
                {
                    if ((interrupted & CharacterBehaviorId.Move) != CharacterBehaviorId.None)
                        EndBehavior(_rules.Movement, ref step);
                    foreach (CharacterBehaviorRule holder in _rules.Actions)
                    {
                        if ((interrupted & holder.Id) != CharacterBehaviorId.None)
                            EndBehavior(holder, ref step);
                    }
                    step.Interrupted |= interrupted;
                }

                StartBehavior(rule, request, ref step);
                float cooldown = rule.Id == CharacterBehaviorId.Dash ? _dash.Cooldown : context.DashCooldownRemaining;
                context = new CharacterBehaviorContext(context.Input, context.Facing, context.DeltaTime, context.Settings,
                    context.StaminaRemaining - cost, cooldown, context.SimulationTime, context.GroundVelocity);
            }
        }

        private CharacterBehaviorRejection CheckStart(CharacterBehaviorRule rule, CharacterBehaviorContext context,
            CharacterBehaviorId started, out float cost, out CharacterBehaviorId interrupted)
        {
            cost = 0f;
            interrupted = CharacterBehaviorId.None;
            if (IsActive(rule.Id))
                return CharacterBehaviorRejection.AlreadyActive;

            CharacterBehaviorRejection rejection = rule.Behavior.GetStartRejection(context);
            if (rejection != CharacterBehaviorRejection.None)
                return rejection;

            cost = rule.Behavior.GetStaminaCost(context);
            if (cost < 0f || float.IsNaN(cost) || float.IsInfinity(cost))
                return CharacterBehaviorRejection.InvalidSettings;
            if (context.StaminaRemaining < cost)
                return CharacterBehaviorRejection.InsufficientStamina;
            if (!PassesGates(rule.Id))
                return CharacterBehaviorRejection.GateDenied;

            CharacterBehaviorRule movement = _rules.Movement;
            if (IsActive(movement.Id) && _previousMoveApproved && _movement.Evaluate(context)
                && rule.InterruptsMovementOnStart(movement))
            {
                rejection = _rules.CheckInterrupt(rule.Id, movement);
                if (rejection != CharacterBehaviorRejection.None)
                    return rejection;
                interrupted |= movement.Id;
            }

            foreach (CharacterBehaviorRule holder in _rules.Actions)
            {
                if (!IsActive(holder.Id) || !rule.ConflictsWith(holder))
                    continue;

                if ((started & holder.Id) != CharacterBehaviorId.None) // 本步刚获准的行为不会被后续请求打断。
                    return CharacterBehaviorRejection.Conflict;

                rejection = _rules.CheckInterrupt(rule.Id, holder);
                if (rejection != CharacterBehaviorRejection.None)
                    return rejection;
                interrupted |= holder.Id;
            }
            return CharacterBehaviorRejection.None;
        }

        private bool PassesGates(CharacterBehaviorId request)
        {
            foreach (CharacterBehaviorRule holder in _rules.Actions)
            {
                if (IsActive(holder.Id) && !holder.Gate.Admits(request))
                    return false;
            }
            return true;
        }

        private bool ResolveMovement(CharacterBehaviorContext context, float movementSeconds, ref CharacterBehaviorStep step)
        {
            CharacterBehaviorRule movement = _rules.Movement;
            bool requested = _movement.Evaluate(context);
            if (movementSeconds <= 0f)
            {
                if (IsActive(movement.Id))
                    EndBehavior(movement, ref step);
                if (requested)
                    step.Rejections.Set(movement.Id, CharacterBehaviorRejection.Conflict);
                return false;
            }

            if (!IsActive(movement.Id))
                StartBehavior(movement, context, ref step);
            if (!requested)
                return false;
            if (_previousMoveApproved || PassesGates(movement.Id))
                return true;

            step.Rejections.Set(movement.Id, CharacterBehaviorRejection.GateDenied);
            return false;
        }

        private CharacterBehaviorMotion AdvanceActions(CharacterBehaviorContext context, ref CharacterBehaviorStep step,
            out float movementSeconds)
        {
            movementSeconds = context.DeltaTime;
            CharacterBehaviorMotion providerMotion = default;
            foreach (CharacterBehaviorRule rule in _rules.Actions)
            {
                if (!IsActive(rule.Id))
                    continue;

                var motion = rule.Behavior.AdvanceStep(context);
                step.OrdinarySpeedLimit = Math.Min(step.OrdinarySpeedLimit, rule.OrdinarySpeedLimit);
                if (rule.HasExplicitConflictWith(_rules.Movement))
                    movementSeconds = 0f;
                if (!rule.ProvidesDisplacement)
                    continue;

                providerMotion = motion; // 准入规则保证至多一个活动动作提供位移。
                movementSeconds = Math.Min(movementSeconds, motion.OrdinaryMovementSeconds);
                step.Motion = motion.Motion;
                if (motion.ControlsDisplacement || motion.Displacement != Vector2.zero)
                    step.DisplacementProvider = rule.Id;
            }
            return providerMotion;
        }

        private Vector2 ApplyOrdinaryMovement(CharacterBehaviorContext context, float seconds, bool actionControlsDisplacement,
            ref CharacterBehaviorStep step)
        {
            if (!actionControlsDisplacement)
                step.DisplacementProvider = CharacterBehaviorId.Move;
            Vector2 movement = _movement.Update(context, seconds);
            return float.IsPositiveInfinity(step.OrdinarySpeedLimit) ? movement :
                Vector2.ClampMagnitude(movement, step.OrdinarySpeedLimit * seconds);
        }

        private void FinishCompletedActions(ref CharacterBehaviorStep step)
        {
            foreach (CharacterBehaviorRule rule in _rules.Actions)
                if (IsActive(rule.Id) && rule.Behavior.IsComplete)
                    EndBehavior(rule, ref step);
        }

        private void StartBehavior(CharacterBehaviorRule rule, CharacterBehaviorContext context, ref CharacterBehaviorStep step)
        {
            rule.Behavior.Activate(context);
            _active |= rule.Id;
            step.Started |= rule.Id;
            if (rule.Id != CharacterBehaviorId.Move) step.RecordGrant(rule.Id);
        }

        private void EndBehavior(CharacterBehaviorRule rule, ref CharacterBehaviorStep step)
        {
            rule.Behavior.Deactivate();
            _active &= ~rule.Id;
            step.Ended |= rule.Id;
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
            return next <= deltaTime * 0.00001f ? 0f : next; // 按步长清除舍入残差，零步长保留余量。
        }

        public static Vector2 ClampDirection(Vector2 direction)
        {
            if (float.IsNaN(direction.x) || float.IsInfinity(direction.x) ||
                float.IsNaN(direction.y) || float.IsInfinity(direction.y))
                return Vector2.zero;

            double lengthSquared = (double)direction.x * direction.x + (double)direction.y * direction.y; // 避免平方和溢出。
            if (lengthSquared <= 1d)
                return direction;

            double length = Math.Sqrt(lengthSquared);
            return new Vector2((float)(direction.x / length), (float)(direction.y / length));
        }

        public static bool TryNormalizeDirection(Vector2 direction, out Vector2 normalized)
        {
            double lengthSquared = (double)direction.x * direction.x + (double)direction.y * direction.y;
            double minimumLengthSquared = (double)Vector2.kEpsilon * Vector2.kEpsilon; // 与方向有效性检查共用下限。
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
