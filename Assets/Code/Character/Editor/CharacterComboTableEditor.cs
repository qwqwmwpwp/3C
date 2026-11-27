using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 展示四招各自的片段、路线及三段秒数，实时计算只读总时长。
    [CustomEditor(typeof(CharacterComboTable)), CanEditMultipleObjects]
    internal sealed class CharacterComboTableEditor : Editor
    {
        // 使用序列化属性编辑原资产，支持撤销、多选与路线数组增删。
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var table = serializedObject.FindProperty(nameof(CharacterComboTable.Data));
            Draw(table, nameof(CharacterAttackTable.DashInterruptPhases), "允许突进打断的阶段");
            Draw(table, nameof(CharacterAttackTable.Openers), "起手路线");
            string useClipLength = null;
            DrawAttack(table, nameof(CharacterAttackTable.LightOne), "轻击一", ref useClipLength);
            DrawAttack(table, nameof(CharacterAttackTable.LightTwo), "轻击二", ref useClipLength);
            DrawAttack(table, nameof(CharacterAttackTable.Heavy), "重击", ref useClipLength);
            DrawAttack(table, nameof(CharacterAttackTable.SkillTwo), "技能二", ref useClipLength);
            serializedObject.ApplyModifiedProperties();
            if (useClipLength != null) UseClipLengths(serializedObject, useClipLength);
        }

        // 在具名招式内编辑通用参数，只有技能字段具备冷却时间。
        private static void DrawAttack(SerializedProperty table, string field, string label, ref string useClipLength)
        {
            var slot = table.FindPropertyRelative(field);
            slot.isExpanded = EditorGUILayout.Foldout(slot.isExpanded, label, true);
            if (!slot.isExpanded) return;
            using (new EditorGUI.IndentLevelScope())
            {
                Draw(slot, nameof(CharacterAttackDefinition.Name), "显示名称");
                var clip = Draw(slot, nameof(CharacterAttackMoveSettings.Clip), "攻击片段");
                var animation = Draw(slot, nameof(CharacterAttackMoveSettings.AnimationDurationSeconds), "动画持续时间（秒）");
                using (new EditorGUI.DisabledScope(!clip.hasMultipleDifferentValues && !clip.objectReferenceValue))
                    if (GUILayout.Button("使用片段原长")) useClipLength = slot.propertyPath;
                if (!animation.hasMultipleDifferentValues && (!(animation.floatValue > 0f) || float.IsInfinity(animation.floatValue)))
                    EditorGUILayout.HelpBox("动画持续时间必须是大于零的有限秒数。", MessageType.Warning);
                var entry = Draw(slot, nameof(CharacterAttackMoveSettings.EntryBlendSeconds), "进入缓入（秒）");
                var active = Draw(slot, nameof(CharacterAttackMoveSettings.ActiveSeconds), "主体持续（秒）");
                var recovery = Draw(slot, nameof(CharacterAttackMoveSettings.RecoverySeconds), "后摇淡出（秒）");
                bool mixed = entry.hasMultipleDifferentValues || active.hasMultipleDifferentValues || recovery.hasMultipleDifferentValues;
                string total = mixed ? "—" : (entry.floatValue + active.floatValue + recovery.floatValue).ToString("G7");
                EditorGUILayout.LabelField("状态总时长（秒）", total);
                if (field == nameof(CharacterAttackTable.SkillTwo))
                    Draw(slot, nameof(CharacterSkillAttackSettings.CooldownSeconds), "技能冷却（秒）");
                Draw(slot, nameof(CharacterAttackDefinition.Links), "后续路线");
            }
        }

        // 按每个选中资产自己的片段长度写入该招动画时长，并把这次批量操作合并为一次撤销。
        private static void UseClipLengths(SerializedObject selection, string path)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("使用攻击片段原长");
            foreach (var asset in selection.targetObjects)
            {
                using var target = new SerializedObject(asset);
                var attack = target.FindProperty(path);
                if (!(attack.FindPropertyRelative(nameof(CharacterAttackDefinition.Clip)).objectReferenceValue is AnimationClip clip) ||
                    !(clip.length > 0f) || float.IsInfinity(clip.length)) continue;
                attack.FindPropertyRelative(nameof(CharacterAttackMoveSettings.AnimationDurationSeconds)).floatValue = clip.length;
                target.ApplyModifiedProperties();
            }
            Undo.CollapseUndoOperations(group);
        }

        // 用中文标签绘制完整字段并返回本次编辑后的值。
        private static SerializedProperty Draw(SerializedProperty parent, string field, string label)
        {
            var value = parent.FindPropertyRelative(field);
            EditorGUILayout.PropertyField(value, new GUIContent(label), true);
            return value;
        }
    }

    // 为原生可排序路线数组的每个元素显示中文字段、归一化范围与时间单位。
    [CustomPropertyDrawer(typeof(CharacterAttackLink))]
    internal sealed class CharacterAttackLinkDrawer : PropertyDrawer
    {
        private static readonly (string field, GUIContent label)[] Fields = {
            (nameof(CharacterAttackLink.Trigger), new GUIContent("触发方式")),
            (nameof(CharacterAttackLink.Button), new GUIContent("按键")),
            (nameof(CharacterAttackLink.To), new GUIContent("目标招式")),
            (nameof(CharacterAttackLink.OpeningCondition), new GUIContent("起手移动条件")),
            (nameof(CharacterAttackLink.LinkStart), new GUIContent("开放接续 / 定时进度（0～1）")),
            (nameof(CharacterAttackLink.LinkEnd), new GUIContent("关闭输入进度（0～1）")),
            (nameof(CharacterAttackLink.EarlySeconds), new GUIContent("提前输入（秒）")),
            (nameof(CharacterAttackLink.Result), new GUIContent("匹配状态结果（非零）"))
        };

        // 根据元素折叠状态计算标题与八个原生属性控件需要的高度。
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float line = EditorGUIUtility.singleLineHeight;
            return property.isExpanded ? line + Fields.Length * (line + EditorGUIUtility.standardVerticalSpacing) : line;
        }

        // 通过 Unity 序列化字段绘制当前路线，使多选、撤销和原生数组排序继续生效。
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            using (new EditorGUI.PropertyScope(position, label, property))
            {
                position.height = EditorGUIUtility.singleLineHeight;
                string path = property.propertyPath;
                int index = path.LastIndexOf('[');
                string title = index >= 0 && int.TryParse(path.Substring(index + 1, path.Length - index - 2), out int number)
                    ? $"路线 {number + 1}" : "路线";
                property.isExpanded = EditorGUI.Foldout(position, property.isExpanded, title, true);
                if (!property.isExpanded) return;
                using (new EditorGUI.IndentLevelScope())
                    foreach (var item in Fields)
                    {
                        position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
                        EditorGUI.PropertyField(position, property.FindPropertyRelative(item.field), item.label);
                    }
            }
        }
    }
}
