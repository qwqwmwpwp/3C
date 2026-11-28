using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在可复用候选表中只编辑角色无关的逻辑库键、组键和必需性。
    [CustomPropertyDrawer(typeof(GroundCandidateSource))]
    internal sealed class GroundCandidateSourceDrawer : PropertyDrawer
    {
        private static float Step => EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

        // 展开时为逻辑来源和原生组键数组预留显示高度。
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            !property.isExpanded ? EditorGUIUtility.singleLineHeight :
            3f * Step + EditorGUI.GetPropertyHeight(property.FindPropertyRelative(nameof(GroundCandidateSource.GroupKeys)), true);

        // 以逻辑库键标记来源，空组键列表表示全库。
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var key = property.FindPropertyRelative(nameof(GroundCandidateSource.LibraryKey));
            var optional = property.FindPropertyRelative(nameof(GroundCandidateSource.Optional));
            var groups = property.FindPropertyRelative(nameof(GroundCandidateSource.GroupKeys));
            EditorGUI.BeginProperty(position, label, property);
            position.height = EditorGUIUtility.singleLineHeight;
            var title = string.IsNullOrEmpty(key.stringValue) || key.hasMultipleDifferentValues ? label : new GUIContent(key.stringValue);
            property.isExpanded = EditorGUI.Foldout(position, property.isExpanded, title, true);
            if (property.isExpanded)
            {
                using var indent = new EditorGUI.IndentLevelScope();
                position.y += Step;
                EditorGUI.PropertyField(position, key);
                position.y += Step;
                EditorGUI.PropertyField(position, optional);
                position.y += Step;
                position.height = EditorGUI.GetPropertyHeight(groups, true);
                EditorGUI.PropertyField(position, groups, true);
            }
            EditorGUI.EndProperty();
        }
    }

    // 在角色库绑定中提供按源组名称选择稳定 ID 的编辑入口。
    [CustomPropertyDrawer(typeof(GroundGroupBinding))]
    internal sealed class GroundGroupBindingDrawer : PropertyDrawer
    {
        // 为逻辑组键和库内组选择分别保留一行。
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            2f * EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

        // 在同一逻辑库声明中查找数据库，组选择仅写入当前绑定项的稳定 ID。
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            position.height = EditorGUIUtility.singleLineHeight;
            EditorGUI.PropertyField(position, property.FindPropertyRelative(nameof(GroundGroupBinding.Key)));
            position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
            var id = property.FindPropertyRelative(nameof(GroundGroupBinding.GroupId));
            string path = property.propertyPath;
            int parent = path.LastIndexOf(".Groups.Array.data[", StringComparison.Ordinal);
            var database = parent < 0 ? null : property.serializedObject.FindProperty(path.Substring(0, parent))
                ?.FindPropertyRelative(nameof(GroundLibraryBinding.Database));
            var source = database?.objectReferenceValue as CharacterMotionDatabase;
            if (!source || !source.Groups || property.serializedObject.isEditingMultipleObjects)
                EditorGUI.PropertyField(position, id);
            else
                DrawGroup(position, source.Groups, id);
            EditorGUI.EndProperty();
        }

        // 显示源组的可读名称与序号，保留缺失 ID 直到作者显式选择其他组。
        private static void DrawGroup(Rect position, CharacterMotionGroups source, SerializedProperty id)
        {
            var labels = new List<string> { string.IsNullOrEmpty(id.stringValue) ? "请选择动作组" : $"失效 ID · {id.stringValue}" };
            var ids = new List<string> { null };
            int selected = 0;
            foreach (var pair in source.IndexGroups(out _))
            {
                string name = source.Groups[pair.Value].Name;
                labels.Add($"{pair.Value + 1}. {name}");
                ids.Add(pair.Key);
                if (string.Equals(id.stringValue, pair.Key, StringComparison.OrdinalIgnoreCase)) selected = ids.Count - 1;
            }
            int next = EditorGUI.Popup(position, "库内动作组", selected, labels.ToArray());
            if (next != selected && next > 0) id.stringValue = ids[next];
        }
    }
}
