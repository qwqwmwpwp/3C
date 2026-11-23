using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal static class CharacterForecastRollout
    {
        internal static void Simulate(CharacterBehaviorFlow simulation, ICharacterForecastGround geometry,
            CharacterMovementForecast output, double controlReturnsAt, float forcedBraking, in CharacterForecastOrigin origin, in CharacterForecastInputs inputs,
            CharacterMovementSettings movement, in CharacterDriveParameters drive, float dt, int steps)
        {
            var state = origin.Motor;
            Vector3 ground = origin.Ground;
            var steep = origin.Steep;
            Vector3 position = origin.Pose.Position, velocity = origin.Pose.Velocity;
            Quaternion rotation = origin.Pose.Rotation;
            CollisionFlags contacts = origin.Pose.Contacts;
            float damping = Mathf.Max(0f, 1f - drive.Drag * dt);
            for (int i = 0; i < steps; i++)
            {
                var input = inputs.AtStep(i);
                Vector3 forward = rotation * Vector3.forward;
                bool forced = origin.Time + i * (double)dt + 0.000001 < controlReturnsAt;
                var behavior = simulation.Tick(forced ? default : input, new Vector2(forward.x, forward.z), dt, movement,
                    CharacterMotorStep.GroundVelocity(velocity, ground));
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
                    if (uncertain) { output.EndAtContact(); break; }
                    if (!stepped) target = steep.Limit(target);
                    state.Advance(behavior, input, dt, position, ref velocity, target, ground, contacts,
                        supported, stepped, drive, ref rotation);
                }
                if (drive.UseGravity) velocity += drive.Gravity * dt;
                velocity *= damping;
                if (!geometry.Move(ref position, rotation, ref velocity, dt, out contacts, out ground, out steep))
                {
                    output.EndAtContact();
                    break;
                }
                var sample = new CharacterForecastSample(position, velocity, rotation, forced ? default : behavior,
                    contacts, input.Gait, forced);
                output.Append(sample);
                // 所有边沿已消费，平地静止且没有计时动作时，余下采样共享同一姿态。
                if (i >= 2 && !forced && input.Direction == Vector2.zero && velocity.sqrMagnitude < 0.000000001f &&
                    contacts == CollisionFlags.Below && ground.y > 0.9999f &&
                    ((behavior.ActiveBehaviors | behavior.ExecutedBehaviors) & ~(CharacterBehaviorId.Move | CharacterBehaviorId.Guard)) == 0)
                {
                    for (int j = i + 1; j < steps; j++) output.Append(sample);
                    break;
                }
            }
        }
    }
}
