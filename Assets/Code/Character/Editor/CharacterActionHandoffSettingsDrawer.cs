using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 隐去具名攻击已由状态定义的交接参数，其他动作保留原有字段与折叠编辑。
    [CustomPropertyDrawer(typeof(CharacterActionHandoffSettings))]
    internal sealed class CharacterActionHandoffSettingsDrawer : PropertyDrawer
    {
        // 按显示的子字段高度预留空间，攻击动作不占用交接配置行。
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (UsesAttackTiming(property)) return -EditorGUIUtility.standardVerticalSpacing;
            float height = EditorGUIUtility.singleLineHeight;
            if (property.isExpanded)
                foreach (SerializedProperty child in property.Copy())
                    height += EditorGUIUtility.standardVerticalSpacing + EditorGUI.GetPropertyHeight(child, true);
            return height;
        }

        // 为普通动作绘制现有交接参数，保留序列化撤销与预制体覆盖标记。
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (UsesAttackTiming(property)) return;
            using (new EditorGUI.PropertyScope(position, label, property))
            {
                position.height = EditorGUIUtility.singleLineHeight;
                property.isExpanded = EditorGUI.Foldout(position, property.isExpanded, label, true);
                if (!property.isExpanded) return;
                using (new EditorGUI.IndentLevelScope())
                    foreach (SerializedProperty child in property.Copy())
                    {
                        position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
                        position.height = EditorGUI.GetPropertyHeight(child, true);
                        EditorGUI.PropertyField(position, child, true);
                    }
            }
        }

        // 仅对游戏映射中使用招式时钟的字段隐藏通用交接设置。
        private static bool UsesAttackTiming(SerializedProperty property)
        {
            if (!(property.serializedObject.targetObject is CharacterBehaviorAnimationProfile)) return false;
            int end = property.propertyPath.LastIndexOf('.');
            if (end < 0) return false;
            var row = property.serializedObject.FindProperty(property.propertyPath.Substring(0, end));
            return row != null && IsAttackMapping(row);
        }

        // 只对所有选中对象都指向具名攻击的单次映射采用状态时钟。
        private static bool IsAttackMapping(SerializedProperty row)
        {
            var behavior = row.FindPropertyRelative("Behavior");
            var trigger = row.FindPropertyRelative(nameof(CharacterBehaviorAnimationMapping.Trigger));
            var state = row.FindPropertyRelative(nameof(CharacterBehaviorAnimationMapping.AttackStateId));
            return behavior != null && !behavior.hasMultipleDifferentValues && behavior.longValue == (long)CharacterBehaviorId.Attack &&
                !trigger.hasMultipleDifferentValues && trigger.intValue == (int)CharacterAnimationTrigger.OnStart &&
                !state.hasMultipleDifferentValues && state.intValue != 0;
        }
    }
}
