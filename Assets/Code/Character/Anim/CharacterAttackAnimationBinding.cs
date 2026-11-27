namespace ThreeC.CharacterBehavior
{
    // 只将具名招式的进入、后摇和退出边界翻译成带角色时刻的播放请求。
    internal sealed class CharacterAttackAnimationBinding
    {
        private readonly CharacterBehaviorAnimationBinding binding;
        private readonly CharacterBehaviorAnimationBindings animations;
        private CharacterActionPlayback playback;
        private long instance;
        private bool fading;

        // 绑定这一条上层动作映射，播放实例独立拥有自己的素材时钟。
        internal CharacterAttackAnimationBinding(CharacterBehaviorAnimationBinding binding, CharacterBehaviorAnimationBindings animations)
        {
            this.binding = binding;
            this.animations = animations;
        }

        // 依事件时刻先取消旧实例，再接入新招，后摇只发送一次淡出请求。
        internal void Observe(in CharacterBehaviorObservation frame, bool active, bool seed)
        {
            var step = frame.Behavior;
            var attack = step.Attack;
            bool matches = attack.StateId == binding.Rule.AttackStateId;
            if (playback != null && (!matches || attack.InstanceId != instance))
            {
                bool replaced = attack.StateId != 0 && attack.InstanceId != instance;
                var reason = (step.InterruptedBehaviors & CharacterBehaviorId.Attack) != 0 ? CharacterPlaybackEndSource.BehaviorInterrupted :
                    replaced ? CharacterPlaybackEndSource.ArbitrationLost : CharacterPlaybackEndSource.BehaviorWithdrawn;
                Cancel(replaced ? attack.StartedAt : frame.Time, CharacterActionPlaybackStatus.Interrupted, reason);
            }
            if (!matches) return;
            var presentation = attack.Presentation;
            if (attack.InstanceId != instance && (active || step.AttackMoveStarted))
            {
                instance = attack.InstanceId;
                fading = false;
                playback = animations.PlayRequest(binding.Rule.EntryAction, binding.Rule.Weight, binding.Origin,
                    seed ? CharacterPlaybackStartSource.Rebind : CharacterPlaybackStartSource.BehaviorGrant,
                    presentation.Play(attack.StartedAt), presentation.ClipId);
                if (seed && playback != null) playback.InitialPhase = playback.Clock.Sample(frame.Time).Phase;
            }
            if (playback == null) return;
            if (!fading && attack.Phase == CharacterBehaviorPhase.Recovery)
            {
                fading = true;
                playback.FadeOut(seed ? presentation.RecoveryAt(attack.StartedAt) : frame.Time, presentation.Recovery);
            }
            if (!active)
            {
                var interrupted = (step.InterruptedBehaviors & CharacterBehaviorId.Attack) != 0;
                var withdrawn = (step.WithdrawnBehaviors & CharacterBehaviorId.Attack) != 0;
                var reason = interrupted ? CharacterPlaybackEndSource.BehaviorInterrupted :
                    withdrawn ? CharacterPlaybackEndSource.BehaviorWithdrawn : CharacterPlaybackEndSource.BehaviorWindowEnded;
                double at = !interrupted && !withdrawn && attack.Duration > 0f && attack.ElapsedSeconds >= attack.Duration
                    ? attack.StartedAt + attack.Duration : frame.Time;
                Cancel(at, interrupted ? CharacterActionPlaybackStatus.Interrupted : CharacterActionPlaybackStatus.Withdrawn, reason);
            }
        }

        // 角色硬重置后丢弃旧招身份，新代次可以独立接收相同编号。
        internal void Reset()
        {
            instance = 0;
            fading = false;
            playback = null;
        }

        // 标记该请求的最后合法时刻，终态由播放端在结算真实采样后发布。
        internal void Cancel(double at, CharacterActionPlaybackStatus status, CharacterPlaybackEndSource source)
        {
            playback?.Cancel(at, status, source);
            playback = null;
        }
    }
}
