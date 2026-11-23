using System;
using ThreeC.FollowCamera.Contracts;
using ThreeC.Player;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>逐帧采集输入；固定步先发布上一步物理结果，再提交本步行为与下一轮输入。</summary>
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

        [Header("共享角色配置")]
        [SerializeField, Tooltip("在配置资产中编辑共享参数。角色初始化时绑定此资产。")] private CharacterProfile profile;
        [Header("当前实例覆盖")]
        [SerializeField, InspectorName("实例覆盖")] private CharacterProfileOverrides profileOverrides = new CharacterProfileOverrides();

        [SerializeField, InspectorName("并行预测与动画决策")] private bool useWorkerThreads = true;
        internal bool UseWorkerThreads
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return false;
#else
                return useWorkerThreads;
#endif
            }
        }

        private CharacterProfile initializedProfile;
        private bool profileInitialized;
        internal CharacterProfile Profile => profileInitialized ? initializedProfile : profile;
        internal CharacterProfileOverrides Overrides => profileOverrides;
        internal CharacterMotorSettings Motor => Profile ? Profile.Motor : motor;

        [HideInInspector, SerializeField, Tooltip("配置世界 XZ 平面的移动、普通移动转向和冲刺。冲刺起步立即对准锁定方向。")]
        private CharacterMovementSettings settings = new CharacterMovementSettings();

        [HideInInspector, SerializeField, Tooltip("配置刚体对输入的响应、可行走坡度及跨阶高度。")]
        private CharacterMotorSettings motor = new CharacterMotorSettings();

        private readonly CharacterRigidbodyMotor rigidbodyMotor = new CharacterRigidbodyMotor();
        private CharacterMotorSuspension suspension; // 脚本热重载后仍能归还刚体控制和暂停前速度。
        private readonly CharacterMovementSettings resolvedMovement = new CharacterMovementSettings();

        internal CharacterMovementSettings ReadMovement()
        {
            if (!Profile) return settings;
            Profile.ReadMovement(resolvedMovement);
            return resolvedMovement;
        }

        private Rigidbody body;
        private CapsuleCollider capsule;
        private CharacterBehaviorFrameResult pendingBehavior;
        private CharacterGroundDrive pendingGroundDrive;
        private CharacterInputSnapshot pendingInput;
        private Vector3 pendingStart;
        private float pendingDuration;
        private double pendingTime;
        private readonly CharacterInputBlackboard inputBlackboard = new CharacterInputBlackboard();
        private CharacterInputSnapshot executionInput;
        private CharacterBehaviorFlow behaviorFlow;
        private CharacterBehaviorRegistration registration;
        private CharacterMovementPredictor predictor;
        private bool suppressPrediction;
        private readonly CharacterForecastSettings fallbackForecast = new CharacterForecastSettings();
        internal CharacterForecastSettings ForecastSettings => Profile && Profile.Forecast != null ? Profile.Forecast : fallbackForecast;
        public CharacterMovementForecast MovementForecast => predictor?.Output;
        /// <summary>按当前模拟时钟查询未来位姿。超出预测时长或可信接触范围时返回 false。</summary>
        public bool TryGetPredictedMovement(float secondsAhead, out CharacterForecastSample sample)
        {
            sample = default;
            return IsSimulating && MovementForecast != null && MovementForecast.TrySample(Time.timeAsDouble, secondsAhead, out sample);
        }

        internal double LastSolvedTime { get; private set; }

        /// <summary>瞬移、外部冲量或已确认的状态变化发生时，在下一次动画读取前重建预测。</summary>
        public void InvalidateMovementPrediction() => predictor?.Invalidate(ForecastRefreshReason.ExternalState);

        /// <summary>外部行为完全接管移动（例如死亡或布娃娃）时立即撤销轨迹，交还控制后重新预测。</summary>
        public void SetPredictionSuppressed(bool suppressed)
        {
            suppressPrediction = suppressed;
            predictor?.Suppress(suppressed);
        }

        /// <summary>上游已施加击退速度并打断动作后调用。提供硬直剩余秒数及地面减速度，仅刷新预测。</summary>
        public void NotifyPredictionKnockback(float remainingSeconds, float groundDeceleration = 0f) =>
            predictor?.ConfirmKnockback(Time.timeAsDouble, remainingSeconds, groundDeceleration);

        private float GroundAcceleration => Profile ? profileOverrides.GroundAcceleration.Resolve(Motor.groundAcceleration)
            : motor.groundAcceleration;
        private readonly CharacterFrameSubscriptions frameListeners = new CharacterFrameSubscriptions();
        private bool hasInputFocus = true;
        private bool applicationPaused;
        private bool gameplayPaused;
        private bool wasReceivingPlayerInput;
        private ulong lastDashPressId;
        private bool suppressGuardUntilRelease;
        private bool guardRequested;
        private long guardApprovals;
        private int lifecycleVersion;
        private int resetVersion;
        private bool notifyingReset;
        private bool simulationRunning;

        /// <summary>最后完成的逻辑步结果；首次执行前或重置后编号为 0，暂停期间保留最后结果。</summary>
        public CharacterControllerFrameResult LatestFrame { get; private set; }
        /// <summary>当前是否允许推进角色模拟，包含组件状态、焦点、应用暂停、游戏暂停和时间缩放。</summary>
        public bool IsSimulating => CanSimulate();
        /// <summary>显式状态重置的代次；表现层可据此补偿回调重入时被中止的旧通知。</summary>
        public int StateVersion => resetVersion;
        /// <summary>模拟连续性的代次；暂停、焦点和启停边界清空输入时递增，供表现层识别旧观测。</summary>
        public int SimulationVersion => lifecycleVersion;
        /// <summary>当前仍请求且已提交的持物许可编号，输入释放或模拟中断时为零。</summary>
        internal long CommittedGuardApproval => guardRequested && LatestFrame.SimulationVersion == lifecycleVersion
            && (LatestFrame.Behavior.ActiveBehaviors & CharacterBehaviorId.Guard) != CharacterBehaviorId.None
            && CanSimulate() ? guardApprovals : 0;

        /// <summary>下一固定步开始时同步发布上一步的位移和朝向，暂停前已求解的最后一步也会交付。
        /// 每步独立通知，回调应避免再次推进模拟。订阅者异常不影响后续交付，重置或停用会中止当前通知。</summary>
        public event Action<CharacterControllerFrameResult> FrameCommitted
        {
            add => frameListeners.Committed += value;
            remove => frameListeners.Committed -= value;
        }

        /// <summary>显式重置状态后同步通知表现层清理持续需求，不代表普通暂停或停用。</summary>
        public event Action StateReset;

        private void Awake() => InitializeSimulation();

        private void InitializeSimulation()
        {
            initializedProfile = profile;
            profileInitialized = true;
            body = GetComponent<Rigidbody>();
            capsule = GetComponent<CapsuleCollider>();
            pendingDuration = 0f;
            LatestFrame = default;
            rigidbodyMotor.Initialize(body, capsule, Motor, Profile ? Profile.RunFootfallDistance() : CharacterMotorSettings.DefaultFootfallDistance);
            ResetBehaviorFlow();
            var geometry = new CharacterForecastGeometry(body, capsule, rigidbodyMotor.Steps);
            predictor = new CharacterMovementPredictor(CreateBehaviorFlow(), geometry, CreateForecastFlow);
            predictor.Suppress(suppressPrediction);
        }

        private void ResetBehaviorFlow()
        {
            registration ??= ReadRegistration();
            behaviorFlow ??= CreateBehaviorFlow();
            behaviorFlow.Reset(ReadMovement());
        }

        private CharacterBehaviorRegistration ReadRegistration()
        {
            var source = Profile;
            return source ? new CharacterBehaviorRegistration(allowGuard, source.LightAttack, source.ComboRoutes, source.Guard)
                : new CharacterBehaviorRegistration(allowGuard, null, null, null);
        }

        internal CharacterBehaviorFlow CreateBehaviorFlow() => CharacterBehaviorFlow.ForCharacter(registration ?? ReadRegistration());

        private CharacterBehaviorFlow CreateForecastFlow() => CharacterBehaviorFlow.ForCharacter(registration, snapshot: true);

        private void OnEnable()
        {
            if (predictor == null) InitializeSimulation(); // 首次启用或编辑器脚本重载后重建非序列化的模拟状态。
            hasInputFocus = Application.isFocused;
            SynchronizeSimulation();
            PlayerInputSystem.AttackPressed += RecordAttackPress;
        }

        private void OnDestroy() => predictor?.Release();

        private void OnDisable()
        {
            PlayerInputSystem.AttackPressed -= RecordAttackPress;
            SynchronizeSimulation();
        }

        private void RecordAttackPress(AttackButton button)
        {
            if (receivePlayerInput && wasReceivingPlayerInput && simulationRunning && CanSimulate())
            {
                inputBlackboard.PressAttack(button);
                predictor.RequestRefresh(ForecastRefreshReason.AttackPressed);
            }
        }

        private void Update()
        {
            if (!RefreshSimulation())
                return;

            PlayerInputSample input = receivePlayerInput ? PlayerInputSystem.Read() : default;
            Vector2 direction = input.Move;
            // 采集时将相机相对方向转为世界 XZ，幅值限制留给固定步输入黑板。
            if (cameraControlSource != null &&
                cameraControlSource.TryGetBasis(out Vector3 forward, out Vector3 right))
            {
                direction = new Vector2(right.x, right.z) * direction.x +
                    new Vector2(forward.x, forward.z) * direction.y;
            }

            bool dashPressed = input.DashPressId != 0 && input.DashPressId != lastDashPressId;
            lastDashPressId = input.DashPressId;
            CharacterMoveGait gait = Profile ? Profile.DefaultGait : CharacterMoveGait.Run;
            if (input.WalkHeld)
                gait = CharacterMoveGait.Walk;
            else if (input.SprintHeld)
                gait = CharacterMoveGait.Sprint;

            if (!input.GuardHeld)
                suppressGuardUntilRelease = false;
            guardRequested = registration.Guard && input.GuardHeld && !suppressGuardUntilRelease;
            inputBlackboard.Write(direction, dashPressed, gait, guardRequested);
            if (dashPressed) predictor.RequestRefresh(ForecastRefreshReason.DashPressed);
            RefreshPrediction();
        }

        internal CharacterMovementForecast PublishedAnimationForecast =>
            ForecastSettings.Enabled && ForecastSettings.DriveAnimation && CanSimulate() ? MovementForecast : null;

        internal CharacterMovementForecast AnimationForecast
        {
            get
            {
                if (PublishedAnimationForecast == null) return null;
                if (!Time.inFixedTimeStep && predictor.NeedsRefresh) RefreshPrediction();
                return MovementForecast;
            }
        }

        private void RefreshPrediction()
        {
            if (!ForecastSettings.Enabled)
            {
                predictor.Release();
                return;
            }
            if (predictor.WaitingForValidWindow(Time.timeAsDouble, Time.fixedTimeAsDouble, ForecastSettings, profileOverrides))
                return;
            // Update 在本帧最后一次物理求解之后；行为/马达已走完 pendingBehavior，刚体也处于同一步末。
            var solved = pendingDuration > 0f ? pendingBehavior : LatestFrame.Behavior;
            var origin = rigidbodyMotor.CaptureForecast(Time.fixedTimeAsDouble, solved,
                pendingDuration > 0f ? pendingInput.Gait : LatestFrame.Input.Gait);
            predictor.Refresh(Time.timeAsDouble, origin, behaviorFlow, new CharacterForecastInputs(executionInput, inputBlackboard.Peek()),
                ReadMovement(), rigidbodyMotor.Parameters(GroundAcceleration, MoveTurnSpeed), ForecastSettings, profileOverrides, UseWorkerThreads);
        }

        private void FixedUpdate()
        {
            if (!RefreshSimulation())
                return;
            predictor?.Pump();
            int version = lifecycleVersion;
            CommitSolvedStep();
            if (this == null || version != lifecycleVersion || !CanSimulate())
                return;
            UpdateBehavior(Time.fixedDeltaTime);
        }

        private void UpdateBehavior(float deltaTime)
        {
            Vector3 forward = body.rotation * Vector3.forward;
            Vector2 facing = new Vector2(forward.x, forward.z);
            pendingInput = executionInput;
            Vector3 velocity = body.velocity;
            var contacts = rigidbodyMotor.ReadContacts();
            var groundVelocity = CharacterMotorStep.GroundVelocity(velocity, contacts.Ground);
            pendingBehavior = behaviorFlow.Tick(pendingInput, facing, deltaTime, ReadMovement(), groundVelocity);
            pendingStart = body.position;
            pendingDuration = deltaTime;
            pendingTime = Time.fixedTimeAsDouble;
            var drive = rigidbodyMotor.Parameters(GroundAcceleration, MoveTurnSpeed);
            pendingGroundDrive = rigidbodyMotor.Drive(pendingBehavior, pendingInput, deltaTime, drive, contacts);
            executionInput = inputBlackboard.Consume();
        }

        private void CommitSolvedStep()
        {
            if (pendingDuration <= 0f)
                return;
            LastSolvedTime = pendingTime;
            Vector3 displacement = body.position - pendingStart;
            CollisionFlags contacts = rigidbodyMotor.ReadContacts().Flags;
            var previous = LatestFrame.SimulationVersion == lifecycleVersion ? LatestFrame.Trajectory : default;
            var trajectory = pendingGroundDrive.Observe(body.velocity, displacement, pendingDuration, contacts, previous,
                body.rotation * Vector3.forward, MoveTurnSpeed);
            LatestFrame = new CharacterControllerFrameResult(pendingBehavior, pendingInput, pendingDuration,
                displacement, contacts, body.rotation * Vector3.forward, lifecycleVersion, trajectory);
            var solved = new CharacterForecastSample(body.position, body.velocity, body.rotation, pendingBehavior, contacts, pendingInput.Gait);
            predictor?.ObserveSolvedStep(pendingBehavior.FrameNumber, pendingTime, solved);
            if ((pendingBehavior.StartedBehaviors & CharacterBehaviorId.Guard) != CharacterBehaviorId.None)
                guardApprovals++;
            pendingDuration = 0f;
            PublishCommittedFrame(LatestFrame);
        }

        private void OnCollisionEnter(Collision collision) => rigidbodyMotor.Record(collision);
        private void OnCollisionStay(Collision collision) => rigidbodyMotor.Record(collision);
        private void OnCollisionExit(Collision collision) => rigidbodyMotor.Forget(collision.collider);

        private float MoveTurnSpeed => InputMath.NonNegative(
            Profile ? Profile.Movement.MoveTurnSpeed : settings?.MoveTurnSpeed ?? CharacterMovementSettings.DefaultMoveTurnSpeed,
            CharacterMovementSettings.DefaultMoveTurnSpeed);

        private void PublishCommittedFrame(CharacterControllerFrameResult frame)
        {
            Delegate[] handlers = frameListeners.Handlers; // 回调中增减订阅从下个物理步生效。
            int version = lifecycleVersion;
            foreach (Action<CharacterControllerFrameResult> handler in handlers)
            {
                try
                {
                    handler(frame);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
                if (version != lifecycleVersion || this == null || !isActiveAndEnabled)
                    return;
            }
        }

        private void LateUpdate() => predictor?.Pump();

        /// <summary>设置角色级游戏暂停，冻结行为、体力与冷却，并清空两块输入黑板。</summary>
        public void SetPaused(bool paused)
        {
            if (gameplayPaused == paused)
                return;

            gameplayPaused = paused;
            SynchronizeSimulation();
        }

        /// <summary>角色重生或复用时调用，按当前配置重置行为、资源、逻辑步和结果，保留暂停设置。</summary>
        public void ResetState()
        {
            ResetBehaviorFlow();
            predictor?.ClearControlLock();
            SetPredictionSuppressed(false);
            LatestFrame = default;
            pendingDuration = 0f;
            rigidbodyMotor.ResetMotion(ref suspension);
            resetVersion++;
            ClearInput();
            PublishReset();
        }

        private void PublishReset()
        {
            if (notifyingReset || StateReset == null) // 重入时已完成状态重置，只跳过重复通知。
                return;
            notifyingReset = true;
            int version = resetVersion;
            try
            {
                foreach (Action handler in StateReset.GetInvocationList())
                {
                    try
                    {
                        handler();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception, this);
                    }
                    if (version != resetVersion || this == null)
                        return;
                }
            }
            finally
            {
                notifyingReset = false;
            }
        }

        private bool CanSimulate()
        {
            return this != null && isActiveAndEnabled && hasInputFocus && !applicationPaused
                && !gameplayPaused && Time.timeScale > 0f && body != null && capsule != null && capsule.enabled;
        }

        private void SynchronizeSimulation()
        {
            if (pendingDuration > 0f && (!Time.inFixedTimeStep || Time.fixedTimeAsDouble > pendingTime))
                CommitSolvedStep();
            pendingDuration = 0f;
            ClearInput();
            simulationRunning = CanSimulate();
            rigidbodyMotor.SetSuspended(!simulationRunning, ref suspension);
        }

        private bool RefreshSimulation()
        {
            if (simulationRunning != CanSimulate() || wasReceivingPlayerInput != receivePlayerInput)
                SynchronizeSimulation();
            return simulationRunning;
        }

        private void ClearInput()
        {
            // 暂停时清空输入，冻结冲刺进度、冷却和体力。
            predictor?.Release();
#if UNITY_EDITOR
            predictor?.Diagnostics.Reset();
#endif
            inputBlackboard.Clear();
            executionInput = default;
            wasReceivingPlayerInput = receivePlayerInput;
            lastDashPressId = PlayerInputSystem.LatestDashPressId; // 恢复或接手玩家控制时跳过此前记录的按下。
            behaviorFlow?.ClearAttackBuffer();
            guardRequested = false;
            suppressGuardUntilRelease = true; // 恢复后先松键，再接受持物请求。
            lifecycleVersion++;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            hasInputFocus = hasFocus;
            SynchronizeSimulation();
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            applicationPaused = pauseStatus;
            SynchronizeSimulation();
        }

#if UNITY_EDITOR
        internal int CopyActualPath(Vector3[] points) => predictor?.Diagnostics.CopyActualPath(points) ?? 0;
        internal string DescribeForecast() => predictor?.Diagnostics.Describe(MovementForecast);
#endif

        private void OnValidate()
        {
            if (profileOverrides != null)
                profileOverrides.Transition.Value = Mathf.Clamp01(profileOverrides.Transition.Value);
            if (settings == null)
                settings = new CharacterMovementSettings();
        }
    }
}
