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
        // 实走复用当前角色的参数，帧末预测直接取得与角色隔离的数值副本。
        internal CharacterMovementSettings ReadMovement(bool freeze = false) => board.ReadMovement(Profile, freeze);

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
            float acceleration = Profile ? Overrides.GroundAcceleration.Resolve(motorSettings.groundAcceleration) : motorSettings.groundAcceleration;
            return new CharacterDriveParameters(motorSettings, acceleration, TurnSpeed, footfallDistance,
                body.useGravity, Physics.gravity, body.drag);
        }

        internal void InitializeRegistration() => board.InitializeRegistration(Profile, allowGuard);
        // 按当前角色配置装配独立流程，预测时按值捕获动作定义和连招路线。
        internal CharacterBehaviorFlow CreateBehaviorFlow(bool snapshot = false) =>
            CharacterBehaviorAssembly.ForCharacter(board.ResolveRegistration(Profile, allowGuard), snapshot);
    }
}
