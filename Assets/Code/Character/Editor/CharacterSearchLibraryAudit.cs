using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 使用运行时匹配约束检查角色的搜索库，并定位联合评分和可播放绑定问题。
    internal static class CharacterSearchLibraryAudit
    {
        // 根据角色配置核对显式登记库及条件表，报告启用匹配时的空配置。
        internal static void Inspect(CharacterAnimationConfig config, UnityEngine.Object context)
        {
            MotionMatchModule match;
            try { match = config.ResolveModules().Match; }
            catch (ArgumentException error) { Debug.LogWarning(error.Message, context); return; }
            if (!match) return;

            var playback = config.PlaybackLibrary;
            var libraries = new GroundSearchLibraries(playback, match.searchLibraries, snapshotSamples: false);
            var issues = new List<string>();
            if (match.MatchingEnabled && libraries.Databases.Length == 0)
                issues.Add(GroundSearchLibraries.NoDeclaredLibraries);
            var inspected = new HashSet<CharacterMotionDatabase> { playback };
            issues.AddRange(RuleIssues(match.candidates, libraries));
            for (int i = 0; i < libraries.Databases.Length; i++)
            {
                var source = libraries.Databases[i];
                string label = AssetLabel(source, libraries.Names[i]);
                if (libraries.Issues[i] != null) issues.Add($"{label}：{libraries.Issues[i]}");
                if (!source || !inspected.Add(source) || source.Format != CharacterMotionDatabase.CurrentFormat ||
                    source.BakedTracks == null) continue;
                CheckTrackBindings(source, label, issues);
            }

            issues.AddRange(IncompatiblePairs(libraries));
            string header = $"{config.name} · 地面搜索库检查 · {libraries.Databases.Length} 个登记项";
            if (issues.Count > 0) Debug.LogWarning(header + "\n" + string.Join("\n", issues), context);
            else Debug.Log(header + "，候选规则、轨道绑定及联合评分检查通过。", context);
        }

        // 在声明位置报告每条规则的来源库问题，并保留候选表整体校验。
        internal static IEnumerable<string> RuleIssues(GroundCandidateTable table, GroundSearchLibraries libraries)
        {
            if (!table) yield break;
            var policy = GroundCandidatePolicy.Bind(table, libraries);
            if (policy.Issue != null)
                yield return $"{table.name}：{policy.Issue}。按条件表的无匹配设置处理。";
            var rules = table.Rules ?? Array.Empty<GroundCandidateRule>();
            for (int i = 0; i < rules.Length; i++)
            {
                var rule = rules[i];
                if (rule == null) continue;
                var scope = GroundLibraryScope.Resolve(libraries, rule);
                if (scope.Issue != null)
                    yield return $"{table.name} · 第 {i + 1} 行 {rule.Name}：{scope.Issue}";
            }
        }

        // 检查可搜索轨道及其他播放别名，指出失效绑定所在的库和数组位置。
        private static void CheckTrackBindings(CharacterMotionDatabase database, string label, List<string> issues)
        {
            var playback = database.Playback?.States ?? Array.Empty<CharacterClipState>();
            var bakeIssues = CharacterSearchLibraryBakeIssues.Read(database);
            for (int i = 0; i < database.BakedTracks.Length; i++)
            {
                var track = database.BakedTracks[i];
                string issue = GroundSearchLibraries.TrackIssue(database, track);
                if (issue != null) issues.Add($"{label} · {TrackLabel(track, i)}：{issue}");
                if (track?.States is { Length: > 0 } && !string.IsNullOrWhiteSpace(track.States[0]) &&
                    bakeIssues.TryGetValue(track.States[0], out string bakeIssue))
                    issues.Add($"{label} · {TrackLabel(track, i)}：{bakeIssue}");
                for (int j = 1; j < (track?.States?.Length ?? 0); j++)
                {
                    string alias = track.States[j];
                    var state = string.IsNullOrWhiteSpace(alias) ? null : Array.Find(playback, s => s?.Path == alias);
                    string aliasIssue = state == null ? "缺少播放绑定" : GroundSearchLibraries.PlaybackIssue(database, track, state);
                    if (aliasIssue != null)
                        issues.Add($"{label} · {TrackLabel(track, i)} · {alias ?? "未指定状态"}：{aliasIssue}");
                }
            }
        }

        // 保留因布局不兼容未能登记候选的库，给出具体无法联合评分的配对。
        internal static IEnumerable<string> IncompatiblePairs(GroundSearchLibraries libraries)
        {
            for (int i = 0; i < libraries.Databases.Length; i++)
            {
                if (libraries.Layouts[i] == null) continue;
                for (int j = i + 1; j < libraries.Databases.Length; j++)
                {
                    if (libraries.Layouts[j] == null) continue;
                    string issue = GroundSearchLibraries.ComparisonIssue(libraries.Layouts[i], libraries.Layouts[j]);
                    if (issue == null) continue;
                    string first = AssetLabel(libraries.Databases[i], libraries.Names[i]);
                    string second = AssetLabel(libraries.Databases[j], libraries.Names[j]);
                    yield return $"{first} 与 {second} 无法联合评分：{issue}";
                }
            }
        }

        // 即使缺少状态映射也保留轨道的数组位置，方便定位烘焙问题。
        internal static string TrackLabel(CharacterMotionTrack track, int index)
        {
            string state = track?.States is { Length: > 0 } ? track.States[0] : null;
            string clip = track != null && track.Clip ? track.Clip.name : "缺少片段";
            return $"轨道 #{index + 1} · {(string.IsNullOrWhiteSpace(state) ? "缺少主状态" : state)} · {clip}";
        }

        // 同名动作库通过资产路径区分，缺失引用仍显示原登记位置。
        private static string AssetLabel(CharacterMotionDatabase database, string name) =>
            database ? $"{name}（{AssetDatabase.GetAssetPath(database)}）" : name;
    }
}
