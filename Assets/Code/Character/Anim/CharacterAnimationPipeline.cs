using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ThreeC.CharacterBehavior
{
    /// <summary>只编排观测、语义选择、仲裁、资源解析与播放，统一生命周期与控制器缓存失效。</summary>
    internal sealed class CharacterAnimationPipeline
    {
        private readonly Animator animator;
        private readonly UnityEngine.Object logContext;
        private readonly CharacterAnimationMotionObserver observer;
        private readonly CharacterAnimationModules modules;
        private CharacterAnimationPlaybackPlan committedPlan;
        public CharacterAnimationIk Ik => modules.Ik;
        public CharacterMotionMatcher Matcher => modules.Matcher;
        private readonly List<(CharacterControllerFrameResult frame, CharacterAnimationMotion motion, bool reset)> steps =
            new List<(CharacterControllerFrameResult, CharacterAnimationMotion, bool)>();
        private CharacterPresentationInput input;
        private CharacterAnimationComposition selection;
        private CharacterPlaybackObservation observed;
        private CharacterAnimationLayerPlan lower;
        private CharacterReleaseDecision release;
        private CharacterMatchResult match;
        private bool lowerCoversFeet, matchRecovery, forecastEnabled;
        private bool prepared;
        private string warning;
        private Task job;
        private readonly System.Action compute;
        private int capturedSimulation;
        private Avatar capturedAvatar;
        private bool hasAvatar;
        private bool reselect;
        private int stateVersion;
        private string reportedError;
        private bool animatorUnavailable;
#if UNITY_EDITOR
        internal AnimationClip ActivePreviewClip(int stateHash) => modules.Player.Direct?.StateClip(stateHash);
        private readonly CharacterAnimationDebugTrace debugTrace = new CharacterAnimationDebugTrace();
        private RuntimeAnimatorController debugController;
        private int debugSimulationVersion;
#endif

        public CharacterAnimationPipeline(Animator animator, CharacterAnimationConfiguration configuration,
            CharacterBehaviorAnimator logContext, CharacterControllerFrameResult latestFrame, int currentStateVersion,
            int simulationVersion)
        {
            compute = Plan;
            this.animator = animator;
            this.logContext = logContext;
            observer = new CharacterAnimationMotionObserver(logContext.transform, configuration.IkTeleportDistance);
            modules = new CharacterAnimationModules(animator, logContext, configuration,
                latestFrame.SimulationVersion == simulationVersion ? latestFrame : default);
            stateVersion = currentStateVersion;
            observer.Reset(latestFrame, simulationVersion);
            modules.Intent.Reset(latestFrame.Behavior);
            SynchronizeResources();
        }

        internal void Reconfigure(CharacterAnimationConfiguration config, CharacterControllerFrameResult seed)
        {
            Join();
            if (prepared) Discard();
            ConsumeSteps();
            modules.Reconfigure(config, seed);
        }

        public CharacterAnimationMotion ReadMotion(CharacterControllerFrameResult latestFrame,
            int currentStateVersion, int simulationVersion, bool simulating)
        {
            return currentStateVersion == stateVersion
                ? observer.Read(latestFrame, simulationVersion, simulating) : default;
        }

        public void Receive(CharacterControllerFrameResult frame, int currentStateVersion,
            int simulationVersion, bool simulating)
        {
            Join();
            if (stateVersion != currentStateVersion)
                Reset(default, currentStateVersion, simulationVersion);
            bool received = observer.Receive(frame, simulationVersion, simulating, out bool resetPose);
            if (resetPose) Ik.ClearPose();
            if (received)
            {
#if UNITY_EDITOR
                debugTrace.ObserveGroundStep(frame);
#endif
                var motion = observer.Read(frame, simulationVersion, simulating);
                steps.Add((frame, motion, resetPose));
            }
            else if (resetPose) steps.Add((default, default, true));
        }

        private void ConsumeSteps()
        {
            foreach (var step in steps)
            {
                if (step.reset) { Matcher.Reset(); modules.Intent.InvalidateMotion(); }
                if (step.frame.FrameNumber == 0) continue;
                if (step.motion.IsValid) modules.Resources.ObserveMovement(step.motion);
                modules.Bindings.Observe(step.frame);
                modules.Intent.Receive(step.motion, step.frame.Behavior, step.frame.Input, step.frame.DeltaTime, modules.Bindings.PreserveEntryGait);
            }
            steps.Clear();
        }

        public bool Capture(CharacterControllerFrameResult latestFrame, int currentStateVersion,
            int simulationVersion, bool simulating, float deltaTime, long guardGrant, CharacterMovementForecast forecast)
        {
            Receive(latestFrame, currentStateVersion, simulationVersion, simulating); // 补读遗漏的固定步提交，观测端负责去重。
            CharacterAnimationMotion motion = observer.Read(latestFrame, simulationVersion, simulating);
            bool validGrant = simulating && latestFrame.SimulationVersion == simulationVersion;
#if UNITY_EDITOR
            debugSimulationVersion = simulationVersion;
#endif
            if (animator == null || !animator.isActiveAndEnabled)
            {
                ConsumeSteps();
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
                debugTrace.SetStatus(CharacterAnimationDebugStatus.AnimatorUnavailable);
#endif
                return false;
            }
            if (hasAvatar && capturedAvatar != animator.avatar)
            {
                ClearPoseHistory();
                committedPlan = default;
                modules.Composer.ResetSelection();
                modules.Bindings.RebindGrantedEntries();
            }
            animatorUnavailable = false;
            modules.Player.SetPaused(!simulating);
            bool ready = SynchronizeResources();
            if (!simulating || !ready)
            {
                ConsumeSteps();
                modules.Bindings.UpdateGrants(validGrant, guardGrant);
                modules.Bindings.SuspendPulses();
                Matcher.Reset();
                if (!ready) Ik.Invalidate();
                else Ik.SetFrozen(true);
                modules.Intent.InvalidateMotion();
#if UNITY_EDITOR
                if (!simulating)
                    debugTrace.SetStatus(CharacterAnimationDebugStatus.Paused);
#endif
                return false;
            }
            modules.Resources.CaptureWeights(deltaTime);
            Matcher.CaptureCompatibility();
            var feet = motion.IsValid ? Ik.ReadFeet(motion) : default;
            var seen = modules.Playback.Capture(committedPlan.FullBody, feet);
            forecastEnabled = forecast != null;
            input = new CharacterPresentationInput(latestFrame, motion, forecast?.Snapshot(), seen, feet,
                deltaTime, Time.timeAsDouble, validGrant, guardGrant);
            capturedSimulation = simulationVersion;
            capturedAvatar = animator.avatar;
            hasAvatar = true;
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

        private void Plan()
        {
            using var sample = CharacterPresentationBatch.Planning.Auto();
            modules.Composer.BeginPlan();
            ConsumeSteps();
            var motion = input.Motion;
            modules.Bindings.UpdateGrants(input.ValidGrant, input.GuardGrant);
            if (!motion.IsValid && !motion.RetainedStrideSpeed.HasValue) modules.Resources.ObserveMovement(default);
            modules.Bindings.SubmitGranted(input.FixedRemainder);
            if (motion.IsValid) modules.Intent.AdvancePresentation(input.DeltaTime);
            else modules.Intent.InvalidateMotion();
            observed = input.Observation.ForPhase(modules.Intent.Phase, committedPlan.FullBody);
            Matcher.SaveSelectionHistory();
            EvaluateCandidate();
        }

        private void SelectOwners()
        {
            selection = modules.Composer.Preview(modules.Intent.Current);
            bool returning = modules.Bindings.IsReturningToMovement(selection.Overlay, input.Frame.Behavior);
            lower = modules.Poses.SelectLower(selection, observed, committedPlan, returning, input.DeltaTime,
                out lowerCoversFeet, out matchRecovery);
        }

        private void EvaluateCandidate()
        {
            SelectOwners();
            Matcher.PrepareReturn(input.Forecast, input.Time, selection.FullBody, lowerCoversFeet, matchRecovery, input.Motion, input.Feet);
            release = modules.Poses.EvaluateRelease(modules.Intent, Matcher, selection.FullBody, lowerCoversFeet,
                matchRecovery, observed, committedPlan, input.Motion, input.Feet, input.DeltaTime, out match);
        }

        internal void RefreshForecast(CharacterMovementForecast published, CharacterControllerFrameResult actual)
        {
            var snapshot = published?.Snapshot();
            if (forecastEnabled == (published != null) && ReferenceEquals(snapshot, input.Forecast)) return;
            forecastEnabled = published != null;
            var latest = CharacterForecastQuery.ForAnimation(actual, published, input.Time);
            var motion = new CharacterAnimationMotion(input.Motion, latest.Trajectory);
            input = new CharacterPresentationInput(latest, motion, snapshot, input.Observation, input.Feet,
                input.DeltaTime, input.Time, input.ValidGrant, input.GuardGrant);
            reselect = true;
        }

        internal void Apply()
        {
            if (!prepared) return;
            prepared = false;
            modules.Composer.BeginPlan(); // 单次动作的完成回执随最终播放结果发布。
            try { ApplySelection(); }
            catch { Discard(); throw; }
        }

        private void ApplySelection()
        {
            if (reselect) selection = modules.Composer.Preview(modules.Intent.Current);
            if (ResolveOneShots(observed, selection))
            {
                modules.Bindings.SubmitGranted(input.FixedRemainder);
                reselect = true;
            }
            if (reselect)
            {
                Matcher.RestoreSelectionHistory();
                EvaluateCandidate();
            }
            var entryVelocity = modules.Intent.StopEntryVelocity;
            if (modules.Intent.ApplyRelease(release)) SelectOwners();
            var motion = input.Motion;
            float deltaTime = input.DeltaTime;
            var plan = modules.Poses.Select(selection, lower, lowerCoversFeet, matchRecovery, observed, motion,
                entryVelocity, committedPlan, release, match);
            selection = selection.WithPlaybackWeights(plan);
            var emission = modules.Player.Active.Apply(plan, deltaTime);
            modules.Composer.PublishReceipts(true);
            modules.Composer.Commit(selection);
            if (emission.FullBody) plan.FullBody.Demand.Playback?.Start();
            if (emission.Overlay) plan.Overlay.Demand.Playback?.Start();
            if (emission.LowerBody) plan.LowerBody.Demand.Playback?.Start();
            if (emission.UpperBody) plan.UpperBody.Demand.Playback?.Start();
            modules.Bindings.ReleaseExpiredRequests();
            committedPlan = plan;
            Ik.Prepare(selection, motion, plan, deltaTime); // 目标许可与当前片段混合、分层遮罩同步。
            modules.Player.Direct?.Evaluate();
#if UNITY_EDITOR
            var handoff = new CharacterDebugHandoff(input.Frame.Behavior, modules.Player);
            debugTrace.Record(motion, modules.Intent.Phase, modules.Intent.PhaseReason, selection, plan, emission,
                observed.LeftSupport, entryVelocity, handoff);
#endif
            modules.Bindings.FlushWarnings();
            if (warning != null) { Debug.LogWarning(warning, logContext); warning = null; }
        }

        internal void Discard()
        {
            prepared = false;
            modules.Composer.PublishReceipts(false);
            Matcher.Reset();
        }

        private bool ResolveOneShots(in CharacterPlaybackObservation seen, in CharacterAnimationComposition selected)
        {
            if (committedPlan.FullBody.Demand.Playback == null && committedPlan.UpperBody.Demand.Playback == null &&
                committedPlan.LowerBody.Demand.Playback == null && committedPlan.Overlay.Demand.Playback == null) return false;
            bool changed = InspectOneShot(committedPlan.FullBody, seen.Full, selected.FullBody);
            changed |= InspectOneShot(committedPlan.Overlay, seen.Overlay, selected.Overlay);
            changed |= InspectOneShot(committedPlan.LowerBody, seen.Lower, selected.LowerBody);
            changed |= InspectOneShot(committedPlan.UpperBody, seen.Upper, selected.UpperBody);
            return changed;
        }

        private bool InspectOneShot(in CharacterAnimationLayerPlan previous, in CharacterLayerObservation layer,
            CharacterAnimationDemand selected)
        {
            var playback = previous.Demand.Playback;
            if (playback == null || playback.Status != CharacterActionPlaybackStatus.Started ||
                selected.Playback != playback) return false;
            var status = playback.Observe(previous.StateHash, layer);
            if (status == CharacterActionPlaybackStatus.Started) return false;
            if (layer.Destination.fullPathHash == previous.StateHash && layer.Destination.loop)
                warning = $"单次动作 {playback.Key} 绑定了循环状态 {previous.StateName}，已中止。";
            return modules.Composer.End(playback.Key, playback.InstanceId, status);
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

        public void ResetPoseHistory()
        {
            Join();
            Discard();
            ConsumeSteps();
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

        public void Reset(CharacterControllerFrameResult latestFrame, int currentStateVersion, int simulationVersion)
        {
            Join();
            Discard();
            steps.Clear();
            stateVersion = currentStateVersion;
            observer.Reset(latestFrame, simulationVersion);
            modules.Intent.Reset(latestFrame.Behavior);
            modules.Bindings.Reset();
            modules.Resources.ObserveMovement(default);
            modules.Composer.Reset();
            committedPlan = default;
            Ik.Reset();
            modules.Player.ResetPlayback();
#if UNITY_EDITOR
            debugTrace.Clear();
#endif
        }

        private bool SynchronizeResources()
        {
            if (modules.Player.NeedsRebind)
            {
                Discard();
                modules.Intent.InvalidateMotion();
                // 更换控制器只清播放和选择历史，保留持续需求及实例编号。
                committedPlan = default;
                Ik.Invalidate();
                modules.Player.BindController();
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
                debugController = modules.Player.Controller;
                debugTrace.Clear();
#endif
            }
            if (!modules.Resources.Ready)
            {
                if (modules.Resources.ValidationError != null)
                    ReportError(modules.Resources.ValidationError);
#if UNITY_EDITOR
                else
                    debugTrace.SetStatus(CharacterAnimationDebugStatus.ResourceUnavailable, "缺少可用的动画播放库或 Animator Controller。");
#endif
            }
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

        public void Release()
        {
            Join();
            Discard();
            steps.Clear();
            Ik.Dispose();
            modules.Player.Release();
            modules.Composer.Reset();
        }

#if UNITY_EDITOR
        public CharacterAnimationDebugSnapshot ReadDebug(int currentStateVersion, int simulationVersion, bool simulating,
            out ICharacterAnimationDebugHistory history)
        {
            bool matchingOwner = stateVersion == currentStateVersion && animator != null &&
                debugController == modules.Player.Controller;
            history = matchingOwner ? debugTrace : null;
            if (animator == null || !animator.isActiveAndEnabled)
                return new CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus.AnimatorUnavailable);
            if (!simulating)
                return new CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus.Paused);
            CharacterAnimationDebugSnapshot snapshot = debugTrace.Current;
            if (!matchingOwner || debugSimulationVersion != simulationVersion ||
                snapshot.Status == CharacterAnimationDebugStatus.Paused ||
                snapshot.Status == CharacterAnimationDebugStatus.AnimatorUnavailable)
                return default;
            return snapshot;
        }
#endif
    }
}
