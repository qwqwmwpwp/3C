using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在项目适配器上显示行为、招式与当前动画实例的对应关系。
    [CustomEditor(typeof(CharacterAnimationAdapter))]
    internal sealed class CharacterAnimationAdapterEditor : Editor
    {
        private readonly CharacterAnimationActionHistoryView history = new CharacterAnimationActionHistoryView();
        private readonly Vector3[] actualPath = new Vector3[128];
        private bool showActualPath;

        // 绘制当前适配器实际消费的业务记录和驱动器预测。
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            if (!Application.isPlaying) return;
            var adapter = (CharacterAnimationAdapter)target;
            history.Draw(adapter);
            var driver = adapter.GetComponent<CharacterBehaviorDriver>();
            if (!driver) return;
            EditorGUILayout.LabelField("移动预测", driver.DescribeForecast(), EditorStyles.wordWrappedLabel);
            showActualPath = EditorGUILayout.Foldout(showActualPath, "实际路径（俯视）");
            if (showActualPath) DrawActualPath(driver);
        }

        // 关闭检查器后停止跟踪行为请求。
        private void OnDisable() => history.Clear();

        // 播放中刷新当前选中角色的最新接入结果。
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        // 在俯视图中绘制角色实际移动经过的路径。
        private void DrawActualPath(CharacterBehaviorDriver driver)
        {
            Rect rect = GUILayoutUtility.GetRect(10f, 90f, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;
            int count = driver.CopyActualPath(actualPath);
            if (count < 2) return;
            var bounds = new Bounds(actualPath[0], Vector3.zero);
            for (int i = 1; i < count; i++) bounds.Encapsulate(actualPath[i]);
            float scale = Mathf.Min((rect.width - 8f) / Mathf.Max(0.1f, bounds.size.x),
                (rect.height - 8f) / Mathf.Max(0.1f, bounds.size.z));
            // 将实际位置的右前坐标映射为图表像素。
            Vector2 Project(Vector3 p) => rect.center + new Vector2(p.x - bounds.center.x, bounds.center.z - p.z) * scale;
            Handles.BeginGUI();
            using (new Handles.DrawingScope(Color.white))
                for (int i = 1; i < count; i++) Handles.DrawLine(Project(actualPath[i - 1]), Project(actualPath[i]), 2f);
            Handles.EndGUI();
        }

    }
}
