using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ThreeC.CharacterBehavior
{
    /// <summary>只编排观测、语义选择、仲裁、资源解析与播放，统一播放图的生命周期与资源缓存失效。</summary>
    internal sealed class CharacterAnimationPipeline
    {
        private readonly Animator animator;
        private readonly UnityEngine.Object logContext;
        private readonly CharacterAnimationMotionObserver observer;
        private readonly CharacterAnimationModules modules;
        private CharacterAnimationPlaybackPlan committedPlan;
        public CharacterAnimationIk Ik => modules.Ik;
        public CharacterMotionMatcher Matcher => modules.Matcher;
        private readonly List<(CharacterControllerFrameResult frame, CharacterAnimationMotion motion, bool reset, double time, bool preserve)> steps =
            new List<(CharacterControllerFrameResult, CharacterAnimationMotion, bool, double, bool)>();
        private CharacterPresentationInput input;
        private CharacterPoseCandidate candidate;
        private CharacterPlaybackObservation observed;
        private bool prepared;
        private bool awaitingGaitForecast;
        private int capturedSteps, deferredSteps;
        private Task job;
        private readonly System.Action compute;
        private int capturedSimulation;
        private Avatar capturedAvatar;
        private bool reselect;
        private int stateVersion;
        private string reportedError;
        private bool animatorUnavailable;
        private CharacterBehaviorObservation lastBehavior;
#if UNITY_EDITOR
        private readonly CharacterAnimationDebugTrace debugTrace = new CharacterAnimationDebugTrace();
#endif

        // 从最近求解步恢复同源运动轨迹，再以当前行为恢复动作绑定。
        public CharacterAnimationPipeline(Animator animator, CharacterAnimationConfiguration configuration,
            CharacterBehaviorAnimator logContext, CharacterControllerFrameResult latestFrame, CharacterBehaviorObservation behavior, int currentStateVersion,
            int simulationVersion, CharacterGroundTrajectory externalTrajectory)
        {
            compute = Plan;
            this.animator = animator;
            this.logContext = logContext;
            observer = new CharacterAnimationMotionObserver(logContext.transform, configuration.IkTeleportDistance);
            lastBehavior = behavior.SimulationVersion == simulationVersion ? behavior : default;
            modules = new CharacterAnimationModules(animator, logContext, configuration, lastBehavior);
            stateVersion = currentStateVersion;
            observer.Reset(latestFrame, simulationVersion, externalTrajectory);
            modules.Intent.ResetGait(lastBehavior.Behavior);
            SynchronizeResources();
        }

        // 交接旧配置下的固定步后重新装配模块。
        internal void Reconfigure(CharacterAnimationConfiguration config, CharacterBehaviorObservation seed)
        {
            Join();
            if (prepared) Discard();
            ConsumeSteps(steps.Count);
            modules.Reconfigure(config, seed);
        }

        public CharacterAnimationMotion ReadMotion(CharacterControllerFrameResult latestFrame,
            int currentStateVersion, int simulationVersion, bool simulating)
        {
            return currentStateVersion == stateVersion
                ? observer.Read(latestFrame, simulationVersion, simulating) : default;
        }

        // 记录已结算的固定步及外部来源提供的表现轨迹，供步态历史按原顺序消费。
        public void Receive(CharacterControllerFrameResult frame, int currentStateVersion,
            int simulationVersion, bool simulating, double time, CharacterGroundTrajectory externalTrajectory = default)
        {
            Join();
            if (stateVersion != currentStateVersion)
                Reset(default, currentStateVersion, simulationVersion);
            bool received = observer.Receive(frame, simulationVersion, simulating, out bool resetPose, externalTrajectory);
            if (resetPose) Ik.ClearPose();
            if (received)
                steps.Add((frame, observer.Read(frame, simulationVersion, simulating), resetPose, time, false));
            else if (resetPose) steps.Add((default, default, true, time, false));
        }

        // 接收当前代次的新行为片段；固定步重读与同帧重复采集均不会触发动作。
        internal void ReceiveBehavior(in CharacterBehaviorObservation frame, int currentStateVersion, int simulationVersion)
        {
            if (stateVersion != currentStateVersion) Reset(default, currentStateVersion, simulationVersion);
            if (frame.FrameNumber == 0 || frame.SimulationVersion != simulationVersion ||
                (frame.SimulationVersion == lastBehavior.SimulationVersion && frame.FrameNumber <= lastBehavior.FrameNumber)) return;
            lastBehavior = frame;
            modules.Bindings.Observe(frame);
            modules.Intent.ObserveDash(frame.Behavior);
        }

        // 按顺序消化本次采集覆盖的固定步，后到的提交留给下一次采集。
        private void ConsumeSteps(int count, bool deferGait = false)
        {
            for (int i = 0; i < count; i++)
            {
                var step = steps[i];
                if (step.reset) Matcher.Reset();
                if (step.frame.FrameNumber != 0)
                {
                    modules.Intent.ObserveActionVariantMotion(step.motion);
                    step.preserve = modules.Bindings.PreservesEntryGait(step.frame);
                }
                if (deferGait) steps[i] = step;
                else ReceiveIntent(step, false);
            }
            if (deferGait) deferredSteps = count;
            else steps.RemoveRange(0, count);
        }

        // 用与本步绑定结果对应的步态许可推进意图。
        private void ReceiveIntent(in (CharacterControllerFrameResult frame, CharacterAnimationMotion motion,
            bool reset, double time, bool preserve) step, bool projectGait)
        {
            if (step.reset) modules.Intent.InvalidateMotion();
            if (step.frame.FrameNumber == 0) return;
            var motion = projectGait && modules.Intent.MayUsePredictedGait(step.motion, step.frame.Behavior)
                ? input.ProjectStep(step.frame, step.motion, step.time) : step.motion;
            modules.Intent.Receive(motion, step.frame.Behavior, step.frame.Input, step.frame.DeltaTime, step.preserve);
        }

        // 只补交已处理绑定的固定步意图，绑定和许可仍然按原计划发布一次。
        private void FinishDeferredGait(bool projectGait)
        {
            for (int i = 0; i < deferredSteps; i++) ReceiveIntent(steps[i], projectGait);
            steps.RemoveRange(0, deferredSteps);
            deferredSteps = 0;
        }

        // 找出需要先核对发布预测、再更新持久步态的固定步批次。
        private bool HasForecastSensitiveSteps()
        {
            foreach (var step in steps)
                if (modules.Intent.MayUsePredictedGait(step.motion, step.frame.Behavior)) return true;
            return false;
        }

        // 采样动画更新所需输入，并处理暂停、Animator 停用与资源缺失。
        public bool Capture(CharacterControllerFrameResult latestFrame, CharacterBehaviorObservation behavior, int currentStateVersion,
            int simulationVersion, bool simulating, float deltaTime, double now, long guardGrant, CharacterMovementForecast forecast)
        {
            Receive(latestFrame, currentStateVersion, simulationVersion, simulating, now); // 补读遗漏的固定步提交，观测端负责去重。
            CharacterAnimationMotion motion = observer.Read(latestFrame, simulationVersion, simulating);
            bool validGrant = simulating && behavior.FrameNumber > 0 && behavior.SimulationVersion == simulationVersion;
#if UNITY_EDITOR
            debugTrace.CaptureSimulation(simulationVersion);
#endif
            if (animator == null || !animator.isActiveAndEnabled)
            {
                ConsumeSteps(steps.Count);
                modules.Bindings.UpdateGrants(validGrant, guardGrant);
                modules.Bindings.SuspendPulses();
                // Animator 停用时仍接收固定步，恢复时重发播放状态并重建步态相位。
                if (!animatorUnavailable)
                {
                    modules.Bindings.RebindGrantedEntries();
                    modules.Composer.ResetSelection();
                    committedPlan = default;
                    Matcher.Reset();
                    Ik.Invalidate();
                    modules.Player.CloseGraph();
                }
                animatorUnavailable = true;
                modules.Intent.InvalidateMotion();
#if UNITY_EDITOR
                CharacterMatchDebugCapture.Clear(animator);
                debugTrace.SetStatus(CharacterAnimationDebugStatus.AnimatorUnavailable);
#endif
                return false;
            }
            animatorUnavailable = false;
            modules.Player.SetPaused(!simulating);
            bool ready = SynchronizeResources();
            if (!simulating || !ready)
            {
                ConsumeSteps(steps.Count);
                modules.Bindings.UpdateGrants(validGrant, guardGrant);
                modules.Bindings.SuspendPulses();
                Matcher.Reset();
                if (!ready) Ik.Invalidate();
                else Ik.SetFrozen(true);
                modules.Intent.InvalidateMotion();
#if UNITY_EDITOR
                CharacterMatchDebugCapture.Clear(animator);
                if (!simulating)
                    debugTrace.SetStatus(CharacterAnimationDebugStatus.Paused);
#endif
                return false;
            }
            modules.Resources.CaptureWeights(deltaTime);
            Matcher.CaptureCompatibility();
            var feet = motion.IsValid ? Ik.ReadFeet(motion) : default;
            Matcher.Features = Ik.ReadFeatures(feet);
            var seen = modules.Playback.Capture(committedPlan.FullBody, feet);
            input = new CharacterPresentationInput(latestFrame, behavior.Behavior, motion, forecast, seen, feet,
                deltaTime, now, validGrant, guardGrant, Matcher.Windows);
            capturedSteps = steps.Count;
            awaitingGaitForecast = forecast != null && HasForecastSensitiveSteps();
            capturedSimulation = simulationVersion;
            capturedAvatar = animator.avatar;
            reselect = false;
            prepared = true;
            return true;
        }

        internal void Schedule(bool threaded)
        {
            if (threaded) job = Task.Run(compute);
            else { Plan(); modules.Composer.FinishPlan(); }
        }

        internal void Join()
        {
            if (job == null) return;
            using var wait = CharacterPresentationBatch.Wait.Auto();
            var running = job;
            job = null;
            try { running.GetAwaiter().GetResult(); }
            catch { Discard(); throw; }
            finally { modules.Composer.FinishPlan(); }
        }

        internal bool CanApply(int state, int simulation, bool simulating)
        {
            if (!simulating && animator && animator.isActiveAndEnabled)
            {
                modules.Player.SetPaused(true);
                Ik.SetFrozen(true);
            }
            return prepared && simulating && stateVersion == state && capturedSimulation == simulation &&
                animator && animator.isActiveAndEnabled && animator.avatar == capturedAvatar && !modules.Player.NeedsRebind;
        }

        // 按计划时点提交绑定；预测敏感的步态要等最终核对后才用于选姿。
        private void Plan()
        {
            using var sample = CharacterPresentationBatch.Planning.Auto();
            modules.Composer.BeginPlan();
            ConsumeSteps(capturedSteps, awaitingGaitForecast);
            var motion = input.Motion;
            modules.Bindings.UpdateGrants(input.ValidGrant, input.GuardGrant);
            if (!motion.IsValid) modules.Intent.ObserveActionVariantMotion(motion);
            modules.Bindings.SubmitGranted();
            if (!awaitingGaitForecast) PlanSelection();
        }

        // 用此批次已更新的步态历史推进表现时间和候选选择。
        private void PlanSelection()
        {
            if (input.Motion.IsValid) modules.Intent.AdvancePresentation(input.DeltaTime);
            else modules.Intent.InvalidateMotion();
            observed = input.Observation.ForPhase(modules.Intent.Phase);
            Matcher.SaveSelectionHistory();
            candidate = EvaluateCandidate();
        }

        // 以当前意图仲裁部位，计算下身片段及足部覆盖许可。
        private CharacterPoseOwners SelectOwners()
        {
            var selection = modules.Composer.Preview(modules.Intent.Current);
            bool returning = modules.Bindings.IsReturningToMovement(selection.Overlay, input.Behavior);
            return modules.Poses.SelectOwners(selection, observed, committedPlan, returning, modules.Intent.UsesMovingActionVariant);
        }

        // 用本帧观测和最近提交的播放计划计算完整候选。
        private CharacterPoseCandidate EvaluateCandidate() =>
            modules.Poses.EvaluateCandidate(SelectOwners(), modules.Intent, Matcher, input, observed, committedPlan);

        // 在提交前更新本帧的预测输入，轨迹发生变化时重新选择。
        internal void RefreshForecast(CharacterMovementForecast published)
        {
            input = input.RefreshForecast(published, out bool changed);
            reselect |= changed;
        }

        // 在预测核对后处理尚未计划的步态批次，并提交动画组合。
        internal void Apply()
        {
            if (!prepared) return;
            prepared = false;
            try
            {
                if (awaitingGaitForecast)
                {
                    FinishDeferredGait(true);
                    PlanSelection();
                    reselect = false;
                }
                modules.Composer.BeginPlan(); // 单次动作的完成回执随最终播放结果发布。
                ApplySelection();
            }
            catch { Discard(); throw; }
        }

        // 核对单次动作回执并重新选择，完成播放下发后发布最终结果。
        private void ApplySelection()
        {
            var preview = reselect ? modules.Composer.Preview(modules.Intent.Current) : candidate.Owners.Selection;
            if (modules.Player.ReconcileOneShots(committedPlan, observed, preview, modules.Composer))
            {
                modules.Bindings.SubmitGranted();
                reselect = true;
            }
            if (reselect)
            {
                Matcher.RestoreSelectionHistory();
                candidate = EvaluateCandidate();
            }
            var entryVelocity = modules.Intent.StopEntryVelocity;
            if (modules.Intent.ApplyRelease(candidate.Release))
                candidate = new CharacterPoseCandidate(SelectOwners(), candidate.Release, candidate.Match);
            var motion = input.Motion;
            float deltaTime = input.DeltaTime;
            var plan = modules.Poses.Select(candidate, modules.Intent.UsesMovingActionVariant, observed, motion, entryVelocity, committedPlan);
            var selection = candidate.Owners.Selection.WithPlaybackWeights(plan);
            var emission = modules.Player.ApplyPlan(plan, deltaTime);
            modules.Composer.PublishReceipts(true);
            modules.Composer.Commit(selection);
            modules.Player.PublishOneShotStarts(plan, emission);
            modules.Bindings.ReleaseExpiredRequests();
            committedPlan = plan;
            Ik.Prepare(selection, motion, plan, deltaTime, input.Time); // 目标许可与当前片段混合、分层遮罩同步。
            modules.Player.EvaluatePose();
#if UNITY_EDITOR
            Matcher.Publish(candidate.Match, plan);
            debugTrace.Publish(plan, emission, modules.Player.CaptureDebugMix(), Ik.RootDebug);
#endif
            modules.Bindings.FlushWarnings();
            modules.Player.FlushWarnings(logContext);
        }

        // 撤销本帧回执，已结算意图仍以固定步观测完成交接。
        internal void Discard()
        {
            prepared = false;
            if (deferredSteps != 0) FinishDeferredGait(false);
            modules.Composer.PublishReceipts(false);
            Matcher.Reset();
        }

        public CharacterActionPlayback PlayOnce(string key, float weight, CharacterControllerFrameResult latestFrame,
            int currentStateVersion, int simulationVersion)
        {
            Join();
            if (stateVersion != currentStateVersion) Reset(latestFrame, currentStateVersion, simulationVersion);
            var result = SynchronizeResources() && modules.Resources.CanPlayOnce(key) ? modules.Composer.PlayOnce(key, weight) : null;
            reselect |= prepared && result != null;
            return result;
        }

        // 消费已结算固定步并重新建立位移及姿态历史。
        public void ResetPoseHistory()
        {
            Join();
            Discard();
            ConsumeSteps(steps.Count);
            observer.InvalidatePosition();
            ClearPoseHistory();
        }

        private void ClearPoseHistory()
        {
            Matcher.Reset();
            modules.Intent.InvalidateMotion();
            Ik.ClearPose();
        }

        public bool Submit(CharacterAnimationRequest request, CharacterControllerFrameResult latestFrame,
            int currentStateVersion, int simulationVersion)
        {
            Join();
            if (stateVersion != currentStateVersion)
                Reset(latestFrame, currentStateVersion, simulationVersion);
            if (string.IsNullOrWhiteSpace(request.SemanticKey) || request.InstanceId <= 0 ||
                !SynchronizeResources() || !modules.Resources.CanSubmit(request.SemanticKey))
                return false;
            bool accepted = modules.Composer.Submit(request);
            reselect |= prepared && accepted;
            return accepted;
        }

        public bool Withdraw(string semanticKey, long instanceId)
        {
            Join();
            bool removed = !string.IsNullOrWhiteSpace(semanticKey) && modules.Composer.Withdraw(semanticKey, instanceId);
            reselect |= prepared && removed;
            return removed;
        }

        public bool Withdraw(CharacterActionPlayback playback)
        {
            Join();
            bool removed = modules.Composer.Withdraw(playback);
            reselect |= prepared && removed;
            return removed;
        }

        // 清空两个时间域的观察历史，等待新代次的动作与位移。
        public void Reset(CharacterControllerFrameResult latestFrame, int currentStateVersion, int simulationVersion)
        {
            Join();
            Discard();
            steps.Clear();
            stateVersion = currentStateVersion;
            observer.Reset(latestFrame, simulationVersion);
            lastBehavior = default;
            modules.Intent.ResetGait(default);
            modules.Intent.ObserveActionVariantMotion(default);
            modules.Bindings.Reset();
            modules.Composer.Reset();
            committedPlan = default;
            Ik.Reset();
            modules.Player.CloseGraph();
#if UNITY_EDITOR
            CharacterMatchDebugCapture.Clear(animator);
            debugTrace.Clear();
#endif
        }

        // Animator 的 Avatar 或控制器占用变化时重新验证资源与播放可用状态。
        private bool SynchronizeResources()
        {
            if (modules.Player.NeedsRebind)
            {
                Discard();
                modules.Intent.InvalidateMotion();
                // 资源重绑时保留持续需求及实例编号，清除上一次播放和姿态历史。
                committedPlan = default;
                Ik.Invalidate();
                modules.Player.BindPlayback();
                modules.Resources.RefreshBindings();
                modules.Poses.BindClips();
                modules.Bindings.RebindGrantedEntries();
                modules.Composer.ResetSelection();
                reportedError = null;
                if (modules.Resources.Ready)
                {
                    foreach (var key in modules.Composer.ActiveKeys)
                        if (!modules.Resources.HasBinding(key))
                            Debug.LogWarning($"当前动画资源缺少动作 {key}，持续请求将在资源恢复后参与选择。", logContext);
                }
#if UNITY_EDITOR
                CharacterMatchDebugCapture.Clear(animator);
                debugTrace.Clear();
#endif
            }
            if (modules.Resources.Ready) reportedError = null;
            else ReportError(modules.Resources.ValidationError);
            return modules.Resources.Ready;
        }

        private void ReportError(string error)
        {
#if UNITY_EDITOR
            debugTrace.SetStatus(CharacterAnimationDebugStatus.ResourceUnavailable, error);
#endif
            if (reportedError == error)
                return;
            reportedError = error;
            Debug.LogError(error, logContext);
        }

        // 销毁播放图并释放该角色最后一次诊断结果。
        public void Release()
        {
#if UNITY_EDITOR
            CharacterMatchDebugCapture.Clear(animator);
#endif
            Join();
            Discard();
            steps.Clear();
            Ik.Dispose();
            modules.Player.Release();
            modules.Composer.Reset();
        }

#if UNITY_EDITOR
        // 读取当前动画实例已发布的播放结果。
        public CharacterAnimationDebugSnapshot ReadDebug(int currentStateVersion, int simulationVersion, bool simulating)
        {
            return debugTrace.Read(animator, modules.Player, stateVersion, currentStateVersion, simulationVersion, simulating);
        }
#endif
    }
}
