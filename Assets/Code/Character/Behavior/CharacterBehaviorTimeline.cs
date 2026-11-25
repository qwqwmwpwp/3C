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
        private readonly IReadOnlyList<CharacterBehaviorObservation> currentBatch;
        private int observationFrame = -1;
        private bool latestIsExternal;
        private double previousDeviceTime;
        private double firstAcceptedDeviceTime;
        internal IReadOnlyList<CharacterBehaviorObservation> Updates
        {
            get
            {
                BeginObservationBatch();
                return currentBatch;
            }
        }

        // 缓存只读视图，调用方可以逐条读取本批结果，无法改写时间线持有的列表。
        internal CharacterBehaviorTimeline() => currentBatch = updates.AsReadOnly();
        internal CharacterBehaviorObservation Latest { get; private set; }
        internal CharacterInputSnapshot Held { get; private set; }
        internal double Time { get; private set; }

        // 主线程首次读写新渲染帧时收回上一批；暂停或停用不依赖驱动器 Update 来回收。
        private void BeginObservationBatch()
        {
            int frame = UnityEngine.Time.frameCount;
            if (observationFrame == frame) return;
            observationFrame = frame;
            updates.Clear();
            latestIsExternal = false;
        }

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
            if (flow.DispatchCancelled) return;
            var frame = new Frame(flow, board.Rigidbody, grants, movement, state, board.Simulation.LifecycleVersion);
            BeginObservationBatch();
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
                if (flow.DispatchCancelled) return;
                Held = current;
                var input = new CharacterInputSnapshot(current.Direction, press.IsDash, current.GuardHeld,
                    CharacterBehaviorId.None, current.Gait, default(AttackButtonPresses).With(press.Attack));
                Publish(frame, input, 0f, Time);
                if (flow.DispatchCancelled) return;
            }
            board.Input.ClearPresses();
            previousDeviceTime = deviceNow;
            AdvanceTo(frame, end, (collected.Requests & ~CharacterBehaviorId.Guard) != 0);
            if (flow.DispatchCancelled) return;
            Held = current;
            // 直接写黑板的调用者没有设备时间，离散请求只在采到的帧末交付一次。
            Publish(frame, collected, 0f, Time);
        }

        // 依活动动作的边界截开长帧，目标处留给调用方交付该时刻的输入。
        private void AdvanceTo(in Frame frame, double target, bool inputAtTarget)
        {
            while (Time < target && !frame.Flow.DispatchCancelled)
            {
                double end = frame.Flow.NextBoundary(Time, target, inputAtTarget);
                if (frame.Flow.DispatchCancelled) return;
                double start = Time;
                float dt = (float)(end - start);
                Publish(frame, Held, dt, end);
                if (!frame.Flow.DispatchCancelled && end < target) Publish(frame, Held, 0f, end);
            }
        }

        // 先提交资源和动作实例，再向物理、动画写入相同裁决时刻的结果。
        private void Publish(in Frame frame, CharacterInputSnapshot input, float seconds, double end)
        {
            double start = Time;
            var behavior = frame.Flow.AdvanceTimeline(input, frame.Facing, seconds, frame.Movement, frame.Velocity);
            if (behavior.DurationCommitted) Time = end;
            if (frame.Flow.DispatchCancelled) return;
            var origin = frame.Flow.Alignment.HasTarget && behavior.Attack.Move != CharacterAttackMove.None
                ? frame.Flow.ProjectAlignmentOrigin(frame.Body, Time, frame.Origin, frame.Movement.MoveTurnSpeed) : frame.Origin;
            if (frame.Flow.DispatchCancelled)
            {
                frame.Flow.RetainUnpublished(behavior);
                return;
            }
            var command = frame.Flow.PublishMotion(Time, behavior, Held, frame.Facing, frame.Velocity, frame.Movement, origin);
            // 包括扩展采样器冻结在内的回调全部返回后，才把这一刻交给物理与动画。
            if (frame.Flow.DispatchCancelled)
            {
                frame.Flow.RetainUnpublished(behavior);
                return;
            }
            if (seconds > 0f && frame.Flow.HasCustomMotion(behavior))
                frame.Body.Write(new CharacterMotionCommand(start, Time, behavior, Held));
            frame.Body.Write(command);
            latestIsExternal = false;
            Latest = new CharacterBehaviorObservation(behavior, seconds, frame.Version);
            updates.Add(Latest);
            frame.Grants.Observe(behavior, frame.Version);
        }

        // 外部边界与本渲染帧的普通裁决按发生顺序交付，仅重生丢弃整批旧代结果。
        internal void PublishExternal(CharacterBehaviorFrameResult result, int version, double deviceNow)
        {
            BeginObservationBatch();
            previousDeviceTime = firstAcceptedDeviceTime = deviceNow;
            Held = default;
            Latest = new CharacterBehaviorObservation(result, 0f, version);
            updates.Add(Latest);
            latestIsExternal = true;
        }

        // 仅让行为时钟与冷却走过已交给外部控制的区间。
        internal void AdvanceExternal(CharacterBehaviorFlow flow, CharacterMovementSettings settings, double end)
        {
            if (flow.DispatchCancelled || end <= Time) return;
            flow.AdvanceExternal(end - Time);
            Time = end;
        }

        // 清除失效的持续输入和当前状态，已提交观测保留所属代次供本帧订阅者读取。
        internal void EndContinuity(double deviceNow)
        {
            firstAcceptedDeviceTime = previousDeviceTime = deviceNow;
            BeginObservationBatch();
            if (!latestIsExternal) Latest = default;
            Held = default;
        }

        // 重生时让新一代的角色时间和行为编号从头开始。
        internal void Reset(double deviceNow)
        {
            updates.Clear();
            latestIsExternal = false;
            Time = 0d;
            EndContinuity(deviceNow);
        }
    }
}
