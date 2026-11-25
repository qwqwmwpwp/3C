using UnityEditor;

namespace ThreeC.CharacterBehavior
{
    // 编辑动画表现的共享字段并报告模块装配错误。
    [CustomEditor(typeof(CharacterAnimationConfig))]
    internal sealed class CharacterAnimationConfigEditor : Editor
    {
        // 绘制当前配置，并验证模块组合是否可展开。
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var config = (CharacterAnimationConfig)target;
            if (!config.PlaybackLibrary) EditorGUILayout.HelpBox("请选择已烘焙的双层动画播放库。", MessageType.Error);
            if (!config.Modules) return;
            try { config.ResolveModules(); }
            catch (System.ArgumentException error) { EditorGUILayout.HelpBox(error.Message, MessageType.Error); }
        }
    }
}
