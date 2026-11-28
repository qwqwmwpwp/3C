using UnityEditor;

namespace ThreeC.CharacterBehavior
{
    // 查看所选宿主的实际播放回执，无需项目行为组件。
    internal sealed class AnimationPlaybackHistoryView
    {
        private readonly CharacterPlaybackHistoryEntry[] entries = new CharacterPlaybackHistoryEntry[CharacterPlaybackHistory.Capacity];
        private CharacterBehaviorAnimator selected;
        private int count;
        private bool expanded;

        // 切换目标时释放旧宿主的历史采集。
        internal void Clear()
        {
            if (selected) selected.PlaybackHistory.Enable(false);
            selected = null;
            count = 0;
        }

        // 展开期间冻结布局用的回执快照并展示终止来源。
        internal void Draw(CharacterBehaviorAnimator host)
        {
            expanded = EditorGUILayout.Foldout(expanded, "动作播放历史");
            var next = expanded ? host : null;
            if (next != selected)
            {
                Clear();
                selected = next;
                if (selected) selected.PlaybackHistory.Enable(true);
            }
            if (!selected) return;
            if (UnityEngine.Event.current.type == UnityEngine.EventType.Layout)
                count = selected.PlaybackHistory.CopyTo(entries);
            if (count == 0) EditorGUILayout.LabelField("等待动作下发或结束。");
            for (int i = 0; i < count; i++)
            {
                var entry = entries[i];
                EditorGUILayout.LabelField($"#{entry.PlaybackId} · {entry.Key} · {entry.Status} · {entry.EndSource}");
            }
        }
    }
}
