using System;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 独立模块允许编辑全部权重，角色配置按其播放库显示实际评分路径。
    [CustomEditor(typeof(MotionMatchModule))]
    internal sealed class GroundMatchModuleInspector : Editor
    {
        // 编辑共享模块的完整参数，具体角色的布局由角色配置决定。
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("此模块可供多个角色使用。请在角色的动画表现配置中查看播放库、有效评分参数及回退原因。", MessageType.Info);
        }

        // 在所属角色上下文中绘制共享模块的评分字段，并保留兼容参数的已保存值。
        internal static void DrawForCharacter(MotionMatchModule match, CharacterMotionDatabase library)
        {
            EditorGUILayout.LabelField("地面匹配", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("共享匹配模块", match, typeof(MotionMatchModule), false);
            if (!library) return;
            using var module = new SerializedObject(match);
            var issue = library.ValidatePlayback();
            bool configured = module.FindProperty("motionMatching").intValue == (int)CharacterMatchMode.Apply;
            bool usable = issue == null && HasSearchTrack(library);
            if (!configured)
                EditorGUILayout.HelpBox("当前使用常规步态规则，匹配评分未启用。", MessageType.Info);
            if (!usable)
                EditorGUILayout.HelpBox(issue ?? "当前播放库没有与可播放状态和素材对应的候选轨道，匹配回退到常规步态。", MessageType.Warning);
            var layout = MotionMatchModule.LayoutFor(library);
            var schema = library.Groups ? library.Groups.Schema : null;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField("动作库特征布局", schema, typeof(GroundFeatureSchema), false);
                if (schema && GUILayout.Button("定位", GUILayout.ExpandWidth(false))) EditorGUIUtility.PingObject(schema);
            }
            if (usable) EditorGUILayout.HelpBox(layout.Status + (layout.Active
                ? "。通道使用布局的权重和尺度；接触与步频沿用模块中的用途权重和尺度。"
                : "。使用模块中的兼容脚姿、轨迹和用途权重。"), schema && !layout.Active ? MessageType.Warning : MessageType.Info);
            if (layout.Active) DrawChannels(layout, configured && usable);
            var policy = new GroundCandidatePolicy(match.candidates, library);
            if (policy.Issue != null) EditorGUILayout.HelpBox(policy.Issue + "；按条件表的无匹配设置处理。", MessageType.Warning);
            var costs = module.FindProperty("costs");
            costs.isExpanded = EditorGUILayout.Foldout(costs.isExpanded, "编辑共享模块的评分参数", true);
            if (!costs.isExpanded) return;
            using (new EditorGUI.IndentLevelScope())
            using (new EditorGUI.DisabledScope(!configured || !usable))
            {
                var property = costs.Copy();
                var end = costs.GetEndProperty();
                for (bool enter = true; property.NextVisible(enter) && !SerializedProperty.EqualContents(property, end); enter = false)
                {
                    if (property.hasVisibleChildren) DrawUse(property, layout.Active);
                    else
                    {
                        bool disabled = layout.Active && property.name != "ContactScale" && property.name != "PaceScale" ||
                            property.name == "PelvisWeight" && !layout.LegacyPelvis;
                        using (new EditorGUI.DisabledScope(disabled)) EditorGUILayout.PropertyField(property);
                    }
                }
            }
            module.ApplyModifiedProperties();
        }

        // 展示烘焙库共用的布局维度以及运行时评分直接读取的通道权重和尺度。
        private static void DrawChannels(GroundFeatureLayout layout, bool scoringEnabled)
        {
            EditorGUILayout.LabelField("布局评分通道", EditorStyles.boldLabel);
            for (int i = 0; i < layout.Channels.Length; i++)
            {
                var channel = layout.Channels[i];
                bool available = (layout.Available & (1u << i)) != 0;
                string status = !available ? "库未覆盖" : channel.Weight <= 0f ? "权重为零" :
                    scoringEnabled ? "可参与评分" : "匹配未启用";
                string name = channel.Path ? channel.Kind.ToString() : $"{channel.Bone} {channel.Kind}";
                string label = $"{i + 1}. {name} ({channel.Time:G4} s)";
                EditorGUILayout.LabelField(label, $"Weight {channel.Weight:G6}    Scale {channel.Scale:G6}    {status}");
            }
        }

        // 与搜索入口核对主状态及素材配方，排除仅凭布局有效却没有可播放轨道的情况。
        private static bool HasSearchTrack(CharacterMotionDatabase library)
        {
            foreach (var track in library.BakedTracks)
            {
                if (track == null || !track.ReadyForSearch || track.States == null || track.States.Length == 0 ||
                    string.IsNullOrWhiteSpace(track.States[0])) continue;
                if (Array.Exists(library.Playback.States, s => s != null && s.Path == track.States[0] && s.MatchesTrack(track, s.Clip)))
                    return true;
            }
            return false;
        }

        // 布局评分只从每种用途读取接触与步频，其余权重在使用兼容脚姿时生效。
        private static void DrawUse(SerializedProperty use, bool layoutActive)
        {
            use.isExpanded = EditorGUILayout.Foldout(use.isExpanded, use.displayName, true);
            if (!use.isExpanded) return;
            using (new EditorGUI.IndentLevelScope())
            {
                var weight = use.Copy();
                var end = use.GetEndProperty();
                for (bool enter = true; weight.NextVisible(enter) && !SerializedProperty.EqualContents(weight, end); enter = false)
                    using (new EditorGUI.DisabledScope(layoutActive && weight.name != "Contact" && weight.name != "Pace"))
                        EditorGUILayout.PropertyField(weight);
            }
        }
    }
}
