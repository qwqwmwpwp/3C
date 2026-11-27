using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在给定的胶囊位姿检查台阶立面和顶面。实时驱动与预测各自检查抬升净空。</summary>
    internal sealed class CharacterStepProbe
    {
        private readonly Rigidbody body;
        private readonly CapsuleCollider capsule;
        internal CharacterStepProbe(Rigidbody body, CapsuleCollider capsule)
        {
            this.body = body;
            this.capsule = capsule;
        }

        internal bool TryRise(Vector3 position, Quaternion rotation, Vector3 displacement, float height,
            float groundDot, out float lift, out bool moving)
        {
            moving = false;
            lift = 0f;
            if (height <= 0f || displacement == Vector3.zero) return false;
            Vector3 scale = capsule.transform.lossyScale;
            float radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            float halfHeight = Mathf.Max(radius, capsule.height * Mathf.Abs(scale.y) * 0.5f);
            Vector3 center = position + rotation * Vector3.Scale(capsule.center, scale);
            Vector3 foot = center - Vector3.up * halfHeight;
            float skin = Mathf.Max(0.005f, capsule.contactOffset);
            var probe = new CharacterStepProbePlan(foot, displacement, radius, skin, height);
            if (!FindStepSurface(probe.FaceOrigin, probe.Direction, probe.FaceDistance, out var face) ||
                face.normal.y >= groundDot) return false;
            moving = face.rigidbody != null;
            if (moving && !face.rigidbody.isKinematic) return false;
            if (!FindStepSurface(probe.TopOrigin(face.distance), Vector3.down, probe.TopDistance, out var top) ||
                top.normal.y < groundDot) return false;
            moving |= top.rigidbody != null;
            if (top.rigidbody != null && !top.rigidbody.isKinematic) return false;
            return probe.TryLift(top.point.y, out lift);
        }

        private RaycastHit[] hits = new RaycastHit[4];

        private bool FindStepSurface(Vector3 origin, Vector3 direction, float distance, out RaycastHit nearest)
        {
            int count;
            while ((count = body.gameObject.scene.GetPhysicsScene().Raycast(origin, direction, hits, distance, Physics.AllLayers,
                       QueryTriggerInteraction.Ignore)) == hits.Length)
                Array.Resize(ref hits, hits.Length * 2);
            nearest = default;
            float best = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.rigidbody == body || hit.distance >= best ||
                    Physics.GetIgnoreLayerCollision(capsule.gameObject.layer, hit.collider.gameObject.layer) ||
                    Physics.GetIgnoreCollision(capsule, hit.collider))
                    continue;
                nearest = hit;
                best = hit.distance;
            }
            return best < float.PositiveInfinity;
        }

    }
}
