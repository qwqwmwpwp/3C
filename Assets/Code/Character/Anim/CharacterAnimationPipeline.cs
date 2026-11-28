using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>只编排观测、语义选择、仲裁、资源解析与播放，统一播放图的生命周期与资源缓存失效。</summary>
    internal sealed class CharacterAnimationPipeline
    {
        private readonly Animator animator;
        private readonly CharacterBehaviorAnimator logContext;
        private readonly CharacterAnimationMotionObserver observer;
        private readonly CharacterAnimationModules modules;
        private CharacterAnimationPlaybackPlan committedPlan;
        public CharacterPosePipeline Pose => modules.Pose;
        private CharacterStrategySession Strategy => modules.Strategy;
        private readonly CharacterAnimationStepHistory steps;
        private CharacterPresentationWork batch;
        private readonly CharacterPresentationJob planning;
        private long deliveredEndVersion;
        private int stateVersion;
        internal double? EvaluationTime => modules.Composer.EvaluatingAt;
        private bool animatorUnavailable;
        private bool clearMotionSources, resetPlaybackTime;
        private int graphGeneration;
#if UNITY_EDITOR
        private readonly CharacterAnimationDebugTrace debugTrace = new CharacterAnimationDebugTrace();
#endif

        // 从最近运动步恢复轨迹，并用当前表现状态初始化步态。
        public CharacterAnimationPipeline(Animator animator, CharacterAnimationConfiguration configuration,
            CharacterBehaviorAnimator logContext, CharacterAnimationMovement latestFrame, CharacterAnimationContext context,
            int currentStateVersion, int simulationVersion, bool requirePlayable)
        {
            this.animator = animator;
            this.logContext = logContext;
            observer = new CharacterAnimationMotionObserver(logContext.transform, configuration.IkTeleportDistance);
            modules = new CharacterAnimationModules(animator, logContext, configuration, requirePlayable);
            planning = new CharacterPresentationJob(Plan, () => Discard(), modules.Composer.FinishPlan);
            steps = new CharacterAnimationStepHistory(modules);
            stateVersion = currentStateVersion;
            observer.Detail = Strategy.Detail;
            observer.Reset(latestFrame, simulationVersion);
            SynchronizeResources();
            Strategy.Detail?.ObserveContext(context);
        }

        // 核对当前播放库与模块影响，旧批次可在校验期间继续完成规划。
        internal AnimationModuleChanges PrepareModules(CharacterAnimationConfiguration config, bool refreshContents) =>
            modules.Prepare(config, refreshContents);

        // 交接旧配置下的固定步后重新装配模块。
        internal void Reconfigure(CharacterAnimationConfiguration config, AnimationModuleChanges changes, CharacterAnimationContext context, double time)
        {
            Join();
            if (batch.Pending) Discard(CharacterPlaybackEndSource.Rebind);
            steps.Consume(steps.Count, batch.Input);
            modules.Composer.SetTime(time);
            graphGeneration++;
            if (changes.GroundChanged) committedPlan = committedPlan.WithoutGround();
            Strategy.MarkBoundary(changes.GroundChanged ? "地面库或规则重新装配" : null);
            modules.Reconfigure(config, changes, context);
            observer.Detail = Strategy.Detail;
            observer.ConfigureTeleport(config.IkTeleportDistance);
        }

        // 瞬移切断旧候选与预测入点，保留独立动作的时钟。
        private void ResetGroundHistory()
        {
            graphGeneration++;
            Strategy.MarkBoundary("地面运动观测不连续");
            Discard();
            committedPlan = committedPlan.WithoutGround();
            modules.Player.AfterEvaluation(Pose.ClearPose);
        }

        // 只向当前行为代次提供运动观测。
        public CharacterAnimationMotion ReadMotion(CharacterAnimationMovement latestFrame, int currentStateVersion,
            int simulationVersion, bool simulating) => currentStateVersion == stateVersion
            ? observer.Read(latestFrame, simulationVersion, simulating) : default;

        // 记录已结算的固定步及外部来源提供的表现轨迹，供步态历史按原顺序消费。
        public void Receive(CharacterAnimationMovement frame, int simulationVersion, bool simulating, double time)
        {
            Join();
            bool received = observer.Receive(frame, simulationVersion, simulating, out bool resetPose);
            if (resetPose) ResetGroundHistory();
            if (received)
                steps.Add(frame, observer.Read(frame, simulationVersion, simulating), resetPose, time);
            else if (resetPose) steps.Add(default, default, true, time);
        }

        // 交接规划后立即应用已批准的表现覆盖，最终提交使用最新状态。
        internal void ReceiveContext(in CharacterAnimationContext context, double time)
        {
            Join();
            var before = Strategy.Instance.GetIntent();
            Strategy.Detail?.ObserveContext(context);
            var after = Strategy.Instance.GetIntent();
            if (before.SemanticKey != after.SemanticKey || before.InstanceId != after.InstanceId)
                modules.Composer.ChangeBaseline(after, time);
            if (batch.Pending) batch.RefreshContext(context);
        }

        // 采样动画更新所需输入，并处理暂停、Animator 停用与资源缺失。
        public bool Capture(CharacterAnimationMovement latestFrame, CharacterAnimationContext context, int currentStateVersion,
            int simulationVersion, bool simulating, float deltaTime, double now, AnimationTrajectoryPublication forecast)
        {
            Join();
            if (batch.Pending) Discard();
            if (clearMotionSources)
            {
                // 外控可从片段事件重入，等到下一次采集再修改可播放图和 IK 作业。
                clearMotionSources = false;
                modules.Player.ClearMotionSources(resetPlaybackTime);
                resetPlaybackTime = false;
                Pose.ClearPose();
            }
            Receive(latestFrame, simulationVersion, simulating, now); // 补读遗漏的固定步提交，观测端负责去重。
            CharacterAnimationMotion motion = observer.Read(latestFrame, simulationVersion, simulating);
#if UNITY_EDITOR
            debugTrace.CaptureSimulation(simulationVersion);
#endif
            if (animator == null || !animator.isActiveAndEnabled)
            {
                SynchronizeResources(force: !animator && !animatorUnavailable);
                steps.Consume(steps.Count, batch.Input);
                // Animator 停用时仍接收固定步，恢复时重发播放状态并重建步态相位。
                if (!animatorUnavailable)
                {
                    committedPlan = default;
                    Strategy.InvalidateMotion();
                    Pose.Invalidate();
                    modules.Player.CloseGraph();
                }
                else Strategy.Detail?.InvalidateMotion();
                animatorUnavailable = true;

#if UNITY_EDITOR
                Strategy.MarkBoundary("Animator 停用");
                debugTrace.SetStatus(CharacterAnimationDebugStatus.AnimatorUnavailable);
#endif
                return false;
            }
            animatorUnavailable = false;
            modules.Player.SetPaused(!simulating);
            bool ready = SynchronizeResources();
            if (!simulating || !ready)
            {
                steps.Consume(steps.Count, batch.Input);
                Strategy.Detail?.InvalidateMotion();
                if (ready && !simulating) Pose.SetFrozen(true);
                if (!ready) Pose.Invalidate();

#if UNITY_EDITOR
                Strategy.MarkBoundary(simulating ? "播放资源不可用" : "角色模拟暂停");
                if (!simulating)
                    debugTrace.SetStatus(CharacterAnimationDebugStatus.Paused);
#endif
                return false;
            }
            modules.Composer.SetTime(now);
            modules.Resources.CaptureWeights(modules.Player.CaptureFrame(now, deltaTime));
            modules.Composer.RetireChangedClips(modules.Resources);
            var feet = motion.IsValid ? Pose.ReadFeet(motion) : default;
            Strategy.Detail?.Capture(Pose, feet);
            var seen = modules.Playback.Capture(committedPlan.FullBody, feet);
            var input = new CharacterPresentationInput(latestFrame, context, motion, forecast, seen, feet,
                deltaTime, now, Pose.ReadStrategyBones(motion), Strategy.Setup);
            steps.CaptureTrajectories(forecast, steps.Count);
            batch = new CharacterPresentationWork(input, steps.Count, steps.HasForecastSensitiveSteps(),
                currentStateVersion, simulationVersion, animator.avatar);
            return true;
        }

        // 交由此角色独占的规划任务执行本批候选选择。
        internal void Schedule(bool threaded) => planning.Schedule(threaded);

        // 发布或复用可变模块前在主线程收回当前批次。
        internal void Join() => planning.Join();

        // 在片段事件触发暂停时等图退出本段求值，再冻结 Animator 和姿态缓存。
        internal void Freeze()
        {
            int generation = graphGeneration;
            modules.Player.AfterEvaluation(() =>
            {
                if (generation != graphGeneration || !animator || !animator.isActiveAndEnabled) return;
                modules.Player.SetPaused(true);
                Pose.SetFrozen(true);
            });
        }

        // 核对本批次的连续性、模型与播放图，暂停期间不提交旧候选。
        internal bool CanApply(int state, int simulation, bool simulating) =>
            simulating && animator && animator.isActiveAndEnabled && SynchronizeResources() && batch.Matches(state, simulation, animator.avatar);

        // 按计划时点提交绑定；预测敏感的步态要等最终核对后才用于选姿。
        private void Plan()
        {
            using var sample = CharacterPresentationBatch.Planning.Auto();
            modules.Composer.BeginPlan();
            if (!batch.AwaitingGaitForecast) PlanSelection();
        }

        // 按序交付固定步并只推进一次表现时钟，后续同帧重选复用其结果。
        private void PlanSelection()
        {
            bool project = batch.AwaitingGaitForecast;
            batch.ClaimSteps();
            steps.Consume(batch.CoveredSteps, batch.Input, project);
            Strategy.Detail?.BeginFrame(batch.Input);
            batch.Observed = batch.Input.Observation;
            SelectPose();
        }

        // 用当前仲裁、冻结观测和已提交的历史试选本批姿势。
        private void SelectPose()
        {
            Strategy.Select(batch.Input, batch.Observed, committedPlan, modules.Composer);
            batch.Selected();
        }

        // 在提交前更新本帧的预测输入，轨迹发生变化时重新选择。
        internal void RefreshForecast(AnimationTrajectoryPublication published)
        {
            if (batch.Pending && batch.RefreshForecast(published) && !batch.StepsConsumed)
                steps.CaptureTrajectories(published, batch.CoveredSteps);
        }

        // 在预测核对后处理尚未计划的步态批次，并提交动画组合。
        internal void Apply()
        {
            if (!batch.Pending) return;
            try
            {
                if (batch.AwaitingGaitForecast)
                {
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
            // 片段和终态监听者都可能重置、暂停、换模或拆图，继续选姿前须保留原绑定。
            bool Current()
            {
                if (generation != graphGeneration) return false;
                if (!animator) { SynchronizeResources(force: true); return false; }
                if (!animator.isActiveAndEnabled || logContext.HasPendingConfiguration)
                {
                    Discard(CharacterPlaybackEndSource.Rebind);
                    return false;
                }
                if (!modules.Player.NeedsRebind) return true;
                SynchronizeResources();
                return false;
            }

            // 只消费已经到达本批截止时刻的预约，尚未生效的请求留给后续批次。
            (bool NeedsSelection, long PublishedEndVersion) Drain() => modules.Player.DrainRetired(
                modules.Composer, Strategy.Instance.GetIntent(), batch.Input.Time, Pose, batch.Input.Motion, Current, ref committedPlan);

            var settled = Drain();
            if (!Current()) return;
            bool changed = settled.NeedsSelection;
            if (settled.PublishedEndVersion != deliveredEndVersion)
            {
                deliveredEndVersion = settled.PublishedEndVersion;
                logContext.PublishPlaybackSettled();
                if (!Current()) return;
                // 本批只通知一次；回调追加的动作和撤销在最终选姿前生效，其新终态下批通知。
                Drain();
                if (!Current()) return;
                changed = true;
            }
            RefreshForecast(logContext.CurrentTrajectory);
            var step = modules.Player.Remaining;
            if (changed)
            {
                modules.Resources.RefreshWeights(step);
                batch.Observed = modules.Playback.Capture(committedPlan.FullBody, batch.Input.Feet);
            }
            if (changed || batch.NeedsSelection) SelectPose();
            var strategy = Strategy;
            var plan = strategy.Plan;
            var motion = strategy.PoseMotion;
            var selection = strategy.Owners.WithPlaybackWeights(plan);
            batch.BeginApply();
            var emission = modules.Player.ApplyPlan(plan, step);
            modules.Composer.PublishReceipts();
            if (!Current()) return;
            modules.Composer.Commit(selection);
            modules.Player.PublishOneShotStarts(plan, emission, modules.Composer);
            committedPlan = plan;
            Pose.Prepare(selection, motion, plan, step); // 目标许可与当前片段混合、分层遮罩同步。
            bool evaluated = modules.Player.EvaluatePose(plan, step, modules.Composer, batch.Input.Time);
            if (!evaluated || !Current() || clearMotionSources) return; // 片段事件已切换到新的运动或模型基线。
            strategy.Commit(modules.Player);
#if UNITY_EDITOR
            var playback = debugTrace.Publish(plan, emission, modules.Player.CaptureDebugMix(), Pose.RootDebug);
            strategy.Detail?.Publish(playback, batch.Input.Time, batch.Input.Frame.FrameNumber);
#endif
        }

        // 撤销本帧回执，已结算意图仍以固定步观测完成交接。
        internal void Discard(CharacterPlaybackEndSource source = CharacterPlaybackEndSource.BatchDiscarded)
        {
            if (batch.Pending && !batch.StepsConsumed)
            {
                batch.ClaimSteps();
                steps.Consume(batch.CoveredSteps, batch.Input);
            }
            batch = default;
            modules.Composer.PublishReceipts(source);
        }

        // 提交新单次动作并使本帧尚未提交的候选重新参与仲裁。
        internal CharacterActionPlayback PlayOnce(in CharacterOneShotRequest request, double? at, double now)
        {
            Join();
            modules.Composer.SetTime(now);
            if (!SynchronizeResources()) return null;
            modules.Resources.CaptureOneShots();
            var result = modules.Composer.PlayOnce(request, modules.Resources, at ?? modules.Composer.Time);
            batch.RequestSelection(result != null);
            return result;
        }

        // 分配本动作键的新持续实例，成功时返回可更新或撤回的请求。
        internal CharacterAnimationRequest Hold(string key, float weight, double now)
        {
            Join();
            modules.Composer.SetTime(now);
            if (!SynchronizeResources() || !modules.Resources.CanSubmit(key)) return default;
            var request = modules.Composer.Hold(key, weight);
            batch.RequestSelection(request.InstanceId > 0);
            return request;
        }

        // 给指定单次播放安排淡出，或在指定时点取消。
        internal bool EndPlayback(CharacterActionPlayback playback, double? at, float? fade, double now)
        {
            Join();
            modules.Composer.SetTime(now);
            double time = at ?? modules.Composer.Time;
            bool changed = fade.HasValue ? modules.Composer.FadeOut(playback, time, fade.Value)
                : modules.Composer.Cancel(playback, time);
            batch.RequestSelection(changed);
            return changed;
        }

        // 消费已结算固定步并重新建立位移及姿态历史。
        public void ResetPoseHistory()
        {
            Strategy.MarkBoundary("重置姿态历史");
            graphGeneration++;
            Join();
            Discard();
            steps.Consume(steps.Count, batch.Input);
            observer.InvalidatePosition();
            modules.Player.AfterEvaluation(ClearPoseHistory);
        }

        // 清空匹配、步态及最终骨骼修正的跨帧采样。
        private void ClearPoseHistory()
        {
            Strategy.InvalidateMotion();
            Pose.ClearPose();
        }

        // 交接暂停或时间轴换代之前的工作，下一步重新建立运动基线。
        internal void ContinuityChanged(int simulationVersion, CharacterPlaybackEndSource source = CharacterPlaybackEndSource.BatchDiscarded)
        {
            graphGeneration++;
            Join();
            Discard(source);
            steps.Clear();
            observer.Reset(default, simulationVersion);
            Strategy.Instance.Reset();
            modules.Player.AfterEvaluation(Pose.ClearStrategyObservations);
        }

        // 外控边界停止旧动作并丢弃运动历史，保留绑定、播放图和显式 IK 目标。
        internal void MotionBoundary(CharacterPlaybackEndSource source, bool cancelActions, int simulationVersion)
        {
            ContinuityChanged(simulationVersion, source);
            if (cancelActions) modules.Composer.Reset(source);
            committedPlan = default;
            clearMotionSources = true;
            resetPlaybackTime |= cancelActions;
#if UNITY_EDITOR
            Strategy.MarkBoundary("运动连续性已切换");
            debugTrace.Clear();
#endif
        }

        // 校验资源与实例号，接收持续动作后更新本批次的选姿需求。
        public bool Submit(CharacterAnimationRequest request, double now)
        {
            Join();
            modules.Composer.SetTime(now);
            if (string.IsNullOrWhiteSpace(request.SemanticKey) || request.InstanceId <= 0 ||
                !SynchronizeResources() || !modules.Resources.CanSubmit(request.SemanticKey))
                return false;
            bool accepted = modules.Composer.Submit(request);
            batch.RequestSelection(accepted);
            return accepted;
        }

        // 接回后台规划后读取动作实例的真实归属。
        internal bool HasAction(string key, long instanceId)
        {
            Join();
            return modules.Composer.HasAction(key, instanceId);
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

        // 清空两个时间域的观察历史，等待新代次的动作与位移。
        public void Reset(int currentStateVersion, int simulationVersion)
        {
            graphGeneration++;
            Join();
            Discard(CharacterPlaybackEndSource.StateReset);
            steps.Clear();
            stateVersion = currentStateVersion;
            observer.Reset(default, simulationVersion);
            Strategy.Instance.Reset();
            modules.Composer.Reset(CharacterPlaybackEndSource.StateReset);
            committedPlan = default;
            modules.Player.AfterEvaluation(() => { Pose.Reset(); modules.Player.ResetPlayback(); });
#if UNITY_EDITOR
            Strategy.MarkBoundary("角色状态重置");
            debugTrace.Clear();
#endif
        }

        // Animator 的 Avatar 或控制器占用变化时重新验证资源与播放可用状态。
        private bool SynchronizeResources(bool force = false)
        {
            if (modules.Player.Evaluating) return modules.Resources.Ready;
            if (force || modules.Player.NeedsRebind)
            {
                graphGeneration++;
                Strategy.InvalidateMotion();
                // 资源重绑时保留持续需求及实例编号，清除上一次播放和姿态历史。
                committedPlan = default;
                Pose.Invalidate();
                modules.RefreshPlayback();
                Discard(modules.Resources.Ready ? CharacterPlaybackEndSource.Rebind : CharacterPlaybackEndSource.ResourceUnavailable);
#if UNITY_EDITOR
                Strategy.MarkBoundary("播放图或模型重绑");
                debugTrace.Clear();
#endif
            }
#if UNITY_EDITOR
            if (!modules.Resources.Ready)
                debugTrace.SetStatus(CharacterAnimationDebugStatus.ResourceUnavailable, modules.Resources.ValidationError);
#endif
            return modules.Resources.Ready;
        }

        // 销毁播放图并释放该角色最后一次诊断结果。
        public void Release(CharacterPlaybackEndSource source)
        {
#if UNITY_EDITOR
            Strategy.MarkBoundary("角色播放图释放");
#endif
            Join();
            Discard(source);
            steps.Clear();
            Strategy.Instance.Reset();
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
