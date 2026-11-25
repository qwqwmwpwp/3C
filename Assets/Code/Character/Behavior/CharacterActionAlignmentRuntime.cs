using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 由行为发布边界维护对齐实例；预测复制此值后独立推进，物理采样只读方案。
    internal struct CharacterActionAlignmentRuntime
    {
        internal CharacterAlignmentTarget Target { get; private set; }
        internal CharacterAlignmentPlan Plan { get; private set; }
        internal CharacterActionAlignmentStatus Status { get; private set; }
        internal long Instance { get; private set; }
        private long rejectedRequest;
        internal bool HasTarget => Target.Valid && Target.Request != rejectedRequest;

        // 更新主线程冻结的输入，明确撤销、失效或跳变时立即终止本实例的修正。
        internal bool SetTarget(in CharacterAlignmentTarget next, out long cancelled)
        {
            cancelled = 0;
            var previous = Target;
            if (!previous.Changed(next)) return false;
            Target = next;
            if (Plan == null) return HasTarget || previous.Valid;
            var settings = Plan.Settings;
            if (previous.Request != next.Request)
                cancelled = Stop(CharacterActionAlignmentStatus.Cancelled);
            else if (!next.Valid)
            {
                if (settings.OnLost == CharacterAlignmentLoss.RequireNewRequest) rejectedRequest = next.Request;
                cancelled = Stop(CharacterActionAlignmentStatus.TargetLost);
            }
            else if ((next.Position - previous.Position).magnitude > settings.JumpDistance ||
                Vector2.Angle(previous.Facing, next.Facing) > settings.JumpAngle)
            {
                if (settings.OnJump == CharacterAlignmentLoss.RequireNewRequest) rejectedRequest = next.Request;
                cancelled = Stop(CharacterActionAlignmentStatus.TargetJumped);
            }
            return true;
        }

        // 在实际裁决或预测副本发布命令时建立新实例或重定向剩余曲线；读取冻结命令不会调用这里。
        internal void Publish(CharacterFrozenMotion motion, in CharacterAlignmentOrigin origin, double time, Vector2 ordinaryVelocity)
        {
            var window = motion?.AlignmentWindow ?? default;
            if (window.Instance != 0 && Instance != window.Instance)
            {
                Instance = window.Instance;
                Plan = null;
                Status = CharacterActionAlignmentStatus.None;
            }
            if (!window.Settings.IsValid)
            {
                if (Plan != null && time < Plan.End) Stop(CharacterActionAlignmentStatus.Cancelled);
                return;
            }
            if (!HasTarget || origin.Time >= time + window.End) return;
            if (Plan == null && Status != CharacterActionAlignmentStatus.None) return;
            if (Plan != null && (Plan.Settings.Tracking != CharacterAlignmentTracking.Follow ||
                !Plan.Target.Changed(Target))) return;
            if (!CharacterAlignmentPlan.Allows(window.Settings, Target, origin))
            {
                Stop(CharacterActionAlignmentStatus.OutOfRange);
                return;
            }
            if (Plan != null)
                Plan = Plan.Follow(Target, origin.Time);
            else
            {
                Plan = CharacterAlignmentPlan.Begin(motion, Target, origin, time, ordinaryVelocity);
                if (Plan != null) Status = CharacterActionAlignmentStatus.Pending;
            }
        }

        // 用同一求解步的切片身份与实测位姿判定受阻或到达，旧实例结果不能覆盖新实例。
        internal long Observe(IReadOnlyList<CharacterMotionSlice> slices, Vector3 start, Vector3 end, Vector2 facing,
            CollisionFlags contacts)
        {
            if (Plan == null || slices.Count == 0) return 0;
            CharacterAlignmentPlan executed = null;
            Vector2 requested = default, aligned = default;
            for (int i = 0; i < slices.Count; i++)
            {
                var slice = slices[i];
                requested += slice.Behavior.Displacement;
                if (slice.Alignment == null || slice.Alignment.Instance != Instance || slice.Alignment.Target.Request != Plan.Target.Request)
                    continue;
                executed = slice.Alignment;
                aligned += slice.Behavior.Displacement;
            }
            if (executed == null) return 0;
            var settings = executed.Settings;
            Vector2 actual = CharacterAlignmentPlan.Flat(end - start);
            Vector2 axis = aligned.normalized;
            float expected = Vector2.Dot(requested, axis);
            bool shortfall = expected > 0.0001f && Vector2.Dot(actual, axis) < expected * settings.MinimumProgress;
            if (shortfall && (settings.Obstruction == CharacterAlignmentObstruction.LostProgress || (contacts & CollisionFlags.Sides) != 0))
                return Stop(CharacterActionAlignmentStatus.Blocked);
            if (slices[slices.Count - 1].End >= executed.End)
            {
                Vector3 error = executed.Target.Position - end;
                bool reached = CharacterAlignmentPlan.Flat(error).magnitude <= settings.PositionTolerance &&
                    Mathf.Abs(error.y) <= settings.Height && Vector2.Angle(executed.Target.Facing, facing) <= settings.AngleTolerance;
                return Stop(reached ? CharacterActionAlignmentStatus.Reached : CharacterActionAlignmentStatus.Missed);
            }
            Status = CharacterActionAlignmentStatus.Active;
            return 0;
        }

        // 撤销后保留可查询的实例和结果，只有新动作实例可以再次使用预算。
        internal long Stop(CharacterActionAlignmentStatus reason)
        {
            Status = reason;
            Plan = null;
            return Instance;
        }
    }
}
