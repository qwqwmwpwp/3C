using System;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    // 保存候选规则或无匹配回退的库优先序与库内组路径，供后台筛选。
    internal sealed class GroundLibraryScope
    {
        internal readonly int[] Indices;
        internal readonly string Label, Issue, UnionIssue, RelaxedUnionIssue, StrictBlockIssue;
        private readonly Dictionary<int, HashSet<string>> groups;

        // 固定库内状态范围，并记录严格搜索无法产生候选或无法联合评分的原因。
        private GroundLibraryScope(List<int> indices, List<string> labels, List<string> issues,
            Dictionary<int, HashSet<string>> groups, string unionIssue, string relaxedUnionIssue)
        {
            Indices = indices.ToArray();
            Label = string.Join(" → ", labels);
            Issue = issues.Count == 0 ? null : string.Join("；", issues);
            UnionIssue = unionIssue;
            RelaxedUnionIssue = relaxedUnionIssue;
            this.groups = groups;
            StrictBlockIssue = unionIssue;
            if (StrictBlockIssue == null && !indices.Exists(index => AllowsLibrary(index, relaxed: false)))
                StrictBlockIssue = Issue ?? "所选动作组没有候选状态";
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
            var labels = new List<string>();
            var issues = new List<string>();
            var groups = new Dictionary<int, HashSet<string>>();
            if (useDeclaredLibraries)
                foreach (var db in libraries.Databases) Add(db, null, null);
            else if (!(sources?.Length > 0))
                issues.Add("候选规则未配置来源库（Sources）");
            else if (libraries == null)
                issues.Add("角色未登记来源库");
            else
                for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
                {
                    var source = sources[sourceIndex];
                    Add(source?.Library, source?.GroupIds, $"来源 {sourceIndex + 1} · ");
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
            return new GroundLibraryScope(indices, labels, issues, groups, unionIssue, relaxedUnionIssue);

            // 将稳定 ID 解析为本库状态集合，无效引用仅从严格范围中排除。
            void Add(CharacterMotionDatabase db, string[] groupIds, string sourceLabel)
            {
                int i = db ? Array.IndexOf(libraries.Databases, db) : -1;
                if (i < 0) { issues.Add(sourceLabel + (db ? $"库 {db.name} 未登记" : "未指定或缺失来源库")); return; }
                string label = libraries.Names[i];
                if (groups.ContainsKey(i)) { issues.Add($"{sourceLabel}{label} 重复指定"); return; }
                labels.Add(label);
                HashSet<string> states = null;
                if (groupIds?.Length > 0)
                {
                    states = new HashSet<string>(StringComparer.Ordinal);
                    for (int groupIndex = 0; groupIndex < groupIds.Length; groupIndex++)
                    {
                        string id = groupIds[groupIndex];
                        var group = libraries.FindGroup(i, id);
                        if (group == null)
                        {
                            issues.Add($"{sourceLabel}{label} 第 {groupIndex + 1} 个组 ID 无法解析：{(string.IsNullOrWhiteSpace(id) ? "空" : id)}");
                            continue;
                        }
                        foreach (var variant in group.Variants ?? Array.Empty<CharacterMotionTrack>())
                            foreach (var path in variant?.States ?? Array.Empty<string>())
                                if (!string.IsNullOrWhiteSpace(path)) states.Add(path);
                    }
                }
                if (libraries.Issues[i] != null) { issues.Add($"{sourceLabel}{label} {libraries.Issues[i]}"); return; }
                indices.Add(i);
                groups.Add(i, states);
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
