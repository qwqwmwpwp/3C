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

        // 组合内置行为、调用方提供的行为和打断关系，并绑定共享状态。
        internal CharacterBehaviorAssembly(CharacterBehaviorRule[] additionalRules, CharacterBehaviorInterrupt[] interrupts)
        {
            var movement = new CharacterMoveBehavior();
            var dash = new CharacterDashBehavior();
            var fallback = new CharacterBehaviorRule(CharacterBehaviorId.Move, movement, providesDisplacement: true);

            int additionalCount = additionalRules?.Length ?? 0;
            if (additionalCount > 62)
                throw new ArgumentException("Too many behavior identifiers.", nameof(additionalRules));
            var actions = new CharacterBehaviorRule[additionalCount + 1];
            actions[0] = new CharacterBehaviorRule(CharacterBehaviorId.Dash, dash, 100, providesDisplacement: true);
            if (additionalCount > 0)
                Array.Copy(additionalRules, 0, actions, 1, additionalCount);

            int interruptCount = interrupts?.Length ?? 0;
            var interruptRules = new CharacterBehaviorInterrupt[interruptCount + 1];
            interruptRules[0] = new CharacterBehaviorInterrupt(CharacterBehaviorId.Dash, CharacterBehaviorId.Move);
            if (interruptCount > 0)
                Array.Copy(interrupts, 0, interruptRules, 1, interruptCount);

            Rules = new CharacterBehaviorRules(fallback, actions, interruptRules);
            RuntimeState = new CharacterBehaviorRuntimeState(dash);
            Locomotion = new CharacterBehaviorLocomotion(fallback, movement);
        }

        // 按角色方案声明动作和打断权限，预测方案独立保留攻击定义与接续表。
        internal static CharacterBehaviorFlow ForCharacter(CharacterBehaviorRegistration registration, bool snapshot = false)
        {
            var rules = new List<CharacterBehaviorRule>();
            var interrupts = new List<CharacterBehaviorInterrupt>();
            if (registration.Guard)
            {
                rules.Add(new CharacterBehaviorRule(CharacterBehaviorId.Guard, new CharacterGuardBehavior(),
                    conflicts: CharacterBehaviorId.Dash));
                if (registration.DashInterruptsGuard)
                    interrupts.Add(new CharacterBehaviorInterrupt(CharacterBehaviorId.Dash, CharacterBehaviorId.Guard));
            }
            if (registration.Attack != null)
            {
                var combo = new CharacterAttackBehavior(snapshot ? registration.Attack.Snapshot() : registration.Attack,
                    snapshot ? registration.Routes.Snapshot() : registration.Routes);
                rules.Add(new CharacterBehaviorRule(CharacterBehaviorId.Attack, combo,
                    priority: 80, conflicts: registration.Guard ? CharacterBehaviorId.Guard : CharacterBehaviorId.None,
                    providesDisplacement: true));
                if (registration.Guard && registration.AttackInterruptsGuard)
                    interrupts.Add(new CharacterBehaviorInterrupt(CharacterBehaviorId.Attack, CharacterBehaviorId.Guard));
                var phases = registration.DashInterruptPhases;
                if (phases != 0)
                    interrupts.Add(new CharacterBehaviorInterrupt(CharacterBehaviorId.Dash, CharacterBehaviorId.Attack, phases));
                interrupts.Add(new CharacterBehaviorInterrupt(CharacterBehaviorId.Attack, CharacterBehaviorId.Move));
            }
            return new CharacterBehaviorFlow(rules.ToArray(), interrupts.ToArray());
        }
    }
}
