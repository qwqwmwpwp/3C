using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在总表中选择可创建的具体攻击定义，并直接编辑该定义自己的序列化字段。
    [CustomPropertyDrawer(typeof(CharacterAttackDefinition), true)]
    internal sealed class CharacterAttackDefinitionDrawer : PropertyDrawer
    {
        private static readonly Type[] Types = TypeCache.GetTypesDerivedFrom<CharacterAttackDefinition>()
            .Where(type => !type.IsAbstract && !type.ContainsGenericParameters && type.IsSerializable &&
                type.GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null) != null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();

        // 按字段实际展开高度给不同状态的数据预留空间。
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = EditorGUIUtility.singleLineHeight;
            if (property.isExpanded && !property.serializedObject.isEditingMultipleObjects)
                EachChild(property, child => height += EditorGUIUtility.standardVerticalSpacing + EditorGUI.GetPropertyHeight(child, true));
            return height;
        }

        // 显示类型菜单及其子字段，多选时避免将托管引用实例跨资产共享。
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            bool multi = property.serializedObject.isEditingMultipleObjects;
            var field = EditorGUI.PrefixLabel(line, GUIContent.none);
            var caption = new Rect(line.x, line.y, EditorGUIUtility.labelWidth, line.height);
            property.isExpanded = EditorGUI.Foldout(caption, property.isExpanded, label, true);
            field.xMin = caption.xMax;
            using (new EditorGUI.DisabledScope(multi))
            {
                string current = multi ? "请单选资产以编辑状态定义" : TypeName(property.managedReferenceValue?.GetType());
                if (GUI.Button(field, current, EditorStyles.popup)) ShowTypes(property);
            }
            if (property.isExpanded && !multi)
            {
                EditorGUI.indentLevel++;
                EachChild(property, child => {
                    line.y += line.height + EditorGUIUtility.standardVerticalSpacing;
                    line.height = EditorGUI.GetPropertyHeight(child, true);
                    EditorGUI.PropertyField(line, child, true);
                });
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndProperty();
        }

        // 为内置普通招式提供中文名称，其他实现直接显示类型全名以避免重名。
        private static string TypeName(Type type) => type == null ? "未配置" :
            type == typeof(CharacterAttackMoveSettings) ? "普通招式" : type.FullName;

        // 捕获资产与属性路径，菜单回调通过 SerializedObject 提交可撤销的独立实例。
        private static void ShowTypes(SerializedProperty property)
        {
            var menu = new GenericMenu();
            var asset = property.serializedObject.targetObject;
            string path = property.propertyPath;
            var current = property.managedReferenceValue?.GetType();
            foreach (var type in Types)
            {
                if (type == current) menu.AddDisabledItem(new GUIContent(TypeName(type)), true);
                else menu.AddItem(new GUIContent(TypeName(type)), false, () => {
                    if (!asset) return;
                    using var serialized = new SerializedObject(asset);
                    var target = serialized.FindProperty(path);
                    if (target == null) return;
                    target.managedReferenceValue = Activator.CreateInstance(type, true);
                    serialized.ApplyModifiedProperties();
                });
            }
            menu.ShowAsContext();
        }

        // 遍历当前托管引用的直属可见字段，嵌套结构由 Unity 自身绘制。
        private static void EachChild(SerializedProperty property, Action<SerializedProperty> visit)
        {
            var child = property.Copy();
            var end = child.GetEndProperty();
            bool enter = true;
            while (child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end))
            {
                enter = false;
                visit(child);
            }
        }
    }
}
