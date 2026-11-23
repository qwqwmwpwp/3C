using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal readonly struct CharacterDriveParameters
    {
        internal readonly CharacterMotorSettings Settings;
        internal readonly float GroundAcceleration, TurnSpeed, FootfallDistance, Drag;
        internal readonly Vector3 Gravity;
        internal readonly bool UseGravity;

        internal CharacterDriveParameters(CharacterMotorSettings settings, float acceleration, float turnSpeed,
            float footfallDistance, bool useGravity, Vector3 gravity, float drag)
        {
            Settings = settings;
            GroundAcceleration = acceleration;
            TurnSpeed = turnSpeed;
            FootfallDistance = footfallDistance;
            UseGravity = useGravity;
            Gravity = gravity;
            Drag = drag;
        }
    }

    /// <summary>角色与预测共用的单步速度、步幅和朝向计算。复制值即可独立推演。</summary>
    internal struct CharacterMotorStep
    {
        private const float RunCadenceDirectionDot = 0.7f;
        private Vector3 previousDashVelocity;
        private CharacterMoveGait? groundMoveGait;
        private Vector3 pendingRunStepStart;
        private Vector3 pendingRunStepDirection;
        private float pendingRunStepSeconds;
        private float runCadenceTravel;
        private float runBrakeLeadInRemaining;
        private Vector3 pendingBrakeVelocity;
        private float yawRate; // 世界竖直轴角速度（度/秒），随物理状态一起复制给预测器。

        internal void ResetStride()
        {
            yawRate = 0f;
            groundMoveGait = null;
            ResetRunBrakeLeadIn();
        }

        internal static Vector3 Target(CharacterBehaviorFrameResult frame, float dt) =>
            new Vector3(frame.Displacement.x, 0f, frame.Displacement.y) / dt;

        internal static Vector3 LimitOnSteepSurface(Vector3 target, Vector3 steep)
        {
            Vector3 downhill = Vector3.ProjectOnPlane(steep, Vector3.up).normalized;
            return target - downhill * Mathf.Min(0f, Vector3.Dot(target, downhill));
        }

        internal CharacterGroundDrive Advance(CharacterBehaviorFrameResult frame, CharacterInputSnapshot input, float dt,
            Vector3 position, ref Vector3 velocity, Vector3 target, Vector3 ground, CollisionFlags contacts, bool supported,
            bool stepped, in CharacterDriveParameters drive, ref Quaternion rotation)
        {
            var settings = drive.Settings;
            bool dash = frame.DisplacementSource == CharacterDisplacementSource.Dash;
            Vector3 requested = Target(frame, dt);
            // 仅扣除冲刺前向残速，保留侧向冲量，避免撞墙后反向拉回。
            if ((!dash || frame.DashStarted) && previousDashVelocity.sqrMagnitude > 0f)
            {
                Vector3 axis = previousDashVelocity.normalized;
                float excess = Vector3.Dot(velocity - requested, axis);
                velocity -= axis * Mathf.Clamp(excess, 0f, previousDashVelocity.magnitude);
            }

            Vector3 plane = supported && !stepped ? ground : Vector3.up;
            target = Vector3.ProjectOnPlane(target, plane).normalized * target.magnitude;
            Vector3 current = Vector3.ProjectOnPlane(velocity, plane);
            float change = supported ? drive.GroundAcceleration : settings.airAcceleration;
            if (supported && frame.ActionMotion.Braking > 0f) change = frame.ActionMotion.Braking;
            bool braking = supported && input.Direction == Vector2.zero &&
                frame.DisplacementProvider == CharacterBehaviorId.None && groundMoveGait.HasValue;
            if (supported && (frame.MoveApproved || frame.ActionMotion.RecoversMovement && target != Vector3.zero))
                groundMoveGait = input.Gait;
            else if (braking)
            {
                switch (groundMoveGait.Value) // 按最后的主动档位和刚体实速制动。
                {
                    case CharacterMoveGait.Walk: change = settings.walkBraking; break;
                    case CharacterMoveGait.Sprint: change = settings.sprintBraking; break;
                    default: change = settings.runBraking; break;
                }
                change = Mathf.Max(0.01f, InputMath.NonNegative(change, drive.GroundAcceleration));
            }
            else
                groundMoveGait = null;
            float leadInDeceleration = Mathf.Min(change,
                Mathf.Max(0.01f, InputMath.NonNegative(settings.runBrakeLeadInDeceleration, 8f)));
            UpdateRunBrakeLeadIn(position, contacts, drive, frame, input, current, target, dt,
                supported && !stepped, braking, leadInDeceleration);
            CharacterGroundBrake brake = braking
                ? new CharacterGroundBrake(change, leadInDeceleration, runBrakeLeadInRemaining) : default;
            bool ordinary = supported && frame.MoveApproved && frame.DisplacementProvider == CharacterBehaviorId.Move &&
                !frame.ActionMotion.ControlsFacing && !frame.DashStarted;
            var steering = ordinary ? new CharacterGroundSteering(input, drive) : default;
            AdvanceFacing(ref rotation, frame, input, steering, drive.TurnSpeed, dt);
            Vector3 next;
            if (brake.IsActive) next = brake.Apply(current, dt);
            else if (dash) next = target;
            else if (ordinary) next = steering.Velocity(current, target, dt);
            else next = Vector3.MoveTowards(current, target, change * dt);
            runBrakeLeadInRemaining = brake.AfterStep(dt).LeadInSeconds;
            pendingBrakeVelocity = runBrakeLeadInRemaining > 0f ? next : Vector3.zero;
            if (!frame.MoveApproved && next.sqrMagnitude < 0.000001f)
            {
                groundMoveGait = null;
                ResetRunBrakeLeadIn();
            }
            if (supported || frame.DisplacementProvider != CharacterBehaviorId.None)
                velocity += next - current;
            if (supported && !stepped && drive.UseGravity) // 只抵消沿坡重力，保留贴地的法向重力。
                velocity -= Vector3.ProjectOnPlane(drive.Gravity, plane) * dt;
            previousDashVelocity = dash ? target : Vector3.zero;
            return supported && !stepped && (frame.MoveApproved || frame.ActionMotion.RecoversMovement || braking)
                ? new CharacterGroundDrive(plane, target, change, current, brake, steering, yawRate) : default;
        }

        private void UpdateRunBrakeLeadIn(Vector3 position, CollisionFlags contacts, in CharacterDriveParameters drive,
            CharacterBehaviorFrameResult frame, CharacterInputSnapshot input,
            Vector3 current, Vector3 target, float dt, bool grounded, bool braking, float leadInDeceleration)
        {
            var settings = drive.Settings;
            bool running = frame.DisplacementProvider == CharacterBehaviorId.Move && frame.MoveApproved &&
                input.Gait == CharacterMoveGait.Run;
            bool runBrakingActive = braking && groundMoveGait == CharacterMoveGait.Run;
            float maximum = Mathf.Min(CharacterMotorSettings.MaxLeadInSeconds, InputMath.NonNegative(settings.runBrakeLeadInSeconds, 0f));
            if (maximum <= 0f || !grounded || (!running && !runBrakingActive))
            {
                ResetRunBrakeLeadIn();
                return;
            }

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

            float speedThreshold = InputMath.NonNegative(settings.runBrakeLeadInMinSpeed, 3.5f);
            float manualDistance = InputMath.NonNegative(settings.runBrakeLeadInFootfallDistance, 0f);
            float footfallDistance = Mathf.Max(0.01f, manualDistance > 0f ? manualDistance : drive.FootfallDistance);
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
                    !AdvanceRunCadence(position, current, speedThreshold, footfallDistance))
                    runCadenceTravel = 0f;
                pendingRunStepStart = position;
                pendingRunStepDirection = direction;
                pendingRunStepSeconds = dt;
                return;
            }

            if (pendingRunStepSeconds <= 0f)
                return; // 此次松键窗口已经建立，或刚才没有可用的实际跑步步幅。
            if (AdvanceRunCadence(position, current, speedThreshold, footfallDistance))
                runBrakeLeadInRemaining = EstimateRunBrakeLeadIn(current, maximum, footfallDistance, leadInDeceleration);
            pendingRunStepSeconds = 0f;
            runCadenceTravel = 0f;
        }

        private bool AdvanceRunCadence(Vector3 position, Vector3 current, float speedThreshold, float footfallDistance)
        {
            if (pendingRunStepSeconds <= 0f)
                return false;
            float actualSpeed = Vector3.Dot(current, pendingRunStepDirection);
            float travel = Vector3.Dot(position - pendingRunStepStart, pendingRunStepDirection);
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

        private void AdvanceFacing(ref Quaternion rotation, CharacterBehaviorFrameResult frame, CharacterInputSnapshot input,
            in CharacterGroundSteering steering, float turnSpeed, float dt)
        {
            Vector3 forward = rotation * Vector3.forward;
            float angle;
            if (steering.Active)
                angle = steering.Turn(steering.HeadingError(forward), ref yawRate, dt);
            else
            {
                yawRate = 0f;
                if (frame.DashStarted) angle = CharacterGroundSteering.AngleTo(forward, frame.DashDirection);
                else if (frame.ActionMotion.ControlsFacing)
                    angle = LimitedTurn(forward, frame.ActionMotion.Facing, frame.ActionMotion.MaxTurn);
                else if (frame.MoveApproved) angle = LimitedTurn(forward, input.Direction, turnSpeed * dt);
                else return;
            }
            if (angle != 0f) rotation = Quaternion.AngleAxis(angle, Vector3.up) * rotation;
        }

        private static float LimitedTurn(Vector3 forward, Vector2 target, float limit) =>
            limit > 0f ? Mathf.Clamp(CharacterGroundSteering.AngleTo(forward, target), -limit, limit) : 0f;
    }
}
