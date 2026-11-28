using System;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 检查动画模块组合的引用和所有权。
    [CustomEditor(typeof(AnimationModuleGroup))]
    internal sealed class AnimationModuleGroupEditor : Editor
    {
        // 展示当前组合的装配错误。
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            try { AnimationModuleAssembly.Resolve(new[] { (AnimationModuleGroup)target }, null); }
            catch (ArgumentException error) { EditorGUILayout.HelpBox(error.Message, MessageType.Error); }
        }
    }
}
