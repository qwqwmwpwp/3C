using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 安装地面动作库、用途权重和步态调速配置。
    [CreateAssetMenu(menuName = "3C/动画模块/地面动作匹配", fileName = "地面动作匹配")]
    public sealed class MotionMatchModule : AnimationModule
    {
        [SerializeField, InspectorName("匹配模式")] private CharacterMatchMode motionMatching;
        [InspectorName("续播代价宽限"), SerializeField, Min(0f), Tooltip("续播相对新入点的代价宽限，越大越不易换片。")]
        private float matchSwitchMargin = 0.12f;
        [InspectorName("反向续播上限（秒）"), SerializeField, Range(0f, 0.15f), Tooltip("反向输入后允许原动作继续的最长表现时间（秒）。")]
        private float matchReverseHold = 0.08f;
        [InspectorName("改向选姿夹角（度）"), SerializeField, Range(15f, 180f), Tooltip("持续移动中触发选姿的输入与速度改向角（度）。")]
        private float matchTurnAngle = 55f;
        [SerializeField, InspectorName("候选条件表")] internal GroundCandidateTable candidates;
        [SerializeField, InspectorName("地面动作库")] private CharacterMotionDatabase database;

        [SerializeField, InspectorName("匹配特征与用途权重")] private GroundMatchTuning costs = new GroundMatchTuning();
        [SerializeField, InspectorName("循环步频倍率范围")] private GroundPaceLimits paceLimits = new GroundPaceLimits();

        internal override void Include(AnimationModuleAssembly assembly) => assembly.Add(this);

        // 将匹配配置写入本次角色装配。
        internal void Apply(CharacterAnimationConfiguration config)
        {
            config.CandidatePolicy = new GroundCandidatePolicy(candidates, database);
            config.FeatureLayout = database && database.Groups ? new GroundFeatureLayout(database.Groups.Schema, database.BakedTracks) : null;
            config.MatchCosts = costs ?? new GroundMatchTuning();
            config.PaceLimits = paceLimits;
            config.MatchDatabase = database;
            config.MatchMode = motionMatching;
            config.MatchMargin = matchSwitchMargin;
            config.MatchReverseHold = matchReverseHold;
            config.MatchTurnAngle = matchTurnAngle;
        }
    }
}
