using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在同一角色时间轴协调行为准入、执行、结束和结果发布。</summary>
    public sealed class CharacterBehaviorFlow
    {
        private readonly CharacterMovementSettings fallbackSettings = new CharacterMovementSettings();
        private readonly CharacterBehaviorRules rules;
        private readonly CharacterBehaviorRuntimeState runtime;
        private readonly CharacterBehaviorLocomotion locomotion;
        private CharacterBehaviorId active;
        private double simulationSeconds;
        private Dictionary<CharacterBehaviorId, double> grantTimes;
        private readonly CharacterBehaviorJournal journal;
        private bool forecasting;
        internal bool AllowsStationaryForecast { get; }
        internal bool IsDispatching { get; private set; }
        internal bool DispatchCancelled => journal.DispatchCancelled;

        // 回调内排入新生命周期时撤销旧批次，当前已提交的原子动作仍保留。
        internal void CancelCurrentDispatch() => journal.DispatchCancelled = true;

        // 宿主在回调完成、准备处理延期请求时放行新批次。
        internal void ClearDispatchCancellation()
        {
            if (IsDispatching) throw new InvalidOperationException("行为回调尚未返回。");
            journal.DispatchCancelled = false;
        }

        // 禁止扩展回调嵌套修改同一流程，作用域在异常时也会释放占用。
        private DispatchScope EnterDispatch()
        {
            if (IsDispatching || DispatchCancelled) throw new InvalidOperationException("行为流程正在裁决或等待生命周期切换。");
            IsDispatching = true;
            return new DispatchScope(this);
        }

        // 保存本次分发的拥有者，退出作用域后恢复宿主的生命周期入口。
        private readonly struct DispatchScope : IDisposable
        {
            private readonly CharacterBehaviorFlow flow;
            // 记录本次已取得分发权的行为流程。
            internal DispatchScope(CharacterBehaviorFlow flow) => this.flow = flow;
            // 释放分发权，取消标志留给宿主处理延期操作。
            public void Dispose() => flow.IsDispatching = false;
        }
        public bool HistoryEnabled { get => journal.HistoryEnabled; set => journal.HistoryEnabled = value; }
        public IReadOnlyList<CharacterBehaviorDecision> DecisionHistory => journal.History;
        private bool unavailableForCopy;

        // 当前端点裁决后查询全部活动参与者，截取下一段可推进的角色时间。
        internal double NextBoundary(double time, double limit, bool inputAtLimit = false)
        {
            using var dispatch = EnterDispatch();
            double next = limit;
            foreach (var rule in rules.Participants)
            {
                if (DispatchCancelled) return limit;
                if ((active & rule.Id) == 0) continue;
                double delay = rule.Behavior.NextBoundaryDelay;
                if (DispatchCancelled) return limit;
                double boundary = time + delay;
                if (!(delay > 0d) || !(boundary > time))
                    throw new InvalidOperationException($"行为 {rule.Id} 在端点裁决后没有可推进的边界，余时为 {delay} 秒。");

                // 在动作自身的时间容差内，把离散输入留给该端点的首次裁决。
                if (inputAtLimit && boundary < limit && limit - boundary <= rule.Behavior.InputEndpointTolerance)
                    continue;
                next = Math.Min(next, boundary);
            }

            float seconds = (float)(next - time);
            if (!(seconds > 0f) || float.IsInfinity(seconds))
                throw new InvalidOperationException($"角色时间段无法从 {time} 推进到 {next}。");
            return next;
        }

        // 运动采样撤销了已裁决的一帧时，把已发生的请求与执行边沿留给下个有效边界。
        internal void RetainUnpublished(in CharacterBehaviorFrameResult frame) => journal.RetainUnpublished(frame);

        // 在裁决后捕获活动位移动作的精确采样器和持续输入，供之后的物理区间读取。
        internal CharacterMotionCommand CaptureMotion(double time, CharacterBehaviorFrameResult result,
            CharacterInputSnapshot input, Vector2 facing, Vector2 velocity, CharacterMovementSettings settings)
        {
            using var dispatch = EnterDispatch();
            settings ??= fallbackSettings;
            foreach (var rule in rules.Actions)
            {
                if (!rule.ProvidesDisplacement || (active & rule.Id) == 0) continue;
                var interval = rule.Behavior as ICharacterIntervalMotion;
                if (interval == null)
                    return new CharacterMotionCommand(time, result, input, settings, rule.Id, null);

                float turnSpeed = InputMath.NonNegative(settings.MoveTurnSpeed, CharacterMovementSettings.DefaultMoveTurnSpeed);
                var capture = new CharacterMotionCapture(time, facing, velocity, input, turnSpeed);
                var motion = interval.FreezeMotion(capture);
                if (DispatchCancelled) return default;
                if (motion == null)
                    throw new InvalidOperationException($"行为 {rule.Id} 已声明精确区间运动，须提供冻结采样器。");
                return new CharacterMotionCommand(time, result, input, settings, rule.Id, motion);
            }
            return new CharacterMotionCommand(time, result, input, settings, CharacterBehaviorId.None, null);
        }

        // 检查实际执行的位移动作是否需要用本段总量兼容写入运动命令。
        internal bool HasCustomMotion(in CharacterBehaviorFrameResult result)
        {
            foreach (var rule in rules.Actions)
                if (rule.ProvidesDisplacement && (result.ExecutedBehaviors & rule.Id) != 0 &&
                    !(rule.Behavior is ICharacterIntervalMotion)) return true;
            return false;
        }

        public long FrameNumber { get; private set; }
        public CharacterBehaviorFrameResult LastResetResult { get; private set; }
        public float StaminaRemaining => runtime.StaminaRemaining;
        public float DashCooldownRemaining => runtime.DashCooldownRemaining;

        // 兼容已有调用者，角色方案由装配层创建。
        internal static CharacterBehaviorFlow ForCharacter(CharacterBehaviorRegistration registration, bool snapshot = false) =>
            CharacterBehaviorAssembly.ForCharacter(registration, snapshot);

        /// <summary>创建使用默认角色规则的行为流程。</summary>
        public CharacterBehaviorFlow() : this((CharacterBehaviorRule[])null)
        {
        }

        /// <summary>追加行为规则。内置普通移动与冲刺保留，额外规则需要提供独立的行为实例。</summary>
        public CharacterBehaviorFlow(CharacterBehaviorRule[] additionalRules, CharacterBehaviorInterrupt[] interrupts = null)
            : this(new CharacterBehaviorAssembly(additionalRules, interrupts), false) { }

        // 绑定独立装配和诊断；预演与预测模式从首次准入起就禁用真实通知。
        internal CharacterBehaviorFlow(CharacterBehaviorAssembly assembly, bool snapshot)
        {
            rules = assembly.Rules;
            AllowsStationaryForecast = true;
            foreach (var rule in rules.Actions)
                if (!(rule.Id == CharacterBehaviorId.Dash && rule.Behavior is CharacterDashBehavior ||
                    rule.Id == CharacterBehaviorId.Guard && rule.Behavior is CharacterGuardBehavior ||
                    rule.Id == CharacterBehaviorId.Attack && rule.Behavior is CharacterAttackBehavior))
                    AllowsStationaryForecast = false;
            runtime = assembly.RuntimeState;
            locomotion = assembly.Locomotion;
            forecasting = runtime.IsPrediction = snapshot;
            journal = new CharacterBehaviorJournal(rules.Participants, snapshot);
        }

        // 查询当前攻击行为及其保留的待执行招式能否共享冻结预测表。
        internal bool SupportsSharedForecastAttack
        {
            get
            {
                foreach (var rule in rules.Actions)
                {
                    if (rule.Behavior is CharacterAttackBehavior attack && !attack.SupportsSharedForecastTable)
                        return false;
                }
                return true;
            }
        }

        // 预检整组行为后复制流程与动作状态，并让目标绑定自己的预测定义。
        internal void CopyStateTo(CharacterBehaviorFlow copy)
        {
            if (copy == null || ReferenceEquals(this, copy))
                throw new InvalidOperationException("需要独立的预测副本。");
            if (unavailableForCopy || copy.unavailableForCopy)
                throw new InvalidOperationException("复制尚未完成的预测副本不可复用。");

            using var sourceDispatch = EnterDispatch();
            using var targetDispatch = copy.EnterDispatch();
            rules.ValidateCopyTarget(copy.rules);
            copy.unavailableForCopy = true; // 私有复制或重绑失败时，此实例留在作废状态。
            copy.runtime.IsPrediction = copy.forecasting = true;
            journal.CopyTo(copy.journal);
            copy.active = active;
            copy.simulationSeconds = simulationSeconds;
            copy.grantTimes = null; // 预测不发布许可时刻。
            copy.FrameNumber = FrameNumber;
            runtime.CopyStateTo(copy.runtime);
            locomotion.CopyStateTo(copy.locomotion);
            rules.CopyParticipantStatesTo(copy.rules);
            copy.rules.BindPredictionDefinitions();
            copy.unavailableForCopy = false;
        }

        // 预测副本收到已打断通知后释放已有活动行为和遗留输入。
        internal void ReleasePredictionActions()
        {
            if (!forecasting) throw new InvalidOperationException("需要独立的预测副本");
            InterruptAll(CharacterBehaviorEndReason.ExternalControl, fallbackSettings);
        }

        // 撤销整组动作与连招缓存，保留角色时间、体力、冲刺及招式冷却。
        public CharacterBehaviorFrameResult InterruptAll(CharacterBehaviorEndReason reason, CharacterMovementSettings settings)
        {
            using var dispatch = EnterDispatch();
            return InterruptCore(reason, settings);
        }

        // 当前分发内依次撤销行为，回调要求改换生命周期后停止撤销后续实例。
        private CharacterBehaviorFrameResult InterruptCore(CharacterBehaviorEndReason reason, CharacterMovementSettings settings)
        {
            if (reason == CharacterBehaviorEndReason.None) throw new ArgumentOutOfRangeException(nameof(reason));
            var step = new CharacterBehaviorStep { Active = active, StartSeconds = simulationSeconds, GrantTimes = grantTimes };
            journal.Time = simulationSeconds;
            locomotion.BeginStep(0f, ref step);
            locomotion.Reset();
            try
            {
                foreach (var rule in rules.Participants)
                {
                    if (DispatchCancelled) break;
                    if (step.IsActive(rule.Id)) step.End(rule, reason);
                }
            }
            finally { active = step.Active; }
            step.Interrupted = step.Ended;
            EndContinuityCore();
            var context = runtime.CreateContext(default, default, 0f, settings ?? fallbackSettings, simulationSeconds, default);
            runtime.ReadOutcome(context, ref step);
            if (!DispatchCancelled) journal.Commit(ref step);
            return new CharacterBehaviorFrameResult(++FrameNumber, default, step);
        }

        // 仅推进外部控制占用的角色时间与跨活动期计时，不重新申请动作。
        internal void AdvanceExternal(double seconds)
        {
            using var dispatch = EnterDispatch();
            if (!(seconds > 0d)) return;
            // 本段所有计时属于同一次时钟提交，随后由时间线取消剩余区间。
            foreach (var rule in rules.Participants) rule.Behavior.AfterStep((float)seconds);
            simulationSeconds += seconds;
            journal.Time = simulationSeconds;
        }

        // 把输入连续性结束通知发给每个已注册的行为。
        internal void EndInputContinuity()
        {
            using var dispatch = EnterDispatch();
            EndContinuityCore();
        }

        // 当前分发内撤销输入缓存，切换生命周期时让新边界接手剩余清理。
        private void EndContinuityCore()
        {
            foreach (var rule in rules.Participants)
            {
                if (DispatchCancelled) break;
                rule.Behavior.HeldRequest = false;
                rule.Behavior.EndInputContinuity();
            }
            journal.CommitPendingHistory();
        }

        /// <summary>结束全部行为并按配置恢复运行状态，供角色创建或重生时调用。</summary>
        public void Reset(CharacterMovementSettings settings = null)
        {
            using var dispatch = EnterDispatch();
            LastResetResult = active == default && !journal.HasPending ? default : InterruptCore(CharacterBehaviorEndReason.Reset, settings);
            locomotion.Reset();
            foreach (var rule in rules.Participants)
            {
                try
                {
                    rule.Behavior.Reset();
                }
                finally
                {
                    if (!rule.Behavior.IsActive) active &= ~rule.Id;
                }
            }
            journal.Reset();
            runtime.Reset(settings ?? fallbackSettings);
            active = default;
            FrameNumber = 0;
            simulationSeconds = 0d;
            grantTimes = null;
        }

        /// <summary>用提交的输入推进一个固定步，返回本步位移和执行结果。</summary>
        public CharacterBehaviorFrameResult Tick(CharacterInputSnapshot input, Vector2 facing,
            float deltaTime, CharacterMovementSettings settings) => Tick(input, facing, deltaTime, settings, default);

        /// <summary>携带当前实际地速推进行为，只有有限正步长会改变流程状态。</summary>
        public CharacterBehaviorFrameResult Tick(CharacterInputSnapshot input, Vector2 facing, float deltaTime,
            CharacterMovementSettings settings, Vector2 groundVelocity)
        {
            float dt = InputMath.NonNegative(deltaTime, 0f);
            return dt == 0f ? ReadCurrentState(input, facing, settings, groundVelocity) :
                Run(input, facing, dt, settings, groundVelocity, false);
        }

        // 逐帧入口按事件及动作边界精确分段；零长片段也可在该时刻裁决一次按下。
        internal CharacterBehaviorFrameResult AdvanceTimeline(CharacterInputSnapshot input, Vector2 facing, float deltaTime,
            CharacterMovementSettings settings, Vector2 groundVelocity) =>
            Run(input, facing, InputMath.NonNegative(deltaTime, 0f), settings, groundVelocity, true);

        // 使用同一套规则推进显式步进和时间线区间，由时间线处理端点分段。
        private CharacterBehaviorFrameResult Run(CharacterInputSnapshot input, Vector2 facing, float dt,
            CharacterMovementSettings settings, Vector2 groundVelocity, bool separateBoundaries)
        {
            using var dispatch = EnterDispatch();
            settings ??= fallbackSettings;
            var step = CreateStep();

            runtime.BeginStep(settings);
            journal.Time = simulationSeconds;
            var context = runtime.CreateContext(input, facing, dt, settings, simulationSeconds, groundVelocity, separateBoundaries);
            Vector2 displacement;
            try
            {
                displacement = AdvanceStep(ref context, ref step);
            }
            finally
            {
                // 已提交的许可、资源和行为生命周期在后续回调出错时仍须保持一致。
                active = step.Active;
                grantTimes = step.GrantTimes;
            }
            if (step.DurationCommitted && dt > 0f)
                foreach (var rule in rules.Participants)
                    rule.Behavior.AfterStep(dt);
            runtime.ReadOutcome(context, ref step);
            if (step.DurationCommitted) simulationSeconds += dt;
            journal.Time = simulationSeconds;
            if (!DispatchCancelled) journal.Commit(ref step);
            return new CharacterBehaviorFrameResult(++FrameNumber, displacement, step);
        }

        // 依序推进准入、活动行为、位移合成及结束回调。
        private Vector2 AdvanceStep(ref CharacterBehaviorContext context, ref CharacterBehaviorStep step)
        {
            var movement = locomotion.BeginStep(context.DeltaTime, ref step);
            locomotion.ReadRequest(context);
            bool attackContinued = ReleaseBeforeRequests(context, ref step);
            if (!DispatchCancelled) ResolveRequests(ref context, ref step, attackContinued);
            if (DispatchCancelled) return default;
            // 从首个运动回调开始，同段推进不可拆分，所有已活动行为共用完整时长。
            step.DurationCommitted = true;
            foreach (var rule in rules.Actions)
                if (step.IsActive(rule.Id))
                    locomotion.Combine(rule, rule.Behavior.AdvanceStep(context), ref movement, ref step);
            Vector2 displacement = DispatchCancelled ? default : locomotion.Resolve(context, rules, movement, ref step);

            // 发布本步真正执行的结果，然后才释放已完成的参与者。
            step.Executed = step.Active;
            foreach (var rule in rules.Actions)
                if (step.IsActive(rule.Id))
                    rule.Behavior.RecordOutcome(step.OrdinaryMovementApproved, ref step);
            journal.Time = simulationSeconds + context.DeltaTime;
            FinishCompleted(context.SeparateBoundaries && context.DeltaTime > 0f, ref step);
            return displacement;
        }

        // 从现有活动许可和角色时钟建立本次推进或观测的起点。
        private CharacterBehaviorStep CreateStep() => new CharacterBehaviorStep { Active = active, StartSeconds = simulationSeconds,
            GrantTimes = grantTimes, SkipGrantHistory = forecasting };

        // 在分发保护下读取当前行为帧，保持角色时钟、帧号与许可状态。
        internal CharacterBehaviorFrameResult ReadCurrentState(CharacterInputSnapshot input, Vector2 facing,
            CharacterMovementSettings settings, Vector2 groundVelocity)
        {
            using var dispatch = EnterDispatch();
            var step = CreateStep();
            var context = runtime.CreateContext(input, facing, 0f, settings ?? fallbackSettings, simulationSeconds, groundVelocity);
            locomotion.ReadOutcome(ref step);
            foreach (var rule in rules.Participants)
                rule.Behavior.ReadOutcome(ref step);
            runtime.ReadOutcome(context, ref step);
            journal.ReadExecutions(ref step);
            return new CharacterBehaviorFrameResult(FrameNumber, Vector2.zero, step);
        }

        // 新请求仲裁前释放已撤销的许可，并让已到自然终点的招式优先尝试自身接续。
        private bool ReleaseBeforeRequests(CharacterBehaviorContext context, ref CharacterBehaviorStep step)
        {
            bool attackContinued = false;
            foreach (var rule in rules.Actions)
            {
                if (DispatchCancelled) return false;
                if (!step.IsActive(rule.Id)) continue;
                bool keep = rule.Behavior.ShouldContinue(context);
                if (DispatchCancelled) return false;
                if (!keep)
                {
                    step.Withdrawn |= rule.Id;
                    step.End(rule, CharacterBehaviorEndReason.Withdrawn);
                }
                else if (rule.Behavior is CharacterAttackBehavior attack && attack.IsComplete)
                {
                    attackContinued = attack.TryContinueAtEndpoint(context);
                    if (attackContinued || DispatchCancelled) continue;
                    // 先冻结旧招末相位；若同刻有新首招，本步执行结果将发布新实例。
                    attack.ReadOutcome(ref step);
                    step.End(rule);
                }
            }
            return attackContinued;
        }

        // 按已装配的优先顺序判断请求，所有检查通过后再提交行为变更。
        private void ResolveRequests(ref CharacterBehaviorContext context, ref CharacterBehaviorStep step, bool attackContinued)
        {
            ulong missing = (ulong)(context.Input.Requests & ~rules.Registered);
            while (missing != 0)
            {
                ulong rest = missing & (missing - 1);
                journal.Record((CharacterBehaviorId)(missing ^ rest), journal.NewRequest(), 0, CharacterBehaviorRequestStatus.Rejected,
                    CharacterBehaviorRejectReason.InvalidConfiguration);
                missing = rest;
            }
            foreach (var rule in rules.Actions)
            {
                if (DispatchCancelled) return;
                var behavior = rule.Behavior;
                // 同批突进可能刚打断已接续的新招；已用作接续的攻击键不能再次申请首招。
                if (attackContinued && behavior is CharacterAttackBehavior) continue;
                bool explicitRequest = (context.Input.Requests & rule.Id) != 0;
                bool continuedHold = rule.Id == CharacterBehaviorId.Guard && explicitRequest && behavior.HeldRequest;
                behavior.HeldRequest = explicitRequest;
                if (step.IsActive(rule.Id))
                {
                    if (explicitRequest && !(behavior is CharacterAttackBehavior))
                    {
                        step.HeldRequests |= rule.Id;
                        if (!continuedHold)
                            journal.Record(rule.Id, journal.NewRequest(), behavior.Execution.Instance, CharacterBehaviorRequestStatus.AlreadyActive);
                    }
                    continue;
                }
                var request = behavior.Quote(context, out var reason);
                if (DispatchCancelled) return;
                if (request == null && !explicitRequest) continue;
                long requestId = journal.NewRequest();
                CharacterBehaviorId blocker = default;
                CharacterBehaviorId interruptions = default;
                bool approved = request != null && rules.TryApprove(rule, locomotion.HasExistingPermit(context, step), step,
                    out interruptions, out reason, out blocker);
                if (DispatchCancelled) return;
                if (!approved)
                {
                    journal.Record(rule.Id, requestId, 0, CharacterBehaviorRequestStatus.Rejected, reason, blocker);
                    continue;
                }
                ApplyInterruptions(interruptions, ref step);
                if (DispatchCancelled) return;
                step.Start(rule, request);
                context = runtime.CommitStart(request, context);
                journal.Start(behavior, requestId);
                journal.Record(rule.Id, requestId, behavior.Execution.Instance, CharacterBehaviorRequestStatus.Started);
            }
        }

        // 释放本次申请已经获准打断的参与者。
        private void ApplyInterruptions(CharacterBehaviorId interruptions, ref CharacterBehaviorStep step)
        {
            if (interruptions == default) return;
            foreach (var rule in rules.Participants)
            {
                if (DispatchCancelled) return;
                if ((interruptions & rule.Id) == default) continue;
                step.End(rule, CharacterBehaviorEndReason.Interrupted);
                step.Interrupted |= rule.Id;
            }
        }

        // 执行结果记录完成后，释放本步自然结束的参与者。
        private void FinishCompleted(bool atPositiveEndpoint, ref CharacterBehaviorStep step)
        {
            foreach (var rule in rules.Actions)
                // 需处理端点输入的动作在下一次零长裁决后才释放。
                if (step.IsActive(rule.Id) && rule.Behavior.IsComplete &&
                    !(atPositiveEndpoint && rule.Behavior.DefersCompletionAtEndpoint))
                    step.End(rule);
        }
    }
}
