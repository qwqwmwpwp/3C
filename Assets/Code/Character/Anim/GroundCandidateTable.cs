using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 描述筛选开关以及无法选出候选时的去向，均不改变行为层许可。
    internal enum GroundRuleGate { Any, Required, Excluded }
    // 决定无匹配行或空候选组时是否继续在已绑定的库范围内搜索。
    internal enum GroundRuleFallback { BoundLibrary, LocomotionRules }

    // 顺序模式采用首个有合法入点的库，联合模式在同一评分空间中择优。
    internal enum GroundLibrarySearchMode { Ordered, Union }

    // 约束一条候选规则在某个已登记库内可用的动作组。
    [Serializable] internal sealed class GroundCandidateSource
    {
        [InspectorName("逻辑库键")] public string LibraryKey;
        [InspectorName("逻辑组键"), Tooltip("留空表示搜索此逻辑库的全部地面动作组。")] public string[] GroupKeys;
        [InspectorName("可选来源"), Tooltip("此角色缺少该库或组时跳过本来源，其余来源仍按声明顺序参与。")] public bool Optional;
    }

    // 规定命中条件、候选范围和是否允许输入急转触发搜索。
    [Serializable] internal sealed class GroundCandidateRule
    {
        public string Name;
        public int Priority;
        public GroundMotionUse Use; // 0 匹配所有用途。
        [InspectorName("响应输入急转"), Tooltip("仅用途为 Any 或 Turn 的规则可启用。输入急转由正常排序选出的规则决定。")]
        public bool RespondToInputTurn;
        public GroundTrackGait Gait;
        public GroundTrackSide Support;
        public GroundRuleGate MoveApproved, Returning;
        [InspectorName("需要的活动状态")] public string[] RequiredStates;
        [Tooltip("勾选可用的移动阶段。")] public GroundRulePhases Phases = GroundRulePhases.All;
        public Vector2 Speed = new Vector2(0f, 100f), SignedAngle = new Vector2(-180f, 180f);
        [InspectorName("库选择模式")] public GroundLibrarySearchMode SearchMode;
        [InspectorName("候选库与组")] public GroundCandidateSource[] Sources;
        public GroundFeatureGroups Features = GroundFeatureGroups.All;
        public GroundRuleFallback Empty = GroundRuleFallback.LocomotionRules;

        // 判定规则上下文，所有状态条件均来自本次已冻结的上下文。
        internal bool Matches(in GroundPoseQuery query, in CharacterAnimationContext context, CharacterLocomotionPhase phase,
            GroundTrackSide support, bool returning) =>
            (Use == 0 || Use == query.Use) && (Gait == GroundTrackGait.Any || Gait == query.Target) &&
            (Support == GroundTrackSide.Either || Support == support) && ((int)Phases & (1 << (int)phase)) != 0 &&
            Accepts(MoveApproved, context.MoveApproved) && Accepts(Returning, returning) &&
            HasRequiredStates(context.States) &&
            query.EntrySpeed >= Speed.x && query.EntrySpeed <= Speed.y && query.Angle >= SignedAngle.x && query.Angle <= SignedAngle.y;

        // 所有声明的键均在本次冻结上下文中活动时才允许选择该行。
        private bool HasRequiredStates(in AnimationStateSet states)
        {
            if (RequiredStates != null)
                foreach (var key in RequiredStates)
                    if (!states.Contains(key)) return false;
            return true;
        }

        // 任意、必须、禁止三态匹配。
        private static bool Accepts(GroundRuleGate gate, bool value) => gate == GroundRuleGate.Any || value == (gate == GroundRuleGate.Required);

        // 检测同优先级规则可同时命中的上下文交集。
        internal bool Overlaps(GroundCandidateRule b) =>
            Priority == b.Priority && (Use == 0 || b.Use == 0 || Use == b.Use) &&
            (Gait == 0 || b.Gait == 0 || Gait == b.Gait) && (Support == 0 || b.Support == 0 || Support == b.Support) &&
            (MoveApproved == 0 || b.MoveApproved == 0 || MoveApproved == b.MoveApproved) &&
            (Returning == 0 || b.Returning == 0 || Returning == b.Returning) && (Phases & b.Phases) != 0 &&
            Speed.x <= b.Speed.y && b.Speed.x <= Speed.y && SignedAngle.x <= b.SignedAngle.y && b.SignedAngle.x <= SignedAngle.y;

        // 固定后台使用的规则条件，来源库与组由独立范围保存。
        internal GroundCandidateRule Copy()
        {
            var copy = (GroundCandidateRule)MemberwiseClone();
            copy.RequiredStates = (string[])RequiredStates?.Clone();
            copy.Sources = null;
            return copy;
        }
    }

    // 与角色移动阶段逐项对应的表条件位。
    [Flags] internal enum GroundRulePhases { Idle = 1, Starting = 2, Steady = 4, Turning = 8, MotionOverride = 16, Stopping = 32, BrakeLeadIn = 64, All = 127 }

    // 保存基于当前动画状态和地面状态的候选范围配置。
    [CreateAssetMenu(menuName = "3C/地面候选条件表")]
    public sealed class GroundCandidateTable : ScriptableObject
    {
#if UNITY_EDITOR
        // 标记已装配的角色搜索范围，使编辑器修改在下一批次重新生效。
        private void OnValidate() => GroundSearchLibraries.InvalidateBindings();
#endif
        [SerializeField] internal GroundRuleFallback NoMatch;
        [SerializeField] internal GroundCandidateRule[] Rules;
    }

    // 保存角色条件命中的已冻结库范围；按优先库逐次搜索时进一步限定到单库。
    internal readonly struct GroundRuleSelection
    {
        internal readonly GroundCandidateRule Rule;
        internal readonly GroundLibraryScope Scope;
        internal readonly string Label;
        internal readonly bool Skip, Relaxed, IsInputTurn, BoundLibraryFallback;
        private readonly int? only;
        internal GroundFeatureGroups Features => Rule?.Features ?? GroundFeatureGroups.All;
        internal bool Relax => !Relaxed && Rule?.Empty == GroundRuleFallback.BoundLibrary;
        internal bool UnionBlocked => Rule?.SearchMode == GroundLibrarySearchMode.Union && Scope?.UnionIssue != null;
        internal bool Ordered => Rule?.SearchMode != GroundLibrarySearchMode.Union || Relaxed && Scope?.RelaxedUnionIssue != null;
        internal int Count => Scope?.Indices.Length ?? 0;

        // 固定候选规则和范围，保留输入急转及候选表无匹配回退的来源。
        internal GroundRuleSelection(GroundCandidateRule rule, GroundLibraryScope scope, string label,
            bool skip = false, bool relaxed = false, int? only = null, bool isInputTurn = false, bool boundLibraryFallback = false)
        {
            Rule = rule;
            Scope = scope;
            Label = label;
            Skip = skip;
            Relaxed = relaxed;
            IsInputTurn = isInputTurn;
            BoundLibraryFallback = boundLibraryFallback;
            this.only = only;
        }

        // 单库优先搜索不将尚未尝试的其他库计入本轮淘汰。
        internal bool InSearchedLibrary(int index) => !only.HasValue || only == index;

        // 检查候选的真实来源库和库内路径是否在本次范围。
        internal bool Allows(CharacterMotionTrack track) => !Skip && (!UnionBlocked || Relaxed) && InSearchedLibrary(track.LibraryIndex) &&
            Scope != null && Scope.Allows(track, Relaxed);

        // 将本次试算或已命中结果固定到一个优先库。
        internal GroundRuleSelection ForLibrary(int index) =>
            new GroundRuleSelection(Rule, Scope, Label, Skip, Relaxed, index, IsInputTurn, BoundLibraryFallback);

        // 保持候选表给出的库范围，只放宽组过滤。
        internal GroundRuleSelection WithoutGroups() =>
            new GroundRuleSelection(Rule, Scope, Label + " · 放宽组过滤", relaxed: true, isInputTurn: IsInputTurn, boundLibraryFallback: BoundLibraryFallback);

#if UNITY_EDITOR
        // 标记由输入边沿触发的候选查询，供诊断区分常规转向。
        internal GroundRuleSelection ForInputTurn() =>
            new GroundRuleSelection(Rule, Scope, Label, Skip, Relaxed, only, isInputTurn: true, boundLibraryFallback: BoundLibraryFallback);
#endif
    }

    // 解析候选组并校验表规则，查询时按优先级接受程序允许的合法行。
    internal sealed class GroundCandidatePolicy
    {
        private readonly List<(int index, GroundRuleSelection selection)> rows = new List<(int, GroundRuleSelection)>();
        private readonly GroundRuleFallback fallback;
        private readonly bool hasTable;
        private readonly AnimationDecisionEvaluator decisions;
        internal readonly string Issue;
#if UNITY_EDITOR
        private int inspectedIndex;
        private string inspectedRule;
        internal string InputTurnDecision { get; private set; }
#endif

        private readonly GroundLibraryScope defaults;
        internal readonly bool SupportsInputTurn;

        // 使用角色主线程已编译的库约束生成后台候选策略。
        internal static GroundCandidatePolicy Bind(GroundCandidateTable table, GroundSearchLibraries libraries,
            CompiledAnimationDecisions program = null) => new GroundCandidatePolicy(libraries, table, program);

        // 按声明顺序编译库范围，缺少的库或组仅影响其所属的行和来源。
        private GroundCandidatePolicy(GroundSearchLibraries libraries, GroundCandidateTable table, CompiledAnimationDecisions program)
        {
            defaults = GroundLibraryScope.ForDeclaredLibraries(libraries);
            if (!table) return;
            hasTable = true;
            fallback = table.NoMatch;
            if (!Enum.IsDefined(typeof(GroundRuleFallback), fallback)) { Issue = "候选表无匹配回退无效"; return; }
            var rules = table.Rules ?? Array.Empty<GroundCandidateRule>();
            for (int i = 0; i < rules.Length; i++)
            {
                var source = rules[i];
                if (source == null || string.IsNullOrWhiteSpace(source.Name) || source.Use < 0 || source.Use > GroundMotionUse.Transition ||
                    !Enum.IsDefined(typeof(GroundTrackGait), source.Gait) || !Enum.IsDefined(typeof(GroundTrackSide), source.Support) ||
                    !Enum.IsDefined(typeof(GroundRuleGate), source.MoveApproved) || !Enum.IsDefined(typeof(GroundRuleGate), source.Returning) ||
                    !Enum.IsDefined(typeof(GroundLibrarySearchMode), source.SearchMode) ||
                    !Enum.IsDefined(typeof(GroundRuleFallback), source.Empty) || (source.Phases & ~GroundRulePhases.All) != 0 ||
                    (source.Features & ~GroundFeatureGroups.All) != 0 || !(source.Speed.x >= 0f && source.Speed.y >= source.Speed.x) ||
                    !(source.SignedAngle.x >= -180f && source.SignedAngle.y <= 180f && source.SignedAngle.y >= source.SignedAngle.x) || source.Phases == 0)
                { Issue = "候选表名称、速度、阶段或角度范围无效"; return; }
                if (source.RespondToInputTurn && source.Use != GroundMotionUse.Any && source.Use != GroundMotionUse.Turn)
                { Issue = $"候选表行 {i} · {source.Name} 的用途不支持输入急转"; return; }
                if (source.RequiredStates != null && Array.Exists(source.RequiredStates, string.IsNullOrWhiteSpace))
                { Issue = $"候选表行 {i} · {source.Name} 含空状态键"; return; }
                var rule = source.Copy();
                var scope = GroundLibraryScope.Resolve(libraries, source);
                if (scope.BindingIssue != null)
                { Issue = $"{table.name} · 第 {i + 1} 行 {source.Name}：{scope.BindingIssue}"; return; }
                foreach (var (_, prior) in rows)
                    if (rule.Overlaps(prior.Rule)) { Issue = $"规则 {rule.Name} 与 {prior.Rule.Name} 同优先级重叠"; return; }
                // 没有可用来源库时直接回退；严格范围失效时，仅显式允许放宽的行继续搜索。
                bool mustUseStrictScope = scope.Indices.Length == 0 || rule.Empty == GroundRuleFallback.LocomotionRules;
                string blockIssue = mustUseStrictScope ? scope.StrictBlockIssue : null;
                string label = blockIssue != null ? $"{rule.Name} · {blockIssue}" : rule.Name;
                rows.Add((i, new GroundRuleSelection(rule, scope, label, skip: blockIssue != null)));
            }
            rows.Sort((a, b) => b.selection.Rule.Priority.CompareTo(a.selection.Rule.Priority));
            SupportsInputTurn = rows.Exists(row => row.selection.Rule.RespondToInputTurn);
            if (program != null && rows.Count > 0)
                decisions = new AnimationDecisionEvaluator(program, AnimationDecisionStage.CandidateSelection);
        }

        // 只让按正常条件和优先级选中的启用行响应输入急转，保留该行的空候选回退。
        internal bool TrySelectInputTurn(in GroundPoseQuery edge, in CharacterAnimationContext context,
            CharacterLocomotionPhase phase, GroundTrackSide support, out GroundPoseQuery query)
        {
            query = default;
            var selection = Select(edge, context, phase, support, false);
#if UNITY_EDITOR
            InputTurnDecision = selection.Rule == null ? Issue ?? "没有满足条件且获许可的候选规则" :
                selection.Rule.RespondToInputTurn ? selection.Label : $"{selection.Rule.Name} 未启用输入急转";
#endif
            if (selection.Rule?.RespondToInputTurn != true) return false;
#if UNITY_EDITOR
            selection = selection.ForInputTurn();
            inspectedRule = selection.Label + " · 输入急转";
#endif
            query = new GroundPoseQuery(edge, selection);
            return true;
        }

        // 选择首个满足原有表条件且被程序接受的行，耗尽可选行时按表回退。
        internal GroundRuleSelection Select(in GroundPoseQuery query, in CharacterAnimationContext context,
            CharacterLocomotionPhase phase, GroundTrackSide support, bool returning)
        {
            if (Issue == null)
            {
                if (decisions != null)
                {
                    decisions.Begin(AnimationDecisionStage.CandidateSelection);
                    decisions.Set(AnimationDecisionInput.Speed, query.EntrySpeed);
                    decisions.Set(AnimationDecisionInput.SignedAngle, query.Angle);
                    decisions.Set(AnimationDecisionInput.MoveApproved, context.MoveApproved);
                    decisions.Set(AnimationDecisionInput.Returning, returning);
                }
                foreach (var (index, row) in rows)
                    if (row.Rule.Matches(query, context, phase, support, returning) && AllowsRule(index, row.Rule))
                        return row;
            }
            if (!hasTable) return new GroundRuleSelection(null, defaults, "未配置候选表，使用登记库");
            bool skip = fallback == GroundRuleFallback.LocomotionRules;
            string label = skip ? "无匹配规则，使用步态规则" : "无匹配规则，回退到登记库";
            return new GroundRuleSelection(null, defaults, Issue ?? label, skip, boundLibraryFallback: !skip);
        }

        // 应用本次候选许可，并在编辑器记录命中条件的原表位置。
        private bool AllowsRule(int index, GroundCandidateRule rule)
        {
#if UNITY_EDITOR
            inspectedIndex = index;
            inspectedRule = rule.Name;
#endif
            return decisions?.Flag(AnimationDecisionOutput.AllowCandidateRule) ?? true;
        }

        // 开始新一次选姿前清除候选表的上一轮诊断。
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        internal void ClearTrace()
        {
#if UNITY_EDITOR
            decisions?.ClearTrace();
            inspectedRule = null;
            InputTurnDecision = null;
#endif
        }

#if UNITY_EDITOR
        // 输出最近求值的候选行及其程序分支，并保留声明行号。
        internal void AppendTrace(System.Text.StringBuilder trace)
        {
            if (inspectedRule != null)
                trace.Append("候选表行 ").Append(inspectedIndex).Append(" · ").AppendLine(inspectedRule);
            decisions?.AppendTrace(trace);
        }
#endif
    }
}
