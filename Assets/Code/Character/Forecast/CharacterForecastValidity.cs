using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存固定采样步长、正常刷新间隔、对外时长及实际计算步数。
    internal readonly struct CharacterForecastWindow
    {
        internal readonly float Step, Interval, Horizon;
        internal readonly int Steps;

        // 采样长度包含常规续算期间供消费者使用的余量。
        internal CharacterForecastWindow(float step, float interval, float horizon, int steps)
        {
            Step = step;
            Interval = interval;
            Horizon = horizon;
            Steps = steps;
        }
    }

    // 保存轨迹真正依赖的连续运动数值，可与原地编辑之后的新观测逐项比较。
    internal readonly struct CharacterForecastTuning
    {
        private readonly (float walk, float run, float sprint, float turn) movement;
        private readonly (float ground, float air, float turnSpeed, float footfall) acceleration;
        private readonly (float walk, float run, float sprint) braking, turnRise;
        private readonly (float settle, float brakingRatio, float lateralRatio) turning;
        private readonly (float seconds, float deceleration, float minSpeed, float footfall) leadIn;
        private readonly (float slopeLimit, float stepHeight, float drag, bool useGravity) body;
        private readonly Vector3 gravity;

        // 记录已应用实例覆盖的驱动参数，以及本次行为和物理计算读取的数值。
        internal CharacterForecastTuning(CharacterMovementSettings speeds, in CharacterDriveParameters drive)
        {
            var motor = drive.Settings;
            movement = (speeds.WalkSpeed, speeds.MoveSpeed, speeds.SprintSpeed, speeds.MoveTurnSpeed);
            acceleration = (drive.GroundAcceleration, motor.airAcceleration, drive.TurnSpeed, drive.FootfallDistance);
            braking = (motor.walkBraking, motor.runBraking, motor.sprintBraking);
            turnRise = (motor.walkTurnRise, motor.runTurnRise, motor.sprintTurnRise);
            turning = (motor.turnSettle, motor.turnBrakingRatio, motor.turnLateralRatio);
            leadIn = (motor.runBrakeLeadInSeconds, motor.runBrakeLeadInDeceleration,
                motor.runBrakeLeadInMinSpeed, motor.runBrakeLeadInFootfallDistance);
            body = (motor.slopeLimit, motor.stepHeight, drive.Drag, drive.UseGravity);
            gravity = drive.Gravity;
        }

        // 标量沿用逐项相等判断，重力向量沿用 Unity 的比较精度。
        internal bool Matches(in CharacterForecastTuning other) => movement == other.movement && acceleration == other.acceleration &&
            braking == other.braking && turnRise == other.turnRise && turning == other.turning && leadIn == other.leadIn &&
            body == other.body && gravity == other.gravity;
    }

    // 按当前帧末的输入、动作、动态接触和实测偏差校验任务与已发布轨迹。
    internal static class CharacterForecastValidity
    {
        private const float MinimumPublishedLookahead = 0.30f; // 非接触截断的发布结果应至少覆盖的未来秒数。

        // 结果从行为时刻起采样，额外保留一个刷新周期和一个采样步。
        internal static bool TryReadWindow(float dt, CharacterForecastSettings config,
            CharacterProfileOverrides overrides, out CharacterForecastWindow window)
        {
            float interval = InputMath.NonNegative(overrides.ForecastInterval.Resolve(config.Interval), 0.2f);
            float horizon = InputMath.NonNegative(overrides.ForecastHorizon.Resolve(config.Horizon), 2f);
            double count = dt > 0f ? Math.Ceiling((horizon + (double)interval) / dt) + 1d : 0d;
            int steps = count > 0d && count <= 8192 ? (int)count : 0;
            window = new CharacterForecastWindow(dt, interval, horizon, steps);
            return steps > 0 && horizon > 0f;
        }

        // 以任务真正使用的那一帧为基准，能识别许多小幅输入变化累计造成的方向偏离。
        internal static ForecastRefreshReason Changed(in CharacterForecastFrame basis, in CharacterForecastObservation current,
            double controlReturnsAt, float forcedBraking)
        {
            ref readonly var old = ref basis.Observation;
            if (old.Generation != current.Generation) return ForecastRefreshReason.SimulationBoundary;
            if (old.AlignmentRevision != current.AlignmentRevision) return ForecastRefreshReason.ExternalState;
            if (old.Origin.DynamicContact != current.Origin.DynamicContact) return ForecastRefreshReason.DynamicContact;
            if (basis.ControlReturnsAt != controlReturnsAt || basis.ForcedBraking != forcedBraking)
                return ForecastRefreshReason.Knockback;
            var changed = ChangedInput(basis, current.Held);
            if (changed != ForecastRefreshReason.None) return changed;
            return old.Window.Step == current.Window.Step && old.Window.Interval == current.Window.Interval &&
                old.Window.Horizon == current.Window.Horizon && old.DirectionTolerance == current.DirectionTolerance &&
                old.Tuning.Matches(current.Tuning) ? ForecastRefreshReason.None : ForecastRefreshReason.Settings;
        }

        // 动画采集前也可只检查已完成的行为和输入，具体计算仍由帧末统一启动。
        internal static ForecastRefreshReason ChangedInput(in CharacterForecastFrame basis, in CharacterInputSnapshot input)
        {
            var oldInput = basis.Held;
            if (input.Gait != oldInput.Gait || input.GuardHeld != oldInput.GuardHeld ||
                (input.Direction == Vector2.zero) != (oldInput.Direction == Vector2.zero) ||
                (input.Direction - oldInput.Direction).sqrMagnitude >= basis.Observation.DirectionTolerance * basis.Observation.DirectionTolerance)
                return ForecastRefreshReason.MovementInput;
            return ForecastRefreshReason.None;
        }

        // 后台任务可晚于新帧到达。校验沿途全部已求解步、当前实测和剩余可信窗口。
        internal static ForecastRefreshReason ValidateCompleted(CharacterMovementForecast path,
            List<(double time, CharacterForecastSample pose)> observed, in CharacterForecastFrame current)
        {
            foreach (var check in observed)
            {
                var difference = CompareAt(path, check.time, check.pose);
                if (difference != ForecastRefreshReason.None) return difference;
            }
            return ValidatePublished(path, current.Observation);
        }

        // 可信边界前可以保留短样本；边界过去后即使是动态物体截断也须重新取得起点。
        internal static ForecastRefreshReason ValidatePublished(CharacterMovementForecast path, in CharacterForecastObservation current)
        {
            var difference = CompareAt(path, current.Origin.Time, current.Origin.Pose);
            if (difference != ForecastRefreshReason.None) return difference;
            float required = path.EnvironmentLimited ? 0f : Mathf.Min(current.Window.Horizon, MinimumPublishedLookahead);
            if (!path.TrySample(current.Time, required, out _))
                return path.EnvironmentLimited ? ForecastRefreshReason.RecheckContact : ForecastRefreshReason.Expired;
            return CompareBehavior(path, current.Time, current.Window.Step, current.Behavior);
        }

        // 动作状态按离散采样点保存；真实边界可能落在当前点与下一采样点之间。
        internal static ForecastRefreshReason CompareBehavior(CharacterMovementForecast path, double time, float step,
            in CharacterBehaviorFrameResult behavior)
        {
            if (!path.TrySample(time, 0f, out var expected) || expected.ExternallyControlled || SameBehavior(expected, behavior))
                return ForecastRefreshReason.None;
            if (path.TrySample(time + step, 0f, out var next) && !next.ExternallyControlled && SameBehavior(next, behavior))
                return ForecastRefreshReason.None;
            return ForecastRefreshReason.Behavior;
        }

        // 用完整动作实例及接续缓存识别假设；扣费和阶段在推演到同一时刻之后才比较。
        private static bool SameBehavior(in CharacterForecastSample sample, in CharacterBehaviorFrameResult behavior)
        {
            var attack = behavior.Attack;
            return sample.Behaviors == behavior.ActiveBehaviors && sample.MoveApproved == behavior.MoveApproved &&
                sample.Attack == attack.Move && sample.AttackInstance == attack.InstanceId && sample.AttackPhase == attack.Phase &&
                sample.BufferedMove == attack.BufferedMove && sample.LinkAtSeconds == attack.LinkAtSeconds &&
                sample.Stamina == behavior.StaminaRemaining && (!behavior.DashActive || sample.DashDirection == behavior.DashDirection);
        }

        // 只比较同一物理时刻的实测，帧末之前尚无推演样本的区间由快照中的运动覆盖。
        internal static ForecastRefreshReason CompareAt(CharacterMovementForecast path, double time, in CharacterForecastSample actual)
        {
            if (!path.TrySample(time, 0f, out var expected)) return ForecastRefreshReason.None;
            if (actual.Contacts != expected.Contacts) return ForecastRefreshReason.Contact;
            if ((actual.Position - expected.Position).sqrMagnitude > 0.0064f ||
                (actual.Velocity - expected.Velocity).sqrMagnitude > 0.36f ||
                Quaternion.Angle(actual.Rotation, expected.Rotation) > 6f) return ForecastRefreshReason.Deviation;
            return ForecastRefreshReason.None;
        }
    }
}
