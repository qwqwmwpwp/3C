using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在匹配模块 Inspector 显示当前库能使用的特征布局和规则表诊断。
    [CustomEditor(typeof(MotionMatchModule))]
    internal sealed class GroundMatchModuleInspector : Editor
    {
        // 根据当前序列化配置定位布局资源，说明生效的评分权重及规则表诊断。
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var db = serializedObject.FindProperty("database").objectReferenceValue as CharacterMotionDatabase;
            if (!db || !db.Groups) return;
            var schema = db.Groups.Schema;
            var layout = new GroundFeatureLayout(schema, db.BakedTracks);
            if (schema)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField("动作库特征布局", schema, typeof(GroundFeatureSchema), false);
                    if (GUILayout.Button("定位", GUILayout.ExpandWidth(false))) EditorGUIUtility.PingObject(schema);
                }
            }
            string scoring = layout.Active
                ? "布局通道代价 = 误差平方 × Weight ÷ Scale²。接触使用当前用途的 Contact 权重与 ContactScale 尺度，步频使用当前用途的 Pace 权重与 PaceScale 尺度。"
                : "兼容评分使用上方的用途权重和尺度，包括 Position、Velocity、Path 和 Facing。";
            EditorGUILayout.HelpBox($"{layout.Status}\n{scoring}", schema && !layout.Active ? MessageType.Warning : MessageType.Info);
            var table = new GroundCandidatePolicy(((MotionMatchModule)target).candidates, db);
            if (table.Issue != null) EditorGUILayout.HelpBox(table.Issue + "；根据条件表的 No Match 处理。", MessageType.Warning);
        }
    }
}
