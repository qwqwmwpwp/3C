using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>角色装配时在主线程捕获规则和工厂，行为集合在本次生命周期内固定。</summary>
    public abstract class CharacterBehaviorDefinition : ScriptableObject
    {
        /// <summary>提供一个单比特编号和独立实例工厂；数值可在主线程构造各副本时捕获。</summary>
        public abstract CharacterBehaviorDescriptor Capture();
    }

    /// <summary>声明行为能提供的运动，精确采样器必须冻结自己的区间数据。</summary>
    public enum CharacterBehaviorMotionCapability { None, StepDisplacement, ExactInterval }

    /// <summary>把固定的规则关系与主线程工厂一起交给实走、预演和后台预测。</summary>
    public sealed class CharacterBehaviorDescriptor
    {
        public CharacterBehaviorId Id { get; }
        private readonly Func<bool, CharacterBehavior> create;
        private readonly int priority;
        private readonly CharacterBehaviorId conflicts;
        private readonly CharacterBehaviorGate gate;
        private readonly float speedLimit;
        private readonly CharacterBehaviorMotionCapability motion;
        internal readonly CharacterBehaviorInterrupt[] Interrupts;

        /// <summary>工厂每次返回新实例；参数为 true 时须在此主线程调用内捕获所有 Unity 资源数值，之后只操作独占运行数据。</summary>
        public CharacterBehaviorDescriptor(CharacterBehaviorId id, Func<bool, CharacterBehavior> factory,
            int priority = 0, CharacterBehaviorId conflicts = default, CharacterBehaviorGate gate = default,
            CharacterBehaviorMotionCapability motion = default, float ordinarySpeedLimit = float.PositiveInfinity,
            params CharacterBehaviorInterrupt[] interrupts)
        {
            Id = id;
            create = factory ?? throw new ArgumentNullException(nameof(factory));
            this.priority = priority;
            this.conflicts = conflicts;
            this.gate = gate;
            this.motion = motion;
            speedLimit = ordinarySpeedLimit;
            Interrupts = (CharacterBehaviorInterrupt[])interrupts?.Clone() ?? Array.Empty<CharacterBehaviorInterrupt>();
        }

        // 在主线程创建本流程独占的行为，核对工厂产物与声明的精确位移能力。
        internal CharacterBehaviorRule Create(bool snapshot)
        {
            var behavior = create(snapshot);
            if (behavior == null || (uint)motion > (uint)CharacterBehaviorMotionCapability.ExactInterval ||
                (behavior is ICharacterIntervalMotion) != (motion == CharacterBehaviorMotionCapability.ExactInterval))
                throw new ArgumentException($"行为 {Id} 的工厂或运动能力无效。");
            return new CharacterBehaviorRule(Id, behavior, priority, conflicts,
                motion != CharacterBehaviorMotionCapability.None, speedLimit, gate);
        }
    }
}
