namespace ThreeC.CharacterBehavior
{
    // 为持续许可先播放一次进入动作，然后按回执接入持续姿势。
    internal sealed class CharacterGrantedAnimationState
    {
        private readonly CharacterBehaviorAnimationMapping rule;
        private readonly CharacterBehaviorAnimationBindings owner;
        private CharacterBehaviorObservation origin;
        private bool active, attemptedEntry;
        private long appliedGuard;
        private CharacterPlaybackStartSource source;
        private CharacterActionPlayback entry;
        private CharacterAnimationRequest hold;

        // 固定该许可使用的进入、保持与恢复规则。
        internal CharacterGrantedAnimationState(CharacterBehaviorAnimationMapping rule, CharacterBehaviorAnimationBindings owner)
        {
            this.rule = rule;
            this.owner = owner;
        }

        // 许可边沿立即撤回旧请求，并保存下一次进入动作的准确来源。
        internal void Observe(in CharacterBehaviorObservation frame, bool granted, bool began, bool seed)
        {
            if (!granted || began) Clear(frame.Time);
            if (!granted || active) return;
            active = true;
            origin = frame;
            source = seed ? CharacterPlaybackStartSource.Rebind : CharacterPlaybackStartSource.BehaviorGrant;

        }

        // 已起播的进入动作以图发布的实际完成或抢占结果选择恢复方式。
        internal void Tick(long guardGrant)
        {
            if (!active) return;
            if (rule.BehaviorId == CharacterBehaviorId.Guard && appliedGuard != guardGrant)
            {
                Withdraw(null);
                appliedGuard = guardGrant;
            }
            if (rule.BehaviorId == CharacterBehaviorId.Guard && guardGrant == 0) return;
            if (hold.InstanceId != 0 && owner.Host.HasAction(hold.SemanticKey, hold.InstanceId)) return;
            hold = default;
            if (!string.IsNullOrWhiteSpace(rule.EntryAction))
            {
                if (entry != null && entry.EndSource == CharacterPlaybackEndSource.ArbitrationLost &&
                    rule.EntryRecovery == CharacterGrantedEntryRecovery.ReplayEntry)
                {
                    entry = null;
                    source = CharacterPlaybackStartSource.ArbitrationRecovery;
                }
                if (entry == null)
                {
                    var request = new CharacterOneShotRequest(rule.EntryAction, rule.Weight);
                    entry = owner.Play(request, attemptedEntry || source == CharacterPlaybackStartSource.ArbitrationRecovery ? (double?)null : origin.Time,
                        origin, rule, source);
                    attemptedEntry = true;
                }
                if (entry == null || entry.EndSource == CharacterPlaybackEndSource.None) return;
                if (entry.Status != CharacterActionPlaybackStatus.Completed && entry.EndSource != CharacterPlaybackEndSource.ArbitrationLost &&
                    entry.EndSource != CharacterPlaybackEndSource.WindowExpired)
                {
                    if (entry.EndSource == CharacterPlaybackEndSource.ResourceUnavailable || entry.EndSource == CharacterPlaybackEndSource.ModuleRemoved)
                    {
                        entry = null;
                        source = CharacterPlaybackStartSource.ArbitrationRecovery;
                    }
                    return;
                }
            }
            if (!string.IsNullOrWhiteSpace(rule.HoldAction)) hold = owner.Host.Hold(rule.HoldAction, rule.Weight);
        }

        // 以原句柄撤回本次许可正在使用的进入和持续动作。
        internal void Clear(double at)
        {
            Withdraw(at);
            active = false;
            appliedGuard = 0;
            attemptedEntry = false;
        }

        // 许可在采集或提交前失效时，同步撤销上一许可的实际姿势。
        private void Withdraw(double? at)
        {
            if (entry != null)
            {
                if (at.HasValue) owner.Host.Cancel(entry, at.Value);
                else owner.Host.Cancel(entry);
            }
            if (hold.InstanceId != 0) owner.Host.WithdrawAction(hold.SemanticKey, hold.InstanceId);
            hold = default;
            entry = null;
        }
    }
}
