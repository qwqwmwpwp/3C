using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 维护一条行为映射的许可、播放归属及可续播的当前相位。
    internal sealed class CharacterBehaviorAnimationBinding
    {
        public CharacterBehaviorAnimationMapping Source, Rule;
        public bool Removed;
        public CharacterActionHandoffSettings HandoffSettings;
        public long ObservedGrant, CurrentGrant;
        public bool IsPulse => CurrentGrant == 0 && Rule.Trigger == CharacterAnimationTrigger.OnStart;
        public CharacterAnimationRequest Hold;
        public string EntryAction, HoldAction;
        public float GrantWeight;
        public CharacterGrantedEntryRecovery EntryRecovery;
        public bool Interrupted, Reported;
        public CharacterActionPlayback Entry;
        public CharacterActionHandoff Handoff;
        public float? MoveStart;
        public float? FirstPose;
        public float LastPose, Duration;
        public bool TimedActive, ResumeTimed, ResumedEntry;
        public long AttackInstance, EntryAttackInstance;
        public float AttackPhase;
        public float AttackEntryPhase;

        internal CharacterPlaybackOrigin Origin;
        internal CharacterPlaybackStartSource NextEntryStart;
        internal CharacterPlaybackEndSource GrantEnd;
        internal bool ObservedActive;

        // 固定本次行为帧中的请求、执行和招式身份，入队边沿随后独立保留这份值。
        internal void CaptureOrigin(int stateVersion, in CharacterBehaviorObservation frame)
        {
            var id = Rule.BehaviorId;
            frame.Behavior.TryGetExecution(id, out var execution);
            long move = id == CharacterBehaviorId.Attack ? frame.Behavior.Attack.InstanceId : 0;
            Origin = new CharacterPlaybackOrigin(stateVersion, frame.SimulationVersion, id, execution.Instance, execution.RequestId, move);
        }

        // 只从确实开始的同一招式保留游标，重绑时跨过已发事件。
        public void RememberAttackCursor()
        {
            if (AttackInstance == 0 || EntryAttackInstance != AttackInstance || Entry?.HasStarted != true) return;
            AttackPhase = Mathf.Max(AttackPhase, Mathf.Clamp01(Entry.LastObservedPhase));
            if (AttackPhase == 0f) AttackPhase = 0.000001f; // 首帧事件已交付，恢复点越过零时刻。
        }

        // 将本次行为相位和移动许可转换成动作交还权重。
        internal void SetHandoff(CharacterActionHandoff source, bool moveApproved, bool entering)
        {
            var settings = HandoffSettings;
            if (entering) MoveStart = null;
            if (!source.UsesRouteBlend && settings?.OverridePhases == true)
            {
                float releaseFrom = source.Valid ? Mathf.Max(source.ReleaseFrom, settings.ReleaseFrom) : settings.ReleaseFrom;
                if (moveApproved && !MoveStart.HasValue && LastPose >= releaseFrom)
                    MoveStart = Mathf.Max(FirstPose.GetValueOrDefault(), releaseFrom);
                source = settings.Resolve(Duration, source, MoveStart);
            }
            Handoff = source;
        }
    }
}
