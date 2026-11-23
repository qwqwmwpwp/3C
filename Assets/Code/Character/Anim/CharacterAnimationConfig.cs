using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/角色数据/04 动画表现", fileName = "04 动画表现")]
    public sealed class CharacterAnimationConfig : ScriptableObject
    {
        [SerializeField, InspectorName("动画模块组合"), Tooltip("指定后由组合提供步态、动作、动作匹配和姿态修正。未指定的旧配置沿用内嵌数据。")]
        internal AnimationModule Modules;
        [Header("行为映射与身体表现")]
        [SerializeField, InspectorName("表现参数")] internal CharacterPresentationSettings Presentation = new CharacterPresentationSettings();
        [Header("步态与动作选择")]
        [SerializeField, InspectorName("步态参数")] internal CharacterGaitSettings Gaits = new CharacterGaitSettings();
        [Header("动作资源")]
        [SerializeField, InspectorName("地面动作数据库")] internal CharacterMotionDatabase MotionDatabase;
        [SerializeField, InspectorName("播放库"), Tooltip("留空时使用地面动作数据库。")] internal CharacterMotionDatabase PlaybackLibrary;

        internal CharacterAnimationConfiguration Bind(CharacterModelBindings model, CharacterProfileOverrides overrides,
            CharacterLightAttackSettings attack, AnimationModuleAssembly modules)
        {
            if (modules == null && Modules) modules = AnimationModuleAssembly.Resolve(Modules);
            return Bind(Presentation, Gaits, MotionDatabase, PlaybackLibrary ? PlaybackLibrary : MotionDatabase,
                attack, model, overrides, modules);
        }

        internal static CharacterAnimationConfiguration Bind(CharacterPresentationSettings presentation, CharacterGaitSettings gaits,
            CharacterMotionDatabase database, CharacterMotionDatabase library, CharacterLightAttackSettings attack,
            CharacterModelBindings model, CharacterProfileOverrides overrides, AnimationModuleAssembly modules)
        {
            var config = new CharacterAnimationConfiguration
            {
                PlaybackLibrary = library,
                AttackSettings = attack,
                ReuseActionMixers = presentation.ReuseActionMixers,
                FullLayer = presentation.layerIndex,
                LowerLayer = presentation.lowerBodyLayerIndex,
                OverlayLayer = presentation.overlayLayerIndex,
                UpperLayer = presentation.upperBodyLayerIndex,
                IkTeleportDistance = presentation.ikTeleportDistance
            };
            if (!modules?.Gait)
            {
                config.IdleState = presentation.idleStateName;
                config.MoveState = presentation.moveStateName;
                config.WalkState = presentation.walkStateName;
                config.SprintState = presentation.sprintStateName;
                config.DashState = presentation.dashStateName;
                config.LocomotionPriority = presentation.locomotionPriority;
                config.DashPriority = presentation.dashPriority;
                config.Transition = overrides.Transition.Resolve(presentation.behaviorTransitionDuration);
                config.DashTransition = presentation.dashTransitionDuration;
                config.IdleEnterSpeed = gaits.Idle.Down;
                config.IdleExitSpeed = gaits.Idle.Up;
                config.RunEnterSpeed = gaits.Run.Up;
                config.RunExitSpeed = gaits.Run.Down;
                config.SprintEnterSpeed = gaits.Sprint.Up;
                config.SprintExitSpeed = gaits.Sprint.Down;
                config.WalkSpeed = ReferenceSpeed(library, presentation.walkStateName, presentation.walkAnimationSpeed);
                config.RunSpeed = ReferenceSpeed(library, presentation.moveStateName, presentation.runAnimationSpeed);
                config.SprintSpeed = ReferenceSpeed(library, presentation.sprintStateName, presentation.sprintAnimationSpeed);
                config.DashClipSpeed = presentation.dashAnimationSpeed;
                config.LocomotionGaitPhases = presentation.locomotionGaitPhases;
            }
            if (!modules?.StartTurn)
            {
                config.LocomotionReverseAngle = gaits.locomotionReverseAngle;
                config.WalkStartNormalizedTime = presentation.walkStartNormalizedTime;
                config.RunStartNormalizedTime = presentation.runStartNormalizedTime;
                config.WalkToRunBlendSeconds = presentation.walkToRunBlendSeconds;
            }
            if (!modules?.Stop)
            {
                config.MinimumStopTravel = gaits.minimumStopTravel;
            }
            if (!modules?.Match)
            {
                config.MatchTurnAngle = gaits.matchTurnAngle;
                config.MatchMargin = gaits.matchSwitchMargin;
                config.MatchReverseHold = gaits.matchReverseHold;
            }
            if (modules == null)
            {
                config.MatchMode = gaits.motionMatching;
                config.MatchDatabase = database;
                config.LocomotionStartSeconds = overrides.StartSeconds.Resolve(gaits.locomotionStartSeconds);
                config.LocomotionTurnSeconds = gaits.locomotionTurnSeconds;
                config.WalkStartState = presentation.walkStartStateName;
                config.RunStartState = presentation.runStartStateName;
                config.RunStartClip = presentation.runStartClip;
                config.TurnLeftState = presentation.turnLeftStateName;
                config.TurnRightState = presentation.turnRightStateName;
                config.WalkStop = presentation.walkStop;
                config.RunStop = presentation.runStop;
                config.SprintStop = presentation.sprintStop;
                config.Actions = presentation.actions;
                config.BehaviorAnimations = presentation.behaviorAnimations;
                config.PoseInertia = presentation.Inertia;
                config.InertiaBones = model.Inertia;
                config.IkChannels = model.BindChannels(presentation.IkChannels);
            }
            else modules.Apply(config, model, overrides);
            return config;
        }

        internal static float ReferenceSpeed(CharacterMotionDatabase library, string state, float fallback)
        {
            if (!library || library.Playback?.States == null) return fallback;
            return library.FindBakedCycle(state)?.ReferenceSpeed ?? fallback;
        }
    }
}
