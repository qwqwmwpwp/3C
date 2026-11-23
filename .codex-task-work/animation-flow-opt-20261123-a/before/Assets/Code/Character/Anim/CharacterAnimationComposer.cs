using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal readonly struct CharacterBodyConflict
    {
        public readonly string Rejected, Holder, Reason;

        public CharacterBodyConflict(string rejected, string holder, string reason)
        {
            Rejected = rejected;
            Holder = holder;
            Reason = reason;
        }
    }

    /// <summary>一次预览中的两个部位持有者。全身实例在两项检查全部通过后同时占用两处。</summary>
    internal struct CharacterBodyClaims
    {
        private CharacterAnimationDemand upper, lower;
        private CharacterBodyConflict conflict;

        public bool TryClaim(in CharacterAnimationDemand demand, in CharacterAnimationDemand baseline)
        {
            bool needsUpper = demand.Region != CharacterAnimationBodyRegion.LowerBody;
            bool needsLower = demand.Region != CharacterAnimationBodyRegion.UpperBody;
            if (needsUpper && upper.IsValid) return Reject(demand, upper, "上身已被占用");
            if (needsLower && lower.IsValid) return Reject(demand, lower, "下身已被占用");
            if (baseline.IsValid && (demand.Priority < baseline.Priority ||
                baseline.Key == CharacterAnimationIntentConverter.DashKey && !demand.Precedes(baseline)))
                return Reject(demand, baseline, "基础动作优先");
            var partner = needsUpper ? lower : upper;
            if (partner.IsValid && (!partner.Rule.Partners.Allows(demand.Key) || !demand.Rule.Partners.Allows(partner.Key)))
                return Reject(demand, partner, "上下身动作不兼容");
            if (needsUpper) upper = demand;
            if (needsLower) lower = demand;
            return true;
        }

        private bool Reject(in CharacterAnimationDemand demand, in CharacterAnimationDemand holder, string reason)
        {
            if (conflict.Rejected == null) conflict = new CharacterBodyConflict(demand.Key, holder.Key, reason);
            return false;
        }

        public CharacterAnimationComposition Compose(in CharacterAnimationDemand baseline) =>
            new CharacterAnimationComposition(baseline, upper, lower, conflict);
    }

    /// <summary>仲裁结果只保留上下身持有者。空闲区域从持续更新的默认姿势取样。</summary>
    internal readonly struct CharacterAnimationComposition
    {
        private readonly CharacterAnimationDemand baseline;
        public readonly CharacterAnimationDemand UpperOwner, LowerOwner;
        public CharacterAnimationDemand FullBody => UpperOwner.IsValid && UpperOwner.Region == CharacterAnimationBodyRegion.FullBody ? UpperOwner : baseline;
        public CharacterAnimationDemand Overlay => UpperOwner.Region == CharacterAnimationBodyRegion.FullBodyOverlay ? UpperOwner : default;
        public CharacterAnimationDemand UpperBody => UpperOwner.Region == CharacterAnimationBodyRegion.UpperBody ? UpperOwner : default;
        public CharacterAnimationDemand LowerBody => LowerOwner.Region == CharacterAnimationBodyRegion.LowerBody ? LowerOwner : default;
        public CharacterBodyConflict Conflict { get; }
        /// <summary>胸、左臂、右臂各自允许的惯性。只合并最终获选需求，局部层按请求权重参与。</summary>
        public Vector3 InertiaAllowance => Vector3.one - Vector3.Max(FullBody.WeightedInertiaSuppression * (1f - Overlay.Weight) +
            Overlay.WeightedInertiaSuppression * Overlay.Coverage,
            Vector3.Max(UpperBody.WeightedInertiaSuppression * UpperBody.Coverage, LowerBody.WeightedInertiaSuppression * LowerBody.Coverage));

        public CharacterAnimationComposition(CharacterAnimationDemand baseline, CharacterAnimationDemand upperOwner,
            CharacterAnimationDemand lowerOwner, CharacterBodyConflict conflict)
        {
            this.baseline = baseline;
            UpperOwner = upperOwner;
            LowerOwner = lowerOwner;
            Conflict = conflict;
        }

        public CharacterAnimationComposition WithPlaybackWeights(in CharacterAnimationPlaybackPlan plan)
        {
            var upper = UpperOwner;
            var lower = LowerOwner;
            if (Overlay.IsValid) upper = lower = plan.Overlay.Demand;
            else
            {
                if (UpperBody.IsValid) upper = plan.UpperBody.Demand;
                if (LowerBody.IsValid) lower = plan.LowerBody.Demand;
            }
            return new CharacterAnimationComposition(baseline, upper, lower, Conflict);
        }
    }

    /// <summary>仅依据语义规则维护需求、优先级、实例去重及部位组合，不解析或持有动画资源。</summary>
    internal sealed class CharacterAnimationComposer
    {
        private readonly IReadOnlyDictionary<string, CharacterAnimationRule> rules;
        private readonly Predicate<string> hasBinding;
        private readonly Dictionary<string, CharacterAnimationDemand> active =
            new Dictionary<string, CharacterAnimationDemand>();
        private readonly Dictionary<string, long> latestInstances = new Dictionary<string, long>(); // 每个语义键的最大实例号。
        private readonly List<string> rankedKeys = new List<string>(); // 仅在动作实例新增或退出时更新顺序。
        private long submissionOrder;
        private CharacterAnimationComposition previousSelection;

        public CharacterAnimationComposer(IReadOnlyDictionary<string, CharacterAnimationRule> rules, Predicate<string> hasBinding)
        {
            this.rules = rules;
            this.hasBinding = hasBinding;
        }

        public IEnumerable<string> ActiveKeys => active.Keys;
        public bool HasActive(string key) => key != null && active.ContainsKey(key);

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
            if (latestInstances.TryGetValue(key, out long latest))
            {
                if (request.InstanceId < latest)
                    return false;
                if (request.InstanceId == latest)
                {
                    if (!active.TryGetValue(key, out var current))
                        return false;
                    active[key] = CreateDemand(request, rule, current.SubmissionOrder, current.Playback).WithTiming(current.Timing);
                    return true;
                }
                End(key, latest, CharacterActionPlaybackStatus.Interrupted);
            }

            latestInstances[key] = request.InstanceId;
            var demand = CreateDemand(request, rule, ++submissionOrder, playback);
            int index = rankedKeys.Count;
            while (index > 0 && demand.Precedes(active[rankedKeys[index - 1]])) index--;
            rankedKeys.Insert(index, key);
            active[key] = demand;
            return true;
        }

        public void Sample(CharacterActionPlayback playback, float? firstPhase, float phase, CharacterActionHandoff handoff,
            bool startAtZero, bool resumed)
        {
            if (!active.TryGetValue(playback.Key, out var demand) || demand.Playback != playback) return;
            var start = playback.HasStarted ? demand.Timing.StartPhase : firstPhase;
            active[playback.Key] = demand.WithTiming(new CharacterActionTiming(start, phase, handoff, startAtZero, resumed));
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
            rankedKeys.Remove(key);
            return active.Remove(key);
        }

        public CharacterAnimationComposition Preview(CharacterAnimationRequest behavior)
        {
            var baseline = Resolve(behavior);
            var claims = new CharacterBodyClaims();
            foreach (string key in rankedKeys)
            {
                if (!hasBinding(key)) continue;
                var demand = active[key];
                if (claims.TryClaim(demand, baseline) && baseline.Key == CharacterAnimationIntentConverter.DashKey)
                    baseline = Resolve(new CharacterAnimationRequest(CharacterAnimationIntentConverter.IdleKey, baseline.InstanceId));
            }
            return claims.Compose(baseline);
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
            if (End(previous.Key, previous.InstanceId, CharacterActionPlaybackStatus.Interrupted) && previous.Playback != null)
                previous.Playback.WasPreempted = true;
        }

        private CharacterAnimationDemand Resolve(CharacterAnimationRequest request)
        {
            return request.SemanticKey != null && rules.TryGetValue(request.SemanticKey, out var rule)
                ? CreateDemand(request, rule, 0) : default;
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
            for (int i = rankedKeys.Count - 1; i >= 0; i--)
            {
                var demand = active[rankedKeys[i]];
                if (demand.Playback != null) End(demand.Key, demand.InstanceId, CharacterActionPlaybackStatus.Interrupted);
            }
            previousSelection = default;
        }

        public void Reset()
        {
            foreach (var demand in active.Values)
                if (demand.Playback != null) demand.Playback.Status = CharacterActionPlaybackStatus.Withdrawn;
            active.Clear();
            rankedKeys.Clear();
            latestInstances.Clear();
            submissionOrder = 0;
            previousSelection = default;
        }
    }
}
