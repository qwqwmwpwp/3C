using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CustomEditor(typeof(CharacterAnimationConfig))]
    internal sealed class CharacterAnimationConfigEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var modules = serializedObject.FindProperty("Modules");
            EditorGUILayout.PropertyField(modules);
            if (!modules.objectReferenceValue)
            {
                DrawPropertiesExcluding(serializedObject, "m_Script", "Modules");
            }
            else
            {
                try { AnimationModuleAssembly.Resolve((AnimationModule)modules.objectReferenceValue); }
                catch (System.ArgumentException error) { EditorGUILayout.HelpBox(error.Message, MessageType.Error); }
                var presentation = serializedObject.FindProperty("Presentation");
                EditorGUILayout.PropertyField(serializedObject.FindProperty("PlaybackLibrary"));
                // 老资产允许播放库留空并沿用地面库。
                if (!serializedObject.FindProperty("PlaybackLibrary").objectReferenceValue)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("MotionDatabase"), new GUIContent("备用播放库"));
                EditorGUILayout.PropertyField(presentation.FindPropertyRelative("idleStateName"), new GUIContent("无步态模块时的待机状态"));
                EditorGUILayout.PropertyField(presentation.FindPropertyRelative("ikTeleportDistance"));
                EditorGUILayout.PropertyField(presentation.FindPropertyRelative("ReuseActionMixers"));
                EditorGUILayout.PropertyField(presentation.FindPropertyRelative("layerIndex"));
                EditorGUILayout.PropertyField(presentation.FindPropertyRelative("lowerBodyLayerIndex"));
                EditorGUILayout.PropertyField(presentation.FindPropertyRelative("overlayLayerIndex"));
                EditorGUILayout.PropertyField(presentation.FindPropertyRelative("upperBodyLayerIndex"));
            }
            serializedObject.ApplyModifiedProperties();
        }
    }
}
