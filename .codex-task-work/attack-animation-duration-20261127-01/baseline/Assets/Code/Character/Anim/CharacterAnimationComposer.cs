using System;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    /// <summary>一次预览中的两个部位持有者。全身实例在两项检查全部通过后同时占用两处。</summary>
    internal struct CharacterBodyClaims
    {
        private CharacterAnimationDemand upper, lower;

        // 在优先级和搭档规则均允许时，一次提交需求申请的所有部位。
        public bool TryClaim(in CharacterAnimationDemand demand, in CharacterAnimationDemand baseline)
        {
            bool needsUpper = demand.Source != CharacterAnimationSource.LowerBody;
            bool needsLower = demand.Source != CharacterAnimationSource.UpperBody;
            if (needsUpper && upper.IsValid || needsLower && lower.IsValid) return false;
            if (baseline.IsValid && (demand.Priority < baseline.Priority ||
                baseline.Key == CharacterAnimationIntentConverter.DashKey && !demand.Precedes(baseline)))
                return false;
            var partner = needsUpper ? lower : upper;
            if (partner.IsValid && (!partner.Rule.Partners.Allows(demand.Key) || !demand.Rule.Partners.Allows(partner.Key)))
                return false;
            if (needsUpper) upper = demand;
            if (needsLower) lower = demand;
            return true;
        }

        // 汇总本次仲裁的基础动作和两个部位持有者。
        public CharacterAnimationComposition Compose(in CharacterAnimationDemand baseline) =>
            new CharacterAnimationComposition(baseline, upper, lower);
    }

    /// <summary>仲裁结果只保留上下身持有者。空闲区域从持续更新的默认姿势取样。</summary>
    internal readonly struct CharacterAnimationComposition
    {
        private readonly CharacterAnimationDemand baseline;
        public readonly CharacterAnimationDemand UpperOwner, LowerOwner;
        public CharacterAnimationDemand FullBody => baseline;
        public CharacterAnimationDemand Overlay => UpperOwner.Source == CharacterAnimationSource.FullBodyOverlay ? UpperOwner : default;
        public CharacterAnimationDemand UpperBody => UpperOwner.Source == CharacterAnimationSource.UpperBody ? UpperOwner : default;
        public CharacterAnimationDemand LowerBody => LowerOwner.Source == CharacterAnimationSource.LowerBody ? LowerOwner : default;
        // 保存基础动作和已获准占用两个部位的动作。
        public CharacterAnimationComposition(CharacterAnimationDemand baseline, CharacterAnimationDemand upperOwner,
            CharacterAnimationDemand lowerOwner)
        {
            this.baseline = baseline;
            UpperOwner = upperOwner;
            LowerOwner = lowerOwner;
        }

        // 保留仲裁归属，使用播放计划确定的生效权重。
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
            return new CharacterAnimationComposition(baseline, upper, lower);
        }
    }

    /// <summary>仅依据语义规则维护需求、优先级、实例去重及部位组合，不解析或持有动画资源。</summary>
    internal sealed class CharacterAnimationComposer
    {
        private IReadOnlyDictionary<string, CharacterAnimationRule> rules;
        private Predicate<string> hasBinding;
        private readonly Dictionary<string, CharacterAnimationDemand> active =
            new Dictionary<string, CharacterAnimationDemand>();
        private readonly Dictionary<string, long> latestInstances = new Dictionary<string, long>(); // 每个语义键的最大实例号。
        private readonly List<string> rankedKeys = new List<string>(); // 仅在动作实例新增或退出时更新顺序。
        private long submissionOrder;
        private CharacterAnimationComposition previousSelection;

        private readonly List<CharacterActionPlayback> receipts = new List<CharacterActionPlayback>();
        private bool planning;

        internal void BeginPlan() => planning = true;
        internal void FinishPlan() => planning = false;

#if UNITY_EDITOR
        internal CharacterActionTrace Trace { get; set; }
#endif

        // 在真实下发之后记录开始，忽略重复下发和已失效的句柄。
        internal void Start(CharacterActionPlayback playback)
        {
            if (playback?.Start() != true) return;
#if UNITY_EDITOR
            Trace?.Record(playback, CharacterActionTraceKind.PlaybackStarted);
#endif
        }

        // 播放提交或批次撤回时，按规划顺序发布暂存终态。
        internal void PublishReceipts(CharacterPlaybackEndSource discarded = CharacterPlaybackEndSource.None)
        {
            planning = false;
            foreach (var playback in receipts) PublishEnd(playback, discarded);
            receipts.Clear();
        }

        // 冻结终态后向选中角色的历史写入一次结果。
        private void PublishEnd(CharacterActionPlayback playback, CharacterPlaybackEndSource discarded = CharacterPlaybackEndSource.None)
        {
            if (!playback.PublishEnd(discarded)) return;
#if UNITY_EDITOR
            Trace?.Record(playback, CharacterActionTraceKind.PlaybackEnded);
#endif
        }

        public CharacterAnimationComposer(IReadOnlyDictionary<string, CharacterAnimationRule> rules, Predicate<string> hasBinding)
        {
            this.rules = rules;
            this.hasBinding = hasBinding;
        }

        internal void RebindRules(CharacterAnimationResourceCatalog resources)
        {
            rules = resources.Rules;
            hasBinding = resources.HasBinding;
        }

        // 移除装配变更前仍占用该键的请求。
        internal void WithdrawKey(string key)
        {
            if (active.TryGetValue(key, out var demand))
                End(key, demand.InstanceId, CharacterActionPlaybackStatus.Withdrawn, CharacterPlaybackEndSource.ModuleRemoved);
        }

        public IEnumerable<string> ActiveKeys => active.Keys;
        public bool HasActive(string key) => key != null && active.ContainsKey(key);

        public long NextInstance(string key)
        {
            latestInstances.TryGetValue(key, out long latest);
            return latest < long.MaxValue ? latest + 1 : 0;
        }

        // 关联当前执行并申请一个新的单次播放，续播始终产生独立句柄。
        public CharacterActionPlayback PlayOnce(string key, float weight, CharacterPlaybackOrigin origin, CharacterPlaybackStartSource source,
            CharacterActionHandoff explicitTiming = default)
        {
            long id = NextInstance(key);
            if (id == 0) return null;
            var playback = new CharacterActionPlayback(key, id, origin, source, explicitTiming);
            if (!Submit(new CharacterAnimationRequest(key, id, weight), playback)) return null;
#if UNITY_EDITOR
            if (source == CharacterPlaybackStartSource.Rebind || source == CharacterPlaybackStartSource.ArbitrationRecovery)
                Trace?.Record(playback, CharacterActionTraceKind.PlaybackResumed);
#endif
            return playback;
        }

        public bool Submit(CharacterAnimationRequest request) => Submit(request, null);

        // 更新相同实例的需求，新实例抢占同键旧实例。
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
                End(key, latest, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ArbitrationLost);
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

        // 按语义键撤销调用者指定的实例。
        public bool Withdraw(string key, long instanceId) =>
            End(key, instanceId, CharacterActionPlaybackStatus.Withdrawn, CharacterPlaybackEndSource.ExplicitWithdrawal);

        public bool Withdraw(CharacterActionPlayback playback) => playback != null &&
            active.TryGetValue(playback.Key, out var demand) && demand.Playback == playback &&
            Withdraw(playback.Key, playback.InstanceId);

        // 先从仲裁集合移出指定实例，再暂存或提交其唯一终态。
        public bool End(string key, long instanceId, CharacterActionPlaybackStatus status, CharacterPlaybackEndSource source)
        {
            if (!active.TryGetValue(key, out var demand) || demand.InstanceId != instanceId) return false;
            rankedKeys.Remove(key);
            active.Remove(key);
            var playback = demand.Playback;
            if (playback != null && playback.QueueEnd(status, source))
            {
                if (planning) receipts.Add(playback);
                else PublishEnd(playback);
            }
            return true;
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

        // 在最终选择发生变化时结束失去部位的旧实例。
        private void CancelInterrupted(CharacterAnimationDemand previous, CharacterAnimationDemand selected)
        {
            if (!previous.IsValid || previous.Playback == null && previous.InterruptionPolicy != CharacterAnimationInterruptionPolicy.Cancel ||
                previous.Key == selected.Key && previous.InstanceId == selected.InstanceId)
                return;
            if (End(previous.Key, previous.InstanceId, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ArbitrationLost) && previous.Playback != null)
                previous.Playback.WasPreempted = true;
        }

        private CharacterAnimationDemand Resolve(CharacterAnimationRequest request)
        {
            return request.SemanticKey != null && rules.TryGetValue(request.SemanticKey, out var rule)
                ? CreateDemand(request, rule, 0) : default;
        }

        // 将请求转换为用于仲裁的需求，基础姿势始终以完整权重播放。
        private static CharacterAnimationDemand CreateDemand(CharacterAnimationRequest request,
            CharacterAnimationRule rule, long order, CharacterActionPlayback playback = null)
        {
            float weight = rule.Source == CharacterAnimationSource.Baseline ? 1f : ClampUnit(request.Weight);
            float? start = request.StartNormalizedTime.HasValue ? ClampUnit(request.StartNormalizedTime.Value) : (float?)null;
            return new CharacterAnimationDemand(rule, request, weight, start, order, playback);
        }

        private static float ClampUnit(float value) => Math.Min(1f, InputMath.NonNegative(value, 0f));

        // 播放资源重绑时重建选中历史，保留持续请求和实例编号。
        public void ResetSelection(CharacterPlaybackEndSource source)
        {
            for (int i = rankedKeys.Count - 1; i >= 0; i--)
            {
                var demand = active[rankedKeys[i]];
                if (demand.Playback != null) End(demand.Key, demand.InstanceId, CharacterActionPlaybackStatus.Interrupted, source);
            }
            previousSelection = default;
        }

        // 结束本角色当前的全部请求，回执在角色重置后仍然可查。
        public void Reset(CharacterPlaybackEndSource source, bool forgetInstances = true)
        {
            PublishReceipts(source);
            for (int i = rankedKeys.Count - 1; i >= 0; i--)
            {
                var demand = active[rankedKeys[i]];
                End(demand.Key, demand.InstanceId, CharacterActionPlaybackStatus.Withdrawn, source);
            }
            if (forgetInstances) latestInstances.Clear();
            submissionOrder = 0;
            previousSelection = default;
        }
    }
}
