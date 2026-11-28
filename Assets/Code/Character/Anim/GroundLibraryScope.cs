using System;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    // 保存候选规则或无匹配回退的库优先序与库内组路径，供后台筛选。
    internal sealed class GroundLibraryScope
    {
        internal readonly int[] Indices;
        internal readonly string Issue, BindingIssue, UnionIssue, RelaxedUnionIssue, StrictBlockIssue;
        private readonly Dictionary<int, HashSet<string>> groups;
#if UNITY_EDITOR
        internal readonly (int Index, string Name, string Issue)[] Declarations;
        internal readonly string DeclaredLibraries;
#endif

        // 固定库内状态范围，并记录严格搜索无法产生候选或无法联合评分的原因。
        private GroundLibraryScope(List<int> indices, List<string> issues,
            Dictionary<int, HashSet<string>> groups, string bindingIssue, string unionIssue, string relaxedUnionIssue
#if UNITY_EDITOR
            , List<(int Index, string Name, string Issue)> declarations
#endif
            )
        {
            Indices = indices.ToArray();
            Issue = issues.Count == 0 ? null : string.Join("；", issues);
            BindingIssue = bindingIssue;
            UnionIssue = unionIssue;
            RelaxedUnionIssue = relaxedUnionIssue;
            this.groups = groups;
            StrictBlockIssue = unionIssue;
            if (StrictBlockIssue == null && !indices.Exists(index => AllowsLibrary(index, relaxed: false)))
                StrictBlockIssue = Issue ?? "所选动作组没有候选状态";
#if UNITY_EDITOR
            Declarations = declarations.ToArray();
            DeclaredLibraries = string.Join(" → ", declarations.ConvertAll(declaration => declaration.Name));
#endif
        }

        // 为无匹配规则或无条件表的回退按登记顺序纳入所有有效库。
        internal static GroundLibraryScope ForDeclaredLibraries(GroundSearchLibraries libraries) =>
            libraries == null ? null : Build(libraries, null, GroundLibrarySearchMode.Ordered, useDeclaredLibraries: true);

        // 解析候选规则显式声明的来源库及库内组，诊断未声明的来源。
        internal static GroundLibraryScope Resolve(GroundSearchLibraries libraries, GroundCandidateRule rule) =>
            Build(libraries, rule.Sources, rule.SearchMode, useDeclaredLibraries: false,
                allowRelaxation: rule.Empty == GroundRuleFallback.BoundLibrary);

        // 按指定范围验证库与组，并检查联合评分所需的兼容性。
        private static GroundLibraryScope Build(GroundSearchLibraries libraries, GroundCandidateSource[] sources,
            GroundLibrarySearchMode searchMode, bool useDeclaredLibraries, bool allowRelaxation = false)
        {
            var indices = new List<int>();
            string bindingIssue = null;
            var issues = new List<string>();
            var groups = new Dictionary<int, HashSet<string>>();
#if UNITY_EDITOR
            var declarations = new List<(int Index, string Name, string Issue)>();
#endif
            if (useDeclaredLibraries)
            {
                for (int i = 0; i < libraries.Databases.Length; i++)
                    Add(i, libraries.LibraryKeys[i], null, null, false);
            }
            else if (!(sources?.Length > 0))
            {
                bindingIssue = "候选规则未配置逻辑来源库";
                issues.Add(bindingIssue);
            }
            else
            {
                var declared = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < sources.Length; i++)
                {
                    var source = sources[i];
                    string key = source?.LibraryKey;
                    string location = $"来源 {i + 1} · ";
                    if (string.IsNullOrWhiteSpace(key) || !declared.Add(key))
                    {
                        string error = string.IsNullOrWhiteSpace(key) ? "缺少逻辑库键" : $"重复的逻辑库键 {key}";
                        bindingIssue ??= location + error;
                        issues.Add(location + error);
                        continue;
                    }
                    if (source.GroupKeys != null)
                    {
                        var listed = new HashSet<string>(StringComparer.Ordinal);
                        foreach (string group in source.GroupKeys)
                        {
                            if (!string.IsNullOrWhiteSpace(group) && listed.Add(group)) continue;
                            string error = string.IsNullOrWhiteSpace(group) ? "缺少逻辑组键" : $"重复的逻辑组键 {group}";
                            bindingIssue ??= location + key + " · " + error;
                            issues.Add(location + key + " · " + error);
                        }
                    }
                    Add(libraries?.FindLibrary(key) ?? -1, key, source.GroupKeys, location, !source.Optional);
                }
            }
            string unionIssue = null, relaxedUnionIssue = null;
            if (searchMode == GroundLibrarySearchMode.Union)
            {
                unionIssue = CompareLayouts(relaxed: false);
                if (unionIssue != null) issues.Add(unionIssue);
                if (allowRelaxation)
                {
                    relaxedUnionIssue = CompareLayouts(relaxed: true);
                    if (relaxedUnionIssue != null && relaxedUnionIssue != unionIssue)
                        issues.Add($"放宽组过滤后，{relaxedUnionIssue}");
                }
            }
            bindingIssue ??= unionIssue ?? relaxedUnionIssue;
            return new GroundLibraryScope(indices, issues, groups, bindingIssue, unionIssue, relaxedUnionIssue
#if UNITY_EDITOR
                , declarations
#endif
                );

            // 解析完整来源后才发布，缺少可选绑定时连同该来源的放宽搜索一起跳过。
            void Add(int index, string key, string[] groupKeys, string location, bool required)
            {
                string label = index >= 0 ? libraries.Names[index] : key;
                string issue = index < 0 ? "角色缺少该逻辑库绑定" : libraries.Issues[index];
                HashSet<string> states = null;
                if (issue == null && groupKeys?.Length > 0)
                {
                    states = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string groupKey in groupKeys)
                    {
                        var group = libraries.FindGroup(index, groupKey, out issue);
                        if (group == null) break;
                        foreach (var variant in group.Variants ?? Array.Empty<CharacterMotionTrack>())
                            foreach (string path in variant?.States ?? Array.Empty<string>())
                                if (!string.IsNullOrWhiteSpace(path)) states.Add(path);
                    }
                }
#if UNITY_EDITOR
                declarations.Add((index, label, issue));
#endif
                if (issue != null)
                {
                    string detail = $"{location}{label} · {issue}";
                    issues.Add(detail);
                    if (required) bindingIssue ??= detail;
                    return;
                }
                indices.Add(index);
                groups.Add(index, states);
            }

            // 分别核对严格和放宽阶段能提供候选的库所用评分布局。
            string CompareLayouts(bool relaxed)
            {
                var mismatches = new List<string>();
                int first = -1;
                foreach (int index in indices)
                {
                    if (!relaxed && groups[index]?.Count == 0) continue;
                    if (first < 0) { first = index; continue; }
                    string issue = GroundSearchLibraries.ComparisonIssue(libraries.Layouts[first], libraries.Layouts[index]);
                    if (issue != null) mismatches.Add($"{libraries.Names[first]} 与 {libraries.Names[index]} 无法联合评分：{issue}");
                }
                return mismatches.Count == 0 ? null : string.Join("；", mismatches);
            }
        }

        // 跳过当前阶段没有任何组候选的库，避免将其用作联合评分参考。
        internal bool AllowsLibrary(int index, bool relaxed) =>
            groups.TryGetValue(index, out var states) && (relaxed || states?.Count != 0);

        // 按库和状态同时判定组资格，放宽时仍保留库边界。
        internal bool Allows(CharacterMotionTrack track, bool relaxed) =>
            groups.TryGetValue(track.LibraryIndex, out var states) &&
            (relaxed || states == null || states.Contains(track.States[0]));

        // 联合评分平分时按表行声明的库顺序决胜。
        internal int Rank(int library) => Array.IndexOf(Indices, library);
    }
}
