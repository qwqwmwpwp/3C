namespace ThreeC.CharacterBehavior
{
    // 表示单次播放从排队到实际下发及结束的状态。
    public enum CharacterActionPlaybackStatus { Pending, Started, Completed, Interrupted, Withdrawn }

    // 标明本次实际播放结束的原因。
    public enum CharacterPlaybackEndSource
    {
        None, WindowExpired, ClipFinished, ArbitrationLost, ExplicitWithdrawal, ModuleRemoved,
        StateReset, ResourceUnavailable, Rebind, Paused, BatchDiscarded, MotionBoundary, ComponentDisabled
    }

    // 提交独立的单次动作，时长表示从指定素材入点到末尾所用的秒数。
    public readonly struct CharacterOneShotRequest
    {
        public readonly string Key;
        public readonly float Weight;
        public readonly float? StartPhase, Duration, Transition; // 过渡以秒为单位，空值使用动作配置。

        // 冻结请求参数，后续装配变化不改变已经受理的播放时钟。
        public CharacterOneShotRequest(string key, float weight = 1f, float? startPhase = null,
            float? duration = null, float? transition = null)
        {
            Key = key;
            Weight = weight;
            StartPhase = startPhase;
            Duration = duration;
            Transition = transition;
        }
    }

    /// <summary>记录已受理请求的真实起播和唯一终态。</summary>
    public sealed class CharacterActionPlayback
    {
        private static long lastPlaybackId;
        internal CharacterPlaybackClock Clock;
        internal readonly int ClipId;
        internal CharacterClipProgress? Cursor { get; private set; }
        internal CharacterPlaybackEndSource StopSource { get; private set; }
        internal CharacterActionPlaybackStatus StopStatus { get; private set; }
        internal double LastEvaluatedAt { get; private set; }
        internal bool EndSampleEvaluated { get; private set; }
        internal float? EntryCoverage; // 仅在首次真实求值后锁定，图重建不重置正在执行的权重过渡。
        public string Key { get; }
        public long InstanceId { get; } // 原有的动作键内序号。
        public long PlaybackId { get; } // 可跨动作键、动画重绑及组件启停追踪。
        public CharacterPlaybackEndSource EndSource { get; private set; }
        public CharacterActionPlaybackStatus Status { get; private set; }
        public bool HasStarted { get; private set; }
        internal CharacterActionPlaybackStatus ObservedStatus => pendingStatus ?? Status;
        private CharacterActionPlaybackStatus? pendingStatus;
        private CharacterPlaybackEndSource pendingSource;

        // 为本次播放分配独立编号并保存已解析的素材身份和时钟。
        internal CharacterActionPlayback(string key, long instanceId, CharacterPlaybackClock clock, int clipId)
        {
            Key = key;
            InstanceId = instanceId;
            Clock = clock;
            ClipId = clipId;
            PlaybackId = System.Threading.Interlocked.Increment(ref lastPlaybackId);
        }

        // 对这个逻辑实例安排权重淡出，调用不操作播放图。
        internal void FadeOut(double at, float seconds) => Clock.FadeOut(at, seconds);

        // 记录合法采样的截止与唯一终止原因；相同时刻的显式打断优先于自然窗口退出。
        internal void Cancel(double at, CharacterActionPlaybackStatus status, CharacterPlaybackEndSource source)
        {
            if (at > Clock.StopAt || at == Clock.StopAt && StopSource != CharacterPlaybackEndSource.WindowExpired) return;
            Clock.Cancel(at);
            StopStatus = status;
            StopSource = source;
        }

        // 图求值后只接收当前句柄与素材的真实游标，暂停和同素材重绑保留该值。
        internal void RecordCursor(in CharacterClipProgress sample, double at, bool evaluatedEnd)
        {
            if (sample.PlaybackId != PlaybackId || sample.ClipId != ClipId || sample.loop) return;
            Cursor = sample;
            LastEvaluatedAt = at;
            EndSampleEvaluated |= evaluatedEnd;
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
    }
}
