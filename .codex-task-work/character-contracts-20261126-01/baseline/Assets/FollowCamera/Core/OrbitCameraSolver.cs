using UnityEngine;

namespace ThreeC.FollowCamera.Core
{
    /// <summary>独立保存偏航、俯仰、回正目标及遮挡距离，只根据传入数值求解。</summary>
    public sealed class OrbitCameraSolver
    {
        private const float RecenterResponse = 12f;
        private const float RecenterTolerance = 0.1f;
        private const float DistanceTolerance = 0.001f;

        private float yaw;
        private float pitch;
        private float recenterYaw;
        private bool recentering;
        private float distance;
        private bool hasDistance;

        /// <summary>用目标朝向和配置的初始俯角建立相机自由视角。角度单位为度。</summary>
        public void Initialize(Vector3 facing, float initialPitch, float minimumPitch, float maximumPitch)
        {
            yaw = GetYaw(facing, 0f);
            pitch = ClampPitch(initialPitch, minimumPitch, maximumPitch);
            CutFollow();
        }

        /// <summary>清除过渡状态。传送时保留玩家当前偏航和俯仰。</summary>
        public void CutFollow()
        {
            CancelRecenter();
            hasDistance = false;
        }

        /// <summary>保留当前观察角，取消未完成的手动回正。</summary>
        public void CancelRecenter() => recentering = false;

        /// <summary>应用本帧鼠标角增量，或向按键当帧捕获的偏航回正。</summary>
        public void StepOrbit(Vector2 orbitDegrees, bool recenterPressed, Vector3 facing,
            float minimumPitch, float maximumPitch, float recenterSpeed, float deltaTime)
        {
            pitch = ClampPitch(pitch + orbitDegrees.y, minimumPitch, maximumPitch);
            if (orbitDegrees.x != 0f || orbitDegrees.y != 0f)
            {
                yaw = Mathf.Repeat(yaw + orbitDegrees.x, 360f);
                recentering = false;
                return;
            }

            if (recenterPressed)
            {
                recenterYaw = GetYaw(facing, yaw);
                recentering = true;
            }

            if (!recentering || deltaTime <= 0f)
                return;

            float remaining = Mathf.DeltaAngle(yaw, recenterYaw);
            if (Mathf.Abs(remaining) <= RecenterTolerance)
            {
                yaw = recenterYaw;
                recentering = false;
                return;
            }

            float maxStep = Mathf.Max(0f, recenterSpeed) * deltaTime;
            float step = remaining * (1f - Mathf.Exp(-RecenterResponse * deltaTime));
            yaw = Mathf.Repeat(yaw + Mathf.Clamp(step, -maxStep, maxStep), 360f);
        }

        /// <summary>直接由偏航生成世界平面基向量，操作方向独立于俯仰和遮挡。</summary>
        public void GetControlBasis(out Vector3 forward, out Vector3 right)
        {
            float radians = yaw * Mathf.Deg2Rad;
            float sin = Mathf.Sin(radians);
            float cos = Mathf.Cos(radians);
            forward = new Vector3(sin, 0f, cos);
            right = new Vector3(cos, 0f, -sin);
        }

        /// <summary>生成无滚转的世界视角。正俯角使相机位于焦点上方。</summary>
        public void GetView(out Quaternion rotation, out Vector3 boomDirection)
        {
            rotation = Quaternion.Euler(pitch, yaw, 0f);
            boomDirection = rotation * Vector3.back;
        }

        /// <summary>在本次探测允许的距离内，立即收近并按秒平滑恢复。</summary>
        public Vector3 ResolvePosition(Vector3 pivot, Vector3 boomDirection, float availableDistance,
            float recoverySpeed, float deltaTime)
        {
            if (!hasDistance || availableDistance <= distance + DistanceTolerance)
            {
                distance = availableDistance;
                hasDistance = true;
            }
            else
            {
                float blend = 1f - Mathf.Exp(-Mathf.Max(0f, recoverySpeed) * Mathf.Max(0f, deltaTime));
                distance = Mathf.Lerp(distance, availableDistance, blend);
            }

            return pivot + boomDirection * distance;
        }

        private static float GetYaw(Vector3 facing, float fallback)
        {
            return facing.x * facing.x + facing.z * facing.z > 0.000001f
                ? Mathf.Repeat(Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg, 360f)
                : fallback;
        }

        private static float ClampPitch(float angle, float firstLimit, float secondLimit)
        {
            float minimum = Mathf.Clamp(Mathf.Min(firstLimit, secondLimit), -89f, 89f);
            float maximum = Mathf.Clamp(Mathf.Max(firstLimit, secondLimit), -89f, 89f);
            return Mathf.Clamp(angle, minimum, maximum);
        }
    }
}
