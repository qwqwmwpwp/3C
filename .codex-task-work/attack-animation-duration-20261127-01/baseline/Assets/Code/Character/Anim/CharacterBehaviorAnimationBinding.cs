namespace ThreeC.CharacterBehavior
{
    // 固定一条映射的规则与行为身份，只创建该触发方式使用的播放状态。
    internal sealed class CharacterBehaviorAnimationBinding
    {
        internal CharacterBehaviorAnimationMapping Source;
        internal readonly CharacterBehaviorAnimationMapping Rule;
        internal readonly CharacterActionHandoffSettings HandoffSettings;
        internal readonly CharacterGrantedAnimationState Grant;
        internal readonly CharacterPulseAnimationState Pulse;
        internal bool IsPulse => Rule.Trigger == CharacterAnimationTrigger.OnStart;
        internal bool Removed, Reported;
        internal CharacterPlaybackOrigin Origin { get; private set; }

        // 冻结资源与交还规则，区分持续许可与单次触发的状态归属。
        internal CharacterBehaviorAnimationBinding(CharacterBehaviorAnimationMapping rule, CharacterActionHandoffSettings settings,
            CharacterBehaviorAnimationBindings animations)
        {
            Source = rule;
            Rule = rule.Snapshot();
            HandoffSettings = settings?.Snapshot();
            if (IsPulse) Pulse = new CharacterPulseAnimationState(this, animations);
            else Grant = new CharacterGrantedAnimationState(this, animations);
        }

        // 用当步身份与许可边沿更新当前触发方式的状态。
        internal void Observe(int stateVersion, in CharacterBehaviorObservation frame, bool reconnect, bool seed)
        {
            var id = Rule.BehaviorId;
            var step = frame.Behavior;
            step.TryGetExecution(id, out var execution);
            long move = id == CharacterBehaviorId.Attack ? step.Attack.InstanceId : 0;
            Origin = new CharacterPlaybackOrigin(stateVersion, frame.SimulationVersion, id, execution.Instance, execution.RequestId, move);
            bool active = id == CharacterBehaviorId.Move ? step.MoveApproved : (step.ActiveBehaviors & id) != 0;
            bool began = id == CharacterBehaviorId.Move ? step.MoveStarted : (step.StartedBehaviors & id) != 0;
            if (IsPulse) Pulse.Observe(step, frame.DeltaTime, active, began, reconnect, seed);
            else Grant.Observe(step, active, began, reconnect && (seed || !began));
        }

        // 在旧代播放已经撤回后清除身份与观察数据。
        internal void ResetObservation()
        {
            Origin = default;
            if (IsPulse) Pulse.ResetObservation();
            else Grant.ResetObservation();
        }
    }
}
