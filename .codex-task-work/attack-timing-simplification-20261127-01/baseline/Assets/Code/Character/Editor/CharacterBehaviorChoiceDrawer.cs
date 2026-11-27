using System;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 为单选与多选行为字段共享全部合法单比特及其可识别名称。
    internal static class CharacterBehaviorBitChoices
    {
        internal static readonly CharacterBehaviorId[] Ids = new CharacterBehaviorId[64];
        internal static readonly GUIContent[] Labels = new GUIContent[64];

        // 内置位沿用原名，扩展位显示位号及精确十六进制值。
        static CharacterBehaviorBitChoices()
        {
            for (int index = 0; index < Ids.Length; index++)
            {
                ulong bit = 1UL << index;
                var id = (CharacterBehaviorId)bit;
                Ids[index] = id;
                string name = id == CharacterBehaviorId.Attack ? nameof(CharacterBehaviorId.Attack) : Enum.GetName(typeof(CharacterBehaviorId), id);
                Labels[index] = new GUIContent(name == null ? $"自定义位 {index} (0x{bit:X})" : ObjectNames.NicifyVariableName(name));
            }
        }
    }

    // 为启动与持续许可映射选择一个行为编号，未操作时保留原序列化值。
    [CustomPropertyDrawer(typeof(CharacterBehaviorChoiceAttribute))]
    internal sealed class CharacterBehaviorChoiceDrawer : PropertyDrawer
    {
        // 呈现多对象混合值，只在明确选择后写入一个有效单比特。
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            using (var scope = new EditorGUI.PropertyScope(position, label, property))
            {
                bool previousMixed = EditorGUI.showMixedValue;
                EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
                int current = Array.IndexOf(CharacterBehaviorBitChoices.Ids, (CharacterBehaviorId)property.ulongValue);
                EditorGUI.BeginChangeCheck();
                int selected = EditorGUI.Popup(position, scope.content, current, CharacterBehaviorBitChoices.Labels);
                if (EditorGUI.EndChangeCheck() && selected >= 0)
                    property.ulongValue = (ulong)CharacterBehaviorBitChoices.Ids[selected];
                EditorGUI.showMixedValue = previousMixed;
            }
        }
    }
}
