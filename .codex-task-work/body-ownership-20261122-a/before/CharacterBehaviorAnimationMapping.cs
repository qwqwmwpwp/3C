using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal sealed class CharacterBehaviorChoiceAttribute : PropertyAttribute { }

    internal enum CharacterAnimationTrigger
    {
        [InspectorName("许可期间持续")] WhileGranted,
        [InspectorName("获准时播放一次")] OnStart
    }

    internal enum CharacterGrantedEntryRecovery
    {
        [InspectorName("接入持续姿势")] ContinueToHold,
        [InspectorName("重新播放进入动作")] ReplayEntry
    }

    [Serializable]
    internal sealed class CharacterBehaviorAnimationMapping
    {
        // 保留资产中的 Behavior 数值及完整位宽；Unity 不能序列化以 ulong 为底层类型的枚举。
        [SerializeField, CharacterBehaviorChoice, InspectorName("许可行为"), Tooltip("选择一个行为。普通移动按移动请求获准判断。")]
        private ulong Behavior = (ulong)CharacterBehaviorId.Guard;
        public CharacterBehaviorId BehaviorId => (CharacterBehaviorId)Behavior;
        [InspectorName("触发方式")] public CharacterAnimationTrigger Trigger;
        [InspectorName("单次攻击招式"), Tooltip("用于攻击行为的单次映射，None 表示跟随整次行为许可。")]
        public CharacterAttackMove AttackMove = CharacterAttackMove.LightOne;
        [InspectorName("进入动作键"), Tooltip("单次模式必填；持续模式下可选，播放完成后转入持续姿势。")]
        public string EntryAction;
        [InspectorName("持续动作键")] public string HoldAction;
        [InspectorName("进入动作失去部位后的恢复")] public CharacterGrantedEntryRecovery EntryRecovery;
        [InspectorName("局部层权重"), Range(0f, 1f)] public float Weight = 1f;
        [InspectorName("单次动作交接")] public CharacterActionHandoffSettings Handoff = new CharacterActionHandoffSettings();
    }

    /// <summary>按已提交的许可维护动作实例。一次许可只进入一次，持续姿势随动作资源选择移动变体。</summary>
    internal sealed class CharacterBehaviorAnimationBindings
    {
        private sealed class Binding
        {
            public CharacterBehaviorAnimationMapping Rule;
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
        private readonly CharacterAnimationResourceCatalog resources;
        private readonly UnityEngine.Object context;

        public CharacterBehaviorAnimationBindings(CharacterBehaviorAnimationMapping[] mappings,
            CharacterAnimationComposer composer, CharacterAnimationResourceCatalog resources, UnityEngine.Object context)
        {
            this.composer = composer;
            this.resources = resources;
            this.context = context;
            if (mappings == null) return;
            foreach (var rule in mappings)
            {
                if (rule == null) continue;
                ulong id = (ulong)rule.BehaviorId;
                if (id == 0 || (id & (id - 1)) != 0 ||
                    rule.Trigger == CharacterAnimationTrigger.OnStart && string.IsNullOrWhiteSpace(rule.EntryAction) ||
                    string.IsNullOrWhiteSpace(rule.EntryAction) && string.IsNullOrWhiteSpace(rule.HoldAction))
                {
                    Debug.LogWarning("行为表现需要一个许可行为和至少一个动作键。", context);
                    continue;
                }
                if (rule.Trigger == CharacterAnimationTrigger.OnStart && rule.Handoff?.OverridePhases == true &&
                    !rule.Handoff.Valid)
                {
                    Debug.LogWarning($"行为 {rule.BehaviorId} 的单次动作阶段或时长无效：{rule.EntryAction}。", context);
                    continue;
                }
                if (rule.Trigger == CharacterAnimationTrigger.OnStart && rule.Handoff?.OverridePhases == true &&
                    !resources.IsWeightedAction(rule.EntryAction))
                {
                    Debug.LogWarning($"分阶段交接需要已配置的全身叠加、上半身或下半身动作：{rule.EntryAction}。", context);
                    continue;
                }
                bindings.Add(new Binding { Rule = rule });
            }
        }

        public bool PreserveEntryGait { get; private set; }

        public bool Contains(CharacterBehaviorId behavior)
        {
            foreach (var binding in bindings)
                if (binding.Rule.BehaviorId == behavior) return true;
            return false;
        }

        public void Observe(CharacterControllerFrameResult frame, bool seed = false)
        {
            bool reconnect = seed || version != frame.SimulationVersion;
            if (version.HasValue && version != frame.SimulationVersion) Reset();
            version = frame.SimulationVersion;
            var step = frame.Behavior;
            PreserveEntryGait = false;
            foreach (var binding in bindings)
            {
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
                    if (binding.Rule.Handoff?.OverridePhases == true)
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

        // 招式的起止相位来自行为固定步，位移权限和动画淡入都不会重置这份时钟。
        private void ObserveAttack(Binding binding, CharacterBehaviorFrameResult step, bool active, bool reconnect, bool seed)
        {
            bool matches = step.Attack.Move == binding.Rule.AttackMove;
            long instance = matches && active ? step.Attack.InstanceId : 0;
            bool newInstance = instance != 0 && binding.AttackInstance != instance;
            if (newInstance) binding.AttackPhase = 0f;
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

        private void ObserveConfigured(Binding binding, CharacterControllerFrameResult frame, bool active,
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
                if (entering || !wasActive) binding.Duration = binding.Rule.Handoff.Duration;
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

        private void SetHandoff(Binding binding, CharacterActionHandoff source, bool moveApproved, bool entering)
        {
            var settings = binding.Rule.Handoff;
            if (entering) binding.MoveStart = null;
            if (settings?.OverridePhases == true)
            {
                float fullFrom = source.Valid ? Mathf.Min(source.FullFrom, settings.FullFrom) : settings.FullFrom;
                float releaseFrom = source.Valid ? Mathf.Max(source.ReleaseFrom, settings.ReleaseFrom) : settings.ReleaseFrom;
                if (moveApproved && !binding.MoveStart.HasValue && binding.LastPose >= releaseFrom)
                    binding.MoveStart = Mathf.Max(binding.FirstPose.GetValueOrDefault(), releaseFrom);
                source = new CharacterActionHandoff(binding.Duration, fullFrom, releaseFrom,
                    settings.MoveBlendSeconds, binding.MoveStart);
            }
            binding.Handoff = source;
            if (binding.TimedActive && source.Valid && binding.LastPose < source.FullFrom && settings?.FollowSpeedOnEntry != true)
                PreserveEntryGait = true;
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

        public void SubmitGranted()
        {
            while (edges.Count > 0)
            {
                var (binding, edge) = edges.Dequeue();
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
                        float fraction = Mathf.Clamp((float)(Time.timeAsDouble - Time.fixedTimeAsDouble), 0f, Time.fixedDeltaTime);
                        float phase = Mathf.Clamp01(binding.LastPose + fraction / binding.Duration);
                        if (binding.AttackInstance != 0) binding.AttackPhase = Mathf.Max(binding.AttackPhase, phase);
                        float? start = binding.ResumedEntry ? binding.AttackPhase : binding.FirstPose;
                        composer.Sample(binding.Entry, start, phase, binding.Handoff,
                            binding.Rule.Handoff?.StartAtZero == true, binding.ResumedEntry);
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
                    if (binding.Entry == null || binding.Entry.Status != CharacterActionPlaybackStatus.Completed &&
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

        private void Report(Binding binding, string key, string error)
        {
            if (binding.Reported) return;
            binding.Reported = true;
            Debug.LogWarning($"行为 {binding.Rule.BehaviorId} 的表现配置：{error}（{key}）。", context);
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
                        (binding.Entry == null || binding.Entry.Status == CharacterActionPlaybackStatus.Pending ||
                         binding.Entry.Status == CharacterActionPlaybackStatus.Started))
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
            PreserveEntryGait = false;
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
