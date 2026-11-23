using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
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

        internal void ResetStride()
        {
            groundMoveGait = null;
            ResetRunBrakeLeadIn();
        }

        internal static Vector3 Target(CharacterBehaviorFrameResult frame, float dt) =>
            new Vector3(frame.Displacement.x, 0f, frame.Displacement.y) / dt;

        internal CharacterGroundDrive Advance(CharacterBehaviorFrameResult frame, CharacterInputSnapshot input, float dt,
            Vector3 position, ref Vector3 velocity, Vector3 target, Vector3 ground, CollisionFlags contacts, bool supported,
            bool stepped, CharacterMotorSettings settings, float groundAcceleration, float bakedFootfallDistance,
            bool useGravity, Vector3 gravity)
        {
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
            float change = supported ? groundAcceleration : settings.airAcceleration;
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
                change = Mathf.Max(0.01f, InputMath.NonNegative(change, groundAcceleration));
            }
            else
                groundMoveGait = null;
            float leadInDeceleration = Mathf.Min(change,
                Mathf.Max(0.01f, InputMath.NonNegative(settings.runBrakeLeadInDeceleration, 8f)));
            UpdateRunBrakeLeadIn(position, contacts, settings, bakedFootfallDistance, frame, input, current, target, dt, supported && !stepped, braking, leadInDeceleration);
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
            if (supported && !stepped && useGravity) // 只抵消沿坡重力，保留贴地的法向重力。
                velocity -= Vector3.ProjectOnPlane(gravity, plane) * dt;
            previousDashVelocity = dash ? target : Vector3.zero;
            return supported && !stepped && (frame.MoveApproved || frame.ActionMotion.RecoversMovement || braking)
                ? new CharacterGroundDrive(plane, target, change, current, brake) : default;
        }

        private void UpdateRunBrakeLeadIn(Vector3 position, CollisionFlags contacts, CharacterMotorSettings settings,
            float bakedFootfallDistance, CharacterBehaviorFrameResult frame, CharacterInputSnapshot input,
            Vector3 current, Vector3 target, float dt, bool grounded, bool braking, float leadInDeceleration)
        {
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
            float footfallDistance = Mathf.Max(0.01f, manualDistance > 0f ? manualDistance : bakedFootfallDistance);
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

        internal static Quaternion Facing(Quaternion rotation, CharacterBehaviorFrameResult frame, CharacterInputSnapshot input,
            float turnSpeed, float deltaTime)
        {
            Vector2 targetFacing;
            float maximumAngle;
            if (frame.DashStarted)
            {
                targetFacing = frame.DashDirection;
                maximumAngle = 180f;
            }
            else if (frame.ActionMotion.ControlsFacing)
            {
                targetFacing = frame.ActionMotion.Facing;
                maximumAngle = frame.ActionMotion.MaxTurn;
            }
            else if (frame.MoveApproved)
            {
                targetFacing = input.Direction;
                maximumAngle = (float)Math.Min(180d, (double)turnSpeed * deltaTime);
            }
            else
            {
                return rotation; // 冲刺后续步和静止时保持朝向。
            }

            Vector3 forward = rotation * Vector3.forward;
            if (maximumAngle <= 0f ||
                !InputMath.TryNormalizeDirection(new Vector2(forward.x, forward.z), out Vector2 current) ||
                !InputMath.TryNormalizeDirection(targetFacing, out Vector2 target))
                return rotation;

            double cross = (double)current.y * target.x - (double)current.x * target.y;
            double dot = (double)current.x * target.x + (double)current.y * target.y;
            float angle = (float)(Math.Atan2(cross, dot) * (180d / Math.PI));
            if (angle == -180f)
                angle = 180f; // 正反向等距时固定绕世界 +Y 转动。

            float step = Mathf.Clamp(angle, -maximumAngle, maximumAngle);
            return Quaternion.AngleAxis(step, Vector3.up) * rotation;
        }
    }
}
