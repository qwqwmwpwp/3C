using System;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    // 同一个求值区间的角色截止、帧时间和 Animator 时间；各层、素材和 IK 使用同一份。
    internal readonly struct CharacterPlaybackFrameStep
    {
        internal readonly double End, Fraction;
        internal readonly float FrameDelta, GraphDelta;

        // 按本区间在剩余角色时间中的比例分配两种步长。
        internal CharacterPlaybackFrameStep(double end, double fraction, double frame, double graph)
        {
            End = end;
            Fraction = fraction;
            FrameDelta = (float)frame;
            GraphDelta = (float)graph;
        }
    }

    // 按渲染帧登记播放预算，只有实际图求值才扣账，重选、撤批及同素材重绑都沿用余量。
    internal sealed class CharacterPlaybackFrameBudget
    {
        // 一帧登记时的 Animator 倍率已经计入 Graph，跨帧撤批也保留每帧自己的时间换算。
        private struct Frame
        {
            internal double Start, End, Delta, Graph;

            // 只取截止点之前的份额，角色时间静止的渲染帧在其时间点消费一次。
            internal readonly double Through(double at) => at >= End ? 1d : at <= Start ? 0d : (at - Start) / (End - Start);
        }

        private readonly List<Frame> pending = new List<Frame>();
        private int? frameNumber;
        private double evaluatedAt, target;

        // 每个渲染帧只计入一次按 Animator 更新方式和速度换算的步长。
        internal void Capture(int frame, double now, float dt, float animationDt)
        {
            if (!frameNumber.HasValue) evaluatedAt = target = now - dt;
            if (frameNumber != frame)
            {
                pending.Add(new Frame { Start = target, End = now, Delta = dt, Graph = animationDt });
                frameNumber = frame;
            }
            else if (pending.Count > 0 && now != target)
            {
                int last = pending.Count - 1;
                var remaining = pending[last];
                remaining.End = now;
                pending[last] = remaining;
            }
            target = now;
        }

        // 跨帧未提交时按各帧各自的时间比例推进；零角色步长帧随当前时间点一起结算。
        internal double NextFrameEnd
        {
            get
            {
                foreach (var frame in pending)
                    if (frame.End > evaluatedAt) return frame.End;
                return target;
            }
        }

        // 查询到指定角色时刻的预算；同一候选重复查询不推进实际时钟。
        internal CharacterPlaybackFrameStep Until(double at)
        {
            double end = Math.Max(evaluatedAt, Math.Min(at, target));
            double fraction = end >= target ? 1d : (end - evaluatedAt) / (target - evaluatedAt);
            double dt = 0d, graph = 0d;
            foreach (var frame in pending)
            {
                double share = frame.Through(end);
                dt += share * frame.Delta;
                graph += share * frame.Graph;
                if (share < 1d) break;
            }
            return new CharacterPlaybackFrameStep(end, fraction, dt, graph);
        }

        internal CharacterPlaybackFrameStep Remaining => Until(target);

        // 原生图已经返回后确认这一段的截止与余时；延后重入从这一位置继续。
        internal void Commit(in CharacterPlaybackFrameStep step)
        {
            evaluatedAt = step.End;
            int complete = 0;
            foreach (var frame in pending)
            {
                if (frame.End > evaluatedAt) break;
                complete++;
            }
            if (complete > 0) pending.RemoveRange(0, complete);
            if (pending.Count == 0) return;
            var first = pending[0];
            double remainder = 1d - first.Through(evaluatedAt);
            first.Start = Math.Max(first.Start, evaluatedAt);
            first.Delta *= remainder;
            first.Graph *= remainder;
            pending[0] = first;
        }

        // 新角色代次从下一次采集建立独立的渲染时间基准。
        internal void Reset()
        {
            frameNumber = null;
            evaluatedAt = target = 0d;
            pending.Clear();
        }
    }
}
