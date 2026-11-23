using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CustomPropertyDrawer(typeof(CharacterBehaviorChoiceAttribute))]
    internal sealed class CharacterBehaviorChoiceDrawer : PropertyDrawer
    {
        private static readonly CharacterBehaviorId[] choices = ((CharacterBehaviorId[])Enum.GetValues(typeof(CharacterBehaviorId)))
            .Where(IsSingleBehavior).Distinct().ToArray();
        private static readonly GUIContent[] labels = Array.ConvertAll(choices,
            id => new GUIContent(id == CharacterBehaviorId.Attack ? nameof(CharacterBehaviorId.Attack) : ObjectNames.NicifyVariableName(id.ToString())));

        private static bool IsSingleBehavior(CharacterBehaviorId id)
        {
            ulong bits = (ulong)id;
            return bits != 0 && (bits & (bits - 1)) == 0;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            using (var scope = new EditorGUI.PropertyScope(position, label, property))
            {
                bool previousMixed = EditorGUI.showMixedValue;
                EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
                int current = Array.IndexOf(choices, (CharacterBehaviorId)property.ulongValue);
                EditorGUI.BeginChangeCheck();
                int selected = EditorGUI.Popup(position, scope.content, current, labels);
                if (EditorGUI.EndChangeCheck() && selected >= 0)
                    property.ulongValue = (ulong)choices[selected];
                EditorGUI.showMixedValue = previousMixed;
            }
        }
    }
}
