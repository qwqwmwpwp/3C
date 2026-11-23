using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [Serializable]
    internal struct GaitSpeedThresholds
    {
        [InspectorName("升档（米/秒）"), Min(0f)] public float Up;
        [InspectorName("降档（米/秒）"), Min(0f)] public float Down;

        public GaitSpeedThresholds(float up, float down)
        {
            Up = up;
            Down = down;
        }
    }

    [Serializable]
    internal sealed class CharacterGaitSettings
    {
        [InspectorName("待机 ↔ 行走")] public GaitSpeedThresholds Idle = new GaitSpeedThresholds(0.15f, 0.08f);
        [InspectorName("行走 ↔ 跑步")] public GaitSpeedThresholds Run = new GaitSpeedThresholds(3.5f, 3f);
        [InspectorName("跑步 ↔ 持续冲刺")] public GaitSpeedThresholds Sprint = new GaitSpeedThresholds(6f, 5.6f);

        [SerializeField, InspectorName("起步阶段（秒）"), Min(0f), Tooltip("从停止进入移动时保留起步阶段的时长，单位秒；0 表示禁用。")]
        internal float locomotionStartSeconds = 0.22f;
        [InspectorName("反向转身阶段（秒）"), SerializeField, Min(0f), Tooltip("反向转身阶段的时长，单位秒；0 表示禁用。")]
        internal float locomotionTurnSeconds = 0.32f;
        [InspectorName("收步最短移动距离（米）"), SerializeField, Min(0f), Tooltip("连续主动地面位移至少达到此距离才收步，单位米。")]
        internal float minimumStopTravel = 0.09f;
        [InspectorName("反向转身夹角（度）"), SerializeField, Range(90f, 180f), Tooltip("触发反向转身所需的移动方向夹角，单位度。")]
        internal float locomotionReverseAngle = 120f;
        [SerializeField, InspectorName("匹配模式")] internal CharacterMatchMode motionMatching;
        [InspectorName("续播代价宽限"), SerializeField, Min(0f), Tooltip("续播相对新入点的代价宽限，越大越不易换片。")]
        internal float matchSwitchMargin = 0.12f;
        [InspectorName("反向续播上限（秒）"), SerializeField, Range(0f, 0.15f), Tooltip("反向输入后允许原动作继续的最长表现时间（秒）。")]
        internal float matchReverseHold = 0.08f;
        [InspectorName("改向选姿夹角（度）"), SerializeField, Range(15f, 180f), Tooltip("持续移动中触发选姿的输入与速度改向角（度）。")]
        internal float matchTurnAngle = 55f;
    }

    [Serializable]
    internal sealed class CharacterPresentationSettings
    {
        [Header("状态与优先级")]
        [InspectorName("旧控制器基础层索引"), SerializeField, Min(0), Tooltip("仅用于按控制器层号查找状态的旧播放模式。")]
        internal int layerIndex;
        [InspectorName("待机状态"), SerializeField, Tooltip("待机状态完整路径，包含层和子状态机名称。")]
        internal string idleStateName = "Base Layer.idle";
        [InspectorName("常跑状态"), SerializeField, Tooltip("移动状态完整路径，持续移动不重复从头播放。")]
        internal string moveStateName = "Base Layer.move";
        [InspectorName("步行状态"), SerializeField, Tooltip("低速行走状态；留空时沿用旧版移动状态，兼容已有控制器。")]
        internal string walkStateName;
        [InspectorName("持续冲刺状态"), SerializeField, Tooltip("持续冲刺循环的完整状态路径；缺少该状态时使用跑步。")]
        internal string sprintStateName = "Base Layer.sprint";
        [InspectorName("短突进状态"), SerializeField, Tooltip("短突进状态完整路径，每次新突进重新播放。")]
        internal string dashStateName = "Base Layer.dash";
        [InspectorName("移动优先级"), SerializeField, Tooltip("普通待机和移动的全身优先级；同优先级的局部需求不能覆盖全身。")]
        internal int locomotionPriority;
        [InspectorName("短突进优先级"), SerializeField, Tooltip("冲刺的全身优先级，可用于暂时压住较低优先级的局部动作。")]
        internal int dashPriority = 100;
        [Header("过渡与播放校准")]
        [SerializeField, InspectorName("普通过渡（归一化）"), Range(0f, 1f), Tooltip("普通行为动画的归一化过渡时长；相对源状态长度，建议 0.12。")]
        internal float behaviorTransitionDuration = 0.12f;
        [SerializeField, InspectorName("突进过渡（归一化）"), Range(0f, 1f), Tooltip("冲刺进入与退出的归一化过渡时长上限，建议短于普通移动。")]
        internal float dashTransitionDuration = 0.04f;
        [SerializeField, InspectorName("步行无轨道参考速度"), Min(0.01f), Tooltip("步行素材以 1 倍速播放时的前进速度（米/秒），供步行和步行起步换算步频。")]
        internal float walkAnimationSpeed = 1.55f;
        [SerializeField, InspectorName("跑步无轨道参考速度"), Min(0.01f), Tooltip("跑步素材以 1 倍速播放时的前进速度（米/秒），供跑步和跑步起步换算步频。")]
        internal float runAnimationSpeed = 4.98f;
        [SerializeField, InspectorName("持续冲刺无轨道参考速度"), Min(0.01f), Tooltip("持续冲刺素材的名义速度，用于按实际速度调整步频。")]
        internal float sprintAnimationSpeed = 6.52f;
        [SerializeField, InspectorName("短突进参考速度（0 为原速）"), Min(0f), Tooltip("短突进素材以 1 倍速播放时的前进速度（米/秒）。0 表示按素材原速播放。")]
        internal float dashAnimationSpeed;
        [Header("起步、转身与收步")]
        [SerializeField, InspectorName("步行收步")] internal CharacterStopAnimationSettings walkStop;
        [SerializeField, InspectorName("常跑收步")] internal CharacterStopAnimationSettings runStop;
        [SerializeField, InspectorName("持续冲刺收步")] internal CharacterStopAnimationSettings sprintStop;
        [InspectorName("步行起步状态"), SerializeField, Tooltip("行走起步的完整状态路径；留空或不可用时使用普通行走。")]
        internal string walkStartStateName;
        [InspectorName("步行起点（归一化）"), SerializeField, Range(0f, 1f), Tooltip("行走起步素材的归一化起点。")]
        internal float walkStartNormalizedTime;
        [InspectorName("跑步起步状态"), SerializeField, Tooltip("跑步起步的完整状态路径；留空或不可用时使用普通跑步。")]
        internal string runStartStateName;
        [InspectorName("跑步起步片段"), SerializeField, Tooltip("跑步起步状态使用的片段，用于把支撑脚相位换算为固定时间混合的入点。")]
        internal AnimationClip runStartClip;
        [InspectorName("走跑混合（秒）"), SerializeField, Min(0f), Tooltip("普通起跑从走姿展开到跑姿的混合时间（秒）。")]
        internal float walkToRunBlendSeconds = 0.14f;
        [InspectorName("跑步起点（归一化）"), SerializeField, Range(0f, 1f), Tooltip("跑步起步素材的归一化起点。")]
        internal float runStartNormalizedTime;
        [InspectorName("左转状态"), SerializeField, Tooltip("向左反向转身的完整状态路径；留空或不可用时使用当前基础移动状态。")]
        internal string turnLeftStateName;
        [InspectorName("右转状态"), SerializeField, Tooltip("向右反向转身的完整状态路径；留空或不可用时使用当前基础移动状态。")]
        internal string turnRightStateName;
        [InspectorName("步态触地相位"), SerializeField, Tooltip("基础层循环移动状态的左右脚触地相位。无有效映射时使用动作配置的播放起点。")]
        internal CharacterLocomotionGaitPhaseSettings[] locomotionGaitPhases;
        [Header("旧控制器层号（双层片段库按身体部位播放）")]
        [InspectorName("下身层索引"), SerializeField, Tooltip("带下身 AvatarMask 的 Override 层索引；-1 表示未配置。不可与全身层、上身层相同。")]
        internal int lowerBodyLayerIndex = 1;
        [SerializeField, InspectorName("旧控制器全身叠加层"), Tooltip("位于基础层之上、局部遮罩层之下；-1 表示未配置。")]
        internal int overlayLayerIndex = -1;
        [InspectorName("上身层索引"), SerializeField, Tooltip("带上身 AvatarMask 的 Override 层索引；-1 表示未配置。不可与全身层、下身层相同。")]
        internal int upperBodyLayerIndex = 2;
        [InspectorName("局部与全身动作映射"), SerializeField, Tooltip("外部仅提交语义键和实例编号；动画路径、部位、优先级和起始进度均在此映射。")]
        internal CharacterAnimationActionSettings[] actions = CharacterAnimationActionSettings.CreateDefaults();
        [InspectorName("行为表现"), SerializeField] internal CharacterBehaviorAnimationMapping[] behaviorAnimations;
        [InspectorName("瞬移重置距离（米）"), SerializeField, Min(0f), Tooltip("角色单次表现更新移动超过此距离时清空接触锚点，单位米；0 关闭自动检测。")]
        internal float ikTeleportDistance = 2f;

        [InspectorName("旧持物上身权重"), Range(0f, 1f), Tooltip("配置了持物行为表现时，使用映射行内的权重。")]
        public float GripWeight = 0.4f;
        [InspectorName("分区惯性")] public CharacterInertiaTuning Inertia = new CharacterInertiaTuning();
        [InspectorName("手脚 IK（按顺序求解）")] public CharacterIkTuning[] IkChannels;
    }
}
