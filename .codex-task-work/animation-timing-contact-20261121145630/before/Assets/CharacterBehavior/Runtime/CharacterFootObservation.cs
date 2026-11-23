using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 数组中的唯一元素由动画作业发布，下一次 Update 在提交播放命令之前读取。
    internal struct CharacterFootObservation
    {
        public CharacterFootPose Pose;
        public byte Count;
    }

    internal struct CharacterFootSampler
    {
        public NativeArray<CharacterFootObservation> History;
        public const string LeftCurve = "IKLeftFoot";
        public const string RightCurve = "IKRightFoot";
        private TransformStreamHandle hips, left, right;
        private TransformSceneHandle basis;
        private PropertyStreamHandle leftCurve, rightCurve;
        private byte direct;
        public Vector2 ClipContacts;

        public static bool TryBind(Animator animator, bool clipDriven, AnimatorControllerParameter[] parameters,
            out CharacterFootSampler sampler)
        {
            sampler = default;
            if (!animator.isHuman) return false;
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var left = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var right = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (!hips || !left || !right) return false;
            if (!clipDriven)
            {
                if (!HasCurve(parameters, LeftCurve) || !HasCurve(parameters, RightCurve)) return false;
                sampler.leftCurve = animator.BindStreamProperty(animator.transform, typeof(Animator), LeftCurve);
                sampler.rightCurve = animator.BindStreamProperty(animator.transform, typeof(Animator), RightCurve);
            }
            sampler.direct = (byte)(clipDriven ? 1 : 0);
            sampler.hips = animator.BindStreamTransform(hips);
            sampler.left = animator.BindStreamTransform(left);
            sampler.right = animator.BindStreamTransform(right);
            sampler.basis = animator.BindSceneTransform(animator.transform);
            return true;
        }

        private static bool HasCurve(AnimatorControllerParameter[] parameters, string name)
        {
            foreach (var parameter in parameters)
                if (parameter.type == AnimatorControllerParameterType.Float && parameter.name == name) return true;
            return false;
        }

        public void Capture(AnimationStream stream)
        {
            if (History.Length == 0) return;
            if (!left.IsValid(stream) || !right.IsValid(stream) || !hips.IsValid(stream) || !basis.IsValid(stream) ||
                direct == 0 && (!leftCurve.IsValid(stream) || !rightCurve.IsValid(stream)))
            {
                History[0] = default;
                return;
            }
            float dt = stream.deltaTime;
            if (dt <= 0f) return;
            var before = History[0];
            Quaternion inverse = Quaternion.Inverse(basis.GetRotation(stream));
            Vector3 origin = hips.GetPosition(stream);
            var sample = new CharacterFootPose
            {
                Left = inverse * (left.GetPosition(stream) - origin),
                Right = inverse * (right.GetPosition(stream) - origin),
                Contact = direct != 0 ? ClipContacts :
                    new Vector2(Mathf.Clamp01(leftCurve.GetFloat(stream)), Mathf.Clamp01(rightCurve.GetFloat(stream)))
            };
            if (before.Count > 0)
            {
                sample.LeftVelocity = (sample.Left - before.Pose.Left) / dt;
                sample.RightVelocity = (sample.Right - before.Pose.Right) / dt;
            }
            History[0] = new CharacterFootObservation { Count = (byte)Mathf.Min(2, before.Count + 1), Pose = sample };
        }
    }
}
