using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>定义待机和地面基础步态的素材、迟滞阈值与播放速度。</summary>
    [CreateAssetMenu(menuName = "3C/动画模块/基础步态", fileName = "基础步态")]
    public sealed class GaitModule : AnimationModule
    {
        [InspectorName("待机状态"), SerializeField, Tooltip("待机状态在播放库中的完整路径。留空时继承动画表现配置。")]
        private string idleStateName;
        [InspectorName("常跑状态"), SerializeField, Tooltip("移动状态完整路径，持续移动不重复从头播放。")]
        private string moveStateName = "Lower Body.move";
        [InspectorName("步行状态"), SerializeField, Tooltip("低速行走状态。留空时使用常跑状态。")]
        private string walkStateName;
        [InspectorName("持续冲刺状态"), SerializeField, Tooltip("持续冲刺循环的完整状态路径；缺少该状态时使用跑步。")]
        private string sprintStateName = "Lower Body.sprint";
        [InspectorName("移动优先级"), SerializeField, Tooltip("普通待机和移动的全身优先级；同优先级的局部需求不能覆盖全身。")]
        private int locomotionPriority;
        [SerializeField, InspectorName("普通过渡（归一化）"), Range(0f, 1f), Tooltip("普通行为动画的归一化过渡时长；相对源状态长度，建议 0.12。")]
        private float behaviorTransitionDuration = 0.12f;
        [SerializeField, InspectorName("步行无轨道参考速度"), Min(0.01f), Tooltip("步行素材以 1 倍速播放时的前进速度（米/秒），供步行和步行起步换算步频。")]
        private float walkAnimationSpeed = 1.55f;
        [SerializeField, InspectorName("跑步无轨道参考速度"), Min(0.01f), Tooltip("跑步素材以 1 倍速播放时的前进速度（米/秒），供跑步和跑步起步换算步频。")]
        private float runAnimationSpeed = 4.98f;
        [SerializeField, InspectorName("持续冲刺无轨道参考速度"), Min(0.01f), Tooltip("持续冲刺素材的名义速度，用于按实际速度调整步频。")]
        private float sprintAnimationSpeed = 6.52f;
        [SerializeField]
        [InspectorName("待机 ↔ 行走")] private GaitSpeedThresholds Idle = new GaitSpeedThresholds(0.15f, 0.08f);
        [SerializeField]
        [InspectorName("行走 ↔ 跑步")] private GaitSpeedThresholds Run = new GaitSpeedThresholds(3.5f, 3f);
        [SerializeField]
        [InspectorName("跑步 ↔ 持续冲刺")] private GaitSpeedThresholds Sprint = new GaitSpeedThresholds(6f, 5.6f);
        [SerializeField, InspectorName("使用角色步行速度换跑"), Tooltip("以物理步行速度作为进入跑步的门槛，保留配置的升降档间距。")]
        private bool useMovementWalkSpeed;
        [SerializeField, InspectorName("步态触地相位")]
        private CharacterLocomotionGaitPhaseSettings[] locomotionGaitPhases;

        internal string RunState => moveStateName;

        internal override void Include(AnimationModuleAssembly assembly) => assembly.Add(this);

        // 将基础步态写入角色配置，待机路径未填写时保留配置的默认待机。
        internal void Apply(CharacterAnimationConfiguration config)
        {
            if (!string.IsNullOrWhiteSpace(idleStateName)) config.IdleState = idleStateName;
            config.MoveState = moveStateName;
            config.WalkState = walkStateName;
            config.SprintState = sprintStateName;
            config.LocomotionPriority = locomotionPriority;
            config.Transition = behaviorTransitionDuration;
            config.WalkSpeed = CharacterAnimationConfig.ReferenceSpeed(config.PlaybackLibrary, walkStateName, walkAnimationSpeed);
            config.RunSpeed = CharacterAnimationConfig.ReferenceSpeed(config.PlaybackLibrary, moveStateName, runAnimationSpeed);
            config.SprintSpeed = CharacterAnimationConfig.ReferenceSpeed(config.PlaybackLibrary, sprintStateName, sprintAnimationSpeed);
            config.IdleEnterSpeed = Idle.Down;
            config.IdleExitSpeed = Idle.Up;
            config.UseMovementWalkSpeed = useMovementWalkSpeed;
            config.RunEnterSpeed = Run.Up;
            config.RunExitSpeed = Run.Down;
            config.SprintEnterSpeed = Sprint.Up;
            config.SprintExitSpeed = Sprint.Down;
            config.LocomotionGaitPhases = locomotionGaitPhases;
        }
    }

    // 记录步态升档和降档各自使用的速度阈值。
    [Serializable]
    internal struct GaitSpeedThresholds
    {
        [InspectorName("升档（米/秒）"), Min(0f)] public float Up;
        [InspectorName("降档（米/秒）"), Min(0f)] public float Down;

        // 保存步态切换的上下沿，避免临界速度反复切换。
        public GaitSpeedThresholds(float up, float down)
        {
            Up = up;
            Down = down;
        }
    }
}
