using System;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 维护所选角色的匹配采集开关、历史列表和固定记录。
    internal sealed class CharacterMatchHistoryView
    {
        private static readonly GUILayoutOption ExpandRow = GUILayout.ExpandWidth(true);
        private static readonly GUIContent FoldoutLabel = new GUIContent("地面匹配决策", "展开采集当前角色，收起暂停采集并保留已录记录。");

        // 标记列表记录可以同时满足的筛选条件。
        [Flags]
        private enum Filter { None = 0, InputTurn = 1, Fallback = 2, Playback = 4 }

        // 缓存绘制一条历史所需的快照、摘要和筛选条件。
        private struct Row
        {
            internal CharacterMatchHistoryEntry Entry;
            internal GUIContent Label;
            internal Filter Flags;
        }

        private readonly Row[] rows = new Row[CharacterMatchDebugCapture.Capacity];
        private readonly int[] visibleSlots = new int[CharacterMatchDebugCapture.Capacity];
        private CharacterBehaviorAnimator character;
        private Animator animator;
        private bool selectionChanged;
        private bool expandRequested, expanded;
        private Filter requestedFilter, filter;
        private CharacterMatchHistoryEntry requestedPin, displayed;
        private bool pinned;
        private int totalCount, visibleCount;
        private Vector2 scroll;
        private GUIStyle rowStyle;

        // 在下一次布局切换会话前停止采集旧角色。
        internal void StopIfSelectionChanged(CharacterBehaviorAnimator next)
        {
            if (ReferenceEquals(character, next)) return;
            CharacterMatchDebugCapture.Select(null);
            selectionChanged = true;
        }

        // 结束采集会话并清除列表缓存及固定记录。
        internal void Clear()
        {
            CharacterMatchDebugCapture.Select(null);
            character = null;
            animator = null;
            selectionChanged = false;
            Array.Clear(rows, 0, rows.Length);
            totalCount = visibleCount = 0;
            requestedPin = displayed = null;
            pinned = false;
            scroll = Vector2.zero;
        }

        // 展开时采集并显示匹配历史，收起后保留已录记录。
        internal void Draw(CharacterBehaviorAnimator target)
        {
            if (Event.current.type == EventType.Layout) ReadLayout(target);
            expandRequested = EditorGUILayout.Foldout(expanded, FoldoutLabel, true);
            if (!expanded) return;

            DrawControls();
            if (totalCount == 0 && !animator)
                EditorGUILayout.LabelField("等待当前角色绑定 Animator。", EditorStyles.miniLabel);
            else if (totalCount == 0)
                EditorGUILayout.LabelField("等待当前角色提交地面匹配。", EditorStyles.miniLabel);
            else if (visibleCount == 0)
                EditorGUILayout.LabelField("没有同时满足当前筛选条件的记录。", EditorStyles.miniLabel);
            else
                DrawList();

            if (displayed != null) CharacterMatchHistoryDetail.Draw(displayed);
        }

        // 在布局阶段同步目标与采集状态，再冻结本轮界面使用的记录。
        private void ReadLayout(CharacterBehaviorAnimator target)
        {
            var nextAnimator = target.PreviewAnimator;
            if (selectionChanged || !ReferenceEquals(character, target))
            {
                Clear();
                character = target;
                animator = nextAnimator;
            }
            else if (!ReferenceEquals(animator, nextAnimator))
            {
                CharacterMatchDebugCapture.Rebind(animator, nextAnimator);
                animator = nextAnimator;
            }
            expanded = expandRequested;
            if (animator) CharacterMatchDebugCapture.SetRecording(animator, expanded);
            if (!expanded) return;

            // 点击或拖动列表期间保持行位置，防止新记录将按下和松开时的目标错开。
            if (GUIUtility.hotControl != 0) return;

            filter = requestedFilter;
            totalCount = CharacterMatchDebugCapture.Count(animator);
            visibleCount = 0;
            for (int offset = 0; offset < totalCount; offset++)
            {
                var entry = CharacterMatchDebugCapture.ReadNewest(animator, offset);
                int slot = (int)(entry.Sequence % rows.Length);
                ref var row = ref rows[slot];
                if (row.Entry == null || row.Entry.Sequence != entry.Sequence) UpdateRow(ref row, entry);
                if ((row.Flags & filter) == filter) visibleSlots[visibleCount++] = slot;
            }
            displayed = requestedPin ?? (visibleCount == 0 ? null : rows[visibleSlots[0]].Entry);
            pinned = requestedPin != null;
        }

        // 复制环形缓存中的新记录并生成结果与搜索原因摘要。
        private static void UpdateRow(ref Row row, CharacterMatchHistoryEntry entry)
        {
            row.Entry ??= new CharacterMatchHistoryEntry();
            row.Entry.CopyFrom(entry);
            var frame = entry.Frame;
            var trace = frame.Trace;
            row.Flags = Filter.None;
            if (trace.IsInputTurn) row.Flags |= Filter.InputTurn;
            if (trace.IsFallback) row.Flags |= Filter.Fallback;
            if (entry.Playback.SentBaselinePlayback) row.Flags |= Filter.Playback;

            string summary = $"#{entry.Sequence} · {CharacterMatchHistoryDetail.ChoiceLabel(frame.Choice)}";
            if ((row.Flags & Filter.InputTurn) != 0) summary += " · 急转";
            if ((row.Flags & Filter.Fallback) != 0) summary += " · 回退";
            if ((row.Flags & Filter.Playback) != 0) summary += " · 新播放";
            if (!string.IsNullOrEmpty(entry.Boundary)) summary += " · 分界";
            string state = entry.Playback.Mix.Base.Target;
            if (!string.IsNullOrEmpty(state)) summary += $" · {CharacterMatchHistoryDetail.TrackLabel(null, entry.Track)}";
            string search = string.IsNullOrEmpty(trace.SearchedLibraries) ? "未扫描库" : $"搜索 {trace.SearchedLibraries}";
            row.Label ??= new GUIContent();
            row.Label.text = $"{summary}\n{search} · {frame.Reason}";
            string binding = CharacterMatchHistoryDetail.TrackLabel(entry.Library, entry.Track);
            row.Label.tooltip = $"步 {entry.MotionStep} · {entry.Time:F3}s\n状态路径 {state ?? "无"}\n{binding}\n{frame.Query}\n{search} · {frame.Reason}";
        }

        // 绘制跟随状态、记录条数和可叠加的筛选条件。
        private void DrawControls()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string mode = pinned ? $"固定 #{displayed.Sequence}" : "跟随最新";
                EditorGUILayout.LabelField($"{mode} · {visibleCount}/{totalCount} 条", EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(!pinned))
                    if (GUILayout.Button("跟随最新", EditorStyles.miniButton)) requestedPin = null;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawFilter(Filter.InputTurn, "输入急转");
                DrawFilter(Filter.Fallback, "发生回退");
                DrawFilter(Filter.Playback, "下发新播放");
            }
        }

        // 切换一个筛选条件，由下一次布局重新计算列表。
        private void DrawFilter(Filter value, string label)
        {
            bool enabled = (filter & value) != 0;
            if (GUILayout.Toggle(enabled, label, EditorStyles.miniButton) == enabled) return;
            requestedFilter = enabled ? requestedFilter & ~value : requestedFilter | value;
            scroll = Vector2.zero;
        }

        // 显示由新到旧的筛选结果，点击行时独立保存该记录的详情。
        private void DrawList()
        {
            rowStyle ??= new GUIStyle(EditorStyles.miniButton) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            using (var list = new EditorGUILayout.ScrollViewScope(scroll, GUILayout.Height(148f)))
            {
                scroll = list.scrollPosition;
                for (int i = 0; i < visibleCount; i++)
                {
                    ref var row = ref rows[visibleSlots[i]];
                    bool active = displayed?.Sequence == row.Entry.Sequence;
                    Rect rect = GUILayoutUtility.GetRect(0f, 34f, ExpandRow);
                    if (GUI.Toggle(rect, active, row.Label, rowStyle) == active) continue;
                    requestedPin = new CharacterMatchHistoryEntry();
                    requestedPin.CopyFrom(row.Entry);
                }
            }
        }
    }
}
