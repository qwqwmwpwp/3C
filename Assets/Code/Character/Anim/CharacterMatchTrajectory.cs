using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 查询和素材使用相同时间窗及角色坐标系，水平分量依次为向右、向前。
    public readonly struct CharacterMatchTrajectory
    {
        public const float NearSeconds = 0.15f;
        public const float FarSeconds = 0.30f;
        public readonly Vector2 Near, Far;
        public readonly float NearYaw, FarYaw;
        public readonly bool Valid;

        /// <summary>near、far 为两个时间窗内相对当前位置的世界位移；转角为相对当前朝向的度数。</summary>
        public CharacterMatchTrajectory(Vector3 near, Vector3 far, Vector3 facing, float nearYaw, float farYaw)
        {
            Vector3 forward = Vector3.ProjectOnPlane(facing, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Valid = forward.sqrMagnitude > 0.9f;
            Near = new Vector2(Vector3.Dot(near, right), Vector3.Dot(near, forward));
            Far = new Vector2(Vector3.Dot(far, right), Vector3.Dot(far, forward));
            NearYaw = Mathf.DeltaAngle(0f, nearYaw);
            FarYaw = Mathf.DeltaAngle(0f, farYaw);
        }

        internal CharacterMatchTrajectory(in CharacterForecastSample origin, in CharacterForecastSample near,
            in CharacterForecastSample far)
        {
            Vector3 forward = Vector3.ProjectOnPlane(origin.Rotation * Vector3.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 a = near.Position - origin.Position, b = far.Position - origin.Position;
            Near = new Vector2(Vector3.Dot(a, right), Vector3.Dot(a, forward));
            Far = new Vector2(Vector3.Dot(b, right), Vector3.Dot(b, forward));
            NearYaw = Vector3.SignedAngle(forward, Vector3.ProjectOnPlane(near.Rotation * Vector3.forward, Vector3.up), Vector3.up);
            FarYaw = Vector3.SignedAngle(forward, Vector3.ProjectOnPlane(far.Rotation * Vector3.forward, Vector3.up), Vector3.up);
            Valid = forward.sqrMagnitude > 0.9f;
        }

        internal static CharacterMatchTrajectory PredictSteering(in CharacterGroundSteering steering, Vector3 velocity, Vector3 target, Vector3 facing,
            float yawRate, float fixedStep, out Vector3 futureVelocity)
        {
            float goal = steering.HeadingError(facing);
            Vector3 position = Vector3.zero, near = Vector3.zero;
            float yaw = 0f, nearYaw = 0f, time = 0f;
            float farTime = FarSeconds, nearTime = NearSeconds;
            futureVelocity = velocity;
            while (time < farTime)
            {
                Vector3 next = steering.Velocity(velocity, target, fixedStep);
                float turn = steering.Turn(Mathf.DeltaAngle(yaw, goal), ref yawRate, fixedStep);
                if (next.Equals(velocity) && turn == 0f && yawRate == 0f)
                {
                    if (time < CharacterGroundTrajectory.LookAheadSeconds) futureVelocity = velocity;
                    if (time < nearTime) { near = position + velocity * (nearTime - time); nearYaw = yaw; }
                    position += velocity * (farTime - time);
                    break;
                }
                float end = time + fixedStep;
                if (time < CharacterGroundTrajectory.LookAheadSeconds && end >= CharacterGroundTrajectory.LookAheadSeconds)
                    futureVelocity = Vector3.Lerp(velocity, next, (CharacterGroundTrajectory.LookAheadSeconds - time) / fixedStep);
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
            return new CharacterMatchTrajectory(near, position, facing, nearYaw, yaw);
        }

        internal static Vector3 PredictTravel(Vector3 current, Vector3 target, float acceleration, float horizon)
        {
            if (acceleration <= 0f) return current * horizon;
            float changing = Mathf.Min(horizon, (target - current).magnitude / acceleration);
            Vector3 end = Vector3.MoveTowards(current, target, acceleration * changing);
            return (current + end) * (changing * 0.5f) + end * (horizon - changing);
        }
    }
}
