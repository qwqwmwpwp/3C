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

    /// <summary>在角色创建时校验并排列规则。固定步只读取这些数据。</summary>
    internal sealed class CharacterBehaviorRules
    {
        private readonly Dictionary<(CharacterBehaviorId, CharacterBehaviorId), CharacterBehaviorPhase> interrupts =
            new Dictionary<(CharacterBehaviorId, CharacterBehaviorId), CharacterBehaviorPhase>();

        public CharacterBehaviorRule Movement { get; }
        public CharacterBehaviorRule[] Actions { get; }

        public CharacterBehaviorRules(CharacterMoveBehavior movement, CharacterDashBehavior dash,
            CharacterBehaviorRule[] additionalRules, CharacterBehaviorInterrupt[] interruptRules)
        {
            Movement = new CharacterBehaviorRule(CharacterBehaviorId.Move, movement, providesDisplacement: true);
            int additionalCount = additionalRules?.Length ?? 0;
            if (additionalCount > 62)
                throw new ArgumentException("Too many behavior identifiers.", nameof(additionalRules));

            Actions = new CharacterBehaviorRule[additionalCount + 1];
            Actions[0] = new CharacterBehaviorRule(CharacterBehaviorId.Dash, dash, 100, providesDisplacement: true);
            if (additionalCount > 0)
                Array.Copy(additionalRules, 0, Actions, 1, additionalCount);

            var rulesById = new Dictionary<CharacterBehaviorId, CharacterBehaviorRule>(Actions.Length + 1)
            {
                [Movement.Id] = Movement
            };
            CharacterBehaviorId registered = Movement.Id;
            for (int i = 0; i < Actions.Length; i++)
            {
                CharacterBehaviorRule rule = Actions[i];
                if (!IsSingleId(rule.Id) || (registered & rule.Id) != CharacterBehaviorId.None)
                    throw new ArgumentException($"Invalid or duplicate behavior identifier: {rule.Id}.", nameof(additionalRules));
                if (rule.Behavior == null || rule.Behavior.IsActive || rule.Behavior.IsBound
                    || ReferenceEquals(rule.Behavior, movement) || SharesInstance(rule.Behavior, i))
                    throw new ArgumentException($"Behavior {rule.Id} needs its own inactive instance.", nameof(additionalRules));

                registered |= rule.Id;
                rulesById.Add(rule.Id, rule);
            }

            ValidateRules(registered);
            // 冲刺显式拥有从基础移动接管位移的权限。
            interrupts.Add((CharacterBehaviorId.Dash, CharacterBehaviorId.Move), CharacterBehaviorPhase.Any);
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
            movement.IsBound = true;
            foreach (CharacterBehaviorRule rule in Actions)
                rule.Behavior.IsBound = true;
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

        public CharacterBehaviorRejection CheckInterrupt(CharacterBehaviorId requester, CharacterBehaviorRule holder)
        {
            if (!interrupts.TryGetValue((requester, holder.Id), out CharacterBehaviorPhase allowed))
                return CharacterBehaviorRejection.Conflict;

            return (holder.Behavior.Phase & allowed) != 0
                ? CharacterBehaviorRejection.None
                : CharacterBehaviorRejection.InterruptPhaseDenied;
        }
    }
}
