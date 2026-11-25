using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在匹配模块 Inspector 显示当前库能使用的特征布局和规则表诊断。
    [CustomEditor(typeof(MotionMatchModule))]
    internal sealed class GroundMatchModuleInspector : Editor
    {
        // 根据当前序列化配置展示兼容回退及需重烘焙的布局。
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var db = serializedObject.FindProperty("database").objectReferenceValue as CharacterMotionDatabase;
            if (!db || !db.Groups) return;
            var layout = new GroundFeatureLayout(db.Groups.Schema, db.BakedTracks);
            EditorGUILayout.HelpBox(layout.Status, db.Groups.Schema && !layout.Active ? MessageType.Warning : MessageType.Info);
            var table = new GroundCandidatePolicy(((MotionMatchModule)target).candidates, db);
            if (table.Issue != null) EditorGUILayout.HelpBox(table.Issue + "；根据条件表的 No Match 处理。", MessageType.Warning);
        }
    }
}
