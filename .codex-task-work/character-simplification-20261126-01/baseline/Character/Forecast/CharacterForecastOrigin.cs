using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 固定步结算时保存位姿、接触与该步消费后的马达状态，直到下一次真实求解。
    internal readonly struct CharacterForecastOrigin
    {
        internal readonly double Time;
        internal readonly long Number;
        internal readonly MotionStartToken StartCursor;
        internal readonly CharacterForecastSample Pose;
        internal readonly CharacterMotorStep Motor;
        internal readonly Vector3 Ground;
        internal readonly CharacterSlopeContacts Steep;
        internal readonly bool DynamicContact;

        // 从同一次物理求解捕获时钟、观测、马达状态和起步游标。
        internal CharacterForecastOrigin(double time, long stepNumber, in CharacterMotorObservation actual,
            in CharacterBehaviorFrameResult solvedBehavior, CharacterMoveGait gait, in CharacterMotorStep motor,
            MotionStartToken startCursor = default)
        {
            Time = time;
            Number = stepNumber;
            StartCursor = startCursor;
            Pose = new CharacterForecastSample(actual.Position, actual.Velocity, actual.Rotation, solvedBehavior, actual.Contacts, gait);
            Motor = motor;
            Ground = actual.Ground;
            Steep = actual.Steep;
            DynamicContact = actual.HasDynamicContact;
        }
    }

    // 保存验证轨迹需要的本帧观测，连续参数按值捕获，不持有可被下一帧改写的配置对象。
    internal readonly struct CharacterForecastObservation
    {
        internal readonly int Generation;
        internal readonly long AlignmentRevision;
        internal readonly double Time;
        internal readonly float DirectionTolerance;
        internal readonly CharacterForecastOrigin Origin;
        internal readonly CharacterBehaviorFrameResult Behavior;
        internal readonly CharacterInputSnapshot Held;
        internal readonly CharacterForecastWindow Window;
        internal readonly CharacterForecastTuning Tuning;

        // 捕获持续输入、实际求解基线、采样窗口和当前有效参数，不复制待执行命令或行为流程。
        internal CharacterForecastObservation(int generation, long alignmentRevision, double time,
            in CharacterForecastOrigin origin, in CharacterBehaviorFrameResult behavior, in CharacterInputSnapshot held,
            float fixedDelta, in CharacterRuntimeConfiguration config, CharacterMovementSettings movement, in CharacterDriveParameters drive)
        {
            this = default;
            var settings = config.Forecast;
            if (!settings.Enabled || !CharacterForecastValidity.TryReadWindow(fixedDelta, settings, config.Overrides, out var window))
                return;
            Generation = generation;
            AlignmentRevision = alignmentRevision;
            Time = time;
            Origin = origin;
            Behavior = behavior;
            Held = new CharacterInputSnapshot(held.Direction, false, held.GuardHeld, CharacterBehaviorId.None, held.Gait);
            Tuning = new CharacterForecastTuning(movement, drive);
            Window = window;
            DirectionTolerance = Mathf.Max(0.001f, InputMath.NonNegative(settings.DirectionTolerance, 0.05f));
        }
    }

    // 一次可交给推演任务的独立快照，由同一时刻的观测、已裁决的切片和冻结配置组成。
    internal struct CharacterForecastFrame
    {
        internal long Version { get; private set; }
        internal double ControlReturnsAt { get; private set; }
        internal float ForcedBraking { get; private set; }
        internal readonly CharacterForecastObservation Observation;
        internal readonly int Generation => Observation.Generation;
        internal readonly double Time => Observation.Time;
        internal readonly CharacterForecastOrigin Origin => Observation.Origin;
        internal readonly CharacterBehaviorFrameResult Behavior => Observation.Behavior;
        internal readonly CharacterInputSnapshot Held => Observation.Held;
        internal readonly CharacterForecastWindow Window => Observation.Window;
        internal readonly CharacterMotionSlice[] Pending;
        internal readonly CharacterMovementSettings Movement;
        internal readonly CharacterDriveParameters Drive;

        // 编辑器预演从给定角色配置直接取得一份完整快照。
        internal CharacterForecastFrame(int generation, double time, in CharacterForecastOrigin origin,
            in CharacterBehaviorFrameResult behavior, in CharacterInputSnapshot held, CharacterRigidbodyBlackboard commands,
            float fixedDelta, in CharacterRuntimeConfiguration config, in CharacterDriveParameters drive, long alignmentRevision = 0)
        {
            var movement = config.ReadMovement();
            var observation = new CharacterForecastObservation(generation, alignmentRevision, time, origin, behavior, held,
                fixedDelta, config, movement, drive);
            this = new CharacterForecastFrame(observation, commands, movement, drive);
        }

        // 仅完整调度读取待执行切片并复制数值配置，以同一次读取建立任务的私有数据。
        internal CharacterForecastFrame(in CharacterForecastObservation observation, CharacterRigidbodyBlackboard commands,
            CharacterMovementSettings movement, in CharacterDriveParameters drive)
        {
            this = default;
            if (observation.Window.Steps == 0) return;
            Observation = observation;
            var origin = observation.Origin;
            Pending = commands.CopyPending(origin.Time, observation.Time, origin.StartCursor);
            Movement = movement.Snapshot();
            Drive = drive.Snapshot();
        }

        // 为已冻结的帧补上调度版本与外部接管时段，保持数值和切片快照独立。
        internal CharacterForecastFrame ForSubmission(long version, double controlReturnsAt, float forcedBraking)
        {
            var submitted = this;
            submitted.Version = version;
            submitted.ControlReturnsAt = controlReturnsAt;
            submitted.ForcedBraking = forcedBraking;
            return submitted;
        }
    }
}
