using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 声明可复用的地面匹配规则和评分配置。
    [CreateAssetMenu(menuName = "3C/动画模块/地面动作匹配", fileName = "地面动作匹配")]
    public sealed class MotionMatchModule : AnimationModule
    {
#if UNITY_EDITOR
        // 标记已装配的角色搜索范围，使编辑器修改在下一批次重新生效。
        private void OnValidate() => GroundSearchLibraries.InvalidateBindings();
#endif
        [SerializeField, InspectorName("匹配模式")] private CharacterMatchMode motionMatching;
        [InspectorName("续播代价宽限"), SerializeField, Min(0f), Tooltip("续播相对新入点的代价宽限，越大越不易换片。")]
        private float matchSwitchMargin = 0.12f;
        [InspectorName("反向续播上限（秒）"), SerializeField, Range(0f, 0.15f), Tooltip("反向输入后允许原动作继续的最长表现时间（秒）。")]
        private float matchReverseHold = 0.08f;
        [InspectorName("改向选姿夹角（度）"), SerializeField, Range(15f, 180f), Tooltip("持续移动中触发选姿的输入与速度改向角（度）。")]
        private float matchTurnAngle = 55f;
        [SerializeField, InspectorName("候选条件表")] internal GroundCandidateTable candidates;

        [SerializeField, InspectorName("匹配特征与用途权重")] private GroundMatchTuning costs = new GroundMatchTuning();
        [SerializeField, InspectorName("循环步频倍率范围")] private GroundPaceLimits paceLimits = new GroundPaceLimits();

        // 判断当前模式是否启用地面匹配评分。
        internal bool MatchingEnabled => motionMatching == CharacterMatchMode.Apply;

        // 将地面匹配模块登记到角色装配。
        internal override void Include(AnimationModuleAssembly assembly) => assembly.Add(this);

        // 将显式搜索库、候选条件及评分参数写入本次角色装配。
        internal void Apply(CharacterAnimationConfiguration config)
        {
            var database = config.PlaybackLibrary;
            config.SearchLibraries = new GroundSearchLibraries(database, config.GroundBindings);
            config.CandidatePolicy = GroundCandidatePolicy.Bind(candidates, config.SearchLibraries, config.Decisions);
            string issue = config.SearchLibraries.BindingIssue ?? config.CandidatePolicy.Issue;
            if (issue != null) throw new GroundBindingException(issue);
            config.FeatureLayout = config.SearchLibraries.Layout;
            config.MatchCosts = costs?.Snapshot() ?? new GroundMatchTuning();
            config.PaceLimits = paceLimits;
            config.MatchMode = motionMatching;
            config.MatchMargin = matchSwitchMargin;
            config.MatchReverseHold = matchReverseHold;
            config.MatchTurnAngle = matchTurnAngle;
        }
    }
}
