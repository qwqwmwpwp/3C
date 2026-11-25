using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.SceneManagement;

namespace ThreeC.CharacterBehavior
{
    // 按实际表现根中心和腿部朝向中心分别变换脚踝，平移只属于视觉根。
    internal readonly struct CharacterFootWarp
    {
        internal readonly Quaternion Root, Legs;
        internal readonly Vector3 Displacement;

        // 记录与本帧动画流写入相同的两级旋转和根位移。
        internal CharacterFootWarp(Quaternion root, Quaternion legs, Vector3 displacement)
        { Root = root; Legs = legs; Displacement = displacement; }

        // 将表现根写入前的点转换为腿部朝向修正之前的世界坐标。
        internal Vector3 RootPoint(Vector3 point, Vector3 pivot) => pivot + Displacement + Root * (point - pivot);

        // 从根修正后的世界位置还原原始姿态点。
        internal Vector3 UndoRoot(Vector3 point, Vector3 pivot) => pivot + Quaternion.Inverse(Root) * (point - pivot - Displacement);
    }

    // 从最终 IK 前的脚踝观测预测下次根修正与腿部校正后的探测中心。
    internal struct CharacterFootProbeMotion
    {
        private byte hasSample;
        private Vector3 rootPivotLocal, legPivotLocal;
        private Vector3 offset, velocity;

        // 按写入相反的顺序绕各自支点还原原始踝位，避免将旧偏航或平移计入速度。
        public void Observe(AnimationStream stream, TransformSceneHandle basis, Vector3 ankle,
            in CharacterFootWarp warp, Vector3 rootPivot, Vector3 legPivot)
        {
            if (!basis.IsValid(stream)) { this = default; return; }
            Vector3 origin = basis.GetPosition(stream);
            Quaternion inverse = Quaternion.Inverse(basis.GetRotation(stream));
            rootPivotLocal = inverse * (rootPivot - origin);
            legPivotLocal = inverse * (warp.UndoRoot(legPivot, rootPivot) - origin);
            Vector3 beforeLegs = legPivot + Quaternion.Inverse(warp.Legs) * (ankle - legPivot);
            Vector3 next = inverse * (warp.UndoRoot(beforeLegs, rootPivot) - origin);
            float dt = stream.deltaTime;
            velocity = hasSample != 0 && dt > 0.00001f ? (next - offset) / dt : Vector3.zero;
            offset = next;
            hasSample = 1;
        }

        // 使用上帧原始姿势及两个原始支点重施新修正，保留射线起点的垂直高度。
        public void Predict(Transform owner, Vector3 fallback, float expectedDt, in CharacterFootWarp warp,
            out Vector3 center, out Vector3 step)
        {
            Vector3 root = hasSample == 0 ? owner.position : owner.position + owner.rotation * rootPivotLocal;
            Vector3 leg = hasSample == 0 ? root : owner.position + owner.rotation * legPivotLocal;
            leg = warp.RootPoint(leg, root);
            Vector3 raw = hasSample == 0 ? fallback : owner.position + owner.rotation * offset;
            Vector3 point = leg + warp.Legs * (warp.RootPoint(raw, root) - leg);
            step = Vector3.ClampMagnitude(Vector3.ProjectOnPlane(warp.Legs * (warp.Root * (owner.rotation * velocity)), owner.up) *
                Mathf.Clamp(expectedDt, 0f, 0.05f), 0.18f);
            center = fallback + Vector3.ProjectOnPlane(point - fallback, owner.up) + step;
        }
    }

    internal struct CharacterGroundFootHit
    {
        public Vector3 Origin, Point, Normal;
        public int Platform;
        public Matrix4x4 ToWorld, ToLocal;
        public Quaternion Rotation;
    }

    // 保存足底支撑探测并按实际踝位置确定可用踏面。
    internal struct CharacterGroundFootProbes
    {
        private const float SupportDistance = 0.07f;
        private const float ExtraProbeThreshold = 0.015f;
        private const float TreadHeightTolerance = 0.025f;
        private const float TreadBoundaryTolerance = 0.015f;

        private CharacterGroundFootHit center, behind, ahead;
        public Vector3 Up;
        private float soleHeight;
        private int count;

        public CharacterGroundFootHit Center => center;

        // 读取某一道探线的物理命中结果。
        private CharacterGroundFootHit Ray(int index) => index == 0 ? center : index == 1 ? behind : ahead;

        public void AcceptCenter(ref CharacterIkCandidate candidate) => SetTarget(ref candidate, center);

        // 沿脚掌预估位置发射支撑探线，高速移动时补查前后两侧。
        public void Cast(PhysicsScene physics, CharacterIkTuning channel, Transform owner, Transform skeleton,
            float walkableSlope, in CharacterFootProbeMotion motion, float expectedDt, in CharacterFootWarp warp, ref RaycastHit[] hits)
        {
            Up = owner.up;
            soleHeight = channel.SoleHeight;
            motion.Predict(owner, owner.TransformPoint(channel.ProbeOrigin), expectedDt, warp, out var origin, out var step);
            bool inherit = channel.ProbeSlopeLimit < 0f;
            Vector3 supportUp = inherit ? Vector3.up : Up;
            float minDot = Mathf.Cos(Mathf.Clamp(inherit ? walkableSlope : channel.ProbeSlopeLimit, 0f, 89f) * Mathf.Deg2Rad);
            count = 1;
            Vector3 up = owner.up;
            center = FindHit(physics, channel, owner, skeleton, ref hits, origin, up, supportUp, minDot);
            if (step.sqrMagnitude < ExtraProbeThreshold * ExtraProbeThreshold) return;
            Vector3 spacing = Vector3.ClampMagnitude(step, 0.12f);
            behind = FindHit(physics, channel, owner, skeleton, ref hits, origin - spacing, up, supportUp, minDot);
            ahead = FindHit(physics, channel, owner, skeleton, ref hits, origin + spacing, up, supportUp, minDot);
            count = 3;
        }

        // 先按真实踝位置选择最近的探线。近处没支撑或跨过两踏面分界时，让本帧卸载 IK。
        public void Resolve(Vector3 ankle, ref CharacterIkCandidate candidate, out CharacterGroundFootHit hit)
        {
            const float maxDistanceSquared = SupportDistance * SupportDistance;
            int nearest = -1;
            float distance = maxDistanceSquared;
            hit = default;
            for (int i = 0; i < count; i++)
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
                return;
            }
            SetTarget(ref candidate, hit);
        }

        // 将有效支撑面转换为骨链的地面目标。
        private void SetTarget(ref CharacterIkCandidate candidate, in CharacterGroundFootHit hit)
        {
            if (hit.Platform == 0)
            {
                candidate = default;
                return;
            }
            candidate.ObjectId = hit.Platform;
            candidate.Target = new Pose(hit.Point + hit.Normal * soleHeight, Quaternion.FromToRotation(Up, hit.Normal));
        }

        // 判断脚踝最近的探线是否靠近不同高度的踏面边界。
        private bool IsTreadBoundary(int nearest, Vector3 ankle, in CharacterGroundFootHit hit, float distance)
        {
            for (int i = 0; i < count; i++)
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
