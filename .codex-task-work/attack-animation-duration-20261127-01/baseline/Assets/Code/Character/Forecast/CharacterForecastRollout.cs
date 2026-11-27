using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 从真实求解时刻执行已批准的运动，再从帧末行为副本计算未来采样。
    internal static class CharacterForecastRollout
    {
        // 同步查询和后台回放共享起点对齐、动作边界、马达积分及可信接触边界。
        internal static void Simulate(CharacterBehaviorFlow simulation, ICharacterForecastGround geometry,
            CharacterMovementForecast output, in CharacterForecastFrame frame, ForecastRefreshReason reason)
        {
            var origin = frame.Origin;
            var motion = new Motion(origin);
            var window = frame.Window;
            var startCursor = origin.StartCursor;
            if (origin.DynamicContact || !motion.Solve(frame.Pending, geometry, frame, ref startCursor))
            {
                output.Begin(1, origin.Time, frame, reason);
                output.Append(origin.Time < frame.ControlReturnsAt ? origin.Pose.UnderExternalControl() : origin.Pose);
                output.EndAtContact();
                return;
            }

            bool forced = frame.Time < frame.ControlReturnsAt;
            if (forced) simulation.ReleasePredictionActions();
            var behavior = frame.Behavior;
            var slices = new List<CharacterMotionSlice>(4);
            output.Begin(window.Steps + 1, frame.Time, frame, reason);
            output.Append(motion.Sample(behavior, frame.Held.Gait, forced));
            for (int i = 0; i < window.Steps; i++)
            {
                double start = frame.Time + i * (double)window.Step;
                double end = frame.Time + (i + 1) * (double)window.Step;
                slices.Clear();
                PlanStep(simulation, motion, frame, start, end, slices, ref behavior);
                if (!motion.Solve(slices, geometry, frame, ref startCursor))
                {
                    output.EndAtContact();
                    break;
                }
                var sample = motion.Sample(behavior, frame.Held.Gait, end < frame.ControlReturnsAt);
                output.Append(sample);
                if (simulation.AllowsStationaryForecast && end >= frame.ControlReturnsAt && motion.AtRest(frame.Held, behavior))
                {
                    for (int j = i + 1; j < window.Steps; j++) output.Append(sample);
                    break;
                }
            }
        }

        // 与实走一样先执行当前命令，在动作结束、阶段或接续时刻执行零时长裁决，再生成下一段命令。
        private static void PlanStep(CharacterBehaviorFlow flow, in Motion motion, in CharacterForecastFrame frame,
            double time, double end, List<CharacterMotionSlice> slices, ref CharacterBehaviorFrameResult behavior)
        {
            Vector2 facing = motion.Facing;
            Vector2 groundVelocity = motion.GroundVelocity;
            while (time < end)
            {
                bool forced = time < frame.ControlReturnsAt;
                if (forced)
                {
                    double returns = Math.Min(end, frame.ControlReturnsAt);
                    slices.Add(new CharacterMotionSlice(time, returns, default, default));
                    flow.AdvanceExternal(returns - time);
                    time = returns;
                    if (time >= frame.ControlReturnsAt)
                        behavior = flow.AdvanceTimeline(frame.Held, facing, 0f, frame.Movement, groundVelocity);
                    continue;
                }
                var input = frame.Held;
                double next = flow.NextBoundary(time, end);
                var command = flow.CaptureMotion(time, behavior, input, facing, groundVelocity, frame.Movement);
                float seconds = (float)(next - time);
                var advanced = flow.AdvanceTimeline(input, facing, seconds, frame.Movement, groundVelocity);
                if (flow.HasCustomMotion(advanced)) command = new CharacterMotionCommand(time, next, advanced, input);
                double cursor = time;
                while (cursor < next)
                {
                    var slice = command.SampleNext(cursor, next);
                    slices.Add(slice);
                    cursor = slice.End;
                }
                time = next;
                // 已到接续窗口或控制归还的同一时刻，先落地新实例和费用再捕获下一命令。
                behavior = flow.AdvanceTimeline(frame.Held,
                    facing, 0f, frame.Movement, groundVelocity);
            }
        }

        // 保存单个任务的位姿、驱动状态和接触；每次物理提交只向环境移动一次。
        private struct Motion
        {
            private CharacterMotorStep motor;
            private Vector3 position, velocity, ground;
            private Quaternion rotation;
            private CollisionFlags contacts;
            private CharacterSlopeContacts steep;
            internal Vector2 GroundVelocity => CharacterMotorStep.GroundVelocity(velocity, ground);
            internal Vector2 Facing
            {
                get
                {
                    var forward = rotation * Vector3.forward;
                    return new Vector2(forward.x, forward.z);
                }
            }

            // 复制已完成步的实际速度；跨段速度校正在下一次马达开步时执行。
            internal Motion(in CharacterForecastOrigin origin)
            {
                motor = origin.Motor;
                position = origin.Pose.Position;
                velocity = origin.Pose.Velocity;
                rotation = origin.Pose.Rotation;
                ground = origin.Ground;
                steep = origin.Steep;
                contacts = origin.Pose.Contacts;
            }

            // 消费可执行片段的起步，再将整步平均速度交给重力、阻尼与碰撞求解。
            internal bool Solve(IReadOnlyList<CharacterMotionSlice> slices, ICharacterForecastGround geometry,
                in CharacterForecastFrame frame, ref MotionStartToken startCursor)
            {
                if (slices.Count == 0) return true;
                Vector2 requested = default;
                for (int i = 0; i < slices.Count; i++)
                    if (slices[i].Start >= frame.ControlReturnsAt) requested += slices[i].Behavior.Displacement;

                velocity = motor.ResumeVelocity(velocity);
                bool uncertain = false;
                bool stepped = requested != Vector2.zero && CharacterMotorStep.IsGroundSupported(velocity, ground) &&
                    geometry.TryStep(ref position, rotation, new Vector3(requested.x, 0f, requested.y), out uncertain);
                if (uncertain) return false;
                Vector3 integrated = default;
                for (int i = 0; i < slices.Count; i++)
                {
                    var slice = slices[i];
                    float dt = (float)(slice.End - slice.Start);
                    if (slice.Start < frame.ControlReturnsAt)
                    {
                        CharacterMotorStep.AdvanceKnockback(ref velocity, ground, frame.ForcedBraking, dt);
                        motor = default;
                    }
                    else
                    {
                        // 已预读的标记可能来自外部接管前的切片；以本次实际执行的游标为准。
                        slice = slice.ResolveStart(ref startCursor);
                        bool supported = CharacterMotorStep.IsGroundSupported(velocity, ground);
                        motor.Advance(slice.Behavior, slice.Input, dt, position + integrated, ref velocity, steep,
                            ground, contacts, supported, stepped, frame.Drive, ref rotation);
                    }
                    integrated += velocity * dt;
                }
                float seconds = (float)(slices[slices.Count - 1].End - slices[0].Start);
                Vector3 mean = integrated / seconds;
                motor.RecordSubmittedVelocity(mean, velocity, slices.Count > 1);
                velocity = mean;
                if (frame.Drive.UseGravity) velocity += frame.Drive.Gravity * seconds;
                velocity *= Mathf.Max(0f, 1f - frame.Drive.Drag * seconds);
                return geometry.Move(ref position, rotation, ref velocity, seconds, out contacts, out ground, out steep);
            }

            // 平地静止且没有活动计时动作时，后续持续输入产生相同姿态。
            internal bool AtRest(in CharacterInputSnapshot held, in CharacterBehaviorFrameResult behavior) =>
                held.Direction == Vector2.zero && velocity.sqrMagnitude < 0.000000001f &&
                contacts == CollisionFlags.Below && ground.y > 0.9999f &&
                ((behavior.ActiveBehaviors | behavior.ExecutedBehaviors) & ~(CharacterBehaviorId.Move | CharacterBehaviorId.Guard)) == 0;

            // 把同一时刻的运动与行为结果写成采样点。
            internal CharacterForecastSample Sample(in CharacterBehaviorFrameResult behavior, CharacterMoveGait gait, bool forced) =>
                new CharacterForecastSample(position, velocity, rotation, forced ? default : behavior, contacts, gait, forced);
        }
    }
}
