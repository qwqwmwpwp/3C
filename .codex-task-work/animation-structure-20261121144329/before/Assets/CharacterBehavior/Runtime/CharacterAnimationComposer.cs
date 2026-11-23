using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>基础动画、全身叠加和两个局部覆盖的获选需求。</summary>
    internal readonly struct CharacterAnimationComposition
    {
        public CharacterAnimationDemand FullBody { get; }
        public CharacterAnimationDemand Overlay { get; }
        public CharacterAnimationDemand UpperBody { get; }
        public CharacterAnimationDemand LowerBody { get; }
        /// <summary>胸、左臂、右臂各自允许的惯性。只合并最终获选需求，局部层按请求权重参与。</summary>
        public Vector3 InertiaAllowance => Vector3.one - Vector3.Max(FullBody.WeightedInertiaSuppression * (1f - Overlay.Weight) +
            Overlay.WeightedInertiaSuppression * Overlay.Coverage,
            Vector3.Max(UpperBody.WeightedInertiaSuppression * UpperBody.Coverage, LowerBody.WeightedInertiaSuppression * LowerBody.Coverage));

        public CharacterAnimationComposition(CharacterAnimationDemand fullBody,
            CharacterAnimationDemand upperBody, CharacterAnimationDemand lowerBody, CharacterAnimationDemand overlay)
        {
            FullBody = fullBody;
            Overlay = overlay;
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

        public long NextInstance(string key)
        {
            latestInstances.TryGetValue(key, out long latest);
            return latest < long.MaxValue ? latest + 1 : 0;
        }

        public CharacterActionPlayback PlayOnce(string key, float weight)
        {
            long id = NextInstance(key);
            if (id == 0) return null;
            var playback = new CharacterActionPlayback(key, id);
            return Submit(new CharacterAnimationRequest(key, id, weight), playback) ? playback : null;
        }

        public bool Submit(CharacterAnimationRequest request) => Submit(request, null);

        private bool Submit(CharacterAnimationRequest request, CharacterActionPlayback playback)
        {
            string key = request.SemanticKey;
            if (key == null || request.InstanceId <= 0 || !rules.TryGetValue(key, out var rule))
                return false;
            long order;
            if (latestInstances.TryGetValue(key, out long latest))
            {
                if (request.InstanceId < latest)
                    return false;
                if (request.InstanceId == latest)
                {
                    if (!active.TryGetValue(key, out var current))
                        return false;
                    order = current.SubmissionOrder;
                    playback = current.Playback;
                }
                else
                {
                    if (active.TryGetValue(key, out var replaced))
                        End(key, replaced.InstanceId, CharacterActionPlaybackStatus.Interrupted);
                    order = ++submissionOrder;
                }
            }
            else
                order = ++submissionOrder;

            latestInstances[key] = request.InstanceId;
            active[key] = CreateDemand(request, rule, order, playback);
            return true;
        }

        public bool Withdraw(string key, long instanceId) => End(key, instanceId, CharacterActionPlaybackStatus.Withdrawn);

        public bool Withdraw(CharacterActionPlayback playback) => playback != null &&
            active.TryGetValue(playback.Key, out var demand) && demand.Playback == playback &&
            Withdraw(playback.Key, playback.InstanceId);

        public bool End(string key, long instanceId, CharacterActionPlaybackStatus status)
        {
            if (!active.TryGetValue(key, out var demand) || demand.InstanceId != instanceId) return false;
            if (demand.Playback != null)
                demand.Playback.Status = status == CharacterActionPlaybackStatus.Interrupted && !demand.Playback.HasStarted
                    ? CharacterActionPlaybackStatus.Withdrawn : status;
            return active.Remove(key);
        }

        public CharacterAnimationComposition Preview(CharacterAnimationRequest behavior)
        {
            TryResolve(behavior, out CharacterAnimationDemand full);
            CharacterAnimationDemand upper = default;
            CharacterAnimationDemand lower = default;
            CharacterAnimationDemand overlay = default;
            foreach (CharacterAnimationDemand demand in active.Values)
            {
                switch (demand.Region)
                {
                    case CharacterAnimationBodyRegion.FullBodyOverlay:
                        if (Wins(demand, overlay)) overlay = demand;
                        break;
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
            if (overlay.IsValid && !Wins(overlay, full)) overlay = default;
            int priority = overlay.IsValid ? overlay.Priority : full.Priority;
            if (upper.IsValid && upper.Priority <= priority)
                upper = default;
            if (lower.IsValid && lower.Priority <= priority)
                lower = default;
            return new CharacterAnimationComposition(full, upper, lower, overlay);
        }

        public void Commit(in CharacterAnimationComposition selected)
        {
            // 只比较已提交的选择，忽略预览时的短暂压制。
            CancelInterrupted(previousSelection.FullBody, selected.FullBody);
            CancelInterrupted(previousSelection.Overlay, selected.Overlay);
            CancelInterrupted(previousSelection.UpperBody, selected.UpperBody);
            CancelInterrupted(previousSelection.LowerBody, selected.LowerBody);
            previousSelection = selected;
        }

        private void CancelInterrupted(CharacterAnimationDemand previous, CharacterAnimationDemand selected)
        {
            if (!previous.IsValid || previous.Playback == null && previous.InterruptionPolicy != CharacterAnimationInterruptionPolicy.Cancel ||
                previous.Key == selected.Key && previous.InstanceId == selected.InstanceId)
                return;
            End(previous.Key, previous.InstanceId, CharacterActionPlaybackStatus.Interrupted);
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
            CharacterAnimationRule rule, long order, CharacterActionPlayback playback = null)
        {
            float weight = rule.Region == CharacterAnimationBodyRegion.FullBody ? 1f : ClampUnit(request.Weight);
            float? start = request.StartNormalizedTime.HasValue ? ClampUnit(request.StartNormalizedTime.Value) : (float?)null;
            return new CharacterAnimationDemand(rule, request, weight, start, order, playback);
        }

        private static float ClampUnit(float value) => Math.Min(1f, InputMath.NonNegative(value, 0f));

        // 更换控制器时重建选中历史，保留持续需求和实例编号。
        public void ResetSelection()
        {
            List<CharacterActionPlayback> lost = null;
            foreach (var demand in active.Values)
            {
                if (demand.Playback == null) continue;
                lost ??= new List<CharacterActionPlayback>();
                lost.Add(demand.Playback);
            }
            if (lost != null)
                foreach (var playback in lost)
                    End(playback.Key, playback.InstanceId, CharacterActionPlaybackStatus.Interrupted);
            previousSelection = default;
        }

        private static bool Wins(CharacterAnimationDemand candidate, CharacterAnimationDemand current)
        {
            return !current.IsValid || candidate.Priority > current.Priority ||
                candidate.Priority == current.Priority && candidate.SubmissionOrder > current.SubmissionOrder;
        }

        public void Reset()
        {
            foreach (var demand in active.Values)
                if (demand.Playback != null) demand.Playback.Status = CharacterActionPlaybackStatus.Withdrawn;
            active.Clear();
            latestInstances.Clear();
            submissionOrder = 0;
            previousSelection = default;
        }
    }
}
