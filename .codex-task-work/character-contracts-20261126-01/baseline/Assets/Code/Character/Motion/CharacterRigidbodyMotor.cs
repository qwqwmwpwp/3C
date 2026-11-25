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

    // 保存求解后的刚体位姿、速度和接触面，供步末结算与后续推演共享。
    internal readonly struct CharacterMotorObservation
    {
        internal readonly Vector3 Position, Velocity, Ground;
        internal readonly Quaternion Rotation;
        internal readonly CollisionFlags Contacts;
        internal readonly CharacterSlopeContacts Steep;
        internal readonly bool HasDynamicContact;

        // 记录同一次读取的刚体与接触信息，位置和速度均使用世界坐标。
        internal CharacterMotorObservation(Vector3 position, Vector3 velocity, Quaternion rotation,
            CollisionFlags contacts, Vector3 ground, CharacterSlopeContacts steep, bool hasDynamicContact)
        {
            Position = position;
            Velocity = velocity;
            Rotation = rotation;
            Contacts = contacts;
            Ground = ground;
            Steep = steep;
            HasDynamicContact = hasDynamicContact;
        }
    }

    /// <summary>用动态刚体执行水平移动，保存求解器的支撑面与碰撞方向。由角色驱动器推进。</summary>
    internal sealed class CharacterRigidbodyMotor
    {
        private float submittedGroundDot; // 本次求解判为可行走表面的法线 Y 下限。
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
        internal CharacterMotorStep DriveState => step;
        // 用当前角色物理配置和已绑定的刚体读取本次驱动参数，预测读取也不会改写实走状态。
        internal CharacterDriveParameters Parameters(in CharacterRuntimeConfiguration config) =>
            config.DriveParameters(body, config.Motor, bakedFootfallDistance);
        internal bool Available => body != null && capsule != null && capsule.enabled;

        internal readonly struct StepState
        {
            internal readonly Vector3 Start;
            internal readonly Vector2 Facing;
            internal readonly Vector2 GroundVelocity;
            internal readonly Surface Contacts;

            internal StepState(Rigidbody body, Surface contacts)
            {
                Start = body.position;
                Vector3 forward = body.rotation * Vector3.forward;
                Facing = new Vector2(forward.x, forward.z);
                GroundVelocity = CharacterMotorStep.GroundVelocity(body.velocity, contacts.Ground);
                Contacts = contacts;
            }
        }

        internal StepState ReadStepState() => new StepState(body, ReadContacts());

        // 在目标变化时读取当前真实刚体站位与水平朝向，尚未绑定时没有可用起点。
        internal CharacterAlignmentOrigin? ReadAlignmentOrigin()
        {
            if (!Available) return null;
            return new CharacterAlignmentOrigin { Position = body.position, Facing = CharacterAlignmentPlan.Flat(body.rotation * Vector3.forward) };
        }

        // 读取实际求解后的刚体运动和接触面。
        internal CharacterMotorObservation ReadSolvedPose()
        {
            var contacts = ReadContacts(out bool moving);
            return new CharacterMotorObservation(body.position, body.velocity, body.rotation, contacts.Flags,
                contacts.Ground, contacts.Steep, moving);
        }

        // 绑定物理组件并为首个物理步到来以前的碰撞提供初始坡度限制。
        public void Initialize(Rigidbody rigidbody, CapsuleCollider collider, CharacterMotorSettings tuning, float footfallDistance)
        {
            submittedGroundDot = Mathf.Cos(tuning.slopeLimit * Mathf.Deg2Rad);
            bakedFootfallDistance = footfallDistance;
            body = rigidbody;
            capsule = collider;
            Steps = new CharacterStepProbe(body, collider);
        }

        // 按实走提交时锁定的坡度阈值记录本次求解的接触面。
        public void Record(Collision collision)
        {
            var surface = new Surface();
            for (int i = 0; i < collision.contactCount; i++)
            {
                ContactPoint contact = collision.GetContact(i);
                if (contact.thisCollider != capsule)
                    continue;
                Vector3 normal = contact.normal;
                if (normal.y >= submittedGroundDot)
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

        private Surface ReadContacts() => ReadContacts(out _);

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

        private CharacterMotorStep beforePending;
        private float previousGroundDot;
        private Vector3 pendingVelocity, pendingPosition, requestedVelocity, liftedPosition;
        private Quaternion initialRotation, requestedRotation;
        private readonly List<CharacterMotionSlice> stepSlices = new List<CharacterMotionSlice>();

        // 从刚刚求解的那一组实际马达切片结算对齐实例，回调之前仍保留此次提交的起点。
        internal long ObserveAlignment(ref CharacterActionAlignmentRuntime alignment, in CharacterMotorObservation actual)
        {
            Vector3 forward = actual.Rotation * Vector3.forward;
            return alignment.Observe(stepSlices, pendingPosition, actual.Position, new Vector2(forward.x, forward.z), actual.Contacts);
        }

        // 逐段计算速度和转向，将整步积分得到的平均速度只交给求解器一次。
        internal CharacterPendingPhysicsStep DriveStep(CharacterRigidbodyBlackboard board, float dt, double end,
            long number, in CharacterDriveParameters parameters)
        {
            float groundDot = Mathf.Cos(parameters.Settings.slopeLimit * Mathf.Deg2Rad);
            float stepHeight = parameters.Settings.stepHeight;
            var startCursor = board.ReadStep(end, stepSlices);
            beforePending = step;
            pendingVelocity = body.velocity;
            pendingPosition = body.position;
            initialRotation = body.rotation;
            Vector3 start = body.position;
            var contacts = ReadContacts();
            var rotation = body.rotation;
            var velocity = step.ResumeVelocity(body.velocity);
            Vector2 request = default, action = default;
            foreach (var slice in stepSlices) request += slice.Behavior.Displacement;
            Vector3 requested = new Vector3(request.x, 0f, request.y);
            bool stepped = CharacterMotorStep.IsGroundSupported(velocity, contacts.Ground) && requested.sqrMagnitude > 0f &&
                TryLiftOntoStep(requested / dt, dt, stepHeight, groundDot);
            Vector3 integrated = Vector3.zero;
            CharacterGroundDrive drive = default;
            foreach (var slice in stepSlices)
            {
                float seconds = (float)(slice.End - slice.Start);
                bool supported = CharacterMotorStep.IsGroundSupported(velocity, contacts.Ground);
                drive = step.Advance(slice.Behavior, slice.Input, seconds, body.position + integrated,
                    ref velocity, contacts.Steep, contacts.Ground, contacts.Flags, supported, stepped, parameters, ref rotation);
                integrated += velocity * seconds;
                action += slice.Behavior.ActionDisplacement;
            }
            Vector3 mean = integrated / dt;
            step.RecordSubmittedVelocity(mean, velocity, stepSlices.Count > 1);
            if (mean != body.velocity) body.velocity = mean;
            if (!rotation.Equals(body.rotation)) body.MoveRotation(rotation);
            var last = stepSlices[stepSlices.Count - 1];
            requestedRotation = rotation;
            requestedVelocity = mean;
            liftedPosition = body.position;
            var behavior = last.Behavior;
            var total = behavior.WithMotion(request, action, behavior.ActionMotion, behavior.DisplacementProvider,
                behavior.OrdinaryMovementSeconds, behavior.MoveApproved, behavior.ActionMotionStarted);
            previousGroundDot = submittedGroundDot;
            submittedGroundDot = groundDot;
            board.MarkSubmitted(end, startCursor);
            return new CharacterPendingPhysicsStep(total, drive, last.Input, start, dt, end, number,
                Time.fixedTimeAsDouble, parameters.TurnSpeed);
        }

        // 物理求解前撤销提交，恢复被本次步进写入的马达状态、速度、转向和跨阶高度。
        internal void CancelPending(CharacterRigidbodyBlackboard board)
        {
            board.CancelSubmission();
            step = beforePending;
            submittedGroundDot = previousGroundDot;
            if (liftedPosition != pendingPosition && body.position == liftedPosition) body.position = pendingPosition;
            if (requestedRotation != initialRotation)
                body.MoveRotation(body.rotation == requestedRotation ? initialRotation : body.rotation);
            if (!body.isKinematic && body.velocity == requestedVelocity) body.velocity = pendingVelocity;
        }

        // 使用该固定步的高度、坡度和目标位移探测可通行台阶。
        private bool TryLiftOntoStep(Vector3 target, float dt, float stepHeight, float groundDot)
        {
            if (!Steps.TryRise(body.position, body.rotation, target * dt, stepHeight, groundDot, out float lift, out _)
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
