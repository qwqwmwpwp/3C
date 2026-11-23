using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>完成位移与转向后的只读结果，关联本步输入、行为结果、实际世界位移、朝向及碰撞方向。</summary>
    public readonly struct CharacterControllerFrameResult
    {
        public CharacterBehaviorFrameResult Behavior { get; }
        public CharacterInputSnapshot Input { get; }
        public long FrameNumber => Behavior.FrameNumber;
        /// <summary>本逻辑步的模拟时长，单位为秒。</summary>
        public float DeltaTime { get; }
        public Vector3 ExpectedDisplacement => new Vector3(Behavior.Displacement.x, 0f, Behavior.Displacement.y);
        public Vector3 ActualDisplacement { get; }
        public CharacterGroundTrajectory Trajectory { get; }
        public CollisionFlags CollisionFlags { get; }
        /// <summary>本步应用位移和转向后的实际世界朝向，供表现层解析相对移动。</summary>
        public Vector3 FacingDirection { get; }
        /// <summary>提交本步时的模拟连续性代次；恢复后旧代次速度不可作为新观测。</summary>
        public int SimulationVersion { get; }

        internal CharacterControllerFrameResult(CharacterBehaviorFrameResult behavior,
            CharacterInputSnapshot input, float deltaTime, Vector3 actualDisplacement, CollisionFlags collisionFlags,
            Vector3 facingDirection = default, int simulationVersion = 0, CharacterGroundTrajectory trajectory = default)
        {
            Behavior = behavior;
            Input = input;
            DeltaTime = deltaTime;
            ActualDisplacement = actualDisplacement;
            Trajectory = trajectory;
            CollisionFlags = collisionFlags;
            FacingDirection = facingDirection;
            SimulationVersion = simulationVersion;
        }
    }
}
