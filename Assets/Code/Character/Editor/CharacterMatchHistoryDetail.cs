using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 并列展示匹配查询、评分依据和同次提交后观测到的地面播放。
    internal static class CharacterMatchHistoryDetail
    {
        // 显示选库规则、候选代价、续播对照和最终在播的片段。
        internal static void Draw(CharacterMatchHistoryEntry entry)
        {
            var frame = entry.Frame;
            var trace = frame.Trace;
            EditorGUILayout.LabelField($"记录 #{entry.Sequence} · 移动步 {entry.MotionStep} · {entry.Time:F3}s", EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(entry.Boundary))
                EditorGUILayout.LabelField("连续性分界", entry.Boundary, EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrEmpty(trace.InputSummary))
                EditorGUILayout.LabelField("输入", trace.InputSummary, EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrEmpty(frame.Query))
                EditorGUILayout.LabelField("查询", frame.Query, EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrEmpty(frame.Rule))
                EditorGUILayout.LabelField("选库规则", frame.Rule, EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrEmpty(frame.Libraries))
                EditorGUILayout.LabelField("规则选库", frame.Libraries, EditorStyles.wordWrappedLabel);
            bool scanned = !string.IsNullOrEmpty(trace.SearchedLibraries);
            EditorGUILayout.LabelField("实际搜索库", scanned ? trace.SearchedLibraries : "未扫描", EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrEmpty(frame.CompatibilityIssue))
                EditorGUILayout.HelpBox(frame.CompatibilityIssue, MessageType.Warning);
            EditorGUILayout.LabelField("结果", $"{ChoiceLabel(frame.Choice)} · {frame.Reason}", EditorStyles.wordWrappedLabel);
            if (scanned) DrawScoring("末次扫描评分来源", frame.Scoring);
            if (frame.HistoryFallback) EditorGUILayout.HelpBox("末次扫描的入点评分排除了过去采样。", MessageType.Info);
            DrawDecisionScores(frame);
            if (!string.IsNullOrEmpty(trace.ComparisonNote))
                EditorGUILayout.LabelField("代价比较", trace.ComparisonNote, EditorStyles.wordWrappedLabel);
            DrawGroundPlayback(entry);
            if (scanned) DrawLastScan(frame);
            for (int i = 0; i < trace.AttemptCount; i++)
                EditorGUILayout.LabelField($"规则尝试 {i + 1}\n{trace.DescribeAttempt(i)}", EditorStyles.wordWrappedLabel);
        }

        // 显示获选与续播的真实评分、诊断参考性质及不可用原因。
        private static void DrawDecisionScores(CharacterMatchDebugFrame frame)
        {
            var trace = frame.Trace;
            bool continued = frame.Choice == nameof(CharacterMatchChoice.Continue);
            bool hasUnavailableReason = !string.IsNullOrEmpty(trace.ContinuingUnavailableReason);
            bool isReference = trace.ContinuingIsReference && !hasUnavailableReason &&
                trace.ContinuingScoring != GroundMatchScoring.None && !string.IsNullOrEmpty(frame.Continuing.State);
            bool hasChosen = !string.IsNullOrEmpty(frame.Chosen.State);
            if (hasChosen && trace.ChosenScoring != frame.Scoring)
                DrawScoring("获选评分来源", trace.ChosenScoring);
            if (!continued || hasChosen)
                DrawEntry(continued ? "获选续播" : "获选入点", frame.Chosen, trace.ChosenScoring, continued && isReference);
            if (hasUnavailableReason)
                EditorGUILayout.LabelField("续播代价缺失", trace.ContinuingUnavailableReason, EditorStyles.wordWrappedLabel);
            if (continued || (hasUnavailableReason && string.IsNullOrEmpty(frame.Continuing.State))) return;

            if (!string.IsNullOrEmpty(frame.Continuing.State) && trace.ContinuingScoring != frame.Scoring)
                DrawScoring("续播评分来源", trace.ContinuingScoring);
            DrawEntry("延续对照", frame.Continuing, trace.ContinuingScoring, isReference);
        }

        // 展示末次实际扫描的候选和总计，各库结果由各次规则尝试展示。
        private static void DrawLastScan(CharacterMatchDebugFrame frame)
        {
            for (int i = 0; i < frame.TopCount; i++)
                DrawEntry(i == 0 ? "最佳新入点" : $"新入点 {i + 1}", frame.Top[i], frame.Scoring);
            DrawCounts("末次扫描合计", frame.Counts);
        }

        // 将匹配器提交的播放选择显示为列表与详情共用的名称。
        internal static string ChoiceLabel(string choice) => choice switch
        {
            nameof(CharacterMatchChoice.Rules) => "规则播放",
            nameof(CharacterMatchChoice.Continue) => "续播",
            nameof(CharacterMatchChoice.Stop) => "匹配制动",
            nameof(CharacterMatchChoice.Restart) => "匹配新入点",
            _ => choice
        };

        // 显示同次基础混合的下发状态、各片段权重和下身贡献。
        private static void DrawGroundPlayback(CharacterMatchHistoryEntry entry)
        {
            var snapshot = entry.Playback;
            var mix = snapshot.Mix.Base;
            string command = snapshot.SentBaselinePlayback ? "本次下发新播放指令" : "本次未下发新播放指令";
            if (mix.Target == null)
            {
                EditorGUILayout.LabelField("最终地面播放", $"无在播片段 · {command}", EditorStyles.wordWrappedLabel);
                return;
            }
            float groundWeight = Mathf.Max(0f, 1f - snapshot.Mix.Coverage - snapshot.Mix.LowerWeight);
            string detail = $"{mix.Seconds:F3}s · {mix.Rate:F2}× · 地面混合权重 {mix.TargetWeight:P1} · " +
                $"最终下身贡献 {groundWeight * mix.TargetWeight:P1} · {command}";
            EditorGUILayout.LabelField("最终地面播放", detail, EditorStyles.wordWrappedLabel);
            DrawPlaybackSource("目标", mix.Target, entry.Library, entry.Track,
                mix.PrimaryName, mix.SecondaryName, mix.SourceWeights);
            if (mix.Outgoing != null)
            {
                EditorGUILayout.LabelField("地面淡出", $"地面混合权重 {mix.OutgoingWeight:P1} · " +
                    $"最终下身贡献 {groundWeight * mix.OutgoingWeight:P1}", EditorStyles.wordWrappedLabel);
                DrawPlaybackSource("淡出", mix.Outgoing, mix.OutgoingLibrary, mix.OutgoingTrack,
                    mix.OutgoingPrimaryName, mix.OutgoingSecondaryName, mix.OutgoingSourceWeights);
            }
            if (mix.OtherCount > 0)
                EditorGUILayout.LabelField("地面其他片段", $"{mix.OtherCount} 段 · 地面混合权重合计 {Mathf.Max(0f, 1f - mix.TargetWeight - mix.OutgoingWeight):P1}", EditorStyles.wordWrappedLabel);
        }

        // 分别显示实际片段的状态路径、地面绑定与本次求值冻结的素材内配比。
        private static void DrawPlaybackSource(string label, string state, string library, string track,
            string primary, string secondary, Vector2 weights)
        {
            EditorGUILayout.LabelField(label + "状态路径", state, EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField(label + "地面轨道", TrackLabel(library, track), EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrEmpty(secondary))
                EditorGUILayout.LabelField(label + "素材",
                    $"声部内权重 · {primary} {weights.x:P1} · {secondary} {weights.y:P1}", EditorStyles.wordWrappedLabel);
            else if (!string.IsNullOrEmpty(primary))
                EditorGUILayout.LabelField(label + "素材", primary, EditorStyles.wordWrappedLabel);
        }

        // 保留已知库名，并明确显示实际轨道或未绑定状态。
        internal static string TrackLabel(string library, string track)
        {
            string binding = string.IsNullOrEmpty(track) ? "未绑定地面轨道" : track;
            return string.IsNullOrEmpty(library) ? binding : $"{library} · {binding}";
        }

        // 显示末次扫描各类筛选原因的累计数量。
        private static void DrawCounts(string label, in GroundMatchDebugCounts counts) =>
            EditorGUILayout.LabelField(label, $"评分入点 {counts.Evaluated} · 前史不足 {counts.History} · 候选范围 {counts.Group} · " +
                $"用途 {counts.Use} · 步态 {counts.Gait} · 资源 {counts.Resource} · 速度 {counts.Speed} · " +
                $"承重 {counts.Support} · 时间窗 {counts.Window}", EditorStyles.wordWrappedLabel);

        // 指明当前查询使用的评分配置以及共用通道的归属。
        private static void DrawScoring(string label, GroundMatchScoring scoring)
        {
            string detail = scoring switch
            {
                GroundMatchScoring.Schema => "布局通道代价 = 误差平方 × Weight ÷ Scale²。接触使用当前用途的 Contact 权重与 ContactScale 尺度，步频使用当前用途的 Pace 权重与 PaceScale 尺度。共用项汇总同时命中多个启用组的通道，每个通道计一次。",
                GroundMatchScoring.Compatibility => "兼容脚姿与预测轨迹，使用地面匹配模块的用途权重和尺度。",
                _ => "当前帧未进行地面匹配评分。"
            };
            EditorGUILayout.LabelField(label, detail, EditorStyles.wordWrappedLabel);
        }

        // 展示候选的状态、入点、倍率和代价分项，并标识有效的诊断参考分数。
        private static void DrawEntry(string label, in CharacterMatchDebugEntry entry, GroundMatchScoring scoring, bool isReference = false)
        {
            if (string.IsNullOrEmpty(entry.State))
            {
                EditorGUILayout.LabelField(label, "无");
                return;
            }
            var cost = entry.Cost;
            string sample = $"{entry.State} · {entry.Time:F3}s · {entry.Rate:F2}×";
            if (!string.IsNullOrEmpty(entry.Library)) sample = $"{entry.Library} · {sample}";
            if (!string.IsNullOrEmpty(entry.Track)) sample += $" · {entry.Track}";
            if (scoring == GroundMatchScoring.None)
            {
                EditorGUILayout.LabelField(label, $"{sample} · 未评分", EditorStyles.wordWrappedLabel);
                return;
            }
            float total = cost.Total;
            if (cost.MissingHistory || float.IsNaN(total) || float.IsInfinity(total))
            {
                string reason = cost.MissingHistory ? "前史不足，代价不可比较" : "代价无效";
                EditorGUILayout.LabelField(label, $"{sample} · {reason}", EditorStyles.wordWrappedLabel);
                return;
            }
            string score = isReference ? "诊断参考代价" : "总分";
            EditorGUILayout.LabelField(label, $"{sample} · {score} {total:F3}", EditorStyles.wordWrappedLabel);
            string sharedCosts = $"接触 {cost.Contact:F3} · 步频 {cost.Pace:F3} · 入点 {cost.Entry:F3}";
            if (scoring == GroundMatchScoring.Schema)
            {
                var groups = cost.FeatureBreakdown;
                EditorGUILayout.LabelField($"布局 {cost.Features:F3}（脚部 {groups.Feet:F3} · 身体 {groups.Body:F3} · 手部 {groups.Hands:F3} · " +
                    $"轨迹 {groups.Trajectory:F3} · 共用 {groups.Shared:F3}） · {sharedCosts} · 通道位 0x{groups.Channels:X}", EditorStyles.wordWrappedLabel);
                return;
            }
            EditorGUILayout.LabelField($"脚位 {cost.Position:F3} · 脚速 {cost.Velocity:F3} · 骨盆 {cost.Pelvis:F3} · " +
                $"路径 {cost.Path:F3} · 朝向 {cost.Facing:F3} · {sharedCosts}", EditorStyles.wordWrappedLabel);
        }
    }
}
