using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存实走与预测共同读取的最后实测起点，并从当前帧冻结后续推演所需的观测。
    internal sealed class CharacterForecastSource
    {
        private readonly CharacterBlackboard board;
        private readonly CharacterRigidbodyMotor motor;
        private readonly CharacterBehaviorTimeline timeline;
        private readonly CharacterBehaviorFlow flow;
        internal readonly CharacterMovementPredictor Predictor;
        internal CharacterForecastOrigin Origin { get; private set; }
        internal bool NeedsSolvedOrigin;

        // 绑定逐帧裁决与实测状态，两者在捕获快照前由主线程读取。
        internal CharacterForecastSource(CharacterBlackboard board, CharacterRigidbodyMotor motor,
            CharacterBehaviorTimeline timeline, CharacterBehaviorFlow flow, CharacterMovementPredictor predictor)
        {
            this.board = board;
            this.motor = motor;
            this.timeline = timeline;
            this.flow = flow;
            Predictor = predictor;
        }

        // 在恢复时读取真实刚体；外部边界的旧已求解行为不可作为新位姿的来源。
        internal void CaptureBaseline(bool boundary = false)
        {
            if (!motor.Bound) return;
            StoreBaseline(motor.ReadSolvedPose(), boundary);
        }

        // 将同一物理时刻的实测位姿、行为和马达游标组成下一次推演起点。
        internal void StoreBaseline(in CharacterMotorObservation actual, bool boundary = false)
        {
            var physics = board.PhysicsStep;
            var frame = physics.LatestFrame;
            double physicalTime = physics.LastSolvedTime;
            Origin = new CharacterForecastOrigin(physicalTime, frame.FrameNumber, actual,
                boundary ? timeline.Latest.Behavior : frame.Behavior, boundary ? default : frame.Input.Gait,
                motor.DriveState, board.Rigidbody.StartCursor);
            NeedsSolvedOrigin = false;
        }

        // 按当前参数核对已发布及正在推演的轨迹，撤回变化的行为和输入假设。
        internal void CheckAssumptions(in CharacterRuntimeConfiguration config, CharacterMovementSettings movement)
        {
            var tuning = new CharacterForecastTuning(movement, motor.Parameters(config));
            Predictor.CheckFrameAssumptions(timeline.Time, timeline.Latest.Behavior, timeline.Held, tuning);
        }

        // 在发布给动画之前校验输入和配置，监听者重置或接管角色后放弃旧结果。
        internal CharacterMovementForecast ReadForAnimation(Behaviour owner, in CharacterRuntimeConfiguration config)
        {
            int version = board.Simulation.LifecycleVersion;
            if (Predictor.HasFrameAssumptions) CheckAssumptions(config, config.ReadMovement());
            if (version != board.Simulation.LifecycleVersion || !board.Simulation.Running ||
                !board.Simulation.CanSimulate(owner, motor)) return null;
            return Predictor.PublishedForAnimation(config.Forecast);
        }

        // 使用最近已解物理起点和最新已发布行为，提交帧末预测。
        internal void Submit(in CharacterRuntimeConfiguration config)
        {
            var drive = motor.Parameters(config);
            var movement = config.ReadMovement();
            var current = new CharacterForecastObservation(board.Simulation.LifecycleVersion,
                timeline.Time, Origin, timeline.Latest.Behavior, timeline.Held, Time.fixedDeltaTime, config, movement, drive);
            Predictor.SubmitObservation(current, board.Rigidbody, movement, drive, flow, config.UseWorkerThreads);
        }
    }
}
