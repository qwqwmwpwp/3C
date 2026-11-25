using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>动画入口的序列化配置快照，在创建流水线时交给各阶段。</summary>
    internal sealed class CharacterAnimationConfiguration
    {
        public AnimationModuleAssembly Modules { get; }
        public CharacterMatchMode MatchMode;
        internal GroundMatchTuning MatchCosts = new GroundMatchTuning();
        internal GroundPaceLimits PaceLimits;
        internal CharacterTransitionProfiles Transitions;
        internal GroundFeatureLayout FeatureLayout;
        internal GroundCandidatePolicy CandidatePolicy;
        public bool HasGait => Modules.Gait;
        public bool HasStartTurn => Modules.StartTurn;
        public bool HasStop => Modules.Stop;
        public bool SamplesFeet => MatchDatabase || HasStop;
        public CharacterMotionDatabase PlaybackLibrary, MatchDatabase;
        public CharacterLightAttackSettings AttackSettings;
        // 保留普通转向使用的角度与续播窗口，安装匹配模块时可覆盖。
        public float MatchMargin = 0.12f;
        public float MatchReverseHold = 0.08f;
        public float MatchTurnAngle = 55f;
        public bool ReuseActionMixers;

        public string IdleState;
        public string MoveState;
        public string WalkState;
        public string SprintState;
        public string DashState;
        public int LocomotionPriority;
        public int DashPriority;
        public float Transition;
        public float DashTransition;
        // 即使只有动作模块，静止与移动动作变体仍使用步态的默认迟滞。
        public float IdleEnterSpeed = 0.08f;
        public float IdleExitSpeed = 0.15f;
        public bool UseMovementWalkSpeed;
        public float RunEnterSpeed = 3.5f;
        public float RunExitSpeed = 3f;
        public float SprintEnterSpeed = 6f;
        public float SprintExitSpeed = 5.6f;
        public float WalkSpeed = 1.55f;
        public float RunSpeed = 4.98f;
        public float SprintSpeed = 6.52f;
        public float DashClipSpeed;
        public float LocomotionStartSeconds;
        public float LocomotionTurnSeconds;
        public float LocomotionReverseAngle = 120f;
        public string WalkStartState;
        public float WalkStartNormalizedTime;
        public string RunStartState;
        public AnimationClip RunStartClip;
        public float WalkToRunBlendSeconds = 0.14f;
        public float RunStartNormalizedTime;
        public string TurnLeftState;
        public string TurnRightState;
        public float MinimumStopTravel = 0.09f;
        public CharacterStopAnimationSettings WalkStop;
        public CharacterStopAnimationSettings RunStop;
        public CharacterStopAnimationSettings SprintStop;
        public CharacterLocomotionGaitPhaseSettings[] LocomotionGaitPhases;
        public CharacterAnimationActionSettings[] Actions;
        public CharacterBehaviorAnimationMapping[] BehaviorAnimations;
        public CharacterBoundIkChannel[] IkChannels;
        internal OrientationWarpTuning Orientation;
        public CharacterInertiaTuning PoseInertia;
        public CharacterInertiaBones InertiaBones;
        public float IkTeleportDistance;
        public float WalkableSlope = 45f;

        // 固定本次绑定所使用的模块组合，空组合也拥有独立装配。
        internal CharacterAnimationConfiguration(AnimationModuleAssembly modules) => Modules = modules;

        // 按角色运动配置解析行走与跑步的切换门槛，并提供 IK 使用的坡度上限。
        internal void BindMovement(CharacterMovementSettings movement, float walkableSlope)
        {
            WalkableSlope = walkableSlope;
            if (!UseMovementWalkSpeed) return;
            float band = Mathf.Max(0f, RunEnterSpeed - RunExitSpeed);
            RunEnterSpeed = InputMath.NonNegative(movement?.WalkSpeed ?? RunEnterSpeed, RunEnterSpeed);
            RunExitSpeed = Mathf.Max(IdleExitSpeed, RunEnterSpeed - band);
        }
    }
}
