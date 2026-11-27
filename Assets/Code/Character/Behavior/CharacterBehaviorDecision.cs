using System;
using System.Collections;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    /// <summary>标记一次请求在实际准入点获得的结果。</summary>
    public enum CharacterBehaviorRequestStatus { NoRequest, AlreadyActive, Buffered, Rejected, Started }
    /// <summary>记录裁决顺序中首个阻止请求的检查。</summary>
    public enum CharacterBehaviorRejectReason
    {
        None, Cooldown, InsufficientResource, InvalidConfiguration, StateCondition, Gate,
        Conflict, PhaseRestriction, LegacyEvaluation, OutsideWindow, NoRoute, RequiresFixedDuration
    }
    /// <summary>区分一次行为执行离开活动状态的实际原因。</summary>
    public enum CharacterBehaviorEndReason { None, Completed, Withdrawn, Interrupted, Reset, ExternalControl, Teleport, Knockback }
    /// <summary>记录一份连招预输入从接收到退出缓存的具体转换。</summary>
    public enum CharacterBufferEvent { None, Buffered, Overwritten, Expired, Cancelled, Executed }

    /// <summary>关联一次行为执行、发起请求、当前阶段与结束原因。</summary>
    public readonly struct CharacterBehaviorExecution
    {
        public readonly CharacterBehaviorId Id;
        public readonly long Instance, RequestId;
        public readonly CharacterBehaviorPhase Phase;
        public readonly CharacterBehaviorEndReason EndReason;

        // 捕获独立于行为对象的执行状态。
        internal CharacterBehaviorExecution(CharacterBehaviorId id, long instance, long requestId,
            CharacterBehaviorPhase phase, CharacterBehaviorEndReason endReason = default)
        {
            Id = id;
            Instance = instance;
            RequestId = requestId;
            Phase = phase;
            EndReason = endReason;
        }
    }

    /// <summary>保留本次请求、阻挡者、缓存事件及其角色时间；序号在角色重置前单调递增。</summary>
    public readonly struct CharacterBehaviorDecision
    {
        public readonly long Sequence, RequestId, Instance;
        public readonly CharacterBehaviorId Id, Blocker;
        public readonly CharacterBehaviorRequestStatus Status;
        public readonly CharacterBehaviorRejectReason Reason;
        public readonly CharacterBufferEvent BufferEvent;
        public readonly double Time;

        // 冻结实际裁决时的数值供帧结果与可选历史读取。
        internal CharacterBehaviorDecision(long sequence, long request, long instance, CharacterBehaviorId id,
            CharacterBehaviorRequestStatus status, CharacterBehaviorRejectReason reason, CharacterBehaviorId blocker,
            CharacterBufferEvent bufferEvent, double time)
        {
            Sequence = sequence;
            RequestId = request;
            Instance = instance;
            Id = id;
            Status = status;
            Reason = reason;
            Blocker = blocker;
            BufferEvent = bufferEvent;
            Time = time;
        }
    }

    /// <summary>在开启诊断时按提交顺序保留最近六十四个实际请求结果。</summary>
    internal sealed class CharacterDecisionHistory : IReadOnlyList<CharacterBehaviorDecision>
    {
        private readonly CharacterBehaviorDecision[] entries = new CharacterBehaviorDecision[64];
        private int first;
        public int Count { get; private set; }
        public CharacterBehaviorDecision this[int index] => (uint)index < (uint)Count
            ? entries[(first + index) % entries.Length] : throw new ArgumentOutOfRangeException(nameof(index));

        // 追加已提交结果，满容量时覆盖最早的一项。
        internal void Add(CharacterBehaviorDecision decision)
        {
            entries[(first + Count) % entries.Length] = decision;
            if (Count == entries.Length) first = (first + 1) % entries.Length;
            else Count++;
        }

        // 以最早到最新的顺序公开当前短历史。
        public IEnumerator<CharacterBehaviorDecision> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }

        // 提供非泛型枚举入口。
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>为一个独立流程分配请求及执行号，在状态提交后冻结本帧观测。</summary>
    internal sealed class CharacterBehaviorJournal
    {
        private readonly CharacterBehaviorRule[] participants;
        private readonly List<CharacterBehaviorDecision> pending = new List<CharacterBehaviorDecision>();
        private readonly List<CharacterBehaviorExecution> changes = new List<CharacterBehaviorExecution>();
        private IReadOnlyList<CharacterBehaviorExecution> snapshot = Array.Empty<CharacterBehaviorExecution>();
        private CharacterDecisionHistory history;
        private long request, instance, sequence;
        private int committedHistory;
        internal bool Forecasting;
        internal bool DispatchCancelled;
        internal double Time;
        internal bool HasPending => changes.Count != 0 || pending.Count != 0;
        internal bool HistoryEnabled
        {
            get => history != null;
            set { if (value) history ??= new CharacterDecisionHistory(); else history = null; }
        }
        internal IReadOnlyList<CharacterBehaviorDecision> History =>
            (IReadOnlyList<CharacterBehaviorDecision>)history ?? Array.Empty<CharacterBehaviorDecision>();

        // 为每个参与者绑定本流程独有的执行号与诊断收集器。
        internal CharacterBehaviorJournal(CharacterBehaviorRule[] participants, bool forecasting)
        {
            this.participants = participants;
            Forecasting = forecasting;
            foreach (var rule in participants)
            {
                rule.Behavior.RegisteredId = rule.Id;
                rule.Behavior.Journal = this;
            }
        }

        // 从本流程递增序列中取出一次独立请求号。
        internal long NewRequest() => ++request;

        // 为行为新建执行身份，预测复制保留编号但不会产生外部通知。
        internal void Start(CharacterBehavior behavior, long requestId)
        {
            long next = ++instance;
            behavior.Execution = new CharacterBehaviorExecution(behavior.RegisteredId, next, requestId, CharacterBehaviorPhase.Active);
            try { behavior.Execution = new CharacterBehaviorExecution(behavior.RegisteredId, next, requestId, behavior.Phase); }
            finally { if (!Forecasting) changes.Add(behavior.Execution); }
        }

        // 保留终止前最后一个阶段与实例号。
        internal void End(CharacterBehavior behavior, CharacterBehaviorEndReason reason)
        {
            var previous = behavior.Execution;
            if (previous.Instance == 0 || previous.EndReason != default) return;
            behavior.Execution = new CharacterBehaviorExecution(previous.Id, previous.Instance, previous.RequestId,
                behavior.Phase, reason);
            if (!Forecasting) changes.Add(behavior.Execution);
        }

        // 结果暂存在所属行为中；只有整步已提交后才进入公开帧和历史。
        internal void Record(CharacterBehaviorId id, long requestId, long execution, CharacterBehaviorRequestStatus status,
            CharacterBehaviorRejectReason reason = default, CharacterBehaviorId blocker = default, CharacterBufferEvent buffer = default,
            double? at = null)
        {
            if (Forecasting) return;
            pending.Add(new CharacterBehaviorDecision(++sequence, requestId, execution, id, status, reason, blocker, buffer, at ?? Time));
        }

        // 在承诺执行的调用结束后公开积累的请求；无变化时不产生新集合。
        internal void Commit(ref CharacterBehaviorStep step)
        {
            if (Forecasting) return;
            ReadExecutions(ref step);
            if (DispatchCancelled) return;
            if (changes.Count != 0)
            {
                step.ExecutionChanges = Array.AsReadOnly(changes.ToArray());
                changes.Clear();
            }
            if (pending.Count != 0)
            {
                step.Decisions = Array.AsReadOnly(pending.ToArray());
                CommitPendingHistory();
                pending.Clear();
                committedHistory = 0;
            }
        }

        // 前一帧在冻结运动时失去发布资格，保留已提交的真实边沿并避免短历史重复记入。
        internal void RetainUnpublished(in CharacterBehaviorFrameResult frame)
        {
            if (Forecasting) return;
            pending.InsertRange(0, frame.Decisions);
            changes.InsertRange(0, frame.ExecutionChanges);
            committedHistory += frame.Decisions.Count;
        }

        // 连续性切换在帧间发生时立即提交历史，下一帧仍能交付同一事件且不会重复入历史。
        internal void CommitPendingHistory()
        {
            if (history != null)
                for (int i = committedHistory; i < pending.Count; i++) history.Add(pending[i]);
            committedHistory = pending.Count;
        }

        // 快照只在身份、阶段或结束原因变化时复制，两次裁决不会互相改写。
        internal void ReadExecutions(ref CharacterBehaviorStep step)
        {
            if (Forecasting) return;
            CharacterBehaviorExecution[] changed = null;
            for (int i = 0; i < participants.Length; i++)
            {
                var behavior = participants[i].Behavior;
                var current = behavior.Execution;
                if (behavior.IsActive && current.Instance != 0 && current.EndReason == CharacterBehaviorEndReason.None)
                    current = new CharacterBehaviorExecution(current.Id, current.Instance, current.RequestId, behavior.Phase);
                if (changed == null && snapshot.Count == participants.Length &&
                    snapshot[i].Instance == current.Instance && snapshot[i].Phase == current.Phase &&
                    snapshot[i].EndReason == current.EndReason) continue;
                if (changed == null)
                {
                    changed = new CharacterBehaviorExecution[participants.Length];
                    for (int j = 0; j < i; j++) changed[j] = snapshot[j];
                }
                changed[i] = current;
            }
            if (changed != null) snapshot = Array.AsReadOnly(changed);
            step.Executions = snapshot;
        }

        // 复制编号计数器但隔离挂起事件和真实历史。
        internal void CopyTo(CharacterBehaviorJournal target)
        {
            target.Forecasting = true;
            target.request = request;
            target.instance = instance;
            target.Time = Time;
            target.changes.Clear();
            target.pending.Clear();
            target.committedHistory = 0;
        }

        // 重生终止旧代的编号、快照以及待提交的请求。
        internal void Reset()
        {
            request = instance = sequence = 0;
            committedHistory = 0;
            Time = 0d;
            changes.Clear();
            pending.Clear();
            snapshot = Array.Empty<CharacterBehaviorExecution>();
            if (history != null) history = new CharacterDecisionHistory();
            foreach (var rule in participants)
            {
                rule.Behavior.Execution = default;
                rule.Behavior.HeldRequest = false;
            }
        }
    }
}
