using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 维护普通单次映射的许可、阶段与对应的播放句柄。
    internal sealed class CharacterPulseAnimationState
    {
        private readonly CharacterBehaviorAnimationBinding binding;
        private readonly CharacterBehaviorAnimationBindings animations;
        private CharacterActionPlayback entry;
        private CharacterActionHandoff handoff;
        private float? moveStart, firstPose;
        private float lastPose, duration;
        private bool observedActive, timedActive, resumeTimed, resumedEntry;
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

        // 为自定义交接的非攻击动作记录首次获准移动时刻。
        private void SetConfiguredHandoff(bool moveApproved, bool entering)
        {
            var settings = binding.HandoffSettings;
            if (entering) moveStart = null;
            if (moveApproved && !moveStart.HasValue && lastPose >= settings.ReleaseFrom)
                moveStart = Mathf.Max(firstPose.GetValueOrDefault(), settings.ReleaseFrom);
            handoff = settings.Resolve(duration, moveStart: moveStart);
        }

        // 新的许可可申请播放；同一执行同一素材的续绑保留原句柄及其终态。
        internal void Start(CharacterPlaybackOrigin origin, CharacterPlaybackStartSource source)
        {
            resumeTimed = false;
            if (source == CharacterPlaybackStartSource.Rebind && animations.SameClip(entry) &&
                entry.StateVersion == origin.StateVersion && entry.ExecutionId == origin.Execution) return;
            resumedEntry = source == CharacterPlaybackStartSource.Rebind;
            animations.TryPlayEntry(binding, origin, source, out entry);
        }

        // 为旧式单次映射交付行为相位与移动交接。
        internal void Submit()
        {
            if (resumeTimed && observedActive) Start(binding.Origin, CharacterPlaybackStartSource.Rebind);
            resumeTimed = false;
            if (entry != null && firstPose.HasValue && duration > 0f)
                animations.Composer.Sample(entry, firstPose, Mathf.Clamp01(lastPose), handoff,
                    binding.HandoffSettings?.StartAtZero == true, resumedEntry);
        }

        // 在资源重绑前为可恢复动作排入一次申请，已结束的旧句柄不再续播。
        internal void Rebind()
        {
            if (!animations.SameClip(entry) && observedActive && timedActive && (entry == null || entry.ObservedStatus == CharacterActionPlaybackStatus.Pending ||
                entry.ObservedStatus == CharacterActionPlaybackStatus.Started))
            {
                entry = null;
                resumeTimed = true;
            }
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
            resumedEntry = resumeTimed = false;
        }

        // 在模拟代次切换后丢弃旧招式的时间窗与恢复意图。
        internal void ResetObservation()
        {
            observedActive = false;
            resumedEntry = false;
            firstPose = null;
            handoff = default;
            moveStart = null;
            resumeTimed = timedActive = false;
        }
    }
}
