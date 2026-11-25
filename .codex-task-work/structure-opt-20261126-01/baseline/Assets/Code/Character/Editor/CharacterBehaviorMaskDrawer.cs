using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 为 ulong 行为集合提供逐位勾选，保留枚举别名以外的所有既有高位。
    [CustomPropertyDrawer(typeof(CharacterBehaviorMaskAttribute))]
    internal sealed class CharacterBehaviorMaskDrawer : PropertyDrawer
    {
        private static readonly ulong[] bits = ((CharacterBehaviorId[])Enum.GetValues(typeof(CharacterBehaviorId)))
            .Select(id => (ulong)id).Where(bit => bit != 0 && (bit & (bit - 1)) == 0).Distinct().ToArray();

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
            foreach (ulong bit in bits)
            {
                bool selected = Array.TrueForAll(targets, target => HasBit(target, path, bit));
                var name = bit == (ulong)CharacterBehaviorId.Attack ? nameof(CharacterBehaviorId.Attack) : ((CharacterBehaviorId)bit).ToString();
                menu.AddItem(new GUIContent(name), selected, () => SetBit(targets, path, bit, !selected));
            }
            menu.DropDown(field);
        }

        // 单独读取每个所选对象对应的位，支持混合值编辑。
        private static bool HasBit(UnityEngine.Object target, string path, ulong bit)
        {
            using var serialized = new SerializedObject(target);
            var field = serialized.FindProperty(path);
            return field != null && (field.ulongValue & bit) != 0;
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
