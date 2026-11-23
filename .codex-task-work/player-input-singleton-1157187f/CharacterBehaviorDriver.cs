using System;
using System.Collections.Generic;
using ThreeC.FollowCamera.Contracts;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ThreeC.CharacterBehavior
{
    /// <summary>逐帧采集输入；固定步先发布上一步物理结果，再提交本步行为与下一轮输入。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    [AddComponentMenu("3C/角色行为驱动器")]
    public sealed class CharacterBehaviorDriver : MonoBehaviour
    {
        [Header("角色输入")]
        [SerializeField, InspectorName("移动"), Tooltip("二维移动方向，保留摇杆推量。")]
        private InputActionReference moveAction;
        [SerializeField, InspectorName("步行"), Tooltip("按住时步行，同时按住持续冲刺时优先步行。")]
        private InputActionReference walkAction;
        [SerializeField, InspectorName("持续冲刺")] private InputActionReference sprintAction;
        [SerializeField, InspectorName("短突进"), Tooltip("每次按下只请求一次短突进。")]
        private InputActionReference dashAction;
        [SerializeField, InspectorName("持物"), Tooltip("按住请求持物。留空时角色不注册持物行为。")]
        private InputActionReference guardAction;
        [SerializeField, Tooltip("提供相机的水平移动方向。未配置或方向不可用时沿世界 XZ 轴移动。")]
        private CameraControlSource cameraControlSource;

        [Header("角色配置")]
        [SerializeField, Tooltip("在配置资产中编辑共享参数。角色初始化时绑定此资产。")] private CharacterProfile profile;
        [SerializeField, InspectorName("实例覆盖")] private CharacterProfileOverrides profileOverrides = new CharacterProfileOverrides();

        private CharacterProfile initializedProfile;
        private bool profileInitialized;
        internal CharacterProfile Profile => profileInitialized ? initializedProfile : profile;
        internal CharacterProfileOverrides Overrides => profileOverrides;

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
        private readonly Queue<CharacterControllerFrameResult> pendingTransitions =
            new Queue<CharacterControllerFrameResult>();
        private readonly object frameSubscriptionLock = new object();
        private Action<CharacterControllerFrameResult> frameCommitted;
        private volatile Delegate[] frameCommittedHandlers = Array.Empty<Delegate>();
        private bool hasInputFocus = true;
        private bool applicationPaused;
        private bool gameplayPaused;
        private bool suppressDashUntilRelease;
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

        /// <summary>LateUpdate 按固定步顺序分发开始和结束事件，同一步的变化合并交付。
        /// 暂停保留队列，停用延至恢复，重置丢弃历史并中止旧通知。订阅与退订应配对。</summary>
        public event Action<CharacterControllerFrameResult> BehaviorTransition;

        /// <summary>下一固定步开始时同步发布上一步的位移和朝向，暂停前已求解的最后一步也会交付。
        /// 每步独立通知，回调应避免再次推进模拟。订阅者异常不影响后续交付，重置或停用会中止当前通知。</summary>
        public event Action<CharacterControllerFrameResult> FrameCommitted
        {
            add
            {
                lock (frameSubscriptionLock)
                {
                    frameCommitted += value;
                    frameCommittedHandlers = frameCommitted?.GetInvocationList() ?? Array.Empty<Delegate>();
                }
            }
            remove
            {
                lock (frameSubscriptionLock)
                {
                    frameCommitted -= value;
                    frameCommittedHandlers = frameCommitted?.GetInvocationList() ?? Array.Empty<Delegate>();
                }
            }
        }

        /// <summary>显式重置状态后同步通知表现层清理持续需求，不代表普通暂停或停用。</summary>
        public event Action StateReset;

        private void Awake()
        {
            initializedProfile = profile;
            profileInitialized = true;
            body = GetComponent<Rigidbody>();
            capsule = GetComponent<CapsuleCollider>();
            rigidbodyMotor.Initialize(body, capsule, Profile ? Profile.Motor : motor);
            ResetBehaviorFlow();
        }

        private void ResetBehaviorFlow()
        {
            behaviorFlow ??= CreateBehaviorFlow();
            behaviorFlow.Reset(ReadMovement());
        }

        private CharacterBehaviorFlow CreateBehaviorFlow()
        {
            if (guardAction == null || guardAction.action == null)
                return new CharacterBehaviorFlow();

            // 持物可与移动并行。短突进打断持物后，持续按键会重新申请。
            var guard = new CharacterBehaviorRule(CharacterBehaviorId.Guard, new CharacterGuardBehavior(),
                conflicts: CharacterBehaviorId.Dash);
            var interrupt = new CharacterBehaviorInterrupt(CharacterBehaviorId.Dash, CharacterBehaviorId.Guard);
            return new CharacterBehaviorFlow(new[] { guard }, new[] { interrupt });
        }

        private void OnEnable()
        {
            hasInputFocus = Application.isFocused;
            SynchronizeSimulation();
        }

        private void OnDisable()
        {
            SynchronizeSimulation();
        }

        private void Update()
        {
            if (!RefreshSimulation())
                return;

            Vector2 direction = moveAction?.action?.ReadValue<Vector2>() ?? Vector2.zero;
            // 采集时将相机相对方向转为世界 XZ，幅值限制留给固定步输入黑板。
            if (cameraControlSource != null &&
                cameraControlSource.TryGetBasis(out Vector3 forward, out Vector3 right))
            {
                direction = new Vector2(right.x, right.z) * direction.x +
                    new Vector2(forward.x, forward.z) * direction.y;
            }

            bool dashPressed = !suppressDashUntilRelease && dashAction?.action?.WasPressedThisFrame() == true;
            if (!IsHeld(dashAction))
                suppressDashUntilRelease = false;
            CharacterMoveGait gait = CharacterMoveGait.Run;
            if (IsHeld(walkAction))
                gait = CharacterMoveGait.Walk;
            else if (IsHeld(sprintAction))
                gait = CharacterMoveGait.Sprint;

            bool guardHeld = IsHeld(guardAction);
            if (!guardHeld)
                suppressGuardUntilRelease = false;
            guardRequested = guardHeld && !suppressGuardUntilRelease;
            inputBlackboard.Write(direction, dashPressed, gait, guardRequested);
        }

        private static bool IsHeld(InputActionReference action) => action?.action?.IsPressed() == true;

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
            float acceleration = Profile ? profileOverrides.GroundAcceleration.Resolve(Profile.Motor.groundAcceleration) : motor.groundAcceleration;
            pendingGroundDrive = rigidbodyMotor.Drive(pendingBehavior, pendingInput, deltaTime, ground, acceleration);
            ApplyFacing(pendingBehavior, pendingInput, deltaTime);
            executionInput = inputBlackboard.Consume();
        }

        private void CommitSolvedStep()
        {
            if (pendingDuration <= 0f)
                return;
            Vector3 displacement = body.position - pendingStart;
            CollisionFlags contacts = rigidbodyMotor.ReadContacts(out _);
            var previous = LatestFrame.SimulationVersion == lifecycleVersion ? LatestFrame.Trajectory : default;
            var trajectory = pendingGroundDrive.Observe(body.velocity, displacement, pendingDuration, contacts, previous,
                body.rotation * Vector3.forward, MoveTurnSpeed);
            LatestFrame = new CharacterControllerFrameResult(pendingBehavior, pendingInput, pendingDuration,
                displacement, contacts, body.rotation * Vector3.forward, lifecycleVersion, trajectory);
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

        private void ApplyFacing(CharacterBehaviorFrameResult frame, CharacterInputSnapshot input, float deltaTime)
        {
            Vector2 targetFacing;
            float maximumAngle;
            if (frame.DashStarted)
            {
                targetFacing = frame.DashDirection;
                maximumAngle = 180f;
            }
            else if (frame.MoveApproved)
            {
                targetFacing = input.Direction;
                maximumAngle = (float)Math.Min(180d, (double)MoveTurnSpeed * deltaTime);
            }
            else
            {
                return; // 冲刺后续步和静止时保持朝向。
            }

            Vector3 forward = body.rotation * Vector3.forward;
            if (maximumAngle <= 0f ||
                !InputMath.TryNormalizeDirection(new Vector2(forward.x, forward.z), out Vector2 current) ||
                !InputMath.TryNormalizeDirection(targetFacing, out Vector2 target))
                return;

            double cross = (double)current.y * target.x - (double)current.x * target.y;
            double dot = (double)current.x * target.x + (double)current.y * target.y;
            float angle = (float)(Math.Atan2(cross, dot) * (180d / Math.PI));
            if (angle == -180f)
                angle = 180f; // 正反向等距时固定绕世界 +Y 转动。

            float step = Mathf.Clamp(angle, -maximumAngle, maximumAngle);
            if (step != 0f)
                body.MoveRotation(Quaternion.AngleAxis(step, Vector3.up) * body.rotation); // 只叠加世界偏航，保留俯仰和滚转。
        }

        private void PublishCommittedFrame(CharacterControllerFrameResult frame)
        {
            Delegate[] handlers = frameCommittedHandlers; // 回调中增减订阅从下个物理步生效。
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
            if (simulationRunning != CanSimulate()) // 时间缩放和碰撞体启停没有对应的生命周期回调。
                SynchronizeSimulation();
            return simulationRunning;
        }

        private void ClearInput()
        {
            // 暂停时清空输入，冻结冲刺进度、冷却和体力。
            inputBlackboard.Clear();
#if UNITY_EDITOR
            if (MatchReplay?.Started == true) MatchReplay.Cancel();
#endif
            executionInput = default;
            suppressDashUntilRelease = IsHeld(dashAction) || dashAction?.action?.WasPressedThisFrame() == true;
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

        private void OnValidate()
        {
            if (profileOverrides != null)
                profileOverrides.Transition.Value = Mathf.Clamp01(profileOverrides.Transition.Value);
            if (settings == null)
                settings = new CharacterMovementSettings();
        }
    }

    /// <summary>完成位移与转向后的只读结果，关联本步输入、行为结果、实际世界位移、朝向及碰撞方向。</summary>
    public readonly struct CharacterControllerFrameResult
    {
        public CharacterBehaviorFrameResult Behavior { get; }
        public CharacterInputSnapshot Input { get; }
        public long FrameNumber => Behavior.FrameNumber;
        /// <summary>本逻辑步的模拟时长，单位为秒。</summary>
        public float DeltaTime { get; }
        public Vector3 ExpectedDisplacement => new Vector3(Behavior.Displacement.x, 0f, Behavior.Displacement.y);
        public Vector3 ActualDisplacement { get; }
        public CharacterGroundTrajectory Trajectory { get; }
        public CollisionFlags CollisionFlags { get; }
        /// <summary>本步应用位移和转向后的实际世界朝向，供表现层解析相对移动。</summary>
        public Vector3 FacingDirection { get; }
        /// <summary>提交本步时的模拟连续性代次；恢复后旧代次速度不可作为新观测。</summary>
        public int SimulationVersion { get; }

        internal CharacterControllerFrameResult(CharacterBehaviorFrameResult behavior,
            CharacterInputSnapshot input, float deltaTime, Vector3 actualDisplacement, CollisionFlags collisionFlags,
            Vector3 facingDirection = default, int simulationVersion = 0, CharacterGroundTrajectory trajectory = default)
        {
            Behavior = behavior;
            Input = input;
            DeltaTime = deltaTime;
            ActualDisplacement = actualDisplacement;
            Trajectory = trajectory;
            CollisionFlags = collisionFlags;
            FacingDirection = facingDirection;
            SimulationVersion = simulationVersion;
        }
    }
}
