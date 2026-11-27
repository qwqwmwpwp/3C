using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存某一动作实例的只读参考路径、目标及剩余修正，可按任意切片反复采样。
    internal sealed class CharacterAlignmentPlan
    {
        internal readonly long Instance;
        internal readonly CharacterActionAlignmentSettings Settings;
        internal readonly CharacterAlignmentTarget Target;
        internal readonly double Start, End;
        private readonly CharacterFrozenMotion authored;
        private readonly double authoredTime;
        private readonly Vector2 referenceEnd;
        private readonly Vector3 referenceForward;
        private readonly float baseWeight, usedDistance, usedAngle;
        private readonly Vector2 usedOffset, correction;
        private readonly float usedYaw, turn;

        // 按同一参考终点和已经分配的弧长计算本段可用的平移、转角及峰值速度。
        private CharacterAlignmentPlan(long instance, in CharacterActionAlignmentSettings settings,
            in CharacterAlignmentTarget target, double start, double end, CharacterFrozenMotion authored,
            double authoredTime, Vector2 referenceEnd, Vector3 referenceForward, float baseWeight,
            Vector2 usedOffset, float usedDistance, float usedYaw, float usedAngle)
        {
            Instance = instance;
            Settings = settings;
            Target = target;
            Start = start;
            End = end;
            this.authored = authored;
            this.authoredTime = authoredTime;
            this.referenceEnd = referenceEnd;
            this.referenceForward = referenceForward;
            this.baseWeight = baseWeight;
            this.usedOffset = usedOffset;
            this.usedDistance = usedDistance;
            this.usedYaw = usedYaw;
            this.usedAngle = usedAngle;
            // smoothstep 的最大斜率为 1.5；跟随重定向也只能使用原窗剩余权重。
            float speedWindow = (float)(end - start) * (1f - baseWeight) / 1.5f;
            float distance = Mathf.Max(0f, Mathf.Min(settings.Distance - usedDistance, settings.Speed * speedWindow));
            correction = Vector2.ClampMagnitude(Flat(target.Position) - referenceEnd - usedOffset, distance);
            float angle = Mathf.Max(0f, Mathf.Min(settings.Angle - usedAngle, settings.TurnSpeed * speedWindow));
            float desired = Mathf.DeltaAngle(usedYaw, CharacterGroundSteering.AngleTo(referenceForward, target.Facing));
            turn = Mathf.Clamp(desired, -angle, angle);
        }

        // 从已提交运动的末端预测原始动作在窗口终点的站位，固定该实例的作者参考路径。
        internal static CharacterAlignmentPlan Begin(CharacterFrozenMotion motion, in CharacterAlignmentTarget target,
            in CharacterAlignmentOrigin origin, double time, Vector2 ordinaryVelocity)
        {
            var window = motion.AlignmentWindow;
            double start = Math.Max(origin.Time, time + window.Start);
            double end = time + window.End;
            if (!(end > start)) return null;
            double after = Math.Max(0d, origin.Time - time);
            var reference = motion.Sample(after, end - time).Motion;
            Vector2 endpoint = Flat(origin.Position) + reference.Displacement + ordinaryVelocity * reference.OrdinaryMovementSeconds;
            Vector3 forward = new Vector3(origin.Facing.x, 0f, origin.Facing.y);
            // 每个相位有独立的朝向和限角；只积分本次可执行的作者曲线，不把外部目标混入参考终点。
            while (after < end - time)
            {
                double next = Math.Min(motion.NextBoundary(after, end - time), end - time);
                if (!(next > after)) throw new InvalidOperationException("对齐参考动作的转向相位无法向前推进。");
                var action = motion.Sample(after, next).Motion.Motion;
                if (action.ControlsFacing)
                {
                    float angle = CharacterMotorStep.ActionTurn(forward, action, 0f);
                    if (angle != 0f) forward = Quaternion.AngleAxis(angle, Vector3.up) * forward;
                }
                after = next;
            }
            return new CharacterAlignmentPlan(window.Instance, window.Settings, target, start, end, motion, time,
                endpoint, forward, 0f, default, 0f, 0f, 0f);
        }

        // 在新的发布边界扣除此前已分配的路程和转角，只把剩余预算交给新的目标位姿。
        internal CharacterAlignmentPlan Follow(in CharacterAlignmentTarget next, double time)
        {
            float w = Weight(time);
            float fraction = Fraction(w);
            return new CharacterAlignmentPlan(Instance, Settings, next, Start, End, authored, authoredTime,
                referenceEnd, referenceForward, w, usedOffset + correction * fraction, usedDistance + correction.magnitude * fraction,
                usedYaw + turn * fraction, usedAngle + Mathf.Abs(turn) * fraction);
        }

        // 核对平面距离、方位、朝向和地面高差，防止地面动作追逐越界目标。
        internal static bool Allows(in CharacterActionAlignmentSettings settings, in CharacterAlignmentTarget target,
            in CharacterAlignmentOrigin origin)
        {
            Vector2 offset = Flat(target.Position - origin.Position);
            return target.Valid && origin.Facing.sqrMagnitude > 0.000001f && Mathf.Abs(target.Position.y - origin.Position.y) <= settings.Height &&
                offset.magnitude <= settings.Reach && (offset.sqrMagnitude <= settings.PositionTolerance * settings.PositionTolerance ||
                Vector2.Angle(origin.Facing, offset) <= settings.Cone) && Vector2.Angle(origin.Facing, target.Facing) <= settings.Cone;
        }

        // 只对窗口交集积分原始作者路径和有限修正；转向沿原马达的限角入口执行。
        internal CharacterBehaviorMotion Apply(in CharacterBehaviorMotion ordinary, double start, double end)
        {
            float before = Fraction(Weight(start));
            float weight = Fraction(Weight(end)) - before;
            var action = ordinary.Motion;
            Vector2 displacement = authored.Sample(start - authoredTime, end - authoredTime).Motion.Displacement + correction * weight;
            action.AlignmentYawAtStart = usedYaw + turn * before;
            action.AlignmentYawDelta = turn * weight;
            action.UsesBurstVelocity |= correction.sqrMagnitude > 0f;
            return new CharacterBehaviorMotion(displacement, ordinary.OrdinaryMovementSeconds, true, action);
        }

        // 读取统一绝对窗口的累计平滑权重。
        private float Weight(double time)
        {
            float t = Mathf.Clamp01((float)((time - Start) / (End - Start)));
            return t * t * (3f - 2f * t);
        }

        // 将整窗权重换算为本次目标发布后可消费的比例。
        private float Fraction(float weight) => baseWeight < 1f ? Mathf.Clamp01((weight - baseWeight) / (1f - baseWeight)) : 0f;

        // 将世界位置投影到地面动作使用的 XZ 坐标。
        internal static Vector2 Flat(Vector3 position) => new Vector2(position.x, position.z);
    }

    // 将可重采的有限窗口接在原始动作采样器上，窗口外仍按当前动作执行。
    internal sealed class CharacterFrozenAlignedMotion : CharacterFrozenMotion
    {
        internal readonly CharacterFrozenMotion Original;
        internal readonly CharacterAlignmentPlan Plan;
        private readonly double time;

        // 保留捕获时刻、原动作与该实例的只读对齐方案。
        internal CharacterFrozenAlignedMotion(CharacterFrozenMotion original, CharacterAlignmentPlan plan, double time)
        {
            Original = original;
            Plan = plan;
            this.time = time;
        }

        // 合并动作阶段和对齐窗口的边界，使每个马达切片只采用一套运动规则。
        public override double NextBoundary(double after, double limit)
        {
            double end = Original.NextBoundary(after, limit);
            double first = Plan.Start - time;
            double last = Plan.End - time;
            if (first > after) end = Math.Min(end, first);
            if (last > after) end = Math.Min(end, last);
            return end;
        }

        // 只在窗口内附上物理结算凭据，重复读取相同区间不修改预算或实例状态。
        public override CharacterMotionSample Sample(double start, double end)
        {
            var original = Original.Sample(start, end);
            double a = Math.Max(start, Plan.Start - time);
            double b = Math.Min(end, Plan.End - time);
            if (b <= a) return original;
            if (a == start && b == end)
                return new CharacterMotionSample(Plan.Apply(original.Motion, time + a, time + b), original.StartInstance, Plan);
            // 对外区间也允许一次跨窗；替换交集的位移和转向预算，保留两侧原曲线。
            var inside = Original.Sample(a, b).Motion;
            var warped = Plan.Apply(inside, time + a, time + b);
            var action = original.Motion.Motion;
            action.AlignmentYawAtStart = warped.Motion.AlignmentYawAtStart;
            action.AlignmentYawDelta = warped.Motion.AlignmentYawDelta;
            var motion = new CharacterBehaviorMotion(original.Motion.Displacement - inside.Displacement + warped.Displacement,
                original.Motion.OrdinaryMovementSeconds, true, action);
            return new CharacterMotionSample(motion, original.StartInstance, Plan);
        }
    }
}
