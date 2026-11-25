using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 用现有数据组作为模板，保存换用新素材的组和对应的播放库。
    internal sealed class CharacterMotionGroupCreationWindow : EditorWindow
    {
        private const string AssetMenu = "Assets/3C/用基础片段创建地面动作库";
        private CharacterMotionGroups template;
        private Avatar avatar;
        private string error;
        private Vector2 scroll;
        private readonly List<ClipChoice> choices = new List<ClipChoice>();

        private sealed class ClipChoice
        {
            public readonly AnimationClip Original;
            public AnimationClip Replacement;

            public ClipChoice(AnimationClip original)
            {
                Original = original;
                Replacement = original;
            }
        }

        [MenuItem(AssetMenu, true)]
        private static bool HasTemplateSelection() => Selection.activeObject is CharacterMotionGroups ||
            Selection.activeObject is CharacterMotionDatabase db && db.Groups;

        [MenuItem(AssetMenu)]
        [MenuItem("Tools/3C/用基础片段创建地面动作库")]
        private static void Open()
        {
            var window = GetWindow<CharacterMotionGroupCreationWindow>("创建地面动作库");
            window.minSize = new Vector2(430f, 270f);
            if (Selection.activeObject is CharacterMotionDatabase db && db.Groups)
            {
                window.SetTemplate(db.Groups);
                window.avatar = db.Avatar;
            }
            else if (Selection.activeObject is CharacterMotionGroups groups)
            {
                window.SetTemplate(groups);
            }
            window.Show();
        }

        private void OnGUI()
        {
            var selected = (CharacterMotionGroups)EditorGUILayout.ObjectField("数据组模板", template, typeof(CharacterMotionGroups), false);
            if (selected != template) SetTemplate(selected);
            avatar = (Avatar)EditorGUILayout.ObjectField("模型 Avatar", avatar, typeof(Avatar), false);
            if (!template)
            {
                EditorGUILayout.HelpBox("请选择已配置的基础动画数据组。", MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox("为每个基础片段选择对应素材，保留模板的分组、变体参数、遮罩和状态路径。", MessageType.Info);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var choice in choices)
            {
                if (!choice.Original) continue;
                var label = new GUIContent(choice.Original.name, AssetDatabase.GetAssetPath(choice.Original));
                choice.Replacement = (AnimationClip)EditorGUILayout.ObjectField(label, choice.Replacement, typeof(AnimationClip), false);
            }
            EditorGUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            using (new EditorGUI.DisabledScope(!avatar || EditorApplication.isPlayingOrWillChangePlaymode ||
                choices.Count == 0 || choices.Exists(choice => !choice.Replacement)))
            {
                if (GUILayout.Button("创建并烘焙")) CreateAssets();
            }
        }

        private void SetTemplate(CharacterMotionGroups value)
        {
            template = value;
            choices.Clear();
            error = null;
            if (!value) return;
            var seen = new HashSet<AnimationClip>();
            void Add(AnimationClip clip)
            {
                if (clip && seen.Add(clip)) choices.Add(new ClipChoice(clip));
            }
            foreach (var binding in value.Bindings ?? Array.Empty<CharacterClipState>()) Add(binding?.Clip);
            foreach (var group in value.Groups ?? Array.Empty<GroundAnimationGroup>())
            {
                Add(group?.First);
                Add(group?.Second);
            }
        }

        // 替换模板片段并校验采样，保存数据组和对应动作库。
        private void CreateAssets()
        {
            string requestedPath = EditorUtility.SaveFilePanelInProject(
                "创建地面动作库", "地面动作库", "asset", "同一目录将保存动作库和可继续编辑的数据组。");
            if (string.IsNullOrEmpty(requestedPath)) return;

            string dbPath = AssetDatabase.GenerateUniqueAssetPath(requestedPath);
            string groupPath = AssetDatabase.GenerateUniqueAssetPath(Path.ChangeExtension(dbPath, null) + " 数据组.asset");
            var groups = Instantiate(template);
            var db = CreateInstance<CharacterMotionDatabase>();
            try
            {
                ApplyChoices(groups);
                groups.name = Path.GetFileNameWithoutExtension(groupPath);
                db.name = Path.GetFileNameWithoutExtension(dbPath);
                db.Avatar = avatar;
                db.Groups = groups;
                var result = CharacterMotionGroupBaker.Compile(db);
                result.Apply(db);

                AssetDatabase.CreateAsset(groups, groupPath);
                if (!AssetDatabase.Contains(groups)) throw new InvalidOperationException("无法保存基础动画数据组。");
                AssetDatabase.CreateAsset(db, dbPath);
                if (!AssetDatabase.Contains(db)) throw new InvalidOperationException("无法保存地面动作库。");
                AssetDatabase.SaveAssetIfDirty(groups);
                AssetDatabase.SaveAssetIfDirty(db);
                Selection.activeObject = db;
                EditorGUIUtility.PingObject(db);
                Debug.Log($"{db.name}：已创建数据组并烘焙 {result.Tracks.Length} 条轨道。", db);
                result.ReportQuality(db);
                Close();
            }
            catch (Exception failure)
            {
                error = failure.Message;
                DiscardDraft(db, dbPath);
                DiscardDraft(groups, groupPath);
            }
        }

        private void ApplyChoices(CharacterMotionGroups groups)
        {
            var replacements = new Dictionary<AnimationClip, AnimationClip>();
            foreach (var choice in choices)
                if (choice.Original) replacements.Add(choice.Original, choice.Replacement);
            AnimationClip Resolve(AnimationClip clip) => clip && replacements.TryGetValue(clip, out var chosen) ? chosen : clip;
            foreach (var binding in groups.Bindings ?? Array.Empty<CharacterClipState>())
                if (binding != null) binding.Clip = Resolve(binding.Clip);
            foreach (var group in groups.Groups ?? Array.Empty<GroundAnimationGroup>())
            {
                if (group == null) continue;
                group.First = Resolve(group.First);
                group.Second = Resolve(group.Second);
            }
        }

        private static void DiscardDraft(ScriptableObject asset, string path)
        {
            if (!asset) return;
            if (AssetDatabase.GetAssetPath(asset) == path) AssetDatabase.DeleteAsset(path);
            else DestroyImmediate(asset);
        }
    }
}
