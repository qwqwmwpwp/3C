using UnityEngine;
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
        public CharacterPosePipeline Pose => modules.Pose;
        public CharacterMotionMatcher Matcher => modules.Matcher;
        private readonly CharacterAnimationStepHistory steps;
        private CharacterPresentationWork batch;
        private Task job;
        private readonly System.Action compute;
        private int stateVersion;
        private string reportedError;
        private bool animatorUnavailable;
        private bool clearMotionSources;
        private int graphGeneration;
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
            modules = new CharacterAnimationModules(animator, logContext, configuration, lastBehavior, currentStateVersion);
            steps = new CharacterAnimationStepHistory(modules);
            stateVersion = currentStateVersion;
            observer.Reset(latestFrame, simulationVersion, externalTrajectory);
            modules.Intent.ResetGait(lastBehavior.Behavior);
            SynchronizeResources();
        }

        // 核对当前播放库与模块影响，旧批次可在校验期间继续完成规划。
        internal AnimationModuleChanges PrepareModules(CharacterAnimationConfiguration config, bool refreshContents) =>
            modules.Prepare(config, refreshContents);

        // 交接旧配置下的固定步后重新装配模块。
        internal void Reconfigure(CharacterAnimationConfiguration config, AnimationModuleChanges changes, CharacterBehaviorObservation seed)
        {
            Join();
            if (batch.Pending) Discard(CharacterPlaybackEndSource.Rebind);
            steps.Consume(steps.Count, ref batch);
            modules.Composer.SetTime(seed.Time);
            if (changes.GroundChanged) committedPlan = committedPlan.WithoutGround();
            CharacterMatchDebugCapture.MarkBoundary(animator, changes.GroundChanged ? "地面库或规则重新装配" : null);
            modules.Reconfigure(config, changes, seed);
            observer.ConfigureTeleport(config.IkTeleportDistance);
        }

        // 瞬移切断旧候选与预测入点，保留独立动作的时钟。
        private void ResetGroundHistory()
        {
            CharacterMatchDebugCapture.MarkBoundary(animator, "地面运动观测不连续");
            Discard();
            committedPlan = committedPlan.WithoutGround();
            Pose.ClearPose();
        }

        // 只向当前行为代次提供运动观测。
        public CharacterAnimationMotion ReadMotion(CharacterControllerFrameResult latestFrame, int currentStateVersion,
            int simulationVersion, bool simulating) => currentStateVersion == stateVersion
            ? observer.Read(latestFrame, simulationVersion, simulating) : default;

        // 记录已结算的固定步及外部来源提供的表现轨迹，供步态历史按原顺序消费。
        public void Receive(CharacterControllerFrameResult frame, int currentStateVersion,
            int simulationVersion, bool simulating, double time, CharacterGroundTrajectory externalTrajectory = default)
        {
            Join();
            if (stateVersion != currentStateVersion)
                Reset(default, currentStateVersion, simulationVersion);
            bool received = observer.Receive(frame, simulationVersion, simulating, out bool resetPose, externalTrajectory);
            if (resetPose) ResetGroundHistory();
            if (received)
                steps.Add(frame, observer.Read(frame, simulationVersion, simulating), resetPose, time);
            else if (resetPose) steps.Add(default, default, true, time);
        }

        // 接收当前代次的新行为片段；固定步重读与同帧重复采集均不会触发动作。
        internal void ReceiveBehavior(in CharacterBehaviorObservation frame, int currentStateVersion, int simulationVersion)
        {
            if (stateVersion != currentStateVersion) Reset(default, currentStateVersion, simulationVersion);
            if (frame.FrameNumber == 0 || frame.SimulationVersion != simulationVersion ||
                (frame.SimulationVersion == lastBehavior.SimulationVersion && frame.FrameNumber <= lastBehavior.FrameNumber)) return;
            lastBehavior = frame;
            modules.Bindings.Observe(frame);
            var before = modules.Intent.Current;
            modules.Intent.ObserveDash(frame.Behavior);
            var after = modules.Intent.Current;
            if (before.SemanticKey != after.SemanticKey) modules.Composer.ChangeBaseline(after, frame.Time);
        }

        // 采样动画更新所需输入，并处理暂停、Animator 停用与资源缺失。
        public bool Capture(CharacterControllerFrameResult latestFrame, CharacterBehaviorObservation behavior, int currentStateVersion,
            int simulationVersion, bool simulating, float deltaTime, double now, long guardGrant, CharacterMovementForecast forecast)
        {
            Join();
            if (batch.Pending) Discard();
            if (clearMotionSources)
            {
                // 外控可从片段事件重入，等到下一次采集再修改可播放图和 IK 作业。
                clearMotionSources = false;
                modules.Player.ClearMotionSources();
                Pose.ClearPose();
            }
            Receive(latestFrame, currentStateVersion, simulationVersion, simulating, now); // 补读遗漏的固定步提交，观测端负责去重。
            CharacterAnimationMotion motion = observer.Read(latestFrame, simulationVersion, simulating);
            bool validGrant = simulating && behavior.FrameNumber > 0 && behavior.SimulationVersion == simulationVersion;
#if UNITY_EDITOR
            debugTrace.CaptureSimulation(simulationVersion);
#endif
            if (animator == null || !animator.isActiveAndEnabled)
            {
                steps.Consume(steps.Count, ref batch);
                // Animator 停用时仍接收固定步，恢复时重发播放状态并重建步态相位。
                if (!animatorUnavailable)
                {
                    committedPlan = default;
                    Matcher.Reset();
                    Pose.Invalidate();
                    modules.Player.CloseGraph();
                }
                animatorUnavailable = true;
                modules.Intent.InvalidateMotion();
#if UNITY_EDITOR
                CharacterMatchDebugCapture.MarkBoundary(animator, "Animator 停用");
                debugTrace.SetStatus(CharacterAnimationDebugStatus.AnimatorUnavailable);
#endif
                return false;
            }
            animatorUnavailable = false;
            modules.Player.SetPaused(!simulating);
            bool ready = SynchronizeResources();
            if (!simulating || !ready)
            {
                steps.Consume(steps.Count, ref batch);
                Matcher.Reset();
                if (!ready) Pose.Invalidate();
                else Pose.SetFrozen(true);
                modules.Intent.InvalidateMotion();
#if UNITY_EDITOR
                CharacterMatchDebugCapture.MarkBoundary(animator, simulating ? "播放资源不可用" : "角色模拟暂停");
                if (!simulating)
                    debugTrace.SetStatus(CharacterAnimationDebugStatus.Paused);
#endif
                return false;
            }
            modules.Composer.SetTime(now);
            modules.Resources.CaptureWeights(modules.Player.CaptureFrame(now, deltaTime));
            modules.Composer.RetireChangedClips(modules.Resources);
            Matcher.CaptureCompatibility();
            var feet = motion.IsValid ? Pose.ReadFeet(motion) : default;
            Matcher.Features = Pose.ReadFeatures(feet);
            var seen = modules.Playback.Capture(committedPlan.FullBody, feet);
            var input = new CharacterPresentationInput(latestFrame, behavior.Behavior, motion, forecast, seen, feet,
                deltaTime, now, validGrant, guardGrant, Matcher.Windows);
            batch = new CharacterPresentationWork(input, steps.Count, forecast != null && steps.HasForecastSensitiveSteps(),
                currentStateVersion, simulationVersion, animator.avatar);
            return true;
        }

        // 在工作线程或当前线程完成本次试选。
        internal void Schedule(bool threaded)
        {
            if (threaded) job = Task.Run(compute);
            else
            {
                try { Plan(); }
                catch { Discard(); throw; }
                finally { modules.Composer.FinishPlan(); }
            }
        }

        // 等待后台规划退出后，才可交接或复用同一批次。
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

        // 核对本批次的代次、模型与播放图，并在暂停时冻结输出。
        internal bool CanApply(int state, int simulation, bool simulating)
        {
            if (!simulating && animator && animator.isActiveAndEnabled)
            {
                modules.Player.SetPaused(true);
                Pose.SetFrozen(true);
            }
            return simulating && animator && animator.isActiveAndEnabled && batch.Matches(state, simulation, animator.avatar) &&
                !modules.Player.NeedsRebind;
        }

        // 按计划时点提交绑定；预测敏感的步态要等最终核对后才用于选姿。
        private void Plan()
        {
            using var sample = CharacterPresentationBatch.Planning.Auto();
            modules.Composer.BeginPlan();
            steps.Consume(batch.CoveredSteps, ref batch, batch.AwaitingGaitForecast);
            var motion = batch.Input.Motion;
            modules.Bindings.UpdateGrants(batch.Input.ValidGrant, batch.Input.GuardGrant);
            if (!motion.IsValid) modules.Intent.ObserveActionVariantMotion(motion);
            modules.Bindings.SubmitGranted();
            if (!batch.AwaitingGaitForecast) PlanSelection();
        }

        // 用此批次已更新的步态历史推进表现时间和候选选择。
        private void PlanSelection()
        {
            if (batch.Input.Motion.IsValid) modules.Intent.AdvancePresentation(batch.Input.DeltaTime);
            else modules.Intent.InvalidateMotion();
            batch.Observed = batch.Input.Observation.ForPhase(modules.Intent.Phase);
            Matcher.SaveSelectionHistory();
            batch.Select(EvaluateCandidate());
        }

        // 以当前意图仲裁部位，计算下身片段及足部覆盖许可。
        private CharacterPoseOwners SelectOwners()
        {
            var selection = modules.Composer.Preview(modules.Intent.Current);
            return modules.Poses.SelectOwners(selection, batch.Observed, committedPlan, modules.Intent.UsesMovingActionVariant);
        }

        // 用本帧观测和最近提交的播放计划计算完整候选。
        private CharacterPoseCandidate EvaluateCandidate() =>
            modules.Poses.EvaluateCandidate(SelectOwners(), modules.Intent, Matcher, batch.Input, batch.Observed, committedPlan);

        // 在提交前更新本帧的预测输入，轨迹发生变化时重新选择。
        internal void RefreshForecast(CharacterMovementForecast published)
            => batch.RefreshForecast(published);

        // 在预测核对后处理尚未计划的步态批次，并提交动画组合。
        internal void Apply()
        {
            if (!batch.Pending) return;
            batch.BeginApply();
            try
            {
                if (batch.AwaitingGaitForecast)
                {
                    steps.FinishDeferredGait(true, ref batch);
                    PlanSelection();
                }
                modules.Composer.BeginPlan(); // 单次动作的完成回执随最终播放结果发布。
                ApplySelection();
                batch = default;
            }
            catch { Discard(); throw; }
        }

        // 核对单次动作回执并重新选择，完成播放下发后发布最终结果。
        private void ApplySelection()
        {
            int generation = graphGeneration;
            bool drained = modules.Player.DrainRetired(modules.Composer, modules.Intent.Current, batch.Input.Time,
                Pose, batch.Input.Motion, () => generation == graphGeneration, ref committedPlan);
            if (generation != graphGeneration) return;
            var step = modules.Player.Remaining;
            if (drained)
            {
                modules.Resources.RefreshWeights(step);
                batch.Observed = modules.Playback.Capture(committedPlan.FullBody, batch.Input.Feet).ForPhase(modules.Intent.Phase);
            }
            var preview = modules.Composer.Preview(modules.Intent.Current);
            bool reconciled = modules.Player.ReconcileOneShots(committedPlan, batch.Observed, preview, modules.Composer);
            if (reconciled) modules.Bindings.SubmitGranted();
            if (drained || reconciled || batch.NeedsSelection)
            {
                Matcher.RestoreSelectionHistory();
                batch.Select(EvaluateCandidate());
            }
            var candidate = batch.Candidate;
            var entryVelocity = modules.Intent.StopEntryVelocity;
            if (modules.Intent.ApplyRelease(candidate.Release))
                candidate = new CharacterPoseCandidate(SelectOwners(), candidate.Release, candidate.Match);
            var motion = batch.Input.Motion;
            var plan = modules.Poses.Select(candidate, modules.Intent.UsesMovingActionVariant, batch.Observed, motion, entryVelocity, committedPlan);
            var selection = candidate.Owners.Selection.WithPlaybackWeights(plan);
            var emission = modules.Player.ApplyPlan(plan, step);
            modules.Composer.PublishReceipts();
            modules.Composer.Commit(selection);
            modules.Player.PublishOneShotStarts(plan, emission, modules.Composer);
            modules.Bindings.ReleaseExpiredRequests();
            committedPlan = plan;
            Pose.Prepare(selection, motion, plan, step); // 目标许可与当前片段混合、分层遮罩同步。
            modules.Player.EvaluatePose(plan, step);
            if (generation != graphGeneration || clearMotionSources) return; // 片段事件已切换到新的运动基线。
#if UNITY_EDITOR
            var playback = debugTrace.Publish(plan, emission, modules.Player.CaptureDebugMix(), Pose.RootDebug);
            Matcher.Publish(candidate.Match, plan, playback, batch.Input.Time, batch.Input.Frame.FrameNumber);
#endif
            modules.Bindings.FlushWarnings();
            modules.Player.FlushWarnings(logContext);
        }

        // 撤销本帧回执，已结算意图仍以固定步观测完成交接。
        internal void Discard(CharacterPlaybackEndSource source = CharacterPlaybackEndSource.BatchDiscarded)
        {
            if (batch.DeferredSteps != 0) steps.FinishDeferredGait(false, ref batch);
            batch = default;
            modules.Composer.PublishReceipts(source);
            Matcher.Reset();
        }

        // 提交新单次动作并使本帧尚未提交的候选重新参与仲裁。
        public CharacterActionPlayback PlayOnce(string key, float weight, CharacterControllerFrameResult latestFrame,
            int currentStateVersion, int simulationVersion, double now)
        {
            Join();
            if (stateVersion != currentStateVersion) Reset(latestFrame, currentStateVersion, simulationVersion);
            var origin = new CharacterPlaybackOrigin(currentStateVersion, simulationVersion);
            modules.Composer.SetTime(now);
            var result = SynchronizeResources() ? modules.Bindings.PlayExplicit(key, weight, origin, modules.Composer.Time) : null;
            batch.RequestSelection(result != null);
            return result;
        }

        // 消费已结算固定步并重新建立位移及姿态历史。
        public void ResetPoseHistory()
        {
            CharacterMatchDebugCapture.MarkBoundary(animator, "重置姿态历史");
            graphGeneration++;
            Join();
            Discard();
            steps.Consume(steps.Count, ref batch);
            observer.InvalidatePosition();
            modules.Player.AfterEvaluation(ClearPoseHistory);
        }

        // 清空匹配、步态及最终骨骼修正的跨帧采样。
        private void ClearPoseHistory()
        {
            Matcher.Reset();
            modules.Intent.InvalidateMotion();
            Pose.ClearPose();
        }

        // 外控边界停止旧动作并丢弃运动历史，保留绑定、播放图和显式 IK 目标。
        internal void MotionBoundary(CharacterMotionBoundaryKind kind, int simulationVersion)
        {
            graphGeneration++;
            Join();
            var source = kind switch
            {
                CharacterMotionBoundaryKind.Teleport => CharacterPlaybackEndSource.Teleport,
                CharacterMotionBoundaryKind.Knockback => CharacterPlaybackEndSource.Knockback,
                _ => CharacterPlaybackEndSource.ExternalControl
            };
            Discard(source);
            steps.Clear();
            lastBehavior = default;
            observer.Reset(default, simulationVersion);
            modules.Intent.ResetGait(default);
            modules.Intent.ObserveActionVariantMotion(default);
            if (kind != CharacterMotionBoundaryKind.Recovery)
            {
                modules.Bindings.Reset(source);
                modules.Composer.Reset(source, forgetInstances: false);
            }
            committedPlan = default;
            clearMotionSources = true;
#if UNITY_EDITOR
            CharacterMatchDebugCapture.MarkBoundary(animator, kind);
            debugTrace.Clear();
#endif
        }

        // 校验资源与实例号，接收持续动作后更新本批次的选姿需求。
        public bool Submit(CharacterAnimationRequest request, CharacterControllerFrameResult latestFrame,
            int currentStateVersion, int simulationVersion, double now)
        {
            Join();
            modules.Composer.SetTime(now);
            if (stateVersion != currentStateVersion)
                Reset(latestFrame, currentStateVersion, simulationVersion);
            if (string.IsNullOrWhiteSpace(request.SemanticKey) || request.InstanceId <= 0 ||
                !SynchronizeResources() || !modules.Resources.CanSubmit(request.SemanticKey))
                return false;
            bool accepted = modules.Composer.Submit(request);
            batch.RequestSelection(accepted);
            return accepted;
        }

        // 按语义键和实例号撤回动作并使本批次候选失效。
        public bool Withdraw(string semanticKey, long instanceId, double now)
        {
            Join();
            modules.Composer.SetTime(now);
            bool removed = !string.IsNullOrWhiteSpace(semanticKey) && modules.Composer.Withdraw(semanticKey, instanceId);
            batch.RequestSelection(removed);
            return removed;
        }

        // 撤回指定单次回执所属的动作并使本批次候选失效。
        public bool Withdraw(CharacterActionPlayback playback, double now)
        {
            Join();
            modules.Composer.SetTime(now);
            bool removed = modules.Composer.Withdraw(playback);
            batch.RequestSelection(removed);
            return removed;
        }

        // 清空两个时间域的观察历史，等待新代次的动作与位移。
        public void Reset(CharacterControllerFrameResult latestFrame, int currentStateVersion, int simulationVersion)
        {
            graphGeneration++;
            Join();
            Discard(CharacterPlaybackEndSource.StateReset);
            steps.Clear();
            stateVersion = modules.Bindings.StateVersion = currentStateVersion;
            observer.Reset(latestFrame, simulationVersion);
            lastBehavior = default;
            modules.Intent.ResetGait(default);
            modules.Intent.ObserveActionVariantMotion(default);
            modules.Bindings.Reset(CharacterPlaybackEndSource.StateReset);
            modules.Composer.Reset(CharacterPlaybackEndSource.StateReset);
            committedPlan = default;
            modules.Player.AfterEvaluation(() => { Pose.Reset(); modules.Player.ResetPlayback(); });
#if UNITY_EDITOR
            CharacterMatchDebugCapture.MarkBoundary(animator, "角色状态重置");
            debugTrace.Clear();
#endif
        }

        // Animator 的 Avatar 或控制器占用变化时重新验证资源与播放可用状态。
        private bool SynchronizeResources()
        {
            if (modules.Player.Evaluating) return modules.Resources.Ready;
            if (modules.Player.NeedsRebind)
            {
                Discard(CharacterPlaybackEndSource.Rebind);
                modules.Intent.InvalidateMotion();
                // 资源重绑时保留持续需求及实例编号，清除上一次播放和姿态历史。
                committedPlan = default;
                Pose.Invalidate();
                modules.RefreshPlayback();
                reportedError = null;
#if UNITY_EDITOR
                CharacterMatchDebugCapture.MarkBoundary(animator, "播放图或模型重绑");
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
        public void Release(CharacterPlaybackEndSource source)
        {
#if UNITY_EDITOR
            CharacterMatchDebugCapture.MarkBoundary(animator, "角色播放图释放");
#endif
            Join();
            Discard(source);
            steps.Clear();
            graphGeneration++;
            modules.Player.AfterEvaluation(() => { Pose.Dispose(); modules.Player.Release(); });
            modules.Composer.Reset(source);
        }

#if UNITY_EDITOR
        // 读取当前动画实例已发布的播放结果。
        public CharacterAnimationDebugSnapshot ReadDebug(int currentStateVersion, int simulationVersion, bool simulating) =>
            debugTrace.Read(animator, modules.Player, stateVersion, currentStateVersion, simulationVersion, simulating);
        // 在调用方接回规划后读取本角色当前判定的诊断。
        internal string ReadDecisionTrace() => modules.ReadDecisionTrace();
#endif
    }
}
