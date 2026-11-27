using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 通过受控列表编辑动作组，使新增和复制各自生成独立且可撤销的库内 ID。
    [CustomEditor(typeof(CharacterMotionGroups)), CanEditMultipleObjects]
    internal sealed class CharacterMotionGroupsInspector : Editor
    {
        private ReorderableList groupList;

        // 配置仅由本 Inspector 管理结构操作的可拖动列表。
        private void OnEnable()
        {
            if (serializedObject.isEditingMultipleObjects) return;
            var groups = serializedObject.FindProperty(nameof(CharacterMotionGroups.Groups));
            groupList = new ReorderableList(serializedObject, groups, true, true, true, true)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, groups.displayName),
                drawElementCallback = DrawGroupRow,
                onAddCallback = _ => AddGroup(),
                onRemoveCallback = RemoveGroup
            };
            groupList.index = groups.arraySize > 0 ? 0 : -1;
        }

        // 保留基础字段的原生编辑，将动作组结构修改限定在单个数据组资产。
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
            DrawPropertiesExcluding(serializedObject, "m_Script", nameof(CharacterMotionGroups.Groups));
            if (serializedObject.isEditingMultipleObjects)
                EditorGUILayout.HelpBox("请选择单个数据组资产来编辑地面动作组。", MessageType.Info);
            else
                DrawGroups();
            serializedObject.ApplyModifiedProperties();
        }

        // 绘制列表及所选组的可编辑字段，保留每个字段原生的属性控件。
        private void DrawGroups()
        {
            groupList.DoLayoutList();
            ((CharacterMotionGroups)target).IndexGroups(out string issue);
            if (issue != null) EditorGUILayout.HelpBox(issue, MessageType.Warning);

            var groups = groupList.serializedProperty;
            int index = groupList.index;
            if (index < 0 || index >= groups.arraySize) return;
            var group = groups.GetArrayElementAtIndex(index);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(GroupLabel(group, index), EditorStyles.boldLabel);
                if (GUILayout.Button("复制动作组", GUILayout.ExpandWidth(false)))
                    DuplicateGroup(group);
            }
            if (groupList.index != index) return;

            using (new EditorGUI.IndentLevelScope())
            {
                var field = group.Copy();
                var end = group.GetEndProperty();
                for (bool enter = true; field.NextVisible(enter) && !SerializedProperty.EqualContents(field, end); enter = false)
                    EditorGUILayout.PropertyField(field, true);
            }
        }

        // 列表行只展示组名，避免原生数组元素的复制命令连同 ID 一并复制。
        private void DrawGroupRow(Rect rect, int index, bool active, bool focused)
        {
            var group = groupList.serializedProperty.GetArrayElementAtIndex(index);
            rect.height = EditorGUIUtility.singleLineHeight;
            EditorGUI.LabelField(rect, GroupLabel(group, index));
        }

        // 组尚未命名时仍提供可定位的列表标题。
        private static string GroupLabel(SerializedProperty group, int index)
        {
            string name = group.FindPropertyRelative(nameof(GroundAnimationGroup.Name)).stringValue;
            return string.IsNullOrWhiteSpace(name) ? $"未命名动作组（第 {index + 1} 组）" : name;
        }

        // 用动作组默认值创建空白组，避免继承 Unity 扩容时留下的元素内容。
        private void AddGroup() => AppendGroup(new GroundAnimationGroup { Name = "新动作组" });

        // 通过序列化值完整复制组配置，在追加时分配独立 ID。
        private void DuplicateGroup(SerializedProperty source)
        {
            AppendGroup((GroundAnimationGroup)source.boxedValue);
            var copy = groupList.serializedProperty.GetArrayElementAtIndex(groupList.index);
            var name = copy.FindPropertyRelative(nameof(GroundAnimationGroup.Name));
            name.stringValue = string.IsNullOrWhiteSpace(name.stringValue) ? "动作组副本" : name.stringValue + " 副本";
        }

        // 在同一次序列化编辑中写入组内容与新 ID，使撤销和重做恢复同一身份。
        private void AppendGroup(GroundAnimationGroup value)
        {
            var groups = groupList.serializedProperty;
            string id = Guid.NewGuid().ToString("N");
            int index = groups.arraySize;
            groups.arraySize++;
            var added = groups.GetArrayElementAtIndex(index);
            added.boxedValue = value;
            added.FindPropertyRelative(nameof(GroundAnimationGroup.GroupId)).stringValue = id;
            groupList.index = index;
        }

        // 从数据组中移除选中项，候选表中的 ID 继续保留以便定位失效引用。
        private static void RemoveGroup(ReorderableList list)
        {
            var groups = list.serializedProperty;
            int index = list.index;
            if (index < 0 || index >= groups.arraySize) return;
            groups.DeleteArrayElementAtIndex(index);
            list.index = Mathf.Min(index, groups.arraySize - 1);
        }
    }
}
