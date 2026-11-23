namespace ThreeC.CharacterBehavior
{
    public enum CharacterActionPlaybackStatus { Pending, Started, Completed, Interrupted, Withdrawn }

    /// <summary>一个单次动作的播放记录。终态保留在本实例上，重播同一动作会产生新记录。</summary>
    public sealed class CharacterActionPlayback
    {
        public string Key { get; }
        public long InstanceId { get; }
        public CharacterActionPlaybackStatus Status { get; internal set; }
        public bool HasStarted { get; private set; }
        private float? previousEnd;
        private bool observedTarget;

        internal CharacterActionPlayback(string key, long instanceId)
        {
            Key = key;
            InstanceId = instanceId;
        }

        internal void Start(int state, CharacterClipProgress before, bool direct)
        {
            if (Status != CharacterActionPlaybackStatus.Pending) return;
            HasStarted = true;
            Status = CharacterActionPlaybackStatus.Started;
            if (!direct && before.fullPathHash == state && before.normalizedTime >= 1f)
                previousEnd = before.normalizedTime; // 旧控制器可能尚未求值同状态的新播放指令。
        }

        internal CharacterActionPlaybackStatus Observe(int state, CharacterClipProgress current)
        {
            if (current.fullPathHash != state)
                return observedTarget ? CharacterActionPlaybackStatus.Interrupted : Status;
            if (current.normalizedTime == previousEnd) return Status;
            previousEnd = null;
            observedTarget = true;
            if (current.loop) return CharacterActionPlaybackStatus.Interrupted;
            return current.normalizedTime >= 1f ? CharacterActionPlaybackStatus.Completed : Status;
        }
    }
}
