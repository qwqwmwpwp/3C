using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/角色配置", fileName = "CharacterProfile")]
    public sealed class CharacterProfile : ScriptableObject
    {
        [SerializeField, InspectorName("01 角色行为")] private CharacterBehaviorConfig behaviorConfig;
        [SerializeField, InspectorName("02 连招表")] private CharacterComboTable comboTable;
        [SerializeField, InspectorName("03 移动与物理")] private CharacterMovementConfig movementConfig;
        [SerializeField, InspectorName("04 动画表现")] private CharacterAnimationConfig animationConfig;
        [SerializeField, InspectorName("05 移动预测")] private CharacterForecastConfig forecastConfig;

        // 旧角色使用内嵌参数；每个部分独立切换到对应的资产。
        [FormerlySerializedAs("Movement"), SerializeField, HideInInspector] private CharacterLocomotionSettings legacyMovement = new CharacterLocomotionSettings();
        [FormerlySerializedAs("Motor"), SerializeField, HideInInspector] private CharacterMotorSettings legacyMotor = new CharacterMotorSettings();
        [FormerlySerializedAs("Forecast"), SerializeField, HideInInspector] private CharacterForecastSettings legacyForecast = new CharacterForecastSettings();
        [FormerlySerializedAs("dash"), SerializeField, HideInInspector] private CharacterDashSettings legacyDash = new CharacterDashSettings();
        [FormerlySerializedAs("LightAttack"), SerializeField, HideInInspector] private LegacyAttackSettings legacyAttack;
        [FormerlySerializedAs("Gaits"), SerializeField, HideInInspector] private CharacterGaitSettings legacyGaits = new CharacterGaitSettings();
        [FormerlySerializedAs("Presentation"), SerializeField, HideInInspector] private CharacterPresentationSettings legacyPresentation = new CharacterPresentationSettings();
        [FormerlySerializedAs("MotionDatabase"), SerializeField, HideInInspector] private CharacterMotionDatabase legacyMotionDatabase;
        [FormerlySerializedAs("PlaybackLibrary"), SerializeField, HideInInspector] private CharacterMotionDatabase legacyPlaybackLibrary;

        internal CharacterLocomotionSettings Movement => movementConfig ? movementConfig.Movement : legacyMovement;
        internal CharacterMotorSettings Motor => movementConfig ? movementConfig.Motor : legacyMotor;
        internal CharacterGaitSettings Gaits => animationConfig ? animationConfig.Gaits : legacyGaits;
        internal CharacterPresentationSettings Presentation => animationConfig ? animationConfig.Presentation : legacyPresentation;
        internal CharacterMotionDatabase MotionDatabase => animationConfig ? animationConfig.MotionDatabase : legacyMotionDatabase;
        private CharacterMotionDatabase PlaybackLibrary => animationConfig ? animationConfig.PlaybackLibrary : legacyPlaybackLibrary;
        internal CharacterMotionDatabase EffectivePlayback => PlaybackLibrary ? PlaybackLibrary : MotionDatabase;
        internal CharacterLightAttackSettings LightAttack => behaviorConfig ? behaviorConfig.Attack : legacyAttack?.ReadMoves();
        internal CharacterComboRoutes ComboRoutes => comboTable ? comboTable.Routes : behaviorConfig ? null : legacyAttack?.Routes;
        internal CharacterGuardSettings Guard => behaviorConfig ? behaviorConfig.Guard : null;
        internal CharacterMoveGait DefaultGait => behaviorConfig ? behaviorConfig.DefaultGait : CharacterMoveGait.Run;
        public CharacterForecastSettings Forecast
        {
            get => forecastConfig ? forecastConfig.Forecast : legacyForecast;
            set
            {
                if (forecastConfig) forecastConfig.Forecast = value;
                else legacyForecast = value;
            }
        }

        // 保留行为上下文的公共数据类型，并避免行为调用方写入共享资产。
        internal void ReadMovement(CharacterMovementSettings result)
        {
            var movement = Movement;
            var dash = behaviorConfig ? behaviorConfig.Dash : legacyDash;
            result.WalkSpeed = movement.WalkSpeed;
            result.MoveSpeed = movement.MoveSpeed;
            result.SprintSpeed = movement.SprintSpeed;
            result.MoveTurnSpeed = movement.MoveTurnSpeed;
            result.DashSpeed = dash.DashSpeed;
            result.DashDuration = dash.DashDuration;
            result.DashCooldown = dash.DashCooldown;
            result.DashStaminaCost = dash.DashStaminaCost;
            result.InitialStamina = dash.InitialStamina;
        }

        internal CharacterAnimationConfiguration BindAnimation(CharacterModelBindings model, CharacterProfileOverrides overrides)
        {
            var presentation = Presentation;
            var gaits = Gaits;
            return new CharacterAnimationConfiguration
            {
                ReuseActionMixers = presentation.ReuseActionMixers,
                PlaybackLibrary = EffectivePlayback,
                AttackSettings = LightAttack,
                MatchTurnAngle = gaits.matchTurnAngle,
                MatchDatabase = MotionDatabase,
                MatchMargin = gaits.matchSwitchMargin,
                MatchReverseHold = gaits.matchReverseHold,
                FullLayer = presentation.layerIndex,
                LowerLayer = presentation.lowerBodyLayerIndex,
                OverlayLayer = presentation.overlayLayerIndex,
                UpperLayer = presentation.upperBodyLayerIndex,
                IdleState = presentation.idleStateName,
                MoveState = presentation.moveStateName,
                WalkState = presentation.walkStateName,
                SprintState = presentation.sprintStateName,
                DashState = presentation.dashStateName,
                LocomotionPriority = presentation.locomotionPriority,
                DashPriority = presentation.dashPriority,
                Transition = overrides.Transition.Resolve(presentation.behaviorTransitionDuration),
                DashTransition = presentation.dashTransitionDuration,
                IdleEnterSpeed = gaits.Idle.Down,
                IdleExitSpeed = gaits.Idle.Up,
                RunEnterSpeed = gaits.Run.Up,
                RunExitSpeed = gaits.Run.Down,
                SprintEnterSpeed = gaits.Sprint.Up,
                SprintExitSpeed = gaits.Sprint.Down,
                WalkSpeed = ReferenceSpeed(presentation.walkStateName, presentation.walkAnimationSpeed),
                RunSpeed = ReferenceSpeed(presentation.moveStateName, presentation.runAnimationSpeed),
                SprintSpeed = ReferenceSpeed(presentation.sprintStateName, presentation.sprintAnimationSpeed),
                DashClipSpeed = presentation.dashAnimationSpeed,
                LocomotionStartSeconds = overrides.StartSeconds.Resolve(gaits.locomotionStartSeconds),
                LocomotionTurnSeconds = gaits.locomotionTurnSeconds,
                LocomotionReverseAngle = gaits.locomotionReverseAngle,
                WalkStartState = presentation.walkStartStateName,
                WalkStartNormalizedTime = presentation.walkStartNormalizedTime,
                RunStartState = presentation.runStartStateName,
                RunStartNormalizedTime = presentation.runStartNormalizedTime,
                RunStartClip = presentation.runStartClip,
                WalkToRunBlendSeconds = presentation.walkToRunBlendSeconds,
                TurnLeftState = presentation.turnLeftStateName,
                TurnRightState = presentation.turnRightStateName,
                MinimumStopTravel = gaits.minimumStopTravel,
                WalkStop = presentation.walkStop,
                RunStop = presentation.runStop,
                SprintStop = presentation.sprintStop,
                LocomotionGaitPhases = presentation.locomotionGaitPhases,
                Actions = presentation.actions,
                BehaviorAnimations = presentation.behaviorAnimations,
                IkChannels = model.BindChannels(presentation.IkChannels),
                IkTeleportDistance = presentation.ikTeleportDistance,
                PoseInertia = presentation.Inertia,
                InertiaBones = model.Inertia
            };
        }

        internal float RunFootfallDistance()
        {
            var library = EffectivePlayback;
            var track = library ? library.FindBakedCycle(Presentation?.moveStateName) : null;
            return track != null && track.IsReady ? track.ReferenceSpeed * track.Seconds * 0.5f
                : CharacterMotorSettings.DefaultFootfallDistance;
        }

        private float ReferenceSpeed(string state, float fallback)
        {
            var library = EffectivePlayback;
            if (!library || library.Playback?.States == null) return fallback;
            return library.FindBakedCycle(state)?.ReferenceSpeed ?? fallback;
        }
    }

    [Serializable]
    internal sealed class LegacyAttackSettings : CharacterAttackMoveSettings
    {
        public CharacterBehaviorPhase DashInterruptPhases = CharacterBehaviorPhase.Startup | CharacterBehaviorPhase.Recovery;
        public LegacyAttackCombo Combo = new LegacyAttackCombo();

        internal CharacterComboRoutes Routes => Combo ?? new CharacterComboRoutes();

        internal CharacterLightAttackSettings ReadMoves()
        {
            var extra = Combo ?? new LegacyAttackCombo();
            return new CharacterLightAttackSettings
            {
                DashInterruptPhases = DashInterruptPhases,
                Moves = new CharacterAttackComboSettings { LightOne = this, LightTwo = extra.LightTwo, Heavy = extra.Heavy },
                AllowWithoutClip = extra.AllowWithoutClip, LightSeconds = extra.LightSeconds, HeavySeconds = extra.HeavySeconds
            };
        }
    }

    [Serializable]
    internal sealed class LegacyAttackCombo : CharacterComboRoutes
    {
        public CharacterAttackMoveSettings LightTwo = new CharacterAttackMoveSettings();
        public CharacterAttackMoveSettings Heavy = new CharacterAttackMoveSettings();
        public bool AllowWithoutClip;
        public float LightSeconds = 0.6f, HeavySeconds = 0.8f;
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
