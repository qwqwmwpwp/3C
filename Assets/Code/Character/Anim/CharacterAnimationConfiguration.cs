using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>动画入口的序列化配置快照，在创建流水线时交给各阶段。</summary>
    internal sealed class CharacterAnimationConfiguration
    {
        public AnimationModuleAssembly Modules;
        public CharacterMatchMode MatchMode;
        public bool HasGait => Modules == null || Modules.Gait;
        public bool HasStartTurn => Modules == null || Modules.StartTurn;
        public bool HasStop => Modules == null || Modules.Stop;
        public bool SamplesFeet => MatchDatabase || Modules != null && Modules.Stop;
        public CharacterMotionDatabase PlaybackLibrary, MatchDatabase;
        public CharacterLightAttackSettings AttackSettings;
        public float MatchMargin, MatchReverseHold, MatchTurnAngle;
        public bool ReuseActionMixers;
        public int FullLayer;
        public int OverlayLayer = -1;
        public int LowerLayer;
        public int UpperLayer;
        public int ValidOverlayLayer => OverlayLayer > FullLayer && (LowerLayer < 0 || OverlayLayer < LowerLayer)
            && (UpperLayer < 0 || OverlayLayer < UpperLayer) ? OverlayLayer : -1;
        public int LegacyLayer(CharacterAnimationBodyRegion source) => source switch
        {
            CharacterAnimationBodyRegion.FullBody => FullLayer,
            CharacterAnimationBodyRegion.FullBodyOverlay => ValidOverlayLayer,
            CharacterAnimationBodyRegion.LowerBody when LowerLayer != UpperLayer && LowerLayer != FullLayer => LowerLayer,
            CharacterAnimationBodyRegion.UpperBody when LowerLayer != UpperLayer && UpperLayer != FullLayer => UpperLayer,
            _ => -1
        };

        public string IdleState;
        public string MoveState;
        public string WalkState;
        public string SprintState;
        public string DashState;
        public int LocomotionPriority;
        public int DashPriority;
        public float Transition;
        public float DashTransition;
        public float IdleEnterSpeed;
        public float IdleExitSpeed;
        public bool UseMovementWalkSpeed;
        public float RunEnterSpeed;
        public float RunExitSpeed;
        public float SprintEnterSpeed;
        public float SprintExitSpeed;
        public float WalkSpeed;
        public float RunSpeed;
        public float SprintSpeed;
        public float DashClipSpeed;
        public float LocomotionStartSeconds;
        public float LocomotionTurnSeconds;
        public float LocomotionReverseAngle;
        public string WalkStartState;
        public float WalkStartNormalizedTime;
        public string RunStartState;
        public AnimationClip RunStartClip;
        public float WalkToRunBlendSeconds;
        public float RunStartNormalizedTime;
        public string TurnLeftState;
        public string TurnRightState;
        public float MinimumStopTravel;
        public CharacterStopAnimationSettings WalkStop;
        public CharacterStopAnimationSettings RunStop;
        public CharacterStopAnimationSettings SprintStop;
        public CharacterLocomotionGaitPhaseSettings[] LocomotionGaitPhases;
        public CharacterAnimationActionSettings[] Actions;
        public CharacterBehaviorAnimationMapping[] BehaviorAnimations;
        public CharacterBoundIkChannel[] IkChannels;
        public CharacterInertiaTuning PoseInertia;
        public CharacterInertiaBones InertiaBones;
        public float IkTeleportDistance, WalkableSlope;
    }
}
