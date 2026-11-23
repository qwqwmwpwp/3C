using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>用动态刚体执行水平移动，保存求解器的支撑面与碰撞方向。由角色驱动器推进。</summary>
    [Serializable]
    internal sealed class CharacterRigidbodyMotor
    {
        private const float MaximumRunBrakeLeadInSeconds = 0.08f;
        private const float RunCadenceDirectionDot = 0.7f;

        [SerializeField, Min(0f), Tooltip("地面每秒可修正的速度（米/秒²），同时决定受推后恢复控制的快慢。")]
        private float groundAcceleration = 45f;
        [SerializeField, Min(0.01f), Tooltip("步行主动松键时的地面减速度（米/秒²）。")]
        private float walkBraking = 15f;
        [SerializeField, Min(0.01f), Tooltip("常跑主动松键时的地面减速度（米/秒²）。")]
        private float runBraking = 18f;
        [SerializeField, Range(0f, MaximumRunBrakeLeadInSeconds), Tooltip("稳定跑步松键后轻制动的最长时间（秒）；0 立即主制动。")]
        private float runBrakeLeadInSeconds;
        [SerializeField, Min(0.01f), Tooltip("常跑轻制动减速度（米/秒²），不高于主制动减速度。")]
        private float runBrakeLeadInDeceleration = 8f;
        [SerializeField, Min(0f), Tooltip("建立常跑落脚估算所需的实际水平前进速度（米/秒）。")]
        private float runBrakeLeadInMinSpeed = 3.5f;
        [SerializeField, Min(0.01f), Tooltip("估算常跑左右脚交替落地的水平间距（米）。")]
        private float runBrakeLeadInFootfallDistance = 1.66f;
        [SerializeField, Min(0.01f), Tooltip("持续冲刺主动松键时的地面减速度（米/秒²）。")]
        private float sprintBraking = 45f;
        [SerializeField, Min(0f), Tooltip("滞空时有输入的水平加速度（米/秒²）。松手后保留空中惯性。")]
        private float airAcceleration = 12f;
        [SerializeField, Range(0f, 89f), Tooltip("可行走地面相对水平面的最大坡角。")]
        private float slopeLimit = 45f;
        [SerializeField, Min(0f), Tooltip("可跨越的最大台阶高度（世界米），设为 0 关闭跨阶。")]
        private float stepHeight = 0.3f;

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
        private Vector3 previousDashVelocity;
        private CharacterMoveGait? groundMoveGait;
        private Vector3 pendingRunStepStart;
        private Vector3 pendingRunStepDirection;
        private float pendingRunStepSeconds;
        private float runCadenceTravel;
        private float runBrakeLeadInRemaining;
        private Vector3 pendingBrakeVelocity;
        private Vector3 suspendedVelocity;
        private bool suspended;
        private float GroundDot => Mathf.Cos(slopeLimit * Mathf.Deg2Rad);

        public void Initialize(Rigidbody rigidbody, CapsuleCollider collider)
        {
            body = rigidbody;
            capsule = collider;
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

        public CharacterGroundDrive Drive(CharacterBehaviorFrameResult frame, CharacterInputSnapshot input, float dt, Vector3 ground)
        {
            Vector3 target = new Vector3(frame.Displacement.x, 0f, frame.Displacement.y) / dt;
            bool dash = frame.DisplacementSource == CharacterDisplacementSource.Dash;
            Vector3 velocity = body.velocity;
            bool supported = ground.y > 0f && Vector3.Dot(velocity, ground) < 0.5f;
            bool stepped = supported && target.sqrMagnitude > 0f && TryLiftOntoStep(target, dt);
            // 仅扣除冲刺前向残速，保留侧向冲量，避免撞墙后反向拉回。
            if ((!dash || frame.DashStarted) && previousDashVelocity.sqrMagnitude > 0f)
            {
                Vector3 axis = previousDashVelocity.normalized;
                float excess = Vector3.Dot(velocity - target, axis);
                velocity -= axis * Mathf.Clamp(excess, 0f, previousDashVelocity.magnitude);
            }

            if (!stepped && target.sqrMagnitude > 0f)
            {
                foreach (var pair in surfaces)
                {
                    if (pair.Key == null || !pair.Key.enabled || !pair.Key.gameObject.activeInHierarchy)
                        continue;
                    Vector3 downhill = Vector3.ProjectOnPlane(pair.Value.Steep, Vector3.up).normalized;
                    // 陡坡只消除向坡内的主动速度，竖直墙和可推动物由求解器处理。
                    if (pair.Key.attachedRigidbody == null || pair.Key.attachedRigidbody.isKinematic)
                        target -= downhill * Mathf.Min(0f, Vector3.Dot(target, downhill));
                }
            }
            Vector3 plane = supported && !stepped ? ground : Vector3.up;
            target = Vector3.ProjectOnPlane(target, plane).normalized * target.magnitude;
            Vector3 current = Vector3.ProjectOnPlane(velocity, plane);
            float change = supported ? groundAcceleration : airAcceleration;
            bool braking = supported && input.Direction == Vector2.zero &&
                frame.DisplacementProvider == CharacterBehaviorId.None && groundMoveGait.HasValue;
            if (supported && frame.MoveApproved)
                groundMoveGait = input.Gait;
            else if (braking)
            {
                switch (groundMoveGait.Value) // 按最后的主动档位和刚体实速制动。
                {
                    case CharacterMoveGait.Walk: change = walkBraking; break;
                    case CharacterMoveGait.Sprint: change = sprintBraking; break;
                    default: change = runBraking; break;
                }
                change = Mathf.Max(0.01f, InputMath.NonNegative(change, groundAcceleration));
            }
            else
                groundMoveGait = null;
            float leadInDeceleration = Mathf.Min(change,
                Mathf.Max(0.01f, InputMath.NonNegative(runBrakeLeadInDeceleration, 8f)));
            UpdateRunBrakeLeadIn(frame, input, current, target, dt, supported && !stepped, braking, leadInDeceleration);
            CharacterGroundBrake brake = braking
                ? new CharacterGroundBrake(change, leadInDeceleration, runBrakeLeadInRemaining) : default;
            Vector3 next = brake.IsActive ? brake.Apply(current, dt)
                : dash ? target : Vector3.MoveTowards(current, target, change * dt);
            runBrakeLeadInRemaining = brake.AfterStep(dt).LeadInSeconds;
            pendingBrakeVelocity = runBrakeLeadInRemaining > 0f ? next : Vector3.zero;
            if (!frame.MoveApproved && next.sqrMagnitude < 0.000001f)
            {
                groundMoveGait = null;
                ResetRunBrakeLeadIn();
            }
            if (supported || frame.DisplacementProvider != CharacterBehaviorId.None)
                velocity += next - current;
            if (supported && !stepped && body.useGravity) // 只抵消沿坡重力，保留贴地的法向重力。
                velocity -= Vector3.ProjectOnPlane(Physics.gravity, plane) * dt;
            if (velocity != body.velocity)
                body.velocity = velocity;
            previousDashVelocity = dash ? target : Vector3.zero;
            return supported && !stepped && (frame.MoveApproved || braking)
                ? new CharacterGroundDrive(plane, target, change, current, brake) : default;
        }

        private void UpdateRunBrakeLeadIn(CharacterBehaviorFrameResult frame, CharacterInputSnapshot input,
            Vector3 current, Vector3 target, float dt, bool grounded, bool braking, float leadInDeceleration)
        {
            bool running = frame.DisplacementProvider == CharacterBehaviorId.Move && frame.MoveApproved &&
                input.Gait == CharacterMoveGait.Run;
            bool runBrakingActive = braking && groundMoveGait == CharacterMoveGait.Run;
            float maximum = Mathf.Min(MaximumRunBrakeLeadInSeconds, InputMath.NonNegative(runBrakeLeadInSeconds, 0f));
            if (maximum <= 0f || !grounded || (!running && !runBrakingActive))
            {
                ResetRunBrakeLeadIn();
                return;
            }

            CollisionFlags contacts = ReadContacts(out _);
            bool hasLeadIn = runBrakingActive && runBrakeLeadInRemaining > 0f;
            bool blocked = hasLeadIn
                ? CharacterGroundBrake.IsObstructed(pendingBrakeVelocity, current, contacts) ||
                    current.sqrMagnitude <= 0.000001f
                : (contacts & CollisionFlags.Sides) != 0;
            if (blocked)
            {
                ResetRunBrakeLeadIn();
                return;
            }

            float speedThreshold = InputMath.NonNegative(runBrakeLeadInMinSpeed, 3.5f);
            float footfallDistance = Mathf.Max(0.01f, InputMath.NonNegative(runBrakeLeadInFootfallDistance, 1.66f));
            if (running)
            {
                runBrakeLeadInRemaining = 0f;
                Vector3 direction = Vector3.ProjectOnPlane(target, Vector3.up).normalized;
                if (direction == Vector3.zero || Vector3.Dot(current, direction) < speedThreshold)
                {
                    ResetRunBrakeLeadIn();
                    return;
                }
                if (Vector3.Dot(direction, pendingRunStepDirection) < RunCadenceDirectionDot ||
                    !AdvanceRunCadence(current, speedThreshold, footfallDistance))
                    runCadenceTravel = 0f;
                pendingRunStepStart = body.position;
                pendingRunStepDirection = direction;
                pendingRunStepSeconds = dt;
                return;
            }

            if (pendingRunStepSeconds <= 0f)
                return; // 此次松键窗口已经建立，或刚才没有可用的实际跑步步幅。
            if (AdvanceRunCadence(current, speedThreshold, footfallDistance))
                runBrakeLeadInRemaining = EstimateRunBrakeLeadIn(current, maximum, footfallDistance, leadInDeceleration);
            pendingRunStepSeconds = 0f;
            runCadenceTravel = 0f;
        }

        private bool AdvanceRunCadence(Vector3 current, float speedThreshold, float footfallDistance)
        {
            if (pendingRunStepSeconds <= 0f)
                return false;
            float actualSpeed = Vector3.Dot(current, pendingRunStepDirection);
            float travel = Vector3.Dot(body.position - pendingRunStepStart, pendingRunStepDirection);
            if (actualSpeed < speedThreshold || travel <= 0f || travel < speedThreshold * pendingRunStepSeconds ||
                travel > actualSpeed * pendingRunStepSeconds * 2f)
                return false;
            runCadenceTravel = Mathf.Repeat(runCadenceTravel + travel, footfallDistance);
            return true;
        }

        private float EstimateRunBrakeLeadIn(Vector3 current, float maximum, float footfallDistance, float deceleration)
        {
            float speed = Vector3.Dot(current, pendingRunStepDirection);
            if (runCadenceTravel <= speed * pendingRunStepSeconds)
                return 0f; // 在刚过去的已提交物理步中刚换到新的估算支撑。
            float distance = footfallDistance - runCadenceTravel;
            float projectedDeceleration = deceleration * speed / current.magnitude;
            float discriminant = speed * speed - 2f * projectedDeceleration * distance;
            if (discriminant <= 0f)
                return maximum;
            float contactTime = 2f * distance / (speed + Mathf.Sqrt(discriminant));
            return Mathf.Min(maximum, contactTime);
        }

        private void ResetRunBrakeLeadIn()
        {
            pendingRunStepSeconds = 0f;
            runCadenceTravel = 0f;
            runBrakeLeadInRemaining = 0f;
            pendingBrakeVelocity = Vector3.zero;
        }

        private bool TryLiftOntoStep(Vector3 target, float dt)
        {
            if (stepHeight <= 0f)
                return false;
            Vector3 direction = target.normalized;
            Vector3 scale = capsule.transform.lossyScale;
            float radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            float halfHeight = Mathf.Max(radius, capsule.height * Mathf.Abs(scale.y) * 0.5f);
            Vector3 center = body.position + body.rotation * Vector3.Scale(capsule.center, scale);
            Vector3 foot = center - Vector3.up * halfHeight;
            float skin = Mathf.Max(0.005f, capsule.contactOffset);
            float reach = radius + Mathf.Min(radius, target.magnitude * dt);
            // 低探针找到立面，再从边缘内侧往下找可行走顶面。动态物体继续接受碰撞推力。
            if (!FindStepSurface(foot + Vector3.up * skin * 2f, direction, reach, out RaycastHit face)
                || face.normal.y >= GroundDot || (face.rigidbody != null && !face.rigidbody.isKinematic))
                return false;
            Vector3 probe = foot + direction * (face.distance + skin * 2f) + Vector3.up * (stepHeight + skin * 2f);
            if (!FindStepSurface(probe, Vector3.down, stepHeight + skin * 2f, out RaycastHit top)
                || top.normal.y < GroundDot || (top.rigidbody != null && !top.rigidbody.isKinematic))
                return false;
            float rise = top.point.y - foot.y;
            if (rise <= skin || rise > stepHeight + 0.001f ||
                body.SweepTest(Vector3.up, out _, rise + skin, QueryTriggerInteraction.Ignore))
                return false;
            body.position += Vector3.up * (rise + skin); // 仅抬高已检验的净空，前进交给碰撞求解。
            return true;
        }

        private RaycastHit[] hits = new RaycastHit[4];

        private bool FindStepSurface(Vector3 origin, Vector3 direction, float distance, out RaycastHit nearest)
        {
            int count;
            while ((count = Physics.RaycastNonAlloc(origin, direction, hits, distance, Physics.AllLayers,
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

        public void SetSuspended(bool value)
        {
            if (body == null || value == suspended)
                return;
            suspended = value;
            groundMoveGait = null;
            ResetRunBrakeLeadIn();
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
            groundMoveGait = null;
            ResetRunBrakeLeadIn();
            surfaces.Clear();
            previousDashVelocity = suspendedVelocity = Vector3.zero;
            if (body != null && !body.isKinematic)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }
    }
}
