using System;
using System.Collections.Generic;
using ThreeC.Player;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在同一个角色时钟上依次处理行为边界、设备边沿与本帧持续输入。
    internal sealed class CharacterBehaviorTimeline
    {
        private readonly List<CharacterBehaviorObservation> updates = new List<CharacterBehaviorObservation>();
        private double previousDeviceTime;
        private double firstAcceptedDeviceTime;
        internal IReadOnlyList<CharacterBehaviorObservation> Updates => updates;
        internal CharacterBehaviorObservation Latest { get; private set; }
        internal CharacterInputSnapshot Held { get; private set; }
        internal double Time { get; private set; }

        // 一帧只使用一份实测朝向、速度和角色配置。
        private readonly struct Frame
        {
            internal readonly CharacterBehaviorFlow Flow;
            internal readonly CharacterRigidbodyBlackboard Body;
            internal readonly CharacterCommittedBehaviorGrants Grants;
            internal readonly CharacterMovementSettings Movement;
            internal readonly Vector2 Facing, Velocity;
            internal readonly CharacterAlignmentOrigin Origin;
            internal readonly int Version;

            // 固定本次逐帧裁决使用的依赖。
            internal Frame(CharacterBehaviorFlow flow, CharacterRigidbodyBlackboard body, CharacterCommittedBehaviorGrants grants,
                CharacterMovementSettings movement, CharacterRigidbodyMotor.StepState state, int version)
            {
                Flow = flow;
                Body = body;
                Grants = grants;
                Movement = movement;
                Facing = state.Facing;
                Velocity = state.GroundVelocity;
                Origin = new CharacterAlignmentOrigin { Position = state.Start, Facing = state.Facing, Time = body.SubmittedThrough };
                Version = version;
            }
        }

        // 使用最后一个物理步及本帧余时作为帧末，设备时间按同一有效渲染区间换算。
        internal void Advance(CharacterBehaviorFlow flow, CharacterBlackboard board, CharacterCommittedBehaviorGrants grants,
            CharacterMovementSettings movement, CharacterRigidbodyMotor.StepState state, double end, double deviceNow)
        {
            var frame = new Frame(flow, board.Rigidbody, grants, movement, state, board.Simulation.LifecycleVersion);
            updates.Clear();
            var collected = board.Input.Consume();
            var current = new CharacterInputSnapshot(collected.Direction, false, collected.GuardHeld,
                CharacterBehaviorId.None, collected.Gait);
            double start = Time;
            end = Math.Max(start, end);
            foreach (var press in board.Input.Presses)
            {
                if (press.DeviceTime < firstAcceptedDeviceTime) continue;
                double fraction = deviceNow > previousDeviceTime
                    ? Math.Max(0d, Math.Min(1d, (press.DeviceTime - previousDeviceTime) / (deviceNow - previousDeviceTime))) : 1d;
                AdvanceTo(frame, start + fraction * (end - start), inputAtTarget: true);
                Held = current;
                var input = new CharacterInputSnapshot(current.Direction, press.IsDash, current.GuardHeld,
                    CharacterBehaviorId.None, current.Gait, default(AttackButtonPresses).With(press.Attack));
                Publish(frame, input, 0f);
            }
            board.Input.ClearPresses();
            previousDeviceTime = deviceNow;
            AdvanceTo(frame, end, collected.DashPressed || collected.AttackPresses.Count > 0);
            Held = current;
            // 直接写黑板的调用者没有设备时间，离散请求只在采到的帧末交付一次。
            Publish(frame, collected, 0f);
        }

        // 依活动动作的边界截开长帧，目标处留给调用方交付该时刻的输入。
        private void AdvanceTo(in Frame frame, double target, bool inputAtTarget)
        {
            while (Time < target)
            {
                double end = frame.Flow.NextBoundary(Time, target, inputAtTarget);
                double start = Time;
                float dt = (float)(end - start);
                Time = end;
                Publish(frame, Held, dt, start);
                if (end < target) Publish(frame, Held, 0f);
            }
        }

        // 先提交资源和动作实例，再向物理、动画写入相同裁决时刻的结果。
        private void Publish(in Frame frame, CharacterInputSnapshot input, float seconds, double start = 0d)
        {
            var behavior = frame.Flow.AdvanceTimeline(input, frame.Facing, seconds, frame.Movement, frame.Velocity);
            if (seconds > 0f && frame.Flow.HasCustomMotion(behavior))
                frame.Body.Write(new CharacterMotionCommand(start, Time, behavior, Held));
            Latest = new CharacterBehaviorObservation(behavior, seconds, frame.Version);
            updates.Add(Latest);
            frame.Grants.Observe(behavior, frame.Version);
            var origin = frame.Flow.Alignment.HasTarget && behavior.Attack.Move != CharacterAttackMove.None
                ? frame.Body.ProjectAlignmentOrigin(Time, frame.Origin, frame.Movement.MoveTurnSpeed) : frame.Origin;
            frame.Body.Write(frame.Flow.PublishMotion(Time, behavior, Held, frame.Facing, frame.Velocity, frame.Movement, origin));
        }

        // 清掉输入连续性及尚未被动画读取的旧帧，保留活动行为和已消费时间。
        internal void EndContinuity(double deviceNow)
        {
            firstAcceptedDeviceTime = previousDeviceTime = deviceNow;
            updates.Clear();
            Latest = default;
            Held = default;
        }

        // 重生时让新一代的角色时间和行为编号从头开始。
        internal void Reset(double deviceNow)
        {
            Time = 0d;
            EndContinuity(deviceNow);
        }
    }
}
