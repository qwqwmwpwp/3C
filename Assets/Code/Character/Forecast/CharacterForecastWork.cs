using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityEngine.LowLevel;

namespace ThreeC.CharacterBehavior
{
    /// <summary>单次预测拥有独立起点、行为副本和结果区。每个查询批次完成后，从同一起点校正推演。</summary>
    internal sealed class CharacterForecastWork : IDisposable
    {
        private static readonly ProfilerMarker rollout = new ProfilerMarker("3C.Forecast.Rollout");
        internal readonly CharacterMovementForecast Result = new CharacterMovementForecast();
        // 只在主线程记录并核对，工作线程仅读取起点副本。
        internal readonly List<(double time, CharacterForecastSample pose)> Observed =
            new List<(double, CharacterForecastSample)>();
        internal readonly CharacterForecastFrame Frame;
        internal bool Released { get; private set; }
        private readonly CharacterBehaviorFlow seed, simulation;
        private readonly CharacterForecastGeometry geometry;
        private readonly CharacterForecastProbePass pass;
        private readonly CharacterForecastCastBatch casts = new CharacterForecastCastBatch();
        private readonly ForecastRefreshReason reason;
        private readonly Action compute;
        private Task worker;
        private bool querying;
        internal bool FinishedUsingBuffers => querying ? casts.Ready : worker == null || worker.IsCompleted;
#if UNITY_EDITOR
        private readonly long start = Stopwatch.GetTimestamp();
        private long mainAllocated;
        internal long MainTicks { get; private set; }
        internal long WorkerTicks { get; private set; }
        internal int Batches { get; private set; }
        internal void RecordSchedule(long ticks, long allocated)
        {
            MainTicks += Stopwatch.GetTimestamp() - ticks;
            mainAllocated += GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
        internal long ElapsedTicks => Stopwatch.GetTimestamp() - start;
        internal long MainAllocated => mainAllocated;
#endif

        // 在主线程捕获角色附近的几何和物理查询形状，再让后台使用独立的行为副本和结果缓冲。
        internal CharacterForecastWork(CharacterBehaviorFlow seed, CharacterBehaviorFlow simulation,
            CharacterForecastGeometry geometry, in CharacterForecastFrame frame, ForecastRefreshReason reason)
        {
            this.seed = seed;
            this.simulation = simulation;
            this.geometry = geometry;
            Frame = frame;
            this.reason = reason;
            float speed = UnityEngine.Mathf.Max(frame.Origin.Pose.Velocity.magnitude, frame.Movement.DashSpeed);
            float reach = UnityEngine.Mathf.Clamp(2f + speed * frame.Window.Step * frame.Window.Steps, 3f, 48f);
            pass = new CharacterForecastProbePass(geometry.CaptureShape(), new CharacterForecastWorld(geometry, frame.Origin.Pose.Position, reach));
            compute = Compute;
            worker = Task.Run(compute);
        }

        // 每轮查询校正均从同一帧末行为副本和同一已求解起点重新推演。
        private void Compute()
        {
            using var sample = rollout.Auto();
#if UNITY_EDITOR
            long begin = Stopwatch.GetTimestamp();
#endif
            pass.BeginPass();
            seed.CopyStateTo(simulation);
            CharacterForecastRollout.Simulate(simulation, pass, Result, Frame, reason);
#if UNITY_EDITOR
            WorkerTicks += Stopwatch.GetTimestamp() - begin;
#endif
        }

        internal bool Poll(bool discard)
        {
#if UNITY_EDITOR
            long tick = Stopwatch.GetTimestamp();
            long before = GC.GetAllocatedBytesForCurrentThread();
            try { return Advance(discard); }
            finally { RecordSchedule(tick, before); }
#else
            return Advance(discard);
#endif
        }

        private bool Advance(bool discard)
        {
            if (querying)
            {
                if (!casts.Ready) return false;
                if (discard) return true;
                if (!casts.Read(pass, geometry)) return false;
                querying = false;
                if (pass.Confirmed()) return true;
                worker = Task.Run(compute);
                return false;
            }
            if (!worker.IsCompleted) return false;
            var completed = worker;
            worker = null;
            completed.GetAwaiter().GetResult();
            if (discard || pass.Missing.Count == 0) return true;
#if UNITY_EDITOR
            Batches++;
#endif
            casts.Start(pass.Missing, geometry);
            querying = true;
            return false;
        }

        // 等待后台读取和物理作业结束后归还缓冲，通知调度器可启动下一项工作。
        public void Dispose()
        {
            try { worker?.GetAwaiter().GetResult(); }
            finally { casts.Dispose(); Released = true; }
        }
    }

    /// <summary>角色立即撤销旧版本，物理查询和工作线程结束后再由主线程归还缓冲。</summary>
    internal static class CharacterForecastRetirement
    {
        private static readonly List<CharacterForecastWork> retired = new List<CharacterForecastWork>();
        private static bool closing;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            DrainAll();
            closing = false;
            UnityEngine.Application.quitting -= DrainAll;
            UnityEngine.Application.quitting += DrainAll;
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= DrainAll;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += DrainAll;
#endif
        }

        // 在主循环末尾安装预测回收，无需场景角色或动画批处理参与。
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallCollector()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (AppendCollector(ref loop)) PlayerLoop.SetPlayerLoop(loop);
        }

        // 保留当前主循环的阶段顺序；关闭域重载时也只登记一个回收入口。
        private static bool AppendCollector(ref PlayerLoopSystem loop)
        {
            var systems = loop.subSystemList ?? Array.Empty<PlayerLoopSystem>();
            foreach (var system in systems)
                if (system.type == typeof(CharacterForecastRetirement)) return false;

            Array.Resize(ref systems, systems.Length + 1);
            systems[systems.Length - 1] = new PlayerLoopSystem { type = typeof(CharacterForecastRetirement), updateDelegate = Collect };
            loop.subSystemList = systems;
            return true;
        }

        // 已完成的任务立即归还缓冲，其余任务留给全局主线程回收。
        internal static void Retire(CharacterForecastWork work)
        {
            if (work == null) return;
            if (closing || work.FinishedUsingBuffers)
            {
                try { work.Dispose(); }
                catch (Exception error) { UnityEngine.Debug.LogException(error); }
            }
            else retired.Add(work);
        }

        // 仅在工作线程或物理查询已完成时，由主线程释放退役任务的缓冲。
        private static void Collect()
        {
            for (int i = retired.Count - 1; i >= 0; i--)
                if (retired[i].FinishedUsingBuffers) DisposeAt(i);
        }

        private static void DrainAll()
        {
            closing = true;
            while (retired.Count > 0) DisposeAt(retired.Count - 1);
        }

        private static void DisposeAt(int i)
        {
            var work = retired[i];
            retired.RemoveAt(i);
            try { work.Dispose(); }
            catch (Exception error) { UnityEngine.Debug.LogException(error); }
        }
    }

}
