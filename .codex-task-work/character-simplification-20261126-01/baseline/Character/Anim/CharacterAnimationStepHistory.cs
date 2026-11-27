using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    // 保存未消费的物理步，为当前动画批次按提交顺序更新运动意图和步态。
    internal sealed class CharacterAnimationStepHistory
    {
        private readonly CharacterAnimationModules modules;
        private readonly List<(CharacterControllerFrameResult frame, CharacterAnimationMotion motion, bool reset, double time, bool preserve)> steps =
            new List<(CharacterControllerFrameResult, CharacterAnimationMotion, bool, double, bool)>();
        internal int Count => steps.Count;

        // 将步态消费绑定到可以独立重配的角色动画模块集合。
        internal CharacterAnimationStepHistory(CharacterAnimationModules modules) => this.modules = modules;

        // 登记本次已经求解的物理步，或只有轨迹不连续标记的时间点。
        internal void Add(CharacterControllerFrameResult frame, CharacterAnimationMotion motion, bool reset, double time)
            => steps.Add((frame, motion, reset, time, false));

        // 丢弃旧连续性的全部待消费步。
        internal void Clear() => steps.Clear();

        // 按顺序消化本次采集覆盖的固定步，后到的提交留给下一次采集。
        internal void Consume(int count, ref CharacterPresentationWork batch, bool deferGait = false)
        {
            for (int i = 0; i < count; i++)
            {
                var step = steps[i];
                if (step.reset) modules.Matcher.Reset();
                if (step.frame.FrameNumber != 0)
                {
                    modules.Intent.ObserveActionVariantMotion(step.motion);
                    step.preserve = modules.Bindings.PreservesEntryGait(step.frame);
                }
                if (deferGait) steps[i] = step;
                else ReceiveIntent(step, false, batch);
            }
            if (deferGait) batch.DeferGait(count);
            else steps.RemoveRange(0, count);
        }

        // 用与本步绑定结果对应的步态许可推进意图。
        private void ReceiveIntent(in (CharacterControllerFrameResult frame, CharacterAnimationMotion motion,
            bool reset, double time, bool preserve) step, bool projectGait, in CharacterPresentationWork batch)
        {
            if (step.reset) modules.Intent.InvalidateMotion();
            if (step.frame.FrameNumber == 0) return;
            var motion = projectGait && modules.Intent.MayUsePredictedGait(step.motion, step.frame.Behavior)
                ? batch.Input.ProjectStep(step.frame, step.motion, step.time) : step.motion;
            modules.Intent.Receive(motion, step.frame.Behavior, step.frame.Input, step.frame.DeltaTime, step.preserve);
        }

        // 只补交已处理绑定的固定步意图，绑定和许可仍然按原计划发布一次。
        internal void FinishDeferredGait(bool projectGait, ref CharacterPresentationWork batch)
        {
            for (int i = 0; i < batch.DeferredSteps; i++) ReceiveIntent(steps[i], projectGait, batch);
            steps.RemoveRange(0, batch.DeferredSteps);
            batch.FinishGait();
        }

        // 找出需要先核对发布预测、再更新持久步态的固定步批次。
        internal bool HasForecastSensitiveSteps()
        {
            foreach (var step in steps)
                if (modules.Intent.MayUsePredictedGait(step.motion, step.frame.Behavior)) return true;
            return false;
        }
    }
}
