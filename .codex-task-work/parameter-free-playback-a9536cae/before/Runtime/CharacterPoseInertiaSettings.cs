using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    /// <summary>骨骼来自当前 Animator 的骨架。留空的部位跳过，原有角色默认旁路。</summary>
    [Serializable]
    public sealed class CharacterPoseInertiaSettings
    {
        [InspectorName("惯性强度"), Range(0f, 1f)] public float Strength;
        [InspectorName("脊柱")] public Transform Spine;
        [InspectorName("胸部")] public Transform Chest;
        [InspectorName("左上臂")] public Transform LeftUpperArm;
        [InspectorName("左前臂")] public Transform LeftForearm;
        [InspectorName("右上臂")] public Transform RightUpperArm;
        [InspectorName("右前臂")] public Transform RightForearm;
        [InspectorName("胸部响应时间（秒）"), Min(0.01f)] public float ChestResponseSeconds = 0.18f;
        [InspectorName("手臂响应时间（秒）"), Min(0.01f)] public float ArmResponseSeconds = 0.24f;
        [InspectorName("胸部最大偏角（度）"), Range(0f, 60f)] public float ChestMaxDegrees = 12f;
        [InspectorName("手臂最大偏角（度）"), Range(0f, 90f)] public float ArmMaxDegrees = 28f;
        [InspectorName("单位减速度的倾角（度）"), Min(0f)] public float BrakeDegreesPerAcceleration = 1.2f;
        [InspectorName("收步摆臂峰值（度）"), Range(0f, 90f)] public float StopArmDegrees = 32f;
        [InspectorName("收步满幅入场速度（米/秒）"), Min(0.1f)] public float StopFullSpeed = 6.5f;

        internal CharacterPoseBoneBinding[] Bind(Animator animator)
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

            void Add(Transform bone, CharacterPoseRegion region, float share)
            {
                assigned.Add(bone);
                result.Add(new CharacterPoseBoneBinding
                    { Handle = animator.BindStreamTransform(bone), Region = region, Share = share });
            }
        }
    }

    internal enum CharacterPoseRegion { Chest, LeftArm, RightArm }

    internal struct CharacterPoseBoneBinding
    {
        public TransformStreamHandle Handle;
        public CharacterPoseRegion Region;
        public float Share;
    }
}
