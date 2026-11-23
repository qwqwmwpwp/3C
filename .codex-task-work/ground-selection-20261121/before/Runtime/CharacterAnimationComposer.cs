using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>语义仲裁的完整输出：一个全身基底和最多两个局部覆盖。</summary>
    internal readonly struct CharacterAnimationComposition
    {
        public CharacterAnimationDemand FullBody { get; }
        public CharacterAnimationDemand UpperBody { get; }
        public CharacterAnimationDemand LowerBody { get; }
        /// <summary>胸、左臂、右臂各自允许的惯性。只合并最终获选需求，局部层按请求权重参与。</summary>
        public Vector3 InertiaAllowance => Vector3.one - Vector3.Max(FullBody.WeightedInertiaSuppression,
            Vector3.Max(UpperBody.WeightedInertiaSuppression, LowerBody.WeightedInertiaSuppression));

        public CharacterAnimationComposition(CharacterAnimationDemand fullBody,
            CharacterAnimationDemand upperBody, CharacterAnimationDemand lowerBody)
        {
            FullBody = fullBody;
            UpperBody = upperBody;
            LowerBody = lowerBody;
        }
    }

    /// <summary>仅依据语义规则维护需求、优先级、实例去重及部位组合，不解析或持有动画资源。</summary>
    internal sealed class CharacterAnimationComposer
    {
        private readonly Dictionary<string, CharacterAnimationRule> rules =
            new Dictionary<string, CharacterAnimationRule>();
        private readonly Dictionary<string, CharacterAnimationDemand> active =
            new Dictionary<string, CharacterAnimationDemand>();
        private readonly Dictionary<string, long> latestInstances = new Dictionary<string, long>(); // 每个语义键的最大实例号。
        private long submissionOrder;
        private CharacterAnimationComposition previousSelection;

        public CharacterAnimationComposer(IEnumerable<CharacterAnimationRule> actionRules)
        {
            foreach (CharacterAnimationRule rule in actionRules)
                rules.Add(rule.Key, rule);
        }

        public IEnumerable<string> ActiveKeys => active.Keys;

        public bool Submit(CharacterAnimationRequest request)
        {
            if (!TryResolve(request, out CharacterAnimationDemand demand))
                return false;
            long order;
            if (latestInstances.TryGetValue(demand.Key, out long latest))
            {
                if (demand.InstanceId < latest)
                    return false;
                if (demand.InstanceId == latest)
                {
                    if (!active.TryGetValue(demand.Key, out CharacterAnimationDemand current))
                        return false;
                    order = current.SubmissionOrder;
                }
                else
                    order = ++submissionOrder;
            }
            else
                order = ++submissionOrder;

            latestInstances[demand.Key] = demand.InstanceId;
            active[demand.Key] = CreateDemand(request, rules[demand.Key], order);
            return true;
        }

        public bool Withdraw(string key, long instanceId)
        {
            return active.TryGetValue(key, out CharacterAnimationDemand demand) &&
                demand.InstanceId == instanceId && active.Remove(key);
        }

        public CharacterAnimationComposition Preview(CharacterAnimationRequest behavior)
        {
            TryResolve(behavior, out CharacterAnimationDemand full);
            CharacterAnimationDemand upper = default;
            CharacterAnimationDemand lower = default;
            foreach (CharacterAnimationDemand demand in active.Values)
            {
                switch (demand.Region)
                {
                    case CharacterAnimationBodyRegion.FullBody:
                        if (Wins(demand, full))
                            full = demand;
                        break;
                    case CharacterAnimationBodyRegion.UpperBody:
                        if (Wins(demand, upper))
                            upper = demand;
                        break;
                    case CharacterAnimationBodyRegion.LowerBody:
                        if (Wins(demand, lower))
                            lower = demand;
                        break;
                }
            }
            // 预览时排除被全身动作压制的局部候选。
            if (upper.IsValid && upper.Priority <= full.Priority)
                upper = default;
            if (lower.IsValid && lower.Priority <= full.Priority)
                lower = default;
            return new CharacterAnimationComposition(full, upper, lower);
        }

        public void Commit(in CharacterAnimationComposition selected)
        {
            // 只比较已提交的选择，忽略预览时的短暂压制。
            CancelInterrupted(previousSelection.FullBody, selected.FullBody);
            CancelInterrupted(previousSelection.UpperBody, selected.UpperBody);
            CancelInterrupted(previousSelection.LowerBody, selected.LowerBody);
            previousSelection = selected;
        }

        private void CancelInterrupted(CharacterAnimationDemand previous, CharacterAnimationDemand selected)
        {
            if (!previous.IsValid || previous.InterruptionPolicy != CharacterAnimationInterruptionPolicy.Cancel ||
                previous.Key == selected.Key && previous.InstanceId == selected.InstanceId)
                return;
            Withdraw(previous.Key, previous.InstanceId); // 撤销保留最大编号，过期请求无法恢复该实例。
        }

        private bool TryResolve(CharacterAnimationRequest request, out CharacterAnimationDemand demand)
        {
            demand = default;
            if (request.SemanticKey == null || !rules.TryGetValue(request.SemanticKey, out CharacterAnimationRule rule))
                return false;
            demand = CreateDemand(request, rule, 0);
            return true;
        }

        private static CharacterAnimationDemand CreateDemand(CharacterAnimationRequest request,
            CharacterAnimationRule rule, long order)
        {
            float weight = rule.Region == CharacterAnimationBodyRegion.FullBody ? 1f : ClampUnit(request.Weight);
            float? start = request.StartNormalizedTime.HasValue ? ClampUnit(request.StartNormalizedTime.Value) : (float?)null;
            return new CharacterAnimationDemand(rule, request, weight, start, order);
        }

        private static float ClampUnit(float value) => Math.Min(1f, InputMath.NonNegative(value, 0f));

        // 更换控制器时重建选中历史，保留持续需求和实例编号。
        public void ResetSelection() => previousSelection = default;

        private static bool Wins(CharacterAnimationDemand candidate, CharacterAnimationDemand current)
        {
            return !current.IsValid || candidate.Priority > current.Priority ||
                candidate.Priority == current.Priority && candidate.SubmissionOrder > current.SubmissionOrder;
        }

        public void Reset()
        {
            active.Clear();
            latestInstances.Clear();
            submissionOrder = 0;
            previousSelection = default;
        }
    }
}
