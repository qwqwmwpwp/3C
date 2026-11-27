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
        [Obsolete("请使用 Attack。")]
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

        internal CharacterBehaviorId Registered { get; }
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

            Registered = registered;
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
            ClaimStorage();
            foreach (CharacterBehaviorRule rule in Participants)
                rule.Behavior.IsBound = true;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, CharacterBehavior> owners =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, CharacterBehavior>();

        // 在所有真实和预演流程装配时检查独占容器；弱键不延长行为实例的生命周期。
        private void ClaimStorage()
        {
            var storage = new HashSet<object>(ReferenceIdentityComparer.Instance);
            lock (owners)
            {
                foreach (var rule in Participants)
                {
                    var item = rule.Behavior.RuntimeStorageForCopy;
                    if (item != null && (!storage.Add(item) || owners.TryGetValue(item, out _)))
                        throw new ArgumentException($"行为 {rule.Id} 复用了独占运行容器。");
                }
                foreach (var rule in Participants)
                {
                    var item = rule.Behavior.RuntimeStorageForCopy;
                    if (item != null) owners.Add(item, rule.Behavior);
                }
            }
        }

        // 使用引用身份识别不同流程共同持有的可变容器。
        private sealed class ReferenceIdentityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceIdentityComparer Instance = new ReferenceIdentityComparer();
            // 比较两个容器是否是同一对象。
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            // 返回与引用身份一致的哈希值。
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
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

        // 返回首个拒绝当前请求的活动门控，顺序与裁决顺序相同。
        internal CharacterBehaviorId GateBlocker(CharacterBehaviorId request, CharacterBehaviorId active)
        {
            foreach (var holder in Actions)
                if ((active & holder.Id) != 0 && !holder.Gate.Admits(request)) return holder.Id;
            return default;
        }

        // 收集整组可打断行为，首个无法通过的门控、互斥或阶段限制就是本次拒绝原因。
        internal bool TryApprove(CharacterBehaviorRule request, bool fallbackPermit, in CharacterBehaviorStep step,
            out CharacterBehaviorId interrupted, out CharacterBehaviorRejectReason reason, out CharacterBehaviorId blocker)
        {
            interrupted = default;
            blocker = GateBlocker(request.Id, step.Active);
            reason = CharacterBehaviorRejectReason.Gate;
            if (blocker != default) return false;
            if (fallbackPermit && request.InterruptsMovementOnStart(Movement))
            {
                blocker = Movement.Id;
                reason = InterruptReason(request.Id, Movement);
                if (reason != default) return false;
                interrupted |= Movement.Id;
            }
            foreach (var holder in Actions)
            {
                if (!step.IsActive(holder.Id) || !request.ConflictsWith(holder)) continue;
                blocker = holder.Id;
                reason = (step.Started & holder.Id) != 0 ? CharacterBehaviorRejectReason.Conflict : InterruptReason(request.Id, holder);
                if (reason != default) return false;
                interrupted |= holder.Id;
            }
            blocker = default;
            reason = default;
            return true;
        }

        // 缺少关系与持有者所处阶段不允许打断有不同的诊断原因。
        private CharacterBehaviorRejectReason InterruptReason(CharacterBehaviorId requester, CharacterBehaviorRule holder)
        {
            if (!interrupts.TryGetValue((requester, holder.Id), out var allowed)) return CharacterBehaviorRejectReason.Conflict;
            return (holder.Behavior.Phase & allowed) == 0 ? CharacterBehaviorRejectReason.PhaseRestriction : default;
        }
    }
}
