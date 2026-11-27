exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterPlaybackFrameBudget')
sub('using System;','using System;\nusing System.Collections.Generic;')
sub('FrameDelta = (float)(frame * fraction);\n            GraphDelta = (float)(graph * fraction);', 'FrameDelta = (float)frame;\n            GraphDelta = (float)graph;')
sub('        private int? frameNumber;\n        private double evaluatedAt, target, remainingFrame, remainingGraph;', '''        // 一帧登记时的 Animator 倍率已经计入 Graph，跨帧撤批也保留每帧自己的时间换算。
        private struct Frame
        {
            internal double Start, End, Delta, Graph;

            // 只取截止点之前的份额，角色时间静止的渲染帧在其时间点消费一次。
            internal readonly double Through(double at) => at >= End ? 1d : at <= Start ? 0d : (at - Start) / (End - Start);
        }

        private readonly List<Frame> pending = new List<Frame>();
        private int? frameNumber;
        private double evaluatedAt, target;''')
cut('            if (!frameNumber.HasValue) evaluatedAt = now - dt;', '        // 查询到指定角色时刻', '''            if (!frameNumber.HasValue) evaluatedAt = target = now - dt;
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

''')
sub('return new CharacterPlaybackFrameStep(end, fraction, remainingFrame, remainingGraph);','''double dt = 0d, graph = 0d;
            foreach (var frame in pending)
            {
                double share = frame.Through(end);
                dt += share * frame.Delta;
                graph += share * frame.Graph;
                if (share < 1d) break;
            }
            return new CharacterPlaybackFrameStep(end, fraction, dt, graph);''')
sub('''            remainingFrame *= 1d - step.Fraction;
            remainingGraph *= 1d - step.Fraction;''','''            int complete = 0;
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
            pending[0] = first;''')
sub('evaluatedAt = target = remainingFrame = remainingGraph = 0d;','evaluatedAt = target = 0d;\n            pending.Clear();')
write()
use('CharacterAnimationPlayer')
sub('''                var step = budget.Until(to);''','''                if (budget.NextFrameEnd > from) to = System.Math.Min(to, budget.NextFrameEnd);
                var step = budget.Until(to);''')
write()

