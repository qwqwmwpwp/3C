using System;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    /// <summary>行为标识和集合。单个行为使用一个独立位。</summary>
    [Flags]
    public enum CharacterBehaviorId : ulong
    {
        None = 0,
        Move = 1UL << 0,
        Dash = 1UL << 1,
        Guard = 1UL << 2,
        Attack = 1UL << 3,
        LightAttack = Attack // 兼容旧脚本和动画映射，具体招式由行为帧给出。
    }

    [Flags]
    public enum CharacterBehaviorPhase
    {
        [UnityEngine.InspectorName("起手")] Startup = 1,
        [UnityEngine.InspectorName("出招")] Active = 2,
        [UnityEngine.InspectorName("收招")] Recovery = 4,
        [UnityEngine.InspectorName("全部阶段")] Any = Startup | Active | Recovery
    }

    public enum CharacterBehaviorGateMode
    {
        Open,
        Blacklist,
        Whitelist
    }

    /// <summary>活动时对新主动请求施加的准入条件。已有许可不因此结束。</summary>
    public readonly struct CharacterBehaviorGate
    {
        public CharacterBehaviorGateMode Mode { get; }
        public CharacterBehaviorId Behaviors { get; }

        public CharacterBehaviorGate(CharacterBehaviorGateMode mode, CharacterBehaviorId behaviors = CharacterBehaviorId.None)
        {
            Mode = mode;
            Behaviors = behaviors;
        }

        internal bool Admits(CharacterBehaviorId id)
        {
            bool listed = (Behaviors & id) != CharacterBehaviorId.None;
            return Mode == CharacterBehaviorGateMode.Open || (Mode == CharacterBehaviorGateMode.Whitelist && listed)
                || (Mode == CharacterBehaviorGateMode.Blacklist && !listed);
        }
    }

    /// <summary>当请求者与活动持有者互斥时，允许从指定阶段打断持有者。反向需要独立声明。</summary>
    public readonly struct CharacterBehaviorInterrupt
    {
        public CharacterBehaviorId Requester { get; }
        public CharacterBehaviorId Holder { get; }
        public CharacterBehaviorPhase AllowedPhases { get; }

        public CharacterBehaviorInterrupt(CharacterBehaviorId requester, CharacterBehaviorId holder,
            CharacterBehaviorPhase allowedPhases = CharacterBehaviorPhase.Any)
        {
            Requester = requester;
            Holder = holder;
            AllowedPhases = allowedPhases;
        }
    }

    /// <summary>声明行为的裁决关系。一份运行时行为实例只属于一个角色流程。</summary>
    public readonly struct CharacterBehaviorRule
    {
        public CharacterBehaviorId Id { get; }
        public CharacterBehavior Behavior { get; }
        public int Priority { get; }
        public CharacterBehaviorId Conflicts { get; }
        public bool ProvidesDisplacement { get; }
        public float OrdinarySpeedLimit { get; }
        public CharacterBehaviorGate Gate { get; }

        public CharacterBehaviorRule(CharacterBehaviorId id, CharacterBehavior behavior, int priority = 0,
            CharacterBehaviorId conflicts = CharacterBehaviorId.None, bool providesDisplacement = false,
            float ordinarySpeedLimit = float.PositiveInfinity, CharacterBehaviorGate gate = default)
        {
            Id = id;
            Behavior = behavior;
            Priority = priority;
            Conflicts = conflicts;
            ProvidesDisplacement = providesDisplacement;
            OrdinarySpeedLimit = ordinarySpeedLimit;
            Gate = gate;
        }

        internal bool ConflictsWith(CharacterBehaviorRule other)
        {
            return (ProvidesDisplacement && other.ProvidesDisplacement) || HasExplicitConflictWith(other);
        }

        internal bool HasExplicitConflictWith(CharacterBehaviorRule other) =>
            (Conflicts & other.Id) != CharacterBehaviorId.None || (other.Conflicts & Id) != CharacterBehaviorId.None;

        internal bool InterruptsMovementOnStart(CharacterBehaviorRule movement) =>
            HasExplicitConflictWith(movement) || (ProvidesDisplacement && !Behavior.SharesMovementOnStart);
    }

    /// <summary>维护行为的优先级、准入门控、互斥和有向打断规则。</summary>
    internal sealed class CharacterBehaviorRules
    {
        private readonly Dictionary<(CharacterBehaviorId, CharacterBehaviorId), CharacterBehaviorPhase> interrupts =
            new Dictionary<(CharacterBehaviorId, CharacterBehaviorId), CharacterBehaviorPhase>();
        private readonly Dictionary<CharacterBehaviorId, CharacterBehaviorRule> rulesById;

        public CharacterBehaviorRule Movement { get; }
        public CharacterBehaviorRule[] Actions { get; }
        internal CharacterBehaviorRule[] Participants { get; }

        // 校验装配提供的默认行为、优先队列与打断关系，再绑定各自的行为实例。
        internal CharacterBehaviorRules(CharacterBehaviorRule movement,
            CharacterBehaviorRule[] actions, CharacterBehaviorInterrupt[] interruptRules)
        {
            Movement = movement;
            Actions = actions;

            rulesById = new Dictionary<CharacterBehaviorId, CharacterBehaviorRule>(Actions.Length + 1)
            {
                [Movement.Id] = Movement
            };
            CharacterBehaviorId registered = Movement.Id;
            for (int i = 0; i < Actions.Length; i++)
            {
                CharacterBehaviorRule rule = Actions[i];
                if (!IsSingleId(rule.Id) || (registered & rule.Id) != CharacterBehaviorId.None)
                    throw new ArgumentException($"Invalid or duplicate behavior identifier: {rule.Id}.", nameof(actions));
                if (rule.Behavior == null || rule.Behavior.IsActive || rule.Behavior.IsBound
                    || ReferenceEquals(rule.Behavior, movement.Behavior) || SharesInstance(rule.Behavior, i))
                    throw new ArgumentException($"Behavior {rule.Id} needs its own inactive instance.", nameof(actions));

                registered |= rule.Id;
                rulesById.Add(rule.Id, rule);
            }

            ValidateRules(registered);
            if (interruptRules != null)
            {
                foreach (CharacterBehaviorInterrupt interrupt in interruptRules)
                {
                    if (interrupt.Requester == Movement.Id
                        || !rulesById.TryGetValue(interrupt.Requester, out CharacterBehaviorRule requester)
                        || !rulesById.TryGetValue(interrupt.Holder, out CharacterBehaviorRule holder)
                        || requester.Id == holder.Id || !requester.ConflictsWith(holder)
                        || interrupt.AllowedPhases == 0 || (interrupt.AllowedPhases & ~CharacterBehaviorPhase.Any) != 0)
                        throw new ArgumentException($"Invalid interrupt rule: {interrupt.Requester} -> {interrupt.Holder}.",
                            nameof(interruptRules));

                    var pair = (interrupt.Requester, interrupt.Holder);
                    if (interrupts.ContainsKey(pair))
                        throw new ArgumentException($"Duplicate interrupt rule: {interrupt.Requester} -> {interrupt.Holder}.",
                            nameof(interruptRules));
                    interrupts.Add(pair, interrupt.AllowedPhases);
                }
            }

            Array.Sort(Actions, ComparePriority);
            Participants = new CharacterBehaviorRule[Actions.Length + 1];
            Participants[0] = Movement;
            Array.Copy(Actions, 0, Participants, 1, Actions.Length);
            foreach (CharacterBehaviorRule rule in Participants)
                rule.Behavior.IsBound = true;
        }

        // 核对两个流程的完整编号集合、实际类型和运行状态隔离，验证期间不修改目标。
        internal void ValidateCopyTarget(CharacterBehaviorRules target)
        {
            if (Participants.Length != target.Participants.Length)
                throw new InvalidOperationException("预测副本的行为数量不匹配。");

            foreach (var rule in Participants)
            {
                if (!target.rulesById.TryGetValue(rule.Id, out var matching))
                    throw new InvalidOperationException($"预测副本缺少行为 {rule.Id}。");
                if (rule.Behavior.GetType() != matching.Behavior.GetType())
                    throw new InvalidOperationException($"预测副本的行为 {rule.Id} 类型不匹配。");
                if (ReferenceEquals(rule.Behavior, matching.Behavior))
                    throw new InvalidOperationException($"预测副本共享了行为 {rule.Id} 的实例。");
            }

            ValidateStorageIsolation(target);
            target.ValidateStorageIsolation(this);
        }

        // 检查已声明的可变容器在流程内外均由单一动作独占。
        private void ValidateStorageIsolation(CharacterBehaviorRules other)
        {
            for (int i = 0; i < Participants.Length; i++)
            {
                var owner = Participants[i];
                object storage = owner.Behavior.RuntimeStorageForCopy;
                if (storage == null) continue;

                for (int j = 0; j < i; j++)
                    if (ReferenceEquals(storage, Participants[j].Behavior.RuntimeStorageForCopy))
                        throw new InvalidOperationException($"行为 {owner.Id} 与 {Participants[j].Id} 共享可变运行容器。");
                foreach (var rule in other.Participants)
                    if (ReferenceEquals(storage, rule.Behavior.RuntimeStorageForCopy))
                        throw new InvalidOperationException($"行为 {owner.Id} 与另一流程的 {rule.Id} 共享可变运行容器。");
            }
        }

        // 预检通过后按注册编号将各参与者的状态复制到对应目标。
        internal void CopyParticipantStatesTo(CharacterBehaviorRules target)
        {
            foreach (var rule in Participants)
                rule.Behavior.ApplyRuntimeCopyTo(target.rulesById[rule.Id].Behavior);
        }

        // 将目标的全部运行状态与其预测定义绑定。
        internal void BindPredictionDefinitions()
        {
            foreach (var rule in Participants)
                rule.Behavior.RebindPredictionDefinition();
        }

        private void ValidateRules(CharacterBehaviorId registered)
        {
            for (int i = 0; i < Actions.Length; i++)
            {
                CharacterBehaviorRule rule = Actions[i];
                if (((rule.Conflicts | rule.Gate.Behaviors) & ~registered) != CharacterBehaviorId.None
                    || (rule.Conflicts & rule.Id) != CharacterBehaviorId.None)
                    throw new ArgumentException($"Invalid conflict or gate identifier for {rule.Id}.");
                if (rule.Gate.Mode != CharacterBehaviorGateMode.Open && rule.Gate.Mode != CharacterBehaviorGateMode.Blacklist
                    && rule.Gate.Mode != CharacterBehaviorGateMode.Whitelist)
                    throw new ArgumentException($"Unknown gate mode for {rule.Id}.");
                if (rule.Gate.Mode == CharacterBehaviorGateMode.Open && rule.Gate.Behaviors != CharacterBehaviorId.None)
                    throw new ArgumentException($"Open gate for {rule.Id} cannot carry a list.");
                if (rule.OrdinarySpeedLimit < 0f || float.IsNaN(rule.OrdinarySpeedLimit))
                    throw new ArgumentOutOfRangeException(nameof(rule.OrdinarySpeedLimit), $"Invalid speed limit for {rule.Id}.");

                for (int j = 0; j < i; j++)
                {
                    CharacterBehaviorRule other = Actions[j];
                    if (rule.Priority == other.Priority && (rule.ConflictsWith(other)
                        || !rule.Gate.Admits(other.Id) || !other.Gate.Admits(rule.Id)))
                        throw new ArgumentException($"Behaviors {rule.Id} and {other.Id} need distinct priorities.");
                }
            }
        }

        private bool SharesInstance(CharacterBehavior behavior, int index)
        {
            for (int i = 0; i < index; i++)
            {
                if (ReferenceEquals(behavior, Actions[i].Behavior))
                    return true;
            }
            return false;
        }

        private static bool IsSingleId(CharacterBehaviorId id)
        {
            ulong bits = (ulong)id;
            return bits != 0 && (bits & (bits - 1)) == 0;
        }

        private static int ComparePriority(CharacterBehaviorRule left, CharacterBehaviorRule right)
        {
            int priority = right.Priority.CompareTo(left.Priority);
            return priority != 0 ? priority : ((ulong)left.Id).CompareTo((ulong)right.Id);
        }

        // 检查活动行为的门控是否接受新许可。
        internal bool Admits(CharacterBehaviorId request, CharacterBehaviorId active)
        {
            foreach (CharacterBehaviorRule holder in Actions)
                if ((active & holder.Id) != CharacterBehaviorId.None && !holder.Gate.Admits(request))
                    return false;
            return true;
        }

        // 收集本次请求可打断的全部冲突许可，未全部通过时不修改任何行为。
        internal bool TryApprove(CharacterBehaviorRule request, bool fallbackPermit,
            in CharacterBehaviorStep step, out CharacterBehaviorId interrupted)
        {
            interrupted = CharacterBehaviorId.None;
            if (!Admits(request.Id, step.Active))
                return false;

            if (fallbackPermit && request.InterruptsMovementOnStart(Movement))
            {
                if (!CheckInterrupt(request.Id, Movement))
                    return false;
                interrupted |= Movement.Id;
            }

            foreach (CharacterBehaviorRule holder in Actions)
            {
                if (!step.IsActive(holder.Id) || !request.ConflictsWith(holder))
                    continue;
                // 本步刚获准的行为不会被后续请求打断。
                if ((step.Started & holder.Id) != CharacterBehaviorId.None || !CheckInterrupt(request.Id, holder))
                    return false;
                interrupted |= holder.Id;
            }
            return true;
        }

        // 检查打断关系及持有者所处阶段。
        private bool CheckInterrupt(CharacterBehaviorId requester, CharacterBehaviorRule holder) =>
            interrupts.TryGetValue((requester, holder.Id), out CharacterBehaviorPhase allowed) &&
            (holder.Behavior.Phase & allowed) != 0;
    }
}
