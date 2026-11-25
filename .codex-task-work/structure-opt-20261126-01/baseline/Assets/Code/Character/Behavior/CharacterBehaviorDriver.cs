using System;
using System.Collections.Generic;
using ThreeC.FollowCamera.Contracts;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>逐帧裁决输入、按固定步执行获准运动，并在帧末提交预测起点。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    [AddComponentMenu("3C/角色行为驱动器")]
    public sealed class CharacterBehaviorDriver : MonoBehaviour
    {
        [Header("输入与能力开关")]
        [SerializeField, InspectorName("接收玩家输入")] private bool receivePlayerInput;
        [SerializeField, InspectorName("允许持物"), Tooltip("初始化时为角色注册持物行为。")] private bool allowGuard;
        [SerializeField, Tooltip("提供相机的水平移动方向。未配置或方向不可用时沿世界 XZ 轴移动。")]
        private CameraControlSource cameraControlSource;
        [SerializeField, InspectorName("动作目标对齐")] private CharacterActionAlignmentBinding actionAlignment = new CharacterActionAlignmentBinding();

        [Header("共享角色配置")]
        [SerializeField, Tooltip("在配置资产中编辑共享参数。角色初始化时绑定此资产。")] private CharacterProfile profile;
        [Header("当前实例覆盖")]
        [SerializeField, InspectorName("实例覆盖")] private CharacterProfileOverrides profileOverrides = new CharacterProfileOverrides();

        [SerializeField, InspectorName("并行预测与动画决策")] private bool useWorkerThreads = true;
        private CharacterBlackboard blackboard = new CharacterBlackboard();
        internal CharacterRuntimeConfiguration Configuration => new CharacterRuntimeConfiguration(
            blackboard.Configuration, profile, profileOverrides, allowGuard, useWorkerThreads);

        internal bool UseWorkerThreads => Configuration.UseWorkerThreads;
        internal CharacterProfile Profile => Configuration.Profile;
        internal CharacterProfileOverrides Overrides => profileOverrides;
        internal CharacterMotorSettings Motor => Configuration.Motor;

        private readonly CharacterRigidbodyMotor rigidbodyMotor = new CharacterRigidbodyMotor();
        private CharacterMotorSuspension suspension; // 脚本热重载后仍能归还刚体控制和暂停前速度。
        // 读取来自当前角色配置的有效行为数值。
        internal CharacterMovementSettings ReadMovement() => Configuration.ReadMovement();

        private CharacterBehaviorFlow behaviorFlow;
        private readonly CharacterBehaviorTimeline timeline = new CharacterBehaviorTimeline();
        private CharacterForecastOrigin solvedOrigin;
        private int lastForecastRenderFrame = -1;
        private long alignmentRevision;
        private bool needsNewSolvedOrigin;
        /// <summary>最近一次行为裁决的角色时刻，单位为秒。暂停期间冻结，重生从零开始，可作为轨迹采样的 now。</summary>
        public double CharacterTime => timeline.Time;
        /// <summary>最近一次攻击对齐实例的状态；到达和受阻由真实物理求解提交。</summary>
        public CharacterActionAlignmentStatus ActionAlignmentStatus => behaviorFlow?.Alignment.Status ?? CharacterActionAlignmentStatus.None;
        /// <summary>状态所属的攻击实例序号，可与行为帧的 Attack.InstanceId 对照。</summary>
        public long ActionAlignmentInstance => behaviorFlow?.Alignment.Instance ?? 0;

        /// <summary>主线程指定角色要到达的锚点；站位使用锚点局部坐标，朝向为锚点前向加世界竖轴偏角。</summary>
        public void SetActionAlignmentTarget(Transform target, Vector3 localOffset = default, float yawDegrees = 0f)
        {
            actionAlignment.Set(target, localOffset, yawDegrees);
            RefreshAlignmentTarget();
        }

        /// <summary>撤销当前锚点，并从下一次尚未提交的运动区间恢复原招式曲线。</summary>
        public void ClearActionAlignmentTarget() => SetActionAlignmentTarget(null);

        // 主线程读取锚点并撤销过期命令及在途预测，后台只收到冻结的值。
        private void RefreshAlignmentTarget()
        {
            if (behaviorFlow == null || !behaviorFlow.Alignment.SetTarget(actionAlignment.Capture(), out long cancelled)) return;
            var attack = LatestBehavior.Behavior.Attack;
            var plan = behaviorFlow.Alignment.Plan;
            if (plan != null && plan.Settings.Tracking == CharacterAlignmentTracking.Follow && rigidbodyMotor.Available)
            {
                var state = rigidbodyMotor.ReadStepState();
                var actual = new CharacterAlignmentOrigin { Position = state.Start, Facing = state.Facing };
                if (!CharacterAlignmentPlan.Allows(plan.Settings, behaviorFlow.Alignment.Target, actual))
                    cancelled = behaviorFlow.Alignment.Stop(CharacterActionAlignmentStatus.OutOfRange);
            }
            blackboard.Rigidbody.CancelAlignment(cancelled);
            if (cancelled != 0 || attack.BufferedMove != CharacterAttackMove.None ||
                attack.Move != CharacterAttackMove.None && (plan == null || plan.Settings.Tracking == CharacterAlignmentTracking.Follow))
                InvalidateAlignmentPrediction();
        }

        // 同时作废已发布轨迹、在途任务，并让后续帧携带这次目标修订。
        private void InvalidateAlignmentPrediction()
        {
            alignmentRevision++;
            predictor?.RequestRefresh(ForecastRefreshReason.ExternalState);
        }

        internal IReadOnlyList<CharacterBehaviorObservation> BehaviorUpdates => timeline.Updates;
        internal CharacterBehaviorObservation LatestBehavior => timeline.Latest;
        internal CharacterInputSnapshot FrameInput => timeline.Held;
        private CharacterCommittedBehaviorGrants committedGrants = new CharacterCommittedBehaviorGrants();
        private CharacterMovementPredictor predictor;
        private CharacterPlayerInputAdapter playerInput;
        internal CharacterForecastSettings ForecastSettings => Configuration.Forecast;
        public CharacterMovementForecast MovementForecast => predictor?.Output;
        /// <summary>按当前模拟时钟查询未来位姿。超出预测时长或可信接触范围时返回 false。</summary>
        public bool TryGetPredictedMovement(float secondsAhead, out CharacterForecastSample sample)
        {
            sample = default;
            return IsSimulating && MovementForecast != null && MovementForecast.TrySample(CharacterTime, secondsAhead, out sample);
        }

        internal double LastSolvedTime => blackboard.PhysicsStep.LastSolvedTime;

        /// <summary>瞬移、外部冲量或已确认的状态变化发生时，在下一次动画读取前重建预测。</summary>
        public void InvalidateMovementPrediction()
        {
            needsNewSolvedOrigin = true;
            predictor?.Invalidate(ForecastRefreshReason.ExternalState);
        }

        /// <summary>外部行为完全接管移动（例如死亡或布娃娃）时立即撤销轨迹，交还控制后重新预测。</summary>
        public void SetPredictionSuppressed(bool suppressed)
        {
            if (blackboard.Simulation.PredictionSuppressed == suppressed) return;
            blackboard.Simulation.PredictionSuppressed = suppressed;
            if (blackboard.Simulation.Running) needsNewSolvedOrigin = true;
            predictor?.Suppress(suppressed);
        }

        /// <summary>上游已施加击退速度并打断动作后调用。提供硬直剩余秒数及地面减速度，仅刷新预测。</summary>
        public void NotifyPredictionKnockback(float remainingSeconds, float groundDeceleration = 0f)
        {
            needsNewSolvedOrigin = true;
            predictor?.ConfirmKnockback(CharacterTime, remainingSeconds, groundDeceleration);
        }

        private readonly CharacterFrameSubscriptions frameListeners = new CharacterFrameSubscriptions();

        /// <summary>最后完成的逻辑步结果；首次执行前或重置后编号为 0，暂停期间保留最后结果。</summary>
        public CharacterControllerFrameResult LatestFrame => blackboard.PhysicsStep.LatestFrame;
        /// <summary>当前是否允许推进角色模拟，包含组件状态、焦点、应用暂停、游戏暂停和时间缩放。</summary>
        public bool IsSimulating => blackboard.Simulation.CanSimulate(this, rigidbodyMotor);
        /// <summary>显式状态重置的代次；表现层可据此补偿回调重入时被中止的旧通知。</summary>
        public int StateVersion => blackboard.Simulation.ResetVersion;
        /// <summary>模拟连续性的代次；暂停、焦点和启停边界清空输入时递增，供表现层识别旧观测。</summary>
        public int SimulationVersion => blackboard.Simulation.LifecycleVersion;
        /// <summary>当前仍请求且已提交的持物许可编号，输入释放或模拟中断时为零。</summary>
        internal long CommittedGuardApproval => committedGrants.ReadGuardApproval(blackboard.Input.GuardRequested, IsSimulating, SimulationVersion);

        /// <summary>在下一物理步开始、本帧 Update 或暂停边界同步交付上一个已求解步的位移与朝向。
        /// 每步独立通知，回调应避免再次推进模拟。订阅者异常不影响后续交付，重置或停用会中止当前通知。</summary>
        public event Action<CharacterControllerFrameResult> FrameCommitted
        {
            add => frameListeners.Committed += value;
            remove => frameListeners.Committed -= value;
        }

        /// <summary>显式重置状态后同步通知表现层清理持续需求，不代表普通暂停或停用。</summary>
        public event Action StateReset
        {
            add => frameListeners.Reset += value;
            remove => frameListeners.Reset -= value;
        }

        private void Awake() => InitializeSimulation();

        // 初始化刚体控制、行为实例和预测所需的角色配置。
        private void InitializeSimulation()
        {
            lastForecastRenderFrame = -1;
            blackboard.Configuration.BindProfile(profile);
            var body = GetComponent<Rigidbody>();
            var capsule = GetComponent<CapsuleCollider>();
            blackboard.PhysicsStep.ResetFrame();
            blackboard.Rigidbody.Reset();
            timeline.Reset(Time.realtimeSinceStartupAsDouble);
            committedGrants.Reset();
            rigidbodyMotor.Initialize(body, capsule, Motor, Configuration.FootfallDistance);
            ResetBehaviorFlow();
            CapturePhysicalBaseline();
            var geometry = new CharacterForecastGeometry(body, capsule, rigidbodyMotor.Steps);
            predictor = new CharacterMovementPredictor(CreateBehaviorFlow, geometry, blackboard.Configuration.CreateForecastFlow);
            predictor.Suppress(blackboard.Simulation.PredictionSuppressed);
            playerInput = new CharacterPlayerInputAdapter(blackboard.Input, CanCapturePlayerInput,
                predictor.RequestRefresh);
        }

        private void ResetBehaviorFlow()
        {
            Configuration.InitializeRegistration();
            behaviorFlow ??= CreateBehaviorFlow();
            behaviorFlow.Reset(ReadMovement());
        }

        internal CharacterBehaviorFlow CreateBehaviorFlow() => Configuration.CreateBehaviorFlow();

        // 恢复模拟连续性并开始接收玩家输入。
        private void OnEnable()
        {
            if (predictor == null) InitializeSimulation(); // 首次启用或编辑器脚本重载后重建非序列化的模拟状态。
            blackboard.Simulation.HasInputFocus = Application.isFocused;
            SynchronizeSimulation();
            playerInput.Bind();
        }

        private void OnDestroy() => predictor?.Release();

        // 停止接收玩家输入，并收束停用前的模拟状态。
        private void OnDisable()
        {
            playerInput?.Unbind();
            SynchronizeSimulation();
        }

        // 提供本角色当前是否能接收玩家输入。
        private bool CanCapturePlayerInput() => receivePlayerInput && blackboard.Simulation.Running && IsSimulating;

        // 动画采集之前先交付已求解物理步，再在同一渲染帧消费设备边沿。
        private void Update()
        {
            if (!RefreshSimulation()) return;
            int version = SimulationVersion;
            CommitSolvedStep();
            if (this == null || version != SimulationVersion || !IsSimulating) return;
            var config = Configuration;
            RefreshAlignmentTarget();
            if (this == null || version != SimulationVersion || !IsSimulating) return;
            playerInput.Capture(receivePlayerInput, ReadCameraBasis(), config.InputCapture);
            if (this == null || version != SimulationVersion || !IsSimulating) return;
            double remainder = Math.Max(0d, Time.timeAsDouble - Time.fixedTimeAsDouble);
            timeline.Advance(behaviorFlow, blackboard, committedGrants, config.ReadMovement(), rigidbodyMotor.ReadStepState(),
                blackboard.Rigidbody.SubmittedThrough + remainder, Time.realtimeSinceStartupAsDouble);
            predictor.CheckFrameAssumptions(CharacterTime, LatestBehavior.Behavior, FrameInput);
            // 连续跟随可能每帧撤回上一条预测；在本帧动画采集之前，用已发布的行为输入补齐当前目标的轨迹。
            if (this != null && version == SimulationVersion && FollowsAlignmentTarget && !predictor.Output.Available)
                SubmitForecastFrame(beforeAnimation: true);
        }

        // 从相机读取本帧已发布的水平操作基底。
        private (Vector3 forward, Vector3 right)? ReadCameraBasis()
        {
            if (cameraControlSource != null && cameraControlSource.TryGetBasis(out var forward, out var right))
                return (forward, right);
            return null;
        }

        internal CharacterMovementForecast PublishedAnimationForecast => predictor?.PublishedForAnimation(IsSimulating, ForecastSettings);

        // 固定步只消费当时已获准的运动并登记待求解记录。
        private void FixedUpdate()
        {
            if (!RefreshSimulation()) return;
            int version = SimulationVersion;
            CommitSolvedStep();
            if (this == null || version != SimulationVersion || !IsSimulating) return;
            var config = Configuration;
            var body = blackboard.Rigidbody;
            RefreshAlignmentTarget();
            if (this == null || version != SimulationVersion || !IsSimulating) return;
            float dt = Time.fixedDeltaTime;
            var pending = rigidbodyMotor.DriveStep(body, dt, body.SubmittedThrough + dt,
                LatestFrame.FrameNumber + 1, rigidbodyMotor.Parameters(config));
            blackboard.PhysicsStep.Schedule(pending);
            CharacterPhysicsSolveBatch.Queue(this);
        }

        // 先记住已求解的真实起点，再校正预测并通知可能重入的订阅者。
        private void CommitSolvedStep()
        {
            var steps = blackboard.PhysicsStep;
            if (!steps.TryCommitSolved(rigidbodyMotor, SimulationVersion, out var actual)) return;
            var frame = steps.LatestFrame;
            blackboard.Rigidbody.MarkSolved();
            StorePhysicalBaseline(actual);
            long finished = rigidbodyMotor.ObserveAlignment(ref behaviorFlow.Alignment, actual);
            if (finished != 0)
            {
                blackboard.Rigidbody.CancelAlignment(finished);
                InvalidateAlignmentPrediction();
            }
            if (this == null || frame.SimulationVersion != SimulationVersion) return;
            predictor?.ObserveSolvedStep(solvedOrigin);
            if (this != null && frame.SimulationVersion == SimulationVersion)
                frameListeners.PublishCommitted(frame, this, blackboard.Simulation);
        }

        // 引擎开始求解后，碰撞和触发回调内的暂停也须交付最后的物理结果。
        internal void MarkSolverEntered() => blackboard.PhysicsStep.MarkSolverEntered();

        // 首次接触提供求解完成证据并记录表面。
        private void OnCollisionEnter(Collision collision) => RecordSolvedCollision(collision);

        // 持续接触更新本次求解的表面。
        private void OnCollisionStay(Collision collision) => RecordSolvedCollision(collision);

        // 自定义播放器未安装固定阶段标记时，真实碰撞仍可证明本步进入了求解。
        private void RecordSolvedCollision(Collision collision)
        {
            MarkSolverEntered();
            rigidbodyMotor.Record(collision);
        }

        // 离开接触时撤销上一求解留下的表面。
        private void OnCollisionExit(Collision collision)
        {
            MarkSolverEntered();
            rigidbodyMotor.Forget(collision.collider);
        }

        // 排入帧末统一提交点，使其他脚本的 LateUpdate 变化也能纳入本帧。
        private void LateUpdate()
        {
            if (IsSimulating) CharacterForecastFrameBatch.Queue(this);
        }

        // 动画提交后一次性复制完整起点，求解前的外部位姿变更等待下一次真实结果。
        internal void CommitForecastFrame()
        {
            int renderFrame = Time.frameCount;
            if (lastForecastRenderFrame == renderFrame) return;
            lastForecastRenderFrame = renderFrame;
            SubmitForecastFrame(beforeAnimation: false);
        }

        // 已获准的跟随实例从进入到窗口终点都需要与当前冻结目标一致的轨迹。
        private bool FollowsAlignmentTarget => behaviorFlow?.Alignment.Plan is CharacterAlignmentPlan plan &&
            plan.Settings.Tracking == CharacterAlignmentTracking.Follow && CharacterTime < plan.End;

        // 早期补发和正常帧末提交共用有效性、快照与发布入口；无新变化的帧末提交复用已有结果。
        private void SubmitForecastFrame(bool beforeAnimation)
        {
            if (!IsSimulating || !blackboard.Simulation.Running || needsNewSolvedOrigin || blackboard.Simulation.PredictionSuppressed) return;
            var config = Configuration;
            var frame = new CharacterForecastFrame(SimulationVersion, CharacterTime, solvedOrigin, LatestBehavior.Behavior,
                FrameInput, blackboard.Rigidbody, Time.fixedDeltaTime, config, rigidbodyMotor.Parameters(config), alignmentRevision);
            predictor.SubmitFrame(frame, behaviorFlow, config.UseWorkerThreads, FollowsAlignmentTarget, frameEnd: !beforeAnimation);
        }

        // 初始化、重生或恢复时读取新的物理基线，常规渲染帧沿用上一次已求解观测。
        private void CapturePhysicalBaseline()
        {
            if (rigidbodyMotor.Available) StorePhysicalBaseline(rigidbodyMotor.ReadSolvedPose());
        }

        // 使用实测位姿、同一步的行为结果及马达游标替换预测基线。
        private void StorePhysicalBaseline(in CharacterMotorObservation actual)
        {
            var frame = LatestFrame;
            solvedOrigin = new CharacterForecastOrigin(LastSolvedTime, frame.FrameNumber, actual, frame.Behavior,
                frame.Input.Gait, rigidbodyMotor.DriveState, blackboard.Rigidbody.StartCursor);
            needsNewSolvedOrigin = false;
        }

        /// <summary>设置角色级游戏暂停，冻结行为、体力与冷却，并清空按下事件及连招预输入。</summary>
        public void SetPaused(bool paused)
        {
            if (blackboard.Simulation.SetGameplayPaused(paused)) SynchronizeSimulation();
        }

        /// <summary>角色重生或复用时调用，按当前配置重置行为、资源、逻辑步和结果，保留暂停设置。</summary>
        public void ResetState()
        {
            FinishPendingAtBoundary(publishSolved: false);
            ResetBehaviorFlow();
            predictor?.ClearControlLock();
            SetPredictionSuppressed(false);
            blackboard.PhysicsStep.ResetFrame();
            committedGrants.Reset();
            blackboard.Rigidbody.Reset();
            timeline.Reset(Time.realtimeSinceStartupAsDouble);
            rigidbodyMotor.ResetMotion(ref suspension);
            CapturePhysicalBaseline();
            blackboard.Simulation.AdvanceReset();
            EndInputContinuity();
            frameListeners.PublishReset(this, blackboard.Simulation);
        }

        // 连续性边界交付最后已求解步，未进入求解的提交归还给运动黑板。
        private void SynchronizeSimulation()
        {
            int version = SimulationVersion;
            FinishPendingAtBoundary(publishSolved: true);
            if (this == null) return;
            // 回调可以重生或再次暂停。仅在内层已同步实际运行开关时跳过这次边界。
            if (version != SimulationVersion && blackboard.Simulation.Running == IsSimulating &&
                blackboard.Input.WasReceivingPlayerInput == receivePlayerInput) return;
            EndInputContinuity();
            blackboard.Simulation.Running = IsSimulating;
            rigidbodyMotor.SetSuspended(!blackboard.Simulation.Running, ref suspension);
            if (blackboard.Simulation.Running) CapturePhysicalBaseline();
        }

        // 边界处撤回未求解的提交，已求解的步按暂停或重生的通知约定结算。
        private void FinishPendingAtBoundary(bool publishSolved)
        {
            var steps = blackboard.PhysicsStep;
            if (!steps.HasPending) return;
            if (steps.ShouldCommitAtBoundary(Time.inFixedTimeStep, Time.fixedTimeAsDouble))
            {
                if (publishSolved) CommitSolvedStep();
                else steps.DiscardPending();
                return;
            }
            rigidbodyMotor.CancelPending(blackboard.Rigidbody);
            steps.DiscardPending();
        }

        // 发现模拟开关或输入归属变化时同步连续性，并返回当前运行状态。
        private bool RefreshSimulation()
        {
            if (blackboard.Simulation.Running != IsSimulating || blackboard.Input.WasReceivingPlayerInput != receivePlayerInput)
                SynchronizeSimulation();
            return blackboard.Simulation.Running;
        }

        // 清空跨模拟边界的输入与预测，最后更新连续性代次以撤销旧帧的许可。
        private void EndInputContinuity()
        {
            if (behaviorFlow != null && rigidbodyMotor.Available)
            {
                if (behaviorFlow.Alignment.Plan != null)
                    blackboard.Rigidbody.CancelAlignment(behaviorFlow.Alignment.Stop(CharacterActionAlignmentStatus.Cancelled));
                var state = rigidbodyMotor.ReadStepState();
                var movement = ReadMovement();
                var active = behaviorFlow.Tick(default, state.Facing, 0f, movement, state.GroundVelocity);
                blackboard.Rigidbody.Write(behaviorFlow.CaptureMotion(CharacterTime, active, default, state.Facing,
                    state.GroundVelocity, movement));
            }
            timeline.EndContinuity(Time.realtimeSinceStartupAsDouble);
            predictor?.EndContinuity();
            CharacterPlayerInputAdapter.EndContinuity(blackboard.Input, receivePlayerInput);
            behaviorFlow?.EndInputContinuity();
            blackboard.Simulation.AdvanceContinuity();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (blackboard.Simulation.HasInputFocus == hasFocus) return;
            blackboard.Simulation.HasInputFocus = hasFocus;
            SynchronizeSimulation();
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (blackboard.Simulation.ApplicationPaused == pauseStatus) return;
            blackboard.Simulation.ApplicationPaused = pauseStatus;
            SynchronizeSimulation();
        }

#if UNITY_EDITOR
        internal int CopyActualPath(Vector3[] points) => predictor?.Diagnostics.CopyActualPath(points) ?? 0;
        internal string DescribeForecast() => predictor?.Diagnostics.Describe(MovementForecast);
#endif

        // 在 Inspector 修改实例过渡覆盖时约束其有效范围。
        private void OnValidate()
        {
            if (profileOverrides != null) profileOverrides.Transition.Value = Mathf.Clamp01(profileOverrides.Transition.Value);
        }
    }
}
