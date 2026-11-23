using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>一次普通地面移动的控制参数。实走和两种预测都用同一速度和偏航积分。</summary>
    internal readonly struct CharacterGroundSteering
    {
        internal readonly bool Active;
        private readonly Vector2 heading;
        private readonly float acceleration, braking, lateralAcceleration;
        private readonly float maxRate, riseAcceleration, settleAcceleration;

        internal CharacterGroundSteering(CharacterInputSnapshot input, in CharacterDriveParameters drive)
        {
            Active = true;
            heading = input.Direction;
            var tuning = drive.Settings;
            acceleration = InputMath.NonNegative(drive.GroundAcceleration, 0f);
            braking = acceleration * Mathf.Clamp(tuning.turnBrakingRatio, 0.01f, 1f);
            lateralAcceleration = acceleration * Mathf.Clamp(tuning.turnLateralRatio, 0.01f, 1f);
            float rise = input.Gait == CharacterMoveGait.Walk ? tuning.walkTurnRise :
                input.Gait == CharacterMoveGait.Sprint ? tuning.sprintTurnRise : tuning.runTurnRise;
            maxRate = drive.TurnSpeed;
            riseAcceleration = maxRate / Mathf.Max(0.01f, rise);
            settleAcceleration = maxRate / Mathf.Max(0.01f, tuning.turnSettle);
        }

        internal Vector3 Velocity(Vector3 current, Vector3 target, float dt)
        {
            if (current.Equals(target)) return target;
            float speed = target.magnitude;
            if (speed < 0.00001f || acceleration <= 0f)
                return Vector3.MoveTowards(current, target, acceleration * dt);
            Vector3 axis = target / speed;
            float along = Vector3.Dot(current, axis);
            Vector3 side = current - axis * along;
            float sideSpeed = side.magnitude;
            float push = Mathf.Lerp(acceleration, lateralAcceleration, Mathf.Clamp01(sideSpeed / speed));
            if (along < 0f) // 反向分量先刹到零，本步剩余时间才用于向前加速。
            {
                float brakeTime = Mathf.Min(dt, -along / braking);
                along = Mathf.MoveTowards(along, 0f, braking * brakeTime);
                along = Mathf.MoveTowards(along, speed, push * (dt - brakeTime));
            }
            else along = Mathf.MoveTowards(along, speed, push * dt);
            if (along == speed && sideSpeed <= braking * dt) return target;
            Vector3 next = axis * along + Vector3.MoveTowards(side, Vector3.zero, braking * dt);
            return Vector3.ClampMagnitude(next, Mathf.Max(speed, current.magnitude)); // 斜切本身不额外增速。
        }

        internal bool SameTuning(in CharacterGroundSteering other) =>
            maxRate == other.maxRate && acceleration == other.acceleration && braking == other.braking &&
            lateralAcceleration == other.lateralAcceleration && riseAcceleration == other.riseAcceleration &&
            settleAcceleration == other.settleAcceleration;

        internal static float AngleTo(Vector3 forward, Vector2 target)
        {
            if (!InputMath.TryNormalizeDirection(new Vector2(forward.x, forward.z), out var current) ||
                !InputMath.TryNormalizeDirection(target, out target)) return 0f;
            double cross = (double)current.y * target.x - (double)current.x * target.y;
            double dot = (double)current.x * target.x + (double)current.y * target.y;
            float error = (float)(Math.Atan2(cross, dot) * Mathf.Rad2Deg);
            return error == -180f ? 180f : error;
        }

        internal float HeadingError(Vector3 forward) => AngleTo(forward, heading);

        internal float Turn(float error, ref float yawRate, float dt)
        {
            if (maxRate <= 0f || Mathf.Abs(error) < 0.0001f)
            {
                yawRate = 0f;
                return 0f;
            }
            if (Mathf.Abs(error) > 175f && yawRate * error < 0f)
                error -= Mathf.Sign(error) * 360f; // 接近正反向时沿用已经起转的一侧。
            float sign = Mathf.Sign(error);
            float current = Mathf.Clamp(yawRate * sign, -maxRate, maxRate);
            // 为本步的梯形积分和后续制动同时留出角度，接近目标时连续降速。
            float halfBrake = settleAcceleration * dt * 0.5f;
            float distance = Mathf.Max(0f, Mathf.Abs(error) - current * dt * 0.5f);
            float goal = Mathf.Min(maxRate, Mathf.Sqrt(halfBrake * halfBrake + 2f * settleAcceleration * distance) - halfBrake);
            float next = Mathf.MoveTowards(current, goal, (current < goal ? riseAcceleration : settleAcceleration) * dt);
            float travel = (current + next) * dt * 0.5f;
            if (travel >= Mathf.Abs(error))
            {
                yawRate = 0f;
                return error;
            }
            yawRate = next * sign;
            return travel * sign;
        }
    }
}
