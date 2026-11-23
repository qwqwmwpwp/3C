using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal enum ForecastScenario { RunStop, DashRight }

    /// <summary>以摆放位置为起点，使用独立的行为副本和场景查询，比较维持输入与改变输入后的轨迹。</summary>
    internal sealed class CharacterForecastRehearsal
    {
        internal CharacterMovementForecast Before { get; private set; }
        internal CharacterMovementForecast After { get; private set; }

        internal void Build(CharacterBehaviorDriver owner, ForecastScenario scenario)
        {
            Before = After = null;
            var body = owner.GetComponent<Rigidbody>();
            var capsule = owner.GetComponent<CapsuleCollider>();
            if (!body || !capsule || !owner.ForecastSettings.Enabled) return;
            float dt = Time.fixedDeltaTime;
            if (!(dt > 0f)) return;
            var settings = owner.ReadMovement();
            var flow = owner.CreateBehaviorFlow();
            flow.Reset(settings);
            var probe = new CharacterStepProbe(body, capsule);
            var geometry = new CharacterForecastGeometry(body, capsule, probe);
            geometry.Begin(owner.Motor);
            var transform = owner.transform;
            Vector3 position = transform.position, velocity = Vector3.zero;
            Quaternion rotation = transform.rotation;
            if (!geometry.Move(ref position, rotation, ref velocity, dt, out var contacts, out var ground, out _)) return;
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up).normalized;
            if (forward == Vector3.zero) forward = Vector3.forward;
            Vector2 forwardXZ = new Vector2(forward.x, forward.z);
            var held = new CharacterInputSnapshot(forwardXZ, false);
            bool dash = scenario == ForecastScenario.DashRight;
            var opening = new CharacterInputSnapshot(forwardXZ, dash);
            var behavior = flow.Tick(opening, forwardXZ, dt, settings);
            var drive = new CharacterDriveParameters(owner.Motor, owner.Overrides.GroundAcceleration.Resolve(owner.Motor.groundAcceleration),
                settings.MoveTurnSpeed, owner.Profile ? owner.Profile.RunFootfallDistance() : CharacterMotorSettings.DefaultFootfallDistance,
                body.useGravity, Physics.gravity, body.drag);
            var motor = new CharacterMotorStep();
            velocity = forward * settings.MoveSpeed;
            motor.Advance(behavior, opening, dt, position, ref velocity, CharacterMotorStep.Target(behavior, dt), ground,
                contacts, (contacts & CollisionFlags.Below) != 0, false, drive);
            // 当前姿态已在跑步或刚进入突进；后续固定步沿用项目原有的输入锁存顺序。
            var pose = new CharacterForecastSample(position, velocity, rotation, behavior, contacts, CharacterMoveGait.Run);
            var origin = new CharacterForecastOrigin(0d, behavior.FrameNumber, pose, motor, ground, Vector3.zero, false);
            var predictor = new CharacterMovementPredictor(owner.CreateBehaviorFlow(), geometry);
            predictor.Refresh(0d, origin, flow, new CharacterForecastInputs(held, held), settings, drive, owner.ForecastSettings, owner.Overrides);
            Before = new CharacterMovementForecast();
            Before.CopyPublishedFrom(predictor.Output);
            Vector2 changed = dash ? new Vector2(forward.z, -forward.x) : Vector2.zero;
            predictor.Invalidate("预演输入变化");
            predictor.Refresh(0d, origin, flow, new CharacterForecastInputs(held, new CharacterInputSnapshot(changed, false)),
                settings, drive, owner.ForecastSettings, owner.Overrides);
            After = predictor.Output;
        }
    }
}
