using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 数组中的唯一元素由动画作业发布，下一次 Update 在提交播放命令之前读取。
    internal struct CharacterFootObservation
    {
        private const float PlantedContact = 0.7f, UnloadedContact = 0.45f, MinimumSupportEvidence = 0.6f;
        public CharacterFootPose Pose;
        public byte Count;
        public Vector2 ContactConfidence; // 每只脚有接触数据的姿势占比，零表示未知。
        public GroundTrackSide Support
        {
            get
            {
                var lower = Vector2.Scale(Pose.Contact, ContactConfidence);
                var upper = lower + (Vector2.one - ContactConfidence); // 未覆盖部分仍可能承担重量。
                if (Pose.Contact.x >= PlantedContact && lower.x >= MinimumSupportEvidence && upper.y < UnloadedContact)
                    return GroundTrackSide.Left;
                if (Pose.Contact.y >= PlantedContact && lower.y >= MinimumSupportEvidence && upper.x < UnloadedContact)
                    return GroundTrackSide.Right;
                return GroundTrackSide.Either;
            }
        }

        public static GroundTrackSide SupportOf(Vector2 contact)
        {
            if (contact.x >= PlantedContact && contact.y < UnloadedContact) return GroundTrackSide.Left;
            if (contact.y >= PlantedContact && contact.x < UnloadedContact) return GroundTrackSide.Right;
            return GroundTrackSide.Either;
        }

        public float SupportWeight(CharacterIkLimb foot, float fallback)
        {
            bool left = foot == CharacterIkLimb.LeftFoot;
            float contact = left ? Pose.Contact.x : Pose.Contact.y;
            float confidence = left ? ContactConfidence.x : ContactConfidence.y;
            return Mathf.InverseLerp(UnloadedContact, PlantedContact, Mathf.Lerp(fallback, contact, confidence));
        }

#if UNITY_EDITOR
        public string DescribeContacts()
        {
            return $"左 / 右脚接触 {DescribeContact(Pose.Contact.x, ContactConfidence.x)} / " +
                DescribeContact(Pose.Contact.y, ContactConfidence.y);
        }

        private static string DescribeContact(float contact, float confidence) =>
            confidence > 0f ? $"{contact:0.00}（置信度 {confidence:0%}）" : "未知";
#endif

        public float ContactCost(Vector2 candidate)
        {
            var error = candidate - Pose.Contact;
            return error.x * error.x * ContactConfidence.x + error.y * error.y * ContactConfidence.y;
        }
    }

    internal struct CharacterFootSampler
    {
        public NativeArray<CharacterFootObservation> History;
        public const string LeftCurve = "IKLeftFoot";
        public const string RightCurve = "IKRightFoot";
        public const string LeftContactCurve = "ContactLeftFoot";
        public const string RightContactCurve = "ContactRightFoot";
        private TransformStreamHandle hips, left, right;
        private TransformSceneHandle basis;
        private PropertyStreamHandle leftCurve, rightCurve, leftContactCurve, rightContactCurve;
        private byte direct;
        public Vector2 ClipContacts;
        public Vector2 ClipConfidence;

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
                sampler.leftCurve = BindCurve(animator, parameters, LeftCurve);
                sampler.rightCurve = BindCurve(animator, parameters, RightCurve);
                sampler.leftContactCurve = BindCurve(animator, parameters, LeftContactCurve);
                sampler.rightContactCurve = BindCurve(animator, parameters, RightContactCurve);
            }
            sampler.direct = (byte)(clipDriven ? 1 : 0);
            sampler.hips = animator.BindStreamTransform(hips);
            sampler.left = animator.BindStreamTransform(left);
            sampler.right = animator.BindStreamTransform(right);
            sampler.basis = animator.BindSceneTransform(animator.transform);
            return true;
        }

        private static PropertyStreamHandle BindCurve(Animator animator, AnimatorControllerParameter[] parameters, string name)
        {
            foreach (var parameter in parameters)
                if (parameter.type == AnimatorControllerParameterType.Float && parameter.name == name)
                    return animator.BindStreamProperty(animator.transform, typeof(Animator), name);
            return default;
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
            var confidence = ClipConfidence;
            if (direct == 0)
            {
                confidence.x = ReadContact(leftContactCurve, leftCurve, stream, out contacts.x);
                confidence.y = ReadContact(rightContactCurve, rightCurve, stream, out contacts.y);
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
            History[0] = new CharacterFootObservation { Count = (byte)Mathf.Min(2, before.Count + 1),
                Pose = sample, ContactConfidence = confidence };
        }

        private static float ReadContact(PropertyStreamHandle contact, PropertyStreamHandle legacy, AnimationStream stream,
            out float value)
        {
            if (!contact.IsValid(stream) || !contact.GetReadMask(stream)) contact = legacy;
            bool readable = contact.IsValid(stream) && contact.GetReadMask(stream);
            value = readable ? Mathf.Clamp01(contact.GetFloat(stream)) : 0f;
            return readable ? 1f : 0f;
        }
    }
}
