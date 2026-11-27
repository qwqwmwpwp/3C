#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    // 区分真实请求裁决、续播申请、实际播放开始和回执终态。
    internal enum CharacterActionTraceKind { Decision, BehaviorEnded, PlaybackResumed, PlaybackStarted, PlaybackEnded }

    // 保存发生时的编号及结果，后续回执变更不再改变历史行。
    internal readonly struct CharacterActionTraceEntry
    {
        internal readonly CharacterActionTraceKind Kind;
        internal readonly int StateVersion, Version;
        internal readonly CharacterBehaviorDecision Decision;
        internal readonly CharacterBehaviorEndReason BehaviorEnd;
        internal readonly CharacterBehaviorId Behavior;
        internal readonly long Request, Execution, Move, Playback;
        internal readonly string Key;
        internal readonly CharacterPlaybackStartSource Start;
        internal readonly CharacterPlaybackEndSource End;
        internal readonly CharacterActionPlaybackStatus Status;
        internal readonly bool HasStarted;

        // 保存行为裁决器本次给出的首个原因和实际阻挡者。
        internal CharacterActionTraceEntry(int stateVersion, int version, in CharacterBehaviorDecision decision)
        {
            this = default;
            Kind = CharacterActionTraceKind.Decision;
            StateVersion = stateVersion;
            Version = version;
            Decision = decision;
            Behavior = decision.Id;
            Request = decision.RequestId;
            Execution = decision.Instance;
        }

        // 直接记录执行方结束的旧实例，接招后仍可独立追踪。
        internal CharacterActionTraceEntry(int stateVersion, int version, in CharacterBehaviorExecution execution)
        {
            this = default;
            Kind = CharacterActionTraceKind.BehaviorEnded;
            StateVersion = stateVersion;
            Version = version;
            Behavior = execution.Id;
            Request = execution.RequestId;
            Execution = execution.Instance;
            BehaviorEnd = execution.EndReason;
        }

        // 在回执边沿采集动作键、各级实例编号及实际下发事实。
        internal CharacterActionTraceEntry(CharacterActionPlayback action, CharacterActionTraceKind kind)
        {
            this = default;
            Kind = kind;
            StateVersion = action.StateVersion;
            Version = action.SimulationVersion;
            Behavior = action.BehaviorId;
            Request = action.RequestId;
            Execution = action.ExecutionId;
            Move = action.MoveInstanceId;
            Playback = action.PlaybackId;
            Key = action.Key;
            Start = action.StartSource;
            End = action.EndSource;
            Status = action.Status;
            HasStarted = action.HasStarted;
        }
    }

    // 只在调试窗口选中角色并展开历史时采集，固定环形缓冲允许规划线程写入续播申请。
    internal sealed class CharacterActionTrace
    {
        internal const int Capacity = 64;
        private readonly object gate = new object();
        private volatile CharacterActionTraceEntry[] entries;
        private int next, count;
        private int previousState, decisionState;
        private long lastDecision;
        private CharacterBehaviorDriver driver;
        private bool ownsDriverHistory;
        private long previousFrame;
        internal bool Enabled => entries != null;

        // 开始新的采集区间，关闭时释放保存的所有动作键。
        internal void Enable(bool enabled, CharacterBehaviorDriver owner)
        {
            lock (gate)
            {
                if ((entries != null) == enabled) return;
                if (driver && ownsDriverHistory) driver.BehaviorHistoryEnabled = false;
                driver = enabled ? owner : null;
                ownsDriverHistory = driver && !driver.BehaviorHistoryEnabled;
                if (ownsDriverHistory) driver.BehaviorHistoryEnabled = true;
                var existing = driver ? driver.BehaviorDecisionHistory : null;
                decisionState = driver ? driver.StateVersion : 0;
                lastDecision = existing != null && existing.Count != 0 ? existing[existing.Count - 1].Sequence : 0;
                entries = enabled ? new CharacterActionTraceEntry[Capacity] : null;
                next = count = 0;
                previousFrame = 0;
            }
        }

        // 按已提交的帧去重，外控中断帧以旧执行所属的代次显示。
        internal void Record(int stateVersion, in CharacterBehaviorObservation frame, int? previousSimulation = null)
        {
            if (!Enabled || frame.FrameNumber == 0) return;
            lock (gate)
            {
                if (entries == null || previousState == stateVersion && previousFrame >= frame.FrameNumber) return;
                previousState = stateVersion;
                previousFrame = frame.FrameNumber;
                int version = previousSimulation ?? frame.SimulationVersion;
                var decisions = frame.Behavior.Decisions;
                for (int i = 0; i < decisions.Count; i++)
                {
                    var decision = decisions[i];
                    if (decisionState == stateVersion && decision.Sequence > lastDecision + 1 && driver && stateVersion == driver.StateVersion)
                        ReadDecisions(stateVersion, version, driver.BehaviorDecisionHistory, decision.Sequence - 1);
                    AddDecision(stateVersion, version, decision);
                }
                var changes = frame.Behavior.ExecutionChanges;
                for (int i = 0; i < changes.Count; i++)
                    if (changes[i].EndReason != CharacterBehaviorEndReason.None)
                        Add(new CharacterActionTraceEntry(stateVersion, version, changes[i]));
            }
        }

        // 暂停期间的缓存撤销可先于下一次行为帧提交，从真实裁决短历史补齐。
        internal void ReadPending()
        {
            if (!Enabled || !driver) return;
            lock (gate)
            {
                if (entries != null) ReadDecisions(driver.StateVersion, driver.SimulationVersion, driver.BehaviorDecisionHistory, long.MaxValue);
            }
        }

        // 在给定编号之前补录未交付的决定；不重复已经进入本窗口的记录。
        private void ReadDecisions(int stateVersion, int version, IReadOnlyList<CharacterBehaviorDecision> decisions, long through)
        {
            if (decisions.Count == 0 || stateVersion == decisionState && decisions[decisions.Count - 1].Sequence <= lastDecision) return;
            for (int i = 0; i < decisions.Count && decisions[i].Sequence <= through; i++)
                AddDecision(stateVersion, version, decisions[i]);
        }

        // 以重生代次和裁决序号去重，连续性变化仍关联原来的请求。
        private void AddDecision(int stateVersion, int version, in CharacterBehaviorDecision decision)
        {
            if (decisionState == stateVersion && decision.Sequence <= lastDecision) return;
            decisionState = stateVersion;
            lastDecision = decision.Sequence;
            Add(new CharacterActionTraceEntry(stateVersion, version, decision));
        }

        // 跨规划与提交阶段记录一个冻结的播放边沿。
        internal void Record(CharacterActionPlayback action, CharacterActionTraceKind kind)
        {
            if (entries == null) return;
            lock (gate)
            {
                if (entries != null) Add(new CharacterActionTraceEntry(action, kind));
            }
        }

        // 调用方持有采集锁，按到达顺序覆盖最旧记录。
        private void Add(in CharacterActionTraceEntry entry)
        {
            entries[next] = entry;
            next = (next + 1) % Capacity;
            if (count < Capacity) count++;
        }

        // 向窗口提供从新到旧的定长快照，使布局和重绘保持一致。
        internal int CopyTo(CharacterActionTraceEntry[] target)
        {
            lock (gate)
            {
                int length = Math.Min(count, target.Length);
                for (int i = 0; i < length; i++)
                    target[i] = entries[(next + Capacity - 1 - i) % Capacity];
                return length;
            }
        }
    }
}
#endif
