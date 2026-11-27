using UnityEngine;
using Output = ThreeC.CharacterBehavior.AnimationDecisionOutput;

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

    internal enum CharacterLocomotionVariant { None, Start, TurnLeft, TurnRight, Stop, StopLeft, StopRight }

    /// <summary>按运动观测和行为许可维护基础步态、阶段及动作移动变体。</summary>
    internal sealed class CharacterAnimationIntentConverter
    {
        public const string IdleKey = "$behavior.idle";
        public const string MoveKey = "$behavior.move";
        public const string WalkKey = "$behavior.walk";
        public const string SprintKey = "$behavior.sprint";
        public const string DashKey = "$behavior.dash";
        private CharacterLocomotionDecisions decisions;
        internal bool UsesMovingActionVariant { get; private set; }

        private string locomotionKey = IdleKey;
        private long generation;
        private int? simulationVersion;
        private long frameNumber;
        private Vector3 previousExpectedDirection;
        private Vector3 previousActualVelocity;
        private float remainingSeconds;
        private bool presentationStarted;
        public CharacterMoveGait RequestedGait { get; private set; }
        public float ReverseAngle { get; private set; }
        public float ReverseSpeed { get; private set; }
        public string ReverseGait { get; private set; }
        private float groundedTravel;
        private Vector3 stopEntryVelocity;
        private CharacterLocomotionPhase phaseBeforeRelease;

        private CharacterAnimationRequest groundRequest;
        private CharacterLocomotionPhase groundPhase;
        private CharacterAnimationRequest dashRequest;
        private bool lastStepWasDash;
        public CharacterAnimationRequest Current => dashRequest.InstanceId > 0 ? dashRequest : groundRequest;
        public CharacterLocomotionPhase Phase => dashRequest.InstanceId > 0 ? CharacterLocomotionPhase.Dash : groundPhase;
        public Vector3 StopEntryVelocity => IsReleasing ? stopEntryVelocity : Vector3.zero; // 跨固定步保留松键时的实速。
        public bool MainBrakeDue { get; private set; }
        public string StopGait => decisions.StopGait(DecisionState);
        public long ReverseReleaseStep { get; private set; }
        private Vector3 releaseDirection;
        public long DirectTurnStep { get; private set; }
        public float DirectTurnAngle { get; private set; }
        public float DirectTurnSpeed { get; private set; }
        public string DirectTurnGait { get; private set; }
        private Vector3 turnTarget;
        internal GroundInputTurn InputTurn { get; private set; }
        private float directWindow;
        private float reverseWindow;
        private bool IsReleasing => groundPhase == CharacterLocomotionPhase.BrakeLeadIn || groundPhase == CharacterLocomotionPhase.Stopping;

        private bool hasGait, hasStartTurn, hasStop;

        private LocomotionDecisionState DecisionState => new LocomotionDecisionState
        {
            LastGait = CharacterLocomotionDecisions.Gait(locomotionKey),
            PlayingGait = CharacterLocomotionDecisions.Gait(groundRequest.SemanticKey),
            Phase = groundPhase, PreviousDirection = previousExpectedDirection, PreviousVelocity = previousActualVelocity,
            ReleaseDirection = releaseDirection, GroundTravel = groundedTravel, RemainingSeconds = remainingSeconds,
            StopSpeed = stopEntryVelocity.magnitude, MainBrakeDue = MainBrakeDue
        };

        // 创建本角色的意图状态及判定程序工作区。
        public CharacterAnimationIntentConverter(CharacterAnimationConfiguration config) => Configure(config);

        // 应用步态模块配置，并取消被禁用模块仍在维持的短暂阶段。
        internal void Configure(CharacterAnimationConfiguration config)
        {
            if (hasStop && !config.HasStop && IsReleasing)
                ApplyRelease(CharacterReleaseDecision.Finished);
            if (hasStartTurn && !config.HasStartTurn)
            {
                DirectTurnStep = ReverseReleaseStep = 0;
                if (groundPhase == CharacterLocomotionPhase.Starting || groundPhase == CharacterLocomotionPhase.Turning)
                    SetBase(locomotionKey);
            }
            hasGait = config.HasGait;
            hasStartTurn = config.HasStartTurn;
            hasStop = config.HasStop;
            decisions = new CharacterLocomotionDecisions(config);
            InputTurn = hasStartTurn ? GroundInputTurn.Bind(config, decisions.ReverseWindowSeconds) : null;
        }

        // 按固定步实速推进动作变体迟滞，瞬移暂存速度时沿用已有选择。
        internal void ObserveActionVariantMotion(in CharacterAnimationMotion motion) =>
            UsesMovingActionVariant = decisions.MovingAction(motion, UsesMovingActionVariant);

        // 只读预判本步是否需要发布预测，实际意图由固定步队列提交一次。
        internal bool MayUsePredictedGait(in CharacterAnimationMotion motion, in CharacterBehaviorFrameResult behavior) =>
            decisions.MayProject(motion, behavior);

        /// <summary>每个有效固定步提交后更新；同一模拟连续性内保留相邻实际与期望方向。</summary>
        public void Receive(CharacterAnimationMotion motion, CharacterBehaviorFrameResult behavior,
            CharacterInputSnapshot input, float stepSeconds, bool preserveEntryGait = false)
        {
            if (!hasGait || !motion.IsValid)
                return;
            if (simulationVersion.HasValue && (simulationVersion.Value != motion.SimulationVersion ||
                motion.FrameNumber != frameNumber + 1))
                InvalidateMotion();
            simulationVersion = motion.SimulationVersion;
            frameNumber = motion.FrameNumber;
            RequestedGait = input.Gait;

            Vector3 expectedDirection = motion.ExpectedVelocity.normalized;
            bool canStartMoving = behavior.MoveApproved && behavior.OrdinarySpeedLimit > 0f;
            InputTurn?.Observe(motion, input, canStartMoving, previousActualVelocity, locomotionKey, stepSeconds);
            if (expectedDirection == Vector3.zero && canStartMoving)
                expectedDirection = new Vector3(input.Direction.x, 0f, input.Direction.y).normalized;
            bool grounded = (motion.CollisionFlags & CollisionFlags.Below) != 0;
            if (IsReleasing && stopEntryVelocity.sqrMagnitude > decisions.IdleExit * decisions.IdleExit)
                releaseDirection = stopEntryVelocity.normalized;
            float signedTurn = Vector3.SignedAngle(previousActualVelocity, expectedDirection, Vector3.up);
            var step = decisions.Step(motion, behavior, input, DecisionState, expectedDirection, signedTurn);
            bool reverseInput = hasStartTurn && step.Flag(Output.ReverseInput);
            if (IsReleasing && reverseInput)
            {
                ReverseReleaseStep = motion.FrameNumber;
                ReverseAngle = Vector3.SignedAngle(releaseDirection, motion.ExpectedVelocity, Vector3.up);
                ReverseSpeed = stopEntryVelocity.magnitude;
                ReverseGait = StopGait;
                reverseWindow = decisions.ReverseWindowSeconds;
            }
            else reverseWindow = Mathf.Max(0f, reverseWindow - stepSeconds);
            if (!reverseInput || reverseWindow <= 0f)
            {
                ReverseReleaseStep = 0;
                if (!IsReleasing || behavior.MoveApproved) releaseDirection = default;
            }

            bool followsDash = lastStepWasDash;
            lastStepWasDash = behavior.DisplacementSource == CharacterDisplacementSource.Dash;
            // 已获准的冲刺即使受阻仍有效，普通移动从新方向开始。
            if (lastStepWasDash)
            {
                ClearDirections();
                SetBase(locomotionKey);
                return;
            }

            if (step.Flag(Output.ReleaseMotion))
            {
                DirectTurnStep = 0;
                ReceiveRelease(motion, hasStop ? (DecisionRelease)step.Number(Output.ReleaseAction) : DecisionRelease.Idle);
                return;
            }
            directWindow = Mathf.Max(0f, directWindow - stepSeconds);
            if (hasStartTurn && step.Flag(Output.DirectTurn))
            {
                DirectTurnStep = motion.FrameNumber;
                DirectTurnAngle = signedTurn <= -179.999f ? 180f : signedTurn;
                DirectTurnSpeed = previousActualVelocity.magnitude;
                DirectTurnGait = locomotionKey;
                turnTarget = expectedDirection;
                directWindow = decisions.ReverseWindowSeconds;
            }
            if (directWindow <= 0f || !grounded || !motion.HasAcceleration || Vector3.Dot(turnTarget, expectedDirection) < 0.8f)
                DirectTurnStep = 0;
            bool resuming = step.Flag(Output.ResumeStride);
            if (IsReleasing && !resuming)
            {
                ClearDirections();
                locomotionKey = IdleKey;
            }
            if (groundPhase == CharacterLocomotionPhase.BrakeLeadIn)
            {
                // 轻制动尚未换播，同向恢复时沿用原实例，再按实速换档。
                groundPhase = phaseBeforeRelease;
            }
            else if (groundPhase == CharacterLocomotionPhase.Stopping && resuming)
                locomotionKey = groundRequest.SemanticKey;
            stopEntryVelocity = Vector3.zero;
            MainBrakeDue = false;
            var stride = decisions.Stride(motion, behavior, DecisionState, input.Gait, expectedDirection,
                resuming, canStartMoving, preserveEntryGait, followsDash);
            locomotionKey = CharacterLocomotionDecisions.Key((DecisionGait)stride.Number(Output.GroundGait));
            if (motion.IsBlocked || locomotionKey == IdleKey)
            {
                ClearDirections();
                SetBase(locomotionKey);
                return;
            }

            if (grounded)
                groundedTravel += motion.ActualSpeed * stepSeconds;
            else
                groundedTravel = 0f;
            switch ((DecisionStride)stride.Number(Output.StrideAction))
            {
                case DecisionStride.Turn when hasStartTurn && decisions.TurnSeconds > 0f:
                    var turn = ResolveTurnVariant(motion.FacingDeltaDegrees, expectedDirection);
                    BeginTransient(CharacterLocomotionPhase.Turning, turn, decisions.TurnSeconds);
                    break;
                case DecisionStride.Start when hasStartTurn && decisions.StartSeconds > 0f:
                    BeginTransient(CharacterLocomotionPhase.Starting, CharacterLocomotionVariant.Start, decisions.StartSeconds);
                    break;
                case DecisionStride.Continue when hasStartTurn && remainingSeconds > 0f:
                    SetIntent(locomotionKey, groundPhase, groundRequest.LocomotionVariant);
                    break;
                default:
                    SetBase(locomotionKey);
                    break;
            }
            previousExpectedDirection = expectedDirection;
            previousActualVelocity = motion.ActualVelocity;
        }

        // 在移动输入解除时延续制动、进入收步等待或回到待机。
        private void ReceiveRelease(in CharacterAnimationMotion motion, DecisionRelease decision)
        {
            switch (decision)
            {
                case DecisionRelease.Hold:
                    previousActualVelocity = motion.ActualVelocity;
                    return;
                case DecisionRelease.Brake:
                    previousActualVelocity = motion.ActualVelocity;
                    MainBrakeDue = !motion.Trajectory.IsBrakeLeadIn;
                    return;
                case DecisionRelease.Begin:
                    stopEntryVelocity = previousActualVelocity;
                    previousActualVelocity = motion.ActualVelocity;
                    groundedTravel = 0f;
                    MainBrakeDue = !motion.Trajectory.IsBrakeLeadIn;
                    phaseBeforeRelease = groundPhase;
                    groundPhase = CharacterLocomotionPhase.BrakeLeadIn;
                    return;
            }
            ClearDirections();
            locomotionKey = IdleKey;
            SetBase(IdleKey);
        }

        /// <summary>根据脚步、制动和部位归属的决策提交一次收步阶段转换。</summary>
        public bool ApplyRelease(CharacterReleaseDecision decision)
        {
            switch (decision)
            {
                case CharacterReleaseDecision.FeetOverridden:
                case CharacterReleaseDecision.Finished:
                case CharacterReleaseDecision.MissingStop:
                    if (!IsReleasing) return false;
                    locomotionKey = IdleKey;
                    ClearDirections();
                    SetBase(IdleKey);
                    return true;
                case CharacterReleaseDecision.EnterOnContact:
                case CharacterReleaseDecision.EnterOnBraking:
                case CharacterReleaseDecision.EnterOnMatch:
                    if (groundPhase != CharacterLocomotionPhase.BrakeLeadIn) return false;
                    BeginStop();
                    return true;
                default:
                    return false;
            }
        }

        // 按松键时的来源步态启动收步实例。
        private void BeginStop()
        {
            string sourceGait = StopGait; // 已预混跑姿但实速仍低时，按步行制动。
            locomotionKey = IdleKey;
            remainingSeconds = 0f;
            SetIntent(sourceGait, CharacterLocomotionPhase.Stopping, CharacterLocomotionVariant.Stop, true);
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
            if (remainingSeconds <= 0f && groundPhase != CharacterLocomotionPhase.BrakeLeadIn)
            {
                SetBase(locomotionKey);
            }
        }

        /// <summary>丢弃上一连续性内的暂态与方向证据，等待新观测时保持最后基础动作或批准的冲刺。</summary>
        public void InvalidateMotion()
        {
            simulationVersion = null;
            InputTurn?.Reset();
            ReverseReleaseStep = 0;
            releaseDirection = default;
            if (IsReleasing) locomotionKey = IdleKey;
            ClearDirections();
            lastStepWasDash = false;
            SetBase(locomotionKey);
        }

        /// <summary>重建步态基线及已批准的冲刺，保留独立的动作移动变体。</summary>
        public void ResetGait(CharacterBehaviorFrameResult behavior)
        {
            simulationVersion = null;
            InputTurn?.Reset();
            ReverseReleaseStep = 0;
            releaseDirection = default;
            locomotionKey = IdleKey;
            ClearDirections();
            lastStepWasDash = false;
            dashRequest = default;
            SetBase(IdleKey, true);
            ObserveDash(behavior);
        }

        // 由本帧获准的冲刺实例立即接管基础动作；地面步态继续以实际求解步维护。
        internal void ObserveDash(in CharacterBehaviorFrameResult behavior)
        {
            if (!decisions.TakeDash(behavior))
                dashRequest = default;
            else if (behavior.DashStarted || dashRequest.InstanceId == 0)
                dashRequest = new CharacterAnimationRequest(DashKey, ++generation);
        }

#if UNITY_EDITOR
        // 输出本角色基础意图程序最近已求值的分支和输入。
        internal void AppendDecisionTrace(System.Text.StringBuilder text) => decisions.AppendTrace(text);
#endif

        // 从有符号朝向差和相邻方向确定实际播放的左右变体。
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
            DirectTurnStep = 0;
            MainBrakeDue = false;
            groundedTravel = 0f;
            stopEntryVelocity = Vector3.zero;
            previousActualVelocity = Vector3.zero;
            previousExpectedDirection = Vector3.zero;
        }

        // 启动给定时长的起步或转身阶段。
        private void BeginTransient(CharacterLocomotionPhase phase, CharacterLocomotionVariant variant, float seconds)
        {
            remainingSeconds = seconds;
            presentationStarted = false;
            SetIntent(locomotionKey, phase, variant, true);
        }

        // 结束短暂阶段并切换到指定的基础动作。
        private void SetBase(string key, bool restart = false)
        {
            remainingSeconds = 0f;
            presentationStarted = false;
            CharacterLocomotionPhase phase = key == IdleKey ? CharacterLocomotionPhase.Idle : CharacterLocomotionPhase.Steady;
            SetIntent(key, phase, CharacterLocomotionVariant.None, restart);
        }

        // 更新基础动作，只有动作实例变化时才生成新编号。
        private void SetIntent(string key, CharacterLocomotionPhase phase, CharacterLocomotionVariant variant, bool restart = false)
        {
            groundPhase = phase;
            if (!restart && groundRequest.SemanticKey == key && groundRequest.LocomotionVariant == variant)
                return;
            bool sameTransient = !restart && variant != CharacterLocomotionVariant.None &&
                groundRequest.LocomotionVariant == variant;
            long instance = sameTransient ? groundRequest.InstanceId : ++generation;
            groundRequest = new CharacterAnimationRequest(key, instance, variant);
        }
    }
}
