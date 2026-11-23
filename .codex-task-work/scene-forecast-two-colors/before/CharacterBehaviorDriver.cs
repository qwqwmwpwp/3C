using System;
using System.Collections.Generic;
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
        [Header("角色输入")]
        [SerializeField, InspectorName("接收玩家输入")] private bool receivePlayerInput;
        [SerializeField, InspectorName("允许持物"), Tooltip("初始化时为角色注册持物行为。")] private bool allowGuard;
        [SerializeField, Tooltip("提供相机的水平移动方向。未配置或方向不可用时沿世界 XZ 轴移动。")]
        private CameraControlSource cameraControlSource;

        [Header("角色配置")]
        [SerializeField, Tooltip("在配置资产中编辑共享参数。角色初始化时绑定此资产。")] private CharacterProfile profile;
        [SerializeField, InspectorName("实例覆盖")] private CharacterProfileOverrides profileOverrides = new CharacterProfileOverrides();

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
        private readonly CharacterMovementSettings resolvedMovement = new CharacterMovementSettings();

        private CharacterMovementSettings ReadMovement()
        {
            if (!Profile) return settings;
            Profile.ReadMovement(resolvedMovement);
            return resolvedMovement;
        }

#if UNITY_EDITOR
        internal CharacterMatchReplay MatchReplay { get; set; }
#endif
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
        public void InvalidateMovementPrediction() => predictor?.Invalidate("外部状态变化");

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
        private readonly Queue<CharacterControllerFrameResult> pendingTransitions =
            new Queue<CharacterControllerFrameResult>();
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
        private bool PresentationGuardHeld
        {
            get
            {
#if UNITY_EDITOR
                if (MatchReplay?.Active == true) return LatestFrame.Input.GuardHeld;
#endif
                return guardRequested;
            }
        }
        /// <summary>当前仍请求且已提交的持物许可编号，输入释放或模拟中断时为零。</summary>
        internal long CommittedGuardApproval => PresentationGuardHeld && LatestFrame.SimulationVersion == lifecycleVersion
            && (LatestFrame.Behavior.ActiveBehaviors & CharacterBehaviorId.Guard) != CharacterBehaviorId.None
            && CanSimulate() ? guardApprovals : 0;

        /// <summary>LateUpdate 按固定步顺序分发开始和结束事件，同一步的变化合并交付。
        /// 暂停保留队列，停用延至恢复，重置丢弃历史并中止旧通知。订阅与退订应配对。</summary>
        public event Action<CharacterControllerFrameResult> BehaviorTransition;

        /// <summary>下一固定步开始时同步发布上一步的位移和朝向，暂停前已求解的最后一步也会交付。
        /// 每步独立通知，回调应避免再次推进模拟。订阅者异常不影响后续交付，重置或停用会中止当前通知。</summary>
        public event Action<CharacterControllerFrameResult> FrameCommitted
        {
            add => frameListeners.Committed += value;
            remove => frameListeners.Committed -= value;
        }

        /// <summary>显式重置状态后同步通知表现层清理持续需求，不代表普通暂停或停用。</summary>
        public event Action StateReset;

        private void Awake()
        {
            initializedProfile = profile;
            profileInitialized = true;
            body = GetComponent<Rigidbody>();
            capsule = GetComponent<CapsuleCollider>();
            rigidbodyMotor.Initialize(body, capsule, Motor, Profile ? Profile.RunFootfallDistance() : CharacterMotorSettings.DefaultFootfallDistance);
            ResetBehaviorFlow();
            var geometry = new CharacterForecastGeometry(body, capsule, rigidbodyMotor.Steps);
            predictor = new CharacterMovementPredictor(CreateBehaviorFlow(), geometry);
            predictor.Suppress(suppressPrediction);
        }

        private void ResetBehaviorFlow()
        {
            behaviorFlow ??= CreateBehaviorFlow();
            behaviorFlow.Reset(ReadMovement());
        }

        private CharacterBehaviorFlow CreateBehaviorFlow()
        {
            return CharacterBehaviorFlow.ForCharacter(allowGuard, Profile ? Profile.LightAttack : null);
        }

        private void OnEnable()
        {
            hasInputFocus = Application.isFocused;
            SynchronizeSimulation();
            PlayerInputSystem.AttackPressed += RecordAttackPress;
        }

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
                predictor.Invalidate("攻击按下");
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
            CharacterMoveGait gait = CharacterMoveGait.Run;
            if (input.WalkHeld)
                gait = CharacterMoveGait.Walk;
            else if (input.SprintHeld)
                gait = CharacterMoveGait.Sprint;

            if (!input.GuardHeld)
                suppressGuardUntilRelease = false;
            guardRequested = allowGuard && input.GuardHeld && !suppressGuardUntilRelease;
            inputBlackboard.Write(direction, dashPressed, gait, guardRequested);
            if (dashPressed) predictor.Invalidate("短突进按下");
            RefreshPrediction();
        }

        internal CharacterMovementForecast AnimationForecast
        {
            get
            {
                if (!ForecastSettings.Enabled || !ForecastSettings.DriveAnimation || !CanSimulate()) return null;
                if (!Time.inFixedTimeStep && predictor.NeedsRefresh) RefreshPrediction();
                return MovementForecast;
            }
        }

        private void RefreshPrediction()
        {
            if (!ForecastSettings.Enabled)
            {
                predictor.Invalidate("预测停用");
                return;
            }
            var locked = executionInput;
            var buffered = inputBlackboard.Peek();
#if UNITY_EDITOR
            if (MatchReplay?.Active == true)
                locked = buffered = new CharacterInputSnapshot(pendingInput.Direction, false, pendingInput.GuardHeld,
                    CharacterBehaviorId.None, pendingInput.Gait);
#endif
            // Update 在本帧最后一次物理求解之后；行为/马达已走完 pendingBehavior，刚体也处于同一步末。
            var solved = pendingDuration > 0f ? pendingBehavior : LatestFrame.Behavior;
            var origin = rigidbodyMotor.CaptureForecast(Time.fixedTimeAsDouble, solved,
                pendingDuration > 0f ? pendingInput.Gait : LatestFrame.Input.Gait);
            predictor.Refresh(Time.timeAsDouble, origin, behaviorFlow, new CharacterForecastInputs(locked, buffered),
                ReadMovement(), rigidbodyMotor.Parameters(GroundAcceleration, MoveTurnSpeed), ForecastSettings, profileOverrides);
        }

        private void FixedUpdate()
        {
            if (!RefreshSimulation())
                return;
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
#if UNITY_EDITOR
            if (MatchReplay != null && MatchReplay.TryInput(deltaTime, out var recorded)) pendingInput = recorded;
#endif
            pendingBehavior = behaviorFlow.Tick(pendingInput, facing, deltaTime, ReadMovement());
            pendingStart = body.position;
            pendingDuration = deltaTime;
            pendingTime = Time.fixedTimeAsDouble;
            rigidbodyMotor.ReadContacts(out Vector3 ground);
            var drive = rigidbodyMotor.Parameters(GroundAcceleration, MoveTurnSpeed);
            pendingGroundDrive = rigidbodyMotor.Drive(pendingBehavior, pendingInput, deltaTime, ground, drive);
            Quaternion next = CharacterMotorStep.Facing(body.rotation, pendingBehavior, pendingInput, MoveTurnSpeed, deltaTime);
            if (!next.Equals(body.rotation)) body.MoveRotation(next);
            executionInput = inputBlackboard.Consume();
        }

        private void CommitSolvedStep()
        {
            if (pendingDuration <= 0f)
                return;
            LastSolvedTime = pendingTime;
            Vector3 displacement = body.position - pendingStart;
            CollisionFlags contacts = rigidbodyMotor.ReadContacts(out _);
            var previous = LatestFrame.SimulationVersion == lifecycleVersion ? LatestFrame.Trajectory : default;
            var trajectory = pendingGroundDrive.Observe(body.velocity, displacement, pendingDuration, contacts, previous,
                body.rotation * Vector3.forward, MoveTurnSpeed);
            LatestFrame = new CharacterControllerFrameResult(pendingBehavior, pendingInput, pendingDuration,
                displacement, contacts, body.rotation * Vector3.forward, lifecycleVersion, trajectory);
            predictor?.ObserveSolvedStep(pendingBehavior.FrameNumber, pendingTime, new CharacterForecastSample(body.position,
                body.velocity, body.rotation, pendingBehavior, contacts, pendingInput.Gait));
#if UNITY_EDITOR
            MatchReplay?.OnStep(LatestFrame);
#endif
            if ((pendingBehavior.StartedBehaviors & CharacterBehaviorId.Guard) != CharacterBehaviorId.None)
                guardApprovals++;
            pendingDuration = 0f;
            if (pendingBehavior.HasTransitions && BehaviorTransition != null)
                pendingTransitions.Enqueue(LatestFrame);
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

        private void LateUpdate()
        {
            while (pendingTransitions.Count > 0)
            {
                CharacterControllerFrameResult frame = pendingTransitions.Dequeue(); // 已完成事件跨暂停保留，先出队再回调。
                Action<CharacterControllerFrameResult> handlers = BehaviorTransition;
                if (handlers == null)
                    continue;

                int version = resetVersion;
                foreach (Action<CharacterControllerFrameResult> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(frame);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception, this); // 单个订阅者异常后继续分发。
                    }

                    if (version != resetVersion)
                        return;
                }

                if (this == null || !isActiveAndEnabled) // 停用时将剩余事件留待恢复。
                    return;
            }
        }

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
            rigidbodyMotor.ResetMotion();
            pendingTransitions.Clear();
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
            rigidbodyMotor.SetSuspended(!simulationRunning);
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
            predictor?.Invalidate("模拟边界");
#if UNITY_EDITOR
            predictor?.Diagnostics.Reset();
#endif
            inputBlackboard.Clear();
#if UNITY_EDITOR
            if (MatchReplay?.Started == true) MatchReplay.Cancel();
#endif
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
        internal string DescribeForecast() => predictor?.Diagnostics.Describe(MovementForecast);
        private void OnDrawGizmosSelected()
        {
            var color = Gizmos.color;
            MovementForecast?.Draw();
            predictor?.Diagnostics.DrawActual();
            Gizmos.color = color;
        }
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
