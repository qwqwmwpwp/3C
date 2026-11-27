namespace ThreeC.CharacterBehavior
{
    // 将地面搜索的时机与续播比较交给动画决策程序，保留匹配器的硬门槛。
    internal sealed class GroundSearchDecisions
    {
        private const float DefaultSearchInterval = 0.10f;
        private readonly AnimationDecisionEvaluator evaluator;

        // 为当前角色分配独立的决策求值工作区。
        internal GroundSearchDecisions(CompiledAnimationDecisions program) => evaluator = new AnimationDecisionEvaluator(program,
            AnimationDecisionStage.SearchBefore | AnimationDecisionStage.SearchAfter);

        // 读取搜索前的等待、续播许可和下一次周期查询间隔，特殊查询仍走匹配。
        internal (bool Delay, bool AllowKeepPlaying, float Interval) BeforeSearch(bool special, bool unchanged,
            bool hasTrackedPlayback, float searchDelay)
        {
            evaluator.Begin(AnimationDecisionStage.SearchBefore);
            evaluator.Set(AnimationDecisionInput.Special, special);
            evaluator.Set(AnimationDecisionInput.Unchanged, unchanged);
            evaluator.Set(AnimationDecisionInput.HasTrackedPlayback, hasTrackedPlayback);
            evaluator.Set(AnimationDecisionInput.SearchDelay, searchDelay);
            return (!special && evaluator.Flag(AnimationDecisionOutput.DelaySearch),
                !special && evaluator.Flag(AnimationDecisionOutput.AllowKeepPlaying),
                InputMath.NonNegative(evaluator.Number(AnimationDecisionOutput.SearchInterval), DefaultSearchInterval));
        }

        // 只在当前素材有效且续播窗口开放时，依据新旧入点的比较结果决定续播。
        internal bool KeepCurrent(bool canWait, bool canKeep, bool sameCycle, bool idleCrossing, bool hasWinner,
            bool moving, bool unchanged, float keepCost, float bestCost, float margin)
        {
            if (!canWait || !canKeep) return false;
            evaluator.Begin(AnimationDecisionStage.SearchAfter);
            evaluator.Set(AnimationDecisionInput.CanWait, canWait);
            evaluator.Set(AnimationDecisionInput.CanKeep, canKeep);
            evaluator.Set(AnimationDecisionInput.SameCycle, sameCycle);
            evaluator.Set(AnimationDecisionInput.IdleCrossing, idleCrossing);
            evaluator.Set(AnimationDecisionInput.HasWinner, hasWinner);
            evaluator.Set(AnimationDecisionInput.Moving, moving);
            evaluator.Set(AnimationDecisionInput.Unchanged, unchanged);
            evaluator.Set(AnimationDecisionInput.KeepCost, keepCost);
            evaluator.Set(AnimationDecisionInput.BestCost, bestCost);
            evaluator.Set(AnimationDecisionInput.Margin, margin);
            return evaluator.Flag(AnimationDecisionOutput.KeepCurrent);
        }

#if UNITY_EDITOR
        // 丢弃上次试选的表达式记录，供本次匹配重新采集。
        internal void ClearTrace() => evaluator.ClearTrace();

        // 追加本次匹配已求值的搜索决策与节点。
        internal void AppendTrace(System.Text.StringBuilder trace) => evaluator.AppendTrace(trace);
#endif
    }
}
