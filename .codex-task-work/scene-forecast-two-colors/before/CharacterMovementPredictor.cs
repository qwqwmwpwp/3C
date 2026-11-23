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
        internal readonly Vector3 Ground, Steep;
        internal readonly bool DynamicContact;

        internal CharacterForecastOrigin(double time, long number, CharacterForecastSample pose, CharacterMotorStep motor,
            Vector3 ground, Vector3 steep, bool dynamicContact)
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
        private CharacterInputSnapshot lastInput;
        private float lastInterval, lastDt, lastAcceleration, lastTurn;
        private double nextRefresh;
        internal bool NeedsRefresh => dirty != null && !suppressed;
        private string dirty;
        private bool suppressed;
        private double controlReturnsAt;
        private float forcedBraking;

        internal void ConfirmKnockback(double now, float seconds, float deceleration)
        {
            controlReturnsAt = now + InputMath.NonNegative(seconds, 0f);
            forcedBraking = InputMath.NonNegative(deceleration, 0f);
            Invalidate("受击接管");
        }

        internal void ClearControlLock() => controlReturnsAt = 0d;

        internal CharacterMovementPredictor(CharacterBehaviorFlow simulation, CharacterForecastGeometry geometry)
        {
            this.simulation = simulation;
            this.geometry = geometry;
        }

        internal void Invalidate(string reason)
        {
#if UNITY_EDITOR
            Diagnostics.ChangeAssumption();
#endif
            dirty = reason;
            Output.Clear();
        }

        internal void Suppress(bool value)
        {
            if (value == suppressed) return;
            suppressed = value;
            Invalidate(value ? "外部行为接管" : "恢复控制");
        }

        internal void Refresh(double now, in CharacterForecastOrigin origin, CharacterBehaviorFlow live,
            in CharacterForecastInputs inputs, CharacterMovementSettings movement, in CharacterDriveParameters drive, CharacterForecastSettings config,
            CharacterProfileOverrides overrides)
        {
            if (suppressed) { Output.Clear(); return; }
            float dt = Time.fixedDeltaTime;
            float interval = InputMath.NonNegative(overrides.ForecastInterval.Resolve(config.Interval), 0.2f);
            float horizon = InputMath.NonNegative(overrides.ForecastHorizon.Resolve(config.Horizon), 2f);
            if (!(dt > 0f) || horizon <= 0f) { Output.Clear(); return; }
            string reason = Reason(now, origin.Time, origin.Pose, inputs.Buffered, config, dt, interval, horizon, drive.GroundAcceleration, drive.TurnSpeed);
            if (reason == null) return;
            // 包括渲染时刻到最近物理步的余量，刷新间隔内始终可从“现在”查询完整时长。
            double steps = Math.Ceiling((horizon + (double)interval + Math.Max(0d, now - origin.Time)) / dt) + 1;
            if (steps > 8192) { Invalidate("采样数超限"); return; }
#if UNITY_EDITOR
            if (reason == "移动输入" || reason == "参数变化") Diagnostics.ChangeAssumption();
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
#endif
            Profiler.BeginSample("3C.MovementForecast");
            try
            {
                Output.Begin((int)steps + 1, origin.Time, origin.Number, dt, horizon, reason);
                Output.Append(origin.Time < controlReturnsAt ? origin.Pose.UnderExternalControl() : origin.Pose);
                geometry.Begin(drive.Settings);
                if (origin.DynamicContact) Output.EndAtContact();
                else
                {
                    live.CopyStateTo(simulation);
                    if (origin.Time < controlReturnsAt) simulation.ReleasePredictionActions();
                    Simulate(origin, inputs, movement, drive, dt, (int)steps);
                }
                nextRefresh = now + interval;
                lastInterval = interval;
                lastDt = dt;
                lastAcceleration = drive.GroundAcceleration;
                lastTurn = drive.TurnSpeed;
                lastInput = inputs.Buffered;
                dirty = null;
            }
            finally { Profiler.EndSample(); }
#if UNITY_EDITOR
            Diagnostics.Published(Output, dt, System.Diagnostics.Stopwatch.GetTimestamp() - ticks,
                GC.GetAllocatedBytesForCurrentThread() - allocated);
#endif
        }

        private string Reason(double now, double solvedTime, CharacterForecastSample start, CharacterInputSnapshot buffered,
            CharacterForecastSettings config, float dt, float interval, float horizon, float acceleration, float turnSpeed)
        {
            if (dirty != null || !Output.Available) return dirty ?? "建立起点";
            if (dt != lastDt || interval != lastInterval || horizon != Output.Horizon ||
                acceleration != lastAcceleration || turnSpeed != lastTurn) return "参数变化";
            float threshold = Mathf.Max(0.001f, InputMath.NonNegative(config.DirectionTolerance, 0.05f));
            if (buffered.Gait != lastInput.Gait || buffered.GuardHeld != lastInput.GuardHeld ||
                (buffered.Direction == Vector2.zero) != (lastInput.Direction == Vector2.zero) ||
                (buffered.Direction - lastInput.Direction).sqrMagnitude >= threshold * threshold) return "移动输入";
            if (Output.TrySample(solvedTime, 0f, out var expected))
            {
                string difference = Difference(start, expected);
                if (difference != null) return difference;
            }
            else if (Output.EnvironmentLimited && solvedTime > Output.StartTime + Output.ValidSeconds) return "重新检查接触";
            if (now >= nextRefresh - 0.000001) return "定时";
            return null;
        }

        internal void ObserveSolvedStep(long step, double time, in CharacterForecastSample actual)
        {
#if UNITY_EDITOR
            Diagnostics.Observe(step, actual);
#endif
            if (!Output.TrySample(time, 0f, out var expected)) return;
            string difference = Difference(actual, expected);
            if (difference == null) return;
            dirty = difference;
            Output.Clear(); // 固定步通知观察者之前，撤回与真实结果不符的预测。
        }

        private static string Difference(in CharacterForecastSample start, in CharacterForecastSample expected)
        {
            if (!expected.ExternallyControlled && (start.Behaviors != expected.Behaviors || start.MoveApproved != expected.MoveApproved ||
                    start.Attack != expected.Attack || start.AttackPhase != expected.AttackPhase)) return "行为变化";
            if (start.Contacts != expected.Contacts) return "接触变化";
            if ((start.Position - expected.Position).sqrMagnitude > 0.0064f ||
                    (start.Velocity - expected.Velocity).sqrMagnitude > 0.36f ||
                    Quaternion.Angle(start.Rotation, expected.Rotation) > 6f) return "物理偏差";
            return null;
        }

        private void Simulate(in CharacterForecastOrigin origin, in CharacterForecastInputs inputs,
            CharacterMovementSettings movement, in CharacterDriveParameters drive, float dt, int steps)
        {
            var state = origin.Motor;
            Vector3 ground = origin.Ground, steep = origin.Steep;
            Vector3 position = origin.Pose.Position, velocity = origin.Pose.Velocity;
            Quaternion rotation = origin.Pose.Rotation;
            CollisionFlags contacts = origin.Pose.Contacts;
            float damping = Mathf.Max(0f, 1f - drive.Drag * dt);
            for (int i = 0; i < steps; i++)
            {
                var input = inputs.AtStep(i);
                Vector3 forward = rotation * Vector3.forward;
                bool forced = origin.Time + i * (double)dt + 0.000001 < controlReturnsAt;
                var behavior = simulation.Tick(forced ? default : input, new Vector2(forward.x, forward.z), dt, movement);
                if (forced)
                {
                    if (ground.y > 0f)
                    {
                        Vector3 planar = Vector3.ProjectOnPlane(velocity, ground);
                        velocity += Vector3.MoveTowards(planar, Vector3.zero, forcedBraking * dt) - planar;
                    }
                    state = default;
                }
                else
                {
                    Vector3 target = CharacterMotorStep.Target(behavior, dt);
                    bool supported = ground.y > 0f && Vector3.Dot(velocity, ground) < 0.5f;
                    bool uncertain = false;
                    bool stepped = supported && geometry.TryStep(ref position, rotation, target * dt, out uncertain);
                    if (uncertain) { Output.EndAtContact(); break; }
                    if (!stepped) target = CharacterMotorStep.LimitOnSteepSurface(target, steep);
                    state.Advance(behavior, input, dt, position, ref velocity, target, ground, contacts, supported, stepped, drive);
                    rotation = CharacterMotorStep.Facing(rotation, behavior, input, drive.TurnSpeed, dt);
                }
                if (drive.UseGravity) velocity += drive.Gravity * dt;
                velocity *= damping;
                if (!geometry.Move(ref position, rotation, ref velocity, dt, out contacts, out ground, out steep))
                {
                    Output.EndAtContact();
                    break;
                }
                var sample = new CharacterForecastSample(position, velocity, rotation, forced ? default : behavior,
                    contacts, input.Gait, forced);
                Output.Append(sample);
                // 所有边沿已消费，平地静止且没有计时动作时，余下采样共享同一姿态。
                if (i >= 2 && !forced && input.Direction == Vector2.zero && velocity.sqrMagnitude < 0.000000001f &&
                    contacts == CollisionFlags.Below && ground.y > 0.9999f &&
                    ((behavior.ActiveBehaviors | behavior.ExecutedBehaviors) & ~(CharacterBehaviorId.Move | CharacterBehaviorId.Guard)) == 0)
                {
                    for (int j = i + 1; j < steps; j++) Output.Append(sample);
                    break;
                }
            }
        }
    }
}
