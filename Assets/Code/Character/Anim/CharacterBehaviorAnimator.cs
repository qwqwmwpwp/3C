using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>配置动画流水线并同步接收固定步结果，在 Update 中、Animator 求值前提交当前有效的动画组合。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    [AddComponentMenu("3C/角色行为动画")]
    public sealed class CharacterBehaviorAnimator : CharacterLegacyAnimationSettings
    {
        [SerializeField, Tooltip("播放角色动画的动画器。留空时使用当前对象上的动画器，兼容原有预制体。")]
        private Animator animator;
        [SerializeField, Min(0f), InspectorName("斜坡模型最大下沉（米）"), Tooltip("仅作用于独立的模型子节点。零表示关闭。")]
        internal float modelDropLimit;
        [SerializeField, InspectorName("模型绑定")] private CharacterModelBindings modelBindings = new CharacterModelBindings();
        [SerializeField, InspectorName("动画表现配置"), Tooltip("可直接指定要装配的配置；留空时继承同一对象上行为驱动器的角色配置。")]
        private CharacterAnimationConfig animationConfig;
        private CharacterBehaviorDriver driver;
        private readonly ExternalAnimationMotion external = new ExternalAnimationMotion();
        private readonly CharacterProfileOverrides externalOverrides = new CharacterProfileOverrides();
        private AnimationModuleAssembly requestedModules;
        private List<AnimationModule> moduleRoots;
        private HashSet<AnimationModule> excludedModules;
        private bool changeModules;
        private CharacterAnimationConfiguration boundConfig;
        private CharacterControllerFrameResult LatestFrame => driver ? driver.LatestFrame : external.Latest;
        private int StateVersion => driver ? driver.StateVersion : external.StateVersion;
        private int SimulationVersion => driver ? driver.SimulationVersion : external.Version;
        private bool IsSimulating => driver ? driver.IsSimulating : !external.Paused;
        private CharacterProfile Profile => driver ? driver.Profile : null;
        private AnimationModule DefaultModules
        {
            get
            {
                if (animationConfig) return animationConfig.Modules;
                var owner = driver ? driver : GetComponent<CharacterBehaviorDriver>();
                return owner && owner.Profile ? owner.Profile.AnimationModules : null;
            }
        }
        private CharacterAnimationPipeline pipeline;

        /// <summary>最后一个有效固定步的运动信息；暂停、停用或等待生命周期恢复后的新步时无效。</summary>
        public CharacterAnimationMotion Motion => isActiveAndEnabled && pipeline != null
            ? pipeline.ReadMotion(LatestFrame, StateVersion, SimulationVersion, IsSimulating)
            : default;

        private void Awake()
        {
            driver = GetComponent<CharacterBehaviorDriver>();
            if (animator == null)
                animator = GetComponent<Animator>();
        }

        internal CharacterAnimationConfiguration Configuration(CharacterBehaviorDriver owner)
        {
            var profile = owner ? owner.Profile : null;
            var overrides = owner ? owner.Overrides : externalOverrides;
            CharacterAnimationConfiguration config;
            if (animationConfig)
                config = animationConfig.Bind(modelBindings, overrides, profile ? profile.LightAttack : null, requestedModules);
            else if (profile)
                config = profile.BindAnimation(modelBindings, overrides, requestedModules);
            else
            {
                config = LegacyConfiguration(modelBindings, overrides, requestedModules);
            }
            if (owner && config.UseMovementWalkSpeed)
            {
                float band = Mathf.Max(0f, config.RunEnterSpeed - config.RunExitSpeed);
                config.RunEnterSpeed = InputMath.NonNegative(owner.ReadMovement()?.WalkSpeed ?? config.RunEnterSpeed, config.RunEnterSpeed);
                config.RunExitSpeed = Mathf.Max(config.IdleExitSpeed, config.RunEnterSpeed - band);
            }
            config.WalkableSlope = owner ? owner.Motor.slopeLimit : 45f;
            return config;
        }

        /// <summary>替换此角色的模块集合。可传单个模块、嵌套组合或空集合。校验失败时保留当前装配；
        /// 变更在下一次动画采集前生效，重复引用只装配一次。</summary>
        public bool SetAnimationModules(params AnimationModule[] roots)
        {
            if (roots == null) return false;
            return SelectModules(new List<AnimationModule>(roots), new HashSet<AnimationModule>());
        }

        /// <summary>启用或停用一个模块或子组合。停用一个组合时，其他分支仍引用的子模块会保留。</summary>
        public bool SetAnimationModuleEnabled(AnimationModule module, bool enabled)
        {
            if (!module) return false;
            var selectedRoot = DefaultModules;
            if (moduleRoots == null && !selectedRoot) return false; // 内嵌旧配置先通过 SetAnimationModules 选择模块集合。
            var roots = moduleRoots == null ? new List<AnimationModule> { selectedRoot } : new List<AnimationModule>(moduleRoots);
            var excluded = excludedModules == null ? new HashSet<AnimationModule>() : new HashSet<AnimationModule>(excludedModules);
            if (requestedModules != null && enabled && requestedModules.Contains(module)) return true;
            if (!enabled && excluded.Contains(module)) return true;
            if (enabled)
            {
                excluded.Remove(module);
                var selected = AnimationModuleAssembly.Includes(roots, excluded, module);
                if (!selected) roots.Add(module);
            }
            else excluded.Add(module);
            return SelectModules(roots, excluded);
        }

        private bool SelectModules(List<AnimationModule> roots, HashSet<AnimationModule> excluded)
        {
            try { requestedModules = AnimationModuleAssembly.Resolve(roots, excluded); }
            catch (System.ArgumentException error) { Debug.LogWarning(error.Message, this); return false; }
            moduleRoots = roots;
            excludedModules = excluded;
            changeModules = true;
            return true;
        }

        /// <summary>无内置行为驱动器时，在运动求解后按固定步提交世界位移。动作通过本组件的动作键接口提交。</summary>
        public bool SubmitMotion(in CharacterAnimationMovement movement)
        {
            if (driver || !isActiveAndEnabled || pipeline == null || !external.Commit(movement)) return false;
            ReceiveFrame(external.Latest);
            return true;
        }

        public void SetExternalPaused(bool paused)
        {
            if (!driver) external.SetPaused(paused);
        }

        private void OnEnable()
        {
            try { boundConfig = Configuration(driver); }
            catch (System.ArgumentException error) { Debug.LogError(error.Message, this); return; }
            StartPipeline();
        }

        private void StartPipeline()
        {
            pipeline = new CharacterAnimationPipeline(animator, boundConfig, this, LatestFrame, StateVersion, SimulationVersion);
            changeModules = false;
            if (!driver) return;
            driver.FrameCommitted += ReceiveFrame;
            driver.StateReset += ResetAnimationState;
        }

        private CharacterMatchMode MatchMode
        {
            get
            {
#if UNITY_EDITOR
                if (boundConfig?.Modules != null && !boundConfig.Modules.Match) return CharacterMatchMode.Rules;
                if (DebugMatchMode.HasValue) return DebugMatchMode.Value;
#endif
                if (boundConfig?.Modules != null) return boundConfig.MatchMode;
                return animationConfig ? animationConfig.Gaits.motionMatching : Profile ? Profile.Gaits.motionMatching : motionMatching;
            }
        }

        private void Update()
        {
            using var capture = CharacterPresentationBatch.Capture.Auto();
            if (changeModules)
            {
                var config = Configuration(driver);
                if (pipeline == null) { boundConfig = config; StartPipeline(); }
                else
                {
                    pipeline.Reconfigure(config, LatestFrame.SimulationVersion == SimulationVersion ? LatestFrame : default);
                    boundConfig = config;
                    changeModules = false;
                }
            }
            if (pipeline == null) return;
            pipeline.Join();
            pipeline.Matcher.Mode = MatchMode;
            var forecast = driver ? driver.AnimationForecast : null;
            if (pipeline.Capture(CharacterForecastQuery.ForAnimation(LatestFrame, forecast, Time.timeAsDouble),
                StateVersion, SimulationVersion, IsSimulating, Time.deltaTime, driver ? driver.CommittedGuardApproval : 0, forecast))
            {
                pipeline.Schedule(driver && driver.UseWorkerThreads);
                CharacterPresentationBatch.Queue(this);
            }
        }

        internal void FinishPresentation()
        {
            var current = pipeline;
            if (current == null) return;
            current.Join();
            if (!current.CanApply(StateVersion, SimulationVersion, IsSimulating)) { current.Discard(); return; }
            current.RefreshForecast(driver ? driver.PublishedAnimationForecast : null, LatestFrame);
            current.Apply();
        }

        /// <summary>每次调用申请一个新的单次动作。返回记录可查询是否开始及最终结果；
        /// 可用该记录撤销。资源不可用时返回 null。</summary>
        public CharacterActionPlayback PlayActionOnce(string semanticKey, float weight = 1f)
        {
            return isActiveAndEnabled && pipeline != null
                ? pipeline.PlayOnce(semanticKey, weight, LatestFrame, StateVersion, SimulationVersion)
                : null;
        }

        /// <summary>仅撤销本角色上这一次播放，即使重置后复用了实例号也不会误撤销。</summary>
        public bool WithdrawAction(CharacterActionPlayback playback) => pipeline != null && pipeline.Withdraw(playback);

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
            return isActiveAndEnabled && pipeline != null &&
                pipeline.Submit(request, LatestFrame, StateVersion, SimulationVersion);
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
            if (!driver) external.Reset();
            if (pipeline != null)
                pipeline.Reset(LatestFrame, StateVersion, SimulationVersion);
        }

        private void ReceiveFrame(CharacterControllerFrameResult frame)
        {
            var forecast = driver ? driver.AnimationForecast : null;
            pipeline.Receive(CharacterForecastQuery.ForAnimation(frame, forecast, driver ? driver.LastSolvedTime : Time.timeAsDouble),
                StateVersion, SimulationVersion, IsSimulating);
        }

#if UNITY_EDITOR
        internal CharacterAnimationConfiguration ActiveConfiguration => pipeline != null ? boundConfig : null;
        internal float PreviewTeleportDistance(CharacterBehaviorDriver owner)
        {
            if (ActiveConfiguration != null) return ActiveConfiguration.IkTeleportDistance;
            if (animationConfig) return animationConfig.Presentation.ikTeleportDistance;
            return owner && owner.Profile ? owner.Profile.Presentation.ikTeleportDistance : ikTeleportDistance;
        }

        internal Animator PreviewAnimator => animator ? animator : GetComponent<Animator>();
        internal AnimationClip ActivePreviewClip(string state) => pipeline?.ActivePreviewClip(Animator.StringToHash(state));
        internal CharacterMatchMode? DebugMatchMode { get; set; }
        internal CharacterMatchMode CurrentMatchMode => MatchMode;

        internal string ReadMatchDebug() { pipeline?.Join(); return pipeline?.Matcher.Describe() ?? ""; }
        internal string ReadIkBindingsDebug() => pipeline?.Ik.DescribeBindings();
        internal CharacterAnimationEvaluationHistory ReadPoseDebug() => pipeline?.Ik.ReadEvaluations();
        /// <summary>读取动画更新已发布的值和历史，不访问运动观察器或触发播放。</summary>
        internal CharacterAnimationDebugSnapshot ReadRuntimeDebug(out ICharacterAnimationDebugHistory history)
        {
            history = null;
            return isActiveAndEnabled && pipeline != null
                ? pipeline.ReadDebug(StateVersion, SimulationVersion, IsSimulating, out history)
                : new CharacterAnimationDebugSnapshot(CharacterAnimationDebugStatus.Inactive);
        }
#endif

        private void OnDisable()
        {
            if (driver != null)
            {
                driver.FrameCommitted -= ReceiveFrame;
                driver.StateReset -= ResetAnimationState;
            }
            external.Reset();
            pipeline?.Release();
            pipeline = null;
        }
    }
}
