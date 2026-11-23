using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>动画入口的序列化配置快照，在创建流水线时交给各阶段。</summary>
    internal sealed class CharacterAnimationConfiguration
    {
        public CharacterMotionDatabase MatchDatabase;
        public float MatchMargin, MatchReverseHold;
        public int FullLayer;
        public int LowerLayer;
        public int UpperLayer;
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
        public CharacterIkChannel[] IkChannels;
        public CharacterPoseInertiaSettings PoseInertia;
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
            observer = new CharacterAnimationMotionObserver(configuration.IkTeleportDistance);
            intent = new CharacterAnimationIntentConverter(configuration);
            resources = new CharacterAnimationResourceCatalog(animator, configuration, logContext);
            Matcher = new CharacterMotionMatcher(animator, resources, configuration);
            playbackObserver = new CharacterPlaybackObserver(animator, resources);
            poseSelector = new CharacterLocomotionPoseSelector(resources, configuration);
            composer = new CharacterAnimationComposer(resources.Rules);
            player = new CharacterAnimationPlayer(animator, configuration.FullLayer, configuration.LowerLayer,
                configuration.UpperLayer);
            Ik = new CharacterAnimationIk(animator, logContext.transform, configuration.IkChannels,
                configuration.IkTeleportDistance, configuration.PoseInertia, configuration.MatchDatabase != null);
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
                intent.Receive(observer.Read(frame, simulationVersion, simulating), frame.Behavior,
                    frame.Input, frame.DeltaTime);
            }
        }

        public void Update(CharacterControllerFrameResult latestFrame, int currentStateVersion,
            int simulationVersion, bool simulating, float deltaTime)
        {
            Receive(latestFrame, currentStateVersion, simulationVersion, simulating); // 补读遗漏的固定步提交，观测端负责去重。
            CharacterAnimationMotion motion = observer.Read(latestFrame, simulationVersion, simulating);
#if UNITY_EDITOR
            debugSimulationVersion = simulationVersion;
#endif
            if (animator == null || !animator.isActiveAndEnabled)
            {
                // Animator 停用时仍接收固定步，恢复时重发播放状态并重建步态相位。
                if (!animatorUnavailable)
                {
                    committedPlan = default;
                    Matcher.Reset();
                    Ik.Invalidate();
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
            // 更换控制器后先检查绑定，避免无法播放的需求打断其他动作。
            if (!resources.CanResolveActive(composer.ActiveKeys, out string invalidKey))
            {
                Ik.ClearPose();
                intent.InvalidateMotion();
                ReportError($"角色动画持续需求的资源在当前控制器中不可用：{invalidKey}。需求仲裁尚未执行，可撤销该实例或恢复有效控制器。");
                return;
            }
            if (motion.IsValid)
                intent.AdvancePresentation(deltaTime);
            else
                intent.InvalidateMotion();
            var seen = playbackObserver.Capture(intent.Phase, committedPlan.FullBody);
            var selection = composer.Preview(intent.Current);
            var release = poseSelector.DecideRelease(intent, selection, seen, committedPlan);
            var feet = Matcher.Mode == CharacterMatchMode.Rules ? default : Ik.ReadFeet(motion);
            var match = Matcher.Evaluate(intent, selection, seen, committedPlan, release, motion, feet, deltaTime);
            if (intent.Phase == CharacterLocomotionPhase.BrakeLeadIn)
            {
                if (match.Choice == CharacterMatchChoice.Stop && !intent.MainBrakeDue)
                    release = CharacterReleaseDecision.EnterOnMatch;
                else if (match.Choice == CharacterMatchChoice.Continue)
                    release = CharacterReleaseDecision.ContinueStride;
            }
            var entryVelocity = intent.StopEntryVelocity;
            if (release == CharacterReleaseDecision.FeetOverridden || release == CharacterReleaseDecision.Finished ||
                release == CharacterReleaseDecision.MissingStop)
            {
                intent.FinishStop();
                selection = composer.Preview(intent.Current);
            }
            else if ((release == CharacterReleaseDecision.EnterOnContact || release == CharacterReleaseDecision.EnterOnBraking ||
                release == CharacterReleaseDecision.EnterOnMatch) &&
                intent.TryBeginStop())
                selection = composer.Preview(intent.Current);
            var plan = poseSelector.Select(selection, seen, motion, entryVelocity, committedPlan, release, match);
            var emission = player.Apply(plan, deltaTime);
            composer.Commit(selection);
            committedPlan = plan;
#if UNITY_EDITOR
            debugTrace.Record(motion, intent.Phase, intent.PhaseReason, selection, plan, emission,
                seen.LeftSupport, entryVelocity);
#endif
            Ik.Prepare(selection, motion, plan); // 目标许可取自基础层实际混合的素材曲线。
        }

        public void ResetPoseHistory()
        {
            Matcher.Reset();
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

        public void Reset(CharacterControllerFrameResult latestFrame, int currentStateVersion, int simulationVersion)
        {
            stateVersion = currentStateVersion;
            Matcher.Reset();
            observer.Reset(latestFrame, simulationVersion);
            intent.Reset(latestFrame.Behavior);
            composer.Reset();
            committedPlan = default;
            player.ResetPlayback();
            Ik.Reset();
#if UNITY_EDITOR
            debugTrace.Clear();
#endif
        }

        private bool SynchronizeResources()
        {
            bool ready = resources.EnsureResources(out bool controllerChanged, out string error);
            if (controllerChanged)
            {
                intent.FinishStop();
                Matcher.Reset();
                // 更换控制器只清播放和选择历史，保留持续需求及实例编号。
                committedPlan = default;
                player.InvalidateController();
                Ik.Invalidate();
                composer.ResetSelection();
                reportedError = null;
#if UNITY_EDITOR
                debugController = animator != null ? animator.runtimeAnimatorController : null;
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
                debugController == animator.runtimeAnimatorController;
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
