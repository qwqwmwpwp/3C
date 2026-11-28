using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>将真实 3C 角色的已解算运动、行为许可、角色时钟和预测发布给人形动画宿主。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterBehaviorDriver), typeof(CharacterBehaviorAnimator))]
    [DefaultExecutionOrder(50)]
    [AddComponentMenu("3C/角色动画适配器")]
    public sealed class CharacterAnimationAdapter : MonoBehaviour
    {
        private CharacterBehaviorDriver driver;
        private CharacterBehaviorAnimator host;
        private CharacterBehaviorAnimationBindings bindings;
        private CharacterBehaviorAnimationProfile actions;
        private readonly ThreeCAnimationMotionSource motion = new ThreeCAnimationMotionSource();
        private int boundVersion = -1, stateVersion, simulationVersion;
        private long motionFrame, contextFrame;
        private CharacterBehaviorObservation reconnect;
        private bool externalOwnsAnimation;
#if UNITY_EDITOR
        internal CharacterActionTrace ActionTrace { get; } = new CharacterActionTrace();
#endif

        // 注册真实固定步和硬边界回调，保留挂接前已经批准的持续状态。
        private void OnEnable()
        {
            driver = GetComponent<CharacterBehaviorDriver>();
            host = GetComponent<CharacterBehaviorAnimator>();
            actions = driver.Profile ? driver.Profile.AnimationActions : null;
            stateVersion = driver.StateVersion;
            simulationVersion = driver.SimulationVersion;
            reconnect = driver.LatestBehavior;
            boundVersion = -1;
            motionFrame = contextFrame = 0;
            externalOwnsAnimation = driver.IsExternallyControlled;
            if (!externalOwnsAnimation)
            {
                host.SetMovementCalibration(driver.ReadMovement().WalkSpeed, driver.Motor.slopeLimit);
                host.SetClock(driver.CharacterTime, simulationVersion, driver.IsSimulating);
            }
            driver.FrameCommitted += ReceiveMotion;
            driver.MotionBoundaryCommitted += ReceiveBoundary;
            driver.StateReset += ReceiveReset;
            host.PreparingPresentation += PublishInputs;
            host.PlaybackSettled += ContinueGrantedActions;
        }

        // 在宿主先建立新图或重新启用时，只恢复已存在的许可，瞬时边沿不重发。
        private void BindHost()
        {
            if (boundVersion == host.BindingVersion) return;
            if (bindings != null) reconnect = driver.LatestBehavior;
            bindings = new CharacterBehaviorAnimationBindings(host, actions) { StateVersion = driver.StateVersion };
#if UNITY_EDITOR
            bindings.Trace = ActionTrace;
#endif
            boundVersion = host.BindingVersion;
            motionFrame = contextFrame = 0;
            if (reconnect.FrameNumber != 0 && reconnect.SimulationVersion == driver.SimulationVersion)
                PublishObservation(reconnect, true);
            reconnect = default;
        }

        // 同步模拟时钟。即使公开回调发生重入，也按驱动器的实际代次清除旧输入。
        private void SynchronizeClock()
        {
            if (stateVersion != driver.StateVersion) ReceiveReset();
            if (simulationVersion != driver.SimulationVersion)
            {
                simulationVersion = driver.SimulationVersion;
                motionFrame = contextFrame = 0;
            }
            host.SetClock(driver.CharacterTime, simulationVersion, driver.IsSimulating);
        }

        // 只将当前模拟代次内尚未发布的已求解步转成动画测量值。
        private void ReceiveMotion(CharacterControllerFrameResult frame)
        {
            if (!host.IsBound || externalOwnsAnimation) return;
            SynchronizeClock();
            if (!driver.IsSimulating || frame.SimulationVersion != simulationVersion || frame.FrameNumber <= motionFrame) return;
            BindHost();
            var step = motion.FromFrame(frame, actions ? actions.MotionOverrideKey : null,
                bindings.PreservesEntryGait(frame));
            if (host.SubmitMotion(step, driver.LastSolvedTime, publishContext: false)) motionFrame = frame.FrameNumber;
        }

        // 按同帧真实裁决顺序分别发布持续状态与动作边沿，最后核对已冻结的预测。
        private void PublishInputs()
        {
            if (externalOwnsAnimation) return;
            SynchronizeClock();
            if (!host.IsBound || !driver.IsSimulating) return;
            BindHost();
            ReceiveMotion(driver.LatestFrame);
            var updates = driver.BehaviorUpdates;
            for (int i = 0; i < updates.Count; i++) PublishObservation(updates[i], false);
            PublishObservation(driver.LatestBehavior, true);
            bindings.Tick(driver.CommittedGuardApproval);
#if UNITY_EDITOR
            if (ActionTrace.Enabled) ActionTrace.ReadPending();
#endif
            int state = stateVersion, simulation = simulationVersion;
            long inputVersion = host.InputVersion;
            var published = driver.ReadAnimationForecast();
            // 预测失效可同步触发外控、重生或销毁；此时已由边界回调撤回旧预测。
            if (!this || !isActiveAndEnabled || !host || !host.IsBound || !driver ||
                state != driver.StateVersion || simulation != driver.SimulationVersion || !driver.IsSimulating) return;
            host.PublishTrajectory(motion.ReadForecast(published), inputVersion);
        }

        // 进入片段在当前帧结束时，依真实回执把剩余帧时间交给获准的保持姿势。
        private void ContinueGrantedActions()
        {
            if (driver && driver.IsSimulating) bindings?.Tick(driver.CommittedGuardApproval);
        }

        // 仅转交一次已批准的表现状态；补挂时已有的许可按中途接入处理。
        private void PublishObservation(in CharacterBehaviorObservation observation, bool seed)
        {
            if (observation.SimulationVersion != simulationVersion || observation.FrameNumber <= contextFrame) return;
            var context = ThreeCAnimationMotionSource.FromBehavior(observation.Behavior, actions ? actions.MotionOverrideKey : null);
            if (!host.PublishContext(context, host.InputVersion, observation.Time)) return;
            contextFrame = observation.FrameNumber;
            bindings.Observe(observation, seed);
#if UNITY_EDITOR
            if (ActionTrace.Enabled) ActionTrace.Record(stateVersion, observation);
#endif
        }

        // 接管与交还都中断上个所有者的动作，切断旧时间域后再允许新所有者写入。
        private void ReceiveBoundary(CharacterMotionBoundaryKind kind, int previousSimulation)
        {
#if UNITY_EDITOR
            if (ActionTrace.Enabled) ActionTrace.Record(stateVersion, driver.LatestBehavior, previousSimulation);
#endif
            bool changesOwner = kind == CharacterMotionBoundaryKind.Takeover || kind == CharacterMotionBoundaryKind.Recovery;
            if (changesOwner) host.UseUnityClock();
            host.NotifyMotionBoundary();
            bindings?.Clear(driver.CharacterTime);
            reconnect = default;
            motionFrame = contextFrame = 0;
            externalOwnsAnimation = driver.IsExternallyControlled;
            if (externalOwnsAnimation) return;
            SynchronizeClock();
        }

        // 角色重生时结束旧时间轴的动作并清空两侧的去重序号。
        private void ReceiveReset()
        {
#if UNITY_EDITOR
            if (ActionTrace.Enabled)
                ActionTrace.Record(driver.LastBehaviorResetVersion, new CharacterBehaviorObservation(driver.LastBehaviorReset,
                    0f, driver.LastBehaviorResetSimulationVersion));
#endif
            stateVersion = driver.StateVersion;
            simulationVersion = driver.SimulationVersion;
            if (externalOwnsAnimation) host.UseUnityClock();
            externalOwnsAnimation = false;
            host.ResetAnimationState();
            bindings?.Clear(driver.CharacterTime);
            if (bindings != null) bindings.StateVersion = stateVersion;
            reconnect = default;
            motionFrame = contextFrame = 0;
            host.SetClock(driver.CharacterTime, simulationVersion, driver.IsSimulating);
        }

        // 注销所有输入来源并归还宿主时钟，单独停用适配器也能立即切断旧角色的播放。
        private void OnDisable()
        {
            if (driver)
            {
                driver.FrameCommitted -= ReceiveMotion;
                driver.MotionBoundaryCommitted -= ReceiveBoundary;
                driver.StateReset -= ReceiveReset;
            }
            if (host)
            {
                host.PreparingPresentation -= PublishInputs;
                host.PlaybackSettled -= ContinueGrantedActions;
                if (!externalOwnsAnimation)
                {
                    host.NotifyMotionBoundary();
                    bindings?.Clear(driver ? driver.CharacterTime : 0d);
                    host.UseUnityClock();
                }
            }
            bindings = null;
        }
    }
}
