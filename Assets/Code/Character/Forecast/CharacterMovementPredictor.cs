using System;
using UnityEngine;
using UnityEngine.Profiling;

namespace ThreeC.CharacterBehavior
{
    internal readonly struct CharacterForecastOrigin
    {
        internal readonly double Time;
        internal readonly long Number;
        internal readonly CharacterForecastSample Pose;
        internal readonly CharacterMotorStep Motor;
        internal readonly Vector3 Ground;
        internal readonly CharacterSlopeContacts Steep;
        internal readonly bool DynamicContact;

        internal CharacterForecastOrigin(double time, long number, CharacterForecastSample pose, CharacterMotorStep motor,
            Vector3 ground, CharacterSlopeContacts steep, bool dynamicContact)
        {
            Time = time;
            Number = number;
            Pose = pose;
            Motor = motor;
            Ground = ground;
            Steep = steep;
            DynamicContact = dynamicContact;
        }
    }

    internal readonly struct CharacterForecastInputs
    {
        private readonly CharacterInputSnapshot locked, held;
        internal readonly CharacterInputSnapshot Buffered;

        internal CharacterForecastInputs(CharacterInputSnapshot locked, CharacterInputSnapshot buffered)
        {
            this.locked = locked;
            Buffered = buffered;
            held = new CharacterInputSnapshot(buffered.Direction, false, buffered.GuardHeld, CharacterBehaviorId.None, buffered.Gait);
        }

        internal CharacterInputSnapshot AtStep(int index) => index == 0 ? locked : index == 1 ? Buffered : held;
    }

    /// <summary>从真实步末状态重建时间序列，只向行为副本、移动值副本和样本缓冲写入。</summary>
    internal sealed class CharacterMovementPredictor
    {
#if UNITY_EDITOR
        internal readonly CharacterForecastDiagnostics Diagnostics = new CharacterForecastDiagnostics();
#endif
        internal readonly CharacterMovementForecast Output = new CharacterMovementForecast();
        private readonly CharacterBehaviorFlow simulation;
        private readonly CharacterForecastGeometry geometry;
        private readonly Func<CharacterBehaviorFlow> createCopy;
        private CharacterForecastWork pending;
        private bool obsolete, readyToPublish, lastDynamicContact;
        private float lastHorizon;
        private CharacterInputSnapshot lastInput;
        private float lastInterval, lastDt;
        private CharacterGroundSteering lastSteering;
        private double nextRefresh;
        internal bool NeedsRefresh => dirty != ForecastRefreshReason.None && !suppressed && !invalidSampling;
        private bool invalidSampling;
        private ForecastRefreshReason dirty;
        private bool suppressed;
        private double controlReturnsAt;
        private float forcedBraking;

        internal void ConfirmKnockback(double now, float seconds, float deceleration)
        {
            bool takingControl = now >= controlReturnsAt;
            controlReturnsAt = now + InputMath.NonNegative(seconds, 0f);
            forcedBraking = InputMath.NonNegative(deceleration, 0f);
            Invalidate(ForecastRefreshReason.Knockback, takingControl && controlReturnsAt > now
                ? CharacterForecastRevision.Takeover : CharacterForecastRevision.Invalidated);
        }

        internal void ClearControlLock() => controlReturnsAt = 0d;

        internal CharacterMovementPredictor(CharacterBehaviorFlow simulation, CharacterForecastGeometry geometry,
            Func<CharacterBehaviorFlow> createCopy = null)
        {
            this.simulation = simulation;
            this.geometry = geometry;
            this.createCopy = createCopy;
        }

        // 旧预测在输入假设变化后立即失效，先于动画读取。
        internal void RequestRefresh(ForecastRefreshReason reason, bool clearPublished = true)
        {
#if UNITY_EDITOR
            Diagnostics.ChangeAssumption();
#endif
            dirty = reason;
            obsolete = pending != null;
            if (clearPublished && Output.Available) Output.Clear(CharacterForecastRevision.Invalidated);
        }

        internal void Invalidate(ForecastRefreshReason reason, CharacterForecastRevision revision = CharacterForecastRevision.Invalidated)
        {
            RequestRefresh(reason, false);
            Output.Clear(revision);
        }

        internal void Suppress(bool value)
        {
            if (value == suppressed) return;
            suppressed = value;
            if (value && pending != null)
            {
                var work = pending;
                pending = null;
                CharacterForecastRetirement.Retire(work);
            }
            Invalidate(value ? ForecastRefreshReason.Suppressed : ForecastRefreshReason.Resumed, value ? CharacterForecastRevision.Withdraw : CharacterForecastRevision.Reset);
        }

        internal bool WaitingForValidWindow(double now, double originTime, CharacterForecastSettings config,
            CharacterProfileOverrides overrides) =>
            invalidSampling && !ReadWindow(now, originTime, config, overrides, out _, out _, out _, out _);

        private static bool ReadWindow(double now, double originTime, CharacterForecastSettings config,
            CharacterProfileOverrides overrides, out float dt, out float interval, out float horizon, out int steps)
        {
            dt = Time.fixedDeltaTime;
            interval = InputMath.NonNegative(overrides.ForecastInterval.Resolve(config.Interval), 0.2f);
            horizon = InputMath.NonNegative(overrides.ForecastHorizon.Resolve(config.Horizon), 2f);
            // 加上发布间隔和渲染帧余量，保证从当前时刻能采到完整窗口。
            double count = dt > 0f ? Math.Ceiling((horizon + (double)interval + Math.Max(0d, now - originTime)) / dt) + 1d : 0d;
            steps = count > 0d && count <= 8192 ? (int)count : 0;
            return steps > 0 && horizon > 0f;
        }

        internal void Refresh(double now, in CharacterForecastOrigin origin, CharacterBehaviorFlow live,
            in CharacterForecastInputs inputs, CharacterMovementSettings movement, in CharacterDriveParameters drive, CharacterForecastSettings config,
            CharacterProfileOverrides overrides, bool threaded = true)
        {
            if (suppressed) return;
            if (!ReadWindow(now, origin.Time, config, overrides, out float dt, out float interval, out float horizon, out int steps))
            {
                if (!invalidSampling)
                {
                    invalidSampling = true;
                    var work = pending;
                    pending = null;
                    obsolete = readyToPublish = false;
                    dirty = ForecastRefreshReason.None;
                    try { CharacterForecastRetirement.Retire(work); }
                    finally { Output.Clear(CharacterForecastRevision.Reset); }
                }
                return;
            }
            if (!threaded && pending != null) Release();
            if (invalidSampling) dirty = ForecastRefreshReason.Settings;
            invalidSampling = false;
            if (origin.DynamicContact) steps = 0;
            if (origin.DynamicContact && pending != null)
            {
                var previous = pending;
                pending = null;
                readyToPublish = obsolete = false;
                CharacterForecastRetirement.Retire(previous);
            }
            var steering = new CharacterGroundSteering(inputs.Buffered, drive);
            ForecastRefreshReason reason = origin.DynamicContact != lastDynamicContact ? ForecastRefreshReason.DynamicContact :
                Reason(now, origin.Time, origin.Pose, inputs.Buffered, config, dt, interval, horizon, steering);
            if (pending != null)
            {
                if (now > pending.ExpiresAt) reason = ForecastRefreshReason.Expired;
                if (reason != ForecastRefreshReason.None && reason != ForecastRefreshReason.Scheduled)
                {
                    obsolete = true;
                    dirty = reason;
                    if (Output.Available) Output.Clear(CharacterForecastRevision.Invalidated);
                }
                if (!Collect(origin)) return;
                reason = Reason(now, origin.Time, origin.Pose, inputs.Buffered, config, dt, interval, horizon, steering);
            }
            if (reason == ForecastRefreshReason.None) return;
#if UNITY_EDITOR
            if (reason == ForecastRefreshReason.MovementInput || reason == ForecastRefreshReason.Settings) Diagnostics.ChangeAssumption();
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
#endif
            lastHorizon = horizon;
            lastDynamicContact = origin.DynamicContact;
            if (threaded && createCopy != null && reason != ForecastRefreshReason.Scheduled && Output.Available)
                Output.Clear(CharacterForecastRevision.Invalidated);
            if (!origin.DynamicContact && threaded && createCopy != null)
            {
                geometry.Begin(drive.Settings);
                var seed = createCopy();
                live.CopyStateTo(seed);
                if (origin.Time < controlReturnsAt) seed.ReleasePredictionActions();
                pending = new CharacterForecastWork(seed, createCopy(), geometry, origin, inputs, movement.Snapshot(),
                    drive.Snapshot(), dt, steps, horizon, controlReturnsAt, forcedBraking, reason);
#if UNITY_EDITOR
                pending.RecordSchedule(ticks, allocated);
#endif
                obsolete = readyToPublish = false;
                lastDt = dt;
                lastInterval = interval;
                lastInput = inputs.Buffered;
                lastSteering = steering;
                nextRefresh = now + interval;
                dirty = ForecastRefreshReason.None;
                return;
            }
            Profiler.BeginSample("3C.MovementForecast");
            try
            {
                Output.Begin(steps + 1, origin.Time, origin.Number, dt, horizon, reason);
                Output.Append(origin.Time < controlReturnsAt ? origin.Pose.UnderExternalControl() : origin.Pose);
                if (origin.DynamicContact) Output.EndAtContact();
                else
                {
                    geometry.Begin(drive.Settings);
                    live.CopyStateTo(simulation);
                    if (origin.Time < controlReturnsAt) simulation.ReleasePredictionActions();
                    CharacterForecastRollout.Simulate(simulation, geometry, Output, controlReturnsAt, forcedBraking,
                        origin, inputs, movement, drive, dt, steps);
                }
                nextRefresh = now + interval;
                lastInterval = interval;
                lastDt = dt;
                lastSteering = steering;
                lastInput = inputs.Buffered;
                dirty = ForecastRefreshReason.None;
            }
            finally { Profiler.EndSample(); }
#if UNITY_EDITOR
            Diagnostics.Published(Output, dt, System.Diagnostics.Stopwatch.GetTimestamp() - ticks,
                GC.GetAllocatedBytesForCurrentThread() - allocated);
#endif
            Output.Notify(CharacterForecastRevision.Published);
        }

        // 物理与 LateUpdate 间隙只推进就绪的查询批次。发布仍统一在采集新输入之后核对真实起点。
        internal void Pump()
        {
            if (pending == null || readyToPublish) return;
            try { readyToPublish = pending.Poll(obsolete); }
            catch
            {
                Release();
                throw;
            }
        }

        // 运行时只轮询预测。编辑器预演使用同一个数值内核同步求解。
        private bool Collect(in CharacterForecastOrigin origin)
        {
            var work = pending;
            try
            {
                if (!readyToPublish && !work.Poll(obsolete)) return false;
                readyToPublish = false;
                pending = null;
                if (obsolete) return true;
                var path = work.Result;
                foreach (var check in work.Observed)
                {
                    if (!path.TrySample(check.time, 0f, out var predicted)) continue;
                    ForecastRefreshReason change = Difference(check.pose, predicted);
                    if (change != ForecastRefreshReason.None) { Invalidate(change); return true; }
                }
                if (path.TrySample(origin.Time, 0f, out var expected))
                {
                    ForecastRefreshReason difference = Difference(origin.Pose, expected);
                    if (difference != ForecastRefreshReason.None) { Invalidate(difference); return true; }
                }
                else if (origin.Time > path.StartTime + path.ValidSeconds && !path.EnvironmentLimited)
                {
                    Invalidate(ForecastRefreshReason.Expired);
                    return true;
                }
                Output.Publish(path);
#if UNITY_EDITOR
                Diagnostics.Published(Output, lastDt, work.MainTicks, work.MainAllocated, work.WorkerTicks, work.ElapsedTicks, work.Batches);
#endif
                return true;
            }
            catch
            {
                pending = null;
                dirty = ForecastRefreshReason.Retry;
                throw;
            }
            finally { if (pending != work) work.Dispose(); }
        }

        internal void Release()
        {
            invalidSampling = false;
            if (pending == null && !Output.Available && dirty == ForecastRefreshReason.SimulationBoundary) return;
            var work = pending;
            pending = null;
            try { CharacterForecastRetirement.Retire(work); }
            finally { Invalidate(ForecastRefreshReason.SimulationBoundary, CharacterForecastRevision.Reset); }
        }

        private ForecastRefreshReason Reason(double now, double solvedTime, CharacterForecastSample start, CharacterInputSnapshot buffered,
            CharacterForecastSettings config, float dt, float interval, float horizon, in CharacterGroundSteering steering)
        {
            if (dirty != ForecastRefreshReason.None) return dirty;
            if (!Output.Available && pending == null) return ForecastRefreshReason.Initial;
            if (dt != lastDt || interval != lastInterval || horizon != lastHorizon ||
                !steering.SameTuning(lastSteering)) return ForecastRefreshReason.Settings;
            float threshold = Mathf.Max(0.001f, InputMath.NonNegative(config.DirectionTolerance, 0.05f));
            if (buffered.Gait != lastInput.Gait || buffered.GuardHeld != lastInput.GuardHeld ||
                (buffered.Direction == Vector2.zero) != (lastInput.Direction == Vector2.zero) ||
                (buffered.Direction - lastInput.Direction).sqrMagnitude >= threshold * threshold) return ForecastRefreshReason.MovementInput;
            if (Output.TrySample(solvedTime, 0f, out var expected))
            {
                ForecastRefreshReason difference = Difference(start, expected);
                if (difference != ForecastRefreshReason.None) return difference;
            }
            else if (Output.EnvironmentLimited && solvedTime > Output.StartTime + Output.ValidSeconds) return ForecastRefreshReason.RecheckContact;
            return pending == null && now >= nextRefresh - 0.000001 ? ForecastRefreshReason.Scheduled : ForecastRefreshReason.None;
        }

        internal void ObserveSolvedStep(long step, double time, in CharacterForecastSample actual)
        {
#if UNITY_EDITOR
            Diagnostics.Observe(step, actual);
#endif
            if (!obsolete && pending != null) pending.Observed.Add((time, actual));
            if (!Output.TrySample(time, 0f, out var expected)) return;
            ForecastRefreshReason difference = Difference(actual, expected);
            if (difference == ForecastRefreshReason.None) return;
            RequestRefresh(difference, false);
            Output.Clear(CharacterForecastRevision.Invalidated); // 固定步通知观察者之前，撤回与真实结果不符的预测。
        }

        private static ForecastRefreshReason Difference(in CharacterForecastSample start, in CharacterForecastSample expected)
        {
            if (!expected.ExternallyControlled && (start.Behaviors != expected.Behaviors || start.MoveApproved != expected.MoveApproved ||
                    start.Attack != expected.Attack || start.AttackPhase != expected.AttackPhase)) return ForecastRefreshReason.Behavior;
            if (start.Contacts != expected.Contacts) return ForecastRefreshReason.Contact;
            if ((start.Position - expected.Position).sqrMagnitude > 0.0064f ||
                    (start.Velocity - expected.Velocity).sqrMagnitude > 0.36f ||
                    Quaternion.Angle(start.Rotation, expected.Rotation) > 6f) return ForecastRefreshReason.Deviation;
            return ForecastRefreshReason.None;
        }

    }
}
