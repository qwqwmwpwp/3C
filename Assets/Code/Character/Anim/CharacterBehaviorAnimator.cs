using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>接收控制器发布的运动、表现状态和轨迹，在引擎求值前提交人形动画。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    [AddComponentMenu("人形动画/动画宿主")]
    public sealed class CharacterBehaviorAnimator : MonoBehaviour
    {
        [SerializeField, Tooltip("播放角色动画的动画器。留空时使用当前对象上的动画器。")]
        private Animator animator;
        [SerializeField, Min(0f), InspectorName("斜坡模型最大下沉（米）"), Tooltip("仅作用于独立的模型子节点。零表示关闭。")]
        internal float modelDropLimit;
        [SerializeField, InspectorName("模型绑定")] private CharacterModelBindings modelBindings = new CharacterModelBindings();
        [SerializeField, InspectorName("动画表现配置")] private CharacterAnimationConfig animationConfig;
        [SerializeField, InspectorName("后台规划")] private bool useWorkerThreads = true;
        [SerializeField, Min(0f), InspectorName("步行换跑速度（米/秒）")] private float walkSpeed = 3.5f;
        [SerializeField, Range(0f, 90f), InspectorName("足部可行走坡度")] private float walkableSlope = 45f;
        private CharacterAnimationMovement latestFrame;
        private CharacterAnimationContext context;
        private AnimationTrajectoryPublication trajectory;
        internal AnimationTrajectoryPublication CurrentTrajectory => trajectory;
        private bool paused, suppliedClock, waitingForMotionEpoch;
        private double lastMotionTime = double.NegativeInfinity, lastContextTime = double.NegativeInfinity;
        private long lastMotionNumber;
        private int stateVersion, simulationVersion;
        internal Dictionary<string, long> ActionInstances { get; } = new Dictionary<string, long>(System.StringComparer.Ordinal);
        /// <summary>当前输入有效期凭据。异步来源在开始读取前保存，提交结果时原样传回。</summary>
        public long InputVersion { get; private set; }
        private double suppliedTime, localOrigin, pauseStart;
        private double ClockTime => suppliedClock ? suppliedTime : (paused ? pauseStart : Time.timeAsDouble) - localOrigin;
        /// <summary>以秒为单位的宿主动画时钟。播放事件中返回当前实际求值区间的截止时刻，供定时动作与轨迹采样共用。</summary>
        public double AnimationTime => pipeline?.EvaluationTime ?? ClockTime;

        /// <summary>采集和最终提交之前同步通知主线程输入源发布最新快照。</summary>
        public event System.Action PreparingPresentation;
        /// <summary>已结束的声部真实求值后、剩余时间选姿前，允许接入同帧的后续动作。</summary>
        public event System.Action PlaybackSettled;

        // 在主线程发布已真实结算的回执，后续请求重新参加本次仲裁。
        internal void PublishPlaybackSettled() => PlaybackSettled?.Invoke();

        /// <summary>重建流水线时递增，输入源可为新绑定恢复当前已批准的持续状态。</summary>
        public int BindingVersion { get; private set; }
        /// <summary>有效配置和播放管线已经装配，可接收动作与固定步。</summary>
        public bool IsBound => isActiveAndEnabled && pipeline != null;

        private CharacterModuleSelection installedSelection, pendingSelection;
        private bool refreshModuleContents;
        private CharacterAnimationConfig configurationSource;
        private CharacterConfigurationRejection rejection;
        private int boundConfigRevision;
        private CharacterAnimationConfiguration boundConfig;
        private CharacterAnimationPipeline pipeline;
#if UNITY_EDITOR
        public CharacterPlaybackHistory PlaybackHistory { get; } = new CharacterPlaybackHistory();
#endif

        /// <summary>最后一个有效固定步的运动信息；暂停、停用或等待生命周期恢复后的新步时无效。</summary>
        public CharacterAnimationMotion Motion => isActiveAndEnabled && pipeline != null
            ? pipeline.ReadMotion(latestFrame, stateVersion, simulationVersion, !paused)
            : default;

        // 缓存显式指定或与宿主同对象的动画器。
        private void Awake()
        {
            if (!animator) animator = GetComponent<Animator>();
        }

        // 为当前角色或编辑器预演绑定独立的模块快照。
        internal CharacterAnimationConfiguration Configuration() =>
            BindConfiguration(animationConfig, (pendingSelection ?? installedSelection)?.Assembly);

        // 使用此模型的骨骼、步态门槛和脚部坡度参数完成装配。
        private CharacterAnimationConfiguration BindConfiguration(CharacterAnimationConfig source, AnimationModuleAssembly assembly)
        {
            if (!source) throw new System.ArgumentException("请在动画宿主中指定动画表现配置。");
            if (!source.PoseStrategy) throw new System.ArgumentException("动画表现配置缺少基础姿态策略。");
            var config = source.Bind(modelBindings, assembly);
            config.BindMovement(walkSpeed, walkableSlope);
            return config;
        }

        /// <summary>替换此角色的模块集合。可传单个模块、嵌套组合或空集合。校验失败时保留当前装配；
        /// 变更在下一次动画采集前生效，传入相同模块会重新读取其配置及匹配数据。
        /// 切换播放库或重新烘焙播放索引和遮罩后，需重新启用此组件以重建播放图。</summary>
        public bool SetAnimationModules(params AnimationModule[] roots)
        {
            if (roots == null) return false;
            return SelectModules(new List<AnimationModule>(roots), new HashSet<AnimationModule>(), refreshContents: true);
        }

        /// <summary>启用或停用一个模块或子组合。停用一个组合时，其他分支仍引用的子模块会保留。</summary>
        public bool SetAnimationModuleEnabled(AnimationModule module, bool enabled)
        {
            if (!module) return false;
            var selection = pendingSelection ?? installedSelection;
            var roots = selection == null ? new List<AnimationModule>() : new List<AnimationModule>(selection.Roots);
            if (selection == null)
            {
                var source = animationConfig;
                if (source && source.Modules) roots.Add(source.Modules);
            }
            var excluded = selection == null ? new HashSet<AnimationModule>() : new HashSet<AnimationModule>(selection.Excluded);
            if (selection != null && enabled && selection.Assembly.Contains(module)) return true;
            if (!enabled && excluded.Contains(module)) return true;
            if (enabled)
            {
                excluded.Remove(module);
                var selected = AnimationModuleAssembly.Includes(roots, excluded, module);
                if (!selected) roots.Add(module);
            }
            else excluded.Add(module);
            return SelectModules(roots, excluded, refreshContents: false);
        }

        // 验证本角色的新模块集合，在下一次动画采集前安装。
        private bool SelectModules(List<AnimationModule> roots, HashSet<AnimationModule> excluded, bool refreshContents)
        {
            var source = animationConfig;
            if (pipeline != null && source == configurationSource && source && source.PlaybackLibrary != boundConfig.PlaybackLibrary)
            {
                Debug.LogWarning(AnimationModuleChanges.PlaybackLibraryChanged, this);
                return false;
            }
            try { pendingSelection = new CharacterModuleSelection(roots, excluded); }
            catch (System.ArgumentException error) { Debug.LogWarning(error.Message, this); return false; }
            refreshModuleContents |= refreshContents;
            return true;
        }

        /// <summary>发布一个已求解运动步。步号为零时自动编号，显式步号须匹配当前连续性。独立提交渲染状态的来源可关闭同步状态发布。</summary>
        public bool SubmitMotion(in CharacterAnimationMovement movement, double? at = null, bool publishContext = true)
        {
            double time = at ?? AnimationTime;
            if (!IsBound || paused || !movement.IsValid || !Finite(time) || time < lastMotionTime) return false;
            if (waitingForMotionEpoch && movement.FrameNumber != 0) return false;
            var frame = movement;
            if (frame.FrameNumber == 0)
            {
                if (lastMotionNumber == long.MaxValue) return false;
                frame.FrameNumber = lastMotionNumber + 1;
                frame.SimulationVersion = simulationVersion;
                if (latestFrame.Context.MoveApproved && !frame.Context.MoveApproved)
                    frame.Context.MoveEnded = true;
            }
            if (frame.SimulationVersion != simulationVersion || frame.FrameNumber <= lastMotionNumber) return false;
            lastMotionNumber = frame.FrameNumber;
            latestFrame = frame;
            lastMotionTime = time;
            pipeline.Receive(frame, simulationVersion, true, time);
            if (publishContext) ApplyContext(frame.Context, time);
            return true;
        }

        /// <summary>提交当前已批准的表现状态，时间使用调用方与动画共用的时钟。</summary>
        public bool PublishContext(in CharacterAnimationContext value, long inputVersion, double? at = null)
        {
            if (!CanPublish(inputVersion)) return false;
            return ApplyContext(value, at ?? AnimationTime);
        }

        // 相同时刻的多个已批准边沿按调用顺序处理，迟到的上一时刻不能倒写。
        private bool ApplyContext(in CharacterAnimationContext value, double time)
        {
            if (!Finite(time) || time < lastContextTime) return false;
            lastContextTime = time;
            context = value;
            pipeline.ReceiveContext(value, time);
            return true;
        }

        /// <summary>发布不可变轨迹快照；可显式发布已启用但尚无可信轨迹的状态。</summary>
        public bool PublishTrajectory(AnimationTrajectoryPublication publication, long inputVersion)
        {
            if (!CanPublish(inputVersion)) return false;
            trajectory = publication;
            return true;
        }

        // 只允许当前有效期的生产者向运行中的宿主发布数据。
        private bool CanPublish(long version) => IsBound && !paused && !waitingForMotionEpoch && version == InputVersion;

        /// <summary>绑定外部时钟。更换来源或代次且回拨时中断旧动作并清空播放预算，同代次倒序时刻直接忽略。</summary>
        public void SetClock(double time, int continuityVersion, bool running = true)
        {
            if (!Finite(time) || suppliedClock && (continuityVersion < simulationVersion ||
                continuityVersion == simulationVersion && time < suppliedTime)) return;
            bool newSource = !suppliedClock;
            bool rewind = (newSource || continuityVersion != simulationVersion) && time < ClockTime;
            bool frozen = suppliedClock && paused && !running && continuityVersion == simulationVersion;
            suppliedClock = true;
            if (!frozen) suppliedTime = time;
            SetContinuity(continuityVersion, !running, newSource, rewind);
        }

        // 拒绝无法排序、无法用于轨迹和播放采样的时间戳。
        private static bool Finite(double time) => !double.IsNaN(time) && !double.IsInfinity(time);

        /// <summary>解除外部时钟并从当前角色时刻继续使用 Unity 缩放时间。</summary>
        public void UseUnityClock()
        {
            if (!suppliedClock) return;
            double time = AnimationTime;
            bool rewind = time < ClockTime;
            localOrigin = Time.timeAsDouble - time;
            suppliedClock = false;
            SetContinuity(simulationVersion + 1, false, rewind: rewind);
        }

        /// <summary>冻结或恢复动画时间。使用内置时钟时暂停区间不会计入素材进度。</summary>
        public void SetPaused(bool value)
        {
            if (paused == value) return;
            if (!suppliedClock)
            {
                if (value) pauseStart = Time.timeAsDouble;
                else localOrigin += Time.timeAsDouble - pauseStart;
            }
            SetContinuity(suppliedClock ? simulationVersion : simulationVersion + 1, value);
        }

        // 连续性变更时同步撤回旧运动、表现上下文和轨迹，保留仍可恢复的动作。
        private void SetContinuity(int version, bool freeze, bool newSource = false, bool rewind = false)
        {
            bool newEpoch = newSource || simulationVersion != version;
            if (!newEpoch && paused == freeze) return;
            if (newEpoch) waitingForMotionEpoch = false;
            simulationVersion = version;
            paused = freeze;
            ClearInputs(newEpoch);
            if (rewind) pipeline?.MotionBoundary(CharacterPlaybackEndSource.MotionBoundary, true, version);
            else pipeline?.ContinuityChanged(version);
            if (freeze) pipeline?.Freeze();
        }

        // 撤销所有在途快照；同源代次内的暂停和停用仍保留最后接受的序号和时间。
        private void ClearInputs(bool newEpoch)
        {
            latestFrame = default;
            context = default;
            trajectory = default;
            InputVersion++;
            if (!newEpoch) return;
            lastMotionNumber = 0;
            lastMotionTime = lastContextTime = double.NegativeInfinity;
        }

        /// <summary>用控制器实际采用的步行速度和最大坡度校准步态阈值及脚部探测。</summary>
        public void SetMovementCalibration(float speed, float slope)
        {
            speed = InputMath.NonNegative(speed, walkSpeed);
            slope = Mathf.Min(90f, InputMath.NonNegative(slope, walkableSlope));
            if (walkSpeed == speed && walkableSlope == slope) return;
            walkSpeed = speed;
            walkableSlope = slope;
            refreshModuleContents = true;
        }

        // 从当前缩放引擎时刻开始独立计时，启用时立即装配以接收第一步运动。
        private void OnEnable()
        {
            InputVersion++;
            if (!suppliedClock) localOrigin = paused ? pauseStart : Time.timeAsDouble;
            ApplyConfiguration(animationConfig, replace: true);
        }

        // 绑定成功后启动或重装配流水线，配置错误时等待有效配置或模块选择。
        private void ApplyConfiguration(CharacterAnimationConfig source, bool replace)
        {
            bool selectionRequested = pendingSelection != null;
            var selection = pendingSelection ?? installedSelection;
            bool refresh = refreshModuleContents;
            pendingSelection = null;
            refreshModuleContents = false;
            CharacterAnimationConfiguration next;
            CharacterAnimationPipeline replacement = null;
            AnimationModuleChanges changes = null;
            try
            {
                if ((replace || selectionRequested) && selection != null)
                    selection = new CharacterModuleSelection(selection.Roots, selection.Excluded);
                next = BindConfiguration(source, selection?.Assembly);
                if (!replace && pipeline != null) changes = pipeline.PrepareModules(next, refresh);
                else
                {
                    if (pipeline != null)
                    {
                        string issue = next.PlaybackLibrary ? next.PlaybackLibrary.ValidatePlayback() : "缺少播放库。";
                        if (issue != null || !animator || animator.avatar != next.PlaybackLibrary.BakedAvatar)
                            throw new System.ArgumentException(issue ?? "角色 Avatar 与新的播放库不匹配。");
                        pipeline.Join();
                    }
                    replacement = new CharacterAnimationPipeline(animator, next, this, latestFrame, context, stateVersion, simulationVersion, pipeline != null);
                }
            }
            catch (System.ArgumentException error)
            {
                if (rejection?.Message != error.Message) Debug.LogError(error.Message, this);
                rejection = new CharacterConfigurationRejection(error.Message, source, selection, animator);
                return;
            }
            if (changes != null)
                pipeline.Reconfigure(next, changes, context, AnimationTime);
            else
            {
                StopPipeline(CharacterPlaybackEndSource.Rebind);
                pipeline = replacement;
                BindingVersion++;
            }
            installedSelection = selection;
            boundConfig = next;
            configurationSource = source;
            boundConfigRevision = source.Revision;
            rejection = null;
        }

        // 核对当前角色搜索范围的绑定版本，供采集与延后提交共同使用。
        private bool GroundBindingsChanged => boundConfig?.SearchLibraries != null &&
            !boundConfig.SearchLibraries.Current(configurationSource ? configurationSource.GroundBindings : null);
        internal bool HasPendingConfiguration => pendingSelection != null || rejection == null &&
            (refreshModuleContents || animationConfig != configurationSource || GroundBindingsChanged ||
            animationConfig && boundConfig != null && (animationConfig.Revision != boundConfigRevision || animationConfig.PoseStrategy != boundConfig.PoseStrategy));

        // 采集行为、运动和发布资源版本，旧装配失效时先接回后台计划。
        private void Update()
        {
            using var capture = CharacterPresentationBatch.Capture.Auto();
            var source = animationConfig;
            if (rejection != null && rejection.Changed(source, animator))
            {
                if (ReferenceEquals(rejection.Source, source)) pendingSelection ??= rejection.Selection;
                refreshModuleContents = true;
                rejection = null;
            }
            bool sourceChanged = pipeline == null || !ReferenceEquals(configurationSource, source);
            if (!sourceChanged && (GroundBindingsChanged || source.Revision != boundConfigRevision ||
                source.PoseStrategy != boundConfig.PoseStrategy)) refreshModuleContents = true;
            if (pendingSelection != null || (sourceChanged || refreshModuleContents) && rejection == null)
                ApplyConfiguration(source, replace: sourceChanged);
            var current = pipeline;
            if (current == null) return;
            current.Join();
            if (!PrepareInputs(current)) return;
            if (current.Capture(latestFrame, context, stateVersion, simulationVersion, !paused, Time.deltaTime, AnimationTime, trajectory))
            {
                current.Schedule(useWorkerThreads);
                CharacterPresentationBatch.Queue(this);
            }
        }

        // 输入源读取预测时可同步停用或重置宿主；只在同一流水线和代次仍有效时继续。
        private bool PrepareInputs(CharacterAnimationPipeline current)
        {
            long version = InputVersion;
            PreparingPresentation?.Invoke();
            return this && isActiveAndEnabled && pipeline == current && version == InputVersion;
        }

        // 在所有 Update 之后核对最新预测、资源和角色代次，再执行一次最终求值。
        internal void FinishPresentation()
        {
            var current = pipeline;
            if (current == null) return;
            current.Join();
            bool valid = PrepareInputs(current);
            if (!this || !isActiveAndEnabled || pipeline != current) return;
            if (!valid || HasPendingConfiguration || !current.CanApply(stateVersion, simulationVersion, !paused))
            { current.Discard(); return; }
            current.RefreshForecast(trajectory);
            current.Apply();
        }

        /// <summary>申请有独立时钟的单次播放，返回可查询真实开始和终态的回执。</summary>
        public CharacterActionPlayback PlayOnce(in CharacterOneShotRequest request, double? at = null) =>
            IsBound && !paused ? pipeline.PlayOnce(request, at, AnimationTime) : null;

        /// <summary>在指定时点开始淡出这一播放实例。</summary>
        public bool FadeOut(CharacterActionPlayback playback, double at, float seconds) =>
            IsBound && pipeline.EndPlayback(playback, at, seconds, AnimationTime);

        /// <summary>在指定时点取消这一播放实例。</summary>
        public bool Cancel(CharacterActionPlayback playback, double? at = null) =>
            IsBound && pipeline.EndPlayback(playback, at, null, AnimationTime);

        /// <summary>为持续动作分配实例号并提交，返回的请求可更新权重或用于撤回。</summary>
        public CharacterAnimationRequest Hold(string key, float weight = 1f) =>
            IsBound && !paused ? pipeline.Hold(key, weight, AnimationTime) : default;

        /// <summary>提交或更新给定实例号的持续动作。</summary>
        public bool SubmitAction(CharacterAnimationRequest request) =>
            IsBound && !paused && pipeline.Submit(request, AnimationTime);

        /// <summary>查询本宿主是否仍持有指定持续动作实例。</summary>
        public bool HasAction(string key, long instanceId) => IsBound && pipeline.HasAction(key, instanceId);

        /// <summary>按键和实例号撤回持续需求，旧实例的迟到撤销不会影响新实例。</summary>
        public bool WithdrawAction(string semanticKey, long instanceId) =>
            IsBound && pipeline.Withdraw(semanticKey, instanceId, AnimationTime);

        /// <summary>启用指定部位的目标来源。实例号在同部位同来源内单调递增；场景目标须独立于角色。
        /// 可选动作键将来源限制到同编号的获选动作。不同场景目标提供者可使用各自固定的 providerKey 并独立编号。
        /// 优先级越大越优先，同级保持当前目标。</summary>
        public bool SubmitIkTarget(CharacterIkLimb limb, CharacterIkSource source, long instanceId,
            Transform target = null, int priority = 10, string actionKey = null, string providerKey = null)
        {
            return pipeline != null && pipeline.Pose.Ik.Submit(limb, source, instanceId, target, priority, actionKey, providerKey);
        }

        /// <summary>只撤销相同来源的当前实例；随后恢复该来源的 Inspector 默认设置。</summary>
        public bool WithdrawIkTarget(CharacterIkLimb limb, CharacterIkSource source, long instanceId, string providerKey = null) =>
            pipeline != null && pipeline.Pose.Ik.Withdraw(limb, source, instanceId, providerKey);

        /// <summary>明确瞬移或参考系突变时清除接触、惯性和差分历史，保留已提交的目标请求。</summary>
        public void ResetIkContacts() => pipeline?.ResetPoseHistory();

        /// <summary>清空动作、运动与时间历史。使用外部编号的来源须通过 SetClock 发布新的连续性代次。</summary>
        public void ResetAnimationState()
        {
            stateVersion++;
            waitingForMotionEpoch = suppliedClock;
            if (!suppliedClock)
            {
                simulationVersion++;
                localOrigin = paused ? pauseStart : Time.timeAsDouble;
            }
            ClearInputs(!suppliedClock);
            pipeline?.Reset(stateVersion, simulationVersion);
        }

        /// <summary>发生瞬移或更换控制器时断开运动连续性并中断动作。只有继续使用同一时间域时才可显式保留动作。</summary>
        public void NotifyMotionBoundary(bool cancelActions = true)
        {
            ClearInputs(!suppliedClock);
            waitingForMotionEpoch = suppliedClock;
            if (!suppliedClock) simulationVersion++;
            pipeline?.MotionBoundary(CharacterPlaybackEndSource.MotionBoundary, cancelActions, simulationVersion);
        }

#if UNITY_EDITOR
        // 接回规划工作后，从组件菜单读取本角色各阶段最近一次已求值的分支。
        [ContextMenu("诊断/打印动画判定程序")]
        private void PrintDecisionTrace()
        {
            if (pipeline == null) { Debug.Log("当前角色尚未安装动画判定程序。", this); return; }
            pipeline.Join();
            Debug.Log(pipeline.ReadDecisionTrace(), this);
        }

        internal CharacterAnimationConfiguration ActiveConfiguration => pipeline != null ? boundConfig : null;

        // 读取当前运行配置或编辑器所选资产的瞬移重置阈值。
        internal float PreviewTeleportDistance()
        {
            if (ActiveConfiguration != null) return ActiveConfiguration.IkTeleportDistance;
            var source = animationConfig;
            return source ? source.IkTeleportDistance : CharacterAnimationConfig.DefaultTeleportDistance;
        }

        internal Animator PreviewAnimator => animator ? animator : GetComponent<Animator>();

        // 读取动画流水线已发布的当前播放状态。
        internal CharacterAnimationDebugSnapshot ReadRuntimeDebug()
        {
            if (!isActiveAndEnabled) return new CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus.Inactive);
            if (pipeline != null) return pipeline.ReadDebug(stateVersion, simulationVersion, !paused);
            return rejection != null
                ? new CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus.ResourceUnavailable, rejection.Message)
                : new CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus.Inactive);
        }
#endif

        // 停用时结束回执、撤销在途发布凭据，仍在同一外部代次时保留输入去重基准。
        private void OnDisable()
        {
            StopPipeline(CharacterPlaybackEndSource.ComponentDisabled);
            if (!suppliedClock) simulationVersion++;
            ClearInputs(!suppliedClock);
            stateVersion++;
        }

        // 销毁当前流水线并结束关联的动画回执。
        private void StopPipeline(CharacterPlaybackEndSource source)
        {
            if (pipeline == null) return;
            pipeline.Release(source);
            pipeline = null;
            boundConfig = null;
        }
    }
}
