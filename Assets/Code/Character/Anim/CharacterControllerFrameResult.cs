using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>订阅变化时生成新快照，同一次固定步交付复用已有列表。</summary>
    internal sealed class CharacterFrameSubscriptions
    {
        private readonly object frameSubscriptionLock = new object();
        private Action<CharacterControllerFrameResult> frameCommitted;
        private volatile Delegate[] frameCommittedHandlers = Array.Empty<Delegate>();
        private bool notifyingReset;
        internal event Action Reset;

        internal void PublishCommitted(in CharacterControllerFrameResult frame, Behaviour owner, CharacterSimulationBlackboard simulation)
        {
            foreach (Action<CharacterControllerFrameResult> handler in frameCommittedHandlers)
            {
                try { handler(frame); }
                catch (Exception error) { Debug.LogException(error, owner); }
                if (frame.SimulationVersion != simulation.LifecycleVersion || owner == null || !owner.isActiveAndEnabled) return;
            }
        }

        internal void PublishReset(Behaviour owner, CharacterSimulationBlackboard simulation)
        {
            var listeners = Reset;
            if (notifyingReset || listeners == null) return;
            notifyingReset = true;
            int version = simulation.ResetVersion;
            try
            {
                foreach (Action handler in listeners.GetInvocationList())
                {
                    try { handler(); }
                    catch (Exception error) { Debug.LogException(error, owner); }
                    if (version != simulation.ResetVersion || owner == null) return;
                }
            }
            finally { notifyingReset = false; }
        }

        internal event Action<CharacterControllerFrameResult> Committed
        {
            add
            {
                lock (frameSubscriptionLock)
                {
                    frameCommitted += value;
                    frameCommittedHandlers = frameCommitted?.GetInvocationList() ?? Array.Empty<Delegate>();
                }
            }
            remove
            {
                lock (frameSubscriptionLock)
                {
                    frameCommitted -= value;
                    frameCommittedHandlers = frameCommitted?.GetInvocationList() ?? Array.Empty<Delegate>();
                }
            }
        }
    }

    /// <summary>完成位移与转向后的只读结果，关联本步输入、行为结果、实际世界位移、朝向及碰撞方向。</summary>
    public readonly struct CharacterControllerFrameResult
    {
        public CharacterBehaviorFrameResult Behavior { get; }
        public CharacterInputSnapshot Input { get; }
        /// <summary>连续的已求解物理步号，独立于产生本步运动命令的行为帧号。</summary>
        public long FrameNumber { get; }
        /// <summary>本逻辑步的模拟时长，单位为秒。</summary>
        public float DeltaTime { get; }
        public Vector3 ExpectedDisplacement => new Vector3(Behavior.Displacement.x, 0f, Behavior.Displacement.y);
        public Vector3 ActualDisplacement { get; }
        public CharacterGroundObservation Ground { get; }
        public CollisionFlags CollisionFlags { get; }
        /// <summary>本步应用位移和转向后的实际世界朝向，供表现层解析相对移动。</summary>
        public Vector3 FacingDirection { get; }
        /// <summary>提交本步时的模拟连续性代次；恢复后旧代次速度不可作为新观测。</summary>
        public int SimulationVersion { get; }

        // 记录本次求解；外部运动源可继续使用其一一对应的行为序号。
        internal CharacterControllerFrameResult(CharacterBehaviorFrameResult behavior,
            CharacterInputSnapshot input, float deltaTime, Vector3 actualDisplacement, CollisionFlags collisionFlags,
            Vector3 facingDirection = default, int simulationVersion = 0, CharacterGroundObservation ground = default,
            long? frameNumber = null)
        {
            FrameNumber = frameNumber ?? behavior.FrameNumber;
            Behavior = behavior;
            Input = input;
            DeltaTime = deltaTime;
            ActualDisplacement = actualDisplacement;
            Ground = ground;
            CollisionFlags = collisionFlags;
            FacingDirection = facingDirection;
            SimulationVersion = simulationVersion;
        }
    }
}
