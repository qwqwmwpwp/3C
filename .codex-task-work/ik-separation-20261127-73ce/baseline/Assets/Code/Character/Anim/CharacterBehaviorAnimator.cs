using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>以本帧行为许可和最近求解的运动组织动画，在引擎求值前提交播放。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    [AddComponentMenu("3C/角色行为动画")]
    public sealed class CharacterBehaviorAnimator : MonoBehaviour
    {
        [SerializeField, Tooltip("播放角色动画的动画器。留空时使用当前对象上的动画器。")]
        private Animator animator;
        [SerializeField, Min(0f), InspectorName("斜坡模型最大下沉（米）"), Tooltip("仅作用于独立的模型子节点。零表示关闭。")]
        internal float modelDropLimit;
        [SerializeField, InspectorName("模型绑定")] private CharacterModelBindings modelBindings = new CharacterModelBindings();
        [SerializeField, InspectorName("动画表现配置"), Tooltip("可直接指定要装配的配置；留空时继承同一对象上行为驱动器的角色配置。")]
        private CharacterAnimationConfig animationConfig;
        private CharacterBehaviorDriver driver;
        private readonly ExternalAnimationMotion external = new ExternalAnimationMotion();
        private readonly CharacterProfileOverrides externalOverrides = new CharacterProfileOverrides();
        // 保存已验证的模块选择，候选选择只有应用成功才取代已安装选择。
        private sealed class ModuleSelection
        {
            internal readonly List<AnimationModule> Roots;
            internal readonly HashSet<AnimationModule> Excluded;
            internal readonly AnimationModuleAssembly Assembly;

            // 展开并校验一组根模块及排除项。
            internal ModuleSelection(List<AnimationModule> roots, HashSet<AnimationModule> excluded)
            {
                Roots = roots;
                Excluded = excluded;
                Assembly = AnimationModuleAssembly.Resolve(roots, excluded);
            }
        }

        private ModuleSelection installedSelection, pendingSelection;
        private bool refreshModuleContents;
        private CharacterAnimationConfig configurationSource;
        private string configurationError;
        private CharacterAnimationConfiguration boundConfig;
        private CharacterControllerFrameResult LatestFrame => driver ? driver.LatestFrame : external.Latest;
        private CharacterBehaviorObservation LatestBehavior => driver ? driver.LatestBehavior :
            new CharacterBehaviorObservation(external.Latest.Behavior, external.Latest.DeltaTime, external.Version);
        private int StateVersion => driver ? driver.StateVersion : external.StateVersion;
        private int SimulationVersion => driver ? driver.SimulationVersion : external.Version;
        private bool IsSimulating => driver ? driver.IsSimulating : !external.Paused;
        private CharacterAnimationPipeline pipeline;
#if UNITY_EDITOR
        internal CharacterActionTrace ActionTrace { get; } = new CharacterActionTrace();
#endif

        /// <summary>最后一个有效固定步的运动信息；暂停、停用或等待生命周期恢复后的新步时无效。</summary>
        public CharacterAnimationMotion Motion => isActiveAndEnabled && pipeline != null
            ? pipeline.ReadMotion(LatestFrame, StateVersion, SimulationVersion, IsSimulating)
            : default;

        // 缓存同对象上的行为来源与动画器。
        private void Awake()
        {
            driver = GetComponent<CharacterBehaviorDriver>();
            if (animator == null)
                animator = GetComponent<Animator>();
        }

        // 读取显式指定的动画资产，留空时从角色配置继承。
        private CharacterAnimationConfig ResolveConfiguration(CharacterBehaviorDriver owner)
        {
            if (animationConfig) return animationConfig;
            var profile = owner ? owner.Profile : null;
            return profile ? profile.AnimationConfig : null;
        }

        // 为当前角色或编辑器预演创建本次装配的动画配置。
        internal CharacterAnimationConfiguration Configuration(CharacterBehaviorDriver owner) =>
            BindConfiguration(owner, ResolveConfiguration(owner), (pendingSelection ?? installedSelection)?.Assembly);

        // 将动画资产与当前角色的骨骼、步态阈值及行为数据绑定。
        private CharacterAnimationConfiguration BindConfiguration(CharacterBehaviorDriver owner, CharacterAnimationConfig source,
            AnimationModuleAssembly assembly)
        {
            var profile = owner ? owner.Profile : null;
            if (!source)
                throw new System.ArgumentException(profile
                    ? $"角色配置 {profile.name} 缺少动画表现配置。请在角色配置或角色行为动画组件中指定。"
                    : "角色行为动画缺少动画表现配置。请在组件中指定动画表现配置，或配置同对象上的行为驱动器。");
            var overrides = owner ? owner.Overrides : externalOverrides;
            var config = source.Bind(modelBindings, overrides, profile ? profile.AttackTable : null, assembly);
            if (owner) config.BindMovement(owner.ReadMovement(), owner.Motor.slopeLimit);
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
                var owner = driver ? driver : GetComponent<CharacterBehaviorDriver>();
                var source = ResolveConfiguration(owner);
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
            var source = ResolveConfiguration(driver);
            if (pipeline != null && source == configurationSource && source && source.PlaybackLibrary != boundConfig.PlaybackLibrary)
            {
                Debug.LogWarning(AnimationModuleChanges.PlaybackLibraryChanged, this);
                return false;
            }
            try { pendingSelection = new ModuleSelection(roots, excluded); }
            catch (System.ArgumentException error) { Debug.LogWarning(error.Message, this); return false; }
            refreshModuleContents |= refreshContents;
            return true;
        }

        /// <summary>无内置行为驱动器时，在运动求解后按固定步提交世界位移。动作通过本组件的动作键接口提交。</summary>
        public bool SubmitMotion(in CharacterAnimationMovement movement)
        {
            if (driver || !isActiveAndEnabled || pipeline == null || !external.Commit(movement)) return false;
            pipeline.Receive(external.Latest, StateVersion, SimulationVersion, IsSimulating, Time.timeAsDouble, external.LatestTrajectory);
            pipeline.ReceiveBehavior(LatestBehavior, StateVersion, SimulationVersion);
            return true;
        }

        public void SetExternalPaused(bool paused)
        {
            if (!driver) external.SetPaused(paused);
        }

        // 启用时立即绑定，使第一步运动可进入动画流水线。
        private void OnEnable()
        {
            if (driver)
            {
                driver.MotionBoundaryCommitted += ReceiveMotionBoundary;
                driver.StateReset += ReceiveStateReset;
            }
            ApplyConfiguration(ResolveConfiguration(driver), replace: true);
        }

        // 绑定成功后启动或重装配流水线，配置错误时等待有效配置或模块选择。
        private void ApplyConfiguration(CharacterAnimationConfig source, bool replace)
        {
            configurationSource = source;
            bool selectionRequested = pendingSelection != null;
            var selection = pendingSelection ?? installedSelection;
            bool refresh = refreshModuleContents;
            pendingSelection = null;
            refreshModuleContents = false;
            CharacterAnimationConfiguration next;
            AnimationModuleChanges changes = null;
            try
            {
                if ((replace || selectionRequested) && selection != null)
                    selection = new ModuleSelection(selection.Roots, selection.Excluded);
                next = BindConfiguration(driver, source, selection?.Assembly);
                if (!replace && pipeline != null) changes = pipeline.PrepareModules(next, refresh);
            }
            catch (System.ArgumentException error)
            {
                if (replace) StopPipeline(CharacterPlaybackEndSource.ResourceUnavailable);
                if (configurationError != error.Message) Debug.LogError(error.Message, this);
                configurationError = error.Message;
                return;
            }
            configurationError = null;
            if (changes != null)
            {
                pipeline.Reconfigure(next, changes, LatestBehavior.SimulationVersion == SimulationVersion ? LatestBehavior : default);
                installedSelection = selection;
                boundConfig = next;
                return;
            }
            StopPipeline(CharacterPlaybackEndSource.Rebind);
            pipeline = new CharacterAnimationPipeline(animator, next, this, LatestFrame, LatestBehavior, StateVersion, SimulationVersion,
                driver ? default : external.LatestTrajectory);
            installedSelection = selection;
            boundConfig = next;
            if (!driver) return;
            driver.FrameCommitted += ReceiveFrame;
        }

        // 从当前配置读取本实例采用的动作匹配模式。
        private CharacterMatchMode MatchMode => boundConfig != null && boundConfig.Modules.Match
            ? boundConfig.MatchMode : CharacterMatchMode.Rules;

        // 采集本帧动画输入，排队在引擎求值前统一提交。
        private void Update()
        {
            using var capture = CharacterPresentationBatch.Capture.Auto();
            var source = ResolveConfiguration(driver);
            bool sourceChanged = !ReferenceEquals(configurationSource, source);
            if (sourceChanged || pendingSelection != null) ApplyConfiguration(source, replace: sourceChanged);
#if UNITY_EDITOR
            if (driver && ActionTrace.Enabled)
            {
                var observed = driver.BehaviorUpdates;
                for (int i = 0; i < observed.Count; i++) ActionTrace.Record(StateVersion, observed[i]);
                ActionTrace.ReadPending();
            }
#endif
            var current = pipeline;
            if (current == null) return;
            current.Join();
            if (driver && IsSimulating)
            {
                var updates = driver.BehaviorUpdates;
                for (int i = 0; i < updates.Count; i++) current.ReceiveBehavior(updates[i], StateVersion, SimulationVersion);
            }
            current.Matcher.Mode = MatchMode;
            int state = StateVersion, simulation = SimulationVersion;
            if (!TryReadForecast(current, state, simulation, out var forecast)) return;
            if (current.Capture(LatestFrame, LatestBehavior, state, simulation, IsSimulating, Time.deltaTime,
                driver ? driver.CharacterTime : external.Time, driver ? driver.CommittedGuardApproval : 0, forecast))
            {
                current.Schedule(driver && driver.UseWorkerThreads);
                CharacterPresentationBatch.Queue(this);
            }
        }

        // 预测失效通知可能同步重置、停用或销毁角色；仅将读取结果交给原流水线和原代次。
        private bool TryReadForecast(CharacterAnimationPipeline current, int state, int simulation, out CharacterMovementForecast forecast)
        {
            bool driven = driver;
            forecast = driven ? driver.ReadAnimationForecast() : null;
            return this && isActiveAndEnabled && pipeline == current && driven == (bool)driver &&
                state == StateVersion && simulation == SimulationVersion;
        }

        // 在预测通知前后分别验证批次，安全处置尚属本组件的失效批次。
        internal void FinishPresentation()
        {
            var current = pipeline;
            if (current == null) return;
            current.Join();
            int state = StateVersion, simulation = SimulationVersion;
            if (!current.CanApply(state, simulation, IsSimulating)) { current.Discard(); return; }
            bool valid = TryReadForecast(current, state, simulation, out var forecast);
            if (!this || !isActiveAndEnabled || pipeline != current) return;
            if (!current.CanApply(StateVersion, SimulationVersion, IsSimulating) || !valid) { current.Discard(); return; }
            current.RefreshForecast(forecast);
            current.Apply();
        }

        /// <summary>每次调用申请一个新的单次动作。返回记录可查询是否开始及最终结果；
        /// 可用该记录撤销。资源不可用时返回 null。</summary>
        public CharacterActionPlayback PlayActionOnce(string semanticKey, float weight = 1f)
        {
            return isActiveAndEnabled && pipeline != null
                ? pipeline.PlayOnce(semanticKey, weight, LatestFrame, StateVersion, SimulationVersion, driver ? driver.CharacterTime : external.Time)
                : null;
        }

        /// <summary>仅撤销本角色上这一次播放，即使重置后复用了实例号也不会误撤销。</summary>
        public bool WithdrawAction(CharacterActionPlayback playback) => pipeline != null && pipeline.Withdraw(playback, driver ? driver.CharacterTime : external.Time);

        /// <summary>提交 Inspector 中已映射的动作。同键实例号从正数递增，重复提交保持进度，更大编号从配置起点重播。
        /// 需求持续到撤销、停用或角色重置，获选后受压制时按映射的中断策略处理。</summary>
        /// <returns>已接收或实例仍有效时返回 true。资源无效、旧实例或已取消实例返回 false。</returns>
        public bool SubmitAction(string semanticKey, long instanceId)
        {
            return SubmitAction(new CharacterAnimationRequest(semanticKey, instanceId));
        }

        /// <summary>提交已通过绑定预检的需求。局部动作和全身动作覆盖均支持 0～1 权重，同实例更新权重时保持进度。
        /// 基础步态的权重固定为 1。新实例或重新获选时应用播放起点，非法浮点参数按 0 处理。</summary>
        public bool SubmitAction(CharacterAnimationRequest request)
        {
            return isActiveAndEnabled && pipeline != null &&
                pipeline.Submit(request, LatestFrame, StateVersion, SimulationVersion, driver ? driver.CharacterTime : external.Time);
        }

        /// <summary>撤销指定键的当前实例；旧实例的迟到撤销不会误删新实例。撤销后下次 Update 恢复组合。</summary>
        public bool WithdrawAction(string semanticKey, long instanceId)
        {
            return pipeline != null && pipeline.Withdraw(semanticKey, instanceId, driver ? driver.CharacterTime : external.Time);
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
            if (!driver) external.Reset();
            if (pipeline != null)
                pipeline.Reset(LatestFrame, StateVersion, SimulationVersion);
        }

        // 重生时将被取消的执行和缓存归入旧状态代次，再结束其表现需求。
        private void ReceiveStateReset()
        {
#if UNITY_EDITOR
            if (ActionTrace.Enabled)
            {
                var old = new CharacterBehaviorObservation(driver.LastBehaviorReset, 0f, driver.LastBehaviorResetSimulationVersion);
                ActionTrace.Record(driver.LastBehaviorResetVersion, old);
            }
#endif
            ResetAnimationState();
        }

        // 在刚体边界提交后、预测及公开回调前，中止旧回执并记录旧执行的结束裁决。
        private void ReceiveMotionBoundary(CharacterMotionBoundaryKind kind, int previousSimulation)
        {
#if UNITY_EDITOR
            ActionTrace.Record(StateVersion, LatestBehavior, previousSimulation);
#endif
            pipeline?.MotionBoundary(kind, SimulationVersion);
        }

        // 按固定步提交顺序转交已结算结果，供步态历史消费。
        private void ReceiveFrame(CharacterControllerFrameResult frame) =>
            pipeline.Receive(frame, StateVersion, SimulationVersion, IsSimulating, driver ? driver.LastSolvedTime : Time.timeAsDouble);

#if UNITY_EDITOR
        internal CharacterAnimationConfiguration ActiveConfiguration => pipeline != null ? boundConfig : null;

        // 读取当前运行配置或编辑器所选资产的瞬移重置阈值。
        internal float PreviewTeleportDistance(CharacterBehaviorDriver owner)
        {
            if (ActiveConfiguration != null) return ActiveConfiguration.IkTeleportDistance;
            var source = ResolveConfiguration(owner);
            return source ? source.IkTeleportDistance : CharacterAnimationConfig.DefaultTeleportDistance;
        }

        internal Animator PreviewAnimator => animator ? animator : GetComponent<Animator>();

        // 读取动画流水线已发布的当前播放状态。
        internal CharacterAnimationDebugSnapshot ReadRuntimeDebug()
        {
            if (!isActiveAndEnabled) return new CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus.Inactive);
            if (pipeline != null) return pipeline.ReadDebug(StateVersion, SimulationVersion, IsSimulating);
            return configurationError != null
                ? new CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus.ResourceUnavailable, configurationError)
                : new CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus.Inactive);
        }
#endif

        // 停用时释放动画播放资源并清空外部运动输入历史。
        private void OnDisable()
        {
            if (driver)
            {
                driver.MotionBoundaryCommitted -= ReceiveMotionBoundary;
                driver.StateReset -= ReceiveStateReset;
            }
            StopPipeline(CharacterPlaybackEndSource.ComponentDisabled);
            external.Reset();
        }

        // 解除固定步订阅并销毁当前流水线。
        private void StopPipeline(CharacterPlaybackEndSource source)
        {
            if (pipeline == null) return;
            if (driver) driver.FrameCommitted -= ReceiveFrame;
            pipeline.Release(source);
            pipeline = null;
            boundConfig = null;
        }
    }
}
