using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>基础移动意图的表现阶段；全身或局部外部动作仍由仲裁处理。</summary>
    public enum CharacterLocomotionPhase
    {
        Idle,
        Starting,
        Steady,
        Turning,
        Dash,
        Stopping,
        BrakeLeadIn
    }

    /// <summary>当前基础移动意图的判定依据，供表现诊断读取。</summary>
    public enum CharacterLocomotionPhaseReason
    {
        AwaitingMotion,
        BelowMovementThreshold,
        StartedMoving,
        SustainedMovement,
        ReversedDirection,
        Obstructed,
        MovementNotApproved,
        DashApproved,
        GroundBraking,
        GroundBrakeLeadIn
    }

    internal enum CharacterLocomotionVariant { None, Start, TurnLeft, TurnRight, Stop, StopLeft, StopRight }

    /// <summary>从已提交的运动观测与批准行为选择基础步态和阶段，不查询或写入动画资源。</summary>
    internal sealed class CharacterAnimationIntentConverter
    {
        public const string IdleKey = "$behavior.idle";
        public const string MoveKey = "$behavior.move";
        public const string WalkKey = "$behavior.walk";
        public const string SprintKey = "$behavior.sprint";
        public const string DashKey = "$behavior.dash";
        private readonly float idleEnterSpeed;
        private readonly float idleExitSpeed;
        private readonly float runEnterSpeed;
        private readonly float runExitSpeed;
        private readonly float sprintEnterSpeed;
        private readonly float sprintExitSpeed;
        private readonly float startSeconds;
        private readonly float turnSeconds;
        private readonly float reverseDotThreshold;
        private readonly float minimumStopTravel;

        private string locomotionKey = IdleKey;
        private long generation;
        private int? simulationVersion;
        private long frameNumber;
        private Vector3 previousExpectedDirection;
        private Vector3 previousActualVelocity;
        private float remainingSeconds;
        private bool presentationStarted;
        private float groundedTravel;
        private Vector3 stopEntryVelocity;
        private CharacterLocomotionPhase phaseBeforeRelease;
        private CharacterLocomotionPhaseReason reasonBeforeRelease;

        public CharacterAnimationRequest Current { get; private set; }
        public CharacterLocomotionPhase Phase { get; private set; }
        public CharacterLocomotionPhaseReason PhaseReason { get; private set; }
        public Vector3 StopEntryVelocity => IsReleasing ? stopEntryVelocity : Vector3.zero; // 跨固定步保留松键时的实速。
        public bool MainBrakeDue { get; private set; }
        public string StopGait => locomotionKey == MoveKey && stopEntryVelocity.magnitude < runExitSpeed ? WalkKey : locomotionKey;
        public long ReverseReleaseStep { get; private set; }
        private Vector3 releaseDirection;
        private float reverseWindow;
        private readonly float reverseWindowSeconds;
        private bool IsReleasing => Phase == CharacterLocomotionPhase.BrakeLeadIn || Phase == CharacterLocomotionPhase.Stopping;

        public CharacterAnimationIntentConverter(CharacterAnimationConfiguration config)
        {
            idleEnterSpeed = InputMath.NonNegative(config.IdleEnterSpeed, 0.08f);
            idleExitSpeed = Mathf.Max(idleEnterSpeed, InputMath.NonNegative(config.IdleExitSpeed, 0.15f));
            runExitSpeed = Mathf.Max(idleExitSpeed, InputMath.NonNegative(config.RunExitSpeed, 3f));
            runEnterSpeed = Mathf.Max(runExitSpeed, InputMath.NonNegative(config.RunEnterSpeed, 3.5f));
            sprintExitSpeed = Mathf.Max(runEnterSpeed, InputMath.NonNegative(config.SprintExitSpeed, 5.6f));
            sprintEnterSpeed = Mathf.Max(sprintExitSpeed, InputMath.NonNegative(config.SprintEnterSpeed, 6f));
            startSeconds = InputMath.NonNegative(config.LocomotionStartSeconds, 0.22f);
            turnSeconds = InputMath.NonNegative(config.LocomotionTurnSeconds, 0.32f);
            float reverseAngle = Mathf.Clamp(InputMath.NonNegative(config.LocomotionReverseAngle, 120f), 90f, 180f);
            reverseDotThreshold = Mathf.Cos(reverseAngle * Mathf.Deg2Rad);
            reverseWindowSeconds = Mathf.Max(0.15f, InputMath.NonNegative(config.MatchReverseHold, 0.08f) + 0.1f);
            minimumStopTravel = InputMath.NonNegative(config.MinimumStopTravel, 0.09f);
        }

        /// <summary>每个有效固定步提交后更新；同一模拟连续性内保留相邻实际与期望方向。</summary>
        public void Receive(CharacterAnimationMotion motion, CharacterBehaviorFrameResult behavior,
            CharacterInputSnapshot input, float stepSeconds)
        {
            if (!motion.IsValid)
                return;
            if (simulationVersion.HasValue && (simulationVersion.Value != motion.SimulationVersion ||
                motion.FrameNumber != frameNumber + 1))
                InvalidateMotion();
            simulationVersion = motion.SimulationVersion;
            frameNumber = motion.FrameNumber;

            // 用松键时的速度判断收步反向，并锁存结果供跨固定步的渲染帧读取。
            if (IsReleasing && Current.SemanticKey == MoveKey && stopEntryVelocity.sqrMagnitude >= runExitSpeed * runExitSpeed)
                releaseDirection = stopEntryVelocity.normalized;
            bool reverseInput = behavior.MoveApproved && behavior.DisplacementSource == CharacterDisplacementSource.BasicMovement &&
                (motion.CollisionFlags & CollisionFlags.Below) != 0 && !motion.IsBlocked && releaseDirection != Vector3.zero &&
                Vector3.Dot(releaseDirection, motion.ExpectedVelocity.normalized) <= reverseDotThreshold + 0.000001f;
            if (IsReleasing && reverseInput)
            {
                ReverseReleaseStep = motion.FrameNumber;
                reverseWindow = reverseWindowSeconds;
            }
            else reverseWindow = Mathf.Max(0f, reverseWindow - stepSeconds);
            if (!reverseInput || reverseWindow <= 0f)
            {
                ReverseReleaseStep = 0;
                if (!IsReleasing || behavior.MoveApproved) releaseDirection = default;
            }

            bool followsDash = Phase == CharacterLocomotionPhase.Dash;
            // 已获准的冲刺即使受阻仍有效，普通移动从新方向开始。
            if (behavior.DisplacementSource == CharacterDisplacementSource.Dash)
            {
                ClearDirections();
                SetBase(DashKey, CharacterLocomotionPhaseReason.DashApproved, behavior.DashStarted);
                return;
            }

            Vector3 expectedDirection = motion.ExpectedVelocity.normalized;
            bool grounded = (motion.CollisionFlags & CollisionFlags.Below) != 0;
            if (!behavior.MoveApproved || expectedDirection == Vector3.zero)
            {
                ReceiveRelease(motion, behavior, input, grounded);
                return;
            }
            bool resuming = IsReleasing && grounded && previousActualVelocity.magnitude > idleExitSpeed;
            if (IsReleasing && !resuming)
            {
                ClearDirections();
                locomotionKey = IdleKey;
            }
            if (Phase == CharacterLocomotionPhase.BrakeLeadIn)
            {
                // 轻制动尚未换播，同向恢复时沿用原实例，再按实速换档。
                Phase = phaseBeforeRelease;
                PhaseReason = reasonBeforeRelease;
            }
            else if (Phase == CharacterLocomotionPhase.Stopping && resuming)
                locomotionKey = Current.SemanticKey;
            stopEntryVelocity = Vector3.zero;
            MainBrakeDue = false;
            bool wasIdle = locomotionKey == IdleKey;
            string lastGait = locomotionKey;
            locomotionKey = ResolveGait(motion.ActualSpeed, input.Gait);
            if (motion.IsBlocked)
            {
                ClearDirections();
                SetBase(locomotionKey, CharacterLocomotionPhaseReason.Obstructed);
                return;
            }
            // 起跑预测只提前从走切到跑，其他档位仍按实速判定。
            bool starting = wasIdle ? !followsDash && startSeconds > 0f : Phase == CharacterLocomotionPhase.Starting;
            float forwardSpeed = Vector3.Dot(motion.Trajectory.FutureVelocity, expectedDirection);
            if (starting && motion.Trajectory.IsValid && input.Gait == CharacterMoveGait.Run &&
                locomotionKey == WalkKey && forwardSpeed >= runEnterSpeed)
                locomotionKey = MoveKey;
            // 转身时实速可能经过零，用预测轨迹判断是否保持原步态。
            bool reversing = IsReversal(expectedDirection);
            if (locomotionKey == IdleKey && (Phase == CharacterLocomotionPhase.Turning || resuming && reversing) &&
                motion.Trajectory.IsValid && forwardSpeed > idleExitSpeed)
                locomotionKey = lastGait;
            if (locomotionKey == IdleKey)
            {
                ClearDirections();
                SetBase(locomotionKey, CharacterLocomotionPhaseReason.BelowMovementThreshold);
                return;
            }

            if (grounded)
                groundedTravel += motion.ActualSpeed * stepSeconds;
            else
                groundedTravel = 0f;
            if (reversing)
            {
                var turn = ResolveTurnVariant(motion.FacingDeltaDegrees, expectedDirection);
                BeginTransient(CharacterLocomotionPhase.Turning, CharacterLocomotionPhaseReason.ReversedDirection,
                    turn, turnSeconds);
            }
            else if (wasIdle && !followsDash && startSeconds > 0f)
            {
                BeginTransient(CharacterLocomotionPhase.Starting, CharacterLocomotionPhaseReason.StartedMoving,
                    CharacterLocomotionVariant.Start, startSeconds);
            }
            else if (remainingSeconds > 0f)
            {
                SetIntent(locomotionKey, Phase, PhaseReason, Current.LocomotionVariant); // 切换步态时保留当前转身实例。
            }
            else
            {
                SetBase(locomotionKey, CharacterLocomotionPhaseReason.SustainedMovement);
            }
            previousExpectedDirection = expectedDirection;
            previousActualVelocity = motion.ActualVelocity;
        }

        private void ReceiveRelease(CharacterAnimationMotion motion, CharacterBehaviorFrameResult behavior,
            CharacterInputSnapshot input, bool grounded)
        {
            bool obstructed = motion.Trajectory.IsBrakeObstructed;
            bool released = grounded && !obstructed && input.Direction == Vector2.zero &&
                behavior.DisplacementProvider == CharacterBehaviorId.None;
            if (released && IsReleasing)
            {
                previousActualVelocity = motion.ActualVelocity;
                if (Phase == CharacterLocomotionPhase.Stopping || MainBrakeDue)
                    return; // 双脚落稳之前，刚体速度可以先归零。
                if (motion.Trajectory.IsBraking)
                {
                    MainBrakeDue = !motion.Trajectory.IsBrakeLeadIn;
                    return;
                }
            }
            else if (released && behavior.MoveEnded && locomotionKey != IdleKey && groundedTravel > 0f &&
                groundedTravel >= minimumStopTravel && Vector3.Dot(previousExpectedDirection, previousActualVelocity) > 0f)
            {
                stopEntryVelocity = previousActualVelocity; // 主动位移达到门槛后才记录收步动量。
                previousActualVelocity = motion.ActualVelocity;
                groundedTravel = 0f;
                // 固定步记录松键，渲染帧取得分层归属和播放状态后再选收步实例。
                MainBrakeDue = !motion.Trajectory.IsBrakeLeadIn;
                phaseBeforeRelease = Phase;
                reasonBeforeRelease = PhaseReason;
                Phase = CharacterLocomotionPhase.BrakeLeadIn;
                PhaseReason = CharacterLocomotionPhaseReason.GroundBrakeLeadIn;
                return;
            }
            ClearDirections();
            locomotionKey = IdleKey;
            SetBase(IdleKey, obstructed ? CharacterLocomotionPhaseReason.Obstructed :
                CharacterLocomotionPhaseReason.MovementNotApproved);
        }

        /// <summary>表现侧确认步态支撑接点，或物理窗口结束后，消费这次待收步。重复通知无效。</summary>
        public bool TryBeginStop()
        {
            if (Phase != CharacterLocomotionPhase.BrakeLeadIn) return false;
            BeginStop();
            return true;
        }

        private void BeginStop()
        {
            string sourceGait = StopGait; // 已预混跑姿但实速仍低时，按步行制动。
            locomotionKey = IdleKey;
            remainingSeconds = 0f;
            SetIntent(sourceGait, CharacterLocomotionPhase.Stopping,
                CharacterLocomotionPhaseReason.GroundBraking, CharacterLocomotionVariant.Stop, true);
        }

        /// <summary>每次可呈现的渲染帧在仲裁前推进，首个呈现帧忽略当前帧耗时。</summary>
        public void AdvancePresentation(float deltaTime)
        {
            if (remainingSeconds <= 0f)
                return;
            if (!presentationStarted) // 窗口从首个可呈现帧起算。
            {
                presentationStarted = true;
                return;
            }
            remainingSeconds = InputMath.Elapsed(remainingSeconds, InputMath.NonNegative(deltaTime, 0f));
            if (remainingSeconds <= 0f && Phase != CharacterLocomotionPhase.BrakeLeadIn)
                SetBase(locomotionKey, CharacterLocomotionPhaseReason.SustainedMovement);
        }

        /// <summary>丢弃上一连续性内的暂态与方向证据，等待新观测时保持最后基础动作或批准的冲刺。</summary>
        public void InvalidateMotion()
        {
            simulationVersion = null;
            ReverseReleaseStep = 0;
            releaseDirection = default;
            if (IsReleasing) locomotionKey = IdleKey;
            ClearDirections();
            string key = Current.SemanticKey == DashKey ? DashKey : locomotionKey;
            SetBase(key, CharacterLocomotionPhaseReason.AwaitingMotion);
        }

        /// <summary>按角色重置或动画入口重建后重新建立基线；保留已批准的冲刺。</summary>
        public void Reset(CharacterBehaviorFrameResult behavior)
        {
            simulationVersion = null;
            ReverseReleaseStep = 0;
            releaseDirection = default;
            locomotionKey = IdleKey;
            ClearDirections();
            string key = behavior.DisplacementSource == CharacterDisplacementSource.Dash ? DashKey : IdleKey;
            SetBase(key, CharacterLocomotionPhaseReason.AwaitingMotion, true);
        }

        private string ResolveGait(float speed, CharacterMoveGait gait)
        {
            if (speed <= (locomotionKey == IdleKey ? idleExitSpeed : idleEnterSpeed))
                return IdleKey;
            if (gait == CharacterMoveGait.Sprint && speed >= (locomotionKey == SprintKey ? sprintExitSpeed : sprintEnterSpeed))
                return SprintKey; // 同步检查输入档与实速，排除短突进余速。
            bool wasRunning = locomotionKey == MoveKey || locomotionKey == SprintKey;
            return speed >= (wasRunning ? runExitSpeed : runEnterSpeed) ? MoveKey : WalkKey;
        }

        private bool IsReversal(Vector3 expectedDirection)
        {
            // 请求方向与实际速度都须反向，排除擦墙引发的转身。
            float threshold = reverseDotThreshold + 0.000001f;
            return turnSeconds > 0f && previousExpectedDirection != Vector3.zero &&
                previousActualVelocity != Vector3.zero &&
                Vector3.Dot(previousExpectedDirection, expectedDirection) <= threshold &&
                Vector3.Dot(previousActualVelocity.normalized, expectedDirection) <= threshold;
        }

        private CharacterLocomotionVariant ResolveTurnVariant(float facingDelta, Vector3 expectedDirection)
        {
            // 朝向已对齐时用相邻请求推断转向，正反向等距时沿用驱动器的右转约定。
            if (Mathf.Abs(facingDelta) < 0.01f)
            {
                facingDelta = Vector3.SignedAngle(previousExpectedDirection, expectedDirection, Vector3.up);
                if (Mathf.Abs(facingDelta) > 179.999f)
                    facingDelta = 180f;
            }
            return facingDelta < 0f ? CharacterLocomotionVariant.TurnLeft : CharacterLocomotionVariant.TurnRight;
        }

        private void ClearDirections()
        {
            MainBrakeDue = false;
            groundedTravel = 0f;
            stopEntryVelocity = Vector3.zero;
            previousActualVelocity = Vector3.zero;
            previousExpectedDirection = Vector3.zero;
        }

        public void FinishStop()
        {
            if (IsReleasing)
            {
                locomotionKey = IdleKey;
                ClearDirections();
                SetBase(IdleKey, CharacterLocomotionPhaseReason.BelowMovementThreshold);
            }
        }

        private void BeginTransient(CharacterLocomotionPhase phase, CharacterLocomotionPhaseReason reason,
            CharacterLocomotionVariant variant, float seconds)
        {
            remainingSeconds = seconds;
            presentationStarted = false;
            SetIntent(locomotionKey, phase, reason, variant, true);
        }

        private void SetBase(string key, CharacterLocomotionPhaseReason reason, bool restart = false)
        {
            remainingSeconds = 0f;
            presentationStarted = false;
            CharacterLocomotionPhase phase = key == DashKey ? CharacterLocomotionPhase.Dash :
                key == IdleKey ? CharacterLocomotionPhase.Idle : CharacterLocomotionPhase.Steady;
            SetIntent(key, phase, reason, CharacterLocomotionVariant.None, restart);
        }

        private void SetIntent(string key, CharacterLocomotionPhase phase, CharacterLocomotionPhaseReason reason,
            CharacterLocomotionVariant variant, bool restart = false)
        {
            Phase = phase;
            PhaseReason = reason;
            if (!restart && Current.SemanticKey == key && Current.LocomotionVariant == variant)
                return;
            bool sameTransient = !restart && variant != CharacterLocomotionVariant.None &&
                Current.LocomotionVariant == variant;
            long instance = sameTransient ? Current.InstanceId : ++generation;
            Current = new CharacterAnimationRequest(key, instance, variant);
        }
    }
}
