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
    public sealed class CharacterBehaviorDriver : MonoBehaviour, ICameraTargetContinuity
    {
        [Header("输入与能力开关")]
        [SerializeField, InspectorName("接收玩家输入")] private bool receivePlayerInput;
        [SerializeField, InspectorName("允许持物"), Tooltip("初始化时为角色注册持物行为。")] private bool allowGuard;
        [SerializeField, Tooltip("提供相机的水平移动方向。未配置或方向不可用时沿世界 XZ 轴移动。")]
        private CameraControlSource cameraControlSource;

        [Header("共享角色配置")]
        [SerializeField, Tooltip("在配置资产中编辑共享参数。角色初始化时绑定此资产。")] private CharacterProfile profile;
        [Header("当前实例覆盖")]
        [SerializeField, InspectorName("实例覆盖")] private CharacterProfileOverrides profileOverrides = new CharacterProfileOverrides();

        [SerializeField, InspectorName("并行预测")] private bool useWorkerThreads = true;
        private CharacterBlackboard blackboard = new CharacterBlackboard();
        internal CharacterRuntimeConfiguration Configuration => new CharacterRuntimeConfiguration(
            blackboard.Configuration, profile, profileOverrides, allowGuard, useWorkerThreads);

        internal bool UseWorkerThreads => Configuration.UseWorkerThreads;
        internal CharacterProfile Profile => Configuration.Profile;
        internal CharacterProfileOverrides Overrides => profileOverrides;
        internal CharacterMotorSettings Motor => Configuration.Motor;

        private readonly CharacterRigidbodyMotor rigidbodyMotor = new CharacterRigidbodyMotor();
        private CharacterLifecycleCoordinator lifecycle;
        private CharacterMotorSuspension suspension; // 脚本热重载后仍能归还刚体控制和暂停前速度。
        // 读取来自当前角色配置的有效行为数值。
        internal CharacterMovementSettings ReadMovement() => Configuration.ReadMovement();

        private CharacterBehaviorFlow behaviorFlow;
        private readonly CharacterBehaviorTimeline timeline = new CharacterBehaviorTimeline();
        private CharacterForecastSource forecast;
        private readonly CharacterSimulationContinuity continuity = new CharacterSimulationContinuity();
        private readonly CharacterMotionHandoff handoff = new CharacterMotionHandoff();
        public int CameraCutVersion { get; private set; }
        public bool IsExternallyControlled => handoff.HasHolder;

        // 内置表现层先于任何公开通知消费刚体与时间线已共同提交的边界。
        internal event Action<CharacterMotionBoundaryKind, int> MotionBoundaryCommitted
        {
            add => handoff.Committed += value;
            remove => handoff.Committed -= value;
        }

        /// <summary>同步标记真实运动边界及中断前的模拟代次，供表现层清除旧姿态并读取结束回执。</summary>
        public event Action<CharacterMotionBoundaryKind, int> MotionDiscontinuity
        {
            add => handoff.Discontinuity += value;
            remove => handoff.Discontinuity -= value;
        }

        /// <summary>在主线程完整瞬移并中断旧动作；默认清零速度，也可保留或指定世界线速度。</summary>
        public bool TryTeleport(Vector3 position, Quaternion rotation, CharacterVelocityPolicy velocityPolicy = CharacterVelocityPolicy.Clear,
            Vector3 velocity = default) => ApplyMotionBoundary(new CharacterMotionBoundary { Kind = CharacterMotionBoundaryKind.Teleport,
                Position = position, Rotation = rotation, VelocityPolicy = velocityPolicy, Velocity = velocity });

        /// <summary>在主线程施加世界速度，硬直按物理秒计。短于渲染余时的击退仍由下次物理步消费。</summary>
        public bool TryKnockback(Vector3 velocity, float duration, float groundDeceleration = 0f) =>
            ApplyMotionBoundary(new CharacterMotionBoundary { Kind = CharacterMotionBoundaryKind.Knockback,
                Velocity = velocity, Duration = duration, Braking = groundDeceleration });

        /// <summary>在主线程独占接管刚体；已有持有者时拒绝，成功返回的凭据跨暂停有效。</summary>
        public bool TryTakeExternalControl(out CharacterExternalControlToken token)
        {
            var next = new CharacterExternalControlToken(StateVersion);
            var request = new CharacterMotionBoundary { Kind = CharacterMotionBoundaryKind.Takeover, Token = next };
            bool applied = ApplyMotionBoundary(request);
            token = applied && handoff.Owns(next) ? next : null;
            return token != null;
        }

        /// <summary>在主线程持有效凭据交还刚体；默认清零，从外部最终位姿重建运动基线。</summary>
        public bool TryRecoverExternalControl(CharacterExternalControlToken token, CharacterVelocityPolicy velocityPolicy = CharacterVelocityPolicy.Clear,
            Vector3 velocity = default) => ApplyMotionBoundary(new CharacterMotionBoundary { Kind = CharacterMotionBoundaryKind.Recovery,
                Token = token, VelocityPolicy = velocityPolicy, Velocity = velocity });

        /// <summary>将脚本请求的单个行为排入下次裁决，返回是否成功入队。</summary>
        public bool TryRequestBehavior(CharacterBehaviorId id)
        {
            if (continuity.InCallback || continuity.Pending || !IsSimulating || !blackboard.Simulation.Running || IsInKnockback) return false;
            return blackboard.Input.PressBehavior(id);
        }

        /// <summary>控制当前角色真实行为裁决历史的记录。</summary>
        public bool BehaviorHistoryEnabled
        {
            get => behaviorFlow?.HistoryEnabled ?? recordBehaviorHistory;
            set { recordBehaviorHistory = value; if (behaviorFlow != null) behaviorFlow.HistoryEnabled = value; }
        }
        private bool recordBehaviorHistory;
        internal CharacterBehaviorFrameResult LastBehaviorReset => behaviorFlow?.LastResetResult ?? default;
        internal int LastBehaviorResetVersion { get; private set; }
        internal int LastBehaviorResetSimulationVersion { get; private set; }
        public IReadOnlyList<CharacterBehaviorDecision> BehaviorDecisionHistory => behaviorFlow?.DecisionHistory ?? Array.Empty<CharacterBehaviorDecision>();
        /// <summary>角色时钟是否仍在马达执行的击退硬直区间。</summary>
        public bool IsInKnockback => timeline.Time < blackboard.Rigidbody.KnockbackUntil;
        private int lastForecastRenderFrame = -1;
        private CharacterPhysicsExecution physics;
        /// <summary>最近一次行为裁决的角色时刻，单位为秒。暂停期间冻结，重生从零开始，可作为轨迹采样的 now。</summary>
        public double CharacterTime => timeline.Time;
        /// <summary>在主线程按帧号读取当前渲染帧的裁决与外部边界，跨渲染帧或重生后重新读取列表。</summary>
        public IReadOnlyList<CharacterBehaviorObservation> BehaviorUpdates => timeline.Updates;
        /// <summary>最近一次逐帧裁决，含实际行为时长和模拟代次。</summary>
        public CharacterBehaviorObservation LatestBehavior => timeline.Latest;
        internal CharacterInputSnapshot FrameInput => timeline.Held;
        private CharacterCommittedBehaviorGrants committedGrants = new CharacterCommittedBehaviorGrants();
        private CharacterMovementPredictor predictor => forecast?.Predictor;
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

        /// <summary>仅使预测失效；需要由角色执行瞬移或击退时使用对应的完整交接入口。</summary>
        public void InvalidateMovementPrediction()
        {
            if (forecast != null) forecast.NeedsSolvedOrigin = true;
            predictor?.Invalidate(ForecastRefreshReason.ExternalState);
        }

        /// <summary>仅控制预测发布；刚体独占交接使用 TryTakeExternalControl 与 TryRecoverExternalControl。</summary>
        public void SetPredictionSuppressed(bool suppressed)
        {
            if (blackboard.Simulation.PredictionSuppressed == suppressed) return;
            blackboard.Simulation.PredictionSuppressed = suppressed;
            if (blackboard.Simulation.Running && forecast != null) forecast.NeedsSolvedOrigin = true;
            predictor?.Suppress(suppressed || handoff.HasHolder);
        }

        /// <summary>上游已施加击退速度并打断动作后调用。提供硬直剩余秒数及地面减速度，仅刷新预测。</summary>
        public void NotifyPredictionKnockback(float remainingSeconds, float groundDeceleration = 0f)
        {
            if (forecast != null) forecast.NeedsSolvedOrigin = true;
            predictor?.ConfirmKnockback(CharacterTime, remainingSeconds, groundDeceleration);
        }

        private readonly CharacterFrameSubscriptions frameListeners = new CharacterFrameSubscriptions();

        /// <summary>最后完成的逻辑步结果；首次执行前或重置后编号为 0，暂停期间保留最后结果。</summary>
        public CharacterControllerFrameResult LatestFrame => blackboard.PhysicsStep.LatestFrame;
        /// <summary>当前是否允许推进角色模拟，包含组件状态、焦点、应用暂停、游戏暂停和时间缩放。</summary>
        public bool IsSimulating => !handoff.HasHolder && blackboard.Simulation.CanSimulate(this, rigidbodyMotor);
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
            Configuration.InitializeRegistration();
            behaviorFlow = CreateBehaviorFlow();
            behaviorFlow.HistoryEnabled = recordBehaviorHistory;
            var geometry = new CharacterForecastGeometry(body, capsule, rigidbodyMotor.Steps);
            var source = new CharacterMovementPredictor(blackboard.Configuration.CreateForecastFlow, geometry,
                blackboard.Configuration.CaptureForecastFactory, continuity);
            forecast = new CharacterForecastSource(blackboard, rigidbodyMotor, timeline, behaviorFlow, source);
            forecast.CaptureBaseline();
            physics = new CharacterPhysicsExecution(blackboard, rigidbodyMotor, forecast, frameListeners, this);
            predictor.Suppress(blackboard.Simulation.PredictionSuppressed);
            playerInput = new CharacterPlayerInputAdapter(blackboard.Input, CanCapturePlayerInput,
                predictor.RequestRefresh);
            lifecycle = new CharacterLifecycleCoordinator(this, blackboard, timeline, behaviorFlow, rigidbodyMotor, physics,
                forecast, continuity, handoff, committedGrants, frameListeners);
            var initialMovement = ReadMovement();
            continuity.Bind(behaviorFlow, () => lifecycle.ApplyReset(ref suspension),
                () => lifecycle.SynchronizeSimulation(ref suspension), () => lifecycle.InitializeBehavior(initialMovement));
        }

        // 依据当前装配的行为集合与打断关系创建独立流程。
        internal CharacterBehaviorFlow CreateBehaviorFlow() => Configuration.CreateBehaviorFlow();

        // 恢复模拟连续性并开始接收玩家输入。
        private void OnEnable()
        {
            if (predictor == null) InitializeSimulation(); // 首次启用或编辑器脚本重载后重建非序列化的模拟状态。
            blackboard.Simulation.HasInputFocus = Application.isFocused;
            continuity.Request(false);
            if (this != null && isActiveAndEnabled) playerInput.Bind();
        }

        private void OnDestroy() => predictor?.Release();

        // 停止接收玩家输入，并收束停用前的模拟状态。
        private void OnDisable()
        {
            playerInput?.Unbind();
            continuity.Request(false);
        }

        // 提供本角色当前是否能接收玩家输入。
        private bool CanCapturePlayerInput() => !continuity.InCallback && !continuity.Pending && receivePlayerInput && blackboard.Simulation.Running && IsSimulating && !IsInKnockback;

        // 动画采集之前先交付已求解物理步，再在同一渲染帧消费设备边沿。
        private void Update()
        {
            if (!RefreshSimulation()) return;
            int version = SimulationVersion;
            physics.Commit();
            if (this == null || version != SimulationVersion || !IsSimulating) return;
            var config = Configuration;
            double remainder = Math.Max(0d, Time.timeAsDouble - Time.fixedTimeAsDouble);
            double end = blackboard.Rigidbody.SubmittedThrough + remainder;
            var movement = config.ReadMovement();
            if (IsInKnockback)
            {
                using (continuity.Protect())
                    timeline.AdvanceExternal(behaviorFlow, movement, Math.Min(end, blackboard.Rigidbody.KnockbackUntil));
                if (this == null || version != SimulationVersion || continuity.Pending) return;
                CharacterPlayerInputAdapter.EndContinuity(blackboard.Input, receivePlayerInput);
                if (IsInKnockback) return;
                timeline.EndContinuity(Time.realtimeSinceStartupAsDouble);
            }
            playerInput.Capture(receivePlayerInput, ReadCameraBasis(), config.InputCapture);
            if (this == null || version != SimulationVersion || !IsSimulating) return;
            using (continuity.Protect())
                timeline.Advance(behaviorFlow, blackboard, committedGrants, movement, rigidbodyMotor.ReadStepState(),
                    end, Time.realtimeSinceStartupAsDouble);
            if (this == null || version != SimulationVersion || continuity.Pending) return;
            if (predictor.HasFrameAssumptions)
                forecast.CheckAssumptions(config, movement);
        }

        // 从相机读取本帧已发布的水平操作基底。
        private (Vector3 forward, Vector3 right)? ReadCameraBasis()
        {
            if (cameraControlSource != null && cameraControlSource.TryGetBasis(out var forward, out var right))
                return (forward, right);
            return null;
        }

        // 动画消费前按实参校验预测，可能重入的观察者改变代次时放弃读取。
        internal CharacterMovementForecast ReadAnimationForecast() =>
            IsSimulating ? forecast?.ReadForAnimation(this, Configuration) : null;

        // 固定步只消费当时已获准的运动并登记待求解记录。
        private void FixedUpdate()
        {
            if (!RefreshSimulation()) return;
            int version = SimulationVersion;
            physics.Commit();
            if (this == null || version != SimulationVersion || !IsSimulating) return;
            using (continuity.Protect())
                physics.Submit(Configuration, Time.fixedDeltaTime, continuity);
            if (this == null || version != SimulationVersion || continuity.Pending) return;
            CharacterPhysicsSolveBatch.Queue(this);
        }

        // 引擎开始求解后，碰撞和触发回调内的暂停也须交付最后的物理结果。
        internal void MarkSolverEntered() => physics?.MarkSolverEntered();

        // 首次接触提供求解完成证据并记录表面。
        private void OnCollisionEnter(Collision collision) => RecordSolvedCollision(collision);

        // 持续接触更新本次求解的表面。
        private void OnCollisionStay(Collision collision) => RecordSolvedCollision(collision);

        // 自定义播放器未安装固定阶段标记时，真实碰撞仍可证明本步进入了求解。
        private void RecordSolvedCollision(Collision collision)
        {
            MarkSolverEntered();
            if (IsSimulating) rigidbodyMotor.Record(collision);
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

        // 动画提交后检查本帧最终起点，求解前的外部位姿变更等待下一次真实结果。
        internal void CommitForecastFrame()
        {
            int renderFrame = Time.frameCount;
            if (lastForecastRenderFrame == renderFrame) return;
            lastForecastRenderFrame = renderFrame;
            if (!CanSubmitForecast) return;
            try { forecast.Submit(Configuration); }
            finally { continuity.Flush(); }
        }

        // 只允许连续模拟从最近一次可信实测起点提交预测。
        private bool CanSubmitForecast => IsSimulating && blackboard.Simulation.Running &&
            forecast != null && !forecast.NeedsSolvedOrigin && !blackboard.Simulation.PredictionSuppressed;

        // 将外部运动请求与可热重载的刚体暂停记录交给同一生命周期事务。
        private bool ApplyMotionBoundary(in CharacterMotionBoundary request) =>
            lifecycle != null && lifecycle.ApplyMotionBoundary(request, ref suspension);

        internal bool ReceivesPlayerInput => receivePlayerInput;

        // 保存最近一次重生前的两个代次，供动画查询旧行为的结束回执。
        internal void RecordBehaviorReset()
        {
            LastBehaviorResetVersion = StateVersion;
            LastBehaviorResetSimulationVersion = SimulationVersion;
        }

        // 发布相机可独立观察的位姿切换代次。
        internal void MarkCameraCut() => CameraCutVersion++;

        /// <summary>设置角色级游戏暂停，冻结行为、体力与冷却，并清空按下事件及连招预输入。</summary>
        public void SetPaused(bool paused)
        {
            if (blackboard.Simulation.SetGameplayPaused(paused)) continuity.Request(false);
        }

        /// <summary>重生或复用时用当前体力初值重置行为、逻辑步和结果，保留装配好的行为关系和暂停设置。</summary>
        public void ResetState() => continuity.Request(true);

        // 发现模拟开关或输入归属变化时同步连续性，并返回当前运行状态。
        private bool RefreshSimulation()
        {
            continuity.Flush();
            if (blackboard.Simulation.Running != IsSimulating || blackboard.Input.WasReceivingPlayerInput != receivePlayerInput)
                continuity.Request(false);
            return !continuity.Pending && blackboard.Simulation.Running;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (blackboard.Simulation.HasInputFocus == hasFocus) return;
            blackboard.Simulation.HasInputFocus = hasFocus;
            continuity.Request(false);
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (blackboard.Simulation.ApplicationPaused == pauseStatus) return;
            blackboard.Simulation.ApplicationPaused = pauseStatus;
            continuity.Request(false);
        }

#if UNITY_EDITOR
        internal int CopyActualPath(Vector3[] points) => predictor?.Diagnostics.CopyActualPath(points) ?? 0;
        internal string DescribeForecast() => predictor?.Diagnostics.Describe(MovementForecast);
#endif

    }
}
