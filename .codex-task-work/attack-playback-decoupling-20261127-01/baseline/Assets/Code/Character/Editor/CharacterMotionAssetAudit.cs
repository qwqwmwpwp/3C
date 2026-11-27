using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 检查数据组发布的状态、轨道样本以及角色使用的许可与接触曲线。
    internal static class CharacterMotionAssetAudit
    {
        private const string Menu = "Assets/3C/检查地面动作库";

        // 判断当前是否选择了动作库或引用动作库的角色配置。
        [MenuItem(Menu, true)]
        private static bool CanCheck() => Selection.activeObject is CharacterMotionDatabase ||
            Selection.activeObject is CharacterProfile;

        // 按当前选择检查已发布的动作库。
        [MenuItem(Menu)]
        private static void CheckSelected()
        {
            var selected = Selection.activeObject;
            var profile = selected as CharacterProfile;
            var db = profile ? profile.EffectivePlayback : selected as CharacterMotionDatabase;
            if (!db)
            {
                Debug.LogWarning("角色配置缺少动画播放库。", selected);
                return;
            }

            Inspect(db, db.Playback, db.BakedTracks);
        }

        // 读取使用该动作库的角色配置，以定位配置引用的动作和 IK 曲线。
        private static IEnumerable<CharacterProfile> Profiles(CharacterMotionDatabase db)
        {
            foreach (var id in AssetDatabase.FindAssets("t:CharacterProfile", new[] { "Assets" }))
            {
                var profile = AssetDatabase.LoadAssetAtPath<CharacterProfile>(AssetDatabase.GUIDToAssetPath(id));
                if (profile && profile.EffectivePlayback == db) yield return profile;
            }
        }

        // 保存检查某个状态时需要的曲线及其用途。
        private readonly struct Need
        {
            public readonly string Action, State, Curve;

            // 记录曲线所属的动作和状态。
            public Need(string action, string state, string curve)
            {
                Action = action;
                State = state;
                Curve = curve;
            }
        }

        // 汇总使用当前播放库的角色动作和持握许可曲线。
        private static void AddGripNeeds(CharacterMotionDatabase db, List<Need> needs)
        {
            foreach (var profile in Profiles(db))
            {
                CharacterAnimationConfiguration config;
                try { config = profile.BindAnimation(new CharacterModelBindings(), new CharacterProfileOverrides()); }
                catch (ArgumentException error) { Debug.LogWarning(error.Message, profile); continue; }

                foreach (var bound in config.IkChannels ?? Array.Empty<CharacterBoundIkChannel>())
                {
                    var channel = bound.Tuning;
                    if (channel == null || string.IsNullOrWhiteSpace(channel.GripActionKey)) continue;
                    var action = Array.Find(config.Actions ?? Array.Empty<CharacterAnimationActionSettings>(),
                        row => row != null && row.Key == channel.GripActionKey);
                    string key = profile.name + "/" + channel.GripActionKey;
                    needs.Add(new Need(key, action?.StateName, channel.WeightCurve));
                    if (!string.IsNullOrWhiteSpace(action?.MovingStateName))
                        needs.Add(new Need(key, action.MovingStateName, channel.WeightCurve));
                }
            }
        }

        // 比对播放状态与轨道样本，报告需要补充曲线或重新烘焙的资源。
        internal static void Inspect(CharacterMotionDatabase db, CharacterClipLibrary library, CharacterMotionTrack[] tracks)
        {
            var errors = new List<string>();
            var states = new Dictionary<string, CharacterClipState>(StringComparer.Ordinal);
            foreach (var state in library?.States ?? Array.Empty<CharacterClipState>())
            {
                if (state == null || string.IsNullOrWhiteSpace(state.Path) || !state.Clip)
                    errors.Add("发布库存在空状态、空状态路径或缺失片段。");
                else if (states.ContainsKey(state.Path)) errors.Add($"发布状态 {state.Path} 重复。");
                else states.Add(state.Path, state);
            }

            var curves = new Dictionary<AnimationClip, CharacterClipCurves>();
            foreach (var entry in library?.Clips ?? Array.Empty<CharacterClipCurves>())
                if (entry != null && entry.Clip) curves[entry.Clip] = entry;

            var needs = new List<Need>();
            AddGripNeeds(db, needs);
            var warnings = new StringBuilder();
            string error = db.ValidatePlayback();
            if (error != null) errors.Add(error);
            string trackSummary = CheckTracks(db, tracks, states, curves, errors, warnings);

            var imported = new Dictionary<AnimationClip, CharacterClipCurves>();
            string contactSummary = CheckFootContacts(db.Groups, curves, imported, warnings);
            foreach (var need in needs)
            {
                if (string.IsNullOrEmpty(need.State) || !states.TryGetValue(need.State, out var state) || !state.Clip)
                {
                    warnings.AppendLine($"{need.Action} · {need.State ?? "未映射"}：缺少状态或素材");
                    continue;
                }

                var first = state.Clip;
                var second = state.Blend.Second;
                // 判断该动作的源片段是否已发布所需许可曲线。
                bool HasCurve(AnimationClip clip) => clip && curves.TryGetValue(clip, out var entry) && entry.Find(need.Curve) != null;
                if (HasCurve(first) || HasCurve(second)) continue;
                bool importedCurve = ReadCurve(first, need.Curve, imported, true) != null ||
                    ReadCurve(second, need.Curve, imported, true) != null;
                string why = importedCurve ? "曲线存在，需重新烘焙到播放库" : "源素材缺少曲线";
                string inputs = second ? first.name + " + " + second.name : first.name;
                warnings.AppendLine($"{need.Action} · {need.State} → {inputs}：{why} {need.Curve}");
            }

            string header = $"{db.name}：{trackSummary}" + contactSummary;
            if (errors.Count > 0) Debug.LogError(header + "\n结构错误\n" + string.Join("\n", errors), db);
            if (warnings.Length > 0) Debug.LogWarning(header + "\n" + warnings, db);
            else if (errors.Count == 0) Debug.Log(header, db);
        }

        // 校验发布轨道、状态映射和当前源配方，并分别统计缺失与过期的采样指纹。
        private static string CheckTracks(CharacterMotionDatabase db, CharacterMotionTrack[] tracks,
            Dictionary<string, CharacterClipState> states, Dictionary<AnimationClip, CharacterClipCurves> curves,
            List<string> errors, StringBuilder warnings)
        {
            var expected = CharacterMotionGroupBaker.ReadExpectedHashes(db, errors);
            var found = new HashSet<string>(StringComparer.Ordinal);
            int valid = 0, total = 0, missingHashes = 0, staleHashes = 0;
            foreach (var track in tracks ?? Array.Empty<CharacterMotionTrack>())
            {
                if (track?.States == null || track.States.Length == 0)
                {
                    errors.Add("发布轨道缺少状态路径。");
                    continue;
                }
                string label = track.States[0] ?? "未指定状态";
                if (string.IsNullOrEmpty(track.SampleSourceHash))
                {
                    missingHashes++;
                    warnings.AppendLine($"{label}：缺少采样指纹，请重新烘焙。");
                }
                else if (expected.TryGetValue(label, out string expectedHash) && expectedHash != track.SampleSourceHash)
                {
                    staleHashes++;
                    warnings.AppendLine($"{label}：采样指纹已过期，请重新烘焙。");
                }

                bool samplesValid = true;
                try
                {
                    CharacterMotionTrackAudit.ValidateRecipe(track);
                    CharacterMotionTrackAudit.ValidateSamples(track);
                }
                catch (InvalidOperationException error)
                {
                    errors.Add($"{label}：{error.Message}");
                    samplesValid = false;
                }

                CharacterClipState firstState = null;
                foreach (string path in track.States)
                {
                    total++;
                    if (string.IsNullOrWhiteSpace(path) || !found.Add(path))
                    {
                        errors.Add($"{label}：状态 {path} 为空或已被另一轨道使用。");
                        continue;
                    }
                    if (!states.TryGetValue(path, out var bound) || !CharacterMotionTrackAudit.MatchesPlayback(track, bound) ||
                        firstState != null && !CharacterMotionTrackAudit.SameBlend(firstState.Blend, bound.Blend))
                    {
                        errors.Add($"{path}：发布状态缺少相同片段和混合时钟的下身轨道。");
                        continue;
                    }
                    if (!expected.ContainsKey(path)) warnings.AppendLine($"{path}：源数据组缺少或无法验证此变体。");
                    firstState ??= bound;
                    if (samplesValid) valid++;
                }
                if (samplesValid) CheckTrackQuality(track, firstState, curves, warnings);
            }
            foreach (string path in expected.Keys)
                if (!found.Contains(path)) warnings.AppendLine($"{path}：当前源变体尚未发布，请重新烘焙。");
            return $"地面轨道状态可用 {valid}/{total}，缺采样指纹 {missingHashes}，过期指纹 {staleHashes}";
        }

        // 汇报可用轨道的采样质量，并检查在发布状态中实际参与混合的接触曲线。
        private static void CheckTrackQuality(CharacterMotionTrack track, CharacterClipState state,
            Dictionary<AnimationClip, CharacterClipCurves> curves, StringBuilder warnings)
        {
            var feedback = new List<string>();
            CharacterMotionTrackAudit.AddQualityWarnings(track, feedback);
            if (state != null && state.Blend.Second && curves.TryGetValue(state.Clip, out var first) &&
                curves.TryGetValue(state.Blend.Second, out var second))
                CharacterMotionTrackAudit.AddMixedContactWarnings(track, state.Blend, first, second, feedback);
            foreach (string message in feedback) warnings.AppendLine($"{track.States[0]}：{message}");
        }

        // 按当前数据组逐脚核对双源覆盖，同时检查直接绑定的下身片段。
        private static string CheckFootContacts(CharacterMotionGroups groups, Dictionary<AnimationClip, CharacterClipCurves> baked,
            Dictionary<AnimationClip, CharacterClipCurves> imported, StringBuilder warnings)
        {
            if (!groups) return string.Empty;
            var summary = new StringBuilder();
            foreach (string name in new[] { CharacterFootSampler.LeftContactCurve, CharacterFootSampler.RightContactCurve })
            {
                int complete = 0, partial = 0, absent = 0, single = 0, singleAbsent = 0;
                foreach (var group in groups.Groups ?? Array.Empty<GroundAnimationGroup>())
                {
                    if (group == null) continue;
                    var first = ReadCurve(group.First, name, imported, true);
                    var second = ReadCurve(group.Second, name, imported, true);
                    if (group.Second)
                    {
                        if (first != null && second != null) complete++;
                        else if (first != null || second != null) partial++;
                        else absent++;
                    }
                    else if (first != null) single++;
                    else singleAbsent++;

                    string coverage = second == null && first == null ? "双源均缺" : "双源仅一侧有曲线";
                    CheckFootSource(group.Name, group.First, name, first, baked, warnings, group.Second ? coverage : "单源缺失");
                    if (group.Second) CheckFootSource(group.Name, group.Second, name, second, baked, warnings, coverage);
                }
                foreach (var state in groups.Bindings ?? Array.Empty<CharacterClipState>())
                {
                    if (state == null || state.Layer != 0) continue;
                    var source = ReadCurve(state.Clip, name, imported, true);
                    if (source != null) single++;
                    else singleAbsent++;
                    CheckFootSource(state.Path, state.Clip, name, source, baked, warnings, "单源缺失");
                }
                summary.AppendLine().Append($"{name}：双源齐全 {complete}，仅一侧有曲线 {partial}，双源均缺 {absent}，单源齐全 {single}，单源缺失 {singleAbsent}");
            }
            return summary.ToString();
        }

        // 对每个源片段分别报告素材缺失和播放库待烘焙。
        private static void CheckFootSource(string label, AnimationClip clip, string name, CharacterClipCurve source,
            Dictionary<AnimationClip, CharacterClipCurves> baked, StringBuilder warnings, string coverage)
        {
            var published = ReadCurve(clip, name, baked, false);
            string input = $"{label} → {(clip ? clip.name : "未指定片段")} · {name}";
            if (source == null) warnings.AppendLine($"{input}：源素材缺少曲线（{coverage}）");
            if (!SameCurve(source, published)) warnings.AppendLine($"{input}：曲线与源素材不同，播放库待烘焙");
        }

        // 核对已发布曲线的时间基准、外推设置和关键帧。
        private static bool SameCurve(CharacterClipCurve source, CharacterClipCurve published)
        {
            if (source == null || published == null) return source == published;
            var a = source.Curve;
            var b = published.Curve;
            if (source.InSeconds != published.InSeconds || a.preWrapMode != b.preWrapMode ||
                a.postWrapMode != b.postWrapMode || a.length != b.length) return false;
            for (int i = 0; i < a.length; i++)
                if (!a[i].Equals(b[i])) return false;
            return true;
        }

        // 读取已发布曲线；检查源素材时按需导入并缓存曲线数据。
        private static CharacterClipCurve ReadCurve(AnimationClip clip, string name,
            Dictionary<AnimationClip, CharacterClipCurves> curves, bool loadSource)
        {
            if (!clip) return null;
            if (!curves.TryGetValue(clip, out var entry) && loadSource)
                curves.Add(clip, entry = CharacterClipLibraryBaker.ReadCurves(clip));
            return entry?.Find(name);
        }
    }
}
