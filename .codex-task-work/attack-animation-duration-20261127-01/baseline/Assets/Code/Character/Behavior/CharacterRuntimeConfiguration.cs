using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>解析当前实例的共享配置、默认数值与覆盖值。编辑器预演也可在组件初始化前读取。</summary>
    internal readonly struct CharacterRuntimeConfiguration
    {
        private readonly CharacterConfigurationBlackboard board;
        private readonly bool allowGuard;
        internal readonly CharacterProfile Profile;
        internal readonly CharacterProfileOverrides Overrides;
        internal readonly bool UseWorkerThreads;

        // 绑定本实例的配置来源、数值覆盖和工作线程选项。
        internal CharacterRuntimeConfiguration(CharacterConfigurationBlackboard board, CharacterProfile profile,
            CharacterProfileOverrides overrides,
            bool allowGuard, bool useWorkerThreads)
        {
            this.board = board;
            Profile = board.ResolveProfile(profile);
            Overrides = overrides;
            this.allowGuard = allowGuard;
#if UNITY_WEBGL && !UNITY_EDITOR
            UseWorkerThreads = false;
#else
            UseWorkerThreads = useWorkerThreads;
#endif
        }

        internal CharacterMotorSettings Motor => (Profile ? Profile.Motor : null) ?? board.DefaultMotor;
        // 解析本次主线程计算使用的移动数值，跨帧推演由快照入口独立复制。
        internal CharacterMovementSettings ReadMovement() => board.ReadMovement(Profile);

        internal CharacterForecastSettings Forecast => board.ReadForecast(Profile);
        internal CharacterMoveGait DefaultGait => Profile ? Profile.DefaultGait : CharacterMoveGait.Run;
        internal bool GuardRegistered => board.Registration.Guard;
        internal CharacterInputCaptureSettings InputCapture => new CharacterInputCaptureSettings(DefaultGait, GuardRegistered);
        internal float FootfallDistance => Profile ? Profile.RunFootfallDistance() : CharacterMotorSettings.DefaultFootfallDistance;
        private float TurnSpeed => InputMath.NonNegative((Profile ? Profile.Movement : null)?.MoveTurnSpeed
            ?? CharacterMovementSettings.DefaultMoveTurnSpeed, CharacterMovementSettings.DefaultMoveTurnSpeed);

        // 组合实例的有效配置、绑定的刚体和烘焙步距，供实走和预演使用。
        internal CharacterDriveParameters DriveParameters(Rigidbody body, CharacterMotorSettings motorSettings, float footfallDistance)
        {
            float acceleration = Overrides.GroundAcceleration.Resolve(motorSettings.groundAcceleration);
            return new CharacterDriveParameters(motorSettings, acceleration, TurnSpeed, footfallDistance,
                body.useGravity, Physics.gravity, body.drag);
        }

        // 装配时固定行为集合和打断关系，供实走与预测的所有流程共享。
        internal void InitializeRegistration() => board.InitializeRegistration(Profile, allowGuard);
        // 未进入运行时的编辑器预演也只捕获一次注册集合，后续每份副本在主线程冻结自己的数值。
        internal System.Func<CharacterBehaviorFlow> CreateRehearsalFactory()
        {
            var registration = board.ResolveRegistration(Profile, allowGuard);
            return () => CharacterBehaviorAssembly.ForCharacter(registration, snapshot: true);
        }

        // 按当前角色配置装配独立流程，预测时按值捕获动作定义和连招路线。
        internal CharacterBehaviorFlow CreateBehaviorFlow(bool snapshot = false) =>
            CharacterBehaviorAssembly.ForCharacter(board.ResolveRegistration(Profile, allowGuard), snapshot);
    }
}
