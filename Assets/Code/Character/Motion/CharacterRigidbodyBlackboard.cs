using System;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    // 保存积分后的运动区间及该区间关联的起步实例。
    internal readonly struct CharacterMotionSlice
    {
        internal readonly double Start, End;
        internal readonly CharacterBehaviorFrameResult Behavior;
        internal readonly CharacterInputSnapshot Input;
        internal readonly MotionStartToken StartToken;
        internal readonly CharacterAlignmentPlan Alignment;

        // 将运动请求、持续输入和起步身份绑定到相同的半开区间。
        internal CharacterMotionSlice(double start, double end, CharacterBehaviorFrameResult behavior,
            CharacterInputSnapshot input, MotionStartToken startToken = default, CharacterAlignmentPlan alignment = null)
        {
            Start = start;
            End = end;
            Behavior = behavior;
            Input = input;
            StartToken = startToken;
            Alignment = alignment;
        }

        // 根据执行域的游标解析首次起步，保留旧行为已执行输出中的起步标记。
        internal CharacterMotionSlice ResolveStart(ref MotionStartToken cursor)
        {
            if (!StartToken.HasValue || !(End > Start)) return this;
            bool starts = StartToken.TryConsume(ref cursor);
            if (starts == Behavior.ActionMotionStarted) return this;
            var frame = Behavior.WithMotion(Behavior.Displacement, Behavior.ActionDisplacement, Behavior.ActionMotion,
                Behavior.DisplacementProvider, Behavior.OrdinaryMovementSeconds, Behavior.MoveApproved, starts);
            return new CharacterMotionSlice(Start, End, frame, Input, StartToken, Alignment);
        }
    }

    // 保存尚未执行的运动变更，以及可提交、撤回的马达时间和起步游标。
    internal sealed class CharacterRigidbodyBlackboard
    {
        private readonly List<CharacterMotionCommand> commands = new List<CharacterMotionCommand>();
        private readonly List<CharacterMotionSlice> alignmentProjection = new List<CharacterMotionSlice>();
        private MotionStartToken previousStartCursor;
        private double previousThrough;
        internal MotionStartToken StartCursor { get; private set; }
        internal double SubmittedThrough { get; private set; }

        internal double KnockbackUntil { get; private set; }
        internal float KnockbackBraking { get; private set; }

        // 按角色时钟登记硬直区间；马达和预测均在同一终点恢复普通运动。
        internal void BeginKnockback(double start, float seconds, float braking)
        {
            KnockbackUntil = start + seconds;
            KnockbackBraking = braking;
        }

        // 清除过界的动作命令、硬直和起步游标，保留已结算物理时间。
        internal void ClearCommands()
        {
            commands.Clear();
            StartCursor = previousStartCursor = default;
            KnockbackUntil = double.NegativeInfinity;
            KnockbackBraking = 0f;
        }

        // 对齐参考起点从当前实测刚体沿尚未执行的获准切片外推，已提交区间不会重复积分。
        internal CharacterAlignmentOrigin ProjectAlignmentOrigin(double time, in CharacterAlignmentOrigin actual, float turnSpeed)
        {
            var origin = actual;
            if (time <= actual.Time) return origin;
            var cursor = StartCursor;
            Read(actual.Time, time, ref cursor, alignmentProjection);
            foreach (var slice in alignmentProjection) origin.Advance(slice, turnSpeed);
            alignmentProjection.Clear();
            return origin;
        }

        // 在下次物理提交点撤销队列内该实例的剩余修正，已经交给求解器的命令仍按原值记录。
        internal void CancelAlignment(long instance)
        {
            if (instance == 0) return;
            for (int i = 0; i < commands.Count; i++)
            {
                var command = commands[i];
                command.CancelAlignment(instance, SubmittedThrough);
                commands[i] = command;
            }
        }

        // 同一时刻的最后裁决覆盖零长命令，已提交区间由马达游标保护。
        internal void Write(in CharacterMotionCommand command)
        {
            int last = commands.Count - 1;
            if (last >= 0 && commands[last].Time == command.Time) commands[last] = command;
            else commands.Add(command);
        }

        // 从已提交的时间和起步游标建立本物理步的请求，返回待提交的游标值。
        internal MotionStartToken ReadStep(double end, List<CharacterMotionSlice> result)
        {
            var cursor = StartCursor;
            Read(SubmittedThrough, end, ref cursor, result);
            return cursor;
        }

        // 用已求解点携带的起步游标预读，不受刚体尚未求解的提交影响。
        internal CharacterMotionSlice[] CopyPending(double solved, double frameTime, MotionStartToken solvedStartCursor)
        {
            if (frameTime <= solved) return Array.Empty<CharacterMotionSlice>();
            var copy = new List<CharacterMotionSlice>();
            Read(solved, frameTime, ref solvedStartCursor, copy);
            return copy.ToArray();
        }

        // 依次遍历命令边界与曲线阶段，并为首条请求以前的时段补待机。
        private void Read(double start, double end, ref MotionStartToken cursor, List<CharacterMotionSlice> result)
        {
            result.Clear();
            int index = -1;
            while (start < end)
            {
                if (start < KnockbackUntil)
                {
                    double returns = Math.Min(end, KnockbackUntil);
                    result.Add(new CharacterMotionSlice(start, returns, default, default));
                    start = returns;
                    continue;
                }
                while (index + 1 < commands.Count && commands[index + 1].Time <= start) index++;
                double next = index + 1 < commands.Count ? Math.Min(end, commands[index + 1].Time) : end;
                var command = index >= 0 ? commands[index] : default;
                var slice = command.SampleNext(start, next).ResolveStart(ref cursor);
                result.Add(slice);
                start = slice.End;
            }
        }

        // 成功提交刚体后同时推进时间与起步游标，并保留求解前可撤回的旧值。
        internal void MarkSubmitted(double through, MotionStartToken cursor)
        {
            previousThrough = SubmittedThrough;
            previousStartCursor = StartCursor;
            SubmittedThrough = through;
            StartCursor = cursor;
        }

        // 暂停发生于提交与求解之间时，把马达的两个游标退回上个已求解点。
        internal void CancelSubmission()
        {
            SubmittedThrough = previousThrough;
            StartCursor = previousStartCursor;
        }

        // 求解完成后只保留游标仍需要的命令及其后继。
        internal void MarkSolved()
        {
            int expired = 0;
            while (expired + 1 < commands.Count && commands[expired + 1].Time <= SubmittedThrough) expired++;
            if (expired > 0) commands.RemoveRange(0, expired);
        }

        // 重置角色时清空跨世代运动、马达游标和求解前的回退点。
        internal void Reset()
        {
            ClearCommands();
            SubmittedThrough = previousThrough = 0d;
        }
    }
}
