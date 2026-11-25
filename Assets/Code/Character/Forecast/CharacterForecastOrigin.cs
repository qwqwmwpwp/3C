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

    // 一次帧末提交的值快照。已裁决的运动只用于从物理游标追到行为时刻，未来只保持持续输入。
    internal struct CharacterForecastFrame
    {
        internal long Version { get; private set; }
        internal readonly int Generation;
        internal readonly long AlignmentRevision;
        internal readonly double Time;
        internal readonly float DirectionTolerance;
        internal double ControlReturnsAt { get; private set; }
        internal float ForcedBraking { get; private set; }
        internal readonly CharacterForecastOrigin Origin;
        internal readonly CharacterBehaviorFrameResult Behavior;
        internal readonly CharacterInputSnapshot Held;
        internal readonly CharacterMotionSlice[] Pending;
        internal readonly CharacterMovementSettings Movement;
        internal readonly CharacterDriveParameters Drive;
        internal readonly CharacterForecastWindow Window;

        // 在主线程一次取得裁决结果、两个时刻间的运动和数值配置，供同步与异步路径共同读取。
        internal CharacterForecastFrame(int generation, double time, in CharacterForecastOrigin origin,
            in CharacterBehaviorFrameResult behavior, in CharacterInputSnapshot held, CharacterRigidbodyBlackboard commands,
            float fixedDelta, in CharacterRuntimeConfiguration config, in CharacterDriveParameters drive, long alignmentRevision = 0)
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
            Pending = commands.CopyPending(origin.Time, time, origin.StartCursor);
            Movement = config.ReadMovement(freeze: true);
            Drive = drive.Snapshot();
            Window = window;
            DirectionTolerance = Mathf.Max(0.001f, InputMath.NonNegative(settings.DirectionTolerance, 0.05f));
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
