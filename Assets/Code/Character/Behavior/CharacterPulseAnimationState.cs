using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 将普通瞬发许可与可选的位移时钟转换为单次请求。
    internal sealed class CharacterPulseAnimationState
    {
        private readonly CharacterBehaviorAnimationMapping rule;
        private readonly CharacterBehaviorAnimationBindings owner;
        private CharacterActionPlayback receipt;

        // 保存瞬发动作所属的游戏规则。
        internal CharacterPulseAnimationState(CharacterBehaviorAnimationMapping rule, CharacterBehaviorAnimationBindings owner)
        {
            this.rule = rule;
            this.owner = owner;
        }

        // 进入时冻结素材相位和时长，行为中断时只撤销所属实例。
        internal void Observe(in CharacterBehaviorObservation frame, bool active, bool began, bool seed)
        {
            var step = frame.Behavior;
            var id = rule.BehaviorId;
            bool timed = step.DisplacementProvider == id && step.ActionMotion.Duration > 0f && step.ActionMotion.StartPhase.HasValue;
            bool configured = rule.Handoff?.OverridePhases == true && rule.Handoff.Valid;
            if (((step.InterruptedBehaviors | step.WithdrawnBehaviors) & id) != 0 || began) Clear(frame.Time);
            if (began || seed && active)
            {
                float? total = timed ? step.ActionMotion.Duration : configured ? rule.Handoff.Duration : (float?)null;
                float? phase = timed ? step.ActionMotion.StartPhase : seed && total.HasValue
                    ? Mathf.Clamp01(step.GrantAgeAtStepStart(id) / total.Value) : (float?)null;
                float entryPhase = phase.GetValueOrDefault();
                double start = seed ? frame.Time : step.StepStartTime;
                var request = new CharacterOneShotRequest(rule.EntryAction, rule.Weight, phase, total * (1f - entryPhase),
                    configured ? total * Mathf.Max(0f, rule.Handoff.FullFrom - entryPhase) : null);
                receipt = owner.Play(request, start, frame, rule, seed ? CharacterPlaybackStartSource.Rebind : CharacterPlaybackStartSource.BehaviorGrant);
                if (receipt != null && configured && total.HasValue)
                {
                    double fade = start + total.Value * Mathf.Max(0f, rule.Handoff.ReleaseFrom - entryPhase);
                    owner.Host.FadeOut(receipt, fade, total.Value * (1f - Mathf.Max(rule.Handoff.ReleaseFrom, entryPhase)));
                }
            }
            if (!active && (step.EndedBehaviors & id) != 0 && (timed || configured))
                Clear(frame.Time);
        }

        // 撤回仍在有效窗口内的瞬发请求。
        internal void Clear(double at)
        {
            if (receipt != null) owner.Host.Cancel(receipt, at);
            receipt = null;
        }
    }
}
