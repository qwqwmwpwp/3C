using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>水平主动速度必须同时满足每个陡坡的约束。预测起点按值复制，额外坡面的数组只读。</summary>
    internal struct CharacterSlopeContacts
    {
        private Vector3 first, second;
        private Vector3[] extra;
        private int count;

        internal void Add(Vector3 normal)
        {
            if (normal.y <= 0f) return; // 竖直墙和顶面由碰撞求解器处理。
            var direction = new Vector3(normal.x, 0f, normal.z).normalized;
            if (direction != Vector3.zero) AddDirection(direction);
        }

        internal void Include(in CharacterSlopeContacts other)
        {
            for (int i = 0; i < other.count; i++) AddDirection(other.At(i));
        }

        private Vector3 At(int index) => index == 0 ? first : index == 1 ? second : extra[index - 2];

        private void AddDirection(Vector3 direction)
        {
            for (int i = 0; i < count; i++) if (At(i) == direction) return;
            if (count == 0) first = direction;
            else if (count == 1) second = direction;
            else
            {
                var next = new Vector3[count - 1];
                extra?.CopyTo(next, 0);
                next[count - 2] = direction;
                extra = next;
            }
            count++;
        }

        // 将合成目标约束到可行区域，并对动作速度分量施加同一边界投影。
        internal Vector3 Limit(Vector3 target, ref Vector3 action)
        {
            if (count == 0) return target;
            if (count == 1)
            {
                if (Vector3.Dot(target, first) >= 0f) return target;
                action = AlongSurface(action, first);
                return AlongSurface(target, first);
            }
            if (Allows(target)) return target;
            // 二维可行区域是过原点的锥。最近可行速度位于某条边界上，或只剩竖直分量。
            var best = new Vector3(0f, target.y, 0f);
            float error = (target - best).sqrMagnitude;
            int boundary = -1;
            for (int i = 0; i < count; i++)
            {
                var normal = At(i);
                var candidate = AlongSurface(target, normal);
                float distance = (candidate - target).sqrMagnitude;
                if (distance < error && Allows(candidate, i))
                {
                    best = candidate;
                    error = distance;
                    boundary = i;
                }
            }
            action = boundary >= 0 ? AlongSurface(action, At(boundary)) : Vector3.zero;
            return best;
        }

        private static Vector3 AlongSurface(Vector3 target, Vector3 normal)
        {
            double tangentSpeed = ((double)target.z * normal.x - (double)target.x * normal.z) /
                ((double)normal.x * normal.x + (double)normal.z * normal.z);
            return new Vector3((float)(-normal.z * tangentSpeed), target.y, (float)(normal.x * tangentSpeed));
        }

        private bool Allows(Vector3 velocity, int ownBoundary = -1)
        {
            double tolerance = (System.Math.Abs((double)velocity.x) + System.Math.Abs((double)velocity.z)) * 1e-7;
            for (int i = 0; i < count; i++)
            {
                if (i == ownBoundary) continue;
                var normal = At(i);
                if ((double)velocity.x * normal.x + (double)velocity.z * normal.z < -tolerance) return false;
            }
            return true;
        }
    }

    internal readonly struct CharacterDriveParameters
    {
        internal readonly CharacterMotorSettings Settings;
        internal readonly float GroundAcceleration, TurnSpeed, FootfallDistance, Drag;
        internal readonly Vector3 Gravity;
        internal readonly bool UseGravity;

        internal CharacterDriveParameters Snapshot() => new CharacterDriveParameters(Settings.Snapshot(), GroundAcceleration,
            TurnSpeed, FootfallDistance, UseGravity, Gravity, Drag);

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
        private Vector3 previousBurstVelocity;
        private CharacterMoveGait? groundMoveGait;
        private Vector3 pendingRunStepStart;
        private Vector3 pendingRunStepDirection;
        private float pendingRunStepSeconds;
        private float runCadenceTravel;
        private float runBrakeLeadInRemaining;
        private Vector3 pendingBrakeVelocity;
        private float yawRate; // 世界竖直轴角速度（度/秒），随物理状态一起复制给预测器。

        private Vector3 submittedMean, terminalVelocity;
        private bool hasMixedStep;

        // 物理求解器收到整步平均速度，下一步从段末速度接回，沿障碍已削去的速度不再补回。
        internal Vector3 ResumeVelocity(Vector3 solved)
        {
            if (!hasMixedStep) return solved;
            hasMixedStep = false;
            Vector3 correction = terminalVelocity - submittedMean;
            Vector3 lost = submittedMean - solved;
            if (lost.sqrMagnitude > 0.000001f)
            {
                Vector3 axis = lost.normalized;
                float removedAtEnd = Vector3.Dot(terminalVelocity, axis);
                float measured = Vector3.Dot(solved, axis);
                float wanted = Vector3.Dot(solved + correction, axis);
                float limited = Mathf.Clamp(wanted, Mathf.Min(removedAtEnd, measured), Mathf.Max(removedAtEnd, measured));
                correction += axis * (limited - wanted);
            }
            return solved + correction;
        }

        // 暂存多段速度的提交均值和末值；单段步无需作差。
        internal void RecordSubmittedVelocity(Vector3 mean, Vector3 terminal, bool mixed)
        {
            submittedMean = mean;
            terminalVelocity = terminal;
            hasMixedStep = mixed;
        }

        // 中断连续步幅观测，保留已消费的动作速度。
        internal void ResetStride()
        {
            yawRate = 0f;
            groundMoveGait = null;
            ResetRunBrakeLeadIn();
        }

        // 法线朝上且沿法线的分离速度低于 0.5 米/秒时，本步仍受地面支撑。
        internal static bool IsGroundSupported(Vector3 velocity, Vector3 ground) =>
            ground.y > 0f && Vector3.Dot(velocity, ground) < 0.5f;

        // 击退在受支撑时衰减切向速度，腾空时保留重力和外部冲量。
        internal static void AdvanceKnockback(ref Vector3 velocity, Vector3 ground, float braking, float seconds)
        {
            if (!IsGroundSupported(velocity, ground)) return;
            Vector3 planar = Vector3.ProjectOnPlane(velocity, ground);
            velocity += Vector3.MoveTowards(planar, Vector3.zero, braking * seconds) - planar;
        }

        // 仅在仍受支撑时向行为提供世界 XZ 平面速度。
        internal static Vector2 GroundVelocity(Vector3 velocity, Vector3 ground) =>
            IsGroundSupported(velocity, ground) ? new Vector2(velocity.x, velocity.z) : default;

        internal static Vector3 Target(CharacterBehaviorFrameResult frame, float dt) =>
            new Vector3(frame.Displacement.x, 0f, frame.Displacement.y) / dt;

        // 共享实走和推演的地面驱动，分别处理合成目标和需要跨步清理的动作余速。
        internal CharacterGroundDrive Advance(CharacterBehaviorFrameResult frame, CharacterInputSnapshot input, float dt,
            Vector3 position, ref Vector3 velocity, in CharacterSlopeContacts steep, Vector3 ground, CollisionFlags contacts, bool supported,
            bool stepped, in CharacterDriveParameters drive, ref Quaternion rotation)
        {
            var settings = drive.Settings;
            var motion = frame.ActionMotion;
            bool burst = motion.UsesBurstVelocity;
            Vector3 requested = Target(frame, dt);
            // 只沿动作的有效速度清理余速，保留普通运动和外部侧向冲量。
            if ((!burst || frame.ActionMotionStarted) && previousBurstVelocity.sqrMagnitude > 0f)
            {
                Vector3 axis = previousBurstVelocity.normalized;
                float excess = Vector3.Dot(velocity - requested, axis);
                velocity -= axis * Mathf.Clamp(excess, 0f, previousBurstVelocity.magnitude);
            }

            var action = burst ? new Vector3(frame.ActionDisplacement.x, 0f, frame.ActionDisplacement.y) / dt : Vector3.zero;
            Vector3 constrained = stepped ? requested : steep.Limit(requested, ref action);
            Vector3 plane = supported && !stepped ? ground : Vector3.up;
            Vector3 projected = Vector3.ProjectOnPlane(constrained, plane);
            Vector3 target = projected.normalized * constrained.magnitude;
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
                !motion.ControlsFacing;
            var steering = ordinary ? new CharacterGroundSteering(input, drive) : default;
            float angle = AdvanceFacing(rotation * Vector3.forward, frame, input, steering, drive.TurnSpeed, dt);
            if (angle != 0f) rotation = Quaternion.AngleAxis(angle, Vector3.up) * rotation;
            Vector3 next;
            if (brake.IsActive) next = brake.Apply(current, dt);
            else if (burst) next = target;
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
            previousBurstVelocity = burst ? AppliedBurstVelocity(action, constrained, projected, target, plane) : Vector3.zero;
            return supported && !stepped && (frame.MoveApproved || frame.ActionMotion.RecoversMovement || braking)
                ? new CharacterGroundDrive(plane, target, change, current, brake, steering, yawRate) : default;
        }

        // 使用合成目标的投影比例计算动作分量，扣除量以实际施加的同向速度为上限。
        private static Vector3 AppliedBurstVelocity(Vector3 action, Vector3 constrained, Vector3 projected, Vector3 target,
            Vector3 plane)
        {
            if (action.Equals(constrained)) return target;
            if (target == Vector3.zero) return Vector3.zero;
            Vector3 planar = Vector3.ProjectOnPlane(action, plane);
            Vector3 axis = planar.normalized;
            float speed = planar.magnitude * constrained.magnitude / projected.magnitude;
            return axis * Mathf.Clamp(Vector3.Dot(target, axis), 0f, speed);
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

        // 先应用动作的起步朝向，否则按普通移动获准的时间计算转向。
        internal float AdvanceFacing(Vector3 forward, CharacterBehaviorFrameResult frame, CharacterInputSnapshot input,
            in CharacterGroundSteering steering, float turnSpeed, float dt)
        {
            var motion = frame.ActionMotion;
            if (frame.ActionMotionStarted && motion.SnapsFacingOnStart)
            {
                yawRate = 0f;
                return CharacterGroundSteering.AngleTo(forward, motion.Facing);
            }

            float movementSeconds = motion.UsesBurstVelocity ? frame.OrdinaryMovementSeconds : dt;
            if (steering.Active)
                return steering.Turn(steering.HeadingError(forward), ref yawRate, movementSeconds);

            yawRate = 0f;
            if (motion.ControlsFacing)
                return LimitedTurn(forward, motion.Facing, motion.MaxTurn);
            return frame.MoveApproved ? LimitedTurn(forward, input.Direction, turnSpeed * movementSeconds) : 0f;
        }

        private static float LimitedTurn(Vector3 forward, Vector2 target, float limit) =>
            limit > 0f ? Mathf.Clamp(CharacterGroundSteering.AngleTo(forward, target), -limit, limit) : 0f;
    }
}
