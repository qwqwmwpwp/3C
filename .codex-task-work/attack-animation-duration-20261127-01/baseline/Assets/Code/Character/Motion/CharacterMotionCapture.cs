using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>提供冻结运动所需的实测值、持续移动意图和有效转向参数。</summary>
    public readonly struct CharacterMotionCapture
    {
        public readonly double Time; // 角色时钟，单位为秒。
        public readonly Vector2 Facing, GroundVelocity, Direction; // 世界 X/Z 平面。
        public readonly CharacterMoveGait Gait;
        public readonly float TurnSpeed; // 角度/秒。

        /// <summary>从裁决时的观测和输入提取精确运动所需的数值。</summary>
        public CharacterMotionCapture(double time, Vector2 facing, Vector2 groundVelocity,
            CharacterInputSnapshot input, float turnSpeed)
        {
            Time = time;
            Facing = facing;
            GroundVelocity = groundVelocity;
            Direction = input.Direction;
            Gait = input.Gait;
            TurnSpeed = turnSpeed;
        }
    }
}
