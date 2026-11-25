using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>按已提交的许可维护动作实例。一次许可只进入一次，持续姿势随动作资源选择移动变体。</summary>
    internal sealed class CharacterBehaviorAnimationBindings
    {
        private sealed class Binding
        {
            public CharacterBehaviorAnimationMapping Source, Rule;
            public bool Removed;
            public CharacterActionHandoffSettings HandoffSettings;
            public long ObservedGrant, CurrentGrant;
            public bool IsPulse => CurrentGrant == 0 && Rule.Trigger == CharacterAnimationTrigger.OnStart;
            public CharacterAnimationRequest Hold;
            public string EntryAction, HoldAction;
            public float GrantWeight;
            public CharacterGrantedEntryRecovery EntryRecovery;
            public bool Interrupted, Reported;
            public CharacterActionPlayback Entry;
            public CharacterActionHandoff Handoff;
            public float? MoveStart;
            public float? FirstPose;
            public float LastPose, Duration;
            public bool TimedActive, ResumeTimed, ResumedEntry;
            public long AttackInstance, EntryAttackInstance;
            public float AttackPhase;
            public float AttackEntryPhase;

            public void RememberAttackCursor()
            {
                if (AttackInstance == 0 || EntryAttackInstance != AttackInstance || Entry?.HasStarted != true) return;
                AttackPhase = Mathf.Max(AttackPhase, Mathf.Clamp01(Entry.LastObservedPhase));
                if (AttackPhase == 0f) AttackPhase = 0.000001f; // 首帧事件已交付，恢复点越过零时刻。
            }
        }

        private readonly List<Binding> bindings = new List<Binding>();
        private readonly Queue<(Binding binding, CharacterActionPlaybackStatus edge)> edges =
            new Queue<(Binding, CharacterActionPlaybackStatus)>();
        private int? version;
        private readonly CharacterAnimationComposer composer;
        private CharacterAnimationResourceCatalog resources;
        private readonly UnityEngine.Object context;

        public CharacterBehaviorAnimationBindings(CharacterBehaviorAnimationMapping[] mappings,
            CharacterAnimationComposer composer, CharacterAnimationResourceCatalog resources, UnityEngine.Object context)
        {
            this.composer = composer;
            this.resources = resources;
            this.context = context;
            AddMappings(mappings, default, false);
        }

        // 用当前行为相位为新增映射续播，并保留仍在使用的绑定。
        internal void Reconfigure(CharacterBehaviorAnimationMapping[] mappings, CharacterAnimationResourceCatalog catalog,
            CharacterBehaviorObservation frame, HashSet<string> replacedKeys)
        {
            resources = catalog;
            var retained = new Dictionary<CharacterBehaviorAnimationMapping, Binding>();
            var wanted = new HashSet<CharacterBehaviorAnimationMapping>(mappings ?? Array.Empty<CharacterBehaviorAnimationMapping>());
            foreach (var binding in bindings)
            {
                if (wanted.Contains(binding.Source) && !replacedKeys.Contains(binding.Rule.EntryAction ?? "") &&
                    !replacedKeys.Contains(binding.Rule.HoldAction ?? ""))
                {
                    retained[binding.Source] = binding;
                    continue;
                }
                binding.Removed = true;
                ClearPulse(binding, CharacterActionPlaybackStatus.Withdrawn);
                if (binding.Hold.InstanceId != 0) composer.Withdraw(binding.Hold.SemanticKey, binding.Hold.InstanceId);
            }
            bindings.Clear();
            AddMappings(mappings, frame, true, retained);
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
                var settings = rule.Handoff;
                if (settings?.OverridePhases != true && rule.EntryAction != null &&
                    resources.Rules.TryGetValue(rule.EntryAction, out var action) && action.Handoff?.OverridePhases == true)
                    settings = action.Handoff;
                if (rule.Trigger == CharacterAnimationTrigger.OnStart && settings?.OverridePhases == true &&
                    (!settings.Valid || !resources.IsWeightedAction(rule.EntryAction)))
                {
                    Debug.LogWarning($"行为 {rule.BehaviorId} 的单次接管需要有效阶段、时长和加权层：{rule.EntryAction}。", context);
                    continue;
                }
                var binding = new Binding { Source = rule, Rule = rule.Snapshot(), HandoffSettings = settings?.Snapshot() };
                bindings.Add(binding);
                if (seed && frame.FrameNumber != 0) Observe(frame, true, binding);
            }
        }

        // 独立调用同一动作键时复用招式阶段；行为驱动的实例采用行为时间。
        internal static CharacterActionHandoffSettings ResolveHandoff(CharacterAnimationActionSettings action,
            CharacterAnimationConfiguration config)
        {
            if (action.Handoff?.OverridePhases == true) return action.Handoff;
            if (config.BehaviorAnimations == null) return null;
            foreach (var mapping in config.BehaviorAnimations)
            {
                if (mapping == null || mapping.Trigger != CharacterAnimationTrigger.OnStart || mapping.EntryAction != action.Key) continue;
                if (mapping.BehaviorId != CharacterBehaviorId.Attack) continue;
                var move = config.AttackSettings?.Moves?.Move(mapping.AttackMove);
                if (move == null || !move.HasValidPhases) continue;
                return new CharacterActionHandoffSettings
                {
                    FullFrom = move.StrikeStart, ReleaseFrom = move.RecoveryStart,
                    MoveBlendSeconds = move.RecoveryMoveBlendSeconds,
                    StartAtZero = mapping.Handoff?.StartAtZero == true
                };
            }
            return null;
        }

        // 按该物理步实际执行的行为判断前摇步态，后到的渲染帧不会改写步态历史。
        internal bool PreservesEntryGait(in CharacterControllerFrameResult frame)
        {
            foreach (var binding in bindings)
                if (binding.Rule.PreservesEntryGait(frame, binding.HandoffSettings)) return true;
            return false;
        }

        internal bool IsReturningToMovement(in CharacterAnimationDemand overlay, in CharacterBehaviorFrameResult step)
        {
            var attack = step.Attack;
            if (overlay.Playback == null || !step.MoveApproved || step.DashActive ||
                attack.InstanceId == 0 || attack.Phase != CharacterBehaviorPhase.Recovery) return false;
            foreach (var binding in bindings)
                if (binding.AttackInstance == attack.InstanceId && binding.EntryAttackInstance == attack.InstanceId &&
                    binding.Entry == overlay.Playback) return true;
            return false;
        }

        // 按行为序列收集本帧全部转换，动画采集时提交最终许可及有序边沿。
        public void Observe(CharacterBehaviorObservation frame, bool seed = false)
            => Observe(frame, seed, null);

        // 处理一次裁决，或仅为新绑定恢复已有状态。
        private void Observe(CharacterBehaviorObservation frame, bool seed, Binding added)
        {
            bool reconnect = seed || version != frame.SimulationVersion;
            if (version.HasValue && version != frame.SimulationVersion) Reset();
            version = frame.SimulationVersion;
            var step = frame.Behavior;
            foreach (var binding in bindings)
            {
                if (added != null && binding != added) continue;
                var id = binding.Rule.BehaviorId;
                if ((step.InterruptedBehaviors & id) != 0) binding.Interrupted = true;
                bool active = id == CharacterBehaviorId.Move ? step.MoveApproved : (step.ActiveBehaviors & id) != 0;
                bool began = id == CharacterBehaviorId.Move ? step.MoveStarted : (step.StartedBehaviors & id) != 0;
                if (binding.IsPulse)
                {
                    // 同一步可能先释放旧许可、再批准新实例；自然结束保留开始事件。
                    if (!seed && (step.InterruptedBehaviors & id) != 0)
                        edges.Enqueue((binding, CharacterActionPlaybackStatus.Interrupted));
                    else if (!seed && (step.WithdrawnBehaviors & id) != 0)
                        edges.Enqueue((binding, CharacterActionPlaybackStatus.Withdrawn));
                    if (id == CharacterBehaviorId.Attack && binding.Rule.AttackMove != CharacterAttackMove.None)
                    {
                        ObserveAttack(binding, step, active, reconnect, seed);
                        continue;
                    }
                    if (binding.HandoffSettings?.OverridePhases == true)
                    {
                        ObserveConfigured(binding, frame, active, began, reconnect, seed);
                        continue;
                    }
                    if (began) binding.FirstPose = null;
                    if (step.DisplacementProvider == id && !(began && !active))
                    {
                        binding.FirstPose = step.ActionMotion.StartPhase;
                        binding.LastPose = step.ActionMotion.EndPhase;
                        binding.Duration = step.ActionMotion.Duration;
                    }
                    binding.TimedActive = active && step.DisplacementProvider == id && binding.FirstPose.HasValue;
                    if ((!seed && began) || (reconnect && binding.TimedActive))
                    {
                        binding.ResumeTimed = false;
                        edges.Enqueue((binding, CharacterActionPlaybackStatus.Pending));
                    }
                    continue;
                }
                if (!active) binding.ObservedGrant = 0;
                else if (began || binding.ObservedGrant == 0) binding.ObservedGrant = frame.FrameNumber;
            }
        }

        // 招式起止相位来自当帧行为时间，位移权限和动画淡入都不会重置时钟。
        private void ObserveAttack(Binding binding, CharacterBehaviorFrameResult step, bool active, bool reconnect, bool seed)
        {
            bool matches = step.Attack.Move == binding.Rule.AttackMove;
            long instance = matches && active ? step.Attack.InstanceId : 0;
            bool newInstance = instance != 0 && binding.AttackInstance != instance;
            if (newInstance)
            {
                binding.AttackPhase = 0f;
                binding.AttackEntryPhase = step.Attack.StartProgress;
            }
            binding.AttackInstance = instance;
            binding.TimedActive = instance != 0;
            if (!matches)
            {
                if (step.AttackMoveStarted)
                {
                    binding.ResumeTimed = false;
                    if (!seed) edges.Enqueue((binding, CharacterActionPlaybackStatus.Withdrawn));
                }
                return;
            }

            binding.FirstPose = step.Attack.StartProgress;
            binding.LastPose = step.Attack.Progress;
            binding.Duration = step.Attack.Duration;
            SetHandoff(binding, step.Attack.Handoff, step.MoveApproved, reconnect || newInstance);
            if (!active)
            {
                binding.ResumeTimed = false;
                if (!seed) edges.Enqueue((binding, CharacterActionPlaybackStatus.Completed));
            }
            else if (reconnect && !step.AttackMoveStarted)
            {
                binding.ResumeTimed = true;
            }
            else if (newInstance || step.AttackMoveStarted)
            {
                binding.ResumeTimed = false;
                edges.Enqueue((binding, CharacterActionPlaybackStatus.Pending));
            }
        }

        // 用当帧行为时钟或配置的时长推进单次动作。
        private void ObserveConfigured(Binding binding, CharacterBehaviorObservation frame, bool active,
            bool began, bool reconnect, bool seed)
        {
            var step = frame.Behavior;
            bool entering = began || reconnect;
            bool wasActive = binding.TimedActive;
            binding.TimedActive = active;
            if (!active && !began && !wasActive) return;
            bool hasClock = binding.Rule.BehaviorId != CharacterBehaviorId.Move &&
                step.DisplacementProvider == binding.Rule.BehaviorId && step.ActionMotion.StartPhase.HasValue &&
                step.ActionMotion.Duration > 0f;
            if (hasClock)
            {
                binding.FirstPose = step.ActionMotion.StartPhase;
                binding.LastPose = step.ActionMotion.EndPhase;
                binding.Duration = step.ActionMotion.Duration;
            }
            else
            {
                if (entering || !wasActive) binding.Duration = binding.HandoffSettings.Duration;
                float age = step.GrantAgeAtStepStart(binding.Rule.BehaviorId);
                binding.FirstPose = Mathf.Clamp01(age / binding.Duration);
                binding.LastPose = Mathf.Clamp01((age + frame.DeltaTime) / binding.Duration);
            }
            SetHandoff(binding, default, step.MoveApproved, entering);
            if (entering && (active || !seed && began))
                edges.Enqueue((binding, CharacterActionPlaybackStatus.Pending));
            if (!active && !seed && (step.InterruptedBehaviors & binding.Rule.BehaviorId) == 0 &&
                (step.WithdrawnBehaviors & binding.Rule.BehaviorId) == 0)
                edges.Enqueue((binding, CharacterActionPlaybackStatus.Completed));
        }

        // 将本次行为相位和移动许可转换成动作交还权重。
        private void SetHandoff(Binding binding, CharacterActionHandoff source, bool moveApproved, bool entering)
        {
            var settings = binding.HandoffSettings;
            if (entering) binding.MoveStart = null;
            if (!source.UsesRouteBlend && settings?.OverridePhases == true)
            {
                float releaseFrom = source.Valid ? Mathf.Max(source.ReleaseFrom, settings.ReleaseFrom) : settings.ReleaseFrom;
                if (moveApproved && !binding.MoveStart.HasValue && binding.LastPose >= releaseFrom)
                    binding.MoveStart = Mathf.Max(binding.FirstPose.GetValueOrDefault(), releaseFrom);
                source = settings.Resolve(binding.Duration, source, binding.MoveStart);
            }
            binding.Handoff = source;
        }

        public void UpdateGrants(bool valid, long guardGrant)
        {
            if (!valid) edges.Clear();
            foreach (var binding in bindings)
            {
                if (binding.IsPulse)
                {
                    if (!valid) ClearPulse(binding, CharacterActionPlaybackStatus.Withdrawn);
                    continue;
                }
                long grant = binding.Rule.BehaviorId == CharacterBehaviorId.Guard ? guardGrant : binding.ObservedGrant;
                if (!valid) grant = 0;
                if (grant != binding.CurrentGrant)
                {
                    var reason = binding.Interrupted ? CharacterActionPlaybackStatus.Interrupted : CharacterActionPlaybackStatus.Withdrawn;
                    if (binding.Entry != null) composer.End(binding.Entry.Key, binding.Entry.InstanceId, reason);
                    if (binding.Hold.InstanceId != 0) composer.Withdraw(binding.Hold.SemanticKey, binding.Hold.InstanceId);
                    binding.CurrentGrant = grant;
                    binding.Entry = null;
                    binding.Hold = default;
                    if (grant != 0)
                    {
                        binding.EntryAction = binding.Rule.EntryAction;
                        binding.HoldAction = binding.Rule.HoldAction;
                        binding.GrantWeight = binding.Rule.Weight;
                        binding.EntryRecovery = binding.Rule.EntryRecovery;
                    }
                }
                binding.Interrupted = false;
            }
        }

        // 按行为结束相位提交本帧许可及播放请求。
        public void SubmitGranted()
        {
            while (edges.Count > 0)
            {
                var (binding, edge) = edges.Dequeue();
                if (binding.Removed) continue;
                if (edge != CharacterActionPlaybackStatus.Pending)
                {
                    ClearPulse(binding, edge);
                    continue;
                }
                StartPulse(binding);
            }
            foreach (var binding in bindings)
            {
                if (binding.IsPulse)
                {
                    if (binding.ResumeTimed && binding.TimedActive) StartPulse(binding, resume: true);
                    binding.ResumeTimed = false;
                    if (binding.Entry != null && binding.FirstPose.HasValue && binding.Duration > 0f)
                    {
                        float phase = Mathf.Clamp01(binding.LastPose);
                        if (binding.AttackInstance != 0) binding.AttackPhase = Mathf.Max(binding.AttackPhase, phase);
                        float? start = binding.ResumedEntry ? binding.AttackPhase :
                            binding.AttackInstance != 0 ? binding.AttackEntryPhase : binding.FirstPose;
                        composer.Sample(binding.Entry, start, phase, binding.Handoff,
                            !binding.Handoff.UsesRouteBlend && binding.HandoffSettings?.StartAtZero == true, binding.ResumedEntry);
                    }
                    continue;
                }
                if (binding.CurrentGrant == 0 || binding.Hold.InstanceId != 0) continue;
                if (!string.IsNullOrWhiteSpace(binding.EntryAction))
                {
                    if (binding.Entry?.WasPreempted == true && binding.EntryRecovery == CharacterGrantedEntryRecovery.ReplayEntry)
                    {
                        if (composer.HasActive(binding.EntryAction)) continue;
                        binding.Entry = null; // 新请求只提交一次，仲裁器在部位腾出后开始播放。
                    }
                    if (binding.Entry == null)
                    {
                        if (!resources.CanPlayOnce(binding.EntryAction))
                        {
                            Report(binding, binding.EntryAction, "需要可用的非循环进入动作");
                            continue;
                        }
                        binding.Entry = composer.PlayOnce(binding.EntryAction, binding.GrantWeight);
                    }
                    if (binding.Entry == null || binding.Entry.ObservedStatus != CharacterActionPlaybackStatus.Completed &&
                        !binding.Entry.WasPreempted) continue; // 显式撤销和无效素材不会触发失去部位后的自动恢复。
                }
                if (string.IsNullOrWhiteSpace(binding.HoldAction)) continue;
                if (!resources.CanSubmit(binding.HoldAction))
                {
                    Report(binding, binding.HoldAction, "持续动作不可用");
                    continue;
                }
                long id = composer.NextInstance(binding.HoldAction);
                var request = new CharacterAnimationRequest(binding.HoldAction, id, binding.GrantWeight);
                if (composer.Submit(request)) binding.Hold = request;
            }
        }

        private readonly Queue<string> warnings = new Queue<string>();
        internal void FlushWarnings()
        {
            while (warnings.Count > 0) Debug.LogWarning(warnings.Dequeue(), context);
        }

        private void Report(Binding binding, string key, string error)
        {
            if (binding.Reported) return;
            binding.Reported = true;
            warnings.Enqueue($"行为 {binding.Rule.BehaviorId} 的表现配置：{error}（{key}）。");
        }

        private void ClearPulse(Binding binding, CharacterActionPlaybackStatus reason)
        {
            var entry = binding.Entry;
            if (entry != null) composer.End(entry.Key, entry.InstanceId, reason);
            binding.Entry = null;
            binding.EntryAttackInstance = 0;
            binding.ResumedEntry = false;
        }

        private void StartPulse(Binding binding, bool resume = false)
        {
            var rule = binding.Rule;
            binding.ResumeTimed = false; // 同一帧的真实开始边沿优先于重绑续播。
            binding.ResumedEntry = resume && binding.AttackInstance != 0;
            binding.EntryAttackInstance = binding.AttackInstance;
            binding.Entry = resources.CanPlayOnce(rule.EntryAction) ? composer.PlayOnce(rule.EntryAction, rule.Weight) : null;
            if (binding.Entry == null) Report(binding, rule.EntryAction, "单次动作需要可用的非循环片段");
        }

        public void RebindGrantedEntries()
        {
            foreach (var binding in bindings)
            {
                if (binding.IsPulse)
                {
                    if (binding.AttackInstance != 0 && binding.EntryAttackInstance == binding.AttackInstance &&
                        (binding.Entry == null || binding.Entry.ObservedStatus == CharacterActionPlaybackStatus.Pending ||
                         binding.Entry.ObservedStatus == CharacterActionPlaybackStatus.Started))
                    {
                        binding.RememberAttackCursor();
                        binding.Entry = null; // 仲裁器结束旧句柄，同一行为实例只从当前相位重建播放。
                        binding.ResumeTimed = true;
                    }
                    continue;
                }
                if (binding.CurrentGrant == 0) continue;
                if (binding.Entry?.Status == CharacterActionPlaybackStatus.Pending ||
                    binding.Entry?.Status == CharacterActionPlaybackStatus.Started)
                    binding.Entry = null; // 控制器重绑后，仍获准的持续动作重新衔接进入姿势。
            }
        }

        public void SuspendPulses()
        {
            edges.Clear();
            foreach (var binding in bindings)
            {
                if (!binding.IsPulse) continue;
                binding.RememberAttackCursor();
                ClearPulse(binding, CharacterActionPlaybackStatus.Interrupted);
                binding.ResumeTimed = binding.TimedActive;
            }
        }

        public void ReleaseExpiredRequests()
        {
            foreach (var binding in bindings)
                if (binding.FirstPose.HasValue && !binding.TimedActive && binding.Entry?.Status == CharacterActionPlaybackStatus.Pending)
                    ClearPulse(binding, CharacterActionPlaybackStatus.Withdrawn);
        }

        public void Reset()
        {
            UpdateGrants(false, 0);
            version = null;
            foreach (var binding in bindings)
            {
                binding.ObservedGrant = 0;
                binding.AttackInstance = binding.EntryAttackInstance = 0;
                binding.ResumedEntry = false;
                binding.FirstPose = null;
                binding.Handoff = default;
                binding.MoveStart = null;
                binding.ResumeTimed = binding.TimedActive = false;
            }
        }
    }
}
