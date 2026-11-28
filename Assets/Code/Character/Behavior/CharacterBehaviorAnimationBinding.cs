namespace ThreeC.CharacterBehavior
{
    // 按映射触发方式为当前角色保存一种业务绑定。
    internal sealed class CharacterBehaviorAnimationBinding
    {
        internal readonly CharacterBehaviorAnimationMapping Rule;
        private readonly CharacterAttackAnimationBinding attack;
        private readonly CharacterGrantedAnimationState grant;
        private readonly CharacterPulseAnimationState pulse;

        // 只为指定的触发条件分配状态。
        internal CharacterBehaviorAnimationBinding(CharacterBehaviorAnimationMapping rule, CharacterBehaviorAnimationBindings owner)
        {
            Rule = rule;
            if (rule.IsAttackState) attack = new CharacterAttackAnimationBinding(rule, owner);
            else if (rule.Trigger == CharacterAnimationTrigger.WhileGranted) grant = new CharacterGrantedAnimationState(rule, owner);
            else pulse = new CharacterPulseAnimationState(rule, owner);
        }

        // 同一裁决中发生重入时，先交回旧实例的许可再投递新请求。
        internal void Observe(in CharacterBehaviorObservation frame, bool seed)
        {
            var step = frame.Behavior;
            var id = Rule.BehaviorId;
            bool active = id == CharacterBehaviorId.Move ? step.MoveApproved : (step.ActiveBehaviors & id) != 0;
            bool began = id == CharacterBehaviorId.Move ? step.MoveStarted : (step.StartedBehaviors & id) != 0;
            attack?.Observe(frame, active, seed);
            grant?.Observe(frame, active, began, seed);
            pulse?.Observe(frame, active, began, seed);
        }

        // 只对仍然获准的持续动作交接进入姿势。
        internal void Tick(long guardGrant) => grant?.Tick(guardGrant);

        // 撤回持有的句柄并清除所有业务身份。
        internal void Clear(double at)
        {
            attack?.Clear(at);
            grant?.Clear(at);
            pulse?.Clear(at);
        }
    }
}
