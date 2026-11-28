#if UNITY_EDITOR
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 将匹配器的实际候选、续播和最终提交结论写入编辑器诊断。
    internal static class CharacterMotionMatchDiagnostics
    {
        // 记录本轮原始观察，在预测重选时替换同批次的初次试算。
        internal static void Begin(CharacterMatchDebugFrame debug, string[] libraryNames, string libraryIssues,
            GroundInputTurn input, long handledInputStep, GroundTrackSide support, AnimationStateSet states)
        {
            if (debug == null) return;
            debug.Reset();
            debug.SetLibraries(libraryNames);
            debug.CompatibilityIssue = libraryIssues;
            debug.Trace.SetLibraries(libraryNames);
            debug.Trace.BeginInput(input, handledInputStep, support, states);
        }

        // 保存候选表声明的库范围与用途，实际查询经过由扫描端记录。
        internal static void Describe(CharacterMatchDebugFrame debug, in GroundPoseQuery query, string libraryIssues)
        {
            if (debug == null) return;
            debug.Rule = query.Selection.Label + (query.Selection.Ordered ? " · 按序试库" : " · 联合评分");
            debug.Libraries = query.Selection.Scope?.DeclaredLibraries;
            debug.CompatibilityIssue = query.Selection.Scope?.Issue ?? libraryIssues;
            string turnLabel = query.Selection.IsInputTurn ? "输入急转" : "常规转向";
            debug.Query = $"{(query.Use == GroundMotionUse.Turn ? turnLabel : query.Use.ToString())} · {query.From} → {query.Target}";
        }

        // 记录候选表直接回退的声明问题和实际跳过的整段查询。
        internal static void SkipSearch(CharacterMatchDebugFrame debug, in GroundRuleSelection selection)
        {
            if (debug == null) return;
            debug.Trace.BeginScope(selection);
            var scope = selection.Scope;
            string reason = selection.Rule == null ? "候选表交回规则，未开始扫描" :
                scope?.UnionIssue is string mismatch ? $"前置不兼容 · {mismatch}" :
                scope?.StrictBlockIssue is string issue ? $"前置不合格 · {issue}" : "候选表交回规则，未开始扫描";
            debug.Trace.RejectScope(selection, reason);
        }

        // 记录回到行为规则的原因，并补齐前置门槛拦下的急转边沿解释。
        internal static void Fallback(CharacterMatchDebugFrame debug, string reason, bool isFallback)
        {
            if (debug == null) return;
            debug.Reason = reason;
            debug.Trace.IsFallback |= isFallback;
            debug.Trace.FinishInput(false, reason);
            if (debug.Trace.ContinuingScoring == GroundMatchScoring.None)
                debug.Trace.ContinuingUnavailableReason ??= reason;
        }

        // 使用当前轨道绑定检查的结果解释为何无法为播放中的姿势评分。
        internal static void ContinuingTrackUnavailable(CharacterMatchDebugFrame debug, CharacterMotionTrack source,
            int observedState, int committedState)
        {
            if (debug == null) return;
            debug.Trace.ContinuingUnavailableReason = source == null ? $"当前播放状态 {observedState} 无登记地面轨道" :
                observedState != committedState ? $"观测状态 {observedState} 与上次提交状态 {committedState} 不符" :
                $"库 {source.LibraryLabel} 的轨道 {source.TrackLabel} 与当前播放资源不符";
        }

        // 保留当前轨道的库不可评分时实际返回的前置原因与通道。
        internal static void ContinuingLibraryUnavailable(CharacterMatchDebugFrame debug, CharacterMotionTrack source, string reason, int channel)
        {
            if (debug == null) return;
            string detail = channel >= 0 ? $"{reason} {channel}" : reason;
            debug.Trace.ContinuingUnavailableReason = $"{source.LibraryLabel} · {detail}";
        }

        // 保存续播候选独立的评分来源和入点代价。
        internal static void SetContinuing(CharacterMatchDebugFrame debug, CharacterMotionTrack source, string state,
            in GroundSearchFrame frame, float time, float rate, in GroundMatchCost cost)
        {
            if (debug == null) return;
            debug.Trace.ContinuingScoring = frame.Features.Active ? GroundMatchScoring.Schema : GroundMatchScoring.Compatibility;
            debug.Trace.ContinuingUnavailableReason = cost.MissingHistory ? "当前相位缺少评分所需的前史样本" :
                float.IsNaN(cost.Total) || float.IsInfinity(cost.Total) ? "当前相位代价无效" : null;
            debug.Continuing = new CharacterMatchDebugEntry { State = state, Library = source.LibraryLabel, Track = source.TrackLabel,
                Time = time, Rate = rate, Cost = cost };
        }

        // 标记续播策略是否读取当前分数，并解释新入点或续播许可缺失时的比较结果。
        internal static void DescribeComparison(CharacterMatchDebugFrame debug, bool canScoreCurrent,
            bool canWait, bool canKeep, bool hasWinner)
        {
            if (debug == null) return;
            bool evaluatesKeep = canWait && canKeep;
            debug.Trace.ContinuingIsReference = canScoreCurrent && !evaluatesKeep;
            if (!hasWinner)
                debug.Trace.ComparisonNote = evaluatesKeep ? "没有合法新入点可比较，续播策略仍求值，可使用当前代价" :
                    "没有合法新入点可比较，本次未求值续播策略";
            else if (!evaluatesKeep)
                debug.Trace.ComparisonNote = !canWait ? "本次不允许等待，未比较当前相位与新入点" :
                    "当前播放不满足续播条件，未比较当前相位与新入点";
        }

        // 只在最终播放归属确认后发布选姿结论，并保留扫描和实际播放之间的差异。
        internal static void Publish(Animator animator, CharacterMatchDebugFrame debug, in CharacterMatchResult result,
            in CharacterAnimationPlaybackPlan plan, in CharacterAnimationDebugSnapshot playback, double time, long motionStep)
        {
            if (debug == null) return;
            var layer = plan.FullBody;
            if (result.HasEntry && layer.Reason == CharacterAnimationPlaybackReason.MatchPose && layer.StateHash == result.Binding.StateHash)
            {
                debug.Choice = result.Choice.ToString();
                debug.Reason ??= "最低代价有效入点";
                debug.Chosen = new CharacterMatchDebugEntry { State = layer.Binding.StateName, Library = layer.Binding.Library, Track = layer.Binding.Track,
                    Time = result.Time, Rate = result.Rate, Cost = result.Components };
                if (debug.Trace.ChosenScoring == GroundMatchScoring.None) debug.Trace.ChosenScoring = debug.Scoring;
            }
            else if (result.Choice == CharacterMatchChoice.Continue && layer.Reason == CharacterAnimationPlaybackReason.ContinueMatch)
            {
                debug.Choice = "Continue";
                debug.Reason ??= "等待当前片段进入播放观测";
                debug.Chosen = debug.Continuing;
                debug.Trace.ChosenScoring = debug.Trace.ContinuingScoring;
            }
            else
            {
                debug.Choice = "Rules";
                debug.Trace.ChosenScoring = GroundMatchScoring.None;
                if (result.HasEntry || result.Choice == CharacterMatchChoice.Continue)
                    Fallback(debug, "最终播放归属或制动规则已更新", true);
                else if (debug.Reason == null)
                    Fallback(debug, "无可用匹配入点", true);
            }
            CharacterMatchDebugCapture.Publish(animator, debug, playback, time, motionStep);
        }
    }
}
#endif
