using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>驱动器实例的输入、固定步结算、模拟连续性和配置绑定。</summary>
    [Serializable]
    internal sealed class CharacterBlackboard
    {
        // 普通私有字段可随脚本热重载恢复，无需加入场景或预制体的序列化数据。
        private CharacterInputBlackboard input = new CharacterInputBlackboard();
        private CharacterPhysicsStepBlackboard physicsStep = new CharacterPhysicsStepBlackboard();
        private CharacterSimulationBlackboard simulation = new CharacterSimulationBlackboard();
        private CharacterConfigurationBlackboard configuration = new CharacterConfigurationBlackboard();

        internal readonly CharacterRigidbodyBlackboard Rigidbody = new CharacterRigidbodyBlackboard();
        internal CharacterInputBlackboard Input => input;
        internal CharacterPhysicsStepBlackboard PhysicsStep => physicsStep;
        internal CharacterSimulationBlackboard Simulation => simulation;
        internal CharacterConfigurationBlackboard Configuration => configuration;
    }

    // 待结算的固定步一次写入，提交后整条清空。
    internal readonly struct CharacterPendingPhysicsStep
    {
        internal readonly CharacterBehaviorFrameResult Behavior;
        internal readonly CharacterGroundDrive GroundDrive;
        internal readonly CharacterInputSnapshot Input;
        internal readonly Vector3 Start;
        internal readonly float Duration;
        private readonly float turnSpeed;
        internal readonly double Time, UnityFixedTime;
        internal readonly long Number;
        internal bool IsScheduled => Duration > 0f;

        // 用物理实测与同一固定步的输入和行为结果建立提交帧。
        internal CharacterControllerFrameResult ResolveFrame(in CharacterMotorObservation actual, int version,
            in CharacterControllerFrameResult previous)
        {
            var displacement = actual.Position - Start;
            var facing = actual.Rotation * Vector3.forward;
            var history = previous.SimulationVersion == version ? previous.Ground : default;
            var observation = GroundDrive.Observe(actual.Velocity, displacement, Duration, actual.Contacts, history, facing, turnSpeed);
            return new CharacterControllerFrameResult(Behavior, Input, Duration, displacement, actual.Contacts, facing, version, observation, Number);
        }

        // 锁定步起点、输入和行为结果，等待本次物理求解完成。
        internal CharacterPendingPhysicsStep(CharacterBehaviorFrameResult behavior, CharacterGroundDrive groundDrive,
            CharacterInputSnapshot input, Vector3 start, float duration, double time, long number, double unityFixedTime, float turnSpeed)
        {
            Behavior = behavior;
            GroundDrive = groundDrive;
            Input = input;
            Start = start;
            Duration = duration;
            this.turnSpeed = turnSpeed;
            Time = time;
            UnityFixedTime = unityFixedTime;
            Number = number;
        }
    }

    // 保存待结算的物理步及最近已提交帧。
    [Serializable]
    internal sealed class CharacterPhysicsStepBlackboard
    {
        private CharacterPendingPhysicsStep pending;
        private CharacterControllerFrameResult latestFrame;
        private double lastSolvedTime;
        private bool solverEntered;

        internal CharacterControllerFrameResult LatestFrame => latestFrame;
        internal double LastSolvedTime => lastSolvedTime;
        internal bool HasPending => pending.IsScheduled;

        // 仅在 Unity 已完成这一步求解后才允许交付。
        internal bool ShouldCommitAtBoundary(bool inFixedStep, double time) =>
            pending.IsScheduled && (!inFixedStep || time > pending.UnityFixedTime || solverEntered);

        // 记录待求解步；同一固定步中的后续脚本仍有机会撤回。
        internal void Schedule(in CharacterPendingPhysicsStep step)
        {
            pending = step;
            solverEntered = false;
        }

        // 进入引擎求解或收到真实碰撞回调时，同一固定步里的脚本可交付已求解结果。
        internal void MarkSolverEntered() => solverEntered = pending.IsScheduled;

        // 读取刚体求解结果并清空待结算步，使后续通知中的暂停或重置不会重复提交。
        internal bool TryCommitSolved(CharacterRigidbodyMotor motor, int version, out CharacterMotorObservation actual)
        {
            actual = default;
            if (!pending.IsScheduled) return false;

            actual = motor.ReadSolvedPose();
            latestFrame = pending.ResolveFrame(actual, version, latestFrame);
            lastSolvedTime = pending.Time;
            DiscardPending();
            return true;
        }

        // 清除已交付或已撤销的步及其求解资格。
        internal void DiscardPending()
        {
            pending = default;
            solverEntered = false;
        }

        internal void ResetFrame()
        {
            lastSolvedTime = 0d;
            latestFrame = default;
            DiscardPending();
        }
    }

    [Serializable]
    internal sealed class CharacterSimulationBlackboard
    {
        private bool hasInputFocus = true;
        private bool applicationPaused;
        private bool gameplayPaused;
        private bool simulationRunning;
        private bool suppressPrediction;
        private int lifecycleVersion;
        private int resetVersion;

        internal bool HasInputFocus { get => hasInputFocus; set => hasInputFocus = value; }
        internal bool ApplicationPaused { get => applicationPaused; set => applicationPaused = value; }
        internal bool Running { get => simulationRunning; set => simulationRunning = value; }
        internal bool PredictionSuppressed { get => suppressPrediction; set => suppressPrediction = value; }
        internal int LifecycleVersion => lifecycleVersion;
        internal int ResetVersion => resetVersion;

        internal bool CanSimulate(Behaviour component, CharacterRigidbodyMotor motor) =>
            component != null && CanRun(component.isActiveAndEnabled, motor.Available, Time.timeScale);

        internal bool CanRun(bool componentActive, bool motorAvailable, float timeScale) =>
            componentActive && motorAvailable && hasInputFocus && !applicationPaused && !gameplayPaused && timeScale > 0f;

        internal bool SetGameplayPaused(bool paused)
        {
            if (gameplayPaused == paused) return false;
            gameplayPaused = paused;
            return true;
        }

        internal void AdvanceContinuity() => lifecycleVersion++;
        internal void AdvanceReset() => resetVersion++;
    }

    [Serializable]
    internal sealed class CharacterConfigurationBlackboard
    {
        private CharacterProfile initializedProfile;
        private bool profileInitialized;
        private CharacterBehaviorRegistration registration;
        internal readonly CharacterMotorSettings DefaultMotor = new CharacterMotorSettings();
        private readonly CharacterMovementSettings defaultMovement = new CharacterMovementSettings();
        private readonly CharacterMovementSettings resolvedMovement = new CharacterMovementSettings();
        private readonly CharacterForecastSettings fallbackForecast = new CharacterForecastSettings();

        internal CharacterBehaviorRegistration Registration => registration;

        internal CharacterProfile ResolveProfile(CharacterProfile currentProfile) =>
            profileInitialized ? initializedProfile : currentProfile;

        internal void BindProfile(CharacterProfile profile)
        {
            initializedProfile = profile;
            profileInitialized = true;
        }

        internal void InitializeRegistration(CharacterProfile source, bool allowGuard) =>
            registration ??= ReadRegistration(source, allowGuard);

        internal CharacterBehaviorFlow CreateForecastFlow() => CharacterBehaviorFlow.ForCharacter(registration, snapshot: true);

        internal CharacterBehaviorRegistration ResolveRegistration(CharacterProfile source, bool allowGuard) =>
            registration ?? ReadRegistration(source, allowGuard);

        private static CharacterBehaviorRegistration ReadRegistration(CharacterProfile source, bool allowGuard) =>
            source ? new CharacterBehaviorRegistration(allowGuard, source.LightAttack, source.ComboRoutes, source.Guard)
                : new CharacterBehaviorRegistration(allowGuard, null, null, null);

        // 为当前行为步填充可复用的参数，需要跨线程读取时创建独立目标。
        internal CharacterMovementSettings ReadMovement(CharacterProfile source, bool freeze)
        {
            if (!source) return freeze ? defaultMovement.Snapshot() : defaultMovement;
            var target = freeze ? new CharacterMovementSettings() : resolvedMovement;
            source.ReadMovement(target);
            return target;
        }

        internal CharacterForecastSettings ReadForecast(CharacterProfile source) =>
            (source ? source.Forecast : null) ?? fallbackForecast;
    }
}
