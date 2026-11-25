using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>汇总角色共享的行为、物理、动画与预测资产。</summary>
    [CreateAssetMenu(menuName = "3C/角色配置", fileName = "CharacterProfile")]
    public sealed class CharacterProfile : ScriptableObject
    {
        [SerializeField, InspectorName("01 角色行为")] private CharacterBehaviorConfig behaviorConfig;
        [SerializeField, InspectorName("02 连招表")] private CharacterComboTable comboTable;
        [SerializeField, InspectorName("03 移动与物理")] private CharacterMovementConfig movementConfig;
        [SerializeField, InspectorName("04 动画表现")] private CharacterAnimationConfig animationConfig;
        [SerializeField, InspectorName("05 移动预测")] private CharacterForecastConfig forecastConfig;

        [NonSerialized] private CharacterForecastSettings runtimeForecast = new CharacterForecastSettings(); // 未绑定预测资产时供公开属性读写。

        internal CharacterLocomotionSettings Movement => movementConfig ? movementConfig.Movement : null;
        internal CharacterMotorSettings Motor => movementConfig ? movementConfig.Motor : null;
        internal CharacterAnimationConfig AnimationConfig => animationConfig;
        internal CharacterMotionDatabase EffectivePlayback => animationConfig ? animationConfig.PlaybackLibrary : null;
        internal CharacterLightAttackSettings LightAttack => behaviorConfig ? behaviorConfig.Attack : null;
        internal CharacterComboRoutes ComboRoutes => comboTable ? comboTable.Routes : null;
        internal CharacterGuardSettings Guard => behaviorConfig ? behaviorConfig.Guard : null;
        internal CharacterMoveGait DefaultGait => behaviorConfig ? behaviorConfig.DefaultGait : CharacterMoveGait.Run;
        /// <summary>读取或设置移动预测数值，未绑定预测资产时仅在当前实例保留。</summary>
        public CharacterForecastSettings Forecast
        {
            get => forecastConfig ? forecastConfig.Forecast : runtimeForecast;
            set
            {
                if (forecastConfig) forecastConfig.Forecast = value;
                else runtimeForecast = value;
            }
        }

        // 保留行为上下文的公共数据类型，并避免行为调用方写入共享资产。
        internal void ReadMovement(CharacterMovementSettings result)
        {
            var movement = Movement;
            var dash = behaviorConfig ? behaviorConfig.Dash : null;
            result.WalkSpeed = movement?.WalkSpeed ?? CharacterMovementSettings.DefaultWalkSpeed;
            result.MoveSpeed = movement?.MoveSpeed ?? CharacterMovementSettings.DefaultMoveSpeed;
            result.SprintSpeed = movement?.SprintSpeed ?? CharacterMovementSettings.DefaultSprintSpeed;
            result.MoveTurnSpeed = movement?.MoveTurnSpeed ?? CharacterMovementSettings.DefaultMoveTurnSpeed;
            result.DashSpeed = dash?.DashSpeed ?? CharacterMovementSettings.DefaultDashSpeed;
            result.DashDuration = dash?.DashDuration ?? CharacterMovementSettings.DefaultDashDuration;
            result.DashCooldown = dash?.DashCooldown ?? CharacterMovementSettings.DefaultDashCooldown;
            result.DashStaminaCost = dash?.DashStaminaCost ?? CharacterMovementSettings.DefaultDashStaminaCost;
            result.InitialStamina = dash?.InitialStamina ?? CharacterMovementSettings.DefaultInitialStamina;
        }

        // 绑定此角色配置的动画资产，供编辑器检查资源与行为映射。
        internal CharacterAnimationConfiguration BindAnimation(CharacterModelBindings model, CharacterProfileOverrides overrides,
            AnimationModuleAssembly modules = null)
        {
            if (!animationConfig) throw new ArgumentException($"角色配置 {name} 缺少动画表现配置。");
            return animationConfig.Bind(model, overrides, LightAttack, modules);
        }

        // 从基础步态的跑步循环读取半周期步距，没有有效轨道时使用物理默认值。
        internal float RunFootfallDistance()
        {
            var library = EffectivePlayback;
            if (!library) return CharacterMotorSettings.DefaultFootfallDistance;
            string runState;
            try { runState = animationConfig.ResolveModules().Gait?.RunState; }
            catch (ArgumentException) { return CharacterMotorSettings.DefaultFootfallDistance; } // 动画宿主报告配置错误。
            var track = library.FindBakedCycle(runState);
            return track != null && track.IsReady ? track.ReferenceSpeed * track.Seconds * 0.5f
                : CharacterMotorSettings.DefaultFootfallDistance;
        }
    }

    [Serializable]
    internal struct CharacterFloatOverride
    {
        [Tooltip("勾选后使用下方数值，取消勾选时继承角色配置资产。")]
        public bool Enabled;
        [Min(0f)] public float Value;
        public float Resolve(float inherited) => Enabled ? Value : inherited;
    }

    [Serializable]
    internal sealed class CharacterProfileOverrides
    {
        [Header("移动与物理")]
        [Tooltip("地面加速度，单位为米/秒²。")] public CharacterFloatOverride GroundAcceleration;
        [Header("动画表现")]
        [Tooltip("起步阶段的时长，单位为秒。")] public CharacterFloatOverride StartSeconds;
        [Tooltip("普通动画过渡，占源动画长度的比例，范围为 0 到 1。")] public CharacterFloatOverride Transition;
        [Header("移动预测")]
        [InspectorName("预测刷新间隔（秒）")] public CharacterFloatOverride ForecastInterval;
        [InspectorName("预测时长（秒）")] public CharacterFloatOverride ForecastHorizon;
    }
}
