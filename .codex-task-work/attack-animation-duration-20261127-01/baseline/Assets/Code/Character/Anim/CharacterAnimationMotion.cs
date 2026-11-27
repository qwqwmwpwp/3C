using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>提供单个已提交固定步的平面运动观测及其动画表现轨迹。</summary>
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
        internal float? RetainedStrideSpeed { get; } // 瞬移步只保留最近一次可信步频所用的速度。
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

        // 将固定步位移换算为平面速度、加速度和受阻程度，并关联同一步的表现轨迹。
        internal CharacterAnimationMotion(in CharacterControllerFrameResult frame, in CharacterGroundTrajectory trajectory,
            Vector3 previousVelocity = default, float velocitySampleInterval = 0f)
        {
            RetainedStrideSpeed = null;
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
            Trajectory = trajectory;
        }

        // 保留步号、速度和受阻事实，只替换本次表现采样的轨迹。
        internal CharacterAnimationMotion(in CharacterAnimationMotion source, in CharacterGroundTrajectory trajectory)
        {
            this = source;
            Trajectory = trajectory;
        }

        // 清除差分采样，只保留最近可信步频及对应固定步的表现轨迹。
        internal CharacterAnimationMotion(float? retainedSpeed, in CharacterGroundTrajectory trajectory)
        {
            this = default;
            RetainedStrideSpeed = retainedSpeed;
            Trajectory = trajectory;
        }
    }

    /// <summary>仅负责固定步采样和观测有效期；不选择走跑动作，也不依赖动画资源。</summary>
    internal sealed class CharacterAnimationMotionObserver
    {
        private float teleportDistanceSquared;
        private long lastFrameNumber;
        private int simulationVersion;
        private CharacterAnimationMotion motion;
        private float previousStepDuration;
        private readonly Transform owner;
        private Vector3 lastPosition;
        private bool discardNextStep, jumpAwaitingCommit;

        // 记录角色位置来源，并按配置初始化瞬移阈值。
        public CharacterAnimationMotionObserver(Transform owner, float teleportDistance)
        {
            this.owner = owner;
            ConfigureTeleport(teleportDistance);
        }

        // 显式装配时更新瞬移门槛并保留最近一次物理观测。
        internal void ConfigureTeleport(float distance) => teleportDistanceSquared = distance > 0f ? distance * distance : 0f;

        // 重建时恢复同代次最近固定步的地面投影或外部轨迹，下一步开始采样速度。
        public void Reset(CharacterControllerFrameResult latestFrame, int currentSimulationVersion,
            CharacterGroundTrajectory externalTrajectory = default)
        {
            lastFrameNumber = latestFrame.FrameNumber; // 重建后从下一次提交开始取速度。
            simulationVersion = currentSimulationVersion;
            ClearSamples();
            if (latestFrame.FrameNumber > 0 && latestFrame.SimulationVersion == currentSimulationVersion)
                motion = new CharacterAnimationMotion(null, StepTrajectory(latestFrame, externalTrajectory));
            lastPosition = owner.position;
            discardNextStep = jumpAwaitingCommit = false;
        }

        // 显式瞬移时舍弃当前及下一步的速度差分，并等待新的固定步位置。
        public void InvalidatePosition()
        {
            lastPosition = owner.position;
            DropMotion(motion.Trajectory);
            discardNextStep = true; // 外部可显式通知小于自动检测距离的瞬移。
            jumpAwaitingCommit = true;
        }

        // 瞬移打断速度差分，同时维持指定固定步的独立表现轨迹。
        private void DropMotion(in CharacterGroundTrajectory trajectory)
        {
            motion = new CharacterAnimationMotion(motion.IsValid ? motion.ActualSpeed : motion.RetainedStrideSpeed, trajectory);
            previousStepDuration = 0f;
        }

        private void ClearSamples()
        {
            motion = default;
            previousStepDuration = 0f;
        }

        // 对每个新固定步取一次地面投影或外部轨迹，并按连续性更新速度采样。
        public bool Receive(CharacterControllerFrameResult frame, int currentSimulationVersion, bool simulating, out bool resetPose,
            CharacterGroundTrajectory externalTrajectory = default)
        {
            SynchronizeVersion(currentSimulationVersion);
            Vector3 position = owner.position;
            resetPose = teleportDistanceSquared > 0f && (position - lastPosition).sqrMagnitude > teleportDistanceSquared;
            lastPosition = position;
            if (!simulating || frame.SimulationVersion != simulationVersion)
            {
                ClearSamples();
                return false;
            }
            if (frame.FrameNumber == lastFrameNumber)
            {
                if (resetPose)
                {
                    DropMotion(motion.Trajectory);
                    jumpAwaitingCommit = true;
                }
                return false;
            }

            bool jumped = teleportDistanceSquared > 0f && frame.ActualDisplacement.sqrMagnitude > teleportDistanceSquared;
            bool consecutive = frame.FrameNumber == lastFrameNumber + 1;
            lastFrameNumber = frame.FrameNumber;
            var trajectory = StepTrajectory(frame, externalTrajectory);
            bool skip = resetPose || jumped || discardNextStep;
            resetPose |= jumped && !jumpAwaitingCommit;
            discardNextStep = jumpAwaitingCommit = false;
            if (skip)
            {
                DropMotion(trajectory);
                return frame.FrameNumber > 0; // 继续交付本步行为边沿，位移交给下一次正常采样。
            }
            float interval = consecutive && previousStepDuration > 0f ? (previousStepDuration + frame.DeltaTime) * 0.5f : 0f;
            motion = frame.FrameNumber > 0 ? new CharacterAnimationMotion(frame, trajectory, motion.ActualVelocity, interval) : default;
            previousStepDuration = motion.IsValid ? frame.DeltaTime : 0f;
            return motion.IsValid;
        }

        // 只交付当前代次和步号对应的采样，重复读取沿用该步原始表现轨迹。
        public CharacterAnimationMotion Read(CharacterControllerFrameResult latestFrame,
            int currentSimulationVersion, bool simulating)
        {
            SynchronizeVersion(currentSimulationVersion);
            if (!simulating || latestFrame.SimulationVersion != simulationVersion)
                ClearSamples();
            return latestFrame.FrameNumber == lastFrameNumber ? motion : default;
        }

        // 从同一已提交固定步取出内置驱动的地面投影或外部驱动附带的轨迹。
        private static CharacterGroundTrajectory StepTrajectory(in CharacterControllerFrameResult frame, in CharacterGroundTrajectory externalTrajectory) =>
            frame.Ground.IsValid ? CharacterGroundTrajectory.FromGround(frame.Ground) : externalTrajectory;

        private void SynchronizeVersion(int currentSimulationVersion)
        {
            if (simulationVersion == currentSimulationVersion)
                return;
            simulationVersion = currentSimulationVersion;
            discardNextStep = jumpAwaitingCommit = false;
            ClearSamples();
        }
    }
}
