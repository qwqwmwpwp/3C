using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal static class CharacterAnimationOverrideAudit
    {
        private const string Menu = "Assets/3C/检查动作及替换片段";

        [MenuItem(Menu, true)]
        private static bool CanCheck() => Selection.activeObject is CharacterMotionDatabase ||
            Selection.activeObject is CharacterProfile || Selection.activeObject is AnimatorOverrideController;

        [MenuItem(Menu)]
        private static void CheckSelected()
        {
            var selected = Selection.activeObject;
            var profile = selected as CharacterProfile;
            var single = selected as AnimatorOverrideController;
            bool found = false;
            foreach (var db in Assets<CharacterMotionDatabase>())
            {
                if (single ? db.BakedController != single.runtimeAnimatorController :
                    db != (profile ? profile.EffectivePlayback : selected)) continue;
                found = true;
                if (db.Playback?.Ready != true)
                { Debug.LogWarning($"{db.name}：缺少已烘焙播放库。", db); continue; }
                Inspect(db, db.Playback, db.BakedTracks, null, single);
            }
            if (!found) Debug.LogWarning("未找到此资源对应的动作播放库。", selected);
        }

        private static IEnumerable<T> Assets<T>() where T : UnityEngine.Object
        {
            foreach (var id in AssetDatabase.FindAssets("t:" + typeof(T).Name, new[] { "Assets" }))
            {
                var asset = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(id));
                if (asset) yield return asset;
            }
        }

        private readonly struct Need
        {
            public readonly string Action, State, Curve;
            public Need(string action, string state, string curve) { Action = action; State = state; Curve = curve; }
        }

        // 烘焙时补入替换素材自己的曲线。姿势、轨迹只承认同路径、同片段的有效采样。
        internal static void Inspect(CharacterMotionDatabase db, CharacterClipLibrary library, CharacterMotionTrack[] tracks,
            Dictionary<AnimationClip, CharacterClipCurves> bakeCurves, AnimatorOverrideController single = null)
        {
            var controllerRoot = bakeCurves != null ? db.Controller : db.BakedController;
            var states = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
            foreach (var state in library.States)
                if (state != null && !string.IsNullOrEmpty(state.Path)) states[state.Path] = state.Clip;
            var curves = bakeCurves ?? new Dictionary<AnimationClip, CharacterClipCurves>();
            foreach (var entry in library.Clips)
                if (entry != null && entry.Clip) curves[entry.Clip] = entry;
            var needs = new List<Need>();
            var ground = new List<CharacterMotionTrack>(tracks ?? Array.Empty<CharacterMotionTrack>());
            foreach (var profile in Assets<CharacterProfile>())
            {
                if (profile.EffectivePlayback != db || profile.Presentation == null) continue;
                var motion = profile.MotionDatabase;
                if (motion && motion != db && motion.BakedController == controllerRoot && motion.BakedTracks != null)
                    foreach (var track in motion.BakedTracks) if (!ground.Contains(track)) ground.Add(track);
                var presentation = profile.Presentation;
                foreach (var channel in presentation.IkChannels ?? Array.Empty<CharacterIkTuning>())
                {
                    if (channel == null || string.IsNullOrWhiteSpace(channel.GripActionKey)) continue;
                    var action = Array.Find(presentation.actions ?? Array.Empty<CharacterAnimationActionSettings>(),
                        row => row != null && row.Key == channel.GripActionKey);
                    string key = profile.name + "/" + channel.GripActionKey;
                    needs.Add(new Need(key, action?.StateName, channel.WeightCurve));
                    if (!string.IsNullOrWhiteSpace(action?.MovingStateName)) needs.Add(new Need(key, action.MovingStateName, channel.WeightCurve));
                }
            }
            foreach (var track in ground)
                if (track?.States != null)
                    foreach (string path in track.States)
                    {
                        needs.Add(new Need("地面动作", path, CharacterFootSampler.LeftCurve));
                        needs.Add(new Need("地面动作", path, CharacterFootSampler.RightCurve));
                    }

            var imported = new Dictionary<AnimationClip, CharacterClipCurves>();
            void Check(AnimatorOverrideController controller)
            {
                var warnings = new StringBuilder();
                var replacements = new Dictionary<AnimationClip, AnimationClip>();
                if (controller)
                {
                    var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(controller.overridesCount);
                    controller.GetOverrides(pairs);
                    foreach (var pair in pairs)
                        if (pair.Key && pair.Value && pair.Key != pair.Value) replacements[pair.Key] = pair.Value;
                }
                AnimationClip Actual(string path)
                {
                    if (string.IsNullOrEmpty(path) || !states.TryGetValue(path, out var original) || !original) return null;
                    return replacements.TryGetValue(original, out var replacement) ? replacement : original;
                }
                CharacterClipCurves ReadImported(AnimationClip clip)
                {
                    if (!imported.TryGetValue(clip, out var entry)) imported.Add(clip, entry = CharacterClipLibraryBaker.ReadCurves(clip));
                    return entry;
                }
                if (bakeCurves != null)
                    foreach (var clip in replacements.Values)
                        if (!curves.ContainsKey(clip)) curves.Add(clip, ReadImported(clip));
                foreach (var need in needs)
                {
                    var clip = Actual(need.State);
                    if (!clip) { warnings.AppendLine($"{need.Action} · {need.State ?? "未映射"}：缺少状态或素材"); continue; }
                    if (curves.TryGetValue(clip, out var entry) && entry.Find(need.Curve)?.Curve.length > 0) continue;
                    string why = ReadImported(clip).Find(need.Curve)?.Curve.length > 0 ? "曲线存在，需重新烘焙到播放库" : "源素材缺少曲线";
                    warnings.AppendLine($"{need.Action} · {need.State} → {clip.name}：{why} {need.Curve}");
                }
                int valid = 0, total = 0;
                foreach (var track in ground)
                {
                    if (track?.States == null) continue;
                    foreach (var path in track.States)
                    {
                        total++;
                        var clip = Actual(path);
                        if (clip && clip == track.Clip && track.ReadyForSearch) { valid++; continue; }
                        warnings.AppendLine($"地面动作 · {path} → {(clip ? clip.name : "无素材")}：此状态缺少匹配当前片段的姿势或轨迹采样");
                    }
                }
                string owner = controller ? controller.name : "原控制器";
                string header = $"{db.name} / {owner}：地面轨道状态可用 {valid}/{total}";
                if (warnings.Length > 0) Debug.LogWarning(header + "\n" + warnings, controller ? (UnityEngine.Object)controller : db);
                else Debug.Log(header, db);
            }
            if (single) Check(single);
            else
            {
                Check(null);
                foreach (var controller in Assets<AnimatorOverrideController>())
                    if (controller.runtimeAnimatorController == controllerRoot) Check(controller);
            }
            if (bakeCurves != null) library.Clips = new List<CharacterClipCurves>(curves.Values).ToArray();
        }
    }
}
