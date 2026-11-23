using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ThreeC.CharacterBehavior
{
    /// <summary>以副本胶囊的位置查询角色所在的物理场景。动态接触处终止可信轨迹。</summary>
    internal sealed class CharacterForecastGeometry
    {
        private readonly Rigidbody owner;
        private readonly CapsuleCollider capsule;
        private RaycastHit[] hits = new RaycastHit[8];
        private PhysicsScene scene;
        private Vector3 centerOffset, axis;
        private float radius, halfSegment, skin, groundDot, stepHeight;
        private int layerMask;

        internal CharacterForecastGeometry(Rigidbody owner, CapsuleCollider capsule)
        {
            this.owner = owner;
            this.capsule = capsule;
        }

        internal void Begin(CharacterMotorSettings settings)
        {
            scene = owner.gameObject.scene.GetPhysicsScene();
            Vector3 scale = capsule.transform.lossyScale;
            centerOffset = Vector3.Scale(capsule.center, scale);
            Vector3 size = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            int d = capsule.direction;
            axis = d == 0 ? Vector3.right : d == 1 ? Vector3.up : Vector3.forward;
            radius = capsule.radius * Mathf.Max(size[(d + 1) % 3], size[(d + 2) % 3]);
            halfSegment = Mathf.Max(0f, capsule.height * size[d] * 0.5f - radius);
            skin = Mathf.Clamp(capsule.contactOffset, 0.001f, Mathf.Max(0.001f, radius * 0.25f));
            groundDot = Mathf.Cos(settings.slopeLimit * Mathf.Deg2Rad);
            stepHeight = settings.stepHeight;
            layerMask = 0;
            for (int i = 0; i < 32; i++)
                if (!Physics.GetIgnoreLayerCollision(capsule.gameObject.layer, i)) layerMask |= 1 << i;
        }

        private bool Cast(Vector3 position, Quaternion rotation, Vector3 direction, float distance, out RaycastHit hit)
        {
            Vector3 center = position + rotation * centerOffset;
            Vector3 span = rotation * axis * halfSegment;
            int count;
            do
            {
                count = scene.CapsuleCast(center - span, center + span, Mathf.Max(0.0001f, radius - skin), direction,
                    hits, distance, layerMask, QueryTriggerInteraction.Ignore);
                if (count < hits.Length) break;
                Array.Resize(ref hits, hits.Length * 2);
            } while (true);
            float nearest = float.PositiveInfinity;
            hit = default;
            for (int i = 0; i < count; i++)
            {
                var next = hits[i];
                if (next.rigidbody == owner || next.distance >= nearest || Vector3.Dot(next.normal, direction) >= -0.0001f ||
                    Physics.GetIgnoreCollision(capsule, next.collider)) continue;
                hit = next;
                nearest = next.distance;
            }
            return nearest < float.PositiveInfinity;
        }

        // 预测无法确定场景中另一刚体的未来运动；不把它假设成静止墙或地面。
        private static bool MovingSurface(RaycastHit hit) => hit.rigidbody != null;

        internal bool TryStep(ref Vector3 position, Quaternion rotation, Vector3 displacement, out bool uncertain)
        {
            uncertain = false;
            if (stepHeight <= 0f || displacement.sqrMagnitude < 0.00000001f) return false;
            Vector3 direction = displacement.normalized;
            float reach = displacement.magnitude + skin;
            if (!Cast(position, rotation, direction, reach, out var face) || face.normal.y >= groundDot) return false;
            if (MovingSurface(face)) { uncertain = true; return false; }
            float height = stepHeight + skin;
            if (Cast(position, rotation, Vector3.up, height, out _)) return false;
            Vector3 raised = position + Vector3.up * height;
            if (Cast(raised, rotation, direction, reach, out _)) return false;
            if (!Cast(raised + direction * reach, rotation, Vector3.down, height + skin * 2f, out var top)
                || top.normal.y < groundDot) return false;
            if (MovingSurface(top)) { uncertain = true; return false; }
            float rise = height - top.distance + skin;
            if (rise <= skin || rise > height) return false;
            position += Vector3.up * rise;
            return true;
        }

        internal bool Move(ref Vector3 position, Quaternion rotation, ref Vector3 velocity, float dt,
            out CollisionFlags contacts, out Vector3 ground, out Vector3 steep)
        {
            contacts = CollisionFlags.None;
            ground = steep = Vector3.zero;
            Vector3 remaining = velocity * dt;
            for (int i = 0; i < 3 && remaining.sqrMagnitude > 0.000000001f; i++)
            {
                Vector3 direction = remaining.normalized;
                if (!Cast(position, rotation, direction, remaining.magnitude + skin, out var hit))
                {
                    position += remaining;
                    break;
                }
                if (MovingSurface(hit)) return false;
                float travel = Mathf.Clamp(hit.distance - skin, 0f, remaining.magnitude);
                position += direction * travel;
                remaining = Vector3.ProjectOnPlane(remaining - direction * travel, hit.normal);
                velocity -= hit.normal * Mathf.Min(0f, Vector3.Dot(velocity, hit.normal));
                if (hit.normal.y >= groundDot) { contacts |= CollisionFlags.Below; ground = hit.normal; }
                else if (hit.normal.y < -0.5f) contacts |= CollisionFlags.Above;
                else { contacts |= CollisionFlags.Sides; steep = hit.normal; }
            }
            if (Cast(position + Vector3.up * skin, rotation, Vector3.down, skin * 4f, out var floor)
                && floor.normal.y >= groundDot && Vector3.Dot(velocity, floor.normal) < 0.5f)
            {
                if (MovingSurface(floor)) return false;
                contacts |= CollisionFlags.Below;
                ground = floor.normal;
                position += Vector3.up * (2f * skin - floor.distance);
                velocity -= ground * Mathf.Min(0f, Vector3.Dot(velocity, ground));
            }
            return true;
        }
    }
}
