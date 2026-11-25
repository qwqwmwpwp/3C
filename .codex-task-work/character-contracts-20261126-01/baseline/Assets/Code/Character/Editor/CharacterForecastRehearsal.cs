using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 现有场景预演支持的两种输入变化。
    internal enum ForecastScenario { RunStop, DashRight }

    /// <summary>以摆放位置为起点，使用独立的行为副本和场景查询，比较维持输入与改变输入后的轨迹。</summary>
    internal sealed class CharacterForecastRehearsal
    {
        internal CharacterMovementForecast Before { get; private set; }
        internal CharacterMovementForecast After { get; private set; }

        // 从摆放姿态建立相同起点，使用角色的有效移动参数分别生成变更输入前后的预演轨迹。
        internal void Build(CharacterBehaviorDriver owner, ForecastScenario scenario)
        {
            Before = After = null;
            var body = owner.GetComponent<Rigidbody>();
            var capsule = owner.GetComponent<CapsuleCollider>();
            if (!body || !capsule || !owner.ForecastSettings.Enabled) return;
            float dt = Time.fixedDeltaTime;
            if (!(dt > 0f)) return;
            var config = owner.Configuration;
            var settings = config.ReadMovement();
            var flow = owner.CreateBehaviorFlow();
            flow.Reset(settings);
            var probe = new CharacterStepProbe(body, capsule);
            var geometry = new CharacterForecastGeometry(body, capsule, probe);
            var drive = config.DriveParameters(body, config.Motor, config.FootfallDistance);
            geometry.Begin(drive.Settings);
            var transform = owner.transform;
            Vector3 position = transform.position, velocity = Vector3.zero;
            Quaternion rotation = transform.rotation;
            if (!geometry.Move(ref position, rotation, ref velocity, dt, out var contacts, out var ground, out var steep)) return;
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up).normalized;
            if (forward == Vector3.zero) forward = Vector3.forward;
            Vector2 forwardXZ = new Vector2(forward.x, forward.z);
            var held = new CharacterInputSnapshot(forwardXZ, false);
            bool dash = scenario == ForecastScenario.DashRight;
            var opening = new CharacterInputSnapshot(forwardXZ, dash);
            velocity = forward * settings.MoveSpeed;
            var groundVelocity = CharacterMotorStep.GroundVelocity(velocity, ground);
            var solved = flow.AdvanceTimeline(held, forwardXZ, 0f, settings, groundVelocity);
            var observation = new CharacterMotorObservation(position, velocity, rotation, contacts, ground, steep, false);
            var origin = new CharacterForecastOrigin(-dt, 0, observation, solved, held.Gait, default);
            var bodyCommands = new CharacterRigidbodyBlackboard();
            // 上一物理步在 -dt 求解，动作在同一时刻获准，帧末 0 时这段运动尚待执行。
            var started = flow.AdvanceTimeline(opening, forwardXZ, 0f, settings, groundVelocity);
            bodyCommands.Write(flow.CaptureMotion(-dt, started, held, forwardXZ, groundVelocity, settings));
            flow.AdvanceTimeline(held, forwardXZ, dt, settings, groundVelocity);
            var behavior = flow.AdvanceTimeline(held, forwardXZ, 0f, settings, groundVelocity);
            var predictor = new CharacterMovementPredictor(owner.CreateBehaviorFlow, geometry, () => config.CreateBehaviorFlow(snapshot: true));
            var frame = new CharacterForecastFrame(0, 0d, origin, behavior, held, bodyCommands, dt, config, drive);
            predictor.SubmitFrame(frame, flow, threaded: false);
            Before = new CharacterMovementForecast();
            Before.CopyPublishedFrom(predictor.Output);

            Vector2 changed = dash ? new Vector2(forward.z, -forward.x) : Vector2.zero;
            var newInput = new CharacterInputSnapshot(changed, false);
            behavior = flow.AdvanceTimeline(newInput, forwardXZ, 0f, settings, groundVelocity);
            predictor.RequestRefresh(ForecastRefreshReason.RehearsalInput);
            frame = new CharacterForecastFrame(0, 0d, origin, behavior, newInput, bodyCommands, dt, config, drive);
            predictor.SubmitFrame(frame, flow, threaded: false);
            After = predictor.Output;
        }
    }
}
