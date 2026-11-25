using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 查询和素材使用相同时间窗及角色坐标系，水平分量依次为向右、向前。
    public readonly struct CharacterMatchTrajectory
    {
        public const float NearSeconds = 0.15f;
        public const float FarSeconds = 0.30f;
        public readonly Vector2 Near, Far;
        public readonly Vector2 Windows; // 查询与候选共用的近、远窗秒数。
        internal static Vector2 DefaultWindows => new Vector2(NearSeconds, FarSeconds);
        internal readonly Vector3 NearWorld; // 保留预测起点坐标基准中的真实世界位移。
        public readonly float NearYaw, FarYaw;
        public readonly bool Valid;

        /// <summary>near、far 为两个时间窗内相对当前位置的世界位移；转角为相对当前朝向的度数。</summary>
        public CharacterMatchTrajectory(Vector3 near, Vector3 far, Vector3 facing, float nearYaw, float farYaw)
            : this(near, far, facing, nearYaw, farYaw, DefaultWindows) { }

        // 按调用方实际取样的时间窗保存世界位移和相对朝向。
        public CharacterMatchTrajectory(Vector3 near, Vector3 far, Vector3 facing, float nearYaw, float farYaw, Vector2 windows)
        {
            Windows = windows;
            Vector3 forward = Vector3.ProjectOnPlane(facing, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            NearWorld = Vector3.ProjectOnPlane(near, Vector3.up);
            Near = new Vector2(Vector3.Dot(near, right), Vector3.Dot(near, forward));
            Far = new Vector2(Vector3.Dot(far, right), Vector3.Dot(far, forward));
            NearYaw = Mathf.DeltaAngle(0f, nearYaw);
            FarYaw = Mathf.DeltaAngle(0f, farYaw);
            Valid = Windows.x > 0f && Windows.y >= Windows.x && Windows.y < float.PositiveInfinity &&
                forward.sqrMagnitude > 0.9f && Finite(Near, Far, NearYaw, FarYaw);
        }

        // 以发布预测的实际起点朝向记录世界和局部近窗位移。
        internal CharacterMatchTrajectory(in CharacterForecastSample origin, in CharacterForecastSample near,
            in CharacterForecastSample far, Vector2 windows)
        {
            Windows = windows;
            Vector3 forward = Vector3.ProjectOnPlane(origin.Rotation * Vector3.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 a = near.Position - origin.Position, b = far.Position - origin.Position;
            NearWorld = Vector3.ProjectOnPlane(a, Vector3.up);
            Near = new Vector2(Vector3.Dot(a, right), Vector3.Dot(a, forward));
            Far = new Vector2(Vector3.Dot(b, right), Vector3.Dot(b, forward));
            NearYaw = Vector3.SignedAngle(forward, Vector3.ProjectOnPlane(near.Rotation * Vector3.forward, Vector3.up), Vector3.up);
            FarYaw = Vector3.SignedAngle(forward, Vector3.ProjectOnPlane(far.Rotation * Vector3.forward, Vector3.up), Vector3.up);
            Valid = Windows.x > 0f && Windows.y >= Windows.x && Windows.y < float.PositiveInfinity &&
                forward.sqrMagnitude > 0.9f && Finite(Near, Far, NearYaw, FarYaw);
        }

        // 无效轨迹必须走规则回退，避免把非数位置或角度加入候选排序。
        private static bool Finite(Vector2 near, Vector2 far, float nearYaw, float farYaw)
        {
            float magnitude = near.sqrMagnitude + far.sqrMagnitude + Mathf.Abs(nearYaw) + Mathf.Abs(farYaw);
            return !float.IsNaN(magnitude) && !float.IsInfinity(magnitude);
        }
    }
}
