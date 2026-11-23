using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.SceneManagement;

namespace ThreeC.CharacterBehavior
{
    internal struct CharacterFootProbeMotion
    {
        private byte hasSample;
        private Vector3 offset, velocity; // 角色根朝向下的踝位置（米）和速度（米/秒）。

        public void Observe(AnimationStream stream, TransformSceneHandle basis, Vector3 ankle)
        {
            if (!basis.IsValid(stream))
            {
                this = default;
                return;
            }
            Vector3 next = Quaternion.Inverse(basis.GetRotation(stream)) * (ankle - basis.GetPosition(stream));
            float dt = stream.deltaTime;
            velocity = hasSample != 0 && dt > 0.00001f ? (next - offset) / dt : Vector3.zero;
            offset = next;
            hasSample = 1;
        }

        public void Predict(Transform owner, Vector3 fallback, float expectedDt, out Vector3 center, out Vector3 step)
        {
            center = fallback;
            step = Vector3.zero;
            if (hasSample == 0) return;
            Vector3 up = owner.up;
            step = Vector3.ClampMagnitude(Vector3.ProjectOnPlane(owner.rotation * velocity, up) * Mathf.Clamp(expectedDt, 0f, 0.05f), 0.18f);
            center += Vector3.ProjectOnPlane(owner.position + owner.rotation * offset - fallback, up) + step;
        }
    }

    internal struct CharacterGroundFootHit
    {
        public Vector3 Origin, Point, Normal;
        public int Platform;
        public Matrix4x4 ToWorld, ToLocal;
        public Quaternion Rotation;
    }

    internal struct CharacterGroundFootProbes
    {
        private const float SupportDistance = 0.07f;
        private const float ExtraProbeThreshold = 0.015f;
        private const float TreadHeightTolerance = 0.025f;
        private const float TreadBoundaryTolerance = 0.015f;

        private CharacterGroundFootHit center, behind, ahead;
        public Vector3 Up;
        public float Length, SoleHeight;
        public int Count;

        public CharacterGroundFootHit Ray(int index) => index == 0 ? center : index == 1 ? behind : ahead;

        public void AcceptCenter(ref CharacterIkCandidate candidate) => SetTarget(ref candidate, center);

        public void Cast(PhysicsScene physics, CharacterIkTuning channel, Transform owner, Transform skeleton,
            float walkableSlope, in CharacterFootProbeMotion motion, float expectedDt, ref RaycastHit[] hits)
        {
            Up = owner.up;
            Length = channel.ProbeLength;
            SoleHeight = channel.SoleHeight;
            motion.Predict(owner, owner.TransformPoint(channel.ProbeOrigin), expectedDt, out var origin, out var step);
            bool inherit = channel.ProbeSlopeLimit < 0f;
            Vector3 supportUp = inherit ? Vector3.up : Up;
            float minDot = Mathf.Cos(Mathf.Clamp(inherit ? walkableSlope : channel.ProbeSlopeLimit, 0f, 89f) * Mathf.Deg2Rad);
            Count = 1;
            Vector3 up = owner.up;
            center = FindHit(physics, channel, owner, skeleton, ref hits, origin, up, supportUp, minDot);
            if (step.sqrMagnitude < ExtraProbeThreshold * ExtraProbeThreshold) return;
            Vector3 spacing = Vector3.ClampMagnitude(step, 0.12f);
            behind = FindHit(physics, channel, owner, skeleton, ref hits, origin - spacing, up, supportUp, minDot);
            ahead = FindHit(physics, channel, owner, skeleton, ref hits, origin + spacing, up, supportUp, minDot);
            Count = 3;
        }

        // 先按真实踝位置选择最近的探线。近处没支撑或跨过两踏面分界时，让本帧卸载 IK。
        public int Resolve(Vector3 ankle, ref CharacterIkCandidate candidate, out CharacterGroundFootHit hit)
        {
            const float maxDistanceSquared = SupportDistance * SupportDistance;
            int nearest = -1;
            float distance = maxDistanceSquared;
            hit = default;
            for (int i = 0; i < Count; i++)
            {
                var ray = Ray(i);
                float gap = Vector3.ProjectOnPlane(ankle - ray.Origin, Up).sqrMagnitude;
                if (gap >= distance) continue;
                nearest = i;
                distance = gap;
                hit = ray;
            }
            if (hit.Platform == 0 || IsTreadBoundary(nearest, ankle, hit, Mathf.Sqrt(distance)))
            {
                candidate = default;
                hit = default;
                return -1;
            }
            SetTarget(ref candidate, hit);
            return nearest;
        }

        private void SetTarget(ref CharacterIkCandidate candidate, in CharacterGroundFootHit hit)
        {
            if (hit.Platform == 0)
            {
                candidate = default;
                return;
            }
            candidate.ObjectId = hit.Platform;
            candidate.Target = new Pose(hit.Point + hit.Normal * SoleHeight, Quaternion.FromToRotation(Up, hit.Normal));
        }

        private bool IsTreadBoundary(int nearest, Vector3 ankle, in CharacterGroundFootHit hit, float distance)
        {
            for (int i = 0; i < Count; i++)
            {
                if (i == nearest) continue;
                var other = Ray(i);
                if (other.Platform == 0) continue;
                float planeGap = Mathf.Abs(Vector3.Dot(other.Point - hit.Point, hit.Normal));
                if (planeGap <= TreadHeightTolerance) continue;
                float horizontalGap = Vector3.ProjectOnPlane(ankle - other.Origin, Up).magnitude;
                if (horizontalGap - distance <= TreadBoundaryTolerance) return true;
            }
            return false;
        }

        private static CharacterGroundFootHit FindHit(PhysicsScene physics, CharacterIkTuning channel, Transform owner,
            Transform skeleton, ref RaycastHit[] hits, Vector3 origin, Vector3 up, Vector3 supportUp, float minDot)
        {
            var result = new CharacterGroundFootHit { Origin = origin };
            int count;
            while ((count = physics.Raycast(origin, -up, hits, channel.ProbeLength, channel.GroundLayers,
                       QueryTriggerInteraction.Ignore)) == hits.Length)
                System.Array.Resize(ref hits, checked(hits.Length * 2));
            int best = -1;
            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                var collider = hit.collider;
                if (!collider || collider.transform.IsChildOf(owner) || (skeleton && collider.transform.IsChildOf(skeleton)) ||
                    Vector3.Dot(hit.normal, supportUp) < minDot || Vector3.Dot(hit.normal, up) <= 0.001f) continue;
                if (best < 0 || hit.distance < hits[best].distance) best = i;
            }
            if (best < 0) return result;
            var support = hits[best];
            Transform platform = support.rigidbody ? support.rigidbody.transform : support.collider.transform;
            result.Platform = platform.GetInstanceID();
            result.Point = support.point;
            result.Normal = support.normal;
            result.ToWorld = platform.localToWorldMatrix;
            result.ToLocal = platform.worldToLocalMatrix;
            result.Rotation = platform.rotation;
            return result;
        }
    }
}
