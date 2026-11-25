#if UNITY_EDITOR
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 标记本次查询实际采用的评分来源。
    internal enum GroundMatchScoring { None, Compatibility, Schema }

    // 累计同一次特征评分各组的贡献和实际纳入评分的通道位。
    internal struct GroundFeatureScoreBreakdown
    {
        internal float Feet, Body, Hands, Trajectory, Shared;
        internal uint Channels;

        // 通道只有一个有效组时归入该组，同时命中多个有效组时只计入共用项。
        internal void Add(int index, GroundFeatureGroups groups, float contribution)
        {
            Channels |= 1u << index;
            switch (groups)
            {
                case GroundFeatureGroups.Feet: Feet += contribution; break;
                case GroundFeatureGroups.Body: Body += contribution; break;
                case GroundFeatureGroups.Hands: Hands += contribution; break;
                case GroundFeatureGroups.Trajectory: Trajectory += contribution; break;
                default: Shared += contribution; break;
            }
        }
    }

    // 保存诊断候选的状态、片段内时间、实际倍率和分项代价。
    internal struct CharacterMatchDebugEntry
    {
        public string State;
        public float Time, Rate;
        public GroundMatchCost Cost;
    }

    // 保存一次最终查询的候选排序及筛选数量。
    internal sealed class CharacterMatchDebugFrame
    {
        public string Query, Reason, Choice;
        public int UseRejected, GaitRejected, ResourceRejected, SpeedRejected, SupportRejected, WindowRejected, GroupRejected, Evaluated;
        public CharacterMatchDebugEntry Chosen, Continuing;
        public readonly CharacterMatchDebugEntry[] Top = new CharacterMatchDebugEntry[3];
        public int TopCount, HistoryRejected;
        public GroundMatchScoring Scoring;
        public bool HistoryFallback;

        // 清空本次试算结果，重新选择时覆盖同帧第一次试算。
        internal void Reset()
        {
            Query = Reason = Choice = null;
            Scoring = GroundMatchScoring.None;
            HistoryFallback = false;
            HistoryRejected = 0;
            UseRejected = GaitRejected = ResourceRejected = SpeedRejected = SupportRejected = WindowRejected = GroupRejected = Evaluated = TopCount = 0;
            Chosen = Continuing = default;
        }

        // 记录本轮评分采用的布局和历史范围，后续回退会覆盖先前的范围。
        internal void StartScoring(in GroundSearchFrame frame)
        {
            Scoring = frame.Features.Active ? GroundMatchScoring.Schema : GroundMatchScoring.Compatibility;
            HistoryFallback = !frame.AllowPast;
        }

        // 区分整条轨道的淘汰与逐采样点的入场过滤。
        internal void Reject(GroundMatchFilter reason)
        {
            switch (reason)
            {
                case GroundMatchFilter.History: HistoryRejected++; break;
                case GroundMatchFilter.Group: GroupRejected++; break;
                case GroundMatchFilter.Use: UseRejected++; break;
                case GroundMatchFilter.Gait: GaitRejected++; break;
                case GroundMatchFilter.Resource: ResourceRejected++; break;
                case GroundMatchFilter.Speed: SpeedRejected++; break;
                case GroundMatchFilter.Support: SupportRejected++; break;
                case GroundMatchFilter.Window: WindowRejected++; break;
            }
        }

        // 按稳定代价顺序维护前三个有效入点。
        internal void Candidate(in CharacterMatchDebugEntry entry)
        {
            Evaluated++;
            float total = entry.Cost.Total;
            if (float.IsNaN(total) || float.IsInfinity(total)) return;
            int i = 0;
            while (i < TopCount && Top[i].Cost.Total <= total) i++;
            if (i == Top.Length) return;
            TopCount = Mathf.Min(TopCount + 1, Top.Length);
            for (int j = TopCount - 1; j > i; j--) Top[j] = Top[j - 1];
            Top[i] = entry;
        }
    }

    // 仅为编辑器所选角色双缓冲记录匹配查询，主线程提交完成后才能读取。
    internal static class CharacterMatchDebugCapture
    {
        private static Animator selected;
        private static CharacterMatchDebugFrame published, writable;

        // 切换诊断目标，同时使上一目标的数据失效。
        internal static void Select(Animator animator)
        {
            if (ReferenceEquals(selected, animator)) return;
            selected = animator;
            published = writable = null;
        }

        // 仅读取当前被监听角色已经提交的查询。
        internal static CharacterMatchDebugFrame Read(Animator animator) =>
            animator && ReferenceEquals(selected, animator) ? published : null;

        // 在主线程为选中角色取得独立的后台规划缓冲。
        internal static CharacterMatchDebugFrame Begin(Animator animator)
        {
            if (!ReferenceEquals(selected, animator)) return null;
            writable ??= new CharacterMatchDebugFrame();
            writable.Reset();
            return writable;
        }

        // 在最终播放计划提交后交换读写缓冲，并验证目标仍受监听。
        internal static void Publish(Animator animator, CharacterMatchDebugFrame frame)
        {
            if (frame == null || !ReferenceEquals(selected, animator) || !ReferenceEquals(writable, frame)) return;
            writable = published;
            published = frame;
        }

        // 使暂停、失效或重新绑定之前已发布的查询失效。
        internal static void Clear(Animator animator)
        {
            if (ReferenceEquals(selected, animator)) published = null;
        }
    }
}
#endif
