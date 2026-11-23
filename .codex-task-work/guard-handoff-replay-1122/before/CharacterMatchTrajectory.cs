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

        internal CharacterMatchTrajectory(Vector3 near, Vector3 far, Vector3 facing, Vector3 target, float turnRate)
        {
            Vector3 forward = Vector3.ProjectOnPlane(facing, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Valid = forward.sqrMagnitude > 0.9f;
            Near = new Vector2(Vector3.Dot(near, right), Vector3.Dot(near, forward));
            Far = new Vector2(Vector3.Dot(far, right), Vector3.Dot(far, forward));
            float angle = target.sqrMagnitude > 0.000001f ? Vector3.SignedAngle(forward,
                Vector3.ProjectOnPlane(target, Vector3.up), Vector3.up) : 0f;
            if (angle <= -179.999f) angle = 180f;
            NearYaw = Mathf.Clamp(angle, -turnRate * NearSeconds, turnRate * NearSeconds);
            FarYaw = Mathf.Clamp(angle, -turnRate * FarSeconds, turnRate * FarSeconds);
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
