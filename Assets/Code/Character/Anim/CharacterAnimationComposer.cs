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
        private readonly CharacterPlaybackArbitration ownership = new CharacterPlaybackArbitration();
        private double? evaluatingAt;
        private CharacterAnimationComposition previousSelection;

        private readonly List<CharacterActionPlayback> receipts = new List<CharacterActionPlayback>();
        private bool planning;
        private double presentationTime;
        private readonly List<CharacterActionPlayback> retiring = new List<CharacterActionPlayback>();
        internal IReadOnlyList<CharacterActionPlayback> Retiring => retiring;

        internal double Time => presentationTime;

        // 更新规划与显式请求共用的角色时刻。
        internal void SetTime(double now) => presentationTime = evaluatingAt ?? now;

        // 动画事件中的请求沿用当前原生求值区间的截止时刻。
        internal void BeginSegment(double at) { evaluatingAt = at; presentationTime = at; }

        // 回到本次提交的角色帧末，各请求仍保留各自生效时刻。
        internal void EndSegment(double now) { evaluatingAt = null; presentationTime = now; }

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
            CharacterPlaybackClock clock = default, int clipId = 0)
        {
            long id = NextInstance(key);
            if (id == 0) return null;
            var playback = new CharacterActionPlayback(key, id, origin, source, clock, clipId);
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
                var at = playback != null && playback.Clock.Valid ? playback.Clock.Start : presentationTime;
                EndAt(key, latest, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ArbitrationLost, at);
            }

            latestInstances[key] = request.InstanceId;
            var demand = CreateDemand(request, rule, ++submissionOrder, playback);
            int index = rankedKeys.Count;
            while (index > 0 && demand.Precedes(active[rankedKeys[index - 1]])) index--;
            rankedKeys.Insert(index, key);
            active[key] = demand;
            ownership.Add(demand, playback != null && playback.Clock.Valid ? playback.Clock.Start : presentationTime);
            return true;
        }

        // 保留已下发的素材入点，提交普通动作本帧的权重和素材采样相位。
        public void Sample(CharacterActionPlayback playback, float? firstPhase, float phase, CharacterActionHandoff handoff,
            bool startAtZero, bool resumed)
        {
            if (!active.TryGetValue(playback.Key, out var demand) || demand.Playback != playback) return;
            var start = playback.HasStarted ? demand.Timing.StartClipPhase : firstPhase;
            active[playback.Key] = demand.WithTiming(new CharacterActionTiming(start, phase, handoff, startAtZero, resumed));
        }

        // 为普通外部定相位动作读取本帧已提交的目标，核对句柄以免跨实例插值。
        internal CharacterActionTiming CurrentTiming(CharacterActionPlayback playback) =>
            active.TryGetValue(playback.Key, out var demand) && demand.Playback == playback ? demand.Timing : default;

        // 按语义键撤销调用者指定的实例。
        public bool Withdraw(string key, long instanceId) =>
            End(key, instanceId, CharacterActionPlaybackStatus.Withdrawn, CharacterPlaybackEndSource.ExplicitWithdrawal);

        public bool Withdraw(CharacterActionPlayback playback) => playback != null &&
            active.TryGetValue(playback.Key, out var demand) && demand.Playback == playback &&
            Withdraw(playback.Key, playback.InstanceId);

        // 先从仲裁集合移出指定实例，再暂存或提交其唯一终态。
        public bool End(string key, long instanceId, CharacterActionPlaybackStatus status, CharacterPlaybackEndSource source) =>
            EndAt(key, instanceId, status, source, presentationTime);

        // 将已生效的撤销与仲裁移除分开，已开始的计时实例仍须实际求值最后合法区间。
        private bool EndAt(string key, long instanceId, CharacterActionPlaybackStatus status, CharacterPlaybackEndSource source, double at)
        {
            if (!active.TryGetValue(key, out var demand) || demand.InstanceId != instanceId) return false;
            rankedKeys.Remove(key);
            active.Remove(key);
            ownership.Close(demand, at);
            var playback = demand.Playback;
            if (playback != null && playback.Clock.Valid && status != CharacterActionPlaybackStatus.Completed)
            {
                playback.Cancel(at, status, source);
                retiring.Add(playback);
                return true;
            }
            if (playback != null && playback.QueueEnd(status, source))
            {
                if (planning) receipts.Add(playback);
                else PublishEnd(playback);
            }
            return true;
        }

        // 在主线程实际提交前摘下到期实例；未获选者撤回，已获选者进入尾段结算。
        internal void RetireDue(double now, CharacterAnimationRequest behavior)
        {
            presentationTime = now;
            var baseline = Resolve(behavior);
            RetireDisplaced(previousSelection.UpperOwner, baseline, now);
            if (previousSelection.LowerOwner.Playback != previousSelection.UpperOwner.Playback)
                RetireDisplaced(previousSelection.LowerOwner, baseline, now);
            for (int i = rankedKeys.Count - 1; i >= 0; i--)
            {
                var demand = active[rankedKeys[i]];
                var playback = demand.Playback;
                if (playback == null || !playback.Clock.Valid || playback.Clock.End > now) continue;
                rankedKeys.RemoveAt(i);
                active.Remove(demand.Key);
                ownership.Close(demand, playback.Clock.End);
                retiring.Add(playback);
            }
        }

        // 核对实际素材结果后发布终态，无法再次取得素材的实例按资源失效退出。
        internal void FinishRetired(CharacterActionPlayback playback, bool available)
        {
            if (!retiring.Remove(playback)) return;
            bool finished = playback.HasStarted && playback.Clock.NaturalEnd <= playback.Clock.StopAt &&
                playback.Cursor?.normalizedTime >= 1f;
            var status = finished ? CharacterActionPlaybackStatus.Completed : !playback.HasStarted ? CharacterActionPlaybackStatus.Withdrawn :
                !available || playback.StopSource == default ? CharacterActionPlaybackStatus.Interrupted : playback.StopStatus;
            var reason = finished ? CharacterPlaybackEndSource.ClipFinished : !available ? CharacterPlaybackEndSource.ResourceUnavailable :
                playback.StopSource == default ? CharacterPlaybackEndSource.BehaviorWindowEnded : playback.StopSource;
            if (playback.QueueEnd(status, reason)) PublishEnd(playback);
        }

        // 同素材图重建保留逻辑句柄；映射消失或真正换片段时终结旧请求。
        internal void RebindPlayback(CharacterAnimationResourceCatalog resources)
        {
            RetireChangedClips(resources);
        }

        // 已激活的请求保留冻结参数，只有实际绑定到不同素材时才撤回。
        internal void RetireChangedClips(CharacterAnimationResourceCatalog resources)
        {
            for (int i = rankedKeys.Count - 1; i >= 0; i--)
            {
                var demand = active[rankedKeys[i]];
                var playback = demand.Playback;
                if (playback == null) continue;
                if (playback.ClipId != 0 && resources.OneShotClipId(demand.Key, playback.Clock.Valid) == playback.ClipId) continue;
                End(demand.Key, demand.InstanceId, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ResourceUnavailable);
            }
        }

        // 以绝对角色时刻对尚在窗口内的请求做纯值试选；再次选择不推进时钟。
        public CharacterAnimationComposition Preview(CharacterAnimationRequest behavior)
        {
            var baseline = Resolve(behavior);
            var claims = new CharacterBodyClaims();
            foreach (string key in rankedKeys)
            {
                if (!hasBinding(key)) continue;
                var demand = active[key];
                if (demand.Playback != null && demand.Playback.Clock.Valid)
                {
                    var clock = demand.Playback.Clock;
                    if (presentationTime < clock.Start || presentationTime >= clock.End) continue;
                    demand = demand.WithTiming(new CharacterActionTiming(clock.Sample(presentationTime), demand.Playback.Cursor, demand.Playback.InitialPhase));
                }
                if (claims.TryClaim(demand, baseline) && baseline.Key == CharacterAnimationIntentConverter.DashKey)
                    baseline = Resolve(new CharacterAnimationRequest(CharacterAnimationIntentConverter.IdleKey, baseline.InstanceId));
            }
            return claims.Compose(baseline);
        }

        // 以观察到的角色时刻登记基础动作的优先级切换。
        internal void ChangeBaseline(CharacterAnimationRequest behavior, double at) => ownership.ChangeBaseline(Resolve(behavior), at);

        // 在摘下到期请求之前重放真实部位冲突，已经离开活动集合的句柄也可缩短尾段。
        private void RetireDisplaced(in CharacterAnimationDemand previous, in CharacterAnimationDemand baseline, double now)
        {
            var handle = previous.Playback;
            if (handle == null || !handle.Clock.Valid || !handle.HasStarted || handle.EndSource != default) return;
            var idle = Resolve(new CharacterAnimationRequest(CharacterAnimationIntentConverter.IdleKey, baseline.InstanceId));
            var at = ownership.FirstLoss(previous, previousSelection.FullBody, baseline, idle, now, hasBinding);
            if (!at.HasValue) return;
            if (!EndAt(previous.Key, previous.InstanceId, CharacterActionPlaybackStatus.Interrupted,
                CharacterPlaybackEndSource.ArbitrationLost, at.Value))
                handle.Cancel(at.Value, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ArbitrationLost);
        }

        // 提交最终获选组合，并按普通动作原策略结束旧许可。
        public void Commit(in CharacterAnimationComposition selected)
        {
            // 只比较已提交的选择，忽略预览时的短暂压制。
            CancelInterrupted(previousSelection.FullBody, selected.FullBody);
            CancelInterrupted(previousSelection.Overlay, selected.Overlay);
            CancelInterrupted(previousSelection.UpperBody, selected.UpperBody);
            CancelInterrupted(previousSelection.LowerBody, selected.LowerBody);
            previousSelection = selected;
            ownership.Prune();
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
            while (retiring.Count > 0)
            {
                var old = retiring[retiring.Count - 1];
                retiring.RemoveAt(retiring.Count - 1);
                if (old.QueueEnd(old.HasStarted ? CharacterActionPlaybackStatus.Interrupted : CharacterActionPlaybackStatus.Withdrawn, source))
                    PublishEnd(old);
            }
            previousSelection = default;
            ownership.Clear();
        }
    }
}
