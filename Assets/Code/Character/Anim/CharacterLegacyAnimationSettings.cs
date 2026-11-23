using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>未引用角色配置资产的旧预制体仍按原序列化字段创建动画配置。</summary>
    public abstract class CharacterLegacyAnimationSettings : MonoBehaviour
    {
        [Header("行为动画映射")]
        [HideInInspector, SerializeField, Min(0), Tooltip("全身动画所在的基础层索引。")]
        private int layerIndex;
        [HideInInspector, SerializeField, Tooltip("待机状态完整路径，包含层和子状态机名称。")]
        private string idleStateName = "Base Layer.idle";
        [HideInInspector, SerializeField, Tooltip("移动状态完整路径，持续移动不重复从头播放。")]
        private string moveStateName = "Base Layer.move";
        [HideInInspector, SerializeField, Tooltip("低速行走状态；留空时沿用旧版移动状态，兼容已有控制器。")]
        private string walkStateName;
        [HideInInspector, SerializeField, Tooltip("持续冲刺循环的完整状态路径；缺少该状态时使用跑步。")]
        private string sprintStateName = "Base Layer.sprint";
        [HideInInspector, SerializeField, Tooltip("短突进状态完整路径，每次新突进重新播放。")]
        private string dashStateName = "Base Layer.dash";
        [HideInInspector, SerializeField, Tooltip("普通待机和移动的全身优先级；同优先级的局部需求不能覆盖全身。")]
        private int locomotionPriority;
        [HideInInspector, SerializeField, Tooltip("冲刺的全身优先级，可用于暂时压住较低优先级的局部动作。")]
        private int dashPriority = 100;
        [HideInInspector, SerializeField, Range(0f, 1f), Tooltip("普通行为动画的归一化过渡时长；相对源状态长度，建议 0.12。")]
        private float behaviorTransitionDuration = 0.12f;
        [HideInInspector, SerializeField, Range(0f, 1f), Tooltip("冲刺进入与退出的归一化过渡时长上限，建议短于普通移动。")]
        private float dashTransitionDuration = 0.04f;

        [Header("运动选择（米/秒）")]
        [HideInInspector, SerializeField, Min(0f), Tooltip("运动中降到此速度时进入待机；应小于离开待机的阈值。")]
        private float idleEnterSpeed = 0.08f;
        [HideInInspector, SerializeField, Min(0f), Tooltip("待机中超过此速度时开始走路或跑步。")]
        private float idleExitSpeed = 0.15f;
        [HideInInspector, SerializeField, Min(0f), Tooltip("走路达到此速度时切换跑步。")]
        private float runEnterSpeed = 3.5f;
        [HideInInspector, SerializeField, Min(0f), Tooltip("跑步低于此速度时切换走路；应小于进入跑步的阈值。")]
        private float runExitSpeed = 3f;
        [HideInInspector, SerializeField, Min(0f), Tooltip("请求持续冲刺且实际速度达到此值时进入冲刺循环。")]
        private float sprintEnterSpeed = 6f;
        [HideInInspector, SerializeField, Min(0f), Tooltip("持续冲刺中实际速度低于此值时退回走跑。")]
        private float sprintExitSpeed = 5.6f;
        [HideInInspector, SerializeField, Min(0.01f), Tooltip("步行素材以 1 倍速播放时的前进速度（米/秒），供步行和步行起步换算步频。")]
        private float walkAnimationSpeed = 1.55f;
        [HideInInspector, SerializeField, Min(0.01f), Tooltip("跑步素材以 1 倍速播放时的前进速度（米/秒），供跑步和跑步起步换算步频。")]
        private float runAnimationSpeed = 4.98f;
        [HideInInspector, SerializeField, Min(0.01f), Tooltip("持续冲刺素材的名义速度，用于按实际速度调整步频。")]
        private float sprintAnimationSpeed = 6.52f;
        [HideInInspector, SerializeField, Min(0f), Tooltip("短突进素材以 1 倍速播放时的前进速度（米/秒）。0 表示按素材原速播放。")]
        private float dashAnimationSpeed;

        [Header("运动阶段")]
        [HideInInspector, SerializeField, Min(0f), Tooltip("从停止进入移动时保留起步阶段的时长，单位秒；0 表示禁用。")]
        private float locomotionStartSeconds = 0.22f;
        [HideInInspector, SerializeField, Min(0f), Tooltip("反向转身阶段的时长，单位秒；0 表示禁用。")]
        private float locomotionTurnSeconds = 0.32f;
        [HideInInspector, SerializeField, Min(0f), Tooltip("连续主动地面位移至少达到此距离才收步，单位米。")]
        private float minimumStopTravel = 0.09f;
        [HideInInspector, SerializeField] private CharacterStopAnimationSettings walkStop;
        [HideInInspector, SerializeField] private CharacterStopAnimationSettings runStop;
        [HideInInspector, SerializeField] private CharacterStopAnimationSettings sprintStop;
        [HideInInspector, SerializeField, Range(90f, 180f), Tooltip("触发反向转身所需的移动方向夹角，单位度。")]
        private float locomotionReverseAngle = 120f;
        [HideInInspector, SerializeField, Tooltip("行走起步的完整状态路径；留空或不可用时使用普通行走。")]
        private string walkStartStateName;
        [HideInInspector, SerializeField, Range(0f, 1f), Tooltip("行走起步素材的归一化起点。")]
        private float walkStartNormalizedTime;
        [HideInInspector, SerializeField, Tooltip("跑步起步的完整状态路径；留空或不可用时使用普通跑步。")]
        private string runStartStateName;
        [HideInInspector, SerializeField, Tooltip("跑步起步状态使用的片段，用于把支撑脚相位换算为固定时间混合的入点。")]
        protected AnimationClip runStartClip;
        [HideInInspector, SerializeField, Min(0f), Tooltip("普通起跑从走姿展开到跑姿的混合时间（秒）。")]
        private float walkToRunBlendSeconds = 0.14f;
        [HideInInspector, SerializeField, Range(0f, 1f), Tooltip("跑步起步素材的归一化起点。")]
        private float runStartNormalizedTime;
        [HideInInspector, SerializeField, Tooltip("向左反向转身的完整状态路径；留空或不可用时使用当前基础移动状态。")]
        private string turnLeftStateName;
        [HideInInspector, SerializeField, Tooltip("向右反向转身的完整状态路径；留空或不可用时使用当前基础移动状态。")]
        private string turnRightStateName;
        [HideInInspector, SerializeField, Tooltip("基础层循环移动状态的左右脚触地相位。无有效映射时使用动作配置的播放起点。")]
        private CharacterLocomotionGaitPhaseSettings[] locomotionGaitPhases;

        [Header("片段播放")]
        [HideInInspector, SerializeField, Tooltip("提供零参数播放用的状态、遮罩和片段曲线。留空时使用地面动作数据库。")]
        private CharacterMotionDatabase playbackLibrary;

        [Header("地面动作匹配")]
        [HideInInspector, SerializeField, InspectorName("动作数据库")] private CharacterMotionDatabase motionDatabase;
        [HideInInspector, SerializeField, InspectorName("匹配模式")] protected CharacterMatchMode motionMatching;
        [HideInInspector, SerializeField, Min(0f), Tooltip("续播相对新入点的代价宽限，越大越不易换片。")]
        private float matchSwitchMargin = 0.12f;
        [HideInInspector, SerializeField, Range(0f, 0.15f), Tooltip("反向输入后允许原动作继续的最长表现时间（秒）。")]
        private float matchReverseHold = 0.08f;
        [HideInInspector, SerializeField, Range(15f, 180f), Tooltip("持续移动中触发选姿的输入与速度改向角（度）。")]
        private float matchTurnAngle = 55f;

        [Header("局部动画层")]
        [HideInInspector, SerializeField] private int overlayLayerIndex = -1;
        [HideInInspector, SerializeField, Tooltip("带下身 AvatarMask 的 Override 层索引；-1 表示未配置。不可与全身层、上身层相同。")]
        private int lowerBodyLayerIndex = 1;
        [HideInInspector, SerializeField, Tooltip("带上身 AvatarMask 的 Override 层索引；-1 表示未配置。不可与全身层、下身层相同。")]
        private int upperBodyLayerIndex = 2;
        [HideInInspector, SerializeField, Tooltip("外部仅提交语义键和实例编号；动画路径、部位、优先级和起始进度均在此映射。")]
        private CharacterAnimationActionSettings[] actions = CharacterAnimationActionSettings.CreateDefaults();

        [Header("分区惯性")]
        [HideInInspector, SerializeField, Tooltip("基础姿态依次经过胸肩和双臂惯性、最终手脚 IK。强度为零时沿用原有姿态。")]
        private CharacterPoseInertiaSettings poseInertia = new CharacterPoseInertiaSettings();

        [Header("姿态求解（IK）")]
        [HideInInspector, SerializeField, Tooltip("按列表顺序求解。动画许可由素材曲线控制，未配置的角色沿用基础姿态。")]
        private CharacterIkChannel[] ikChannels;
        [HideInInspector, SerializeField, Min(0f), Tooltip("角色单次表现更新移动超过此距离时清空接触锚点，单位米；0 关闭自动检测。")]
        protected float ikTeleportDistance = 2f;

        internal CharacterAnimationConfiguration LegacyConfiguration(CharacterModelBindings model, CharacterProfileOverrides overrides,
            AnimationModuleAssembly modules)
        {
            var config = new CharacterAnimationConfiguration
            {
                PlaybackLibrary = playbackLibrary ? playbackLibrary : motionDatabase,
                FullLayer = layerIndex,
                LowerLayer = lowerBodyLayerIndex,
                OverlayLayer = overlayLayerIndex,
                UpperLayer = upperBodyLayerIndex,
                IkTeleportDistance = ikTeleportDistance
            };
            if (!modules?.Gait)
            {
                config.IdleState = idleStateName;
                config.MoveState = moveStateName;
                config.WalkState = walkStateName;
                config.SprintState = sprintStateName;
                config.DashState = dashStateName;
                config.LocomotionPriority = locomotionPriority;
                config.DashPriority = dashPriority;
                config.Transition = behaviorTransitionDuration;
                config.DashTransition = dashTransitionDuration;
                config.IdleEnterSpeed = idleEnterSpeed;
                config.IdleExitSpeed = idleExitSpeed;
                config.RunEnterSpeed = runEnterSpeed;
                config.RunExitSpeed = runExitSpeed;
                config.SprintEnterSpeed = sprintEnterSpeed;
                config.SprintExitSpeed = sprintExitSpeed;
                config.WalkSpeed = walkAnimationSpeed;
                config.RunSpeed = runAnimationSpeed;
                config.SprintSpeed = sprintAnimationSpeed;
                config.DashClipSpeed = dashAnimationSpeed;
                config.LocomotionGaitPhases = locomotionGaitPhases;
            }
            if (!modules?.StartTurn)
            {
                config.LocomotionReverseAngle = locomotionReverseAngle;
                config.WalkStartNormalizedTime = walkStartNormalizedTime;
                config.RunStartNormalizedTime = runStartNormalizedTime;
                config.WalkToRunBlendSeconds = walkToRunBlendSeconds;
            }
            if (!modules?.Stop)
            {
                config.MinimumStopTravel = minimumStopTravel;
            }
            if (!modules?.Match)
            {
                config.MatchTurnAngle = matchTurnAngle;
                config.MatchMargin = matchSwitchMargin;
                config.MatchReverseHold = matchReverseHold;
            }
            if (modules == null)
            {
                config.MatchDatabase = motionDatabase;
                config.LocomotionStartSeconds = locomotionStartSeconds;
                config.LocomotionTurnSeconds = locomotionTurnSeconds;
                config.WalkStartState = walkStartStateName;
                config.RunStartState = runStartStateName;
                config.RunStartClip = runStartClip;
                config.TurnLeftState = turnLeftStateName;
                config.TurnRightState = turnRightStateName;
                config.WalkStop = walkStop;
                config.RunStop = runStop;
                config.SprintStop = sprintStop;
                config.Actions = actions;
                config.PoseInertia = poseInertia;
                config.InertiaBones = poseInertia?.Bones ?? default;
                config.IkChannels = Array.ConvertAll(ikChannels ?? Array.Empty<CharacterIkChannel>(), c => c?.BindModel() ?? default);
            }
            else modules.Apply(config, model, overrides);
            return config;
        }
    }
}
