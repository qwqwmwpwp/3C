using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 描述筛选开关以及无法选出候选时的去向，均不改变行为层许可。
    internal enum GroundRuleGate { Any, Required, Excluded }
    // 决定无匹配行或空候选组时是否放宽至同一绑定库。
    internal enum GroundRuleFallback { BoundLibrary, BehaviorRules }

    // 将一个只读上下文映射到当前播放库的一组状态和特征子集。
    [Serializable] internal sealed class GroundCandidateRule
    {
        public string Name;
        public int Priority;
        public GroundMotionUse Use; // 0 匹配所有用途。
        public GroundTrackGait Gait;
        public GroundTrackSide Support;
        public GroundRuleGate MoveApproved, Returning;
        public CharacterBehaviorId RequiredActive;
        [Tooltip("勾选可用的移动阶段。")] public GroundRulePhases Phases = GroundRulePhases.All;
        public Vector2 Speed = new Vector2(0f, 100f), SignedAngle = new Vector2(-180f, 180f);
        [Tooltip("空列表使用当前绑定库内所有合法组。")] public string[] Groups;
        public GroundFeatureGroups Features = GroundFeatureGroups.All;
        public GroundRuleFallback Empty = GroundRuleFallback.BehaviorRules;

        // 判定规则上下文，所有行为条件都来自已经裁决的快照。
        internal bool Matches(in GroundPoseQuery query, in CharacterBehaviorFrameResult behavior, CharacterLocomotionPhase phase,
            GroundTrackSide support, bool returning) =>
            (Use == 0 || Use == query.Use) && (Gait == GroundTrackGait.Any || Gait == query.Target) &&
            (Support == GroundTrackSide.Either || Support == support) && ((int)Phases & (1 << (int)phase)) != 0 &&
            Accepts(MoveApproved, behavior.MoveApproved) && Accepts(Returning, returning) &&
            (behavior.ActiveBehaviors & RequiredActive) == RequiredActive &&
            query.EntrySpeed >= Speed.x && query.EntrySpeed <= Speed.y && query.Angle >= SignedAngle.x && query.Angle <= SignedAngle.y;

        // 任意、必须、禁止三态匹配。
        private static bool Accepts(GroundRuleGate gate, bool value) => gate == GroundRuleGate.Any || value == (gate == GroundRuleGate.Required);

        // 检测同优先级规则可同时命中的上下文交集。
        internal bool Overlaps(GroundCandidateRule b) =>
            Priority == b.Priority && (Use == 0 || b.Use == 0 || Use == b.Use) &&
            (Gait == 0 || b.Gait == 0 || Gait == b.Gait) && (Support == 0 || b.Support == 0 || Support == b.Support) &&
            (MoveApproved == 0 || b.MoveApproved == 0 || MoveApproved == b.MoveApproved) &&
            (Returning == 0 || b.Returning == 0 || Returning == b.Returning) && (Phases & b.Phases) != 0 &&
            Speed.x <= b.Speed.y && b.Speed.x <= Speed.y && SignedAngle.x <= b.SignedAngle.y && b.SignedAngle.x <= SignedAngle.y;

        // 固定规则定义，原始资产不泄漏到后台搜索。
        internal GroundCandidateRule Copy() => (GroundCandidateRule)MemberwiseClone();
    }

    // 与角色移动阶段逐项对应的表条件位。
    [Flags] internal enum GroundRulePhases { Idle = 1, Starting = 2, Steady = 4, Turning = 8, Dash = 16, Stopping = 32, BrakeLeadIn = 64, All = 127 }

    // 保存基于当前行为和地面状态的候选范围配置。
    [CreateAssetMenu(menuName = "3C/地面候选条件表")]
    public sealed class GroundCandidateTable : ScriptableObject
    {
        [SerializeField] internal GroundRuleFallback NoMatch;
        [SerializeField] internal GroundCandidateRule[] Rules;
    }

    // 保持一条已命中规则的候选范围、缺失回退和可用特征组。
    internal readonly struct GroundRuleSelection
    {
        private readonly HashSet<string> states;
        internal readonly GroundCandidateRule Rule;
        internal readonly string Label;
        internal readonly bool Skip, Relaxed;
        internal GroundFeatureGroups Features => Rule?.Features ?? GroundFeatureGroups.All;
        internal bool Relax => states != null && Rule.Empty == GroundRuleFallback.BoundLibrary;

        // 将已解析的状态集合与同次查询使用的特征配置作为整体传递。
        internal GroundRuleSelection(GroundCandidateRule rule, HashSet<string> states, string label, bool skip = false, bool relaxed = false)
        { Rule = rule; this.states = states; Label = label; Skip = skip; Relaxed = relaxed; }

        // 核对候选是否位于该规则输出的绑定库状态集合。
        internal bool Allows(CharacterMotionTrack track) => !Skip && (states == null || states.Contains(track.States[0]));

        // 保留特征和全部硬约束，仅解除当前表行的组范围。
        internal GroundRuleSelection WithoutGroups() => new GroundRuleSelection(Rule, null, Label + " · 回退到绑定库", relaxed: true);
    }

    // 装配时解析组名并诊断重叠与非法范围，查询时只读纯托管规则。
    internal sealed class GroundCandidatePolicy
    {
        private readonly List<GroundRuleSelection> rows = new List<GroundRuleSelection>();
        private readonly GroundRuleFallback fallback;
        internal readonly string Issue;

        // 将当前库的组名解析到发布状态，无法解析时按显式回退处理。
        internal GroundCandidatePolicy(GroundCandidateTable table, CharacterMotionDatabase database)
        {
            if (!table) return;
            fallback = table.NoMatch;
            if (!Enum.IsDefined(typeof(GroundRuleFallback), fallback)) { Issue = "候选表无匹配回退无效"; return; }
            var groups = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var g in database && database.Groups ? database.Groups.Groups ?? Array.Empty<GroundAnimationGroup>() : Array.Empty<GroundAnimationGroup>())
            {
                if (g == null || string.IsNullOrWhiteSpace(g.Name)) continue;
                var states = new HashSet<string>(StringComparer.Ordinal);
                foreach (var t in g.Variants ?? Array.Empty<CharacterMotionTrack>())
                    foreach (var s in t?.States ?? Array.Empty<string>()) states.Add(s);
                groups[g.Name] = states;
            }
            foreach (var source in table.Rules ?? Array.Empty<GroundCandidateRule>())
            {
                if (source == null || string.IsNullOrWhiteSpace(source.Name) || source.Use < 0 || source.Use > GroundMotionUse.Transition ||
                    !Enum.IsDefined(typeof(GroundTrackGait), source.Gait) || !Enum.IsDefined(typeof(GroundTrackSide), source.Support) ||
                    !Enum.IsDefined(typeof(GroundRuleGate), source.MoveApproved) || !Enum.IsDefined(typeof(GroundRuleGate), source.Returning) ||
                    !Enum.IsDefined(typeof(GroundRuleFallback), source.Empty) || (source.Phases & ~GroundRulePhases.All) != 0 ||
                    (source.Features & ~GroundFeatureGroups.All) != 0 || !(source.Speed.x >= 0f && source.Speed.y >= source.Speed.x) ||
                    !(source.SignedAngle.x >= -180f && source.SignedAngle.y <= 180f && source.SignedAngle.y >= source.SignedAngle.x) || source.Phases == 0)
                { Issue = "候选表名称、速度、阶段或角度范围无效"; return; }
                var rule = source.Copy();
                HashSet<string> states = null;
                foreach (var name in source.Groups ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(name) || !groups.TryGetValue(name, out var group))
                    { Issue = $"规则 {rule.Name} 引用了当前库外的组 {name}"; return; }
                    (states ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(group);
                }
                foreach (var prior in rows)
                    if (rule.Overlaps(prior.Rule)) { Issue = $"规则 {rule.Name} 与 {prior.Label} 同优先级重叠"; return; }
                rows.Add(new GroundRuleSelection(rule, states, rule.Name));
            }
            rows.Sort((a, b) => b.Rule.Priority.CompareTo(a.Rule.Priority));
        }

        // 选择首个命中优先级，未命中或表有错误时输出配置指定的回退。
        internal GroundRuleSelection Select(in GroundPoseQuery query, in CharacterBehaviorFrameResult behavior,
            CharacterLocomotionPhase phase, GroundTrackSide support, bool returning)
        {
            if (Issue == null)
                foreach (var row in rows)
                    if (row.Rule.Matches(query, behavior, phase, support, returning)) return row;
            bool skip = fallback == GroundRuleFallback.BehaviorRules;
            return new GroundRuleSelection(null, null, Issue ?? (skip ? "无匹配规则，交回行为规则" : "无匹配规则，使用绑定库"), skip);
        }
    }
}
