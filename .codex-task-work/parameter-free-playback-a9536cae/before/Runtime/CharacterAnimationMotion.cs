using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>单个已提交固定步的平面运动观测，以及来自同一刚体步的只读预测。</summary>
    public readonly struct CharacterAnimationMotion
    {
        /// <summary>是否来自当前模拟连续性内的有效已提交固定步；默认值与暂停观测无效。</summary>
        public bool IsValid { get; }
        public long FrameNumber { get; }
        /// <summary>本步提交时的模拟连续性代次；跨代次的速度和方向不可用于检测反向。</summary>
        public int SimulationVersion { get; }
        /// <summary>实际与期望世界平面速度，单位米/秒。</summary>
        public Vector3 ActualVelocity { get; }
        public Vector3 ExpectedVelocity { get; }
        public float ActualSpeed => ActualVelocity.magnitude;
        public float ExpectedSpeed => ExpectedVelocity.magnitude;
        /// <summary>是否有连续且可用于差分的两步已提交平面速度。</summary>
        public bool HasAcceleration { get; }
        /// <summary>前一个已提交固定步的世界平面速度，单位米/秒；加速度无效时为零。</summary>
        public Vector3 PreviousActualVelocity { get; }
        /// <summary>世界平面速度的变化率，单位米/秒²；不可差分时为零。</summary>
        public Vector3 ActualAcceleration { get; }
        /// <summary>角色相对实际移动方向：x 向右，y 向前；静止时为零。</summary>
        public Vector2 LocalMoveDirection { get; }
        /// <summary>朝向至期望移动方向的有符号角，单位度；没有期望移动时为零。</summary>
        public float FacingDeltaDegrees { get; }
        /// <summary>沿期望方向损失的速度比例，0 为畅通，1 为完全受阻。</summary>
        public float Obstruction { get; }
        public bool IsBlocked => Obstruction >= 0.5f && (CollisionFlags & UnityEngine.CollisionFlags.Sides) != 0;
        public CollisionFlags CollisionFlags { get; }
        public CharacterGroundTrajectory Trajectory { get; }

        internal CharacterAnimationMotion(CharacterControllerFrameResult frame,
            Vector3 previousVelocity = default, float velocitySampleInterval = 0f)
        {
            IsValid = frame.FrameNumber > 0 && frame.DeltaTime > 0.000001f;
            FrameNumber = frame.FrameNumber;
            SimulationVersion = frame.SimulationVersion;
            float inverseTime = frame.DeltaTime > 0.000001f ? 1f / frame.DeltaTime : 0f;
            ActualVelocity = Vector3.ProjectOnPlane(frame.ActualDisplacement, Vector3.up) * inverseTime;
            ExpectedVelocity = Vector3.ProjectOnPlane(frame.ExpectedDisplacement, Vector3.up) * inverseTime;
            HasAcceleration = IsValid && velocitySampleInterval > 0f;
            PreviousActualVelocity = HasAcceleration ? previousVelocity : default;
            ActualAcceleration = HasAcceleration ? (ActualVelocity - previousVelocity) / velocitySampleInterval : default;
            Vector3 forward = Vector3.ProjectOnPlane(frame.FacingDirection, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.000001f)
                forward = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 direction = ActualVelocity.normalized;
            LocalMoveDirection = new Vector2(Vector3.Dot(direction, right), Vector3.Dot(direction, forward));
            FacingDeltaDegrees = ExpectedVelocity.sqrMagnitude > 0.000001f
                ? Vector3.SignedAngle(forward, ExpectedVelocity, Vector3.up) : 0f;
            float expectedSpeed = ExpectedVelocity.magnitude;
            Obstruction = expectedSpeed > 0.000001f
                ? 1f - Mathf.Clamp01(Vector3.Dot(ActualVelocity, ExpectedVelocity / expectedSpeed) / expectedSpeed) : 0f;
            CollisionFlags = frame.CollisionFlags;
            Trajectory = frame.Trajectory;
        }

        private CharacterAnimationMotion(in CharacterAnimationMotion sample)
        {
            this = sample;
            HasAcceleration = false;
            PreviousActualVelocity = default;
            ActualAcceleration = default;
        }

        internal CharacterAnimationMotion WithoutAcceleration() =>
            HasAcceleration ? new CharacterAnimationMotion(this) : this;
    }

    /// <summary>仅负责固定步采样和观测有效期；不选择走跑动作，也不依赖动画资源。</summary>
    internal sealed class CharacterAnimationMotionObserver
    {
        private readonly float teleportDistanceSquared;
        private long lastFrameNumber;
        private int simulationVersion;
        private CharacterAnimationMotion motion;
        private float previousStepDuration;
        private bool discardNextAccelerationSample;

        public CharacterAnimationMotionObserver(float teleportDistance = 0f)
        {
            teleportDistanceSquared = teleportDistance > 0f ? teleportDistance * teleportDistance : 0f;
        }

        public void Reset(CharacterControllerFrameResult latestFrame, int currentSimulationVersion)
        {
            lastFrameNumber = latestFrame.FrameNumber; // 重建后从下一次提交开始取速度。
            simulationVersion = currentSimulationVersion;
            ClearSamples();
            discardNextAccelerationSample = false;
        }

        public void InvalidateAcceleration()
        {
            motion = motion.WithoutAcceleration();
            previousStepDuration = 0f;
            discardNextAccelerationSample = true; // 本步位移仍可能包含瞬移。
        }

        private void ClearSamples()
        {
            motion = default;
            previousStepDuration = 0f;
        }

        public bool Receive(CharacterControllerFrameResult frame, int currentSimulationVersion, bool simulating)
        {
            SynchronizeVersion(currentSimulationVersion);
            if (!simulating || frame.SimulationVersion != simulationVersion)
            {
                ClearSamples();
                return false;
            }
            if (frame.FrameNumber == lastFrameNumber)
                return false;
            bool consecutive = frame.FrameNumber > lastFrameNumber && frame.FrameNumber - lastFrameNumber == 1;
            bool discardSample = discardNextAccelerationSample || (teleportDistanceSquared > 0f &&
                frame.ActualDisplacement.sqrMagnitude > teleportDistanceSquared);
            // 位移除以步长得到步内平均速度，差分时间取两个固定步中点的间隔。
            float sampleInterval = consecutive && !discardSample && previousStepDuration > 0f
                ? (previousStepDuration + frame.DeltaTime) * 0.5f : 0f;
            lastFrameNumber = frame.FrameNumber;
            motion = frame.FrameNumber > 0 ? new CharacterAnimationMotion(frame, motion.ActualVelocity, sampleInterval) : default;
            previousStepDuration = motion.IsValid && !discardSample ? frame.DeltaTime : 0f;
            discardNextAccelerationSample = false;
            return motion.IsValid;
        }

        public CharacterAnimationMotion Read(CharacterControllerFrameResult latestFrame,
            int currentSimulationVersion, bool simulating)
        {
            SynchronizeVersion(currentSimulationVersion);
            if (!simulating || latestFrame.SimulationVersion != simulationVersion)
                ClearSamples();
            return latestFrame.FrameNumber == lastFrameNumber ? motion : default;
        }

        private void SynchronizeVersion(int currentSimulationVersion)
        {
            if (simulationVersion == currentSimulationVersion)
                return;
            simulationVersion = currentSimulationVersion;
            ClearSamples();
        }
    }
}
