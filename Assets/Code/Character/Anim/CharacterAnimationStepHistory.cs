using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    // 按提交顺序保存固定步；已交给策略的前缀不会因同帧重选或撤批而再次推进。
    internal sealed class CharacterAnimationStepHistory
    {
        private readonly CharacterAnimationModules modules;
        private readonly List<(CharacterAnimationMovement frame, CharacterAnimationMotion motion, bool reset, double time,
            CharacterTrajectoryObservations trajectory)> steps =
            new List<(CharacterAnimationMovement, CharacterAnimationMotion, bool, double, CharacterTrajectoryObservations)>();
        internal int Count => steps.Count;

        // 绑定当前角色的策略实例集合。
        internal CharacterAnimationStepHistory(CharacterAnimationModules modules) => this.modules = modules;

        // 记录一个真实运动步及其原始结算时刻。
        internal void Add(CharacterAnimationMovement frame, CharacterAnimationMotion motion, bool reset, double time) =>
            steps.Add((frame, motion, reset, time, default));

        // 丢弃旧代次尚未消费的固定步。
        internal void Clear() => steps.Clear();

        // 在主线程用每个真实固定步自己的时刻预取当前策略声明的轨迹点。
        internal void CaptureTrajectories(AnimationTrajectoryPublication forecast, int count)
        {
            var setup = modules.Strategy.Setup;
            if (!setup.HasTrajectoryRequests) return;
            for (int i = 0; i < count; i++)
            {
                var step = steps[i];
                step.trajectory = setup.CaptureTrajectory(forecast.Snapshot, step.time);
                steps[i] = step;
            }
        }

        // 使用最终轨迹消费固定步；即使策略抛错也不会再次调用同一步。
        internal void Consume(int count, in CharacterPresentationInput captured, bool project = false)
        {
            int consumed = 0;
            var session = modules.Strategy;
            try
            {
                while (consumed < count)
                {
                    var step = steps[consumed++];
                    if (step.reset) session.InvalidateMotion();
                    if (step.frame.FrameNumber == 0) continue;
                    var motion = step.motion;
                    if (project && session.Detail?.NeedsForecast(motion, step.frame) == true)
                        motion = session.Detail.ProjectStep(step.frame, motion, step.time, captured);
                    var trajectory = session.Setup.HasTrajectoryRequests ? step.trajectory :
                        session.Setup.CaptureTrajectory(captured.Forecast, step.time);
                    var facts = new CharacterBasePoseInput(step.frame, step.frame.Context, motion, trajectory,
                        step.time, step.frame.DeltaTime, captured.Observation, default, captured.Bones);
                    session.Instance.AdvanceStep(facts);
                }
            }
            finally { steps.RemoveRange(0, consumed); }
        }

        // 对需要稳定预测的地面批次，延后到最终发布时再消费固定步。
        internal bool HasForecastSensitiveSteps()
        {
            var detail = modules.Strategy.Detail;
            if (detail == null) return false;
            foreach (var step in steps)
                if (detail.NeedsForecast(step.motion, step.frame)) return true;
            return false;
        }
    }
}
