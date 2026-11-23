using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>动画入口的序列化配置快照，在创建流水线时交给各阶段。</summary>
    internal sealed class CharacterAnimationConfiguration
    {
        public CharacterMotionDatabase PlaybackLibrary, MatchDatabase;
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
        public float IkTeleportDistance;
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
        private readonly CharacterClipPlayback clips;
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
            observer = new CharacterAnimationMotionObserver(configuration.IkTeleportDistance);
            intent = new CharacterAnimationIntentConverter(configuration);
            clips = new CharacterClipPlayback(animator, configuration);
            resources = new CharacterAnimationResourceCatalog(clips, configuration, logContext);
            Matcher = new CharacterMotionMatcher(animator, resources, configuration);
            playbackObserver = new CharacterPlaybackObserver(animator, resources, clips);
            poseSelector = new CharacterLocomotionPoseSelector(resources, configuration);
            composer = new CharacterAnimationComposer(resources.Rules);
            behaviorBindings = new CharacterBehaviorAnimationBindings(configuration.BehaviorAnimations, composer, resources, logContext);
            if (latestFrame.SimulationVersion == simulationVersion) behaviorBindings.Observe(latestFrame, seed: true);
            player = new CharacterAnimationPlayer(animator, clips, configuration);
            Ik = new CharacterAnimationIk(animator, clips, logContext.transform, configuration.IkChannels,
                configuration.IkTeleportDistance, configuration.PoseInertia, configuration.InertiaBones, configuration.MatchDatabase != null);
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
            if (observer.Receive(frame, simulationVersion, simulating))
            {
#if UNITY_EDITOR
                debugTrace.ObserveGroundStep(frame);
#endif
                var motion = observer.Read(frame, simulationVersion, simulating);
                resources.ObserveMovement(motion);
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
            if (!motion.IsValid) resources.ObserveMovement(default);
#if UNITY_EDITOR
            debugSimulationVersion = simulationVersion;
#endif
            if (animator == null || !animator.isActiveAndEnabled)
            {
                behaviorBindings.SuspendPulses();
                // Animator 停用时仍接收固定步，恢复时重发播放状态并重建步态相位。
                if (!animatorUnavailable)
                {
                    composer.ResetSelection();
                    committedPlan = default;
                    Matcher.Reset();
                    Ik.Invalidate();
                    clips.Close();
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
            behaviorBindings.SubmitGranted(); // 先交付固定步累积的开始与撤销，随后校验仍有效的请求。
            if (!resources.CanResolveActive(composer.ActiveKeys, out string invalidKey))
            {
                Ik.ClearPose();
                intent.InvalidateMotion();
                ReportError($"角色动画持续需求的资源在当前控制器中不可用：{invalidKey}。需求仲裁尚未执行，可撤销该实例或恢复有效控制器。");
                return;
            }
            if (Ik.HasTeleported) ResetPoseHistory();
            if (motion.IsValid)
                intent.AdvancePresentation(deltaTime);
            else
                intent.InvalidateMotion();
            var seen = playbackObserver.Capture(intent.Phase, committedPlan.FullBody);
            if (ResolveOneShots(seen)) behaviorBindings.SubmitGranted(); // 结束当帧即可接入持续姿势。
            var feet = Matcher.Mode == CharacterMatchMode.Rules ? default : Ik.ReadFeet(motion);
            var plan = poseSelector.Decide(intent, composer, Matcher, seen, committedPlan, motion, feet, deltaTime,
                out var selection, out var entryVelocity);
            var emission = player.Apply(plan, deltaTime);
            composer.Commit(selection);
            if (emission.FullBody) plan.FullBody.Demand.Playback?.Start(plan.FullBody.StateHash, seen.Full.Destination, clips.Available);
            if (emission.Overlay) plan.Overlay.Demand.Playback?.Start(plan.Overlay.StateHash, seen.Overlay.Destination, clips.Available);
            if (emission.LowerBody) plan.LowerBody.Demand.Playback?.Start(plan.LowerBody.StateHash, seen.Lower.Destination, clips.Available);
            if (emission.UpperBody) plan.UpperBody.Demand.Playback?.Start(plan.UpperBody.StateHash, seen.Upper.Destination, clips.Available);
            behaviorBindings.ReleaseExpiredRequests();
            committedPlan = plan;
            Ik.Prepare(selection, motion, plan); // 目标许可与当前片段混合、分层遮罩同步。
            clips.Evaluate();
#if UNITY_EDITOR
            var handoff = new CharacterDebugHandoff(latestFrame.Behavior, clips);
            debugTrace.Record(motion, intent.Phase, intent.PhaseReason, selection, plan, emission,
                seen.LeftSupport, entryVelocity, handoff);
#endif
        }

        private bool ResolveOneShots(in CharacterPlaybackObservation seen)
        {
            if (committedPlan.FullBody.Demand.Playback == null && committedPlan.UpperBody.Demand.Playback == null &&
                committedPlan.LowerBody.Demand.Playback == null && committedPlan.Overlay.Demand.Playback == null) return false;
            var selected = composer.Preview(intent.Current);
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
                selected.Playback != playback || !layer.Ready) return false;
            var status = playback.Observe(previous.StateHash, layer.Destination);
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
            Matcher.Reset();
            intent.InvalidateMotion();
            observer.InvalidateAcceleration();
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
            bool ready = resources.EnsureResources(out bool controllerChanged, out string error);
            if (controllerChanged)
            {
                intent.InvalidateMotion();
                Matcher.Reset();
                // 更换控制器只清播放和选择历史，保留持续需求及实例编号。
                committedPlan = default;
                Ik.Invalidate();
                player.InvalidateController();
                composer.ResetSelection();
                reportedError = null;
#if UNITY_EDITOR
                debugController = clips.Controller;
                debugTrace.Clear();
#endif
            }
            if (!ready)
            {
                if (error != null)
                    ReportError(error);
#if UNITY_EDITOR
                else
                    debugTrace.SetStatus(CharacterAnimationDebugStatus.ResourceUnavailable, "未配置 Animator Controller。");
#endif
            }
            return ready;
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
                debugController == clips.Controller;
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
