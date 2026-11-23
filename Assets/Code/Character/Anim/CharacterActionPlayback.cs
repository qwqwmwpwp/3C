namespace ThreeC.CharacterBehavior
{
    public enum CharacterActionPlaybackStatus { Pending, Started, Completed, Interrupted, Withdrawn }

    /// <summary>一个单次动作的播放记录。终态保留在本实例上，重播同一动作会产生新记录。</summary>
    public sealed class CharacterActionPlayback
    {
        public string Key { get; }
        public long InstanceId { get; }
        public CharacterActionPlaybackStatus Status { get; internal set; }
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

        internal CharacterActionPlaybackStatus Observe(int state, in CharacterLayerObservation layer)
        {
            if (layer.Confirmation == CharacterPlaybackConfirmation.Lost)
            {
                var source = layer.Current;
                bool finished = observedTarget && layer.InTransition && source.fullPathHash == state &&
                    !source.loop && source.normalizedTime >= 1f;
                return finished ? CharacterActionPlaybackStatus.Completed : CharacterActionPlaybackStatus.Interrupted;
            }
            if (!layer.Ready || layer.Confirmation == CharacterPlaybackConfirmation.Awaiting) return Status;
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
