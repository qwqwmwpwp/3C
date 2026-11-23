#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 固定输入回放专用。逐帧记录值，字符串和分位数只在调试窗口读取时生成。
    internal sealed class CharacterReplayTrace
    {
        private struct Sample
        {
            public int Frame;
            public long Step;
            public CharacterAnimationDebugLayer Upper, Lower;
            public CharacterDebugHandoff Mix;
            public float HandDistance, Grip, GripIk;
            public float? LeftSlip, RightSlip;
            public int MixIn, MixOut, ClipIn, ClipOut;
            public long Bytes, Ticks, NodeTicks;
        }

        public readonly bool ReuseMixers;
        public readonly CharacterReplayEvents Events = new CharacterReplayEvents();
        private readonly List<Sample> frames = new List<Sample>(1024);
        private long start, allocations;
        private CharacterPlaybackCounters startNodes;
        private long p95, totalNodes, totalBytes;
        private int mixersMade;
        private string report;
        private Sample sample;
        private int dropped;
        public int Count => frames.Count;
        public bool CompleteSamples => frames.Count > 0 && dropped == 0;
        public bool HasSeenGrip { get; private set; }

        public CharacterReplayTrace(bool reuseMixers) => ReuseMixers = reuseMixers;

        public void Begin(CharacterPlaybackCounters nodes)
        {
            sample = new Sample { Frame = Time.frameCount };
            startNodes = nodes;
            if (nodes != null) SetNodes(nodes, -1);
            allocations = GC.GetAllocatedBytesForCurrentThread();
            start = Stopwatch.GetTimestamp();
        }

        public void End(CharacterPlaybackCounters nodes)
        {
            sample.Ticks = Stopwatch.GetTimestamp() - start;
            sample.Bytes = GC.GetAllocatedBytesForCurrentThread() - allocations;
            if (nodes == startNodes && nodes != null) SetNodes(nodes, 1);
            else { sample.MixIn = sample.MixOut = sample.ClipIn = sample.ClipOut = 0; sample.NodeTicks = 0; }
        }

        private void SetNodes(CharacterPlaybackCounters n, int sign)
        {
            sample.NodeTicks += sign * n.NodeTicks;
            sample.MixIn += sign * n.MixersCreated; sample.MixOut += sign * n.MixersDestroyed;
            sample.ClipIn += sign * n.ClipsCreated; sample.ClipOut += sign * n.ClipsDestroyed;
        }

        public void Observe(CharacterAnimationPipeline pipeline, float? leftSlip, float? rightSlip)
        {
            var current = pipeline.ReplaySnapshot;
            if (current.Status != CharacterAnimationDebugStatus.Running || current.Handoff.RenderFrame != sample.Frame ||
                sample.Frame != Time.frameCount) return;
            sample.Step = current.Motion.FrameNumber;
            sample.Upper = current.UpperBody;
            sample.Lower = current.LowerBody;
            sample.Mix = current.Handoff;
            if (!pipeline.Ik.TryReadGrip(out sample.HandDistance, out sample.Grip, out sample.GripIk)) sample.HandDistance = float.NaN;
            HasSeenGrip |= sample.Grip > 0f;
            sample.LeftSlip = leftSlip;
            sample.RightSlip = rightSlip;
            if (frames.Count < 8192) frames.Add(sample);
            else dropped++;
        }

        private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

        private static string Foot(float? distance) => distance.HasValue ? $"{distance.Value * 100f:F2} cm" : "无连续承重";

        public string FrameAt(int index)
        {
            var f = frames[index];
            string hand = float.IsNaN(f.HandDistance) ? "未绑定" : $"{100f * f.HandDistance:F2} cm";
            return $"渲染帧 {f.Frame} / 固定步 {f.Step} · {f.Mix.Attack.Move} #{f.Mix.Attack.InstanceId}\n" +
                $"上身 {f.Upper.Key} #{f.Upper.InstanceId}，下身 {f.Lower.Key} #{f.Lower.InstanceId}\n" +
                $"可见贡献 上身局部 {f.Mix.UpperWeight:P1} / 下身局部 {f.Mix.LowerWeight:P1} / 共享 {f.Mix.Coverage:P1}\n" +
                $"手至握点 {hand}，握持姿势 {f.Grip:P1} / 实际握点 IK {f.GripIk:P1}，本帧滑移 左 {Foot(f.LeftSlip)} / 右 {Foot(f.RightSlip)}\n" +
                $"动画更新 {Milliseconds(f.Ticks):F3} ms（节点创建/销毁 {Milliseconds(f.NodeTicks):F3} ms），主线程分配 {f.Bytes} B，混合器 +{f.MixIn}/−{f.MixOut}，片段 +{f.ClipIn}/−{f.ClipOut}" + Events.FrameAt(f.Frame);
        }

        public string Difference(CharacterReplayTrace other)
        {
            Summary(); other.Summary();
            var reused = ReuseMixers ? this : other;
            var original = ReuseMixers ? other : this;
            return $"复用减去销毁：混合器新建 {reused.mixersMade - original.mixersMade:+0;-0;0}，P95 {Milliseconds(reused.p95 - original.p95):+0.000;-0.000;0} ms，" +
                $"节点操作累计 {Milliseconds(reused.totalNodes - original.totalNodes):+0.000;-0.000;0} ms，主线程分配 {reused.totalBytes - original.totalBytes:+0;-0;0} B";
        }

        public string Summary()
        {
            if (report != null) return report;
            if (frames.Count == 0) return "尚无有效渲染帧";
            var times = new long[frames.Count];
            long bytes = 0, nodePeak = 0;
            int mi = 0, mo = 0, ci = 0, co = 0, peakFrame = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i]; times[i] = f.Ticks;
                totalNodes += f.NodeTicks;
                bytes += f.Bytes; mi += f.MixIn; mo += f.MixOut; ci += f.ClipIn; co += f.ClipOut;
                if (f.MixIn + f.MixOut + f.ClipIn + f.ClipOut > 0 && f.Ticks > nodePeak)
                { nodePeak = f.Ticks; peakFrame = f.Frame; }
            }
            Array.Sort(times);
            mixersMade = mi; totalBytes = bytes; p95 = times[Mathf.CeilToInt(times.Length * 0.95f) - 1];
            return report = $"{(ReuseMixers ? "复用" : "按需销毁")} · {times.Length} 帧，P95 {Milliseconds(p95):F3} ms，" +
                $"峰值 {Milliseconds(times[times.Length - 1]):F3} ms，节点变更帧峰值 {Milliseconds(nodePeak):F3} ms（帧 {peakFrame}）\n" +
                $"混合器 +{mi}/−{mo}，片段 +{ci}/−{co}，节点操作累计 {Milliseconds(totalNodes):F3} ms，编辑器动画更新主线程分配 {bytes} B（含回放探针）" +
                (dropped == 0 ? "" : $"，超出记录容量 {dropped} 帧");
        }
    }
}
#endif
