namespace ThreeC.CharacterBehavior
{
    // 维护持续许可从观察、提交进入动作到占有持续姿势的状态。
    internal sealed class CharacterGrantedAnimationState
    {
        private readonly CharacterBehaviorAnimationBinding binding;
        private readonly CharacterBehaviorAnimationBindings animations;
        private long observedGrant, currentGrant;
        private CharacterActionPlayback entry;
        private CharacterAnimationRequest hold;
        private CharacterPlaybackStartSource nextEntryStart;
        private CharacterPlaybackEndSource grantEnd;
        private bool interrupted;

        // 绑定持续映射与用于播放进入动作、提交持续姿势的合成器。
        internal CharacterGrantedAnimationState(CharacterBehaviorAnimationBinding binding, CharacterBehaviorAnimationBindings animations)
        {
            this.binding = binding;
            this.animations = animations;
        }

        // 记录许可边沿和当帧结束原因，等待动画采集时匹配已提交许可。
        internal void Observe(in CharacterBehaviorFrameResult step, bool active, bool began, bool rebind)
        {
            var id = binding.Rule.BehaviorId;
            if ((step.InterruptedBehaviors & id) != 0)
            {
                interrupted = true;
                grantEnd = CharacterPlaybackEndSource.BehaviorInterrupted;
            }
            else if ((step.WithdrawnBehaviors & id) != 0) grantEnd = CharacterPlaybackEndSource.BehaviorWithdrawn;
            if (active && rebind) nextEntryStart = CharacterPlaybackStartSource.Rebind;
            if (!active) observedGrant = 0;
            else if (began || observedGrant == 0) observedGrant = step.FrameNumber;
        }

        // 许可编号切换时终止旧进入动作和姿势；多帧观察同一许可继续使用原句柄。
        internal void UpdateGrant(bool valid, long guardGrant, CharacterPlaybackEndSource invalid)
        {
            long grant = binding.Rule.BehaviorId == CharacterBehaviorId.Guard ? guardGrant : observedGrant;
            if (!valid) grant = 0;
            if (grant != currentGrant)
            {
                var reason = interrupted ? CharacterActionPlaybackStatus.Interrupted : CharacterActionPlaybackStatus.Withdrawn;
                var source = !valid ? invalid : grantEnd == CharacterPlaybackEndSource.None
                    ? CharacterPlaybackEndSource.BehaviorWindowEnded : grantEnd;
                End(reason, source);
                currentGrant = grant;
            }
            interrupted = false;
            grantEnd = CharacterPlaybackEndSource.None;
        }

        // 每个许可先进入一次，只有明确的仲裁恢复配置才允许重播进入动作。
        internal void Submit()
        {
            if (currentGrant == 0 || hold.InstanceId != 0) return;
            var rule = binding.Rule;
            if (!string.IsNullOrWhiteSpace(rule.EntryAction))
            {
                if (entry?.WasPreempted == true && rule.EntryRecovery == CharacterGrantedEntryRecovery.ReplayEntry)
                {
                    if (animations.Composer.HasActive(rule.EntryAction)) return;
                    entry = null;
                    nextEntryStart = CharacterPlaybackStartSource.ArbitrationRecovery;
                }
                if (entry == null)
                {
                    var source = nextEntryStart == default ? CharacterPlaybackStartSource.BehaviorGrant : nextEntryStart;
                    if (!animations.TryPlayEntry(binding, binding.Origin, source, out entry)) return;
                    nextEntryStart = default;
                }
                if (entry == null || entry.ObservedStatus != CharacterActionPlaybackStatus.Completed && !entry.WasPreempted) return;
            }
            if (!string.IsNullOrWhiteSpace(rule.HoldAction)) hold = animations.SubmitHold(binding);
        }

        // 重绑只重新申请仍然待播或正在播放的进入动作。
        internal void Rebind()
        {
            if (currentGrant == 0) return;
            if (entry?.Status == CharacterActionPlaybackStatus.Pending || entry?.Status == CharacterActionPlaybackStatus.Started)
            {
                entry = null;
                nextEntryStart = CharacterPlaybackStartSource.Rebind;
            }
        }

        // 按旧实例撤回进入和持续需求，使结束回执归属旧许可。
        internal void End(CharacterActionPlaybackStatus reason, CharacterPlaybackEndSource source)
        {
            if (entry != null) animations.Composer.End(entry.Key, entry.InstanceId, reason, source);
            if (hold.InstanceId != 0) animations.Composer.Withdraw(hold.SemanticKey, hold.InstanceId);
            entry = null;
            hold = default;
        }

        // 已撤回当前许可后清空尚未提交的观察和重绑来源。
        internal void ResetObservation()
        {
            observedGrant = 0;
            nextEntryStart = default;
        }
    }
}
