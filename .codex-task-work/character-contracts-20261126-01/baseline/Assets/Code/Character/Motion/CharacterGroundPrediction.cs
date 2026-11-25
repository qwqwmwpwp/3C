using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存指定时间窗的世界速度、两个累计世界位移与相对起始朝向的偏航角。
    internal readonly struct CharacterGroundSamples
    {
        internal readonly Vector3 Velocity, NearTravel, FarTravel;
        internal readonly float NearYaw, FarYaw;

        // 记录从同一起点按三个时间窗采样的结果。
        internal CharacterGroundSamples(Vector3 velocity, Vector3 nearTravel, Vector3 farTravel, float nearYaw, float farYaw)
        {
            Velocity = velocity;
            NearTravel = nearTravel;
            FarTravel = farTravel;
            NearYaw = nearYaw;
            FarYaw = farYaw;
        }
    }

    // 从一次地面求解的末状态和实际驱动参数，按调用方给定的时长推算运动。
    internal readonly struct CharacterGroundPrediction
    {
        internal readonly Vector3 Velocity, Facing;
        private readonly Vector3 target;
        private readonly float acceleration, turnRate, initialYawRate, fixedStep;
        private readonly CharacterGroundSteering steering;
        private readonly CharacterGroundBrake brake;

        // 锁定求解后的速度和朝向，制动参数取本步已消耗轻制动时间后的值。
        internal CharacterGroundPrediction(Vector3 velocity, Vector3 target, float acceleration, Vector3 facing,
            float turnRate, CharacterGroundSteering steering, float yawRate, float fixedStep, CharacterGroundBrake brake = default)
        {
            Velocity = velocity;
            Facing = facing;
            this.target = target;
            this.acceleration = acceleration;
            this.turnRate = turnRate;
            this.steering = steering;
            initialYawRate = yawRate;
            this.fixedStep = fixedStep;
            this.brake = brake;
        }

        // 从求解末端起采样，速度时刻和近端时刻均在零至远端时长之间，单位秒。
        internal CharacterGroundSamples Sample(float speedTime, float nearTime, float farTime)
        {
            if (brake.IsActive)
                return new CharacterGroundSamples(brake.Apply(Velocity, speedTime), brake.Travel(Velocity, nearTime, fixedStep),
                    brake.Travel(Velocity, farTime, fixedStep), 0f, 0f);
            if (steering.Active) return PredictSteering(speedTime, nearTime, farTime);

            float angle = CharacterGroundSteering.AngleTo(Facing, new Vector2(target.x, target.z));
            return new CharacterGroundSamples(Vector3.MoveTowards(Velocity, target, acceleration * speedTime),
                PredictTravel(Velocity, target, acceleration, nearTime), PredictTravel(Velocity, target, acceleration, farTime),
                Mathf.Clamp(angle, -turnRate * nearTime, turnRate * nearTime), Mathf.Clamp(angle, -turnRate * farTime, turnRate * farTime));
        }

        // 按同一转向积分前进到远端，途中记录指定时刻的速度、近端位移和累计转角。
        private CharacterGroundSamples PredictSteering(float speedTime, float nearTime, float farTime)
        {
            Vector3 velocity = Velocity;
            float yawRate = initialYawRate;
            float goal = steering.HeadingError(Facing);
            Vector3 position = Vector3.zero, near = Vector3.zero;
            float yaw = 0f, nearYaw = 0f, time = 0f;
            Vector3 futureVelocity = velocity;
            while (time < farTime)
            {
                Vector3 next = steering.Velocity(velocity, target, fixedStep);
                float turn = steering.Turn(Mathf.DeltaAngle(yaw, goal), ref yawRate, fixedStep);
                if (next.Equals(velocity) && turn == 0f && yawRate == 0f)
                {
                    if (time < speedTime) futureVelocity = velocity;
                    if (time < nearTime) { near = position + velocity * (nearTime - time); nearYaw = yaw; }
                    position += velocity * (farTime - time);
                    break;
                }
                float end = time + fixedStep;
                if (time < speedTime && end >= speedTime)
                    futureVelocity = Vector3.Lerp(velocity, next, (speedTime - time) / fixedStep);
                if (time < nearTime && end >= nearTime)
                {
                    near = position + next * (nearTime - time);
                    nearYaw = yaw + turn * ((nearTime - time) / fixedStep);
                }
                float duration = Mathf.Min(fixedStep, farTime - time);
                position += next * duration;
                yaw += turn * (duration / fixedStep);
                velocity = next;
                time = end;
            }
            return new CharacterGroundSamples(futureVelocity, near, position, nearYaw, yaw);
        }

        // 以匀加速到目标再保持速度的规则积分给定时长内的世界位移。
        internal static Vector3 PredictTravel(Vector3 current, Vector3 target, float acceleration, float horizon)
        {
            if (acceleration <= 0f) return current * horizon;
            float changing = Mathf.Min(horizon, (target - current).magnitude / acceleration);
            Vector3 end = Vector3.MoveTowards(current, target, acceleration * changing);
            return (current + end) * (changing * 0.5f) + end * (horizon - changing);
        }
    }
}
