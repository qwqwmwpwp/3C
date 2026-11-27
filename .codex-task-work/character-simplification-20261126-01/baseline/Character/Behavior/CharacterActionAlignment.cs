using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>公开动作对齐的许可、执行状态和最近一次物理结果。</summary>
    public enum CharacterActionAlignmentStatus
    {
        None, Pending, Active, Reached, Missed, Blocked, Cancelled, OutOfRange, TargetLost, TargetJumped
    }

    // 决定实例接纳目标后是否继续捕获该目标的新位姿。
    internal enum CharacterAlignmentTracking { [InspectorName("冻结首次目标")] Frozen, [InspectorName("跟随目标")] Follow }

    // 决定异常目标能否在下一招重新获得对齐许可。
    internal enum CharacterAlignmentLoss { [InspectorName("结束本招对齐")] EndMove, [InspectorName("等待重新提交目标")] RequireNewRequest }

    // 决定实际推进不足时是否还需要侧面接触证据。
    internal enum CharacterAlignmentObstruction { [InspectorName("侧面受阻且推进不足")] SideContact, [InspectorName("实测推进不足")] LostProgress }

    // 配置动作允许的目标范围、有限修正预算和物理结果判定。
    [Serializable]
    internal struct CharacterActionAlignmentSettings
    {
        [InspectorName("允许目标对齐")] public bool Enabled;
        [InspectorName("对齐相位范围")] public Vector2 Window;
        [Min(0f), InspectorName("最大目标距离（米）")] public float Reach;
        [Min(0f), InspectorName("最大目标高差（米）")] public float Height;
        [Range(0f, 180f), InspectorName("最大目标方位与朝向差（度）")] public float Cone;
        [Min(0f), InspectorName("总平移修正（米）")] public float Distance;
        [Min(0f), InspectorName("最大修正速度（米/秒）")] public float Speed;
        [Range(0f, 180f), InspectorName("总转角修正（度）")] public float Angle;
        [Min(0f), InspectorName("最大修正转速（度/秒）")] public float TurnSpeed;
        [InspectorName("目标跟踪")] public CharacterAlignmentTracking Tracking;
        [Min(0f), InspectorName("目标跳变距离（米）")] public float JumpDistance;
        [Range(0f, 180f), InspectorName("目标跳变转角（度）")] public float JumpAngle;
        [InspectorName("目标失效处理")] public CharacterAlignmentLoss OnLost;
        [InspectorName("目标跳变处理")] public CharacterAlignmentLoss OnJump;
        [InspectorName("受阻判据")] public CharacterAlignmentObstruction Obstruction;
        [Range(0f, 1f), InspectorName("最小实测推进比例")] public float MinimumProgress;
        [Min(0f), InspectorName("到达位置容差（米）")] public float PositionTolerance;
        [Range(0f, 180f), InspectorName("到达朝向容差（度）")] public float AngleTolerance;

        // 拒绝不能形成有限窗口、预算或有效物理判据的手工配置。
        internal readonly bool IsValid => Enabled && Window.x >= 0f && Window.y <= 1f && Window.y > Window.x &&
            Finite(Reach) && Finite(Height) && Cone >= 0f && Cone <= 180f &&
            Finite(Distance) && Finite(Speed) && Angle >= 0f && Angle <= 180f && Finite(TurnSpeed) &&
            Finite(JumpDistance) && JumpAngle >= 0f && JumpAngle <= 180f && Finite(PositionTolerance) &&
            AngleTolerance >= 0f && AngleTolerance <= 180f && MinimumProgress >= 0f && MinimumProgress <= 1f;

        // 验证距离、速度与容差为有限非负值。
        private static bool Finite(float x) => x >= 0f && x < float.PositiveInfinity;
    }

    // 仅携带世界坐标中的数值位姿与请求代次，可传给预测线程。
    internal readonly struct CharacterAlignmentTarget
    {
        internal readonly long Request;
        internal readonly Vector3 Position;
        internal readonly Vector2 Facing;
        internal bool Valid => Request != 0 && Facing.sqrMagnitude > 0.5f;

        // 将有效请求绑定到角色应达的世界位置和地面朝向。
        internal CharacterAlignmentTarget(long request, Vector3 position, Vector2 facing)
        {
            Request = request;
            Position = position;
            Facing = facing;
        }

        // 比较已捕获位姿，累积的小变化以最后一次发布的目标为准。
        internal bool Changed(in CharacterAlignmentTarget next) => Request != next.Request || Valid != next.Valid ||
            (Position - next.Position).sqrMagnitude > 0.000001f || Vector2.Angle(Facing, next.Facing) > 0.1f;
    }

    // 从目标变换捕获角色站位与朝向；所有 Unity 对象访问均限制在驱动器主线程。
    [Serializable]
    internal sealed class CharacterActionAlignmentBinding
    {
        [SerializeField, InspectorName("目标锚点")] private Transform target;
        [SerializeField, InspectorName("角色站位（锚点局部偏移）")] private Vector3 offset;
        [SerializeField, InspectorName("朝向偏角（度）")] private float yaw;
        private Transform observed;
        private long request;

        // 显式提交总会分配新代次，包括重新提交同一目标。
        internal void Set(Transform value, Vector3 localOffset, float localYaw)
        {
            observed = target = value;
            offset = localOffset;
            yaw = localYaw;
            request++;
        }

        // 识别 Inspector 换绑、销毁、停用和无效坐标，返回不持有对象的请求快照。
        internal CharacterAlignmentTarget Capture()
        {
            if (!ReferenceEquals(observed, target) || request == 0 && target)
            {
                observed = target;
                request++;
            }
            if (!target || !target.gameObject.activeInHierarchy) return new CharacterAlignmentTarget(request, default, default);
            Vector3 position = target.TransformPoint(offset);
            Vector3 forward = Quaternion.AngleAxis(yaw, Vector3.up) * target.forward;
            Vector2 facing = new Vector2(forward.x, forward.z);
            if (!Finite(position.sqrMagnitude) || !Finite(facing.sqrMagnitude) || facing.sqrMagnitude < 0.000001f)
                return new CharacterAlignmentTarget(request, default, default);
            return new CharacterAlignmentTarget(request, position, facing.normalized);
        }

        // 阻止非有限世界位姿进入物理命令和工作线程。
        private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    }

    // 在同一角色时刻保存已解算或按已有命令外推的站位与朝向。
    internal struct CharacterAlignmentOrigin
    {
        internal double Time;
        internal Vector3 Position;
        internal Vector2 Facing;

        // 用给定区间的已批准运动外推下一个对齐捕获点。
        internal void Advance(in CharacterMotionSlice slice, float turnSpeed)
        {
            var frame = slice.Behavior;
            Position += new Vector3(frame.Displacement.x, 0f, frame.Displacement.y);
            var motion = frame.ActionMotion;
            var forward = new Vector3(Facing.x, 0f, Facing.y);
            float limit = turnSpeed * (float)(slice.End - slice.Start);
            float angle = frame.ActionMotionStarted && motion.SnapsFacingOnStart ? CharacterGroundSteering.AngleTo(forward, motion.Facing) :
                motion.ControlsFacing ? CharacterMotorStep.ActionTurn(forward, motion, limit) :
                frame.MoveApproved ? Mathf.Clamp(CharacterGroundSteering.AngleTo(forward, slice.Input.Direction), -limit, limit) : 0f;
            if (angle != 0f) Facing = CharacterAlignmentPlan.Flat(Quaternion.AngleAxis(angle, Vector3.up) * forward);
            Time = slice.End;
        }
    }
}
