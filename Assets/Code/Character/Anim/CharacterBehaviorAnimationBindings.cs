using Binding = ThreeC.CharacterBehavior.CharacterBehaviorAnimationBinding;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>按映射顺序收集行为许可与瞬发边沿，在动画采集阶段统一提交。</summary>
    internal sealed class CharacterBehaviorAnimationBindings
    {
        private readonly List<Binding> bindings = new List<Binding>();
        private readonly Queue<(Binding binding, CharacterActionPlaybackStatus status, CharacterPlaybackOrigin origin,
            CharacterPlaybackStartSource start, CharacterPlaybackEndSource end)> edges = new();
        internal int StateVersion { get; set; }
        private int? version;
        private CharacterAnimationConfiguration configuration;
        internal readonly CharacterAnimationComposer Composer;
        private CharacterAnimationResourceCatalog resources;
        private readonly UnityEngine.Object context;

        // 装配映射、资源目录与统一的动画提交目标。
        public CharacterBehaviorAnimationBindings(CharacterAnimationConfiguration config,
            CharacterAnimationComposer composer, CharacterAnimationResourceCatalog resources, UnityEngine.Object context)
        {
            Composer = composer;
            this.resources = resources;
            this.context = context;
            configuration = config;
            AddMappings(config.BehaviorAnimations, default, false);
        }

        // 用当前行为相位为新增映射续播，并保留仍在使用的绑定。
        internal void Reconfigure(CharacterAnimationConfiguration config, CharacterAnimationResourceCatalog catalog,
            CharacterBehaviorObservation frame, AnimationModuleChanges changes)
        {
            configuration = config;
            resources = catalog;
            var retained = new Dictionary<CharacterBehaviorAnimationMapping, Binding>();
            foreach (var binding in bindings)
            {
                if (changes.RetainedMappings.TryGetValue(binding.Source, out var next) && !changes.ActionKeys.Contains(binding.Rule.EntryAction ?? "") &&
                    !changes.ActionKeys.Contains(binding.Rule.HoldAction ?? ""))
                {
                    binding.Source = next;
                    retained[next] = binding;
                    continue;
                }
                binding.Removed = true;
                if (binding.Attack != null) binding.Attack.Cancel(frame.Time, CharacterActionPlaybackStatus.Withdrawn, CharacterPlaybackEndSource.ModuleRemoved);
                else if (binding.IsPulse) binding.Pulse.End(CharacterActionPlaybackStatus.Withdrawn, CharacterPlaybackEndSource.ModuleRemoved);
                else binding.Grant.End(CharacterActionPlaybackStatus.Withdrawn, CharacterPlaybackEndSource.ModuleRemoved);
            }
            bindings.Clear();
            AddMappings(config.BehaviorAnimations, frame, true, retained);
        }

        // 绑定动作规则并从已获准的行为恢复新加入的映射。
        private void AddMappings(CharacterBehaviorAnimationMapping[] mappings, CharacterBehaviorObservation frame, bool seed,
            Dictionary<CharacterBehaviorAnimationMapping, Binding> retained = null)
        {
            if (mappings == null) return;
            foreach (var rule in mappings)
            {
                if (rule == null) continue;
                if (retained != null && retained.TryGetValue(rule, out var previous))
                {
                    bindings.Add(previous);
                    continue;
                }
                if (!rule.HasValidTrigger)
                {
                    Debug.LogWarning("行为表现需要一个许可行为和至少一个动作键。", context);
                    continue;
                }
                var settings = rule.IsAttackState ? null : rule.Handoff;
                if (!rule.IsAttackState && settings?.OverridePhases != true && rule.EntryAction != null &&
                    resources.Rules.TryGetValue(rule.EntryAction, out var action) && action.Handoff?.OverridePhases == true)
                    settings = action.Handoff;
                if (rule.Trigger == CharacterAnimationTrigger.OnStart && settings?.OverridePhases == true &&
                    (!settings.Valid || !resources.IsWeightedAction(rule.EntryAction)))
                {
                    Debug.LogWarning($"行为 {rule.BehaviorId} 的单次接管需要有效阶段、时长和加权层：{rule.EntryAction}。", context);
                    continue;
                }
                var binding = new Binding(rule, settings, this);
                bindings.Add(binding);
                if (seed && frame.FrameNumber != 0) Observe(frame, true, binding);
            }
        }

        // 核对主线程已冻结的素材身份，再把请求交给通用单次播放实例。
        internal CharacterActionPlayback PlayRequest(string key, float weight, CharacterPlaybackOrigin origin,
            CharacterPlaybackStartSource source, CharacterPlaybackClock clock, int clipId)
        {
            if (!clock.Valid || clipId == 0 || resources.Ready && resources.OneShotClipId(key, true) != clipId) return null;
            return Composer.PlayOnce(key, weight, origin, source, clock, clipId);
        }

        // 按键独立播放在上层兼容映射中冻结招式配置，并安排后摇及退出命令。
        internal CharacterActionPlayback PlayExplicit(string key, float weight, CharacterPlaybackOrigin origin, double at)
        {
            resources.CaptureOneShots();
            if (!resources.CanPlayOnce(key)) return null;
            int state = FindAttackState(key, configuration);
            if (state == 0) return Composer.PlayOnce(key, weight, origin, CharacterPlaybackStartSource.ExplicitRequest, clipId: resources.OneShotClipId(key));
            var action = configuration.AttackTable?.Find(state)?.Presentation ?? default;
            var handle = PlayRequest(key, weight, origin, CharacterPlaybackStartSource.ExplicitRequest, action.Play(at), action.ClipId);
            handle?.FadeOut(action.RecoveryAt(at), action.Recovery);
            handle?.Cancel(action.ExitAt(at), CharacterActionPlaybackStatus.Withdrawn, CharacterPlaybackEndSource.BehaviorWindowEnded);
            return handle;
        }

        // 将动作键解析到对应的具名攻击，独立播放在请求时从该字段冻结时钟。
        internal static int FindAttackState(string key, CharacterAnimationConfiguration config)
        {
            if (config.BehaviorAnimations != null)
                foreach (var mapping in config.BehaviorAnimations)
                    if (mapping?.IsAttackState == true && mapping.EntryAction == key) return mapping.AttackStateId;
            return 0;
        }

        // 按该物理步实际执行的行为判断前摇步态，后到的渲染帧不会改写步态历史。
        internal bool PreservesEntryGait(in CharacterControllerFrameResult frame)
        {
            foreach (var binding in bindings)
                if (binding.Rule.PreservesEntryGait(frame, binding.HandoffSettings)) return true;
            return false;
        }

        // 按行为序列收集本帧全部转换，动画采集时提交最终许可及有序边沿。
        public void Observe(CharacterBehaviorObservation frame, bool seed = false)
            => Observe(frame, seed, null);

        // 处理一次裁决，或仅为新绑定恢复已有状态。
        private void Observe(CharacterBehaviorObservation frame, bool seed, Binding added)
        {
            bool reconnect = seed || version != frame.SimulationVersion;
            if (version.HasValue && version != frame.SimulationVersion) edges.Clear();
            version = frame.SimulationVersion;
            foreach (var binding in bindings)
            {
                if (added == null || binding == added) binding.Observe(StateVersion, frame, reconnect, seed);
            }
        }

        // 入队时复制身份，避免后续行为片段把尚未消费的开始边沿改为新招式。
        internal void QueueEdge(Binding binding, CharacterActionPlaybackStatus status,
            CharacterPlaybackStartSource start = CharacterPlaybackStartSource.BehaviorGrant,
            CharacterPlaybackEndSource end = CharacterPlaybackEndSource.BehaviorWindowEnded)
        {
            edges.Enqueue((binding, status, binding.Origin, start, end));
        }

        // 将最新许可交接给持续绑定；失效时按调用者提供的边界原因撤回。
        public void UpdateGrants(bool valid, long guardGrant, CharacterPlaybackEndSource invalid = CharacterPlaybackEndSource.Paused)
        {
            if (!valid) edges.Clear();
            foreach (var binding in bindings)
            {
                if (binding.IsPulse)
                {
                    if (!valid) binding.Pulse?.End(CharacterActionPlaybackStatus.Withdrawn, invalid);
                }
                else binding.Grant.UpdateGrant(valid, guardGrant, invalid);
            }
        }

        // 先按全局顺序消费冻结的边沿，再逐绑定提交最终许可与相位。
        public void SubmitGranted()
        {
            while (edges.Count > 0)
            {
                var (binding, edge, origin, start, end) = edges.Dequeue();
                if (binding.Removed) continue;
                if (edge == CharacterActionPlaybackStatus.Pending) binding.Pulse.Start(origin, start);
                else binding.Pulse.End(edge, end);
            }
            foreach (var binding in bindings)
            {
                if (binding.IsPulse) binding.Pulse?.Submit();
                else binding.Grant.Submit();
            }
        }

        // 判断资源绑定是否仍指向这个逻辑实例的原素材。
        internal bool SameClip(CharacterActionPlayback playback) => playback != null && playback.ClipId != 0 &&
            resources.OneShotClipId(playback.Key) == playback.ClipId;

        // 按冻结的执行身份提交非循环进入动作，并区分素材不可用与合成器拒绝。
        internal bool TryPlayEntry(Binding binding, CharacterPlaybackOrigin origin, CharacterPlaybackStartSource source,
            out CharacterActionPlayback entry)
        {
            var rule = binding.Rule;
            bool playable = resources.CanPlayOnce(rule.EntryAction);
            entry = playable ? Composer.PlayOnce(rule.EntryAction, rule.Weight, origin, source, clipId: resources.OneShotClipId(rule.EntryAction)) : null;
            if (!playable || binding.IsPulse && entry == null)
                Report(binding, rule.EntryAction, binding.IsPulse ? "单次动作需要可用的非循环片段" : "需要可用的非循环进入动作");
            return playable;
        }

        // 为可用的持续姿势分配实例，只返回已获合成器接收的需求。
        internal CharacterAnimationRequest SubmitHold(Binding binding)
        {
            var rule = binding.Rule;
            if (!resources.CanSubmit(rule.HoldAction))
            {
                Report(binding, rule.HoldAction, "持续动作不可用");
                return default;
            }
            var request = new CharacterAnimationRequest(rule.HoldAction, Composer.NextInstance(rule.HoldAction), rule.Weight);
            return Composer.Submit(request) ? request : default;
        }

        private readonly Queue<string> warnings = new Queue<string>();

        // 在统一的日志交付阶段输出已积累的配置问题。
        internal void FlushWarnings()
        {
            while (warnings.Count > 0) Debug.LogWarning(warnings.Dequeue(), context);
        }

        // 每条绑定只记录首次遇到的无效资源。
        private void Report(Binding binding, string key, string error)
        {
            if (binding.Reported) return;
            binding.Reported = true;
            warnings.Enqueue($"行为 {binding.Rule.BehaviorId} 的表现配置：{error}（{key}）。");
        }

        // 播放库重绑后只重新申请仍获准且尚未终结的进入动作。
        public void RebindGrantedEntries()
        {
            foreach (var binding in bindings)
            {
                if (binding.IsPulse) binding.Pulse?.Rebind();
                else binding.Grant.Rebind();
            }
        }

        // 清理失去时间窗且始终未获部位的请求。
        public void ReleaseExpiredRequests()
        {
            foreach (var binding in bindings) binding.Pulse?.ReleaseExpired();
        }

        // 角色硬边界撤销绑定观察，暂停与同素材重绑不调用此重置。
        public void Reset(CharacterPlaybackEndSource source)
        {
            UpdateGrants(false, 0, source);
            version = null;
            foreach (var binding in bindings)
            {
                binding.Attack?.Cancel(Composer.Time, CharacterActionPlaybackStatus.Interrupted, source);
                binding.ResetObservation();
            }
        }
    }
}
