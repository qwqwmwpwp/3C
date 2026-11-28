#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;

namespace ThreeC.CharacterBehavior
{
    // 保存一次匹配的急转判定、实际扫描范围和逐段结果，供编辑器历史复用。
    internal sealed class GroundMatchQueryTrace
    {
        private readonly List<string> attempts = new List<string>();
        private readonly List<int> scanned = new List<int>();
        private IReadOnlyList<string> libraryNames;
        private string inputContext, inputDecision;

        internal bool IsInputTurn => inputContext != null;
        internal bool IsFallback { get; set; }
        internal string SearchedLibraries { get; private set; }
        internal string InputSummary => IsInputTurn ? $"{inputContext} · {inputDecision}" : null;
        internal int AttemptCount => attempts.Count;
        internal bool ContinuingIsReference { get; set; }
        internal string ContinuingUnavailableReason { get; set; }
        internal string ComparisonNote { get; set; }
        internal GroundMatchScoring ContinuingScoring { get; set; }
        internal GroundMatchScoring ChosenScoring { get; set; }

        // 清空本次试算，以最终重选的结果替换同批次旧诊断。
        internal void Reset()
        {
            attempts.Clear();
            scanned.Clear();
            libraryNames = null;
            inputContext = inputDecision = SearchedLibraries = null;
            IsFallback = ContinuingIsReference = false;
            ContinuingUnavailableReason = ComparisonNote = null;
            ChosenScoring = ContinuingScoring = GroundMatchScoring.None;
        }

        // 复制已提交的诊断，已格式化的尝试在窗口和历史中可直接复用。
        internal void CopyFrom(GroundMatchQueryTrace source)
        {
            Reset();
            attempts.AddRange(source.attempts);
            inputContext = source.inputContext;
            inputDecision = source.inputDecision;
            SearchedLibraries = source.SearchedLibraries;
            IsFallback = source.IsFallback;
            ContinuingIsReference = source.ContinuingIsReference;
            ContinuingUnavailableReason = source.ContinuingUnavailableReason;
            ComparisonNote = source.ComparisonNote;
            ContinuingScoring = source.ContinuingScoring;
            ChosenScoring = source.ChosenScoring;
        }

        // 绑定主线程已冻结的库名，以便在后台记录实际扫描的库。
        internal void SetLibraries(IReadOnlyList<string> names) => libraryNames = names;

        // 保存急转边沿的步号、入场条件和本次选姿上下文中的活动状态。
        internal void BeginInput(GroundInputTurn input, long handledInputStep, GroundTrackSide support, AnimationStateSet states)
        {
            if (input == null || input.Step == 0 || input.Step == handledInputStep) return;
            var query = input.Query;
            string side = support == GroundTrackSide.Left ? "左脚" : support == GroundTrackSide.Right ? "右脚" : "未判定单侧";
            inputContext = $"固定步 {input.Step} · 转角 {query.Angle:+0.0;-0.0;0.0}° · 入场 {query.EntrySpeed:F2} m/s · " +
                $"步态 {query.Target} · 承重 {side} · 活动状态 {states}";
        }

        // 记录候选表采纳或本轮未处理急转的具体原因。
        internal void FinishInput(bool accepted, string reason)
        {
            if (!IsInputTurn || inputDecision != null) return;
            inputDecision = accepted ? $"候选表采纳 · {reason}" : $"未采纳 · {reason}";
        }

        // 记录本段范围中装配时就缺失或无效的声明库。
        internal void BeginScope(in GroundRuleSelection selection)
        {
            IsFallback |= selection.Relaxed || selection.BoundLibraryFallback;
            if (selection.Scope == null) return;
            foreach (var declaration in selection.Scope.Declarations)
                if (declaration.Issue != null)
                    attempts.Add($"{ScopeLabel(selection)} · {declaration.Name} · 声明无效 · {declaration.Issue}");
            if (selection.Relaxed && selection.Scope.RelaxedUnionIssue is string issue)
                RejectScope(selection, $"放宽后的库不兼容，按库顺序分别评分 · {issue}");
        }

        // 只有实际开始下一库的评分前置检查时才记录顺序回退。
        internal void BeginOrderedSource(in GroundRuleSelection selection, int position, int index)
        {
            var first = selection.Scope.Declarations[0];
            if (position > 0 || first.Index != index || first.Issue != null) IsFallback = true;
        }

        // 记录组范围前置过滤，或载入真实评分输入时遇到的不兼容。
        internal void RejectSource(in GroundRuleSelection selection, int index, string reason, int channel = -1)
        {
            string detail = channel >= 0 ? $"{reason} {channel}" : reason;
            attempts.Add($"{ScopeLabel(selection)} · {Name(index)} · {detail}");
        }

        // 记录范围级阻断，阻断阶段没有启动候选扫描。
        internal void RejectScope(in GroundRuleSelection selection, string reason, bool listLibraries = true)
        {
            string libraries = listLibraries ? selection.Scope?.DeclaredLibraries : null;
            attempts.Add(string.IsNullOrEmpty(libraries) ? $"{ScopeLabel(selection)} · {reason}" :
                $"{ScopeLabel(selection)} · {libraries} · {reason}");
        }

        // 记录联合范围内因当前组范围为空而不会进入候选评分的库。
        internal void RejectEmptyUnionSources(in GroundRuleSelection selection)
        {
            foreach (int index in selection.Scope.Indices)
                if (!selection.Scope.AllowsLibrary(index, selection.Relaxed))
                    RejectSource(selection, index, "前置不合格 · 严格范围无候选组");
        }

        // 当前序库已有可用入点时，标记顺序中其余库未尝试。
        internal void SkipRemainingSources(in GroundRuleSelection selection, int start)
        {
            for (int i = start; i < selection.Count; i++)
                RejectSource(selection, selection.Scope.Indices[i], "按序未尝试 · 前序库已有获胜入点");
        }

        // 固定一次扫描的候选、评分配置和计数，重试时使用独立排名。
        internal void RecordScan(CharacterMatchDebugFrame debug, in GroundRuleSelection selection,
            in CharacterMatchResult best, CharacterMotionTrack winner, bool historyRetry)
        {
            IsFallback |= debug.HistoryFallback;
            string history = historyRetry ? "移除前史后重试" : debug.HistoryFallback ? "沿用不含前史的评分范围" : "保留前史";
            string scoring = debug.Scoring == GroundMatchScoring.Schema ? "特征布局评分" : "兼容脚姿评分";
            var description = new StringBuilder().Append(ScopeLabel(selection)).Append(" · ").Append(history)
                .Append(" · ").Append(scoring).Append(" · ").Append(winner == null ? "无获胜入点" : $"获胜 {winner.LibraryLabel} · {best.Binding.StateName} · {best.Time:F3}s");
            foreach (int index in selection.Scope.Indices)
            {
                if (!selection.InSearchedLibrary(index) || !selection.Scope.AllowsLibrary(index, selection.Relaxed)) continue;
                string name = Name(index);
                if (!scanned.Contains(index))
                {
                    scanned.Add(index);
                    SearchedLibraries = SearchedLibraries == null ? name : $"{SearchedLibraries} → {name}";
                }
                var counts = debug.ReadLibrary(index, out _);
                string state = winner?.LibraryIndex == index ? "获胜" : counts.Evaluated > 0 ? "已评分" :
                    counts.History > 0 ? "已扫描，前史过滤后零可评分入点" : "已扫描，零合法入点";
                description.AppendLine().Append($"{name} · {state} · 评分 {counts.Evaluated} · 前史不足 {counts.History} · 组 {counts.Group} · ")
                    .Append($"用途 {counts.Use} · 步态 {counts.Gait} · 资源 {counts.Resource} · 速度 {counts.Speed} · 承重 {counts.Support} · 入点窗 {counts.Window}");
            }
            for (int i = 0; i < debug.TopCount; i++)
            {
                var entry = debug.Top[i];
                description.AppendLine().Append($"第 {i + 1} 名 · {entry.Library} · {entry.State} · {entry.Time:F3}s · {entry.Cost.Total:F3}");
            }
            attempts.Add(description.ToString());
        }

        // 读取一段实际执行或明确跳过的查询经过。
        internal string DescribeAttempt(int index) => attempts[index];

        // 用真实装配库名显示扫描来源，缺少库名时保留可核对的索引。
        private string Name(int index) => libraryNames != null && (uint)index < libraryNames.Count
            ? libraryNames[index] : $"库 {index + 1}";

        // 标记当前使用的库顺序和组范围，包含放宽后切为顺序的情况。
        private static string ScopeLabel(in GroundRuleSelection selection) =>
            $"{(selection.Relaxed ? "放宽组范围" : "严格组范围")} · {(selection.Ordered ? "按序试库" : "联合评分")}";
    }
}
#endif
