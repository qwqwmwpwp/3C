using System;
using System.Collections.Generic;
using UnityEngine.Animations;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存肢体 IK 的素材许可、探测范围和目标权重。
    [Serializable]
    public class CharacterIkTuning
    {
        // 固定当前角色安装此通道时的求解与许可参数。
        internal CharacterIkTuning Snapshot() => (CharacterIkTuning)MemberwiseClone();

        [InspectorName("部位")] public CharacterIkLimb Limb;
        [InspectorName("许可曲线"), Tooltip("素材许可曲线名称。零参数播放从烘焙素材曲线读取，缺失时权重为零。")]
        public string WeightCurve;
        [InspectorName("默认使用动画目标")] public bool UseAnimationTarget;
        [InspectorName("握持动作键"), Tooltip("全身或上身实际选中此动作时，将动画手目标作为握持约束。")]
        public string GripActionKey;
        [InspectorName("默认探测地面")] public bool ProbeGround;
        [InspectorName("贴合最大坡角（度）"), Range(-1f, 89f), Tooltip("非负值按角色上方向测量。-1 按世界上方向测量，使用角色移动配置的可行走坡角，独立宿主使用 45°。")]
        public float ProbeSlopeLimit = 84.26f;
        [InspectorName("探测起点"), Tooltip("角色局部空间，单位米。脚姿有效后使用脚踝水平位置，保留此高度。")] public Vector3 ProbeOrigin;
        [InspectorName("探测长度"), Min(0f)] public float ProbeLength = 1f;
        [InspectorName("地面层")] public LayerMask GroundLayers = Physics.DefaultRaycastLayers;
        [InspectorName("脚踝离地高度"), Min(0f)] public float SoleHeight = 0.08f;
        [InspectorName("位置权重"), Range(0f, 1f)] public float PositionWeight = 1f;
        [InspectorName("旋转权重"), Range(0f, 1f)] public float RotationWeight = 1f;
        [InspectorName("目标切换秒数"), Min(0f)] public float BlendSeconds = 0.12f;
    }

    // 定义胸臂在制动和收步时的惯性响应。
    [Serializable]
    public class CharacterInertiaTuning
    {
        // 固定本角色安装时的胸臂惯性参数。
        internal CharacterInertiaTuning Snapshot() => (CharacterInertiaTuning)MemberwiseClone();

        [InspectorName("惯性强度"), Range(0f, 1f)] public float Strength;
        [InspectorName("胸部响应时间（秒）"), Min(0.01f)] public float ChestResponseSeconds = 0.18f;
        [InspectorName("手臂响应时间（秒）"), Min(0.01f)] public float ArmResponseSeconds = 0.24f;
        [InspectorName("胸部最大偏角（度）"), Range(0f, 60f)] public float ChestMaxDegrees = 12f;
        [InspectorName("手臂最大偏角（度）"), Range(0f, 90f)] public float ArmMaxDegrees = 28f;
        [InspectorName("单位减速度的倾角（度）"), Min(0f)] public float BrakeDegreesPerAcceleration = 1.2f;
        [InspectorName("收步摆臂峰值（度）"), Range(0f, 90f)] public float StopArmDegrees = 32f;
        [InspectorName("收步满幅入场速度（米/秒）"), Min(0.1f)] public float StopFullSpeed = 6.5f;
    }

    [Serializable]
    internal struct CharacterInertiaBones
    {
        [InspectorName("脊柱")] public Transform Spine;
        [InspectorName("胸部")] public Transform Chest;
        [InspectorName("左上臂")] public Transform LeftUpperArm;
        [InspectorName("左前臂")] public Transform LeftForearm;
        [InspectorName("右上臂")] public Transform RightUpperArm;
        [InspectorName("右前臂")] public Transform RightForearm;

        // 绑定胸臂骨骼并在实际重合的骨上由独立切换模块接管片段交接。
        internal CharacterPoseBoneBinding[] Bind(Animator animator, HashSet<Transform> transitionBones)
        {
            var result = new List<CharacterPoseBoneBinding>(6);
            var assigned = new HashSet<Transform>();
            AddPair(Spine, Chest, CharacterPoseRegion.Chest, 0.35f);
            AddPair(LeftUpperArm, LeftForearm, CharacterPoseRegion.LeftArm, 0.7f);
            AddPair(RightUpperArm, RightForearm, CharacterPoseRegion.RightArm, 0.7f);
            return result.ToArray();

            void AddPair(Transform first, Transform second, CharacterPoseRegion region, float firstShare)
            {
                bool hasFirst = Valid(first);
                bool hasSecond = Valid(second) && (!hasFirst || first != second && second.IsChildOf(first));
                if (hasFirst) Add(first, region, hasSecond ? firstShare : 1f);
                if (hasSecond) Add(second, region, hasFirst ? 1f - firstShare : 1f);
            }

            bool Valid(Transform bone) => bone && bone != animator.transform &&
                bone.IsChildOf(animator.transform) && !assigned.Contains(bone);

            // 将胸臂份额写入作业绑定，按实际骨骼重合标记通用切换惯性的所有权。
            void Add(Transform bone, CharacterPoseRegion region, float share)
            {
                assigned.Add(bone);
                result.Add(new CharacterPoseBoneBinding
                    { Handle = animator.BindStreamTransform(bone), Region = region, Share = share, SharedTransition = (byte)(transitionBones.Contains(bone) ? 1 : 0) });
            }
        }
    }

    [Serializable]
    internal struct CharacterIkBones
    {
        [InspectorName("部位")] public CharacterIkLimb Limb;
        [InspectorName("上段骨骼")] public Transform Root;
        [InspectorName("中段骨骼")] public Transform Mid;
        [InspectorName("末端骨骼")] public Transform Tip;
        [InspectorName("动画目标骨"), Tooltip("绑定在同一动画器下、独立于被求解骨链的目标骨。")]
        public Transform AnimationTarget;
        [InspectorName("默认场景目标")] public Transform SceneTarget;
    }

    // 保存通道来源、安装时的参数和当前模型骨链。
    internal readonly struct CharacterBoundIkChannel
    {
        public readonly CharacterIkTuning Source, Tuning;
        public readonly CharacterIkBones Bones;

        // 用原定义标识可接管的请求，求解使用独立参数快照。
        public CharacterBoundIkChannel(CharacterIkTuning tuning, CharacterIkBones bones)
        {
            Source = tuning;
            Tuning = tuning?.Snapshot();
            Bones = bones;
        }
    }

    [Serializable]
    internal sealed class CharacterModelBindings
    {
        [InspectorName("惯性骨骼")] public CharacterInertiaBones Inertia;
        [InspectorName("手脚骨链")] public CharacterIkBones[] Limbs;

        internal CharacterBoundIkChannel[] BindChannels(CharacterIkTuning[] settings)
        {
            if (settings == null) return Array.Empty<CharacterBoundIkChannel>();
            var channels = new CharacterBoundIkChannel[settings.Length];
            for (int i = 0; i < settings.Length; i++)
            {
                var tuning = settings[i];
                if (tuning != null)
                    channels[i] = new CharacterBoundIkChannel(tuning, FindBones(tuning.Limb));
            }
            return channels;
        }

        private CharacterIkBones FindBones(CharacterIkLimb limb)
        {
            if (Limbs != null)
                foreach (var bones in Limbs)
                    if (bones.Limb == limb) return bones;
            return default;
        }
    }
}
