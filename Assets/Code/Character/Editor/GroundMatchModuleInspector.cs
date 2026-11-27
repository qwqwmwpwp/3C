using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 独立模块编辑库声明和权重，角色配置显示各搜索库的评分路径。
    [CustomEditor(typeof(MotionMatchModule))]
    internal sealed class GroundMatchModuleInspector : Editor
    {
        // 编辑共享模块并提示启用匹配时的零库回退。
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var match = (MotionMatchModule)target;
            if (match.MatchingEnabled && (match.searchLibraries?.Length ?? 0) == 0)
                EditorGUILayout.HelpBox(GroundSearchLibraries.NoDeclaredLibraries, MessageType.Warning);
            EditorGUILayout.HelpBox("在角色的动画表现配置中查看各搜索库的有效评分参数、联合评分限制和回退原因。", MessageType.Info);
        }

        // 在角色上下文中核对显式搜索库和回退原因，并绘制相应评分字段。
        internal static void DrawForCharacter(MotionMatchModule match, CharacterMotionDatabase library)
        {
            EditorGUILayout.LabelField("地面匹配", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("共享匹配模块", match, typeof(MotionMatchModule), false);
            if (!library) return;
            using var module = new SerializedObject(match);
            string playbackIssue = library.ValidatePlayback();
            bool configured = match.MatchingEnabled;
            var libraries = new GroundSearchLibraries(library, match.searchLibraries, snapshotSamples: false);
            bool usable = false, compatibilityWeights = false, legacyPelvis = false;
            if (!configured)
                EditorGUILayout.HelpBox("当前使用常规步态规则，匹配评分未启用。", MessageType.Info);
            if (playbackIssue != null)
                EditorGUILayout.HelpBox(playbackIssue, MessageType.Warning);
            for (int i = 0; i < libraries.Databases.Length; i++)
            {
                var layout = libraries.Layouts[i];
                bool valid = libraries.TrackCounts[i] > 0;
                usable |= valid;
                compatibilityWeights |= valid && !layout.Active;
                legacyPelvis |= valid && !layout.Active && layout.LegacyPelvis;
                DrawLibrary(libraries.Databases[i], libraries.Names[i], libraries.Issues[i], layout, libraries.TrackCounts[i],
                    configured && playbackIssue == null);
            }
            if (configured && !usable)
                EditorGUILayout.HelpBox(libraries.Databases.Length == 0 ? GroundSearchLibraries.NoDeclaredLibraries :
                    "没有可播放的候选轨道，地面匹配将回退到常规步态。", MessageType.Warning);
            foreach (string issue in CharacterSearchLibraryAudit.IncompatiblePairs(libraries))
                EditorGUILayout.HelpBox(issue, MessageType.Info);
            foreach (string issue in CharacterSearchLibraryAudit.RuleIssues(match.candidates, libraries))
                EditorGUILayout.HelpBox(issue, MessageType.Warning);
            var costs = module.FindProperty("costs");
            costs.isExpanded = EditorGUILayout.Foldout(costs.isExpanded, "编辑共享模块的评分参数", true);
            if (!costs.isExpanded) return;
            using (new EditorGUI.IndentLevelScope())
            using (new EditorGUI.DisabledScope(!configured || !usable || playbackIssue != null))
            {
                var property = costs.Copy();
                var end = costs.GetEndProperty();
                for (bool enter = true; property.NextVisible(enter) && !SerializedProperty.EqualContents(property, end); enter = false)
                {
                    if (property.hasVisibleChildren) DrawUse(property, compatibilityWeights);
                    else
                    {
                        bool disabled = !compatibilityWeights && property.name != "ContactScale" && property.name != "PaceScale" ||
                            property.name == "PelvisWeight" && !legacyPelvis;
                        using (new EditorGUI.DisabledScope(disabled)) EditorGUILayout.PropertyField(property);
                    }
                }
            }
            module.ApplyModifiedProperties();
        }

        // 展示指定来源库的可用轨道数、评分配置和退化原因。
        private static void DrawLibrary(CharacterMotionDatabase source, string name, string issue, GroundFeatureLayout layout,
            int trackCount, bool scoringEnabled)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField(name, source, typeof(CharacterMotionDatabase), false);
                if (source && GUILayout.Button("定位", GUILayout.ExpandWidth(false))) EditorGUIUtility.PingObject(source);
            }
            if (issue != null) EditorGUILayout.HelpBox(issue, MessageType.Warning);
            if (trackCount == 0) return;
            var schema = source.Groups ? source.Groups.Schema : null;
            if (schema)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField("动作库特征布局", schema, typeof(GroundFeatureSchema), false);
                    if (GUILayout.Button("定位", GUILayout.ExpandWidth(false))) EditorGUIUtility.PingObject(schema);
                }
            }
            EditorGUILayout.HelpBox($"{trackCount} 条可播放轨道 · {layout.Status}" + (layout.Active
                ? "。通道使用布局的权重和尺度，接触与步频沿用模块中的用途权重和尺度。"
                : "。使用模块中的兼容脚姿、轨迹和用途权重。"), schema && !layout.Active ? MessageType.Warning : MessageType.Info);
            if (layout.Active) DrawChannels(layout, scoringEnabled);
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

        // 布局评分只从每种用途读取接触与步频，其余权重在使用兼容脚姿时生效。
        private static void DrawUse(SerializedProperty use, bool compatibilityWeights)
        {
            use.isExpanded = EditorGUILayout.Foldout(use.isExpanded, use.displayName, true);
            if (!use.isExpanded) return;
            using (new EditorGUI.IndentLevelScope())
            {
                var weight = use.Copy();
                var end = use.GetEndProperty();
                for (bool enter = true; weight.NextVisible(enter) && !SerializedProperty.EqualContents(weight, end); enter = false)
                    using (new EditorGUI.DisabledScope(!compatibilityWeights && weight.name != "Contact" && weight.name != "Pace"))
                        EditorGUILayout.PropertyField(weight);
            }
        }
    }
}
