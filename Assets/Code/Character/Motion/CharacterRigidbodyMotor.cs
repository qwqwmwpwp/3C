using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [System.Serializable]
    internal struct CharacterMotorSuspension
    {
        public bool Active;
        public Vector3 Velocity;
    }

    /// <summary>用动态刚体执行水平移动，保存求解器的支撑面与碰撞方向。由角色驱动器推进。</summary>
    internal sealed class CharacterRigidbodyMotor
    {
        private CharacterMotorSettings settings;
        private float bakedFootfallDistance;

        internal struct Surface
        {
            public CollisionFlags Flags;
            public Vector3 Ground;
            public CharacterSlopeContacts Steep;
        }

        // 休眠时没有持续接触回调，按碰撞体缓存至离开。
        private readonly Dictionary<Collider, Surface> surfaces = new Dictionary<Collider, Surface>();
        private Rigidbody body;
        private CapsuleCollider capsule;
        internal CharacterStepProbe Steps { get; private set; }
        private CharacterMotorStep step;
        internal CharacterDriveParameters Parameters(float acceleration, float turnSpeed) =>
            new CharacterDriveParameters(settings, acceleration, turnSpeed, bakedFootfallDistance, body.useGravity, Physics.gravity, body.drag);
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
                    surface.Steep.Add(normal);
                }
            }
            surfaces[collision.collider] = surface;
        }

        public void Forget(Collider other) => surfaces.Remove(other);

        internal Surface ReadContacts() => ReadContacts(out _);

        private Surface ReadContacts(out bool moving)
        {
            moving = false;
            Surface result = default;
            foreach (var pair in surfaces)
            {
                if (pair.Key == null || !pair.Key.enabled || !pair.Key.gameObject.activeInHierarchy)
                    continue;
                var surface = pair.Value;
                var otherBody = pair.Key.attachedRigidbody;
                moving |= otherBody != null;
                if (!otherBody || otherBody.isKinematic) result.Steep.Include(surface.Steep);
                result.Flags |= surface.Flags;
                if (surface.Ground.y > result.Ground.y)
                    result.Ground = surface.Ground;
            }
            return result;
        }

        internal CharacterForecastOrigin CaptureForecast(double time, CharacterBehaviorFrameResult behavior, CharacterMoveGait gait)
        {
            var contacts = ReadContacts(out bool moving);
            var sample = new CharacterForecastSample(body.position, body.velocity, body.rotation, behavior, contacts.Flags, gait);
            return new CharacterForecastOrigin(time, behavior.FrameNumber, sample, step, contacts.Ground, contacts.Steep, moving);
        }

        public CharacterGroundDrive Drive(CharacterBehaviorFrameResult frame, CharacterInputSnapshot input, float dt,
            in CharacterDriveParameters driveParameters, in Surface contacts)
        {
            var ground = contacts.Ground;
            Vector3 target = CharacterMotorStep.Target(frame, dt);
            Vector3 velocity = body.velocity;
            bool supported = ground.y > 0f && Vector3.Dot(velocity, ground) < 0.5f;
            bool stepped = supported && target.sqrMagnitude > 0f && TryLiftOntoStep(target, dt);
            if (!stepped) target = contacts.Steep.Limit(target);
            Quaternion rotation = body.rotation;
            var drive = step.Advance(frame, input, dt, body.position, ref velocity, target, ground, contacts.Flags,
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

        public void SetSuspended(bool value, ref CharacterMotorSuspension suspension)
        {
            if (body == null || value == suspension.Active)
                return;
            suspension.Active = value;
            step.ResetStride();
            surfaces.Clear();
            if (value)
            {
                suspension.Velocity = body.velocity;
                body.isKinematic = true;
            }
            else
            {
                body.isKinematic = false;
                body.velocity = suspension.Velocity;
                body.WakeUp();
            }
        }

        public void ResetMotion(ref CharacterMotorSuspension suspension)
        {
            step = default;
            surfaces.Clear();
            suspension.Velocity = Vector3.zero;
            if (body != null && !body.isKinematic)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }
    }
}
