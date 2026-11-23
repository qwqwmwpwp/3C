using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>动画入口的序列化配置快照，在创建流水线时交给各阶段。</summary>
    internal sealed class CharacterAnimationConfiguration
    {
        public CharacterMotionDatabase PlaybackLibrary, MatchDatabase;
        public CharacterLightAttackSettings AttackSettings;
        public float MatchMargin, MatchReverseHold, MatchTurnAngle;
        public int FullLayer;
        public int OverlayLayer = -1;
        public int LowerLayer;
        public int UpperLayer;
        public int ValidOverlayLayer => OverlayLayer > FullLayer && (LowerLayer < 0 || OverlayLayer < LowerLayer)
            && (UpperLayer < 0 || OverlayLayer < UpperLayer) ? OverlayLayer : -1;
        public string IdleState;
        public string MoveState;
        public string WalkState;
        public string SprintState;
        public string DashState;
        public int LocomotionPriority;
        public int DashPriority;
        public float Transition;
        public float DashTransition;
        public float IdleEnterSpeed;
        public float IdleExitSpeed;
        public float RunEnterSpeed;
        public float RunExitSpeed;
        public float SprintEnterSpeed;
        public float SprintExitSpeed;
        public float WalkSpeed;
        public float RunSpeed;
        public float SprintSpeed;
        public float DashClipSpeed;
        public float LocomotionStartSeconds;
        public float LocomotionTurnSeconds;
        public float LocomotionReverseAngle;
        public string WalkStartState;
        public float WalkStartNormalizedTime;
        public string RunStartState;
        public AnimationClip RunStartClip;
        public float WalkToRunBlendSeconds;
        public float RunStartNormalizedTime;
        public string TurnLeftState;
        public string TurnRightState;
        public float MinimumStopTravel;
        public CharacterStopAnimationSettings WalkStop;
        public CharacterStopAnimationSettings RunStop;
        public CharacterStopAnimationSettings SprintStop;
        public CharacterLocomotionGaitPhaseSettings[] LocomotionGaitPhases;
        public CharacterAnimationActionSettings[] Actions;
        public CharacterBehaviorAnimationMapping[] BehaviorAnimations;
        public CharacterBoundIkChannel[] IkChannels;
        public CharacterInertiaTuning PoseInertia;
        public CharacterInertiaBones InertiaBones;
        public float IkTeleportDistance, WalkableSlope;
    }

    /// <summary>只编排观测、语义选择、仲裁、资源解析与播放，统一生命周期与控制器缓存失效。</summary>
    internal sealed class CharacterAnimationPipeline
    {
        private readonly Animator animator;
        private readonly UnityEngine.Object logContext;
        private readonly CharacterAnimationMotionObserver observer;
        private readonly CharacterAnimationIntentConverter intent;
        private readonly CharacterAnimationComposer composer;
        private readonly CharacterBehaviorAnimationBindings behaviorBindings;
        private readonly CharacterAnimationResourceCatalog resources;
        private readonly CharacterPlaybackObserver playbackObserver;
        private readonly CharacterLocomotionPoseSelector poseSelector;
        private CharacterAnimationPlaybackPlan committedPlan;
        private readonly CharacterAnimationPlayer player;
        public CharacterAnimationIk Ik { get; }
        public CharacterMotionMatcher Matcher { get; }
        private int stateVersion;
        private string reportedError;
        private bool animatorUnavailable;
#if UNITY_EDITOR
        internal CharacterAnimationLayerPlan DisplayedBase => committedPlan.FullBody;
        private readonly CharacterAnimationDebugTrace debugTrace = new CharacterAnimationDebugTrace();
        private RuntimeAnimatorController debugController;
        private int debugSimulationVersion;
#endif

        public CharacterAnimationPipeline(Animator animator, CharacterAnimationConfiguration configuration,
            CharacterBehaviorAnimator logContext, CharacterControllerFrameResult latestFrame, int currentStateVersion,
            int simulationVersion)
        {
            this.animator = animator;
            this.logContext = logContext;
            observer = new CharacterAnimationMotionObserver(logContext.transform, configuration.IkTeleportDistance);
            intent = new CharacterAnimationIntentConverter(configuration);
            player = new CharacterAnimationPlayer(animator, configuration);
            resources = new CharacterAnimationResourceCatalog(player, configuration, logContext);
            Matcher = new CharacterMotionMatcher(animator, resources, configuration);
            playbackObserver = new CharacterPlaybackObserver(animator, resources, player);
            poseSelector = new CharacterLocomotionPoseSelector(resources, configuration);
            composer = new CharacterAnimationComposer(resources.Rules, resources.HasBinding);
            behaviorBindings = new CharacterBehaviorAnimationBindings(configuration.BehaviorAnimations, composer, resources, logContext);
            if (latestFrame.SimulationVersion == simulationVersion) behaviorBindings.Observe(latestFrame, seed: true);
            Ik = new CharacterAnimationIk(animator, player, logContext.transform, configuration.IkChannels,
                configuration.WalkableSlope, configuration.PoseInertia, configuration.InertiaBones, configuration.MatchDatabase != null);
            stateVersion = currentStateVersion;
            observer.Reset(latestFrame, simulationVersion);
            intent.Reset(latestFrame.Behavior);
            SynchronizeResources();
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
            if (stateVersion != currentStateVersion)
                Reset(default, currentStateVersion, simulationVersion);
            bool received = observer.Receive(frame, simulationVersion, simulating, out bool resetPose);
            if (resetPose) ClearPoseHistory();
            if (received)
            {
#if UNITY_EDITOR
                debugTrace.ObserveGroundStep(frame);
#endif
                var motion = observer.Read(frame, simulationVersion, simulating);
                if (motion.IsValid) resources.ObserveMovement(motion);
                behaviorBindings.Observe(frame);
                intent.Receive(motion, frame.Behavior, frame.Input, frame.DeltaTime, behaviorBindings.PreserveEntryGait);
            }
        }

        public void Update(CharacterControllerFrameResult latestFrame, int currentStateVersion,
            int simulationVersion, bool simulating, float deltaTime, long guardGrant)
        {
            Receive(latestFrame, currentStateVersion, simulationVersion, simulating); // 补读遗漏的固定步提交，观测端负责去重。
            CharacterAnimationMotion motion = observer.Read(latestFrame, simulationVersion, simulating);
            behaviorBindings.UpdateGrants(simulating && latestFrame.SimulationVersion == simulationVersion, guardGrant);
            if (!motion.IsValid && !motion.RetainedStrideSpeed.HasValue) resources.ObserveMovement(default);
#if UNITY_EDITOR
            debugSimulationVersion = simulationVersion;
#endif
            if (animator == null || !animator.isActiveAndEnabled)
            {
                behaviorBindings.SuspendPulses();
                // Animator 停用时仍接收固定步，恢复时重发播放状态并重建步态相位。
                if (!animatorUnavailable)
                {
                    behaviorBindings.RebindGrantedEntries();
                    composer.ResetSelection();
                    committedPlan = default;
                    Matcher.Reset();
                    Ik.Invalidate();
                    player.CloseGraph();
                }
                animatorUnavailable = true;
                intent.InvalidateMotion();
#if UNITY_EDITOR
                debugTrace.SetStatus(CharacterAnimationDebugStatus.AnimatorUnavailable);
#endif
                return;
            }
            animatorUnavailable = false;
            player.SetPaused(!simulating);
            bool ready = SynchronizeResources();
            if (!simulating || !ready)
            {
                behaviorBindings.SuspendPulses();
                Matcher.Reset();
                if (!ready) Ik.Invalidate();
                else Ik.SetFrozen(true);
                intent.InvalidateMotion();
#if UNITY_EDITOR
                if (!simulating)
                    debugTrace.SetStatus(CharacterAnimationDebugStatus.Paused);
#endif
                return;
            }
            behaviorBindings.SubmitGranted();
            if (motion.IsValid)
                intent.AdvancePresentation(deltaTime);
            else
                intent.InvalidateMotion();
            var feet = motion.IsValid ? Ik.ReadFeet(motion) : default;
            var seen = playbackObserver.Capture(intent.Phase, committedPlan.FullBody, feet);
            var selection = composer.Preview(intent.Current);
            if (ResolveOneShots(seen, selection))
            {
                behaviorBindings.SubmitGranted(); // 单次动作结束时，同帧接入持续姿势并重新仲裁。
                selection = composer.Preview(intent.Current);
            }
            var lower = poseSelector.SelectLower(selection.LowerBody, seen, committedPlan.LowerBody, deltaTime, out bool lowerCoversFeet);
            var release = poseSelector.EvaluateRelease(intent, Matcher, selection.FullBody, lowerCoversFeet, seen, committedPlan,
                motion, feet, deltaTime, out var match);
            Vector3 entryVelocity = intent.StopEntryVelocity;
            if (intent.ApplyRelease(release))
            {
                selection = composer.Preview(intent.Current);
                if (selection.LowerBody.Key != lower.Demand.Key || selection.LowerBody.InstanceId != lower.Demand.InstanceId)
                    lower = poseSelector.SelectLower(selection.LowerBody, seen, committedPlan.LowerBody, deltaTime, out lowerCoversFeet);
            }
            var plan = poseSelector.Select(selection, lower, lowerCoversFeet, seen, motion, entryVelocity, committedPlan, release, match);
            selection = new CharacterAnimationComposition(fullBody: selection.FullBody, upperBody: plan.UpperBody.Demand,
                lowerBody: plan.LowerBody.Demand, overlay: plan.Overlay.Demand, conflict: selection.Conflict); // 局部层采用本帧实际交接权重。
            var emission = player.Active.Apply(plan, deltaTime);
            composer.Commit(selection);
            if (emission.FullBody) plan.FullBody.Demand.Playback?.Start();
            if (emission.Overlay) plan.Overlay.Demand.Playback?.Start();
            if (emission.LowerBody) plan.LowerBody.Demand.Playback?.Start();
            if (emission.UpperBody) plan.UpperBody.Demand.Playback?.Start();
            behaviorBindings.ReleaseExpiredRequests();
            committedPlan = plan;
            Ik.Prepare(selection, motion, plan); // 目标许可与当前片段混合、分层遮罩同步。
            player.Direct?.Evaluate();
#if UNITY_EDITOR
            var handoff = new CharacterDebugHandoff(latestFrame.Behavior, player, resources.GetLayer(CharacterAnimationBodyRegion.FullBodyOverlay));
            debugTrace.Record(motion, intent.Phase, intent.PhaseReason, selection, plan, emission,
                seen.LeftSupport, entryVelocity, handoff);
#endif
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
                Debug.LogWarning($"单次动作 {playback.Key} 绑定了循环状态 {previous.StateName}，已中止。", logContext);
            return composer.End(playback.Key, playback.InstanceId, status);
        }

        public bool HasBehaviorMapping(CharacterBehaviorId id) => behaviorBindings.Contains(id);

        public CharacterActionPlayback PlayOnce(string key, float weight, CharacterControllerFrameResult latestFrame,
            int currentStateVersion, int simulationVersion)
        {
            if (stateVersion != currentStateVersion) Reset(latestFrame, currentStateVersion, simulationVersion);
            return SynchronizeResources() && resources.CanPlayOnce(key) ? composer.PlayOnce(key, weight) : null;
        }

        public void ResetPoseHistory()
        {
            observer.InvalidatePosition();
            ClearPoseHistory();
        }

        private void ClearPoseHistory()
        {
            Matcher.Reset();
            intent.InvalidateMotion();
            Ik.ClearPose();
        }

        public bool Submit(CharacterAnimationRequest request, CharacterControllerFrameResult latestFrame,
            int currentStateVersion, int simulationVersion)
        {
            if (stateVersion != currentStateVersion)
                Reset(latestFrame, currentStateVersion, simulationVersion);
            if (string.IsNullOrWhiteSpace(request.SemanticKey) || request.InstanceId <= 0 ||
                !SynchronizeResources() || !resources.CanSubmit(request.SemanticKey))
                return false;
            return composer.Submit(request);
        }

        public bool Withdraw(string semanticKey, long instanceId)
        {
            return !string.IsNullOrWhiteSpace(semanticKey) && composer.Withdraw(semanticKey, instanceId);
        }

        public bool Withdraw(CharacterActionPlayback playback) => composer.Withdraw(playback);

        public void Reset(CharacterControllerFrameResult latestFrame, int currentStateVersion, int simulationVersion)
        {
            stateVersion = currentStateVersion;
            Matcher.Reset();
            observer.Reset(latestFrame, simulationVersion);
            intent.Reset(latestFrame.Behavior);
            behaviorBindings.Reset();
            resources.ObserveMovement(default);
            composer.Reset();
            committedPlan = default;
            Ik.Reset();
            player.ResetPlayback();
#if UNITY_EDITOR
            debugTrace.Clear();
#endif
        }

        private bool SynchronizeResources()
        {
            if (player.NeedsRebind)
            {
                intent.InvalidateMotion();
                Matcher.Reset();
                // 更换控制器只清播放和选择历史，保留持续需求及实例编号。
                committedPlan = default;
                Ik.Invalidate();
                player.BindController();
                resources.RefreshBindings();
                behaviorBindings.RebindGrantedEntries();
                composer.ResetSelection();
                reportedError = null;
                if (resources.Ready)
                {
                    foreach (var key in composer.ActiveKeys)
                        if (!resources.HasBinding(key))
                            Debug.LogWarning($"当前控制器缺少动作 {key}，持续请求将在资源恢复后参与选择。", logContext);
                }
#if UNITY_EDITOR
                debugController = player.Controller;
                debugTrace.Clear();
#endif
            }
            if (!resources.Ready)
            {
                if (resources.ValidationError != null)
                    ReportError(resources.ValidationError);
#if UNITY_EDITOR
                else
                    debugTrace.SetStatus(CharacterAnimationDebugStatus.ResourceUnavailable, "未配置 Animator Controller。");
#endif
            }
            return resources.Ready;
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
            Ik.Dispose();
            player.Release();
            composer.Reset();
        }

#if UNITY_EDITOR
        public CharacterAnimationDebugSnapshot ReadDebug(int currentStateVersion, int simulationVersion, bool simulating,
            out ICharacterAnimationDebugHistory history)
        {
            bool matchingOwner = stateVersion == currentStateVersion && animator != null &&
                debugController == player.Controller;
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
