using System;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    /// <summary>按角色配置创建独立行为、默认许可和有向打断关系。</summary>
    internal sealed class CharacterBehaviorAssembly
    {
        internal CharacterBehaviorRules Rules { get; }
        internal CharacterBehaviorRuntimeState RuntimeState { get; }
        internal CharacterBehaviorLocomotion Locomotion { get; }

        internal static readonly CharacterBehaviorDescriptor Move = new CharacterBehaviorDescriptor(CharacterBehaviorId.Move,
            _ => new CharacterMoveBehavior(), motion: CharacterBehaviorMotionCapability.StepDisplacement);
        internal static readonly CharacterBehaviorDescriptor Dash = new CharacterBehaviorDescriptor(CharacterBehaviorId.Dash,
            _ => new CharacterDashBehavior(), priority: 100, motion: CharacterBehaviorMotionCapability.ExactInterval,
            interrupts: new[] { new CharacterBehaviorInterrupt(CharacterBehaviorId.Dash, CharacterBehaviorId.Move) });

        // 兼容直接传入实例的调用者，共用默认移动、冲刺及相同的最终装配验证。
        internal CharacterBehaviorAssembly(CharacterBehaviorRule[] additionalRules, CharacterBehaviorInterrupt[] interrupts)
        {
            var actions = new List<CharacterBehaviorRule> { Dash.Create(false) };
            if (additionalRules != null) actions.AddRange(additionalRules);
            var relations = new List<CharacterBehaviorInterrupt>(Dash.Interrupts);
            if (interrupts != null) relations.AddRange(interrupts);
            var movement = Move.Create(false);
            Rules = new CharacterBehaviorRules(movement, actions.ToArray(), relations.ToArray());
            RuntimeState = new CharacterBehaviorRuntimeState((CharacterDashBehavior)actions[0].Behavior);
            Locomotion = new CharacterBehaviorLocomotion(movement, (CharacterMoveBehavior)movement.Behavior);
        }

        // 在主线程按注册描述装配，各流程独占行为实例，可选的冻结攻击表仅供本次预测读取。
        private CharacterBehaviorAssembly(CharacterBehaviorRegistration registration, bool snapshot, CharacterAttackLookup sharedAttack)
        {
            var descriptors = registration.Descriptors;
            var movement = descriptors[0].Create(snapshot);
            var actions = new CharacterBehaviorRule[descriptors.Length - 1];
            var relations = new List<CharacterBehaviorInterrupt>(descriptors[0].Interrupts);
            for (int i = 1; i < descriptors.Length; i++)
            {
                actions[i - 1] = descriptors[i].Create(snapshot, sharedAttack);
                relations.AddRange(descriptors[i].Interrupts);
            }
            var dash = (CharacterDashBehavior)actions[0].Behavior;
            Rules = new CharacterBehaviorRules(movement, actions, relations.ToArray());
            RuntimeState = new CharacterBehaviorRuntimeState(dash);
            Locomotion = new CharacterBehaviorLocomotion(movement, (CharacterMoveBehavior)movement.Behavior);
        }

        // 保存完整装配，使真实、预演和预测沿用同一描述中的编号及关系。
        internal static CharacterBehaviorFlow ForCharacter(CharacterBehaviorRegistration registration, bool snapshot = false,
            CharacterAttackLookup sharedAttack = null) =>
            new CharacterBehaviorFlow(new CharacterBehaviorAssembly(registration, snapshot, sharedAttack), snapshot);
    }
}
