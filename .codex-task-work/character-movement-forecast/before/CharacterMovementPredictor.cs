using System;
using UnityEngine;
using UnityEngine.Profiling;

namespace ThreeC.CharacterBehavior
{
    /// <summary>从真实步末状态重建时间序列，只向行为副本、移动值副本和样本缓冲写入。</summary>
    internal sealed class CharacterMovementPredictor
    {
        internal readonly CharacterMovementForecast Output = new CharacterMovementForecast();
        private readonly CharacterBehaviorFlow simulation;
        private readonly Rigidbody body;
        private readonly CharacterRigidbodyMotor motor;
        private readonly CharacterForecastGeometry geometry;
        private CharacterInputSnapshot lastInput;
        private float lastInterval, lastDt, lastAcceleration, lastTurn;
        private string dirty;
        private bool suppressed;

        internal CharacterMovementPredictor(CharacterBehaviorFlow simulation, Rigidbody body, CapsuleCollider capsule,
            CharacterRigidbodyMotor motor)
        {
            this.simulation = simulation;
            this.body = body;
            this.motor = motor;
            geometry = new CharacterForecastGeometry(body, capsule);
        }

        internal void Invalidate(string reason)
        {
            dirty = reason;
            Output.Clear();
        }

        internal void Suppress(bool value)
        {
            if (value == suppressed) return;
            suppressed = value;
            Invalidate(value ? "外部行为接管" : "恢复控制");
        }

        internal void Refresh(double now, double solvedTime, CharacterBehaviorFrameResult behavior, CharacterBehaviorFlow live,
            CharacterInputSnapshot locked, CharacterInputSnapshot buffered, CharacterMovementSettings movement,
            CharacterMotorSettings tuning, float acceleration, float turnSpeed, CharacterForecastSettings config,
            CharacterProfileOverrides overrides)
        {
            if (suppressed || !config.Enabled) { Output.Clear(); return; }
            float dt = Time.fixedDeltaTime;
            float interval = InputMath.NonNegative(overrides.ForecastInterval.Resolve(config.Interval), 0.2f);
            float horizon = InputMath.NonNegative(overrides.ForecastHorizon.Resolve(config.Horizon), 2f);
            if (!(dt > 0f) || horizon <= 0f) { Output.Clear(); return; }
            var flags = motor.ReadContacts(out Vector3 ground);
            var start = new CharacterForecastSample(body.position, body.velocity, body.rotation, behavior, flags);
            string reason = Reason(now, solvedTime, start, buffered, config, dt, interval, horizon, acceleration, turnSpeed);
            if (reason == null) return;
            // 包括渲染时刻到最近物理步的余量，刷新间隔内始终可从“现在”查询完整时长。
            double steps = Math.Ceiling((horizon + (double)interval + Math.Max(0d, now - solvedTime)) / dt) + 1;
            if (steps > 8192) { Invalidate("采样数超限"); return; }
            Profiler.BeginSample("3C.MovementForecast");
            try
            {
                Output.Begin((int)steps + 1, solvedTime, behavior.FrameNumber, dt, horizon, reason);
                Output.Append(start);
                geometry.Begin(tuning);
                motor.ReadForecastContacts(out Vector3 steep, out bool dynamicContact);
                if (dynamicContact) Output.EndAtContact();
                else
                {
                    live.CopyStateTo(simulation);
                    Simulate(start, motor.Step, ground, steep, locked, buffered, movement, tuning, acceleration,
                        turnSpeed, dt, (int)steps);
                }
                lastInterval = interval;
                lastDt = dt;
                lastAcceleration = acceleration;
                lastTurn = turnSpeed;
                lastInput = buffered;
                dirty = null;
            }
            finally { Profiler.EndSample(); }
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
                if (start.Behaviors != expected.Behaviors || start.MoveApproved != expected.MoveApproved ||
                    start.Attack != expected.Attack || start.AttackPhase != expected.AttackPhase) return "行为变化";
                if (start.Contacts != expected.Contacts) return "接触变化";
                if ((start.Position - expected.Position).sqrMagnitude > 0.0064f ||
                    (start.Velocity - expected.Velocity).sqrMagnitude > 0.36f ||
                    Quaternion.Angle(start.Rotation, expected.Rotation) > 6f) return "物理偏差";
            }
            else if (Output.EnvironmentLimited && solvedTime > Output.StartTime + Output.ValidSeconds) return "重新检查接触";
            if (now - Output.StartTime >= interval - 0.000001) return "定时";
            return null;
        }

        private void Simulate(CharacterForecastSample start, CharacterMotorStep state, Vector3 ground, Vector3 steep,
            CharacterInputSnapshot locked, CharacterInputSnapshot buffered, CharacterMovementSettings movement,
            CharacterMotorSettings tuning, float acceleration, float turnSpeed, float dt, int steps)
        {
            var held = new CharacterInputSnapshot(buffered.Direction, false, buffered.GuardHeld,
                CharacterBehaviorId.None, buffered.Gait);
            Vector3 position = start.Position, velocity = start.Velocity;
            Quaternion rotation = start.Rotation;
            CollisionFlags contacts = start.Contacts;
            Vector3 gravity = Physics.gravity;
            bool useGravity = body.useGravity;
            float damping = Mathf.Max(0f, 1f - body.drag * dt);
            for (int i = 0; i < steps; i++)
            {
                var input = i == 0 ? locked : i == 1 ? buffered : held;
                Vector3 forward = rotation * Vector3.forward;
                var behavior = simulation.Tick(input, new Vector2(forward.x, forward.z), dt, movement);
                Vector3 target = CharacterMotorStep.Target(behavior, dt);
                bool supported = ground.y > 0f && Vector3.Dot(velocity, ground) < 0.5f;
                bool uncertain = false;
                bool stepped = supported && geometry.TryStep(ref position, rotation, target * dt, out uncertain);
                if (uncertain) { Output.EndAtContact(); break; }
                Vector3 downhill = Vector3.ProjectOnPlane(steep, Vector3.up).normalized;
                if (!stepped) target -= downhill * Mathf.Min(0f, Vector3.Dot(target, downhill));
                state.Advance(behavior, input, dt, position, ref velocity, target, ground, contacts, supported, stepped,
                    tuning, acceleration, motor.FootfallDistance, useGravity, gravity);
                rotation = CharacterMotorStep.Facing(rotation, behavior, input, turnSpeed, dt);
                if (useGravity) velocity += gravity * dt;
                velocity *= damping;
                if (!geometry.Move(ref position, rotation, ref velocity, dt, out contacts, out ground, out steep))
                {
                    Output.EndAtContact();
                    break;
                }
                Output.Append(new CharacterForecastSample(position, velocity, rotation, behavior, contacts));
            }
        }
    }
}
