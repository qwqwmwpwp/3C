using System;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 显示配置的生效时机并提供连招配置检查。
    [CustomEditor(typeof(CharacterProfile)), CanEditMultipleObjects]
    internal sealed class CharacterProfileEditor : Editor
    {
        // 在资产引用旁标明运行中修改的读取边界，提供手动校验入口。
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("移动速度、转向和加减速由下一次对应的行为或物理计算读取。\n" +
                "单招与短突进参数随请求获准锁定，已接受的接续保留原配置。\n" +
                "初始体力在初始化或显式重置时读取。行为集合与打断关系在角色初始化时固定，动画模块资源在动画装配时绑定。",
                MessageType.Info);
            if (!GUILayout.Button("检查连招配置")) return;
            foreach (CharacterProfile profile in targets) Check(profile);
        }

        // 核对总表、可执行定义与角色动画映射。
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
            CharacterComboValidation.Check(profile.AttackTable, animation, Report);
            if (!found) Debug.Log($"{profile.name}：连招路线和攻击映射检查通过。", profile);
        }
    }
}
