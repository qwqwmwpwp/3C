using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 携带一次原始双脚、骨盆及查询历史样本的值快照。
    internal struct CharacterFootObservation
    {
        private const float PlantedContact = 0.7f, UnloadedContact = 0.45f, MinimumSupportEvidence = 0.6f;
        public CharacterFootPose Pose, Past;
        public float PastSeconds;
        public double Time;
        public int Cursor, Samples;
        public byte Count;
        public Vector2 ContactConfidence; // 每只脚有接触数据的姿势占比，零表示未知。
        // 只有连续且有限的脚姿和接触观测才能进入特征比较。
        internal bool ReadyForMatch => Count >= 2 && Finite(Pose.Left.sqrMagnitude) && Finite(Pose.Right.sqrMagnitude) &&
            Finite(Pose.LeftVelocity.sqrMagnitude) && Finite(Pose.RightVelocity.sqrMagnitude) &&
            Finite(Pose.Contact.sqrMagnitude) && Finite(ContactConfidence.sqrMagnitude);

        // 排除动画流写出的无效坐标和速度。
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

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

        public float ContactCost(Vector2 candidate)
        {
            var error = candidate - Pose.Contact;
            return error.x * error.x * ContactConfidence.x + error.y * error.y * ContactConfidence.y;
        }
    }

    // 采样动画流中的脚部姿势，并附上片段播放提供的接触值。
    internal struct CharacterFootSampler
    {
        public NativeArray<CharacterFootObservation> History;
        public NativeArray<CharacterFootObservation> Ring;
        public double SampleTime;
        public const string LeftCurve = "IKLeftFoot";
        public const string RightCurve = "IKRightFoot";
        public const string LeftContactCurve = "ContactLeftFoot";
        public const string RightContactCurve = "ContactRightFoot";
        private TransformStreamHandle hips, left, right;
        private TransformSceneHandle basis;
        public Vector2 ClipContacts;
        public Vector2 ClipConfidence;

        // 将一次查询所需的当前和过去姿态复制到值快照，规划线程无需访问原生缓冲。
        internal static CharacterFootObservation Snapshot(CharacterFootObservation latest,
            NativeArray<CharacterFootObservation> ring, float age)
        {
            if (age <= 0f || latest.Count < 2) return latest;
            double target = latest.Time - age;
            var newer = latest;
            for (int i = 1; i < latest.Samples; i++)
            {
                var older = ring[(latest.Cursor - i + ring.Length) % ring.Length];
                if (older.Time <= target)
                {
                    float fraction = (float)((target - older.Time) / (newer.Time - older.Time));
                    latest.Past = CharacterFootPose.Interpolate(older.Pose, newer.Pose, fraction);
                    latest.PastSeconds = age;
                    break;
                }
                newer = older;
            }
            return latest;
        }

        // 绑定相对髋部采集双脚位置所需的骨骼和模型朝向。
        public static bool TryBind(Animator animator, out CharacterFootSampler sampler)
        {
            sampler = default;
            if (!animator.isHuman) return false;
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var left = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var right = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (!hips || !left || !right) return false;
            sampler.hips = animator.BindStreamTransform(hips);
            sampler.left = animator.BindStreamTransform(left);
            sampler.right = animator.BindStreamTransform(right);
            sampler.basis = animator.BindSceneTransform(animator.transform);
            return true;
        }

        // 发布本次脚部位置、速度及片段接触值，交给下一次更新使用。
        public void Capture(AnimationStream stream)
        {
            if (History.Length == 0) return;
            if (!left.IsValid(stream) || !right.IsValid(stream) || !hips.IsValid(stream) || !basis.IsValid(stream))
            {
                History[0] = default;
                return;
            }
            var before = History[0];
            float dt = (float)(SampleTime - before.Time);
            if (before.Count != 0 && dt == 0f) return;
            if (dt <= 0f || dt > 0.3f) before = default;
            Quaternion inverse = Quaternion.Inverse(basis.GetRotation(stream));
            Vector3 origin = hips.GetPosition(stream);
            var sample = new CharacterFootPose
            {
                HasPelvis = 1, Pelvis = inverse * (origin - basis.GetPosition(stream)),
                Left = inverse * (left.GetPosition(stream) - origin),
                Right = inverse * (right.GetPosition(stream) - origin),
                Contact = ClipContacts
            };
            if (float.IsNaN(sample.Pelvis.sqrMagnitude) || float.IsInfinity(sample.Pelvis.sqrMagnitude)) sample.HasPelvis = 0;
            if (before.Count > 0)
            {
                sample.LeftVelocity = (sample.Left - before.Pose.Left) / dt;
                sample.RightVelocity = (sample.Right - before.Pose.Right) / dt;
            }
            var current = new CharacterFootObservation { Count = (byte)Mathf.Min(2, before.Count + 1),
                Time = SampleTime, Cursor = (before.Cursor + 1) % Ring.Length, Samples = Mathf.Min(Ring.Length, before.Samples + 1),
                Pose = sample, ContactConfidence = ClipConfidence };
            Ring[current.Cursor] = current;
            History[0] = current;
        }
    }
}
