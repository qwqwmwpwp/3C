using UnityEngine;
using Fact = ThreeC.CharacterBehavior.AnimationDecisionInput;
using Output = ThreeC.CharacterBehavior.AnimationDecisionOutput;

namespace ThreeC.CharacterBehavior
{
    // 提供一次求值使用的已提交步态、方向、制动和剩余时间，不持有角色写入权限。
    internal struct LocomotionDecisionState
    {
        internal DecisionGait LastGait, PlayingGait;
        internal CharacterLocomotionPhase Phase;
        internal Vector3 PreviousDirection, PreviousVelocity, ReleaseDirection;
        internal float GroundTravel, RemainingSeconds, StopSpeed;
        internal bool MainBrakeDue;
        internal bool Releasing => Phase == CharacterLocomotionPhase.BrakeLeadIn || Phase == CharacterLocomotionPhase.Stopping;
    }

    // 将各消费阶段的实际观测投影为程序输入；实例、方向和计时仍由意图转换器提交。
    internal sealed class CharacterLocomotionDecisions
    {
        private readonly AnimationDecisionEvaluator fixedStep, forecast, variant, grant, stop;
        private readonly bool gaitEnabled, turnEnabled, stopEnabled;
        private readonly float idleEnter, runEnter, runExit, sprintEnter, sprintExit, reverseLimit, directAngle, minimumStopTravel;
        internal readonly float IdleExit, StartSeconds, TurnSeconds, ReverseWindowSeconds;

        // 在装配线程固定所有参数，运行求值仅读取数值与不可变程序。
        internal CharacterLocomotionDecisions(CharacterAnimationConfiguration config)
        {
            fixedStep = new AnimationDecisionEvaluator(config.Decisions, AnimationDecisionStage.Step | AnimationDecisionStage.Stride);
            forecast = new AnimationDecisionEvaluator(config.Decisions, AnimationDecisionStage.Forecast);
            variant = new AnimationDecisionEvaluator(config.Decisions, AnimationDecisionStage.MotionVariant);
            grant = new AnimationDecisionEvaluator(config.Decisions, AnimationDecisionStage.Grant);
            stop = new AnimationDecisionEvaluator(config.Decisions, AnimationDecisionStage.StopGait);
            gaitEnabled = config.HasGait;
            turnEnabled = config.HasStartTurn;
            stopEnabled = config.HasStop;
            idleEnter = InputMath.NonNegative(config.IdleEnterSpeed, 0.08f);
            IdleExit = Mathf.Max(idleEnter, InputMath.NonNegative(config.IdleExitSpeed, 0.15f));
            runExit = Mathf.Max(IdleExit, InputMath.NonNegative(config.RunExitSpeed, 3f));
            runEnter = Mathf.Max(runExit, InputMath.NonNegative(config.RunEnterSpeed, 3.5f));
            sprintExit = Mathf.Max(runEnter, InputMath.NonNegative(config.SprintExitSpeed, 5.6f));
            sprintEnter = Mathf.Max(sprintExit, InputMath.NonNegative(config.SprintEnterSpeed, 6f));
            directAngle = Mathf.Clamp(config.MatchTurnAngle, 15f, 180f);
            StartSeconds = InputMath.NonNegative(config.LocomotionStartSeconds, 0.22f);
            TurnSeconds = InputMath.NonNegative(config.LocomotionTurnSeconds, 0.32f);
            float angle = Mathf.Clamp(InputMath.NonNegative(config.LocomotionReverseAngle, 120f), 90f, 180f);
            reverseLimit = Mathf.Cos(angle * Mathf.Deg2Rad) + 0.000001f;
            ReverseWindowSeconds = Mathf.Max(0.15f, InputMath.NonNegative(config.MatchReverseHold, 0.08f) + 0.1f);
            minimumStopTravel = InputMath.NonNegative(config.MinimumStopTravel, 0.09f);
        }

        // 结合当前变体与运动采样求出下一次动作使用的变体。
        internal bool MovingAction(in CharacterAnimationMotion motion, bool moving)
        {
            variant.Begin(AnimationDecisionStage.MotionVariant);
            variant.Set(Fact.Valid, motion.IsValid);
            variant.Set(Fact.RetainedStride, motion.RetainedStrideSpeed.HasValue);
            variant.Set(Fact.MovingVariant, moving);
            variant.Set(Fact.Speed, motion.ActualSpeed);
            variant.Set(Fact.IdleEnter, idleEnter);
            variant.Set(Fact.IdleExit, IdleExit);
            return variant.Flag(Output.MovingAction);
        }

        // 读取行为许可阶段的突进输出，与待消费固定步使用不同寄存器。
        internal bool TakeDash(in CharacterBehaviorFrameResult behavior)
        {
            grant.Begin(AnimationDecisionStage.Grant);
            grant.Set(Fact.GaitEnabled, gaitEnabled);
            grant.Set(Fact.DashGranted, behavior.DashActive);
            grant.Set(Fact.MoveApproved, behavior.MoveApproved);
            grant.Set(Fact.Attacking, behavior.Attack.InstanceId != 0);
            bool requested = grant.Flag(Output.TakeDash);
            return gaitEnabled && behavior.DashActive && requested;
        }

        // 在真正提交前确定是否需要使用发布的预测轨迹重新采样。
        internal bool MayProject(in CharacterAnimationMotion motion, in CharacterBehaviorFrameResult behavior)
        {
            forecast.Begin(AnimationDecisionStage.Forecast);
            forecast.Set(Fact.GaitEnabled, gaitEnabled);
            forecast.Set(Fact.Valid, motion.IsValid);
            forecast.Set(Fact.TurnSeconds, TurnSeconds);
            forecast.Set(Fact.TrajectoryValid, motion.Trajectory.IsValid);
            forecast.Set(Fact.Blocked, motion.IsBlocked);
            forecast.Set(Fact.Speed, motion.ActualSpeed);
            forecast.Set(Fact.IdleEnter, idleEnter);
            forecast.Set(Fact.MoveApproved, behavior.MoveApproved);
            forecast.Set(Fact.RecoversMovement, behavior.ActionMotion.RecoversMovement);
            return forecast.Flag(Output.ProjectGait);
        }

        // 填充物理步入口处的行为事实、方向观测、松键动量与制动阶段。
        internal AnimationDecisionEvaluator Step(in CharacterAnimationMotion motion, in CharacterBehaviorFrameResult behavior,
            in CharacterInputSnapshot input, in LocomotionDecisionState state, Vector3 expectedDirection, float signedTurn)
        {
            var e = fixedStep;
            e.ClearTrace();
            e.Begin(AnimationDecisionStage.Step);
            e.Set(Fact.TurnEnabled, turnEnabled);
            e.Set(Fact.StopEnabled, stopEnabled);
            e.Set(Fact.MoveApproved, behavior.MoveApproved);
            e.Set(Fact.BasicDisplacement, behavior.DisplacementSource == CharacterDisplacementSource.BasicMovement);
            e.Set(Fact.DashDisplacement, behavior.DisplacementSource == CharacterDisplacementSource.Dash);
            e.Set(Fact.Grounded, (motion.CollisionFlags & CollisionFlags.Below) != 0);
            e.Set(Fact.Blocked, motion.IsBlocked);
            e.Set(Fact.HasReleaseDirection, state.ReleaseDirection != Vector3.zero);
            e.Set(Fact.ReleaseDot, Vector3.Dot(state.ReleaseDirection, motion.ExpectedVelocity.normalized));
            e.Set(Fact.ReverseLimit, reverseLimit);
            e.Set(Fact.Releasing, state.Releasing);
            e.Set(Fact.HasDirection, expectedDirection != Vector3.zero);
            e.Set(Fact.RecoversMovement, behavior.ActionMotion.RecoversMovement);
            e.Set(Fact.AccelerationValid, motion.HasAcceleration);
            e.Set(Fact.HasPreviousDirection, state.PreviousDirection != Vector3.zero);
            e.Set(Fact.PreviousSpeed, state.PreviousVelocity.magnitude);
            e.Set(Fact.IdleExit, IdleExit);
            e.Set(Fact.DirectionChange, Vector3.Angle(state.PreviousDirection, expectedDirection));
            e.Set(Fact.ActualTurnAngle, Mathf.Abs(signedTurn));
            e.Set(Fact.DirectAngle, directAngle);
            e.Set(Fact.BrakeObstructed, motion.Trajectory.IsBrakeObstructed);
            e.Set(Fact.InputReleased, input.Direction == Vector2.zero);
            e.Set(Fact.NoProvider, behavior.DisplacementProvider == CharacterBehaviorId.None);
            e.Set(Fact.Stopping, state.Phase == CharacterLocomotionPhase.Stopping);
            e.Set(Fact.MainBrakeDue, state.MainBrakeDue);
            e.Set(Fact.Braking, motion.Trajectory.IsBraking);
            e.Set(Fact.MoveEnded, behavior.MoveEnded);
            e.Set(Fact.LastGait, state.LastGait);
            e.Set(Fact.GroundTravel, state.GroundTravel);
            e.Set(Fact.MinimumStopTravel, minimumStopTravel);
            e.Set(Fact.PreviousForwardSpeed, Vector3.Dot(state.PreviousDirection, state.PreviousVelocity));
            return e;
        }

        // 在释放恢复处理结束后采样实际步态起点、运动趋势、动作与预测输入。
        internal AnimationDecisionEvaluator Stride(in CharacterAnimationMotion motion, in CharacterBehaviorFrameResult behavior,
            in LocomotionDecisionState state, CharacterMoveGait requested, Vector3 expectedDirection,
            bool resuming, bool canStart, bool preserveEntry, bool followsDash)
        {
            var e = fixedStep;
            e.Begin(AnimationDecisionStage.Stride);
            e.Set(Fact.Speed, motion.ActualSpeed);
            e.Set(Fact.IdleEnter, idleEnter);
            e.Set(Fact.IdleExit, IdleExit);
            e.Set(Fact.RunEnter, runEnter);
            e.Set(Fact.RunExit, runExit);
            e.Set(Fact.SprintEnter, sprintEnter);
            e.Set(Fact.SprintExit, sprintExit);
            e.Set(Fact.LastGait, state.LastGait);
            e.Set(Fact.RequestedGait, requested == CharacterMoveGait.Sprint ? DecisionGait.Sprint :
                requested == CharacterMoveGait.Walk ? DecisionGait.Walk : DecisionGait.Run);
            e.Set(Fact.Turning, state.Phase == CharacterLocomotionPhase.Turning);
            e.Set(Fact.Resuming, resuming);
            e.Set(Fact.TrajectoryValid, motion.Trajectory.IsValid);
            e.Set(Fact.ForecastForwardSpeed, Vector3.Dot(motion.Trajectory.FutureVelocity, expectedDirection));
            e.Set(Fact.CanStart, canStart);
            e.Set(Fact.PreserveEntry, preserveEntry);
            e.Set(Fact.Blocked, motion.IsBlocked);
            e.Set(Fact.HasPreviousDirection, state.PreviousDirection != Vector3.zero);
            e.Set(Fact.HasPreviousVelocity, state.PreviousVelocity != Vector3.zero);
            e.Set(Fact.TurnSeconds, TurnSeconds);
            e.Set(Fact.DirectionDot, Vector3.Dot(state.PreviousDirection, expectedDirection));
            e.Set(Fact.VelocityDot, Vector3.Dot(state.PreviousVelocity.normalized, expectedDirection));
            e.Set(Fact.ReverseLimit, reverseLimit);
            e.Set(Fact.FollowsDash, followsDash);
            e.Set(Fact.Attacking, behavior.Attack.InstanceId != 0);
            e.Set(Fact.RecoversMovement, behavior.ActionMotion.RecoversMovement);
            e.Set(Fact.StartSeconds, StartSeconds);
            e.Set(Fact.RemainingSeconds, state.RemainingSeconds);
            return e;
        }

        // 读取松键时实速和此前播放步态，求得收步资源所属步态。
        internal string StopGait(in LocomotionDecisionState state)
        {
            stop.Begin(AnimationDecisionStage.StopGait);
            stop.Set(Fact.StopSpeed, state.StopSpeed);
            stop.Set(Fact.RunEnter, runEnter);
            stop.Set(Fact.SprintExit, sprintExit);
            stop.Set(Fact.LastGait, state.LastGait);
            stop.Set(Fact.PlayingGait, state.PlayingGait);
            return Key((DecisionGait)stop.Number(Output.StopGait));
        }

        // 将现有动作键转为判定程序的有类型步态。
        internal static DecisionGait Gait(string key) => key switch
        {
            CharacterAnimationIntentConverter.SprintKey => DecisionGait.Sprint,
            CharacterAnimationIntentConverter.MoveKey => DecisionGait.Run,
            CharacterAnimationIntentConverter.WalkKey => DecisionGait.Walk,
            _ => DecisionGait.Idle
        };

        // 将程序选择的步态转换为原有合成器和播放库共用的动作键。
        internal static string Key(DecisionGait gait) => gait switch
        {
            DecisionGait.Sprint => CharacterAnimationIntentConverter.SprintKey,
            DecisionGait.Run => CharacterAnimationIntentConverter.MoveKey,
            DecisionGait.Walk => CharacterAnimationIntentConverter.WalkKey,
            _ => CharacterAnimationIntentConverter.IdleKey
        };

#if UNITY_EDITOR
        // 在主线程接回批次后只读格式化各入口的最近一次判定。
        internal void AppendTrace(System.Text.StringBuilder text)
        {
            grant.AppendTrace(text);
            variant.AppendTrace(text);
            forecast.AppendTrace(text);
            fixedStep.AppendTrace(text);
            stop.AppendTrace(text);
        }
#endif
    }
}
