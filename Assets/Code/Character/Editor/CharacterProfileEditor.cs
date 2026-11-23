using System;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CustomEditor(typeof(CharacterProfile)), CanEditMultipleObjects]
    internal sealed class CharacterProfileEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            if (!GUILayout.Button("检查连招配置")) return;
            foreach (CharacterProfile profile in targets) Check(profile);
        }

        private static void Check(CharacterProfile profile)
        {
            bool found = false;
            void Report(string problem)
            {
                found = true;
                Debug.LogWarning($"{profile.name} / {problem}", profile);
            }
            CharacterAnimationConfiguration animation = null;
            try { animation = profile.BindAnimation(new CharacterModelBindings(), new CharacterProfileOverrides()); }
            catch (ArgumentException error) { Report(error.Message); }
            CharacterComboValidation.Check(profile.LightAttack, profile.ComboRoutes, animation, Report);
            if (!found) Debug.Log($"{profile.name}：连招路线和攻击映射检查通过。", profile);
        }
    }
}
