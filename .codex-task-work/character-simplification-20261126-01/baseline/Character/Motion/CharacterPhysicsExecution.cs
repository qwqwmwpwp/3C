using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 协调角色的一次物理提交、求解后的唯一交付和求解前撤回。
    internal sealed class CharacterPhysicsExecution
    {
        private readonly CharacterBlackboard blackboard;
        private readonly CharacterRigidbodyMotor rigidbodyMotor;
        private readonly CharacterBehaviorFlow behaviorFlow;
        private readonly CharacterForecastSource forecast;
        private readonly CharacterFrameSubscriptions frameListeners;
        private readonly Behaviour owner;
        private int SimulationVersion => blackboard.Simulation.LifecycleVersion;

        // 绑定固定步的命令来源、真实马达和求解结果的订阅者。
        internal CharacterPhysicsExecution(CharacterBlackboard board, CharacterRigidbodyMotor motor,
            CharacterBehaviorFlow flow, CharacterForecastSource forecast, CharacterFrameSubscriptions listeners, Behaviour owner)
        {
            blackboard = board;
            rigidbodyMotor = motor;
            behaviorFlow = flow;
            this.forecast = forecast;
            frameListeners = listeners;
            this.owner = owner;
        }

        // 消费已发布的运动区间并登记本次物理提交，等待求解器回报。
        internal void Submit(in CharacterRuntimeConfiguration config, float dt, CharacterSimulationContinuity continuity)
        {
            var body = blackboard.Rigidbody;
            var pending = rigidbodyMotor.DriveStep(body, dt, body.SubmittedThrough + dt,
                blackboard.PhysicsStep.LatestFrame.FrameNumber + 1, rigidbodyMotor.Parameters(config), continuity);
            if (pending.IsScheduled) blackboard.PhysicsStep.Schedule(pending);
        }

        // 同时标记求解资格和接触所属的固定步，供求解回调内的交接使用。
        internal void MarkSolverEntered()
        {
            blackboard.PhysicsStep.MarkSolverEntered();
            rigidbodyMotor.MarkSolverTime(Time.fixedTimeAsDouble);
        }

        // 先记住已求解的真实起点，再校正预测并通知可能重入的订阅者。
        internal void Commit()
        {
            var steps = blackboard.PhysicsStep;
            if (!steps.TryCommitSolved(rigidbodyMotor, SimulationVersion, out var actual)) return;
            var frame = steps.LatestFrame;
            blackboard.Rigidbody.MarkSolved();
            forecast.StoreBaseline(actual);
            long finished = rigidbodyMotor.ObserveAlignment(ref behaviorFlow.Alignment, actual);
            if (finished != 0)
            {
                blackboard.Rigidbody.CancelAlignment(finished);
                forecast.InvalidateAlignment();
            }
            if (owner == null || frame.SimulationVersion != SimulationVersion) return;
            forecast.Predictor.ObserveSolvedStep(forecast.Origin);
            if (owner != null && frame.SimulationVersion == SimulationVersion)
                frameListeners.PublishCommitted(frame, owner, blackboard.Simulation);
        }

        // 边界处撤回未求解的提交，已求解的步按暂停或重生的通知约定结算。
        internal void FinishPending(bool publishSolved)
        {
            var steps = blackboard.PhysicsStep;
            if (!steps.HasPending) return;
            if (steps.ShouldCommitAtBoundary(Time.inFixedTimeStep, Time.fixedTimeAsDouble))
            {
                if (publishSolved) Commit();
                else steps.DiscardPending();
                return;
            }
            rigidbodyMotor.CancelPending(blackboard.Rigidbody);
            steps.DiscardPending();
        }
    }
}
