using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    [System.Flags]
    internal enum FootContactMask : byte { None = 0, Left = 1, Right = 2, Both = Left | Right }

    // 数组中的唯一元素由动画作业发布，下一次 Update 在提交播放命令之前读取。
    internal struct CharacterFootObservation
    {
        public CharacterFootPose Pose;
        public byte Count;
        public FootContactMask KnownContacts;
        public GroundTrackSide Support => KnownContacts == FootContactMask.Both ? SupportOf(Pose.Contact) : GroundTrackSide.Either;

        public static GroundTrackSide SupportOf(Vector2 contact)
        {
            if (contact.x >= 0.7f && contact.y < 0.45f) return GroundTrackSide.Left;
            if (contact.y >= 0.7f && contact.x < 0.45f) return GroundTrackSide.Right;
            return GroundTrackSide.Either;
        }

#if UNITY_EDITOR
        public string DescribeContacts()
        {
            string left = (KnownContacts & FootContactMask.Left) != 0 ? Pose.Contact.x.ToString("0.00") : "未知";
            string right = (KnownContacts & FootContactMask.Right) != 0 ? Pose.Contact.y.ToString("0.00") : "未知";
            return $"左 / 右脚接触 {left} / {right}";
        }
#endif

        public float ContactCost(Vector2 candidate)
        {
            var error = candidate - Pose.Contact;
            if ((KnownContacts & FootContactMask.Left) == 0) error.x = 0f;
            if ((KnownContacts & FootContactMask.Right) == 0) error.y = 0f;
            return error.sqrMagnitude;
        }
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
        public FootContactMask ClipContactMask;

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
                if (HasCurve(parameters, LeftCurve))
                    sampler.leftCurve = animator.BindStreamProperty(animator.transform, typeof(Animator), LeftCurve);
                if (HasCurve(parameters, RightCurve))
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
            if (!left.IsValid(stream) || !right.IsValid(stream) || !hips.IsValid(stream) || !basis.IsValid(stream))
            {
                History[0] = default;
                return;
            }
            float dt = stream.deltaTime;
            if (dt <= 0f) return;
            var before = History[0];
            Quaternion inverse = Quaternion.Inverse(basis.GetRotation(stream));
            Vector3 origin = hips.GetPosition(stream);
            var contacts = ClipContacts;
            var known = ClipContactMask;
            if (direct == 0)
            {
                known = FootContactMask.None;
                contacts.x = ReadContact(leftCurve, stream, FootContactMask.Left, ref known);
                contacts.y = ReadContact(rightCurve, stream, FootContactMask.Right, ref known);
            }
            var sample = new CharacterFootPose
            {
                Left = inverse * (left.GetPosition(stream) - origin),
                Right = inverse * (right.GetPosition(stream) - origin),
                Contact = contacts
            };
            if (before.Count > 0)
            {
                sample.LeftVelocity = (sample.Left - before.Pose.Left) / dt;
                sample.RightVelocity = (sample.Right - before.Pose.Right) / dt;
            }
            History[0] = new CharacterFootObservation { Count = (byte)Mathf.Min(2, before.Count + 1), Pose = sample, KnownContacts = known };
        }

        private static float ReadContact(PropertyStreamHandle curve, AnimationStream stream, FootContactMask foot,
            ref FootContactMask known)
        {
            if (!curve.IsValid(stream) || !curve.GetReadMask(stream)) return 0f;
            known |= foot;
            return Mathf.Clamp01(curve.GetFloat(stream));
        }
    }
}
