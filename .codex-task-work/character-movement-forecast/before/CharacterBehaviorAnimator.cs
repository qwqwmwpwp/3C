using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>配置动画流水线并同步接收固定步结果，在 Update 中、Animator 求值前提交当前有效的动画组合。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterBehaviorDriver))]
    [DefaultExecutionOrder(100)]
    [AddComponentMenu("3C/角色行为动画")]
    public sealed class CharacterBehaviorAnimator : CharacterLegacyAnimationSettings
    {
        [SerializeField, Tooltip("播放角色动画的动画器。留空时使用当前对象上的动画器，兼容原有预制体。")]
        private Animator animator;
        [SerializeField, InspectorName("模型绑定")] private CharacterModelBindings modelBindings = new CharacterModelBindings();
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
            var configuration = driver.Profile
                ? driver.Profile.BindAnimation(modelBindings, driver.Overrides)
                : LegacyConfiguration();
            configuration.WalkableSlope = driver.Motor.slopeLimit;
            pipeline = new CharacterAnimationPipeline(animator, configuration, this, driver.LatestFrame,
                driver.StateVersion, driver.SimulationVersion);
            driver.FrameCommitted += ReceiveFrame;
            driver.StateReset += ResetAnimationState;
        }

        private CharacterMatchMode MatchMode
        {
            get
            {
#if UNITY_EDITOR
                if (DebugMatchMode.HasValue) return DebugMatchMode.Value;
#endif
                return driver.Profile ? driver.Profile.Gaits.motionMatching : motionMatching;
            }
        }

        private void Update()
        {
            pipeline.Matcher.Mode = MatchMode;
#if UNITY_EDITOR
            var trace = Replay?.Active == true ? Replay.Trace : null;
            var player = pipeline.ReplayPlayer;
            if (player != null) player.ReplayTrace = trace;
            trace?.Begin(player?.Counters);
#endif
            pipeline.Update(driver.LatestFrame, driver.StateVersion, driver.SimulationVersion,
                driver.IsSimulating, Time.deltaTime, driver.CommittedGuardApproval);
#if UNITY_EDITOR
            trace?.End(pipeline.ReplayPlayer?.Counters);
#endif
        }

        internal bool HasBehaviorMapping(CharacterBehaviorId id) => pipeline != null && pipeline.HasBehaviorMapping(id);

        /// <summary>每次调用申请一个新的单次动作。返回记录可查询是否开始及最终结果；
        /// 可用该记录撤销。资源不可用时返回 null。</summary>
        public CharacterActionPlayback PlayActionOnce(string semanticKey, float weight = 1f)
        {
            return isActiveAndEnabled && pipeline != null && driver != null
                ? pipeline.PlayOnce(semanticKey, weight, driver.LatestFrame, driver.StateVersion, driver.SimulationVersion)
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
        internal CharacterMatchMode? DebugMatchMode { get; set; }
        internal CharacterMatchMode CurrentMatchMode => MatchMode;
        private CharacterReplayEventProbe replayProbe;
        private CharacterMatchReplay previousRun;
        internal string PreviousReplay { get; private set; }
        internal string ReplayComparison => Replay?.Compare(previousRun);
        internal CharacterMatchReplay Replay => driver != null ? driver.MatchReplay : null;
        internal void BeginReplay(GroundReplayCase scenario, bool shiftPhase, bool reuseMixers = false)
        {
            if (driver == null || !isActiveAndEnabled || Replay != null && Replay.Active) return;
            previousRun = Replay;
            PreviousReplay = previousRun?.Describe();
            var startClip = driver.Profile ? driver.Profile.Presentation.runStartClip : runStartClip;
            driver.MatchReplay = new CharacterMatchReplay(transform.forward, scenario, MatchMode,
                shiftPhase && startClip ? startClip.length * 0.5f : 0f, reuseMixers);
            if (Replay.Trace != null && animator)
            {
                if (!replayProbe)
                {
                    replayProbe = animator.gameObject.AddComponent<CharacterReplayEventProbe>();
                    replayProbe.hideFlags = HideFlags.HideAndDontSave;
                }
                replayProbe.Ledger = Replay.Trace.Events;
            }
        }

        private void LateUpdate()
        {
            if (Replay != null && Replay.Mode != MatchMode) Replay.Cancel();
            if (pipeline != null) Replay?.Observe(pipeline);
            if (Replay?.Active != true) ReleaseProbe();
        }

        private void ReleaseProbe()
        {
            if (!replayProbe) return;
            replayProbe.Ledger = null;
            Destroy(replayProbe);
            replayProbe = null;
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
            ReleaseProbe();
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
