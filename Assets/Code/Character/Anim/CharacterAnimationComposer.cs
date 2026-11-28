using System;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    /// <summary>仅依据语义规则维护需求、优先级、实例去重及部位组合，不解析或持有动画资源。</summary>
    internal sealed class CharacterAnimationComposer
    {
        private IReadOnlyDictionary<string, CharacterAnimationRule> rules;
        private Predicate<string> hasBinding;
        internal bool UsesStrategyIntents { get; set; }
        private CharacterAnimationRule strategyRule;
        private readonly Dictionary<string, CharacterAnimationDemand> active =
            new Dictionary<string, CharacterAnimationDemand>();
        private readonly Dictionary<string, long> latestInstances; // 宿主生命周期内每个键已分配的最大实例号。
        private readonly CharacterActionSchedule schedule = new CharacterActionSchedule();
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

        internal long PublishedEndVersion { get; private set; } // 仅实际发布新终态时递增。
        internal double Time => presentationTime;
        internal double NextActivation => schedule.Next;
        internal double? EvaluatingAt => evaluatingAt;

        // 更新规划与显式请求共用的角色时刻。
        internal void SetTime(double now) => presentationTime = evaluatingAt ?? now;

        // 动画事件中的请求沿用当前原生求值区间的截止时刻。
        internal void BeginSegment(double at) { evaluatingAt = at; presentationTime = at; }

        // 回到本次提交的角色帧末，各请求仍保留各自生效时刻。
        internal void EndSegment(double now)
        {
            if (!evaluatingAt.HasValue) return; // 求值中的硬重置已经切换绝对时间域。
            evaluatingAt = null;
            presentationTime = now;
        }

        internal void BeginPlan() => planning = true;
        internal void FinishPlan() => planning = false;

#if UNITY_EDITOR
        internal CharacterPlaybackHistory History { get; set; }
#endif

        // 在真实下发之后记录开始，忽略重复下发和已失效的句柄。
        internal void Start(CharacterActionPlayback playback)
        {
            if (playback?.Start() != true) return;
#if UNITY_EDITOR
            History?.Record(playback);
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
            PublishedEndVersion++;
#if UNITY_EDITOR
            History?.Record(playback);
#endif
        }

        // 使用宿主的编号高水位建立当前装配独有的活动集合。
        public CharacterAnimationComposer(IReadOnlyDictionary<string, CharacterAnimationRule> rules, Predicate<string> hasBinding,
            Dictionary<string, long> latestInstances)
        {
            this.latestInstances = latestInstances;
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
            schedule.RemoveWhere(row => row.Key == key, handle => CloseUnavailable(handle, CharacterPlaybackEndSource.ModuleRemoved));
            if (active.TryGetValue(key, out var demand))
                End(key, demand.InstanceId, CharacterActionPlaybackStatus.Withdrawn, CharacterPlaybackEndSource.ModuleRemoved);
        }

        public IEnumerable<string> ActiveKeys => active.Keys;
        public bool HasActive(string key) => key != null && active.ContainsKey(key);

        // 查询一个具体的持续请求是否仍由本角色保留。
        internal bool HasAction(string key, long instanceId) => key != null &&
            active.TryGetValue(key, out var demand) && demand.InstanceId == instanceId;

        // 返回该键下一次可用的实例号；编号耗尽后拒绝新请求。
        public long NextInstance(string key)
        {
            latestInstances.TryGetValue(key, out long latest);
            return latest < long.MaxValue ? latest + 1 : 0;
        }

        // 将同一语义键的持续请求分配到新的实例号，供调用方后续只更新这个实例。
        public CharacterAnimationRequest Hold(string key, float weight)
        {
            if (string.IsNullOrWhiteSpace(key)) return default;
            var request = new CharacterAnimationRequest(key, NextInstance(key), weight);
            return Submit(request) ? request : default;
        }

        // 只根据语义键和本次调用的参数建立播放时钟。
        public CharacterActionPlayback PlayOnce(in CharacterOneShotRequest request, CharacterAnimationResourceCatalog resources, double at)
        {
            if (!resources.TryClock(request, at, out var clock, out int clipId)) return null;
            long id = NextInstance(request.Key);
            if (id == 0) return null;
            var playback = new CharacterActionPlayback(request.Key, id, clock, clipId);
            var semantic = new CharacterAnimationRequest(request.Key, id, request.Weight);
            latestInstances[request.Key] = id;
            schedule.Add(CreateDemand(semantic, rules[request.Key], ++submissionOrder, playback));
#if UNITY_EDITOR
            History?.Record(playback);
#endif
            return playback;
        }

        // 将定时淡出绑定到当前宿主仍在使用的句柄。
        public bool FadeOut(CharacterActionPlayback playback, double at, float seconds)
        {
            if (!Owns(playback) || double.IsNaN(at) || double.IsInfinity(at) ||
                !(seconds >= 0f) || float.IsInfinity(seconds) || at >= playback.Clock.FadeAt) return false;
            playback.FadeOut(at, seconds);
            return true;
        }

        // 安排合法播放区间的截止点，由真实图补完截至该时刻的事件后发布终态。
        public bool Cancel(CharacterActionPlayback playback, double at)
        {
            if (playback == null || double.IsNaN(at) || double.IsInfinity(at)) return false;
            if (at <= playback.Clock.Start && schedule.Contains(playback))
            {
                if (!schedule.StopBeforeStart(playback, at)) return false;
                if (at <= presentationTime)
                    schedule.RemoveWhere(row => row.Playback == playback, handle => CloseUnavailable(handle, CharacterPlaybackEndSource.ExplicitWithdrawal));
                return true;
            }
            if (!Owns(playback) || at >= playback.Clock.StopAt) return false;
            playback.Cancel(at, CharacterActionPlaybackStatus.Withdrawn, CharacterPlaybackEndSource.ExplicitWithdrawal);
            return true;
        }

        // 验证句柄所属的宿主和实例，拒绝来自旧代次或其他角色的请求。
        private bool Owns(CharacterActionPlayback playback) => playback != null &&
            (active.TryGetValue(playback.Key, out var demand) && demand.Playback == playback || schedule.Contains(playback));

        // 更新相同实例的需求，新实例抢占同键旧实例。
        public bool Submit(CharacterAnimationRequest request)
        {
            string key = request.SemanticKey;
            if (key == null || request.InstanceId <= 0 || !rules.TryGetValue(key, out var rule)) return false;
            ActivateThrough(presentationTime);
            if (active.TryGetValue(key, out var current) && current.InstanceId == request.InstanceId)
            {
                if (current.Playback != null) return false;
                var updated = new CharacterAnimationRequest(key, request.InstanceId, request.Weight, current.StartNormalizedTime);
                active[key] = CreateDemand(updated, rule, current.SubmissionOrder);
                return true;
            }
            if (latestInstances.TryGetValue(key, out long latest) && request.InstanceId <= latest) return false;
            latestInstances[key] = request.InstanceId;
            Activate(CreateDemand(request, rule, ++submissionOrder), presentationTime);
            return true;
        }

        // 逐个处理已到期预约，未到时的请求保留在独立时间线上。
        private bool ActivateThrough(double through)
        {
            bool changed = false;
            while (schedule.TryTake(through, out var demand, out bool cancelled))
            {
                if (cancelled) CloseUnavailable(demand.Playback, CharacterPlaybackEndSource.ExplicitWithdrawal);
                else Activate(demand, demand.Playback.Clock.Start);
                changed = true;
            }
            return changed;
        }

        // 在实际开始边界交回旧实例的所有权，旧声部保留截至交接时刻的补播区间。
        private void Activate(in CharacterAnimationDemand demand, double at)
        {
            string key = demand.Key;
            foreach (var old in retiring)
                if (old.Key == key && old.Clock.Start <= at && old.Clock.End > at)
                    old.Cancel(at, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ArbitrationLost);
            if (active.TryGetValue(key, out var current))
            {
                double currentStart = ownership.StartOf(current);
                if (currentStart > at)
                {
                    // 从早期片段事件重入的请求只拥有两次起播间的区间，不能撤回较晚生效的实例。
                    ownership.Add(demand, at);
                    ownership.Close(demand, currentStart);
                    if (demand.Playback != null)
                    {
                        demand.Playback.Cancel(currentStart, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ArbitrationLost);
                        retiring.Add(demand.Playback);
                    }
                    return;
                }
                EndAt(key, current.InstanceId, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ArbitrationLost, at);
            }
            int index = rankedKeys.Count;
            while (index > 0 && demand.Precedes(active[rankedKeys[index - 1]])) index--;
            rankedKeys.Insert(index, key);
            active[key] = demand;
            ownership.Add(demand, at);
        }

        // 按语义键撤销调用者指定的实例。
        public bool Withdraw(string key, long instanceId) =>
            key != null && active.TryGetValue(key, out var demand) && demand.Playback == null &&
            End(key, instanceId, CharacterActionPlaybackStatus.Withdrawn, CharacterPlaybackEndSource.ExplicitWithdrawal);

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
        internal bool RetireDue(double now, CharacterAnimationRequest behavior)
        {
            presentationTime = now;
            // 先完成已经显示的较早尾段，再激活之后的预约，使事件重入发生在正确的时刻。
            double through = now;
            foreach (var old in retiring)
                if (old.HasStarted) through = Math.Min(through, old.Clock.End);
            foreach (var key in rankedKeys)
                if (active[key].Playback is CharacterActionPlayback playing && playing.HasStarted)
                    through = Math.Min(through, playing.Clock.End);
            through = Math.Min(through, schedule.Next);
            bool activated = ActivateThrough(through);
            var baseline = Resolve(behavior);
            RetireDisplaced(previousSelection.UpperOwner, baseline, now);
            if (previousSelection.LowerOwner.Playback != previousSelection.UpperOwner.Playback)
                RetireDisplaced(previousSelection.LowerOwner, baseline, now);
            for (int i = rankedKeys.Count - 1; i >= 0; i--)
            {
                var demand = active[rankedKeys[i]];
                var playback = demand.Playback;
                if (playback == null || !playback.Clock.Valid || playback.Clock.End > through) continue;
                rankedKeys.RemoveAt(i);
                active.Remove(demand.Key);
                ownership.Close(demand, playback.Clock.End);
                retiring.Add(playback);
            }
            return activated;
        }

        // 核对实际素材结果后发布终态，无法再次取得素材的实例按资源失效退出。
        internal void FinishRetired(CharacterActionPlayback playback, bool available)
        {
            if (!retiring.Remove(playback)) return;
            bool finished = playback.HasStarted && playback.Clock.NaturalEnd <= playback.Clock.StopAt &&
                playback.Cursor?.AtNaturalEnd == true;
            var status = finished ? CharacterActionPlaybackStatus.Completed : !playback.HasStarted ? CharacterActionPlaybackStatus.Withdrawn :
                !available || playback.StopSource == default ? CharacterActionPlaybackStatus.Interrupted : playback.StopStatus;
            var reason = finished ? CharacterPlaybackEndSource.ClipFinished : !available ? CharacterPlaybackEndSource.ResourceUnavailable :
                playback.StopSource == default ? CharacterPlaybackEndSource.WindowExpired : playback.StopSource;
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
            schedule.RemoveWhere(row => resources.OneShotClipId(row.Key) != row.Playback.ClipId,
                handle => CloseUnavailable(handle, CharacterPlaybackEndSource.ResourceUnavailable));
            for (int i = rankedKeys.Count - 1; i >= 0; i--)
            {
                var demand = active[rankedKeys[i]];
                var playback = demand.Playback;
                if (playback == null) continue;
                if (playback.ClipId != 0 && resources.OneShotClipId(demand.Key) == playback.ClipId) continue;
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
                    demand = demand.WithTiming(new CharacterActionTiming(clock.Sample(presentationTime), demand.Playback.Cursor));
                }
                claims.TryClaim(demand, baseline);
            }
            return claims.Compose(baseline, claims.HasOwner ? AfterTakeover(baseline) : baseline);
        }

        // 部位被接管时按基础动作声明的回退键重新选姿，跳过无效或非基础绑定。
        private CharacterAnimationDemand AfterTakeover(CharacterAnimationDemand baseline)
        {
            var key = baseline.Rule?.OnTakeoverFallback;
            if (string.IsNullOrEmpty(key) || !hasBinding(key)) return baseline;
            var fallback = Resolve(new CharacterAnimationRequest(key, baseline.InstanceId));
            return fallback.IsValid && fallback.Source == CharacterAnimationSource.Baseline ? fallback : baseline;
        }

        // 以观察到的角色时刻登记基础动作的优先级切换。
        internal void ChangeBaseline(CharacterAnimationRequest behavior, double at) => ownership.ChangeBaseline(Resolve(behavior), at);

        // 在摘下到期请求之前重放真实部位冲突，已经离开活动集合的句柄也可缩短尾段。
        private void RetireDisplaced(in CharacterAnimationDemand previous, in CharacterAnimationDemand baseline, double now)
        {
            var handle = previous.Playback;
            if (handle == null || !handle.Clock.Valid || !handle.HasStarted || handle.EndSource != default) return;
            var at = ownership.FirstLoss(previous, previousSelection.RequestedBaseline, baseline, now, hasBinding);
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
            End(previous.Key, previous.InstanceId, CharacterActionPlaybackStatus.Interrupted, CharacterPlaybackEndSource.ArbitrationLost);
        }

        // 解析已配置的基础规则，外部策略还可按自身语义创建基础需求。
        private CharacterAnimationDemand Resolve(CharacterAnimationRequest request)
        {
            if (request.SemanticKey != null && hasBinding(request.SemanticKey) && rules.TryGetValue(request.SemanticKey, out var rule) &&
                rule.Source == CharacterAnimationSource.Baseline) return CreateDemand(request, rule, 0);
            if (UsesStrategyIntents)
            {
                if (string.IsNullOrWhiteSpace(request.SemanticKey) || request.InstanceId <= 0)
                    request = new CharacterAnimationRequest("$base.default", 1);
                if (strategyRule?.Key != request.SemanticKey)
                    strategyRule = new CharacterAnimationRule(request.SemanticKey, CharacterAnimationSource.Baseline, 0);
                return CreateDemand(request, strategyRule, 0);
            }
            string idle = CharacterLocomotionKeys.IdleKey;
            return hasBinding(idle) && rules.TryGetValue(idle, out rule)
                ? CreateDemand(new CharacterAnimationRequest(idle, request.InstanceId), rule, 0) : default;
        }

        // 将请求转换为用于仲裁的需求，基础姿势始终以完整权重播放。
        private static CharacterAnimationDemand CreateDemand(CharacterAnimationRequest request,
            CharacterAnimationRule rule, long order, CharacterActionPlayback playback = null)
        {
            float weight = rule.Source == CharacterAnimationSource.Baseline ? 1f : ClampUnit(request.Weight);
            float? start = request.StartNormalizedTime.HasValue ? ClampUnit(request.StartNormalizedTime.Value) : (float?)null;
            return new CharacterAnimationDemand(rule, request, weight, start, order, playback);
        }

        // 将外部权重或入点约束为有效的归一值。
        private static float ClampUnit(float value) => Math.Min(1f, float.IsNaN(value) ? 0f : Math.Max(0f, value));

        // 失去播放资源时立即结算单次句柄，持续动作保留原编号等待资源恢复。
        internal void LosePlaybackResources()
        {
            var source = CharacterPlaybackEndSource.ResourceUnavailable;
            PublishReceipts(source);
            schedule.RemoveWhere(_ => true, handle => CloseUnavailable(handle, source));
            foreach (var handle in retiring) CloseUnavailable(handle, source);
            retiring.Clear();
            ownership.Clear();
            previousSelection = default;
            for (int i = rankedKeys.Count - 1; i >= 0; i--)
            {
                var demand = active[rankedKeys[i]];
                if (demand.Playback == null) ownership.Add(demand, presentationTime);
                else
                {
                    rankedKeys.RemoveAt(i);
                    active.Remove(demand.Key);
                    CloseUnavailable(demand.Playback, source);
                }
            }
        }

        // 无法继续求值时按是否真实起播发布唯一终态。
        private void CloseUnavailable(CharacterActionPlayback handle, CharacterPlaybackEndSource source)
        {
            if (handle.QueueEnd(handle.HasStarted ? CharacterActionPlaybackStatus.Interrupted : CharacterActionPlaybackStatus.Withdrawn, source))
                PublishEnd(handle);
        }

        // 结束本角色当前的全部请求，回执在角色重置后仍然可查。
        public void Reset(CharacterPlaybackEndSource source)
        {
            evaluatingAt = null;
            PublishReceipts(source);
            for (int i = rankedKeys.Count - 1; i >= 0; i--)
            {
                var demand = active[rankedKeys[i]];
                End(demand.Key, demand.InstanceId, CharacterActionPlaybackStatus.Withdrawn, source);
            }
            schedule.RemoveWhere(_ => true, handle => CloseUnavailable(handle, source));
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
