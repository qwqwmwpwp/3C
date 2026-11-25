using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 维护调试窗口当前选中的采集对象及其布局快照。
    internal sealed class CharacterAnimationActionHistoryView
    {
        private readonly CharacterActionTraceEntry[] entries = new CharacterActionTraceEntry[CharacterActionTrace.Capacity];
        private CharacterBehaviorAnimator selected;
        private bool expanded;
        private int count;

        // 切换角色或关闭窗口时停止旧采集，并释放上次快照中的动作键。
        internal void Clear()
        {
            if (selected) selected.ActionTrace.Enable(false, null);
            selected = null;
            System.Array.Clear(entries, 0, count);
            count = 0;
        }

        // 展开时只为所选角色采集，在布局阶段取得有界历史。
        internal void Draw(CharacterBehaviorAnimator target)
        {
            expanded = EditorGUILayout.Foldout(expanded, "行为请求与播放历史（最近 64 项）");
            var next = expanded ? target : null;
            if (!ReferenceEquals(selected, next))
            {
                Clear();
                selected = next;
                if (selected) selected.ActionTrace.Enable(true, selected.GetComponent<CharacterBehaviorDriver>());
            }
            if (!selected) return;
            if (Event.current.type == EventType.Layout) count = selected.ActionTrace.CopyTo(entries);
            if (count == 0)
            {
                EditorGUILayout.LabelField("等待此角色的请求或播放边沿。", EditorStyles.miniLabel);
                return;
            }
            DrawRecentDecision();
            for (int i = 0; i < count; i++) DrawEntry(entries[i]);
        }

        // 最近请求与最近一次拒绝分开保留，拒绝原因直接使用请求裁决器的首个阻挡检查。
        private void DrawRecentDecision()
        {
            bool latest = false;
            for (int i = 0; i < count; i++)
            {
                var entry = entries[i];
                if (entry.Kind != CharacterActionTraceKind.Decision) continue;
                if (!latest)
                {
                    EditorGUILayout.LabelField("最近请求", DescribeDecision(entry), EditorStyles.wordWrappedLabel);
                    latest = true;
                }
                if (entry.Decision.Status != CharacterBehaviorRequestStatus.Rejected) continue;
                EditorGUILayout.LabelField("最近拒绝", DescribeDecision(entry), EditorStyles.wordWrappedLabel);
                break;
            }
        }

        // 展示能互相对照的角色代次、请求、行为执行、招式和具体播放编号。
        private static void DrawEntry(in CharacterActionTraceEntry entry)
        {
            string identity = $"状态代 {entry.StateVersion} / 连续代 {entry.Version} · 请求 {entry.Request} · 执行 {entry.Execution}";
            string detail;
            if (entry.Kind == CharacterActionTraceKind.Decision) detail = DescribeDecision(entry);
            else if (entry.Kind == CharacterActionTraceKind.BehaviorEnded)
                detail = $"行为结束 · {entry.Behavior} · {entry.BehaviorEnd}";
            else
            {
                string edge = entry.Kind == CharacterActionTraceKind.PlaybackStarted ? "实际开始" :
                    entry.Kind == CharacterActionTraceKind.PlaybackResumed ? "续播申请" : "播放终态";
                detail = $"{edge} · {entry.Key} · {entry.Status} · {(entry.HasStarted ? "已下发" : "未下发")} · {entry.Start}";
                if (entry.End != CharacterPlaybackEndSource.None) detail += $" · {entry.End}";
                identity += $" · 招式 {entry.Move} · 播放 {entry.Playback}";
            }
            EditorGUILayout.LabelField(detail, EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField(identity, EditorStyles.miniLabel);
        }

        // 从已发布结果读取实际原因、阻挡行为和缓存边沿。
        private static string DescribeDecision(in CharacterActionTraceEntry entry)
        {
            var decision = entry.Decision;
            string text = $"#{entry.Request} {entry.Behavior} · {decision.Status} · {decision.Time:F3}s";
            if (decision.Reason != CharacterBehaviorRejectReason.None) text += $" · 原因 {decision.Reason}";
            if (decision.Blocker != CharacterBehaviorId.None) text += $" · 阻挡行为 {decision.Blocker}";
            if (decision.BufferEvent != CharacterBufferEvent.None) text += $" · 缓存 {decision.BufferEvent}";
            return text;
        }
    }
}
