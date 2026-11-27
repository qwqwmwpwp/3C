using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 维护一条瞬发映射的行为时钟、招式身份、播放句柄与重绑游标。
    internal sealed class CharacterPulseAnimationState
    {
        private readonly CharacterBehaviorAnimationBinding binding;
        private readonly CharacterBehaviorAnimationBindings animations;
        private CharacterActionPlayback entry;
        private CharacterActionHandoff handoff;
        private float? moveStart, firstPose;
        private float lastPose, duration;
        private bool observedActive, timedActive, resumeTimed, resumedEntry;
        private long attackInstance, entryAttackInstance;
        private float attackEntryClipPhase;
        private CharacterPlaybackOrigin attackOrigin; // 最近一次匹配本映射的招式，空行为帧不会覆盖待补的尾段。
        private double attackElapsed;

        // 将瞬发状态绑定到冻结的配置和全局有序提交队列。
        internal CharacterPulseAnimationState(CharacterBehaviorAnimationBinding binding, CharacterBehaviorAnimationBindings animations)
        {
            this.binding = binding;
            this.animations = animations;
        }

        // 按单步先结束后开始的次序冻结播放边沿，并维护本次行为时钟。
        internal void Observe(in CharacterBehaviorFrameResult step, float deltaTime, bool active, bool began, bool reconnect, bool seed)
        {
            var id = binding.Rule.BehaviorId;
            observedActive = active;
            // 同一步可能先释放旧许可、再批准新实例；自然结束保留开始事件。
            if (!seed && (step.InterruptedBehaviors & id) != 0)
                animations.QueueEdge(binding, CharacterActionPlaybackStatus.Interrupted, end: CharacterPlaybackEndSource.BehaviorInterrupted);
            else if (!seed && (step.WithdrawnBehaviors & id) != 0)
                animations.QueueEdge(binding, CharacterActionPlaybackStatus.Withdrawn, end: CharacterPlaybackEndSource.BehaviorWithdrawn);
            if (id == CharacterBehaviorId.Attack && binding.Rule.AttackStateId != 0)
            {
                ObserveAttack(step, active, reconnect, seed);
                return;
            }
            if (binding.HandoffSettings?.OverridePhases == true)
            {
                ObserveConfigured(step, deltaTime, active, began, reconnect, seed);
                return;
            }
            bool previouslyTimed = timedActive;
            if (began) firstPose = null;
            if (step.DisplacementProvider == id)
            {
                firstPose = step.ActionMotion.StartPhase;
                lastPose = step.ActionMotion.EndPhase;
                duration = step.ActionMotion.Duration;
            }
            timedActive = active && step.DisplacementProvider == id && firstPose.HasValue;
            if ((!seed && began) || (reconnect && timedActive))
            {
                resumeTimed = false;
                animations.QueueEdge(binding, CharacterActionPlaybackStatus.Pending, reconnect && !began || seed
                    ? CharacterPlaybackStartSource.Rebind : CharacterPlaybackStartSource.BehaviorGrant);
            }
            if (!seed && !active && (step.EndedBehaviors & id) != 0 &&
                (previouslyTimed || firstPose.HasValue))
                animations.QueueEdge(binding, CharacterActionPlaybackStatus.Completed);
        }

        // 招式起止相位来自当帧行为时间，位移权限和动画淡入都不会重置时钟。
        private void ObserveAttack(in CharacterBehaviorFrameResult step, bool active, bool reconnect, bool seed)
        {
            bool matches = step.Attack.StateId == binding.Rule.AttackStateId;
            timedActive = matches && active && step.Attack.Handoff.IsAttack &&
                step.Attack.ElapsedSeconds < step.Attack.Handoff.Duration && step.Attack.ElapsedSeconds < step.Attack.Handoff.AnimationDuration;
            bool revoked = ((step.InterruptedBehaviors | step.WithdrawnBehaviors) & CharacterBehaviorId.Attack) != 0;
            if (!active && revoked)
            {
                attackOrigin = default;
                resumeTimed = false;
                return;
            }
            if (!matches)
            {
                if (step.Attack.StateId != 0) attackOrigin = default;
                if (step.AttackMoveStarted)
                {
                    resumeTimed = false;
                    if (!seed) animations.QueueEdge(binding, CharacterActionPlaybackStatus.Interrupted,
                        end: CharacterPlaybackEndSource.ArbitrationLost);
                }
                return;
            }
            long instance = step.Attack.InstanceId;
            bool newInstance = instance != 0 && attackInstance != instance;
            if (newInstance)
                attackEntryClipPhase = step.Attack.Handoff.IsAttack ? step.Attack.Handoff.ClipProgress(step.Attack.StartElapsed) : 0f;
            attackOrigin = binding.Origin;
            attackInstance = instance;
            firstPose = step.Attack.Handoff.IsAttack ? step.Attack.Handoff.ClipProgress(step.Attack.StartElapsed) : (float?)null;
            lastPose = step.Attack.Progress;
            duration = step.Attack.Duration;
            handoff = step.Attack.Handoff;
            attackElapsed = step.Attack.ElapsedSeconds;
            if (reconnect && (seed || !step.AttackMoveStarted || !newInstance))
            {
                resumeTimed = CanRecover();
                return;
            }
            if (!active)
            {
                resumeTimed = resumeTimed && CanRecover();
                if (!seed && step.AttackMoveStarted) animations.QueueEdge(binding, CharacterActionPlaybackStatus.Pending);
                // 自然结束保留最后的采样需求，图求值后才能确认片段完成；未获选实例由 ReleaseExpired 撤回。
            }
            else if (newInstance || step.AttackMoveStarted)
            {
                resumeTimed = false;
                animations.QueueEdge(binding, CharacterActionPlaybackStatus.Pending, seed
                    ? CharacterPlaybackStartSource.Rebind : CharacterPlaybackStartSource.BehaviorGrant);
            }
        }

        // 用当帧行为时钟或配置的时长推进单次动作。
        private void ObserveConfigured(in CharacterBehaviorFrameResult step, float deltaTime, bool active,
            bool began, bool reconnect, bool seed)
        {
            bool entering = began || reconnect;
            bool wasActive = timedActive;
            timedActive = active;
            if (!active && !began && !wasActive) return;
            bool hasClock = binding.Rule.BehaviorId != CharacterBehaviorId.Move &&
                step.DisplacementProvider == binding.Rule.BehaviorId && step.ActionMotion.StartPhase.HasValue &&
                step.ActionMotion.Duration > 0f;
            if (hasClock)
            {
                firstPose = step.ActionMotion.StartPhase;
                lastPose = step.ActionMotion.EndPhase;
                duration = step.ActionMotion.Duration;
            }
            else
            {
                if (entering || !wasActive) duration = binding.HandoffSettings.Duration;
                float age = step.GrantAgeAtStepStart(binding.Rule.BehaviorId);
                firstPose = Mathf.Clamp01(age / duration);
                lastPose = Mathf.Clamp01((age + deltaTime) / duration);
            }
            SetConfiguredHandoff(step.MoveApproved, entering);
            if (entering && (active || !seed && began))
                animations.QueueEdge(binding, CharacterActionPlaybackStatus.Pending, (seed || reconnect && !began)
                    ? CharacterPlaybackStartSource.Rebind : CharacterPlaybackStartSource.BehaviorGrant);
            if (!active && !seed && (step.InterruptedBehaviors & binding.Rule.BehaviorId) == 0 &&
                (step.WithdrawnBehaviors & binding.Rule.BehaviorId) == 0)
                animations.QueueEdge(binding, CharacterActionPlaybackStatus.Completed);
        }

        // 恢复窗口内的播放，或补齐实际游标到状态截止与素材终点中较早者的末段。
        private bool CanRecover()
        {
            if (!binding.Rule.IsAttackState) return observedActive && timedActive;
            if (attackOrigin.Move == 0 || !handoff.IsAttack) return false;
            var cursor = animations.ReadAttackCursor(attackOrigin, binding.Rule.EntryAction);
            float cutoff = Mathf.Min(handoff.Duration, handoff.AnimationDuration);
            if (cursor.HasValue && cursor.Value.normalizedTime >= handoff.ClipProgress(cutoff)) return false;
            return timedActive || cursor.HasValue && attackElapsed >= cutoff;
        }

        // 为自定义交接的非攻击动作记录首次获准移动时刻。
        private void SetConfiguredHandoff(bool moveApproved, bool entering)
        {
            var settings = binding.HandoffSettings;
            if (entering) moveStart = null;
            if (moveApproved && !moveStart.HasValue && lastPose >= settings.ReleaseFrom)
                moveStart = Mathf.Max(firstPose.GetValueOrDefault(), settings.ReleaseFrom);
            handoff = settings.Resolve(duration, moveStart: moveStart);
        }

        // 收到开始边沿时使用入队时的招式身份，真实开始优先于同帧续播。
        internal void Start(CharacterPlaybackOrigin origin, CharacterPlaybackStartSource source)
        {
            resumeTimed = false;
            resumedEntry = source == CharacterPlaybackStartSource.Rebind && origin.Move != 0;
            if (binding.Rule.IsAttackState && origin.Move == attackInstance &&
                (!handoff.Valid || resumedEntry && !CanRecover()))
            {
                End(CharacterActionPlaybackStatus.Withdrawn, CharacterPlaybackEndSource.BehaviorWindowEnded);
                return;
            }
            entryAttackInstance = origin.Move;
            if (animations.TryPlayEntry(binding, origin, source, out entry) && binding.Rule.IsAttackState && resumedEntry && entry != null)
                entry.ResumeCursor = animations.ReadAttackCursor(origin, binding.Rule.EntryAction);
        }

        // 按当前时钟续播或向合成器提交同一招式的采样范围。
        internal void Submit()
        {
            if (resumeTimed && CanRecover())
                Start(binding.Rule.IsAttackState ? attackOrigin : binding.Origin, CharacterPlaybackStartSource.Rebind);
            resumeTimed = false;
            if (binding.Rule.IsAttackState && (attackInstance == 0 || entryAttackInstance != attackInstance)) return;
            if (entry != null && firstPose.HasValue && (attackInstance != 0 || duration > 0f))
            {
                float phase = Mathf.Clamp01(lastPose);
                float? start = firstPose;
                if (handoff.IsAttack)
                    start = resumedEntry ? (entry.ResumeCursor?.normalizedTime ?? firstPose.Value) : attackEntryClipPhase;
                animations.Composer.Sample(entry, start, phase, handoff,
                    binding.HandoffSettings?.StartAtZero == true, resumedEntry, attackElapsed);
            }
        }

        // 在资源重绑前为可恢复动作排入一次申请，已结束的旧句柄不再续播。
        internal void Rebind()
        {
            if (CanRecover() && (entry == null || entry.ObservedStatus == CharacterActionPlaybackStatus.Pending ||
                entry.ObservedStatus == CharacterActionPlaybackStatus.Started))
            {
                entry = null;
                resumeTimed = true;
            }
        }

        // 暂停撤回句柄，恢复资格根据角色时间与独立保存的实际求值位置判断。
        internal void Suspend(CharacterPlaybackEndSource source)
        {
            End(CharacterActionPlaybackStatus.Interrupted, source);
            resumeTimed = CanRecover();
        }

        // 撤回时间窗已过且尚未获部位的播放请求。
        internal void ReleaseExpired()
        {
            if (firstPose.HasValue && !timedActive && entry?.Status == CharacterActionPlaybackStatus.Pending)
                End(CharacterActionPlaybackStatus.Withdrawn, CharacterPlaybackEndSource.BehaviorWindowEnded);
        }

        // 结束当前句柄并解除它与招式的绑定，保留已提交回执。
        internal void End(CharacterActionPlaybackStatus reason, CharacterPlaybackEndSource source)
        {
            if (entry != null) animations.Composer.End(entry.Key, entry.InstanceId, reason, source);
            entry = null;
            entryAttackInstance = 0;
            resumedEntry = resumeTimed = false;
        }

        // 在模拟代次切换后丢弃旧招式的时间窗与恢复意图。
        internal void ResetObservation()
        {
            observedActive = false;
            attackInstance = entryAttackInstance = 0;
            attackOrigin = default;
            resumedEntry = false;
            firstPose = null;
            handoff = default;
            moveStart = null;
            resumeTimed = timedActive = false;
        }
    }
}
