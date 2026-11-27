using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    /// <summary>从原始片段姿态开始，按固定顺序应用各表现模块，最终由 IK 骨架执行计划。</summary>
    internal struct CharacterPoseJob : IAnimationJob
    {
        public CharacterIkSkeleton Skeleton;
        public CharacterIkCorrection Correction;
        [ReadOnly] public NativeArray<CharacterPoseBoneBinding> PoseBindings;
        public NativeArray<CharacterPoseBoneHistory> PoseHistory;
        public CharacterPoseInertiaFrame Inertia;
        public CharacterFootSampler Feet;
        public GroundFeatureSampler Features;
        public CharacterVisualRootBone VisualRoot;
        public CharacterTransitionInertia Transition;
        public CharacterOrientationBones Orientation;
        private byte frozen;
        public bool Frozen { get => frozen != 0; set => frozen = (byte)(value ? 1 : 0); }

        // 该流程仅修饰姿态，根运动由既有播放图保留。
        public void ProcessRootMotion(AnimationStream stream) { }

        // 原始姿态先入历史，再依次执行切换惯性、视觉根、剩余腿向、胸臂表现和最终 IK。
        public void ProcessAnimation(AnimationStream stream)
        {
            if (!Frozen && Feet.History.Length > 0)
            {
                var before = Feet.History[0];
                Feet.Capture(stream);
                Features.Capture(stream, before, Feet.History[0]);
            }
            var rawFeet = Feet.History.Length > 0 ? Feet.History[0] : default;
            if (!Frozen && Transition.Bones.Length > 0)
            {
                var support = Vector2.Scale(rawFeet.Pose.Contact, rawFeet.ContactConfidence);
                var leftGrip = Correction.ReadHand(stream, Skeleton, Correction.LeftHand).Grip;
                var rightGrip = Correction.ReadHand(stream, Skeleton, Correction.RightHand).Grip;
                Transition.Apply(stream, support, leftGrip, rightGrip);
            }
            var rootYaw = VisualRoot.Apply(stream, out var rootPivot, out var displacement);
            var legYaw = Orientation.Apply(stream, out var pivot);
            var warp = new CharacterFootWarp(rootYaw, legYaw, displacement);
            var left = default(CharacterGripIk.HandReference);
            var right = default(CharacterGripIk.HandReference);
            if (PoseBindings.Length > 0)
            {
                if (!Frozen && Inertia.Strength > 0f)
                {
                    left = Correction.ReadHand(stream, Skeleton, Correction.LeftHand);
                    right = Correction.ReadHand(stream, Skeleton, Correction.RightHand);
                }
                CharacterPoseInertia.Apply(stream, PoseBindings, PoseHistory, Inertia, left.Grip, right.Grip, Frozen);
            }
            Correction.BuildPlans(stream, Skeleton, warp, rootPivot, pivot, rawFeet, left, right, Frozen);
            Skeleton.Apply(stream);
        }
    }
}
