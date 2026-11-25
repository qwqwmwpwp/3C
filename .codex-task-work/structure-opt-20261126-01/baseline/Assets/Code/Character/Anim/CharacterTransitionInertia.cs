using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 按人形部位选择参加切换平滑的局部骨骼。
    [Flags]
    internal enum TransitionRegions { Body = 1, LeftArm = 2, RightArm = 4, LeftLeg = 8, RightLeg = 16, All = 31 }

    // 保存单骨的输入和已输出姿态，以及切换发生后尚待衰减的差分。
    internal struct CharacterTransitionBone
    {
        internal TransformStreamHandle Handle;
        internal TransitionRegions Region;
        internal AvatarMaskBodyPart Part;
        internal HumanBodyBones Name;
        internal byte Ready;
        internal int Epoch;
        internal Quaternion Input, Output;
        internal Vector3 InputPosition, OutputPosition;
        internal TransitionDecay Rotation, Position;
    }

    // 各自保存局部旋转或局部平移的真实输出速度、上次切换残差及其独立衰减配置。
    internal struct TransitionDecay
    {
        internal Vector3 OutputSpeed;
        private Vector3 offset, velocity, outgoingSpeed;
        private float seconds, limit, suppression, strength;
        private byte awaitIncoming;

        // 在切换帧接住已显示输出，在首对连续新输入上求速度差，之后向零衰减。
        internal Vector3 Advance(bool edge, in TransitionPart part, bool position, Vector3 difference, Vector3 inputSpeed,
            float constraint, float dt)
        {
            if (edge)
            {
                seconds = Mathf.Max(0.02f, part.Settings.Seconds);
                limit = position ? part.Settings.Position : part.Settings.Degrees;
                suppression = Mathf.Clamp01(part.Settings.Constraint);
                strength = part.Weight;
                offset = difference * strength;
                velocity = Vector3.zero;
                outgoingSpeed = OutputSpeed;
                awaitIncoming = 1;
            }
            else if (seconds > 0f)
            {
                if (awaitIncoming != 0)
                {
                    velocity = Vector3.ClampMagnitude(outgoingSpeed - inputSpeed, limit / seconds * 3f) * strength;
                    awaitIncoming = 0;
                }
                CharacterPoseInertia.Spring(ref offset, ref velocity, Vector3.zero, seconds, dt);
            }
            offset = Vector3.ClampMagnitude(offset, limit * (1f - Mathf.Clamp01(constraint) * suppression));
            return offset;
        }
    }

    // 独立记录本次合成姿势中实际可见的部位切换，包含局部层退出。
    internal struct CharacterTransitionSignal
    {
        internal TransitionSources Body, Head, LeftArm, RightArm, LeftLeg, RightLeg;
        internal bool Visible => Body.Visible || Head.Visible || LeftArm.Visible || RightArm.Visible || LeftLeg.Visible || RightLeg.Visible;
    }

    // 在合成片段之后、程序性修正之前，对实际切换的局部骨骼应用有界的临界阻尼。
    internal struct CharacterTransitionInertia
    {
        internal NativeArray<CharacterTransitionBone> Bones;
        internal int Epoch;
        private CharacterTransitionSignal edge;

        // 建立独立于胸臂减速逻辑的人形骨骼集合；骨盆整体移动与偏航留给视觉根。
        internal static CharacterTransitionBone[] Bind(Animator animator, TransitionInertiaModule tuning,
            HashSet<Transform> bound)
        {
            if (!tuning || !animator.isHuman) return Array.Empty<CharacterTransitionBone>();
            var bones = new List<CharacterTransitionBone>();
            Add(TransitionRegions.Body, HumanBodyBones.Spine, HumanBodyBones.Chest,
                HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head);
            Add(TransitionRegions.LeftArm, HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm,
                HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
            Add(TransitionRegions.RightArm, HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm,
                HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);
            Add(TransitionRegions.LeftLeg, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
            Add(TransitionRegions.RightLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);
            // 按策略显式要求可补充默认精简集合之外的人形骨，骨盆仍留给视觉根。
            if (tuning.Profiles != null)
                foreach (var profile in tuning.Profiles)
                    if (profile?.Bones != null)
                        foreach (var name in profile.Bones)
                        {
                            if ((int)name < 0 || name >= HumanBodyBones.LastBone || name == HumanBodyBones.Hips) continue;
                            var target = animator.GetBoneTransform(name);
                            if (!target || bound.Contains(target)) continue;
                            var region = RegionOf(animator, target);
                            Add(region, name);
                        }
            return bones.ToArray();

            // 为启用区域保存独有句柄，并标记共享骨上的旧交接已被替代。
            void Add(TransitionRegions region, params HumanBodyBones[] names)
            {
                if ((region & tuning.Regions) == 0) return;
                foreach (var name in names)
                {
                    Transform bone = animator.GetBoneTransform(name);
                    if (!bone || !bound.Add(bone)) continue;
                    var handle = animator.BindStreamTransform(bone);
                    bones.Add(new CharacterTransitionBone { Handle = handle, Region = region, Name = name,
                        Part = name == HumanBodyBones.Head || name == HumanBodyBones.Neck ? AvatarMaskBodyPart.Head : AvatarMaskBodyPart.Body });
                }
            }
        }

        // 显式增加的骨与其实际人形肢体共享调校，例如脚趾与手指。
        private static TransitionRegions RegionOf(Animator animator, Transform target)
        {
            if (Under(HumanBodyBones.LeftUpperArm)) return TransitionRegions.LeftArm;
            if (Under(HumanBodyBones.RightUpperArm)) return TransitionRegions.RightArm;
            if (Under(HumanBodyBones.LeftUpperLeg)) return TransitionRegions.LeftLeg;
            if (Under(HumanBodyBones.RightUpperLeg)) return TransitionRegions.RightLeg;
            return TransitionRegions.Body;

            // 根据实际 Avatar 层级识别一条肢体链上的骨。
            bool Under(HumanBodyBones id)
            {
                var root = animator.GetBoneTransform(id);
                return root && target.IsChildOf(root);
            }
        }

        // 仅在新声部进入或可见旧层短时退出时推进代次，保持帧不触发。
        internal void Prepare(in CharacterTransitionSignal signal)
        {
            if (Bones.Length == 0 || !signal.Visible) return;
            Epoch++;
            edge = signal;
        }

        // 用上一输出和新素材的局部姿态与速度差求余势，承重腿和握持臂按当帧约束减弱。
        internal void Apply(AnimationStream stream, Vector2 support, float leftGrip, float rightGrip)
        {
            float dt = stream.deltaTime;
            if (dt <= 0f) return;
            for (int i = 0; i < Bones.Length; i++)
            {
                var bone = Bones[i];
                if (!bone.Handle.IsValid(stream)) { bone.Ready = 0; Bones[i] = bone; continue; }
                var part = bone.Region switch {
                    TransitionRegions.LeftArm => edge.LeftArm, TransitionRegions.RightArm => edge.RightArm,
                    TransitionRegions.LeftLeg => edge.LeftLeg, TransitionRegions.RightLeg => edge.RightLeg,
                    _ => bone.Part == AvatarMaskBodyPart.Head ? edge.Head : edge.Body };
                float constraint = bone.Region switch {
                    TransitionRegions.LeftArm => leftGrip, TransitionRegions.RightArm => rightGrip,
                    TransitionRegions.LeftLeg => support.x, TransitionRegions.RightLeg => support.y,
                    _ => Mathf.Max(leftGrip, rightGrip) };
                Sample(stream, ref bone, dt, Epoch, part, constraint);
                Bones[i] = bone;
            }
        }

        // 用切换后第一对连续输入估计新片段速度，避免把入点跳跃当成骨骼速度。
        private static void Sample(AnimationStream stream, ref CharacterTransitionBone bone, float dt, int epoch,
            in TransitionSources sources, float constraint)
        {
            Quaternion input = bone.Handle.GetLocalRotation(stream);
            Vector3 position = bone.Handle.GetLocalPosition(stream);
            if (bone.Ready == 0)
            {
                bone.Input = bone.Output = input;
                bone.InputPosition = bone.OutputPosition = position;
                bone.Epoch = epoch;
                bone.Rotation = bone.Position = default;
                bone.Ready = 1;
                return;
            }

            TransitionPart rotation = default, translation = default;
            if (bone.Epoch != epoch)
            {
                ulong bit = 1UL << (int)bone.Name;
                rotation = sources.ForBone(bit, false);
                translation = sources.ForBone(bit, true);
            }
            bool rotate = rotation.Weight > 0f, move = translation.Weight > 0f;
            Vector3 angle = bone.Rotation.Advance(rotate, rotation, false, CharacterPoseInertia.Difference(bone.Output, input),
                CharacterPoseInertia.Difference(input, bone.Input) / dt, constraint, dt);
            Vector3 shift = bone.Position.Advance(move, translation, true, bone.OutputPosition - position,
                (position - bone.InputPosition) / dt, constraint, dt);
            Quaternion output = CharacterPoseInertia.Rotation(angle) * input;
            Vector3 outputPosition = position + shift;
            if (!rotate) bone.Rotation.OutputSpeed = CharacterPoseInertia.Difference(output, bone.Output) / dt;
            if (!move) bone.Position.OutputSpeed = (outputPosition - bone.OutputPosition) / dt;
            if (angle.sqrMagnitude > 0f) bone.Handle.SetLocalRotation(stream, output);
            if (shift.sqrMagnitude > 0f) bone.Handle.SetLocalPosition(stream, outputPosition);
            bone.Output = output;
            bone.Input = input;
            bone.OutputPosition = outputPosition;
            bone.InputPosition = position;
            bone.Epoch = epoch;
        }

        // 清空上一连续段的速度与偏移，保留已经绑定的骨骼和部位配置。
        internal static void Reset(NativeArray<CharacterTransitionBone> bones)
        {
            if (!bones.IsCreated) return;
            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                bone.Ready = 0;
                bones[i] = bone;
            }
        }
    }
}
