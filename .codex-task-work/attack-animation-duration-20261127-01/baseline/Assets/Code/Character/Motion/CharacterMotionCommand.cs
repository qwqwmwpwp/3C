using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存裁决后的持续运动指令，或旧行为已执行区间的总量。
    internal struct CharacterMotionCommand
    {
        internal readonly double Time;
        private readonly CharacterBehaviorFrameResult behavior;
        private readonly CharacterInputSnapshot input;
        private readonly Vector2 ordinaryVelocity;
        private readonly CharacterBehaviorId provider;
        private readonly CharacterFrozenMotion frozen;
        private readonly double executedUntil;
        private readonly bool isExecutedInterval;
        // 按值锁定已获准的普通移动目标速度，冻结动作规则由行为层提供。
        internal CharacterMotionCommand(double time, CharacterBehaviorFrameResult result, CharacterInputSnapshot input,
            CharacterMovementSettings settings, CharacterBehaviorId provider, CharacterFrozenMotion frozen)
        {
            this = default;
            Time = time;
            behavior = result;
            this.input = Held(input);
            this.provider = provider;
            this.frozen = frozen;
            ordinaryVelocity = behavior.MoveApproved
                ? CharacterMoveBehavior.TargetVelocity(this.input, settings, behavior.OrdinarySpeedLimit) : Vector2.zero;
        }

        // 保存旧行为在半开区间内的实际运动，子区间按时长分配总量。
        internal CharacterMotionCommand(double start, double end, CharacterBehaviorFrameResult result, CharacterInputSnapshot held)
        {
            this = default;
            if (!(end > start) || double.IsInfinity(start) || double.IsInfinity(end))
                throw new ArgumentException("已执行运动区间必须具有有限的正时长。");
            Time = start;
            executedUntil = end;
            isExecutedInterval = true;
            behavior = result;
            input = Held(held);
        }

        // 将局部阶段切点映射回绝对时间，同时保留原始切点用于区间积分。
        private double ResolveFrozenEnd(CharacterFrozenMotion sampler, double start, double limit, double localStart, out double localEnd)
        {
            localEnd = limit - Time;
            if (!(localStart >= 0d && localEnd > localStart) || double.IsInfinity(localEnd))
                throw new InvalidOperationException($"行为 {provider} 的运动区间 {start:R} 到 {limit:R} 超出捕获时刻 {Time:R} 的有效范围。");
            double boundary = sampler.NextBoundary(localStart, localEnd);
            if (boundary == localEnd || double.IsPositiveInfinity(boundary)) return limit;
            if (!(boundary > localStart && boundary < localEnd))
                throw new InvalidOperationException($"行为 {provider} 的冻结运动返回无效边界 {boundary:R}，有效范围为 ({localStart:R}, {localEnd:R}]。");

            localEnd = boundary;
            double time = Time + boundary;
            // 绝对时间加法可能向下舍入；让下一段换算回局部时间后已越过同一切点。
            if (time - Time < boundary)
                time = time == 0d ? double.Epsilon
                    : BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(time) + (time > 0d ? 1 : -1));
            if (!(time > start && time <= limit))
                throw new InvalidOperationException($"行为 {provider} 的冻结运动边界 {boundary:R} 无法在当前角色时间上推进。");
            return time;
        }

        // 采样直到上限或下一动作边界，将动作与普通移动合成为马达切片。
        internal CharacterMotionSlice SampleNext(double start, double limit)
        {
            if (start == limit && !double.IsInfinity(start)) return new CharacterMotionSlice(start, limit, default, input);
            if (!(limit > start) || double.IsInfinity(start) || double.IsInfinity(limit))
                throw new ArgumentException("运动采样区间必须具有有限的正时长。");
            bool executed = isExecutedInterval && start < executedUntil;
            double end = executed ? Math.Min(limit, executedUntil) : limit;

            CharacterMotionSample sample = default;
            if (frozen != null)
            {
                double localStart = start - Time;
                end = ResolveFrozenEnd(frozen, start, limit, localStart, out double localEnd);
                sample = frozen.Sample(localStart, localEnd);
            }
            float dt = (float)(end - start);
            if (!(dt > 0f) || float.IsInfinity(dt))
                throw new InvalidOperationException($"行为 {provider} 的运动区间 {start:R} 到 {end:R} 超出马达可执行的步长范围。");
            if (executed) return SampleExecuted(start, end);

            var action = sample.Motion;
            float ordinary = frozen == null ? dt : Mathf.Min(dt, action.OrdinaryMovementSeconds);
            bool move = ordinary > 0f && ordinaryVelocity != Vector2.zero;
            CharacterBehaviorId source = action.ControlsDisplacement || (!move && action.Displacement != Vector2.zero)
                ? provider : move ? CharacterBehaviorId.Move : CharacterBehaviorId.None;
            var frame = behavior.WithMotion(action.Displacement + ordinaryVelocity * ordinary, action.Displacement,
                action.Motion, source, ordinary, move, false);
            var token = sample.StartInstance.HasValue ? new MotionStartToken(provider, sample.StartInstance.Value) : default;
            return new CharacterMotionSlice(start, end, frame, input, token);
        }

        // 旧行为的位移、转角和普通移动时间沿用已执行总量的比例分配。
        private CharacterMotionSlice SampleExecuted(double start, double end)
        {
            if (start < Time || end > executedUntil)
                throw new InvalidOperationException("旧行为的总量只能用于已执行的运动区间。");
            float fraction = (float)((end - start) / (executedUntil - Time));
            var motion = behavior.ActionMotion;
            motion.MaxTurn *= fraction;
            var frame = behavior.WithMotion(behavior.Displacement * fraction, behavior.ActionDisplacement * fraction,
                motion, behavior.DisplacementProvider, behavior.OrdinaryMovementSeconds * fraction, behavior.MoveApproved,
                behavior.ActionMotionStarted && start == Time);
            return new CharacterMotionSlice(start, end, frame, input);
        }

        // 只保留马达和预测在后续时间片仍有效的持续意图。
        private static CharacterInputSnapshot Held(in CharacterInputSnapshot input) =>
            new CharacterInputSnapshot(input.Direction, false, input.GuardHeld, CharacterBehaviorId.None, input.Gait);
    }
}
