using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>Unity 调度胶囊和射线两个独立批次。主线程在完成后处理碰撞对和刚体引用。</summary>
    internal sealed class CharacterForecastCastBatch : IDisposable
    {
        private static readonly ProfilerMarker schedule = new ProfilerMarker("3C.Forecast.ScheduleQueries");
        private readonly List<ForecastQuery> rays = new List<ForecastQuery>(), sweeps = new List<ForecastQuery>();
        private NativeArray<RaycastCommand> rayCommands;
        private NativeArray<CapsulecastCommand> sweepCommands;
        private NativeArray<RaycastHit> rayHits, sweepHits;
        private JobHandle handle;
        private int capacity = 4;
        internal bool Ready => handle.IsCompleted;

        internal void Start(List<ForecastQuery> queries, CharacterForecastGeometry geometry)
        {
            using var profile = schedule.Auto();
            rays.Clear();
            sweeps.Clear();
            foreach (var query in queries) (query.Ray ? rays : sweeps).Add(query);
            var filter = new QueryParameters(geometry.Mask, false, QueryTriggerInteraction.Ignore, Physics.queriesHitBackfaces);
            try
            {
                if (rays.Count > 0)
                {
                    rayCommands = new NativeArray<RaycastCommand>(rays.Count, Allocator.Persistent);
                    rayHits = new NativeArray<RaycastHit>(checked(rays.Count * capacity), Allocator.Persistent);
                    for (int i = 0; i < rays.Count; i++)
                    {
                        var q = rays[i];
                        rayCommands[i] = new RaycastCommand(geometry.Scene, q.A, q.Direction, filter, q.Distance);
                    }
                    handle = RaycastCommand.ScheduleBatch(rayCommands, rayHits, 16, capacity);
                }
                if (sweeps.Count > 0)
                {
                    sweepCommands = new NativeArray<CapsulecastCommand>(sweeps.Count, Allocator.Persistent);
                    sweepHits = new NativeArray<RaycastHit>(checked(sweeps.Count * capacity), Allocator.Persistent);
                    for (int i = 0; i < sweeps.Count; i++)
                    {
                        var q = sweeps[i];
                        sweepCommands[i] = new CapsulecastCommand(geometry.Scene, q.A, q.B, q.Radius, q.Direction, filter, q.Distance);
                    }
                    var sweepsDone = CapsulecastCommand.ScheduleBatch(sweepCommands, sweepHits, 16, capacity);
                    handle = JobHandle.CombineDependencies(handle, sweepsDone);
                }
                JobHandle.ScheduleBatchedJobs();
            }
            catch { Dispose(); throw; }
        }

        internal bool Read(CharacterForecastProbePass pass, CharacterForecastGeometry geometry)
        {
            handle.Complete();
            pass.Missing.Clear();
            ReadGroup(rays, rayHits, pass, geometry);
            ReadGroup(sweeps, sweepHits, pass, geometry);
            Dispose();
            if (pass.Missing.Count == 0) return true;
            capacity = checked(capacity * 2);
            Start(pass.Missing, geometry);
            return false;
        }

        private void ReadGroup(List<ForecastQuery> queries, NativeArray<RaycastHit> hits,
            CharacterForecastProbePass pass, CharacterForecastGeometry geometry)
        {
            for (int i = 0; i < queries.Count; i++)
            {
                var query = queries[i];
                var nearest = default(ForecastHit);
                int n = 0;
                for (; n < capacity; n++)
                {
                    var hit = hits[i * capacity + n];
                    if (hit.colliderInstanceID == 0) break;
                    if (!hit.collider || nearest.Found && nearest.Distance <= hit.distance ||
                        !geometry.Accept(hit, query.Ray, query.Direction)) continue;
                    nearest = new ForecastHit(hit);
                }
                if (n == capacity && capacity >= 32) pass.Answers.Add(query, geometry.ResolveSaturated(query));
                else if (n == capacity) pass.Missing.Add(query);
                else pass.Answers.Add(query, nearest);
            }
        }

        public void Dispose()
        {
            handle.Complete();
            handle = default;
            if (rayCommands.IsCreated) rayCommands.Dispose();
            if (rayHits.IsCreated) rayHits.Dispose();
            if (sweepCommands.IsCreated) sweepCommands.Dispose();
            if (sweepHits.IsCreated) sweepHits.Dispose();
        }
    }
}
