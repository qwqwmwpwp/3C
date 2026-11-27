using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// 以只读方式显示字段，并保留字段的自定义名称。
[CustomPropertyDrawer(typeof(ReadOnlyAttribute))]
public class ReadOnlyDrawer : PropertyDrawer
{
    // 绘制禁用编辑的字段。
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        GUI.enabled = false;

        // Inherit [Rename] attribute text if present to prevent drawer conflicts
        object[] renameAttributes = fieldInfo.GetCustomAttributes(typeof(RenameAttribute), true);
        if (renameAttributes.Length > 0)
        {
            label.text = ((RenameAttribute)renameAttributes[0]).NewName;
        }
        else if (property.name == "name")
        {
            label.text = "Source Name";
        }

        EditorGUI.PropertyField(position, property, label, true);
        GUI.enabled = true;
    }

    // 返回只读字段展开后的高度。
    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        return EditorGUI.GetPropertyHeight(property, label, true);
    }
}

// 在检查器中使用指定名称显示字段。
[CustomPropertyDrawer(typeof(RenameAttribute))]
public class RenameDrawer : PropertyDrawer
{
    // 使用自定义标签绘制字段。
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        label.text = (attribute as RenameAttribute).NewName;
        EditorGUI.PropertyField(position, property, label, true);
    }

    // 返回重命名字段展开后的高度。
    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        return EditorGUI.GetPropertyHeight(property, label, true);
    }
}

// 绘制默认挂点事件及其控制器层监听配置。
[CustomPropertyDrawer(typeof(DefaultEventConfig))]
public class DefaultEventConfigDrawer : PropertyDrawer
{
    private bool layerFoldout = false;

    // 显示默认事件设置并同步控制器层列表。
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        SerializedProperty enabledProp = property.FindPropertyRelative("enabled");
        SerializedProperty eventNameProp = property.FindPropertyRelative("eventName");
        SerializedProperty monitoredLayersProp = property.FindPropertyRelative("monitoredLayers");

        // Resolve label
        object[] renameAttrs = fieldInfo.GetCustomAttributes(typeof(RenameAttribute), true);
        if (renameAttrs.Length > 0)
            label.text = ((RenameAttribute)renameAttrs[0]).NewName;
        else
            label.text = "Default Event String";

        float lineHeight = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;

        // Get Animator for layer names
        Animator animator = null;
        var targetObj = property.serializedObject.targetObject as Component;
        if (targetObj != null) animator = targetObj.GetComponent<Animator>();

        int animLayerCount = 0;
        if (animator != null)
        {
            var animSO = new UnityEditor.SerializedObject(animator);
            var ctrlProp = animSO.FindProperty("m_Controller");
            if (ctrlProp != null && ctrlProp.objectReferenceValue is AnimatorController controller)
            {
                animLayerCount = controller.layers.Length;
            }
        }

        // Sync monitoredLayers list size with actual Animator layer count
        if (animLayerCount > 0)
        {
            while (monitoredLayersProp.arraySize < animLayerCount)
            {
                monitoredLayersProp.InsertArrayElementAtIndex(monitoredLayersProp.arraySize);
                monitoredLayersProp.GetArrayElementAtIndex(monitoredLayersProp.arraySize - 1).boolValue = (monitoredLayersProp.arraySize == 1);
            }
            while (monitoredLayersProp.arraySize > animLayerCount)
                monitoredLayersProp.DeleteArrayElementAtIndex(monitoredLayersProp.arraySize - 1);
        }

        // Main Foldout header
        Rect foldoutRect = new Rect(position.x, position.y, position.width, lineHeight);
        property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, label, true);

        if (property.isExpanded)
        {
            EditorGUI.indentLevel++;
            float y = position.y + lineHeight + spacing;

            // 1st: Enable toggle (always interactable)
            Rect enableRect = new Rect(position.x, y, position.width, lineHeight);
            EditorGUI.PropertyField(enableRect, enabledProp, new GUIContent("Enable"));
            y += lineHeight + spacing;

            // Gray out when disabled
            bool prevEnabled = GUI.enabled;
            GUI.enabled = enabledProp.boolValue;

            // 2nd: Event String Name
            Rect eventNameRect = new Rect(position.x, y, position.width, lineHeight);
            EditorGUI.PropertyField(eventNameRect, eventNameProp, new GUIContent("Event String Name"));
            y += lineHeight + spacing;

            // 3rd: Target Layer (sub-foldout)
            Rect layerFoldRect = new Rect(position.x, y, position.width, lineHeight);
            layerFoldout = EditorGUI.Foldout(layerFoldRect, layerFoldout, "Target Layer", true);
            y += lineHeight + spacing;

            if (layerFoldout && animLayerCount > 0)
            {
                EditorGUI.indentLevel++;
                for (int i = 0; i < animLayerCount; i++)
                {
                    string layerName = "Layer " + i;
                    var animSO = new UnityEditor.SerializedObject(animator);
                    var ctrlProp = animSO.FindProperty("m_Controller");
                    if (ctrlProp != null && ctrlProp.objectReferenceValue is AnimatorController controller)
                    {
                        if (i < controller.layers.Length) layerName = controller.layers[i].name;
                    }

                    Rect toggleRect = new Rect(position.x, y, position.width, lineHeight);
                    SerializedProperty elem = monitoredLayersProp.GetArrayElementAtIndex(i);
                    elem.boolValue = EditorGUI.Toggle(toggleRect, layerName, elem.boolValue);
                    y += lineHeight + spacing;
                }
                EditorGUI.indentLevel--;
            }
            else if (layerFoldout && animLayerCount == 0)
            {
                Rect warnRect = new Rect(position.x, y, position.width, lineHeight);
                EditorGUI.LabelField(warnRect, "  No Animator found on this GameObject.");
                y += lineHeight + spacing;
            }

            GUI.enabled = prevEnabled;
            EditorGUI.indentLevel--;
        }
    }

    // 根据展开状态和层数计算设置所需的高度。
    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float lineHeight = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;

        if (!property.isExpanded)
            return lineHeight;

        // Header + Enable + EventName + TargetLayer foldout
        float height = lineHeight + (lineHeight + spacing) * 3;

        if (layerFoldout)
        {
            Animator animator = null;
            var targetObj = property.serializedObject.targetObject as Component;
            if (targetObj != null) animator = targetObj.GetComponent<Animator>();
            int count = 1;
            if (animator != null)
            {
                var animSO = new UnityEditor.SerializedObject(animator);
                var ctrlProp = animSO.FindProperty("m_Controller");
                if (ctrlProp != null && ctrlProp.objectReferenceValue is AnimatorController controller)
                {
                    count = controller.layers.Length;
                }
            }
            height += (lineHeight + spacing) * count;
        }

        return height;
    }
}

// 使用武器名称显示可展开的挂点配置。
[CustomPropertyDrawer(typeof(WeaponSlot))]
public class WeaponSlotDrawer : PropertyDrawer
{
    // 用武器名称绘制挂点配置。
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        SerializedProperty nameProp = property.FindPropertyRelative("name");

        // Use the name field value as the foldout header
        string displayName = (!string.IsNullOrEmpty(nameProp.stringValue)) ? nameProp.stringValue : label.text;

        EditorGUI.PropertyField(position, property, new GUIContent(displayName), true);
    }

    // 返回武器挂点配置展开后的高度。
    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        return EditorGUI.GetPropertyHeight(property, label, true);
    }
}
