using System;

namespace ThreeC.CharacterBehavior
{
    // 标记各项输入可在哪个时刻取得，以及输出归哪个阶段提交。
    [Flags]
    internal enum AnimationDecisionStage
    {
        MotionVariant = 1,
        Grant = 2,
        Forecast = 4,
        Step = 8,
        Stride = 16,
        StopGait = 32,
        SearchBefore = 64,
        SearchAfter = 128,
        ReleaseTiming = 256,
        CandidateSelection = 512
    }

    // 区分可比较数值、条件以及动作结果，防止把步态当作速度使用。
    internal enum DecisionType { Number, Flag, Gait, Stride, Release }

    // 描述判定输入或输出在编译时需要验证的类型和采样阶段。
    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class DecisionSlotAttribute : Attribute
    {
        internal readonly AnimationDecisionStage Stages;
        internal readonly DecisionType Type;

        // 保存该字段的类型和允许使用的阶段。
        public DecisionSlotAttribute(AnimationDecisionStage stages, DecisionType type)
        {
            Stages = stages;
            Type = type;
        }
    }

    // 列出角色黑板在运动、行为、预测与搜索阶段提供的观测和参数。
    internal enum AnimationDecisionInput
    {
        [DecisionSlot(AnimationDecisionStage.MotionVariant | AnimationDecisionStage.Forecast, DecisionType.Flag)] Valid,
        [DecisionSlot(AnimationDecisionStage.MotionVariant, DecisionType.Flag)] RetainedStride,
        [DecisionSlot(AnimationDecisionStage.MotionVariant, DecisionType.Flag)] MovingVariant,
        [DecisionSlot(AnimationDecisionStage.MotionVariant | AnimationDecisionStage.Forecast | AnimationDecisionStage.Stride | AnimationDecisionStage.ReleaseTiming | AnimationDecisionStage.CandidateSelection, DecisionType.Number)] Speed,
        [DecisionSlot(AnimationDecisionStage.MotionVariant | AnimationDecisionStage.Forecast | AnimationDecisionStage.Stride, DecisionType.Number)] IdleEnter,
        [DecisionSlot(AnimationDecisionStage.MotionVariant | AnimationDecisionStage.Step | AnimationDecisionStage.Stride, DecisionType.Number)] IdleExit,
        [DecisionSlot(AnimationDecisionStage.Grant | AnimationDecisionStage.Forecast, DecisionType.Flag)] GaitEnabled,
        [DecisionSlot(AnimationDecisionStage.Grant, DecisionType.Flag)] DashGranted,
        [DecisionSlot(AnimationDecisionStage.Forecast | AnimationDecisionStage.Stride, DecisionType.Number)] TurnSeconds,
        [DecisionSlot(AnimationDecisionStage.Forecast | AnimationDecisionStage.Stride, DecisionType.Flag)] TrajectoryValid,
        [DecisionSlot(AnimationDecisionStage.Forecast | AnimationDecisionStage.Step | AnimationDecisionStage.Stride, DecisionType.Flag)] Blocked,
        [DecisionSlot(AnimationDecisionStage.Grant | AnimationDecisionStage.Forecast | AnimationDecisionStage.Step | AnimationDecisionStage.CandidateSelection, DecisionType.Flag)] MoveApproved,
        [DecisionSlot(AnimationDecisionStage.Forecast | AnimationDecisionStage.Step | AnimationDecisionStage.Stride, DecisionType.Flag)] RecoversMovement,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] TurnEnabled,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] StopEnabled,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] BasicDisplacement,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] DashDisplacement,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] Grounded,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] HasReleaseDirection,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Number)] ReleaseDot,
        [DecisionSlot(AnimationDecisionStage.Step | AnimationDecisionStage.Stride, DecisionType.Number)] ReverseLimit,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] Releasing,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] HasDirection,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] AccelerationValid,
        [DecisionSlot(AnimationDecisionStage.Step | AnimationDecisionStage.Stride, DecisionType.Flag)] HasPreviousDirection,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Number)] PreviousSpeed,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Number)] DirectionChange,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Number)] ActualTurnAngle,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Number)] DirectAngle,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] BrakeObstructed,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] InputReleased,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] NoProvider,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] Stopping,
        [DecisionSlot(AnimationDecisionStage.Step | AnimationDecisionStage.ReleaseTiming, DecisionType.Flag)] MainBrakeDue,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] Braking,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] MoveEnded,
        [DecisionSlot(AnimationDecisionStage.Step | AnimationDecisionStage.Stride | AnimationDecisionStage.StopGait, DecisionType.Gait)] LastGait,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Number)] GroundTravel,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Number)] MinimumStopTravel,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Number)] PreviousForwardSpeed,
        [DecisionSlot(AnimationDecisionStage.Stride | AnimationDecisionStage.StopGait, DecisionType.Number)] RunEnter,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Number)] RunExit,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Number)] SprintEnter,
        [DecisionSlot(AnimationDecisionStage.Stride | AnimationDecisionStage.StopGait, DecisionType.Number)] SprintExit,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Gait)] RequestedGait,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Flag)] Turning,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Flag)] Resuming,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Number)] ForecastForwardSpeed,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Flag)] CanStart,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Flag)] PreserveEntry,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Flag)] HasPreviousVelocity,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Number)] DirectionDot,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Number)] VelocityDot,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Flag)] FollowsDash,
        [DecisionSlot(AnimationDecisionStage.Grant | AnimationDecisionStage.Stride, DecisionType.Flag)] Attacking,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Number)] StartSeconds,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Number)] RemainingSeconds,
        [DecisionSlot(AnimationDecisionStage.StopGait, DecisionType.Number)] StopSpeed,
        [DecisionSlot(AnimationDecisionStage.StopGait, DecisionType.Gait)] PlayingGait,
        [DecisionSlot(AnimationDecisionStage.SearchBefore, DecisionType.Flag)] Special,
        [DecisionSlot(AnimationDecisionStage.SearchBefore | AnimationDecisionStage.SearchAfter, DecisionType.Flag)] Unchanged,
        [DecisionSlot(AnimationDecisionStage.SearchBefore, DecisionType.Flag)] HasTrackedPlayback,
        [DecisionSlot(AnimationDecisionStage.SearchBefore, DecisionType.Number)] SearchDelay,
        [DecisionSlot(AnimationDecisionStage.SearchAfter, DecisionType.Flag)] CanWait,
        [DecisionSlot(AnimationDecisionStage.SearchAfter, DecisionType.Flag)] CanKeep,
        [DecisionSlot(AnimationDecisionStage.SearchAfter, DecisionType.Flag)] SameCycle,
        [DecisionSlot(AnimationDecisionStage.SearchAfter, DecisionType.Flag)] IdleCrossing,
        [DecisionSlot(AnimationDecisionStage.SearchAfter, DecisionType.Flag)] HasWinner,
        [DecisionSlot(AnimationDecisionStage.SearchAfter, DecisionType.Flag)] Moving,
        [DecisionSlot(AnimationDecisionStage.SearchAfter, DecisionType.Number)] KeepCost,
        [DecisionSlot(AnimationDecisionStage.SearchAfter, DecisionType.Number)] BestCost,
        [DecisionSlot(AnimationDecisionStage.SearchAfter, DecisionType.Number)] Margin,
        [DecisionSlot(AnimationDecisionStage.ReleaseTiming, DecisionType.Flag)] LastLocomotion,
        [DecisionSlot(AnimationDecisionStage.ReleaseTiming, DecisionType.Flag)] EarlyStance,
        [DecisionSlot(AnimationDecisionStage.CandidateSelection, DecisionType.Number)] SignedAngle,
        [DecisionSlot(AnimationDecisionStage.CandidateSelection, DecisionType.Flag)] Returning
    }

    // 命名每个阶段需要的数据输出，资源必须为每个输出提供兜底分支。
    internal enum AnimationDecisionOutput
    {
        [DecisionSlot(AnimationDecisionStage.MotionVariant, DecisionType.Flag)] MovingAction,
        [DecisionSlot(AnimationDecisionStage.Grant, DecisionType.Flag)] TakeDash,
        [DecisionSlot(AnimationDecisionStage.Forecast, DecisionType.Flag)] ProjectGait,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] ReverseInput,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] ReleaseMotion,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] DirectTurn,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Flag)] ResumeStride,
        [DecisionSlot(AnimationDecisionStage.Step, DecisionType.Release)] ReleaseAction,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Gait)] GroundGait,
        [DecisionSlot(AnimationDecisionStage.Stride, DecisionType.Stride)] StrideAction,
        [DecisionSlot(AnimationDecisionStage.StopGait, DecisionType.Gait)] StopGait,
        [DecisionSlot(AnimationDecisionStage.SearchBefore, DecisionType.Flag)] DelaySearch,
        [DecisionSlot(AnimationDecisionStage.SearchBefore, DecisionType.Flag)] AllowKeepPlaying,
        [DecisionSlot(AnimationDecisionStage.SearchBefore, DecisionType.Number)] SearchInterval,
        [DecisionSlot(AnimationDecisionStage.SearchAfter, DecisionType.Flag)] KeepCurrent,
        [DecisionSlot(AnimationDecisionStage.ReleaseTiming, DecisionType.Flag)] StartStop,
        [DecisionSlot(AnimationDecisionStage.CandidateSelection, DecisionType.Flag)] AllowCandidateRule
    }

    // 约定程序与基础意图之间传递的步态。
    internal enum DecisionGait { Idle, Walk, Run, Sprint }

    // 指示普通移动本步要启动、延续或结束的短暂阶段。
    internal enum DecisionStride { Base, Start, Turn, Continue }

    // 指示松键后的动量和制动计时如何提交。
    internal enum DecisionRelease { Idle, Hold, Brake, Begin }
}
