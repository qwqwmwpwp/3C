using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>汇总角色共享的行为、物理、动画与预测资产。</summary>
    [CreateAssetMenu(menuName = "3C/角色配置", fileName = "CharacterProfile")]
    public sealed class CharacterProfile : ScriptableObject
    {
        [SerializeField, InspectorName("01 角色行为")] private CharacterBehaviorConfig behaviorConfig;
        [SerializeField, InspectorName("02 攻击状态表")] private CharacterComboTable comboTable;
        [SerializeField, InspectorName("03 移动与物理")] private CharacterMovementConfig movementConfig;
        [SerializeField, InspectorName("04 动画表现")] private CharacterAnimationConfig animationConfig;
        [SerializeField, InspectorName("行为动画绑定")] private CharacterBehaviorAnimationProfile animationActions;
        [SerializeField, InspectorName("05 移动预测")] private CharacterForecastConfig forecastConfig;

        [NonSerialized] private CharacterForecastSettings runtimeForecast = new CharacterForecastSettings(); // 未绑定预测资产时供公开属性读写。

        internal CharacterLocomotionSettings Movement => movementConfig ? movementConfig.Movement : null;
        internal CharacterMotorSettings Motor => movementConfig ? movementConfig.Motor : null;
        internal CharacterAnimationConfig AnimationConfig => animationConfig;
        internal CharacterMotionDatabase EffectivePlayback => animationConfig ? animationConfig.Library : null;
        internal CharacterBehaviorAnimationProfile AnimationActions => animationActions;
        internal CharacterAttackTable AttackTable => comboTable ? comboTable.Data : null;
        internal CharacterBehaviorDefinition[] AdditionalBehaviors => behaviorConfig ? behaviorConfig.Additional : null;
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

        // 物理步距沿用播放库的跑步落脚间隔。
        internal float RunFootfallDistance() => animationConfig
            ? animationConfig.ReadRunFootfallDistance(CharacterMotorSettings.DefaultFootfallDistance)
            : CharacterMotorSettings.DefaultFootfallDistance;
    }

    [Serializable]
    internal struct CharacterFloatOverride
    {
        [Tooltip("勾选后使用下方数值，取消勾选时继承角色配置资产。")]
        public bool Enabled;
        [Min(0f)] public float Value;
        public float Resolve(float inherited) => Enabled ? Value : inherited;
    }

    // 提供角色实例对运动、动画及预测数值的逐项覆盖。
    [Serializable]
    internal sealed class CharacterProfileOverrides
    {
        [Header("移动与物理")]
        [Tooltip("地面加速度，单位为米/秒²。在下一物理步生效，相关旧预测失效。")] public CharacterFloatOverride GroundAcceleration;
        [Header("移动预测")]
        [InspectorName("预测刷新间隔（秒）")] public CharacterFloatOverride ForecastInterval;
        [InspectorName("预测时长（秒）")] public CharacterFloatOverride ForecastHorizon;
    }
}
