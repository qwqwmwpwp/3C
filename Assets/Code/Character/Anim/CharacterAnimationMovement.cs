using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>一次运动结算的动画输入。位移和朝向保留完整世界三维坐标。</summary>
    public struct CharacterAnimationMovement
    {
        public long FrameNumber;
        public int SimulationVersion;
        public float DeltaTime;
        public Vector3 ActualDisplacement, ExpectedDisplacement, Facing;
        public Vector3? ExpectedFacing; // 独立于位移的目标世界朝向；缺省时按期望平移方向推断。
        public Vector2 MoveDirection;
        public AnimationGait Gait;
        public CollisionFlags Contacts;
        public CharacterAnimationContext Context;
        public bool PreserveEntryGait;
        public CharacterGroundTrajectory Trajectory;
        public IAnimationGroundProjection GroundProjection;
        public bool SuppressForecastProjection; // 此步在轨迹发布源的可信范围之外。

        // 供普通移动源一次填写移动许可、起步许可和位移归属。
        public bool MoveApproved
        {
            get => Context.MoveApproved;
            set
            {
                Context.MoveApproved = Context.CanStartMoving = value;
                Context.MotionSource = value ? AnimationMotionSource.Locomotion : AnimationMotionSource.None;
            }
        }

        // 只接受具有正时长和有限实测值的运动，步号可由宿主在提交时分配。
        public bool IsValid => DeltaTime > 0.000001f && Finite(DeltaTime) &&
            Finite((ActualDisplacement / DeltaTime).sqrMagnitude) && Finite((ExpectedDisplacement / DeltaTime).sqrMagnitude) &&
            Finite(Facing.sqrMagnitude) && Finite(MoveDirection.sqrMagnitude) &&
            (!ExpectedFacing.HasValue || ValidFacing(ExpectedFacing.Value));

        // 明确提交的目标朝向必须为有限且可归一化的三维方向。
        private static bool ValidFacing(Vector3 direction)
        {
            float magnitude = direction.sqrMagnitude;
            return Finite(direction) && Finite(magnitude) && magnitude > 0.000001f;
        }

        // 检查平面输入和时长能否参与本帧数值运算。
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // 检查世界空间向量的每一个分量。
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
