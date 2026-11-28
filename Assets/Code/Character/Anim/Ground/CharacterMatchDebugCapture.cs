using UnityEngine;
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
#endif

namespace ThreeC.CharacterBehavior
{
#if UNITY_EDITOR
    // 标记本次查询实际采用的评分来源。
    internal enum GroundMatchScoring { None, Compatibility, Schema }

    // 保存诊断候选的状态、片段内时间、实际倍率和分项代价。
    internal struct CharacterMatchDebugEntry
    {
        public string State, Library, Track;
        public float Time, Rate;
        public GroundMatchCost Cost;
        internal int SourceRank;
    }

    // 统计一次诊断按原因淘汰的轨道和入点，以及实际评分次数。
    internal struct GroundMatchDebugCounts
    {
        public int Use, Gait, Resource, Speed, Support, Window, Group, History, Evaluated;

        // 按实际过滤原因累计一次淘汰。
        internal void Reject(GroundMatchFilter reason)
        {
            switch (reason)
            {
                case GroundMatchFilter.History: History++; break;
                case GroundMatchFilter.Group: Group++; break;
                case GroundMatchFilter.Use: Use++; break;
                case GroundMatchFilter.Gait: Gait++; break;
                case GroundMatchFilter.Resource: Resource++; break;
                case GroundMatchFilter.Speed: Speed++; break;
                case GroundMatchFilter.Support: Support++; break;
                case GroundMatchFilter.Window: Window++; break;
            }
        }
    }

    // 保存一次最终查询的候选排序及筛选数量。
    internal sealed class CharacterMatchDebugFrame
    {
        private IReadOnlyList<string> libraryNames;
        private GroundMatchDebugCounts[] libraryCounts = Array.Empty<GroundMatchDebugCounts>();
        public string Query, Reason, Choice, Rule, Libraries, CompatibilityIssue;
        public GroundMatchDebugCounts Counts;
        public CharacterMatchDebugEntry Chosen, Continuing;
        internal readonly GroundMatchQueryTrace Trace = new GroundMatchQueryTrace();
        public readonly CharacterMatchDebugEntry[] Top = new CharacterMatchDebugEntry[3];
        public int TopCount;
        public GroundMatchScoring Scoring;
        public bool HistoryFallback;
        public int LibraryCount => libraryNames?.Count ?? 0;

        // 清空本次试算结果，重新选择时覆盖同帧第一次试算。
        internal void Reset()
        {
            Query = Reason = Choice = Rule = Libraries = CompatibilityIssue = null;
            Scoring = GroundMatchScoring.None;
            HistoryFallback = false;
            Array.Clear(libraryCounts, 0, LibraryCount);
            libraryNames = null;
            Counts = default;
            TopCount = 0;
            Chosen = Continuing = default;
            Trace.Reset();
        }

        // 绑定已在主线程生成且装配期间保持不变的库名列表。
        internal void SetLibraries(IReadOnlyList<string> names)
        {
            libraryNames = names;
            if (libraryCounts.Length < LibraryCount) Array.Resize(ref libraryCounts, LibraryCount);
        }

        // 取得声明顺序上此库的标识和过滤统计。
        internal GroundMatchDebugCounts ReadLibrary(int index, out string name)
        {
            name = libraryNames[index];
            return libraryCounts[index];
        }

        // 把已发布结果复制到窗口缓冲，使重绘使用同一份查询数据。
        internal void CopyFrom(CharacterMatchDebugFrame source)
        {
            Query = source.Query;
            Reason = source.Reason;
            Choice = source.Choice;
            Rule = source.Rule;
            Libraries = source.Libraries;
            CompatibilityIssue = source.CompatibilityIssue;
            Scoring = source.Scoring;
            HistoryFallback = source.HistoryFallback;
            Counts = source.Counts;
            Chosen = source.Chosen;
            Continuing = source.Continuing;
            Trace.CopyFrom(source.Trace);
            TopCount = source.TopCount;
            Array.Copy(source.Top, Top, TopCount);
            SetLibraries(source.libraryNames);
            Array.Copy(source.libraryCounts, libraryCounts, LibraryCount);
        }

        // 记录本轮评分采用的布局和历史范围，后续回退会覆盖先前的范围。
        internal void StartScoring(in GroundSearchFrame frame)
        {
            Scoring = frame.Features.Active ? GroundMatchScoring.Schema : GroundMatchScoring.Compatibility;
            HistoryFallback = !frame.AllowPast;
        }

        // 每次真实扫描使用本轮的计数、排名和评分维度，前一次尝试保存在经过记录中。
        internal void BeginScan(in GroundSearchFrame frame)
        {
            Array.Clear(libraryCounts, 0, LibraryCount);
            Counts = default;
            TopCount = 0;
            StartScoring(frame);
        }

        // 区分整条轨道的淘汰与逐采样点的入场过滤。
        internal void Reject(GroundMatchFilter reason, int libraryIndex = -1)
        {
            Counts.Reject(reason);
            if ((uint)libraryIndex < LibraryCount) libraryCounts[libraryIndex].Reject(reason);
        }

        // 按代价与规则来源优先级记录前三个入点，库内同分沿用样本扫描顺序。
        internal void Candidate(in CharacterMatchDebugEntry entry, int libraryIndex, int sourceRank)
        {
            Counts.Evaluated++;
            if ((uint)libraryIndex < LibraryCount) libraryCounts[libraryIndex].Evaluated++;
            float total = entry.Cost.Total;
            if (float.IsNaN(total) || float.IsInfinity(total)) return;
            int i = 0;
            while (i < TopCount && !GroundPoseSearch.Precedes(total, sourceRank, Top[i].Cost.Total, Top[i].SourceRank)) i++;
            if (i == Top.Length) return;
            TopCount = Mathf.Min(TopCount + 1, Top.Length);
            for (int j = TopCount - 1; j > i; j--) Top[j] = Top[j - 1];
            Top[i] = entry;
            Top[i].SourceRank = sourceRank;
        }
    }

#endif

    // 仅为编辑器监听的角色保存已提交的查询、播放结果和运行分界。
    internal static class CharacterMatchDebugCapture
    {
#if UNITY_EDITOR
        internal const int Capacity = 256;
        private static Animator selected;
        private static bool recording;
        private static CharacterMatchDebugFrame writable, spare;
        private static readonly CharacterMatchHistoryEntry[] entries = new CharacterMatchHistoryEntry[Capacity];
        private static readonly List<string> boundaries = new List<string>();
        private static long sequence;
        private static int count;

        // 切换诊断对象时释放旧会话的槽位，也令尚在后台工作的缓冲失去发布资格。
        internal static void Select(Animator animator)
        {
            if (ReferenceEquals(selected, animator) && (animator || count == 0)) return;
            selected = animator;
            writable = spare = null;
            recording = false;
            count = 0;
            sequence = 0;
            boundaries.Clear();
            Array.Clear(entries, 0, entries.Length);
        }

        // 在同一角色的会话中启停录制，收起窗口时保留历史并撤销未提交的试算。
        internal static void SetRecording(Animator target, bool enabled)
        {
            if (!ReferenceEquals(selected, target)) Select(target);
            if (recording == enabled || !target) return;
            recording = enabled;
            if (!enabled) MarkBoundary(target, "暂停采集");
        }

        // 同一角色更换模型时保留旧记录，作废旧模型的试算并在新模型首条提交处标出分界。
        internal static void Rebind(Animator previous, Animator next)
        {
            if (!ReferenceEquals(selected, previous) || ReferenceEquals(previous, next)) return;
            const string reason = "角色 Animator 更换";
            if (count != 0 && !boundaries.Contains(reason)) boundaries.Add(reason);
            selected = next;
            writable = spare = null;
            recording = false;
        }

        // 返回指定角色已提交且尚在环形缓冲中的记录数。
        internal static int Count(Animator animator) => ReferenceEquals(selected, animator) ? count : 0;

        // 按由新到旧的偏移读取记录；持有跨提交的视图应自行复制。
        internal static CharacterMatchHistoryEntry ReadNewest(Animator animator, int offset) =>
            (uint)offset < Count(animator) ? entries[(int)((sequence - 1 - offset) % Capacity)] : null;

        // 在主线程为选中角色取得独立的后台规划缓冲。
        internal static CharacterMatchDebugFrame Begin(Animator animator)
        {
            if (!recording || !ReferenceEquals(selected, animator)) return null;
            writable = spare ?? new CharacterMatchDebugFrame();
            spare = null;
            writable.Reset();
            return writable;
        }

        // 仅主线程在最终求值成功后提交一次，将临时查询和这次播放图的实际结果冻结到同一槽。
        internal static void Publish(Animator animator, CharacterMatchDebugFrame frame,
            in CharacterAnimationDebugSnapshot playback, double time, long motionStep)
        {
            if (!recording || frame == null || !ReferenceEquals(selected, animator) || !ReferenceEquals(writable, frame)) return;
            var entry = entries[(int)(sequence % Capacity)] ??= new CharacterMatchHistoryEntry();
            string boundary = boundaries.Count == 0 ? null : string.Join("、", boundaries);
            entry.Capture(++sequence, time, motionStep, boundary, frame, playback);
            if (count < Capacity) count++;
            boundaries.Clear();
            spare = writable;
            writable = null;
        }

        // 关闭域重载时也从空会话开始，释放上一次播放的角色及素材引用。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession() => Select(null);

#endif

        // 在下次实际提交上标出采集间断，旧版本的后台试算不能再发布。
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        internal static void MarkBoundary(Animator animator, string reason)
        {
#if UNITY_EDITOR
            if (reason == null || ReferenceEquals(selected, null) || !ReferenceEquals(selected, animator)) return;
            writable = null;
            if (count != 0 && !boundaries.Contains(reason)) boundaries.Add(reason);
#endif
        }
    }
}
