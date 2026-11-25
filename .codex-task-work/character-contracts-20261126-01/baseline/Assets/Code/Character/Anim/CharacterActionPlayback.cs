namespace ThreeC.CharacterBehavior
{
    public enum CharacterActionPlaybackStatus { Pending, Started, Completed, Interrupted, Withdrawn }

    /// <summary>一个单次动作的播放记录。终态保留在本实例上，重播同一动作会产生新记录。</summary>
    public sealed class CharacterActionPlayback
    {
        public string Key { get; }
        public long InstanceId { get; }
        public CharacterActionPlaybackStatus Status { get; internal set; }
        // 由直接混合器的片段进度确认单次动作已完成或被替换。
        internal CharacterActionPlaybackStatus ObservedStatus => PendingStatus ?? Status;
        internal CharacterActionPlaybackStatus? PendingStatus;
        public bool HasStarted { get; private set; }
        internal bool WasPreempted { get; set; }
        internal float LastObservedPhase { get; private set; }
        private bool observedTarget;

        internal CharacterActionPlayback(string key, long instanceId)
        {
            Key = key;
            InstanceId = instanceId;
        }

        internal void Start()
        {
            if (Status != CharacterActionPlaybackStatus.Pending) return;
            HasStarted = true;
            Status = CharacterActionPlaybackStatus.Started;
        }

        // 由直接混合器的片段进度确认单次动作已完成或被替换。
        internal CharacterActionPlaybackStatus Observe(int state, in CharacterLayerObservation layer)
        {
            if (!layer.Ready) return Status;
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
