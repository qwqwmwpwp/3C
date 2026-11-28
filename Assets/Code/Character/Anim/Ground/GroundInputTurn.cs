using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存固定步的输入急转边沿，供表现阶段在有效窗口内选姿。
    internal sealed class GroundInputTurn
    {
        private readonly float threshold, window, minimumSpeed;
        internal long Step { get; private set; }
        internal Vector3 Direction { get; private set; }
        private Vector3 target;
        private float remaining, angle, speed;
        private GroundTrackGait gait;
        internal GroundPoseQuery Query => new GroundPoseQuery(GroundMotionUse.Turn, gait, speed, angle);

        // 仅在候选策略具有合法的急转启用行时采样输入边沿。
        internal static GroundInputTurn Bind(CharacterAnimationConfiguration config, float window) =>
            config.CandidatePolicy?.SupportsInputTurn == true ? new GroundInputTurn(config, window) : null;

        // 使用同一角色配置的速度门槛、换向夹角和请求保留时长。
        private GroundInputTurn(CharacterAnimationConfiguration config, float window)
        {
            threshold = Mathf.Clamp(config.MatchTurnAngle, 15f, 180f);
            minimumSpeed = Mathf.Max(0f, config.IdleExitSpeed);
            this.window = window;
        }

        // 对比前后两次已许可的世界空间方向，保留换向前的实速与步态。
        internal void Observe(in CharacterAnimationMotion motion, in CharacterAnimationMovement movement,
            bool approved, Vector3 previousVelocity, string previousGait, float dt)
        {
            Vector3 next = new Vector3(movement.MoveDirection.x, 0f, movement.MoveDirection.y).normalized;
            if (!approved || next == Vector3.zero || (motion.CollisionFlags & CollisionFlags.Below) == 0)
            { Reset(); return; }
            remaining -= dt;
            if (remaining <= 0f || Vector3.Dot(target, next) < 0.8f) Step = 0;
            if (Direction != Vector3.zero && previousVelocity.magnitude >= minimumSpeed)
            {
                float change = Vector3.SignedAngle(Direction, next, Vector3.up);
                if (Mathf.Abs(change) >= threshold)
                {
                    Step = motion.FrameNumber;
                    angle = change <= -179.999f ? 180f : change;
                    speed = previousVelocity.magnitude;
                    gait = GroundPoseQuery.GaitOf(previousGait);
                    target = next;
                    remaining = window;
                }
            }
            Direction = next;
        }

        // 清除不连续输入来源的方向和待处理边沿。
        internal void Reset()
        {
            Step = 0;
            remaining = 0f;
            Direction = target = Vector3.zero;
        }
    }
}
