using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在候选表的原生规则数组中启用候选来源的 IMGUI 属性编辑。
    [CustomEditor(typeof(GroundCandidateTable)), CanEditMultipleObjects]
    internal sealed class GroundCandidateTableInspector : Editor
    {
        // 保留规则表的原生字段和数组行为。
        public override void OnInspectorGUI() => DrawDefaultInspector();
    }

    // 使用来源库中的组名称编辑库内 ID，保留失效槽的显式筛选范围。
    [CustomPropertyDrawer(typeof(GroundCandidateSource))]
    internal sealed class GroundCandidateSourceDrawer : PropertyDrawer
    {
        private static readonly string[] Scopes = { "全库", "指定动作组" };
        private static readonly StringComparer GroupIdComparer = StringComparer.OrdinalIgnoreCase;
        private static float RowStep => EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

        // 为来源的折叠标题、范围选择和当前已保存的槽计算高度。
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = EditorGUIUtility.singleLineHeight;
            if (!property.isExpanded) return height;
            if (property.serializedObject.isEditingMultipleObjects) return height + RowStep;
            int slots = property.FindPropertyRelative(nameof(GroundCandidateSource.GroupIds)).arraySize;
            return height + (slots == 0 ? 2 : 3 + slots) * RowStep;
        }

        // 绘制来源库及其筛选范围，多选时保持各资产已配置的来源不被覆盖。
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var libraryField = property.FindPropertyRelative(nameof(GroundCandidateSource.Library));
            var ids = property.FindPropertyRelative(nameof(GroundCandidateSource.GroupIds));
            var library = libraryField.objectReferenceValue as CharacterMotionDatabase;
            bool multiEdit = property.serializedObject.isEditingMultipleObjects;
            position.height = EditorGUIUtility.singleLineHeight;
            var title = !multiEdit && library ? new GUIContent(library.name) : label;
            property.isExpanded = EditorGUI.Foldout(position, property.isExpanded, title, true);
            if (!property.isExpanded) return;

            using var indent = new EditorGUI.IndentLevelScope();
            position.y += RowStep;
            if (multiEdit)
            {
                EditorGUI.LabelField(position, "请选择一张候选表来编辑来源。");
                return;
            }

            // 库和槽同时提交，库字段不使用可独立粘贴对象引用的原生属性菜单。
            EditorGUI.BeginChangeCheck();
            var selected = (CharacterMotionDatabase)EditorGUI.ObjectField(position, "来源库", library,
                typeof(CharacterMotionDatabase), false);
            if (EditorGUI.EndChangeCheck() && selected != library)
            {
                libraryField.objectReferenceValue = selected;
                for (int i = 0; i < ids.arraySize; i++)
                    ids.GetArrayElementAtIndex(i).stringValue = string.Empty;
                return;
            }

            position.y += RowStep;
            int scope = ids.arraySize == 0 ? 0 : 1;
            using (new EditorGUI.DisabledScope(!library))
            {
                int chosen = EditorGUI.Popup(position, "筛选范围", scope, Scopes);
                if (chosen != scope)
                {
                    if (chosen == 0) ids.ClearArray();
                    else AddEmptySlot(ids);
                    return;
                }
            }
            if (scope == 0) return;
            DrawSlots(position, property, library, ids);
        }

        // 展示每个现有槽，保留缺失 ID，并仅在有可选组时提供追加。
        private static void DrawSlots(Rect position, SerializedProperty source, CharacterMotionDatabase library, SerializedProperty ids)
        {
            var groups = library && library.Groups ? library.Groups.IndexGroups(out _) : null;
            for (int i = 0; i < ids.arraySize; i++)
            {
                position.y += RowStep;
                Rect row = EditorGUI.IndentedRect(position);
                Rect remove = new Rect(row.xMax - 25f, row.y, 25f, row.height);
                row.xMax = remove.xMin - 3f;
                DrawGroup(row, source, library, groups, ids, i);

                bool last = ids.arraySize == 1;
                using (new EditorGUI.DisabledScope(last))
                {
                    var removeLabel = new GUIContent("−", last ? "如需使用全库，请切换筛选范围。" : "移除此动作组");
                    if (!GUI.Button(remove, removeLabel)) continue;
                    ids.DeleteArrayElementAtIndex(i);
                    return;
                }
            }

            position.y += RowStep;
            using (new EditorGUI.DisabledScope(groups == null || groups.Count == 0 || HasUnselectedSlot(ids) ||
                !HasAvailableGroup(groups, ids)))
                if (GUI.Button(EditorGUI.IndentedRect(position), "添加动作组"))
                    AddEmptySlot(ids);
        }

        // 在选择菜单中显示仍有效的组名，失效槽的标签和提示显示原始 ID。
        private static void DrawGroup(Rect row, SerializedProperty source, CharacterMotionDatabase library,
            Dictionary<string, int> groups, SerializedProperty ids, int index)
        {
            string id = ids.GetArrayElementAtIndex(index).stringValue;
            var current = groups != null && !string.IsNullOrEmpty(id) && groups.TryGetValue(id, out int currentIndex)
                ? library.Groups.Groups[currentIndex] : null;
            var caption = current != null ? new GUIContent(GroupName(current), $"动作组 ID {current.GroupId}") :
                new GUIContent(string.IsNullOrEmpty(id) ? "⚠ 请选择动作组" : "⚠ 失效动作组 · " + id,
                    string.IsNullOrEmpty(id) ? "该槽需要从来源库中选择动作组。" : "来源库已无此 ID 对应的有效动作组：" + id);
            using var disabled = new EditorGUI.DisabledScope(groups == null || groups.Count == 0);
            if (!EditorGUI.DropdownButton(row, caption, FocusType.Keyboard)) return;

            var menu = new GenericMenu();
            var owner = source.serializedObject.targetObject;
            string path = source.propertyPath;
            var nameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (int groupIndex in groups.Values)
            {
                string name = GroupName(library.Groups.Groups[groupIndex]).Replace('/', '／');
                nameCounts.TryGetValue(name, out int count);
                nameCounts[name] = count + 1;
            }
            foreach (var group in library.Groups.Groups)
            {
                if (string.IsNullOrEmpty(group?.GroupId) || !groups.ContainsKey(group.GroupId)) continue;
                string groupId = group.GroupId;
                bool selected = GroupIdComparer.Equals(id, groupId);
                if (!selected && ContainsId(ids, groupId, index)) continue;
                string menuLabel = GroupName(group).Replace('/', '／');
                if (nameCounts[menuLabel] > 1) menuLabel += $"（第 {groups[groupId] + 1} 组）";
                menu.AddItem(new GUIContent(menuLabel), selected, () => SetGroup(owner, path, library, index, id, groupId));
            }
            if (menu.GetItemCount() == 0) menu.AddDisabledItem(new GUIContent("没有其他可选动作组"));
            menu.DropDown(row);
        }

        // 只有来源库和目标槽仍与打开菜单时一致，才提交菜单项携带的稳定 ID。
        private static void SetGroup(UnityEngine.Object owner, string path, CharacterMotionDatabase library, int index,
            string previousId, string chosenId)
        {
            if (!owner || !library || !library.Groups || !library.Groups.IndexGroups(out _).ContainsKey(chosenId)) return;
            using var serialized = new SerializedObject(owner);
            var source = serialized.FindProperty(path);
            if (source == null || source.FindPropertyRelative(nameof(GroundCandidateSource.Library)).objectReferenceValue != library) return;
            var ids = source.FindPropertyRelative(nameof(GroundCandidateSource.GroupIds));
            if (index >= ids.arraySize || ContainsId(ids, chosenId, index)) return;
            var slot = ids.GetArrayElementAtIndex(index);
            if (!GroupIdComparer.Equals(slot.stringValue, previousId)) return;
            slot.stringValue = chosenId;
            serialized.ApplyModifiedProperties();
        }

        // 为未命名的选项提供可定位的菜单名称。
        private static string GroupName(GroundAnimationGroup group) =>
            string.IsNullOrWhiteSpace(group.Name) ? $"未命名动作组 · {group.GroupId.Substring(0, 8)}" : group.Name;

        // 判断选择中是否还留有一个待填写的空槽。
        private static bool HasUnselectedSlot(SerializedProperty ids)
        {
            for (int i = 0; i < ids.arraySize; i++)
                if (string.IsNullOrEmpty(ids.GetArrayElementAtIndex(i).stringValue)) return true;
            return false;
        }

        // 检查该来源是否还能增加一个尚未声明的有效组。
        private static bool HasAvailableGroup(Dictionary<string, int> groups, SerializedProperty ids)
        {
            foreach (string id in groups.Keys)
                if (!ContainsId(ids, id, -1)) return true;
            return false;
        }

        // 按库内 ID 的比较规则查找其他已选槽。
        private static bool ContainsId(SerializedProperty ids, string id, int except)
        {
            for (int i = 0; i < ids.arraySize; i++)
                if (i != except && GroupIdComparer.Equals(ids.GetArrayElementAtIndex(i).stringValue, id)) return true;
            return false;
        }

        // 插入显式待选择的槽，避免 Unity 扩展数组时复制末项 ID。
        private static void AddEmptySlot(SerializedProperty ids)
        {
            int index = ids.arraySize;
            ids.arraySize++;
            ids.GetArrayElementAtIndex(index).stringValue = string.Empty;
        }
    }
}
