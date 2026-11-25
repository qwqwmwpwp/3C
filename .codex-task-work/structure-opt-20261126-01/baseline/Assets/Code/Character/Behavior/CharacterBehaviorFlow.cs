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
        private bool forecasting;
        private bool unavailableForCopy;
        internal CharacterActionAlignmentRuntime Alignment;

        // 当前端点裁决后查询全部活动参与者，截取下一段可推进的角色时间。
        internal double NextBoundary(double time, double limit, bool inputAtLimit = false)
        {
            double next = limit;
            foreach (var rule in rules.Participants)
            {
                if ((active & rule.Id) == 0) continue;
                double delay = rule.Behavior.NextBoundaryDelay;
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

        // 在裁决后捕获活动位移动作的精确采样器和持续输入，供之后的物理区间读取。
        internal CharacterMotionCommand CaptureMotion(double time, CharacterBehaviorFrameResult result,
            CharacterInputSnapshot input, Vector2 facing, Vector2 velocity, CharacterMovementSettings settings)
            => BuildMotion(time, result, input, facing, velocity, settings, false, default);

        // 在行为发布边界提交该实例的目标方案，再冻结可重复读取的命令。
        internal CharacterMotionCommand PublishMotion(double time, CharacterBehaviorFrameResult result,
            CharacterInputSnapshot input, Vector2 facing, Vector2 velocity, CharacterMovementSettings settings,
            in CharacterAlignmentOrigin origin) => BuildMotion(time, result, input, facing, velocity, settings, true, origin);

        // 合成许可、普通移动和动作区间采样，仅显式发布时允许写入目标方案。
        private CharacterMotionCommand BuildMotion(double time, CharacterBehaviorFrameResult result, CharacterInputSnapshot input,
            Vector2 facing, Vector2 velocity, CharacterMovementSettings settings, bool publish, in CharacterAlignmentOrigin origin)
        {
            settings ??= fallbackSettings;
            foreach (var rule in rules.Actions)
            {
                if (!rule.ProvidesDisplacement || (active & rule.Id) == 0) continue;
                var interval = rule.Behavior as ICharacterIntervalMotion;
                if (interval == null)
                {
                    if (publish && Alignment.Plan != null) Alignment.Publish(null, origin, time, default);
                    return new CharacterMotionCommand(time, result, input, settings, rule.Id, null);
                }

                float turnSpeed = InputMath.NonNegative(settings.MoveTurnSpeed, CharacterMovementSettings.DefaultMoveTurnSpeed);
                var capture = new CharacterMotionCapture(time, facing, velocity, input, turnSpeed);
                var motion = interval.FreezeMotion(capture);
                if (motion == null)
                    throw new InvalidOperationException($"行为 {rule.Id} 已声明精确区间运动，须提供冻结采样器。");
                if (publish && (Alignment.HasTarget || Alignment.Instance != 0))
                {
                    Vector2 ordinary = result.MoveApproved ? CharacterMoveBehavior.TargetVelocity(input, settings, result.OrdinarySpeedLimit) : default;
                    Alignment.Publish(motion, origin, time, ordinary);
                }
                if (Alignment.Plan != null && motion.AlignmentWindow.Instance == Alignment.Plan.Instance)
                    motion = new CharacterFrozenAlignedMotion(motion, Alignment.Plan, time);
                return new CharacterMotionCommand(time, result, input, settings, rule.Id, motion);
            }
            if (publish && Alignment.Plan != null) Alignment.Publish(null, origin, time, default);
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
        public float StaminaRemaining => runtime.StaminaRemaining;
        public float DashCooldownRemaining => runtime.DashCooldownRemaining;

        // 兼容已有调用者，角色方案由装配层创建。
        internal static CharacterBehaviorFlow ForCharacter(CharacterBehaviorRegistration registration, bool snapshot = false) =>
            CharacterBehaviorAssembly.ForCharacter(registration, snapshot);

        /// <summary>创建使用默认角色规则的行为流程。</summary>
        public CharacterBehaviorFlow() : this(null)
        {
        }

        /// <summary>追加行为规则。内置普通移动与冲刺保留，额外规则需要提供独立的行为实例。</summary>
        public CharacterBehaviorFlow(CharacterBehaviorRule[] additionalRules, CharacterBehaviorInterrupt[] interrupts = null)
        {
            var assembly = new CharacterBehaviorAssembly(additionalRules, interrupts);
            rules = assembly.Rules;
            runtime = assembly.RuntimeState;
            locomotion = assembly.Locomotion;
        }

        // 预检整组行为后复制流程与动作状态，并让目标绑定自己的预测定义。
        internal void CopyStateTo(CharacterBehaviorFlow copy)
        {
            if (copy == null || ReferenceEquals(this, copy))
                throw new InvalidOperationException("需要独立的预测副本。");
            if (unavailableForCopy || copy.unavailableForCopy)
                throw new InvalidOperationException("复制尚未完成的预测副本不可复用。");

            rules.ValidateCopyTarget(copy.rules);
            copy.unavailableForCopy = true; // 私有复制或重绑失败时，此实例留在作废状态。
            copy.active = active;
            copy.simulationSeconds = simulationSeconds;
            copy.Alignment = Alignment;
            copy.grantTimes = null; // 预测不发布许可时刻。
            copy.FrameNumber = FrameNumber;
            runtime.CopyStateTo(copy.runtime);
            locomotion.CopyStateTo(copy.locomotion);
            rules.CopyParticipantStatesTo(copy.rules);
            copy.rules.BindPredictionDefinitions();
            copy.forecasting = true;
            copy.unavailableForCopy = false;
        }

        // 预测副本收到已打断通知后释放已有活动行为和遗留输入。
        internal void ReleasePredictionActions()
        {
            if (!forecasting) throw new InvalidOperationException("需要独立的预测副本");
            locomotion.Reset();
            var step = new CharacterBehaviorStep { Active = active };
            try
            {
                foreach (var rule in rules.Participants)
                    if (step.IsActive(rule.Id)) step.End(rule);
            }
            finally
            {
                active = step.Active;
            }
            EndInputContinuity();
        }

        // 把输入连续性结束通知发给每个已注册的行为。
        internal void EndInputContinuity()
        {
            foreach (var rule in rules.Participants)
                rule.Behavior.EndInputContinuity();
        }

        /// <summary>结束全部行为并按配置恢复运行状态，供角色创建或重生时调用。</summary>
        public void Reset(CharacterMovementSettings settings = null)
        {
            Alignment = default;
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
            return Run(input, facing, deltaTime, settings, groundVelocity, false);
        }

        // 逐帧入口按事件及动作边界精确分段；零长片段也可在该时刻裁决一次按下。
        internal CharacterBehaviorFrameResult AdvanceTimeline(CharacterInputSnapshot input, Vector2 facing, float deltaTime,
            CharacterMovementSettings settings, Vector2 groundVelocity) =>
            Run(input, facing, deltaTime, settings, groundVelocity, true);

        // 同一套规则服务逐帧裁决和显式步进调用，后者保留零步长只读语义。
        private CharacterBehaviorFrameResult Run(CharacterInputSnapshot input, Vector2 facing, float deltaTime,
            CharacterMovementSettings settings, Vector2 groundVelocity, bool separateBoundaries)
        {
            settings ??= fallbackSettings;
            float dt = InputMath.NonNegative(deltaTime, 0f);
            var step = new CharacterBehaviorStep { Active = active, StartSeconds = simulationSeconds,
                GrantTimes = grantTimes, SkipGrantHistory = forecasting };

            if (dt == 0f && !separateBoundaries)
                return ReadCurrentFrame(input, facing, settings, groundVelocity, ref step);

            runtime.BeginStep(settings);
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
            if (dt > 0f)
                foreach (var rule in rules.Participants)
                    rule.Behavior.AfterStep(dt);
            runtime.ReadOutcome(context, ref step);
            simulationSeconds += dt;
            return new CharacterBehaviorFrameResult(++FrameNumber, displacement, step);
        }

        // 依序推进准入、活动行为、位移合成及结束回调。
        private Vector2 AdvanceStep(ref CharacterBehaviorContext context, ref CharacterBehaviorStep step)
        {
            var movement = locomotion.BeginStep(context.DeltaTime, ref step);
            ReleaseWithdrawn(context, ref step);
            ResolveRequests(ref context, ref step);
            foreach (var rule in rules.Actions)
                if (step.IsActive(rule.Id))
                    locomotion.Combine(rule, rule.Behavior.AdvanceStep(context), ref movement, ref step);
            Vector2 displacement = locomotion.Resolve(context, rules, movement, ref step);

            // 发布本步真正执行的结果，然后才释放已完成的参与者。
            step.Executed = step.Active;
            foreach (var rule in rules.Actions)
                if (step.IsActive(rule.Id))
                    rule.Behavior.RecordOutcome(step.OrdinaryMovementApproved, ref step);
            FinishCompleted(context.SeparateBoundaries && context.DeltaTime > 0f, ref step);
            return displacement;
        }

        // 读取当前帧状态，供无效步长时保持观测而不推进流程。
        private CharacterBehaviorFrameResult ReadCurrentFrame(CharacterInputSnapshot input, Vector2 facing,
            CharacterMovementSettings settings, Vector2 groundVelocity, ref CharacterBehaviorStep step)
        {
            var context = runtime.CreateContext(input, facing, 0f, settings, simulationSeconds, groundVelocity);
            locomotion.ReadOutcome(ref step);
            foreach (var rule in rules.Participants)
                rule.Behavior.ReadOutcome(ref step);
            runtime.ReadOutcome(context, ref step);
            return new CharacterBehaviorFrameResult(FrameNumber, Vector2.zero, step);
        }

        // 在处理新请求前释放已撤销的活动许可。
        private void ReleaseWithdrawn(CharacterBehaviorContext context, ref CharacterBehaviorStep step)
        {
            foreach (var rule in rules.Actions)
            {
                if (!step.IsActive(rule.Id) || rule.Behavior.ShouldContinue(context))
                    continue;

                step.Withdrawn |= rule.Id;
                step.End(rule);
            }
        }

        // 按已装配的优先顺序判断请求，所有检查通过后再提交行为变更。
        private void ResolveRequests(ref CharacterBehaviorContext context, ref CharacterBehaviorStep step)
        {
            foreach (var rule in rules.Actions)
            {
                if (step.IsActive(rule.Id) || !rule.Behavior.TryPrepareStart(context, out var request))
                    continue;
                if (!rules.TryApprove(rule, locomotion.HasExistingPermit(context, step), step, out var interruptions))
                    continue;

                ApplyInterruptions(interruptions, ref step);
                step.Start(rule, request);
                context = runtime.CommitStart(request, context);
            }
        }

        // 释放本次申请已经获准打断的参与者。
        private void ApplyInterruptions(CharacterBehaviorId interruptions, ref CharacterBehaviorStep step)
        {
            if (interruptions == default) return;
            foreach (var rule in rules.Participants)
                if ((interruptions & rule.Id) != default)
                    step.End(rule);
            step.Interrupted |= interruptions;
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
