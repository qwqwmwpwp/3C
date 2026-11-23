using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>用动态刚体执行水平移动，保存求解器的支撑面与碰撞方向。由角色驱动器推进。</summary>
    internal sealed class CharacterRigidbodyMotor
    {
        private CharacterMotorSettings settings;
        private float bakedFootfallDistance;

        private struct Surface
        {
            public CollisionFlags Flags;
            public Vector3 Ground;
            public Vector3 Steep;
        }

        // 休眠时没有持续接触回调，按碰撞体缓存至离开。
        private readonly Dictionary<Collider, Surface> surfaces = new Dictionary<Collider, Surface>();
        private Rigidbody body;
        private CapsuleCollider capsule;
        internal CharacterStepProbe Steps { get; private set; }
        private CharacterMotorStep step;
        internal CharacterDriveParameters Parameters(float acceleration, float turnSpeed) =>
            new CharacterDriveParameters(settings, acceleration, turnSpeed, bakedFootfallDistance, body.useGravity, Physics.gravity, body.drag);
        private Vector3 suspendedVelocity;
        private bool suspended;
        private float GroundDot => Mathf.Cos(settings.slopeLimit * Mathf.Deg2Rad);

        public void Initialize(Rigidbody rigidbody, CapsuleCollider collider, CharacterMotorSettings tuning, float footfallDistance)
        {
            settings = tuning;
            bakedFootfallDistance = footfallDistance;
            body = rigidbody;
            capsule = collider;
            Steps = new CharacterStepProbe(body, collider);
        }

        public void Record(Collision collision)
        {
            var surface = new Surface();
            float minimumY = GroundDot;
            for (int i = 0; i < collision.contactCount; i++)
            {
                ContactPoint contact = collision.GetContact(i);
                if (contact.thisCollider != capsule)
                    continue;
                Vector3 normal = contact.normal;
                if (normal.y >= minimumY)
                {
                    surface.Flags |= CollisionFlags.Below;
                    if (normal.y > surface.Ground.y)
                        surface.Ground = normal;
                }
                else if (normal.y < -0.5f)
                    surface.Flags |= CollisionFlags.Above;
                else
                {
                    surface.Flags |= CollisionFlags.Sides;
                    if (normal.y > surface.Steep.y)
                        surface.Steep = normal;
                }
            }
            surfaces[collision.collider] = surface;
        }

        public void Forget(Collider other) => surfaces.Remove(other);

        public CollisionFlags ReadContacts(out Vector3 ground)
        {
            ground = Vector3.zero;
            CollisionFlags flags = CollisionFlags.None;
            foreach (var pair in surfaces)
            {
                if (pair.Key == null || !pair.Key.enabled || !pair.Key.gameObject.activeInHierarchy)
                    continue;
                Surface surface = pair.Value;
                flags |= surface.Flags;
                if (surface.Ground.y > ground.y)
                    ground = surface.Ground;
            }
            return flags;
        }

        internal CharacterForecastOrigin CaptureForecast(double time, CharacterBehaviorFrameResult behavior, CharacterMoveGait gait)
        {
            var contacts = ReadContacts(out var ground);
            Vector3 steep = Vector3.zero;
            bool moving = false;
            foreach (var pair in surfaces)
            {
                var other = pair.Key;
                if (!other || !other.enabled || !other.gameObject.activeInHierarchy) continue;
                moving |= other.attachedRigidbody != null;
                if (pair.Value.Steep.y > steep.y) steep = pair.Value.Steep;
            }
            var sample = new CharacterForecastSample(body.position, body.velocity, body.rotation, behavior, contacts, gait);
            return new CharacterForecastOrigin(time, behavior.FrameNumber, sample, step, ground, steep, moving);
        }

        public CharacterGroundDrive Drive(CharacterBehaviorFrameResult frame, CharacterInputSnapshot input, float dt,
            in CharacterDriveParameters driveParameters)
        {
            var contacts = ReadContacts(out Vector3 ground);
            Vector3 target = CharacterMotorStep.Target(frame, dt);
            Vector3 velocity = body.velocity;
            bool supported = ground.y > 0f && Vector3.Dot(velocity, ground) < 0.5f;
            bool stepped = supported && target.sqrMagnitude > 0f && TryLiftOntoStep(target, dt);
            if (!stepped && target.sqrMagnitude > 0f)
            {
                foreach (var pair in surfaces)
                {
                    if (pair.Key == null || !pair.Key.enabled || !pair.Key.gameObject.activeInHierarchy)
                        continue;
                    // 陡坡只消除向坡内的主动速度，竖直墙和可推动物由求解器处理。
                    if (pair.Key.attachedRigidbody == null || pair.Key.attachedRigidbody.isKinematic)
                        target = CharacterMotorStep.LimitOnSteepSurface(target, pair.Value.Steep);
                }
            }
            Quaternion rotation = body.rotation;
            var drive = step.Advance(frame, input, dt, body.position, ref velocity, target, ground, contacts,
                supported, stepped, driveParameters, ref rotation);
            if (velocity != body.velocity) body.velocity = velocity;
            if (!rotation.Equals(body.rotation)) body.MoveRotation(rotation);
            return drive;
        }

        private bool TryLiftOntoStep(Vector3 target, float dt)
        {
            if (!Steps.TryRise(body.position, body.rotation, target * dt, settings.stepHeight, GroundDot, out float lift, out _)
                || body.SweepTest(Vector3.up, out _, lift, QueryTriggerInteraction.Ignore)) return false;
            body.position += Vector3.up * lift;
            return true;
        }

        public void SetSuspended(bool value)
        {
            if (body == null || value == suspended)
                return;
            suspended = value;
            step.ResetStride();
            surfaces.Clear();
            if (value)
            {
                suspendedVelocity = body.velocity;
                body.isKinematic = true;
            }
            else
            {
                body.isKinematic = false;
                body.velocity = suspendedVelocity;
                body.WakeUp();
            }
        }

        public void ResetMotion()
        {
            step = default;
            surfaces.Clear();
            suspendedVelocity = Vector3.zero;
            if (body != null && !body.isKinematic)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }
    }
}
