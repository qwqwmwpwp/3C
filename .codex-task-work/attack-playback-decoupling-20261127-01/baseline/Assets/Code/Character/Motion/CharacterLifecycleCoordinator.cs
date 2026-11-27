using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在物理步、行为、输入与预测之间按固定顺序交接角色的生命周期边界。
    internal sealed class CharacterLifecycleCoordinator
    {
        private readonly CharacterBehaviorDriver owner;
        private readonly CharacterBlackboard blackboard;
        private readonly CharacterBehaviorTimeline timeline;
        private readonly CharacterBehaviorFlow behaviorFlow;
        private readonly CharacterRigidbodyMotor rigidbodyMotor;
        private readonly CharacterPhysicsExecution physics;
        private readonly CharacterForecastSource forecast;
        private readonly CharacterSimulationContinuity continuity;
        private readonly CharacterMotionHandoff handoff;
        private readonly CharacterCommittedBehaviorGrants committedGrants;
        private readonly CharacterFrameSubscriptions frameListeners;
        private CharacterMovementPredictor predictor => forecast.Predictor;

        // 绑定本代驱动器已经装配的服务，所有事务直接读写角色原有黑板。
        internal CharacterLifecycleCoordinator(CharacterBehaviorDriver owner, CharacterBlackboard blackboard,
            CharacterBehaviorTimeline timeline, CharacterBehaviorFlow behaviorFlow, CharacterRigidbodyMotor rigidbodyMotor,
            CharacterPhysicsExecution physics, CharacterForecastSource forecast, CharacterSimulationContinuity continuity,
            CharacterMotionHandoff handoff, CharacterCommittedBehaviorGrants committedGrants, CharacterFrameSubscriptions frameListeners)
        {
            this.owner = owner;
            this.blackboard = blackboard;
            this.timeline = timeline;
            this.behaviorFlow = behaviorFlow;
            this.rigidbodyMotor = rigidbodyMotor;
            this.physics = physics;
            this.forecast = forecast;
            this.continuity = continuity;
            this.handoff = handoff;
            this.committedGrants = committedGrants;
            this.frameListeners = frameListeners;
        }

        // 使用装配时的移动配置初始化行为，不产生重生边界或通知。
        internal void InitializeBehavior(CharacterMovementSettings movement) => behaviorFlow.Reset(movement);

        // 在主线程结算旧物理步后中断旧行为，再切换刚体写权限并发布新基线。
        internal bool ApplyMotionBoundary(in CharacterMotionBoundary request, ref CharacterMotorSuspension suspension)
        {
            if (continuity.InCallback || continuity.Pending || !rigidbodyMotor.Bound || !request.Valid() || !handoff.CanApply(request, owner.StateVersion) ||
                request.Kind == CharacterMotionBoundaryKind.Knockback && !owner.IsSimulating) return false;
            int previous = owner.SimulationVersion;
            physics.FinishPending(publishSolved: true);
            if (owner == null || previous != owner.SimulationVersion || !handoff.CanApply(request, owner.StateVersion)) return false;
            int revision = continuity.Revision;
            using (continuity.Protect())
                InterruptForBoundary(request, owner.ReadMovement(), owner.ReceivesPlayerInput);
            if (owner == null || revision != continuity.Revision) return false;
            handoff.ApplyBody(request, rigidbodyMotor, ref suspension, blackboard.Simulation.CanSimulate(owner, rigidbodyMotor));
            blackboard.Simulation.Running = owner.IsSimulating;
            if (request.Kind != CharacterMotionBoundaryKind.Knockback) owner.MarkCameraCut();
            forecast.CaptureBaseline(boundary: true);
            int current = owner.SimulationVersion;
            var change = predictor.PrepareBoundary(blackboard.Simulation.PredictionSuppressed || handoff.HasHolder,
                blackboard.Rigidbody.KnockbackUntil, blackboard.Rigidbody.KnockbackBraking, request.Kind);
            handoff.Publish(request.Kind, previous, current, owner, blackboard.Simulation, predictor.Output, change);
            return true;
        }

        // 在回调保护内冻结配置与输入归属，然后中断旧代行为并记录外部命令。
        private void InterruptForBoundary(in CharacterMotionBoundary request, CharacterMovementSettings movement,
            bool receivesInput)
        {
            timeline.AdvanceExternal(behaviorFlow, movement, blackboard.Rigidbody.SubmittedThrough);
            if (behaviorFlow.DispatchCancelled) return;
            var reason = request.Kind == CharacterMotionBoundaryKind.Teleport ? CharacterBehaviorEndReason.Teleport :
                request.Kind == CharacterMotionBoundaryKind.Knockback ? CharacterBehaviorEndReason.Knockback : CharacterBehaviorEndReason.ExternalControl;
            var result = behaviorFlow.InterruptAll(reason, movement);
            if (behaviorFlow.DispatchCancelled) return;
            CharacterPlayerInputAdapter.EndContinuity(blackboard.Input, receivesInput);
            blackboard.Simulation.AdvanceContinuity();
            int version = blackboard.Simulation.LifecycleVersion;
            double deviceNow = Time.realtimeSinceStartupAsDouble;
            timeline.EndContinuity(deviceNow);
            timeline.PublishExternal(result, version, deviceNow);
            committedGrants.Observe(result, version);
            blackboard.Rigidbody.ClearCommands();
            if (request.Kind == CharacterMotionBoundaryKind.Knockback)
                blackboard.Rigidbody.BeginKnockback(blackboard.Rigidbody.SubmittedThrough, request.Duration, request.Braking);
        }

        // 完成单次重生，由同一次重生回调提出的重复请求在生命周期入口合并。
        internal void ApplyReset(ref CharacterMotorSuspension suspension)
        {
            physics.FinishPending(publishSolved: false);
            if (handoff.Revoke())
                rigidbodyMotor.ResumeOwnership(CharacterVelocityPolicy.Clear, default, ref suspension, owner.IsSimulating);
            blackboard.Simulation.Running = owner.IsSimulating;
            owner.RecordBehaviorReset();
            behaviorFlow.Reset(owner.ReadMovement());
            blackboard.Simulation.PredictionSuppressed = false;
            blackboard.PhysicsStep.ResetFrame();
            committedGrants.Reset();
            blackboard.Rigidbody.Reset();
            timeline.Reset(Time.realtimeSinceStartupAsDouble);
            rigidbodyMotor.ResetMotion(ref suspension);
            forecast.CaptureBaseline();
            owner.MarkCameraCut();
            blackboard.Simulation.AdvanceReset();
            EndInputContinuity();
            blackboard.Simulation.Running = owner.IsSimulating;
            rigidbodyMotor.SetSuspended(!blackboard.Simulation.Running, ref suspension);
            int current = owner.SimulationVersion;
            var change = predictor.PrepareBoundary(false, double.NegativeInfinity, 0f, CharacterMotionBoundaryKind.Recovery);
            frameListeners.PublishReset(owner, blackboard.Simulation);
            if (owner != null && current == owner.SimulationVersion) predictor.Output.Notify(change);
        }

        // 连续性边界交付最后已求解步，未进入求解的提交归还给运动黑板。
        internal void SynchronizeSimulation(ref CharacterMotorSuspension suspension)
        {
            int version = owner.SimulationVersion;
            physics.FinishPending(publishSolved: true);
            if (owner == null || continuity.Pending) return;
            // 回调可以重生或再次暂停。仅在内层已同步实际运行开关时跳过这次边界。
            if (version != owner.SimulationVersion && blackboard.Simulation.Running == owner.IsSimulating &&
                blackboard.Input.WasReceivingPlayerInput == owner.ReceivesPlayerInput) return;
            EndInputContinuity();
            if (continuity.Pending) return;
            blackboard.Simulation.Running = owner.IsSimulating;
            if (!handoff.HasHolder)
            {
                rigidbodyMotor.SetSuspended(!blackboard.Simulation.Running, ref suspension);
                if (blackboard.Simulation.Running) forecast.CaptureBaseline();
            }
            predictor.EndContinuity();
        }

        // 在连续性边界冻结仍活动的运动，回调申请重生时放弃旧代的命令与输入。
        private void EndInputContinuity()
        {
            using var protection = continuity.Protect();
            var movement = owner.ReadMovement();
            bool ownsBody = !handoff.HasHolder;
            bool receivesInput = owner.ReceivesPlayerInput;
            if (ownsBody && rigidbodyMotor.Available)
            {
                var state = rigidbodyMotor.ReadStepState();
                var active = behaviorFlow.ReadCurrentState(default, state.Facing, movement, state.GroundVelocity);
                if (behaviorFlow.DispatchCancelled) return;
                var command = behaviorFlow.CaptureMotion(timeline.Time, active, default, state.Facing, state.GroundVelocity, movement);
                if (behaviorFlow.DispatchCancelled) return;
                blackboard.Rigidbody.Write(command);
            }
            behaviorFlow.EndInputContinuity();
            if (behaviorFlow.DispatchCancelled) return;
            timeline.EndContinuity(Time.realtimeSinceStartupAsDouble);
            CharacterPlayerInputAdapter.EndContinuity(blackboard.Input, receivesInput);
            blackboard.Simulation.AdvanceContinuity();
        }
    }
}
