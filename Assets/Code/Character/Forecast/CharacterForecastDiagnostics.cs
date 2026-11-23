#if UNITY_EDITOR
using System;
using System.Text;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>把预测发布时保存的检查点与随后真实提交的同一步比较，仅统计输入假设仍成立的窗口。</summary>
    internal sealed class CharacterForecastDiagnostics
    {
        private struct Check
        {
            internal bool Pending;
            internal long Step;
            internal CharacterForecastSample Sample;
            internal int Count;
            internal double PositionError, AngleError;
        }

        private readonly float[] horizons = { 0.3f, 1f, 2f };
        private readonly Check[] checks = new Check[3];
        private readonly Vector3[] path = new Vector3[128];
        private int pathHead, pathCount;
        private bool transitionPending;
        private long transitionStep, deadline, transitionError, transitions, missed;
        private CharacterForecastSample transition;
        private double elapsedMs, workerMs, publishMs;
        private int queryBatches;
        private long bytes;
        internal void ChangeAssumption()
        {
            for (int i = 0; i < checks.Length; i++) checks[i].Pending = false;
            transitionPending = false;
        }

        internal void Reset()
        {
            ChangeAssumption();
            Array.Clear(checks, 0, checks.Length);
            pathHead = pathCount = 0;
            transitions = missed = transitionError = 0;
        }

        internal void Published(CharacterMovementForecast data, float dt, long ticks, long allocated, long worker = 0, long elapsed = 0, int batches = 0)
        {
            elapsedMs = ticks * 1000d / System.Diagnostics.Stopwatch.Frequency;
            workerMs = worker * 1000d / System.Diagnostics.Stopwatch.Frequency;
            publishMs = elapsed * 1000d / System.Diagnostics.Stopwatch.Frequency;
            queryBatches = batches;
            bytes = allocated;
            for (int i = 0; i < horizons.Length; i++)
            {
                int steps = Mathf.RoundToInt(horizons[i] / dt);
                if (steps < 1 || checks[i].Pending || !data.TrySample(data.StartTime, steps * dt, out var sample)) continue;
                checks[i].Sample = sample;
                checks[i].Step = data.SourceStep + steps;
                checks[i].Pending = true;
            }
            if (transitionPending || !data.TrySample(data.StartTime, 0f, out var origin)) return;
            for (int i = 1; data.TrySample(data.StartTime, i * dt, out var sample); i++)
            {
                if (SameBehavior(origin, sample)) continue;
                transition = sample;
                transitionStep = data.SourceStep + i;
                deadline = transitionStep + Mathf.CeilToInt(0.25f / dt);
                transitionPending = true;
                break;
            }
        }

        private static bool SameBehavior(in CharacterForecastSample a, in CharacterForecastSample b) =>
            a.Behaviors == b.Behaviors && a.Attack == b.Attack && a.AttackPhase == b.AttackPhase &&
            a.MoveApproved == b.MoveApproved;

        internal void Observe(long step, in CharacterForecastSample actual)
        {
            path[pathHead] = actual.Position;
            pathHead = (pathHead + 1) % path.Length;
            pathCount = Mathf.Min(pathCount + 1, path.Length);
            for (int i = 0; i < checks.Length; i++)
            {
                ref var check = ref checks[i];
                if (!check.Pending || step < check.Step) continue;
                check.Pending = false;
                if (step != check.Step) continue;
                check.PositionError += Vector3.Distance(check.Sample.Position, actual.Position);
                check.AngleError += Quaternion.Angle(check.Sample.Rotation, actual.Rotation);
                check.Count++;
            }
            if (!transitionPending) return;
            if (SameBehavior(transition, actual))
            {
                transitionError += Math.Abs(step - transitionStep);
                transitions++;
                transitionPending = false;
            }
            else if (step >= deadline) { missed++; transitionPending = false; }
        }

        internal string Describe(CharacterMovementForecast data)
        {
            if (!data.Available) return "当前没有可用预测";
            var lines = new StringBuilder();
            lines.AppendLine($"版本 {data.Version} · 来源步 {data.SourceStep} · {data.RefreshReason} · 有效 {data.ValidSeconds:F2} 秒");
            lines.AppendLine($"主线程 {elapsedMs:F2} ms，托管分配 {bytes} B");
            if (publishMs > 0d) lines.AppendLine($"工作线程推演 {workerMs:F2} ms，物理查询 {queryBatches} 批，发布延时 {publishMs:F2} ms");
            for (int i = 0; i < horizons.Length; i++)
            {
                var c = checks[i];
                lines.AppendLine(c.Count == 0 ? $"{horizons[i]:0.0}s · 等待可对齐样本" :
                    $"{horizons[i]:0.0}s · 平均 {100d * c.PositionError / c.Count:F1} cm / {c.AngleError / c.Count:F1}°（{c.Count} 次）");
            }
            lines.Append($"行为切换 {transitions} 次，平均误差 {(transitions == 0 ? 0d : (double)transitionError / transitions):F1} 步，超窗 {missed} 次");
            return lines.ToString();
        }

        internal int CopyActualPath(Vector3[] destination)
        {
            int size = Mathf.Min(pathCount, destination.Length);
            for (int i = 0; i < size; i++)
                destination[i] = path[(pathHead + path.Length - size + i) % path.Length];
            return size;
        }
    }
}
#endif
