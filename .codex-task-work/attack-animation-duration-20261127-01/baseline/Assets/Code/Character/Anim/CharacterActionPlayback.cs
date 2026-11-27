namespace ThreeC.CharacterBehavior
{
    // 表示单次播放从排队到实际下发及结束的状态。
    public enum CharacterActionPlaybackStatus { Pending, Started, Completed, Interrupted, Withdrawn }

    // 区分新的表现请求、行为开始及已有执行的续播。
    public enum CharacterPlaybackStartSource { ExplicitRequest, BehaviorGrant, Rebind, ArbitrationRecovery }

    // 标明真正使本次播放结束的事实；终态来源在回执提交后固定。
    public enum CharacterPlaybackEndSource
    {
        None, BehaviorWindowEnded, ClipFinished, ArbitrationLost, ExplicitWithdrawal, BehaviorWithdrawn,
        BehaviorInterrupted, ModuleRemoved, StateReset, ResourceUnavailable, Rebind, Paused,
        BatchDiscarded, Teleport, Knockback, ExternalControl, ComponentDisabled
    }

    // 固定表现需求发出时的请求、行为执行和攻击招式归属。
    internal readonly struct CharacterPlaybackOrigin
    {
        internal readonly int StateVersion, Version;
        internal readonly CharacterBehaviorId Behavior;
        internal readonly long Execution, Request, Move;

        // 纯表现请求只填写代次；行为和招式的零编号表示没有对应来源。
        internal CharacterPlaybackOrigin(int stateVersion, int version, CharacterBehaviorId behavior = default,
            long execution = 0, long request = 0, long move = 0)
        {
            StateVersion = stateVersion;
            Version = version;
            Behavior = behavior;
            Execution = execution;
            Request = request;
            Move = move;
        }
    }

    /// <summary>一次单次播放的回执；行为时间窗结束可以发生在首条播放指令之前。</summary>
    public sealed class CharacterActionPlayback
    {
        private static long lastPlaybackId;
        internal CharacterPlaybackOrigin Origin { get; }
        internal readonly CharacterActionHandoff ExplicitAttackTiming;
        public string Key { get; }
        public long InstanceId { get; } // 原有的动作键内序号。
        public long PlaybackId { get; } // 可跨动作键、动画重绑及组件启停追踪。
        public int StateVersion => Origin.StateVersion;
        public int SimulationVersion => Origin.Version;
        public CharacterBehaviorId BehaviorId => Origin.Behavior;
        public long ExecutionId => Origin.Execution;
        public long RequestId => Origin.Request;
        public long MoveInstanceId => Origin.Move;
        public CharacterPlaybackStartSource StartSource { get; }
        public CharacterPlaybackEndSource EndSource { get; private set; }
        public CharacterActionPlaybackStatus Status { get; private set; }
        public bool HasStarted { get; private set; }
        internal CharacterActionPlaybackStatus ObservedStatus => pendingStatus ?? Status;
        internal bool WasPreempted { get; set; }
        internal float LastObservedPhase { get; private set; }
        private CharacterActionPlaybackStatus? pendingStatus;
        private CharacterPlaybackEndSource pendingSource;
        private bool observedTarget;

        // 为这次申请分配播放编号，并冻结动作键和原始业务身份。
        internal CharacterActionPlayback(string key, long instanceId, CharacterPlaybackOrigin origin,
            CharacterPlaybackStartSource startSource, CharacterActionHandoff explicitAttackTiming = default)
        {
            Key = key;
            InstanceId = instanceId;
            Origin = origin;
            StartSource = startSource;
            ExplicitAttackTiming = explicitAttackTiming;
            PlaybackId = System.Threading.Interlocked.Increment(ref lastPlaybackId);
        }

        // 只在播放端实际下发指令后确认首次开始。
        internal bool Start()
        {
            if (ObservedStatus != CharacterActionPlaybackStatus.Pending) return false;
            HasStarted = true;
            Status = CharacterActionPlaybackStatus.Started;
            return true;
        }

        // 暂存本批次第一条终态，供绑定试选并在播放提交后对外发布。
        internal bool QueueEnd(CharacterActionPlaybackStatus status, CharacterPlaybackEndSource source)
        {
            if (pendingStatus.HasValue || EndSource != CharacterPlaybackEndSource.None) return false;
            pendingStatus = status == CharacterActionPlaybackStatus.Interrupted && !HasStarted
                ? CharacterActionPlaybackStatus.Withdrawn : status;
            pendingSource = source;
            return true;
        }

        // 提交唯一终态；批次失效时按实际下发事实发布中止原因。
        internal bool PublishEnd(CharacterPlaybackEndSource discarded = CharacterPlaybackEndSource.None)
        {
            if (!pendingStatus.HasValue) return false;
            Status = discarded == CharacterPlaybackEndSource.None ? pendingStatus.Value :
                HasStarted ? CharacterActionPlaybackStatus.Interrupted : CharacterActionPlaybackStatus.Withdrawn;
            EndSource = discarded == CharacterPlaybackEndSource.None ? pendingSource : discarded;
            pendingStatus = null;
            return true;
        }

        // 只读取仍在播放的实例，已进入终态的回执忽略迟到的片段观测。
        internal CharacterActionPlaybackStatus Observe(int state, in CharacterLayerObservation layer)
        {
            if (ObservedStatus != CharacterActionPlaybackStatus.Started || !layer.Ready) return ObservedStatus;
            var current = layer.Destination;
            if (current.fullPathHash != state)
                return observedTarget ? CharacterActionPlaybackStatus.Interrupted : Status;
            observedTarget = true;
            LastObservedPhase = current.normalizedTime;
            if (current.loop) return CharacterActionPlaybackStatus.Interrupted;
            return current.normalizedTime >= 1f ? CharacterActionPlaybackStatus.Completed : Status;
        }
    }
}
