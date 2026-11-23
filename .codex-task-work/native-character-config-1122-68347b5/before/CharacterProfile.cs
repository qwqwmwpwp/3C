using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/角色配置", fileName = "CharacterProfile")]
    public sealed class CharacterProfile : ScriptableObject
    {
        [SerializeField, InspectorName("移动与转向")] internal CharacterLocomotionSettings Movement = new CharacterLocomotionSettings();
        [SerializeField, InspectorName("刚体与制动")] internal CharacterMotorSettings Motor = new CharacterMotorSettings();
        [InspectorName("移动预测")] public CharacterForecastSettings Forecast = new CharacterForecastSettings();
        [SerializeField, InspectorName("短突进与体力")] private CharacterDashSettings dash = new CharacterDashSettings();
        [SerializeField, InspectorName("攻击与连招")] internal CharacterLightAttackSettings LightAttack;
        [SerializeField, InspectorName("步态与动作选择")] internal CharacterGaitSettings Gaits = new CharacterGaitSettings();
        [SerializeField, InspectorName("动画表现")] internal CharacterPresentationSettings Presentation = new CharacterPresentationSettings();
        [SerializeField, InspectorName("地面动作数据库")] internal CharacterMotionDatabase MotionDatabase;
        [SerializeField, InspectorName("播放库"), Tooltip("留空时使用地面动作数据库。")] internal CharacterMotionDatabase PlaybackLibrary;
        internal CharacterMotionDatabase EffectivePlayback => PlaybackLibrary ? PlaybackLibrary : MotionDatabase;

        // 保留行为上下文的公共数据类型，并避免行为调用方写入共享资产。
        internal void ReadMovement(CharacterMovementSettings result)
        {
            result.WalkSpeed = Movement.WalkSpeed;
            result.MoveSpeed = Movement.MoveSpeed;
            result.SprintSpeed = Movement.SprintSpeed;
            result.MoveTurnSpeed = Movement.MoveTurnSpeed;
            result.DashSpeed = dash.DashSpeed;
            result.DashDuration = dash.DashDuration;
            result.DashCooldown = dash.DashCooldown;
            result.DashStaminaCost = dash.DashStaminaCost;
            result.InitialStamina = dash.InitialStamina;
        }

        internal CharacterAnimationConfiguration BindAnimation(CharacterModelBindings model, CharacterProfileOverrides overrides)
        {
            return new CharacterAnimationConfiguration
            {
                ReuseActionMixers = Presentation.ReuseActionMixers,
                PlaybackLibrary = EffectivePlayback,
                AttackSettings = LightAttack,
                MatchTurnAngle = Gaits.matchTurnAngle,
                MatchDatabase = MotionDatabase,
                MatchMargin = Gaits.matchSwitchMargin,
                MatchReverseHold = Gaits.matchReverseHold,
                FullLayer = Presentation.layerIndex,
                LowerLayer = Presentation.lowerBodyLayerIndex,
                OverlayLayer = Presentation.overlayLayerIndex,
                UpperLayer = Presentation.upperBodyLayerIndex,
                IdleState = Presentation.idleStateName,
                MoveState = Presentation.moveStateName,
                WalkState = Presentation.walkStateName,
                SprintState = Presentation.sprintStateName,
                DashState = Presentation.dashStateName,
                LocomotionPriority = Presentation.locomotionPriority,
                DashPriority = Presentation.dashPriority,
                Transition = overrides.Transition.Resolve(Presentation.behaviorTransitionDuration),
                DashTransition = Presentation.dashTransitionDuration,
                IdleEnterSpeed = Gaits.Idle.Down,
                IdleExitSpeed = Gaits.Idle.Up,
                RunEnterSpeed = Gaits.Run.Up,
                RunExitSpeed = Gaits.Run.Down,
                SprintEnterSpeed = Gaits.Sprint.Up,
                SprintExitSpeed = Gaits.Sprint.Down,
                WalkSpeed = ReferenceSpeed(Presentation.walkStateName, Presentation.walkAnimationSpeed),
                RunSpeed = ReferenceSpeed(Presentation.moveStateName, Presentation.runAnimationSpeed),
                SprintSpeed = ReferenceSpeed(Presentation.sprintStateName, Presentation.sprintAnimationSpeed),
                DashClipSpeed = Presentation.dashAnimationSpeed,
                LocomotionStartSeconds = overrides.StartSeconds.Resolve(Gaits.locomotionStartSeconds),
                LocomotionTurnSeconds = Gaits.locomotionTurnSeconds,
                LocomotionReverseAngle = Gaits.locomotionReverseAngle,
                WalkStartState = Presentation.walkStartStateName,
                WalkStartNormalizedTime = Presentation.walkStartNormalizedTime,
                RunStartState = Presentation.runStartStateName,
                RunStartNormalizedTime = Presentation.runStartNormalizedTime,
                RunStartClip = Presentation.runStartClip,
                WalkToRunBlendSeconds = Presentation.walkToRunBlendSeconds,
                TurnLeftState = Presentation.turnLeftStateName,
                TurnRightState = Presentation.turnRightStateName,
                MinimumStopTravel = Gaits.minimumStopTravel,
                WalkStop = Presentation.walkStop,
                RunStop = Presentation.runStop,
                SprintStop = Presentation.sprintStop,
                LocomotionGaitPhases = Presentation.locomotionGaitPhases,
                Actions = Presentation.actions,
                BehaviorAnimations = Presentation.behaviorAnimations,
                IkChannels = model.BindChannels(Presentation.IkChannels),
                IkTeleportDistance = Presentation.ikTeleportDistance,
                PoseInertia = Presentation.Inertia,
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
        [InspectorName("预测刷新间隔（秒）")] public CharacterFloatOverride ForecastInterval;
        [InspectorName("预测时长（秒）")] public CharacterFloatOverride ForecastHorizon;
        [Tooltip("地面加速度，单位为米/秒²。")] public CharacterFloatOverride GroundAcceleration;
        [Tooltip("起步阶段的时长，单位为秒。")] public CharacterFloatOverride StartSeconds;
        [Tooltip("普通动画过渡，占源动画长度的比例，范围为 0 到 1。")] public CharacterFloatOverride Transition;
    }
}
