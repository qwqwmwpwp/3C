using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 将一个招式实例的进入、后摇和结束时刻翻译为通用播放命令。
    internal sealed class CharacterAttackAnimationBinding
    {
        private readonly CharacterBehaviorAnimationMapping rule;
        private readonly CharacterBehaviorAnimationBindings owner;
        private CharacterActionPlayback playback;
        private long instance;
        private bool fading;

        // 绑定角色自己的招式规则和动画宿主。
        internal CharacterAttackAnimationBinding(CharacterBehaviorAnimationMapping rule, CharacterBehaviorAnimationBindings owner)
        {
            this.rule = rule;
            this.owner = owner;
        }

        // 每条裁决都在原时刻投递，即使一帧中先后发生多次接招也保留各自的结束边界。
        internal void Observe(in CharacterBehaviorObservation frame, bool active, bool seed)
        {
            var step = frame.Behavior;
            var attack = step.Attack;
            bool matches = attack.StateId == rule.AttackStateId;
            if (!matches || instance != attack.InstanceId)
                Clear(attack.StateId != 0 && instance != attack.InstanceId ? attack.StartedAt : frame.Time);
            if (!matches) return;
            var presentation = attack.Presentation;
            if (instance != attack.InstanceId && (active || step.AttackMoveStarted) && presentation.Valid)
            {
                instance = attack.InstanceId;
                double start = seed ? Math.Max(frame.Time, attack.StartedAt) : attack.StartedAt;
                float elapsed = (float)(start - attack.StartedAt);
                float remaining = presentation.AnimationSeconds - elapsed;
                if (remaining <= 0f) return;
                var request = new CharacterOneShotRequest(rule.EntryAction, rule.Weight,
                    Mathf.Clamp01(elapsed / presentation.AnimationSeconds), remaining, Mathf.Max(0f, presentation.Entry - elapsed));
                playback = owner.Play(request, start, frame, rule,
                    seed ? CharacterPlaybackStartSource.Rebind : CharacterPlaybackStartSource.BehaviorGrant);
            }
            if (playback == null) return;
            if (!fading && attack.Phase == CharacterBehaviorPhase.Recovery)
            {
                fading = true;
                owner.Host.FadeOut(playback, presentation.RecoveryAt(attack.StartedAt), presentation.Recovery);
            }
            if (!active)
            {
                bool interrupted = ((step.InterruptedBehaviors | step.WithdrawnBehaviors) & CharacterBehaviorId.Attack) != 0;
                double end = !interrupted && attack.Duration > 0f && attack.ElapsedSeconds >= attack.Duration
                    ? attack.StartedAt + attack.Duration : frame.Time;
                owner.Host.Cancel(playback, end);
                playback = null;
            }
        }

        // 结束旧回执并放弃这一次招式身份。
        internal void Clear(double at)
        {
            if (playback != null) owner.Host.Cancel(playback, at);
            playback = null;
            instance = 0;
            fading = false;
        }
    }
}
