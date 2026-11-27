using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 为 ulong 行为集合提供逐位勾选，保留枚举别名以外的所有既有高位。
    [CustomPropertyDrawer(typeof(CharacterBehaviorMaskAttribute))]
    internal sealed class CharacterBehaviorMaskDrawer : PropertyDrawer
    {
        // 显示当前行为集合，仅在用户点击菜单项时修改对应位。
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            using var scope = new EditorGUI.PropertyScope(position, label, property);
            var field = EditorGUI.PrefixLabel(position, scope.content);
            ulong value = property.ulongValue;
            string caption = property.hasMultipleDifferentValues ? "—" : value == 0 ? "无" : ((CharacterBehaviorId)value).ToString();
            if (!EditorGUI.DropdownButton(field, new GUIContent(caption), FocusType.Keyboard)) return;

            var menu = new GenericMenu();
            var targets = property.serializedObject.targetObjects;
            string path = property.propertyPath;
            menu.AddItem(new GUIContent("无"), value == 0 && !property.hasMultipleDifferentValues, () => SetBit(targets, path, 0, false));
            ulong common = ulong.MaxValue;
            foreach (var target in targets) common &= ReadBits(target, path);
            for (int index = 0; index < CharacterBehaviorBitChoices.Ids.Length; index++)
            {
                ulong bit = (ulong)CharacterBehaviorBitChoices.Ids[index];
                bool selected = (common & bit) != 0;
                menu.AddItem(CharacterBehaviorBitChoices.Labels[index], selected, () => SetBit(targets, path, bit, !selected));
            }
            menu.DropDown(field);
        }

        // 每个所选对象仅读取一次完整掩码，用于计算混合选择中共同勾选的位。
        private static ulong ReadBits(UnityEngine.Object target, string path)
        {
            using var serialized = new SerializedObject(target);
            var field = serialized.FindProperty(path);
            return field?.ulongValue ?? 0UL;
        }

        // 对每个对象保留其他位，显式“无”才清空整个集合。
        private static void SetBit(UnityEngine.Object[] targets, string path, ulong bit, bool enable)
        {
            foreach (var target in targets)
            {
                if (!target) continue;
                using var serialized = new SerializedObject(target);
                var field = serialized.FindProperty(path);
                if (field == null) continue;
                field.ulongValue = bit == 0 ? 0UL : enable ? field.ulongValue | bit : field.ulongValue & ~bit;
                serialized.ApplyModifiedProperties();
            }
        }
    }
}
