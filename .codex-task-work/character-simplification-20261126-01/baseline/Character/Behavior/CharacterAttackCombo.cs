using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在攻击许可内统一裁决各状态的进入、接续与退出，将运动和时钟交给行为框架。
    internal sealed class CharacterAttackBehavior : CharacterBehavior, ICharacterIntervalMotion
    {
        private const float Tolerance = 0.000001f;
        private readonly CharacterAttackTable table;
        private readonly Dictionary<int, double> readyAt = new Dictionary<int, double>();
        private readonly HashSet<int> enteredAtInstant = new HashSet<int>();
        private readonly HashSet<int> processedTimeLinks = new HashSet<int>(); // 本状态实例已到达过阈值的路线下标。
        private double instant = double.NaN;
        private CharacterAttackExecution current;
        private int id;
        private string stateName, bufferResult;
        private long instance;
        private Vector2 blend;
        private float startProgress;
        private bool movingOpening, openingStep, moveStarted;
        private CharacterAttackPending pending;
        private CharacterAttackLink[] Links => table.Find(id)?.Links;
        internal CharacterAttackState State => current == null ? default :
            new CharacterAttackState(id, stateName, instance, current, startProgress, blend, pending, bufferResult);
        public override bool SharesMovementOnStart => true;
        public override bool DefersCompletionAtEndpoint => true;
        public override float InputEndpointTolerance => Tolerance;
        protected override object ExclusiveRuntimeStorage => readyAt;
        public override CharacterBehaviorPhase Phase => current?.Phase ?? CharacterBehaviorPhase.Active;
        public override bool IsComplete => current == null || current.IsComplete;
        public override bool AllowsOrdinaryMotionNow => current?.AllowsOrdinaryMotion ?? true;
        public override double NextBoundaryDelay => current == null ? double.PositiveInfinity :
            NextEndpoint(current.NextBoundary) - current.Elapsed;

        // 绑定当前角色的作者表或该预测流程自己的纯数据表。
        internal CharacterAttackBehavior(CharacterAttackTable table) => this.table = table;

        // 选定首招并保存报价，框架准入成功后直接使用同一个冻结目标。
        internal override CharacterBehaviorStart PrepareStart(CharacterBehaviorContext context, out CharacterBehaviorRejectReason reason)
        {
            reason = CharacterBehaviorRejectReason.StateCondition;
            if (IsActive) return null;
            var target = SelectOpening(context, out reason);
            return target.Id == 0 ? null : new CharacterAttackStart(context, GetStaminaCost(context), target);
        }

        // 切换失败已撤销状态所有权时，让外层流程在下一次裁决回收旧许可。
        public override bool ShouldContinue(CharacterBehaviorContext context) => current != null;

        // 接收已经获准的首招申请。
        internal override void StartPrepared(CharacterBehaviorStart request) => StartAttack(request.Context, ((CharacterAttackStart)request).Target);

        // 按本批按键的原始顺序解析首个可进入的攻击状态。
        private CharacterAttackTarget SelectOpening(CharacterBehaviorContext context, out CharacterBehaviorRejectReason reason)
        {
            reason = CharacterBehaviorRejectReason.NoRoute;
            var presses = context.Input.AttackPresses;
            for (int i = 0; i < presses.Count; i++)
            {
                var target = SelectInput(table.Openers, presses[i], context, true, out _, out _, out var failure);
                if (target.Id != 0) { reason = default; return target; }
                if (i == 0) reason = failure;
            }
            return default;
        }

        // 从首招进入时的实际地速判断本次整个连招的移动起手分类。
        private static bool IsMoving(CharacterBehaviorContext context) => context.GroundVelocity.sqrMagnitude > 0.0025f;

        // 使用目标编号、角色时刻和当前配置解析本次冻结候选。
        private CharacterAttackTarget Resolve(CharacterAttackLink link, CharacterBehaviorContext context, out CharacterBehaviorRejectReason reason)
        {
            var row = table.Find(link.To);
            reason = link.To == 0 ? CharacterBehaviorRejectReason.NoRoute : CharacterBehaviorRejectReason.InvalidConfiguration;
            if (row?.Definition == null) return default;
            reason = CheckAdmission(link.To, row.Definition, context);
            return reason == default ? new CharacterAttackTarget(row.Id, row.Name, link.BlendRange, row.Definition.Snapshot()) : default;
        }

        // 所有路径共用同一套定义、冷却及状态专属准入检查。
        private CharacterBehaviorRejectReason CheckAdmission(int target, CharacterAttackDefinition definition, CharacterBehaviorContext context)
        {
            if (!definition.IsUsable) return CharacterBehaviorRejectReason.InvalidConfiguration;
            if (readyAt.TryGetValue(target, out double ready) && ready > context.SimulationTime + Tolerance)
                return CharacterBehaviorRejectReason.Cooldown;
            return definition.CanEnter(At(context, 0d, 0f, true)) ? default : CharacterBehaviorRejectReason.StateCondition;
        }

        // 查询首招是否获得状态机准入。
        public override bool Evaluate(CharacterBehaviorContext context) => !IsActive && SelectOpening(context, out _).Id != 0;

        // 为直接调用者解析并启动本次首招。
        public override void Start(CharacterBehaviorContext context)
        {
            if (!IsActive) StartAttack(context, SelectOpening(context, out _));
        }

        // 打开一次攻击许可，锁定本次连招的首招分类。
        private void StartAttack(CharacterBehaviorContext context, CharacterAttackTarget target)
        {
            if (target.Id == 0) return;
            movingOpening = IsMoving(context);
            bufferResult = null;
            enteredAtInstant.Clear();
            Enter(target, context, true);
            base.Start(context);
            openingStep = true;
        }

        // 创建并进入目标实例，统一更新状态身份、动画交接和从进入时刻起算的冷却。
        private void Enter(CharacterAttackTarget target, CharacterBehaviorContext context, bool first)
        {
            var next = target.Definition.CreateExecution() ?? throw new InvalidOperationException($"攻击状态 {target.Id} 没有创建运行实例。");
            float progress;
            try
            {
                next.Enter(At(context, 0d, 0f, true), movingOpening, first ? InputMath.NonNegative(table.OpeningStepDistance, 0f) : (float?)null);
                progress = next.Progress;
            }
            catch (Exception failure)
            {
                // 尚未发布所有权，清理部分进入的新实例，同时保留原始进入异常及清理异常。
                try { next.Exit(CharacterBehaviorEndReason.Withdrawn, context.IsPrediction); }
                catch (Exception cleanup) { failure.Data["AttackEnterCleanup"] = cleanup; }
                throw;
            }
            processedTimeLinks.Clear();
            current = next;
            id = target.Id;
            stateName = target.Name;
            blend = target.BlendRange;
            instance++;
            startProgress = progress;
            moveStarted = true;
            BeginInstant(context.SimulationTime);
            enteredAtInstant.Add(id);
            float cooldown = InputMath.NonNegative(target.Definition.CooldownSeconds, 0f);
            if (cooldown > 0f) readyAt[id] = context.SimulationTime + cooldown;
        }

        // 同一角色时刻最多进入每个目标一次，避免零耗时自环及多状态环。
        private void BeginInstant(double at)
        {
            if (at == instant) return;
            instant = at;
            enteredAtInstant.Clear();
        }

        // 冻结状态自己的运动采样器，物理步只消费该采样器。
        public CharacterFrozenMotion FreezeMotion(in CharacterMotionCapture capture) => current == null
            ? CharacterReleasedAttackMotion.Instance : current.Freeze(instance, in capture);

        // 按列表优先级检查同键路线，允许不同时段或不同条件选择不同目标。
        private CharacterAttackTarget SelectInput(CharacterAttackLink[] links, ThreeC.Player.AttackButton button,
            CharacterBehaviorContext context, bool opener, out double at, out double expires, out CharacterBehaviorRejectReason reason)
        {
            at = expires = 0d;
            reason = CharacterBehaviorRejectReason.NoRoute;
            if (links == null || button == ThreeC.Player.AttackButton.None) return default;
            foreach (var link in links)
            {
                if (link.Trigger != CharacterAttackTrigger.Input || link.Button != button) continue;
                CharacterBehaviorRejectReason failure = default;
                if (!link.AllowsOpening(opener ? IsMoving(context) : movingOpening))
                    failure = CharacterBehaviorRejectReason.StateCondition;
                else if (!opener)
                {
                    if (!link.HasValidWindow) failure = CharacterBehaviorRejectReason.InvalidConfiguration;
                    else if (!(current.Duration > 0f))
                    {
                        at = expires = current.Elapsed;
                        float progress = current.Progress;
                        if (link.EarlySeconds > 0f) failure = CharacterBehaviorRejectReason.RequiresFixedDuration;
                        else if (!(progress >= link.LinkStart && progress <= link.LinkEnd)) failure = CharacterBehaviorRejectReason.OutsideWindow;
                    }
                    else
                    {
                        double open = link.LinkStart * current.Duration;
                        expires = link.LinkEnd * current.Duration;
                        at = Math.Max(open, current.Elapsed);
                        if (current.Elapsed + Tolerance < Math.Max(0d, open - link.EarlySeconds) || current.Elapsed > expires + Tolerance)
                            failure = CharacterBehaviorRejectReason.OutsideWindow;
                    }
                }
                if (failure == default)
                {
                    var target = Resolve(link, opener ? context : At(context, at - current.Elapsed, 0f), out failure);
                    if (target.Id != 0) { reason = default; return target; }
                }
                if (reason == CharacterBehaviorRejectReason.NoRoute) reason = failure;
            }
            return default;
        }

        // 按键窗口合格且目标可准入时才覆盖一个预输入槽。
        private void ReadLinks(CharacterBehaviorContext context)
        {
            var presses = context.Input.AttackPresses;
            if (presses.Count == 0) return;
            var links = Links;
            for (int i = 0; i < presses.Count; i++)
            {
                long request = Journal?.NewRequest() ?? 0;
                var target = SelectInput(links, presses[i], context, false, out double at, out double expires, out var reason);
                if (reason != default)
                {
                    bufferResult = reason switch {
                        CharacterBehaviorRejectReason.NoRoute => "未覆盖：当前状态无此路线",
                        CharacterBehaviorRejectReason.OutsideWindow => "未覆盖：窗口外",
                        CharacterBehaviorRejectReason.Cooldown => "未覆盖：接续时仍在冷却",
                        CharacterBehaviorRejectReason.StateCondition => "未覆盖：状态条件不满足",
                        CharacterBehaviorRejectReason.RequiresFixedDuration => "未覆盖：此状态无固定时长，提前输入须设置为 0 秒",
                        _ => "未覆盖：路线或目标配置无效"
                    };
                    Journal?.Record(RegisteredId, request, Execution.Instance, CharacterBehaviorRequestStatus.Rejected, reason);
                    continue;
                }
                bufferResult = pending.Target.Id == 0 ? "已预输入" : "后一次输入已覆盖";
                CloseBuffer(CharacterBufferEvent.Overwritten);
                pending = new CharacterAttackPending { Request = request, Target = target, At = at, Expires = expires };
                Journal?.Record(RegisteredId, request, Execution.Instance, CharacterBehaviorRequestStatus.Buffered, buffer: CharacterBufferEvent.Buffered);
            }
        }

        // 释放尚未执行的预输入并记录其终止事件。
        private void CloseBuffer(CharacterBufferEvent change, CharacterBehaviorRejectReason reason = default, double? at = null)
        {
            if (pending.Target.Id == 0) return;
            Journal?.Record(RegisteredId, pending.Request, Execution.Instance, CharacterBehaviorRequestStatus.Rejected, reason, buffer: change, at: at);
            pending = default;
        }

        // 在状态报告的下个时刻与未到达的输入、时间路线之间取最近的边界。
        private double NextEndpoint(double next)
        {
            if (pending.Target.Id != 0 && pending.At > current.Elapsed) next = Math.Min(next, pending.At);
            var links = Links;
            if (links == null || !(current.Duration > 0f)) return next;
            for (int i = 0; i < links.Length; i++)
            {
                var link = links[i];
                if (link.Trigger != CharacterAttackTrigger.Time || processedTimeLinks.Contains(i) ||
                    !link.HasValidTime || !link.AllowsOpening(movingOpening)) continue;
                double time = link.LinkStart * current.Duration;
                if (time > current.Elapsed) next = Math.Min(next, time);
            }
            return next;
        }

        // 依次尝试已到时的预输入、专属状态的目标请求及按表顺序匹配的自动转移。
        private bool TrySwitch(CharacterBehaviorContext context)
        {
            BeginInstant(context.SimulationTime);
            if (pending.Target.Id != 0 && pending.At <= current.Elapsed + Tolerance)
            {
                var reason = current.Elapsed > pending.Expires + Tolerance ? CharacterBehaviorRejectReason.OutsideWindow :
                    CheckAdmission(pending.Target.Id, pending.Target.Definition, context);
                if (reason == default)
                {
                    var accepted = pending;
                    pending = default;
                    Switch(accepted.Target, context, accepted.Request, CharacterBufferEvent.Executed);
                    return true;
                }
                CloseBuffer(CharacterBufferEvent.Expired, reason, context.SimulationTime);
            }
            int requested = current.RequestedTarget;
            if (requested != 0 && TryAutomatic(CharacterAttackLink.ToState(requested), context)) return true;
            var links = Links;
            if (links == null) return false;
            for (int i = 0; i < links.Length; i++)
            {
                var link = links[i];
                if (!link.AllowsOpening(movingOpening)) continue;
                bool matches = link.Trigger switch {
                    CharacterAttackTrigger.Completed => current.IsComplete,
                    CharacterAttackTrigger.Time => ConsumeTimeLink(i, link),
                    CharacterAttackTrigger.Result => link.Result != 0 && current.Result == link.Result,
                    _ => false
                };
                if (matches && TryAutomatic(link, context)) return true;
            }
            return false;
        }

        // 阈值首次到达时先消费该定时路线；冷却、专属条件或防环检查拒绝后均不再重试。
        private bool ConsumeTimeLink(int index, CharacterAttackLink link)
        {
            if (!link.HasValidTime || processedTimeLinks.Contains(index)) return false;
            bool reached = current.Progress >= link.LinkStart ||
                current.Duration > 0f && current.Elapsed >= link.LinkStart * current.Duration;
            return reached && processedTimeLinks.Add(index);
        }

        // 自动与动态意图经过同一目标查找和准入后才获得新的执行实例。
        private bool TryAutomatic(CharacterAttackLink link, CharacterBehaviorContext context)
        {
            if (enteredAtInstant.Contains(link.To)) return false;
            var target = Resolve(link, context, out _);
            if (target.Id == 0) return false;
            Switch(target, context, Journal?.NewRequest() ?? 0, default);
            return true;
        }

        // 交接时先终结旧实例并清理其缓存，再进入新状态并发布对应执行号。
        private void Switch(CharacterAttackTarget target, CharacterBehaviorContext context, long request, CharacterBufferEvent buffer)
        {
            CloseBuffer(CharacterBufferEvent.Cancelled, at: context.SimulationTime);
            Journal?.End(this, CharacterBehaviorEndReason.Completed);
            try
            {
                ExitCurrent(CharacterBehaviorEndReason.Completed, context.IsPrediction);
                Enter(target, context, false);
                Journal?.Start(this, request);
            }
            catch (Exception failure)
            {
                IsActive = false;
                // 登记新实例时还会读取其阶段；登记失败也需释放已成功进入的目标。
                try { ExitCurrent(CharacterBehaviorEndReason.Withdrawn, context.IsPrediction); }
                catch (Exception cleanup) { failure.Data["AttackSwitchCleanup"] = cleanup; }
                Journal?.Record(RegisteredId, request, Execution.Instance, CharacterBehaviorRequestStatus.Rejected,
                    CharacterBehaviorRejectReason.InvalidConfiguration, buffer: buffer == CharacterBufferEvent.Executed
                        ? CharacterBufferEvent.Cancelled : default, at: context.SimulationTime);
                throw;
            }
            Journal?.Record(RegisteredId, request, Execution.Instance, CharacterBehaviorRequestStatus.Started, buffer: buffer, at: context.SimulationTime);
        }

        // 产生子区间的准确时间；已经消耗的离散输入不再交付给下一时段。
        private static CharacterBehaviorContext At(CharacterBehaviorContext context, double offset, float seconds, bool heldOnly = false)
        {
            var input = context.Input;
            if (offset > 0d || heldOnly) input = new CharacterInputSnapshot(input.Direction, false, input.GuardHeld, default, input.Gait);
            return new CharacterBehaviorContext(input, context.Facing, seconds, context.Settings, context.StaminaRemaining,
                context.DashCooldownRemaining, context.SimulationTime + offset, context.GroundVelocity, context.SeparateBoundaries, context.IsPrediction);
        }

        // 兼容普通位移调用入口，仍由同一状态机推进。
        public override Vector2 Update(CharacterBehaviorContext context) => AdvanceStep(context).Displacement;

        // 逐帧时间线在零长端点切换，整步调用则逐段消耗输入、自动转移和自然结束后的余时。
        public override CharacterBehaviorMotion AdvanceStep(CharacterBehaviorContext context)
        {
            if (current == null) return new CharacterBehaviorMotion(Vector2.zero, context.DeltaTime, controlsDisplacement: false);
            moveStarted = openingStep;
            startProgress = current.Progress;
            if (openingStep) context = At(context, 0d, context.DeltaTime, true);
            else ReadLinks(context);
            openingStep = false;
            if (context.SeparateBoundaries && context.DeltaTime > 0f)
                return AdvanceSegment(context, context.DeltaTime, NextEndpoint(current.NextBoundary));

            var result = current.Advance(At(context, 0d, 0f), current.Elapsed);
            double offset = 0d;
            while (true)
            {
                var endpoint = At(context, offset, 0f, true);
                while (TrySwitch(endpoint))
                    result = Combine(result, current.Advance(endpoint, current.Elapsed));
                double remaining = context.DeltaTime - offset;
                if (!(remaining > 0d)) return result;
                if (current.IsComplete)
                    return new CharacterBehaviorMotion(result.Displacement, result.OrdinaryMovementSeconds + (float)remaining,
                        result.ControlsDisplacement, result.Motion);
                double next = NextEndpoint(current.NextDecision);
                double seconds = Math.Min(remaining, next - current.Elapsed);
                if (!(seconds > 0d) || !(offset + seconds > offset))
                    throw new InvalidOperationException($"攻击状态 {id} 在 {current.Elapsed} 秒没有可推进的下一边界。");
                result = Combine(result, AdvanceSegment(At(context, offset, (float)seconds, true), seconds, next));
                offset += seconds;
            }
        }

        // 按相同浮点步长到达的边界校正时钟，避免边界舍入阻断下一次裁决。
        private CharacterBehaviorMotion AdvanceSegment(CharacterBehaviorContext context, double seconds, double boundary)
        {
            double rawEnd = current.Elapsed + seconds;
            double end = rawEnd;
            if (seconds >= (float)(boundary - current.Elapsed)) end = Math.Max(end, boundary);
            if (current.Duration > 0f && end > current.Duration && boundary == current.Duration) end = boundary;
            var motion = current.Advance(context, end);
            return rawEnd > end ? new CharacterBehaviorMotion(motion.Displacement, motion.OrdinaryMovementSeconds + (float)(rawEnd - end),
                motion.ControlsDisplacement, motion.Motion) : motion;
        }

        // 汇总各状态的位移和交还时间，运动控制与动画时钟属于最后执行的状态。
        private static CharacterBehaviorMotion Combine(CharacterBehaviorMotion before, CharacterBehaviorMotion after) =>
            new CharacterBehaviorMotion(before.Displacement + after.Displacement, before.OrdinaryMovementSeconds + after.OrdinaryMovementSeconds,
                after.ControlsDisplacement, after.Motion);

        // 将实际获准的移动交还时刻及当前攻击实例写入行为结果。
        internal override void RecordOutcome(bool moveApproved, ref CharacterBehaviorStep step)
        {
            if (current == null) return;
            current.RecordMovement(moveApproved, startProgress);
            ReadOutcome(ref step);
            step.AttackMoveStarted = moveStarted;
        }

        // 发布当前状态的冻结观测和缓存信息。
        internal override void ReadOutcome(ref CharacterBehaviorStep step) => step.Attack = State;

        // 将活动实例、缓存、冷却、定时路线消费记录及零时刻防环状态独立复制到预测流程。
        protected override void CopyRuntimeStateTo(CharacterBehavior target)
        {
            var copy = (CharacterAttackBehavior)target;
            copy.current = current?.CopyForPrediction();
            if (current != null && (copy.current == null || ReferenceEquals(current, copy.current)))
                throw new InvalidOperationException($"攻击状态 {id} 必须提供独立的预测运行实例。");
            copy.id = id;
            copy.stateName = stateName;
            copy.instance = instance;
            copy.blend = blend;
            copy.startProgress = startProgress;
            copy.openingStep = openingStep;
            copy.movingOpening = movingOpening;
            copy.moveStarted = moveStarted;
            copy.bufferResult = bufferResult;
            copy.pending = pending;
            copy.pending.Target = pending.Target.Snapshot();
            copy.readyAt.Clear();
            foreach (var pair in readyAt) copy.readyAt.Add(pair.Key, pair.Value);
            copy.instant = instant;
            copy.enteredAtInstant.Clear();
            copy.enteredAtInstant.UnionWith(enteredAtInstant);
            copy.processedTimeLinks.Clear();
            copy.processedTimeLinks.UnionWith(processedTimeLinks);
        }

        // 输入连续性中断时只撤销未执行的接续。
        protected override void OnInputContinuityEnded() => CloseBuffer(CharacterBufferEvent.Cancelled);

        // 先解除当前实例的所有权，再交付退出回调，后续恢复和外层撤销均无法重复退出。
        private void ExitCurrent(CharacterBehaviorEndReason reason, bool isPrediction)
        {
            var previous = current;
            current = null;
            id = 0;
            stateName = null;
            openingStep = moveStarted = false;
            processedTimeLinks.Clear();
            previous?.Exit(reason, isPrediction);
        }

        // 在自然结束、外部接管或被打断时退出活动状态并保留跨许可冷却。
        public override void End()
        {
            CloseBuffer(CharacterBufferEvent.Cancelled);
            try { ExitCurrent(Execution.EndReason, IsPrediction); }
            finally { base.End(); }
        }

        // 角色重置时释放本代缓存和冷却，招式实例序号继续单调增长。
        internal override void Reset()
        {
            base.Reset();
            readyAt.Clear();
            enteredAtInstant.Clear();
            instant = double.NaN;
            pending = default;
            bufferResult = null;
        }
    }
}
