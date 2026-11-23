using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>配置动画流水线并同步接收固定步结果，在 Update 中、Animator 求值前提交当前有效的动画组合。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterBehaviorDriver))]
    [DefaultExecutionOrder(100)]
    [AddComponentMenu("3C/角色行为动画")]
    public sealed class CharacterBehaviorAnimator : MonoBehaviour
    {
        // 保留旧组件 GUID 和字段名，现有预制体无需迁移即可播放全身动画。
        [Header("行为动画映射")]
        [SerializeField, Min(0), Tooltip("全身动画所在的基础层索引。")]
        private int layerIndex;
        [SerializeField, Tooltip("待机状态完整路径，包含层和子状态机名称。")]
        private string idleStateName = "Base Layer.idle";
        [SerializeField, Tooltip("移动状态完整路径，持续移动不重复从头播放。")]
        private string moveStateName = "Base Layer.move";
        [SerializeField, Tooltip("低速行走状态；留空时沿用旧版移动状态，兼容已有控制器。")]
        private string walkStateName;
        [SerializeField, Tooltip("持续冲刺循环的完整状态路径；缺少该状态时使用跑步。")]
        private string sprintStateName = "Base Layer.sprint";
        [SerializeField, Tooltip("短突进状态完整路径，每次新突进重新播放。")]
        private string dashStateName = "Base Layer.dash";
        [SerializeField, Tooltip("普通待机和移动的全身优先级；同优先级的局部需求不能覆盖全身。")]
        private int locomotionPriority;
        [SerializeField, Tooltip("冲刺的全身优先级，可用于暂时压住较低优先级的局部动作。")]
        private int dashPriority = 100;
        [SerializeField, Range(0f, 1f), Tooltip("普通行为动画的归一化过渡时长；相对源状态长度，建议 0.12。")]
        private float behaviorTransitionDuration = 0.12f;
        [SerializeField, Range(0f, 1f), Tooltip("冲刺进入与退出的归一化过渡时长上限，建议短于普通移动。")]
        private float dashTransitionDuration = 0.04f;

        [Header("运动选择（米/秒）")]
        [SerializeField, Min(0f), Tooltip("运动中降到此速度时进入待机；应小于离开待机的阈值。")]
        private float idleEnterSpeed = 0.08f;
        [SerializeField, Min(0f), Tooltip("待机中超过此速度时开始走路或跑步。")]
        private float idleExitSpeed = 0.15f;
        [SerializeField, Min(0f), Tooltip("走路达到此速度时切换跑步。")]
        private float runEnterSpeed = 3.5f;
        [SerializeField, Min(0f), Tooltip("跑步低于此速度时切换走路；应小于进入跑步的阈值。")]
        private float runExitSpeed = 3f;
        [SerializeField, Min(0f), Tooltip("请求持续冲刺且实际速度达到此值时进入冲刺循环。")]
        private float sprintEnterSpeed = 6f;
        [SerializeField, Min(0f), Tooltip("持续冲刺中实际速度低于此值时退回走跑。")]
        private float sprintExitSpeed = 5.6f;
        [SerializeField, Min(0.01f), Tooltip("步行素材以 1 倍速播放时的前进速度（米/秒），供步行和步行起步换算步频。")]
        private float walkAnimationSpeed = 1.55f;
        [SerializeField, Min(0.01f), Tooltip("跑步素材以 1 倍速播放时的前进速度（米/秒），供跑步和跑步起步换算步频。")]
        private float runAnimationSpeed = 4.98f;
        [SerializeField, Min(0.01f), Tooltip("持续冲刺素材的名义速度，用于按实际速度调整步频。")]
        private float sprintAnimationSpeed = 6.52f;
        [SerializeField, Min(0f), Tooltip("短突进素材以 1 倍速播放时的前进速度（米/秒）。0 表示按素材原速播放。")]
        private float dashAnimationSpeed;

        [Header("运动阶段")]
        [SerializeField, Min(0f), Tooltip("从停止进入移动时保留起步阶段的时长，单位秒；0 表示禁用。")]
        private float locomotionStartSeconds = 0.22f;
        [SerializeField, Min(0f), Tooltip("反向转身阶段的时长，单位秒；0 表示禁用。")]
        private float locomotionTurnSeconds = 0.32f;
        [SerializeField, Min(0f), Tooltip("连续主动地面位移至少达到此距离才收步，单位米。")]
        private float minimumStopTravel = 0.09f;
        [SerializeField] private CharacterStopAnimationSettings walkStop;
        [SerializeField] private CharacterStopAnimationSettings runStop;
        [SerializeField] private CharacterStopAnimationSettings sprintStop;
        [SerializeField, Range(90f, 180f), Tooltip("触发反向转身所需的移动方向夹角，单位度。")]
        private float locomotionReverseAngle = 120f;
        [SerializeField, Tooltip("行走起步的完整状态路径；留空或不可用时使用普通行走。")]
        private string walkStartStateName;
        [SerializeField, Range(0f, 1f), Tooltip("行走起步素材的归一化起点。")]
        private float walkStartNormalizedTime;
        [SerializeField, Tooltip("跑步起步的完整状态路径；留空或不可用时使用普通跑步。")]
        private string runStartStateName;
        [SerializeField, Tooltip("跑步起步状态使用的片段，用于把支撑脚相位换算为固定时间混合的入点。")]
        private AnimationClip runStartClip;
        [SerializeField, Min(0f), Tooltip("普通起跑从走姿展开到跑姿的混合时间（秒）。")]
        private float walkToRunBlendSeconds = 0.14f;
        [SerializeField, Range(0f, 1f), Tooltip("跑步起步素材的归一化起点。")]
        private float runStartNormalizedTime;
        [SerializeField, Tooltip("向左反向转身的完整状态路径；留空或不可用时使用当前基础移动状态。")]
        private string turnLeftStateName;
        [SerializeField, Tooltip("向右反向转身的完整状态路径；留空或不可用时使用当前基础移动状态。")]
        private string turnRightStateName;
        [SerializeField, Tooltip("基础层循环移动状态的左右脚触地相位。无有效映射时使用动作配置的播放起点。")]
        private CharacterLocomotionGaitPhaseSettings[] locomotionGaitPhases;

        [Header("地面动作匹配")]
        [SerializeField, InspectorName("动作数据库")] private CharacterMotionDatabase motionDatabase;
        [SerializeField, InspectorName("匹配模式")] private CharacterMatchMode motionMatching;
        [SerializeField, Min(0f), Tooltip("续播相对新入点的代价宽限，越大越不易换片。")]
        private float matchSwitchMargin = 0.12f;
        [SerializeField, Range(0f, 0.15f), Tooltip("反向输入后允许原动作继续的最长表现时间（秒）。")]
        private float matchReverseHold = 0.08f;
        [SerializeField, Range(15f, 180f), Tooltip("持续移动中触发选姿的输入与速度改向角（度）。")]
        private float matchTurnAngle = 55f;

        [Header("局部动画层")]
        [SerializeField, Tooltip("带下身 AvatarMask 的 Override 层索引；-1 表示未配置。不可与全身层、上身层相同。")]
        private int lowerBodyLayerIndex = 1;
        [SerializeField, Tooltip("带上身 AvatarMask 的 Override 层索引；-1 表示未配置。不可与全身层、下身层相同。")]
        private int upperBodyLayerIndex = 2;
        [SerializeField, Tooltip("外部仅提交语义键和实例编号；动画路径、部位、优先级和起始进度均在此映射。")]
        private CharacterAnimationActionSettings[] actions = CharacterAnimationActionSettings.CreateDefaults();

        [Header("分区惯性")]
        [SerializeField, Tooltip("基础姿态依次经过胸肩和双臂惯性、最终手脚 IK。强度为零时沿用原有姿态。")]
        private CharacterPoseInertiaSettings poseInertia = new CharacterPoseInertiaSettings();

        [Header("姿态求解（IK）")]
        [SerializeField, Tooltip("按列表顺序求解。动画许可由素材曲线控制，未配置的角色沿用基础姿态。")]
        private CharacterIkChannel[] ikChannels;
        [SerializeField, Min(0f), Tooltip("角色单次表现更新移动超过此距离时清空接触锚点，单位米；0 关闭自动检测。")]
        private float ikTeleportDistance = 2f;
        [SerializeField, Tooltip("播放角色动画的动画器。留空时使用当前对象上的动画器，兼容原有预制体。")]
        private Animator animator;
        private CharacterBehaviorDriver driver;
        private CharacterAnimationPipeline pipeline;

        /// <summary>最后一个有效固定步的运动信息；暂停、停用或等待生命周期恢复后的新步时无效。</summary>
        public CharacterAnimationMotion Motion => isActiveAndEnabled && driver != null && pipeline != null
            ? pipeline.ReadMotion(driver.LatestFrame, driver.StateVersion, driver.SimulationVersion, driver.IsSimulating)
            : default;

        private void Awake()
        {
            driver = GetComponent<CharacterBehaviorDriver>();
            if (animator == null)
                animator = GetComponent<Animator>();
        }

        private void OnEnable()
        {
            pipeline = new CharacterAnimationPipeline(animator, new CharacterAnimationConfiguration
            {
                MatchTurnAngle = matchTurnAngle, MatchDatabase = motionDatabase, MatchMargin = matchSwitchMargin, MatchReverseHold = matchReverseHold,
                FullLayer = layerIndex, LowerLayer = lowerBodyLayerIndex, UpperLayer = upperBodyLayerIndex,
                IdleState = idleStateName, MoveState = moveStateName, WalkState = walkStateName,
                SprintState = sprintStateName, DashState = dashStateName,
                LocomotionPriority = locomotionPriority, DashPriority = dashPriority,
                Transition = behaviorTransitionDuration, DashTransition = dashTransitionDuration,
                IdleEnterSpeed = idleEnterSpeed, IdleExitSpeed = idleExitSpeed,
                RunEnterSpeed = runEnterSpeed, RunExitSpeed = runExitSpeed,
                SprintEnterSpeed = sprintEnterSpeed, SprintExitSpeed = sprintExitSpeed,
                WalkSpeed = walkAnimationSpeed, RunSpeed = runAnimationSpeed, SprintSpeed = sprintAnimationSpeed,
                DashClipSpeed = dashAnimationSpeed,
                LocomotionStartSeconds = locomotionStartSeconds, LocomotionTurnSeconds = locomotionTurnSeconds,
                LocomotionReverseAngle = locomotionReverseAngle,
                WalkStartState = walkStartStateName, WalkStartNormalizedTime = walkStartNormalizedTime,
                RunStartState = runStartStateName, RunStartNormalizedTime = runStartNormalizedTime,
                RunStartClip = runStartClip, WalkToRunBlendSeconds = walkToRunBlendSeconds,
                TurnLeftState = turnLeftStateName, TurnRightState = turnRightStateName,
                MinimumStopTravel = minimumStopTravel, WalkStop = walkStop, RunStop = runStop, SprintStop = sprintStop,
                LocomotionGaitPhases = locomotionGaitPhases, Actions = actions,
                IkChannels = ikChannels, IkTeleportDistance = ikTeleportDistance, PoseInertia = poseInertia
            }, this, driver.LatestFrame, driver.StateVersion, driver.SimulationVersion);
            driver.FrameCommitted += ReceiveFrame;
            driver.StateReset += ResetAnimationState;
        }

        private void Update()
        {
            pipeline.Matcher.Mode = motionMatching;
            pipeline.Update(driver.LatestFrame, driver.StateVersion, driver.SimulationVersion,
                driver.IsSimulating, Time.deltaTime);
        }

        /// <summary>提交 Inspector 中已映射的动作。同键实例号从正数递增，重复提交保持进度，更大编号从配置起点重播。
        /// 需求持续到撤销、停用或角色重置，获选后受压制时按映射的中断策略处理。</summary>
        /// <returns>已接收或实例仍有效时返回 true。资源无效、旧实例或已取消实例返回 false。</returns>
        public bool SubmitAction(string semanticKey, long instanceId)
        {
            return SubmitAction(new CharacterAnimationRequest(semanticKey, instanceId));
        }

        /// <summary>提交已通过绑定预检的需求。同实例更新局部权重（0～1）时保持进度，全身权重固定为 1。
        /// 新实例或重新获选时应用播放起点，非法浮点参数按 0 处理。</summary>
        public bool SubmitAction(CharacterAnimationRequest request)
        {
            return isActiveAndEnabled && pipeline != null && driver != null &&
                pipeline.Submit(request, driver.LatestFrame, driver.StateVersion, driver.SimulationVersion);
        }

        /// <summary>撤销指定键的当前实例；旧实例的迟到撤销不会误删新实例。撤销后下次 Update 恢复组合。</summary>
        public bool WithdrawAction(string semanticKey, long instanceId)
        {
            return pipeline != null && pipeline.Withdraw(semanticKey, instanceId);
        }

        /// <summary>启用指定部位的目标来源。实例号在同部位同来源内单调递增；场景目标须独立于角色。
        /// 可选动作键将来源限制到同编号的获选动作。不同场景目标提供者可使用各自固定的 providerKey 并独立编号。
        /// 优先级越大越优先，同级保持当前目标。</summary>
        public bool SubmitIkTarget(CharacterIkLimb limb, CharacterIkSource source, long instanceId,
            Transform target = null, int priority = 10, string actionKey = null, string providerKey = null)
        {
            return pipeline != null && pipeline.Ik.Submit(limb, source, instanceId, target, priority, actionKey, providerKey);
        }

        /// <summary>只撤销相同来源的当前实例；随后恢复该来源的 Inspector 默认设置。</summary>
        public bool WithdrawIkTarget(CharacterIkLimb limb, CharacterIkSource source, long instanceId, string providerKey = null) =>
            pipeline != null && pipeline.Ik.Withdraw(limb, source, instanceId, providerKey);

        /// <summary>明确瞬移或参考系突变时清除接触、惯性和差分历史，保留已提交的目标请求。</summary>
        public void ResetIkContacts() => pipeline?.ResetPoseHistory();

        /// <summary>清空外部需求及去重历史，恢复行为意图并等待新的运动观测，保留驱动器暂停设置。</summary>
        public void ResetAnimationState()
        {
            if (pipeline != null)
                pipeline.Reset(driver.LatestFrame, driver.StateVersion, driver.SimulationVersion);
        }

        private void ReceiveFrame(CharacterControllerFrameResult frame)
        {
            pipeline.Receive(frame, driver.StateVersion, driver.SimulationVersion, driver.IsSimulating);
        }

#if UNITY_EDITOR
        internal string PreviousReplay { get; private set; }
        internal CharacterMatchReplay Replay => driver != null ? driver.MatchReplay : null;
        internal void BeginReplay(GroundReplayCase scenario, bool shiftPhase)
        {
            if (driver == null || !isActiveAndEnabled || Replay != null && Replay.Active) return;
            PreviousReplay = Replay?.Describe();
            driver.MatchReplay = new CharacterMatchReplay(transform.forward, scenario, motionMatching,
                shiftPhase && runStartClip ? runStartClip.length * 0.5f : 0f);
        }

        private void LateUpdate()
        {
            if (Replay != null && Replay.Mode != motionMatching) Replay.Cancel();
            if (pipeline != null) Replay?.Observe(pipeline);
        }

        internal string ReadMatchDebug() => pipeline?.Matcher.Describe() ?? "";
        internal string ReadIkDebug() => pipeline?.Ik.Describe() ?? "角色行为动画未启用";
        /// <summary>读取动画更新已发布的值和历史，不访问运动观察器或触发播放。</summary>
        internal CharacterAnimationDebugSnapshot ReadRuntimeDebug(out ICharacterAnimationDebugHistory history)
        {
            history = null;
            return isActiveAndEnabled && driver != null && pipeline != null
                ? pipeline.ReadDebug(driver.StateVersion, driver.SimulationVersion, driver.IsSimulating, out history)
                : new CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus.Inactive);
        }
#endif

        private void OnDisable()
        {
#if UNITY_EDITOR
            Replay?.Cancel();
#endif
            if (driver != null)
            {
                driver.FrameCommitted -= ReceiveFrame;
                driver.StateReset -= ResetAnimationState;
            }
            pipeline?.Release();
            pipeline = null;
        }
    }
}
