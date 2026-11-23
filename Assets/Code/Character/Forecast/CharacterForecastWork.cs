using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Profiling;

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
        internal double ExpiresAt => origin.Time + steps * (double)dt;
        private readonly CharacterBehaviorFlow seed, simulation;
        private readonly CharacterForecastGeometry geometry;
        private readonly CharacterForecastProbePass pass;
        private readonly CharacterForecastCastBatch casts = new CharacterForecastCastBatch();
        private readonly CharacterForecastOrigin origin;
        private readonly CharacterForecastInputs inputs;
        private readonly CharacterMovementSettings movement;
        private readonly CharacterDriveParameters drive;
        private readonly float dt, horizon, braking;
        private readonly double controlReturnsAt;
        private readonly int steps;
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

        internal CharacterForecastWork(CharacterBehaviorFlow seed, CharacterBehaviorFlow simulation,
            CharacterForecastGeometry geometry, CharacterForecastOrigin origin, CharacterForecastInputs inputs,
            CharacterMovementSettings movement, CharacterDriveParameters drive, float dt, int steps, float horizon,
            double controlReturnsAt, float braking, ForecastRefreshReason reason)
        {
            this.seed = seed;
            this.simulation = simulation;
            this.geometry = geometry;
            this.origin = origin;
            this.inputs = inputs;
            this.movement = movement;
            this.drive = drive;
            this.dt = dt;
            this.steps = steps;
            this.horizon = horizon;
            this.controlReturnsAt = controlReturnsAt;
            this.braking = braking;
            this.reason = reason;
            float speed = UnityEngine.Mathf.Max(origin.Pose.Velocity.magnitude, movement.DashSpeed);
            float reach = UnityEngine.Mathf.Clamp(2f + speed * dt * steps, 3f, 48f);
            pass = new CharacterForecastProbePass(geometry.CaptureShape(), new CharacterForecastWorld(geometry, origin.Pose.Position, reach));
            compute = Compute;
            worker = Task.Run(compute);
        }

        private void Compute()
        {
            using var sample = rollout.Auto();
#if UNITY_EDITOR
            long begin = Stopwatch.GetTimestamp();
#endif
            pass.BeginPass();
            seed.CopyStateTo(simulation);
            Result.Begin(steps + 1, origin.Time, origin.Number, dt, horizon, reason);
            Result.Append(origin.Time < controlReturnsAt ? origin.Pose.UnderExternalControl() : origin.Pose);
            CharacterForecastRollout.Simulate(simulation, pass, Result, controlReturnsAt, braking,
                origin, inputs, movement, drive, dt, steps);
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

        public void Dispose()
        {
            try { worker?.GetAwaiter().GetResult(); }
            finally { casts.Dispose(); }
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

        internal static void Retire(CharacterForecastWork work)
        {
            if (work == null) return;
            if (closing || work.FinishedUsingBuffers) work.Dispose();
            else retired.Add(work);
        }

        internal static void Collect()
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
