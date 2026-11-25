using UnityEditor;

namespace ThreeC.CharacterBehavior
{
    // 编辑动画表现的共享字段并报告模块装配错误。
    [CustomEditor(typeof(CharacterAnimationConfig))]
    internal sealed class CharacterAnimationConfigEditor : Editor
    {
        // 验证模块组合，并按此角色的播放库显示地面匹配的生效参数。
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var config = (CharacterAnimationConfig)target;
            if (!config.PlaybackLibrary) EditorGUILayout.HelpBox("请选择已烘焙的双层动画播放库。", MessageType.Error);
            if (!config.Modules) return;
            try
            {
                var modules = config.ResolveModules();
                if (modules.Match) GroundMatchModuleInspector.DrawForCharacter(modules.Match, config.PlaybackLibrary);
            }
            catch (System.ArgumentException error) { EditorGUILayout.HelpBox(error.Message, MessageType.Error); }
        }
    }
}
