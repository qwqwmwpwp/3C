using System;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CustomEditor(typeof(AnimationModuleGroup))]
    internal sealed class AnimationModuleGroupEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            try { AnimationModuleAssembly.Resolve(new[] { (AnimationModuleGroup)target }, null, checkActionReferences: false); }
            catch (ArgumentException error) { EditorGUILayout.HelpBox(error.Message, MessageType.Error); }
        }
    }
}
