using System;
using System.Collections.Generic;
using ThreeC.Player;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>普通移动请求的速度档。默认值对应原有跑步速度。</summary>
    public enum CharacterMoveGait { Run, Walk, Sprint }

    /// <summary>只读执行黑板，在固定步末尾复制生成，下一步所有行为共享；x/y 分别对应世界 X/Z 轴。</summary>
    public readonly struct CharacterInputSnapshot
    {
        public Vector2 Direction { get; }
        public CharacterMoveGait Gait { get; }
        public CharacterBehaviorId Requests { get; }
        public AttackButtonPresses AttackPresses { get; }
        public bool DashPressed => (Requests & CharacterBehaviorId.Dash) != CharacterBehaviorId.None;
        public bool GuardHeld => (Requests & CharacterBehaviorId.Guard) != CharacterBehaviorId.None;

        public CharacterInputSnapshot(Vector2 direction, bool dashPressed)
            : this(direction, dashPressed, false)
        {
        }

        public CharacterInputSnapshot(Vector2 direction, bool dashPressed, bool guardHeld = false,
            CharacterBehaviorId requests = CharacterBehaviorId.None)
            : this(direction, dashPressed, guardHeld, requests, CharacterMoveGait.Run)
        {
        }

        public CharacterInputSnapshot(Vector2 direction, bool dashPressed, bool guardHeld,
            CharacterBehaviorId requests, CharacterMoveGait gait)
            : this(direction, dashPressed, guardHeld, requests, gait, default)
        {
        }

        public CharacterInputSnapshot(Vector2 direction, bool dashPressed, bool guardHeld,
            CharacterBehaviorId requests, CharacterMoveGait gait, AttackButtonPresses attackPresses)
        {
            Direction = InputMath.ClampDirection(direction);
            Gait = gait;
            AttackPresses = attackPresses.First == AttackButton.None && (requests & CharacterBehaviorId.Attack) != 0
                ? attackPresses.With(AttackButton.Light) : attackPresses;
            Requests = requests | (dashPressed ? CharacterBehaviorId.Dash : CharacterBehaviorId.None)
                | (guardHeld ? CharacterBehaviorId.Guard : CharacterBehaviorId.None)
                | (AttackPresses.First != AttackButton.None ? CharacterBehaviorId.Attack : CharacterBehaviorId.None);
        }
    }

    /// <summary>保存最新方向、移动档和持续请求，并累计短突进的按下事件，在固定步末尾转交。</summary>
    public sealed class CharacterInputBlackboard
    {
        private Vector2 _direction;
        private CharacterMoveGait _gait;
        private bool _dashPressed;
        private bool _guardHeld;
        private CharacterBehaviorId _pressedActions;
        private AttackButtonPresses _attackPresses;

        public void PressAttack(AttackButton button) => _attackPresses = _attackPresses.With(button);

        /// <summary>写入最新方向并累计按下事件，多次写入会合并为一次待消费的冲刺请求。</summary>
        public void Write(Vector2 direction, bool dashPressed)
        {
            Write(direction, dashPressed, CharacterMoveGait.Run);
        }

        public void Write(Vector2 direction, bool dashPressed, CharacterMoveGait gait)
        {
            Write(direction, dashPressed, gait, false);
        }

        internal void Write(Vector2 direction, bool dashPressed, CharacterMoveGait gait, bool guardHeld,
            CharacterBehaviorId pressedActions = CharacterBehaviorId.None)
        {
            _direction = direction;
            _gait = gait;
            _dashPressed |= dashPressed;
            _guardHeld = guardHeld;
            _pressedActions |= pressedActions;
        }

        /// <summary>保留方向和持续请求，只清除已转交的按下事件。每个固定步末尾调用一次。</summary>
        public CharacterInputSnapshot Consume()
        {
            var snapshot = new CharacterInputSnapshot(_direction, _dashPressed, _guardHeld, _pressedActions, _gait, _attackPresses);
            _attackPresses = default;
            _pressedActions = CharacterBehaviorId.None;
            _dashPressed = false;
            return snapshot;
        }

        /// <summary>清空全部输入，用于停用、失焦或暂停等生命周期边界。</summary>
        public void Clear()
        {
            _direction = Vector2.zero;
            _gait = CharacterMoveGait.Run;
            _dashPressed = false;
            _guardHeld = false;
            _attackPresses = default;
            _pressedActions = CharacterBehaviorId.None;
        }
    }

    /// <summary>世界 XZ 平面移动与冲刺配置，体力初值在初始化或显式重置时读取。</summary>
    [Serializable]
    public sealed class CharacterMovementSettings : CharacterLocomotionSettings
    {
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

        internal void ClearAttackBuffer() => attackBehavior?.ClearBuffer();

        public long FrameNumber { get; private set; }
        public float StaminaRemaining { get; private set; }
        public float DashCooldownRemaining { get; private set; }

        internal static CharacterBehaviorFlow ForCharacter(bool allowGuard, CharacterLightAttackSettings attack)
        {
            var rules = new List<CharacterBehaviorRule>();
            var interrupts = new List<CharacterBehaviorInterrupt>();
            if (allowGuard)
            {
                rules.Add(new CharacterBehaviorRule(CharacterBehaviorId.Guard, new CharacterGuardBehavior(),
                    conflicts: CharacterBehaviorId.Dash));
                interrupts.Add(new CharacterBehaviorInterrupt(CharacterBehaviorId.Dash, CharacterBehaviorId.Guard));
            }
            CharacterAttackBehavior combo = null;
            if (attack != null && (attack.Clip || attack.Combo?.LightTwo?.Clip || attack.Combo?.Heavy?.Clip ||
                attack.Combo?.AllowWithoutClip == true))
            {
                combo = new CharacterAttackBehavior(attack);
                rules.Add(new CharacterBehaviorRule(CharacterBehaviorId.Attack, combo,
                    priority: 80, conflicts: allowGuard ? CharacterBehaviorId.Guard : CharacterBehaviorId.None,
                    providesDisplacement: true));
                if (allowGuard)
                    interrupts.Add(new CharacterBehaviorInterrupt(CharacterBehaviorId.Attack, CharacterBehaviorId.Guard));
                interrupts.Add(new CharacterBehaviorInterrupt(CharacterBehaviorId.Attack, CharacterBehaviorId.Move));
                var phases = attack.DashInterruptPhases & CharacterBehaviorPhase.Any;
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
            float deltaTime, CharacterMovementSettings settings)
        {
            settings ??= _fallbackSettings;
            float dt = InputMath.NonNegative(deltaTime, 0f);
            float stamina = _initialized
                ? StaminaRemaining
                : InputMath.NonNegative(settings.InitialStamina, CharacterMovementSettings.DefaultInitialStamina);
            float cooldown = InputMath.Elapsed(DashCooldownRemaining, dt);
            var step = new CharacterBehaviorStep { Active = _active, OrdinarySpeedLimit = float.PositiveInfinity,
                StartSeconds = simulationSeconds, GrantTimes = grantTimes };

            if (dt == 0f)
            {
                step.Attack = attackBehavior?.State ?? default;
                return new CharacterBehaviorFrameResult(FrameNumber, Vector2.zero, false, false,
                    _dash.Direction, stamina, cooldown, step);
            }

            var context = new CharacterBehaviorContext(input, facing, dt, settings, stamina, cooldown);
            ReleaseBehaviorRequests(context, ref step);
            ResolveActionRequests(ref context, ref step);
            Vector2 displacement = ExecuteBehaviors(context, ref step, out bool moveApproved);
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
                if (!rule.Behavior.RequestsStart(context))
                    continue;

                CharacterBehaviorRejection rejection = CheckStart(rule, context, step.Started,
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

                StartBehavior(rule, context, ref step);
                float cooldown = rule.Id == CharacterBehaviorId.Dash ? _dash.Cooldown : context.DashCooldownRemaining;
                context = new CharacterBehaviorContext(context.Input, context.Facing, context.DeltaTime, context.Settings,
                    context.StaminaRemaining - cost, cooldown);
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
                && rule.ConflictsWith(movement)
                && rule.Behavior != attackBehavior) // 起手与普通移动并行，关键段在本步位移裁决中接管。
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
            foreach (CharacterBehaviorRule holder in _rules.Actions)
            {
                if (!IsActive(holder.Id) || !movement.ConflictsWith(holder)
                    || (holder.Behavior == attackBehavior && movementSeconds > 0f))
                    continue;

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

        private Vector2 ExecuteBehaviors(CharacterBehaviorContext context, ref CharacterBehaviorStep step, out bool moveApproved)
        {
            float movementSeconds = context.DeltaTime;
            Vector2 displacement = Vector2.zero;
            bool attackControlsMotion = false;
            if (attackBehavior?.IsActive == true)
            {
                displacement = attackBehavior.Update(context); // 先确定本步切招时刻，再裁决普通移动可用的时间。
                step.AttackMoveStarted = attackBehavior.MoveStarted;
                movementSeconds = attackBehavior.OrdinaryMovementSeconds;
                attackControlsMotion = attackBehavior.ControlsMotion;
                if (attackControlsMotion || displacement != Vector2.zero)
                    step.DisplacementProvider = CharacterBehaviorId.Attack;
                step.Motion = attackBehavior.StepMotion;
            }

            moveApproved = ResolveMovement(context, movementSeconds, ref step);
            step.Attack = attackBehavior?.ObserveMovementGrant(moveApproved) ?? default;
            step.Executed = _active;
            foreach (CharacterBehaviorRule rule in _rules.Actions)
            {
                if (!IsActive(rule.Id))
                    continue;

                step.OrdinarySpeedLimit = Math.Min(step.OrdinarySpeedLimit, rule.OrdinarySpeedLimit);
                if (rule.Behavior != attackBehavior)
                {
                    Vector2 request = rule.Behavior.Update(context);
                    if (rule.ProvidesDisplacement)
                    {
                        step.DisplacementProvider = rule.Id;
                        step.Motion = rule.Behavior.StepMotion;
                        displacement = request;
                    }
                }
                if (rule.Behavior.IsComplete)
                    EndBehavior(rule, ref step);
            }

            if (moveApproved)
            {
                if (!attackControlsMotion) step.DisplacementProvider = CharacterBehaviorId.Move;
                Vector2 movement = _movement.Update(context, movementSeconds);
                if (!float.IsPositiveInfinity(step.OrdinarySpeedLimit))
                    movement = Vector2.ClampMagnitude(movement, step.OrdinarySpeedLimit * movementSeconds);
                displacement += movement;
            }
            return displacement;
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
