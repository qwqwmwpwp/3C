using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ThreeC.CharacterBehavior
{
    /// <summary>以副本胶囊的位置查询角色所在的物理场景。动态接触处终止可信轨迹。</summary>
    internal sealed class CharacterForecastGeometry : ICharacterForecastGround
    {
        private readonly CharacterStepProbe steps;
        private readonly Rigidbody owner;
        private readonly CapsuleCollider capsule;
        private RaycastHit[] hits = new RaycastHit[8];
        private PhysicsScene scene;
        private Vector3 centerOffset, axis;
        private float radius, halfSegment, skin, groundDot, stepHeight;
        private int layerMask;

        internal CharacterForecastGeometry(Rigidbody owner, CapsuleCollider capsule, CharacterStepProbe steps)
        {
            this.steps = steps;
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

        internal readonly Collider[] Nearby = new Collider[64];
        internal bool Includes(Collider other) => other.attachedRigidbody != owner && !Physics.GetIgnoreCollision(capsule, other);
        internal PhysicsScene Scene => scene;
        internal int Mask => layerMask;
        internal CharacterForecastShape CaptureShape()
        {
            Vector3 scale = capsule.transform.lossyScale;
            float footRadius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            return new CharacterForecastShape(centerOffset, axis, radius, halfSegment, skin, groundDot, stepHeight,
                footRadius, Mathf.Max(footRadius, capsule.height * Mathf.Abs(scale.y) * 0.5f),
                Mathf.Max(0.005f, capsule.contactOffset));
        }

        internal bool Accept(RaycastHit hit, bool ray, Vector3 direction) =>
            hit.rigidbody != owner && !Physics.GetIgnoreCollision(capsule, hit.collider) &&
            (ray || Vector3.Dot(hit.normal, direction) < -0.0001f);

        // 极密集的重叠只重查溢出的单个指令，避免为整个批次放大结果区。
        internal ForecastHit ResolveSaturated(in ForecastQuery q)
        {
            int count;
            do
            {
                count = q.Ray ? scene.Raycast(q.A, q.Direction, hits, q.Distance, layerMask, QueryTriggerInteraction.Ignore)
                    : scene.CapsuleCast(q.A, q.B, q.Radius, q.Direction, hits, q.Distance, layerMask, QueryTriggerInteraction.Ignore);
                if (count < hits.Length) break;
                Array.Resize(ref hits, checked(hits.Length * 2));
            } while (true);
            var best = default(ForecastHit);
            for (int i = 0; i < count; i++)
                if ((!best.Found || hits[i].distance < best.Distance) && Accept(hits[i], q.Ray, q.Direction))
                    best = new ForecastHit(hits[i]);
            return best;
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

        private bool CastHit(Vector3 position, Quaternion rotation, Vector3 direction, float distance, out ForecastHit hit)
        {
            bool found = Cast(position, rotation, direction, distance, out var nearest);
            hit = found ? new ForecastHit(nearest) : default;
            return found;
        }

        public bool TryStep(ref Vector3 position, Quaternion rotation, Vector3 displacement, out bool uncertain)
        {
            if (!steps.TryRise(position, rotation, displacement, stepHeight, groundDot, out float lift, out uncertain)
                || uncertain || Cast(position, rotation, Vector3.up, lift, out _)) return false;
            position += Vector3.up * lift;
            return true;
        }

        public bool Move(ref Vector3 position, Quaternion rotation, ref Vector3 velocity, float dt,
            out CollisionFlags contacts, out Vector3 ground, out CharacterSlopeContacts steep)
        {
            return CharacterForecastSlide.Move(CastHit, skin, groundDot, ref position, rotation, ref velocity, dt,
                out contacts, out ground, out steep);
        }
    }
}
