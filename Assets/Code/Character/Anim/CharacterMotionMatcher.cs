using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal enum CharacterMatchChoice { Rules, Continue, Stop, Restart }

    // 保存选中的播放方式、入点和比较代价。
    internal struct CharacterMatchResult
    {
        public CharacterMatchChoice Choice;
        public CharacterAnimationBinding Binding;
        public float Time, Length, Cost, Rate;
        public GroundMatchCost Components;
        internal bool RejectPreviousStop;
        public bool HasEntry => Choice == CharacterMatchChoice.Stop || Choice == CharacterMatchChoice.Restart;
    }

    // 根据当前脚姿和预测轨迹搜索烘焙的地面片段，并保存续播历史。
    internal sealed class CharacterMotionMatcher
    {
        private readonly Animator animator;
        private readonly CharacterAnimationResourceCatalog resources;
        private readonly float margin, maxHold, walkLimit, sprintEntry;
        private readonly GroundMatchTuning tuning;
        private readonly GroundFeatureLayout layout;
        private readonly GroundCandidatePolicy policy;
        private readonly string[] libraryNames;
        private readonly string libraryIssues;
        private readonly GroundSearchDecisions decisions;
        private readonly GroundLibraryObservations observations;
        internal GroundFeatureQuery[] Features { set => observations.Features = value; }
        internal CharacterPresentationInput Input { set => observations.Input = value; }
        internal Vector2 Windows => layout?.Active == true ? layout.Windows : tuning.Windows;
        private readonly GroundPoseSearch search;
        private readonly Dictionary<int, CharacterMotionTrack> states = new Dictionary<int, CharacterMotionTrack>();

        private CharacterMatchMode mode;
        private GroundMatchHistory history = new GroundMatchHistory { PreviewVersion = -1 };
        private GroundMatchHistory beforeSelection;
        private string compatibilityError;
        // 切换匹配方式时清除历史，旧序列化值沿用原规则。
        public CharacterMatchMode Mode
        {
            get => mode;
            set
            {
                if (value != CharacterMatchMode.Apply) value = CharacterMatchMode.Rules;
                if (value == mode) return;
                Reset();
                mode = value;
            }
        }

        // 绑定候选策略并按当前烘焙库建立可搜索的状态与轨道索引。
        public CharacterMotionMatcher(Animator animator, CharacterAnimationResourceCatalog resources,
            CharacterAnimationConfiguration config)
        {
            this.animator = animator;
            this.resources = resources;
            decisions = new GroundSearchDecisions(config.Decisions);
            tuning = config.MatchCosts;
            layout = config.FeatureLayout;
            var libraries = config.SearchLibraries;
            policy = config.CandidatePolicy ?? GroundCandidatePolicy.Bind(null, libraries, config.Decisions);
            search = new GroundPoseSearch(resources);
            walkLimit = Mathf.Max(0.01f, config.RunEnterSpeed);
            sprintEntry = Mathf.Max(walkLimit, config.SprintEnterSpeed);
            observations = new GroundLibraryObservations(libraries, tuning.Windows);
            libraryNames = libraries?.Names;
            libraryIssues = libraries?.Issue;
            margin = InputMath.NonNegative(config.MatchMargin, 0.12f);
            maxHold = InputMath.NonNegative(config.MatchReverseHold, 0.08f);
            if (libraries == null) return;
            foreach (var entry in libraries.Tracks)
            {
                search.Add(entry.Frozen, entry.Bind(config, resources));
                foreach (int hash in entry.Hashes) states[hash] = entry.Frozen;
            }
            search.BuildIndex();
        }

        // 在主线程核对匹配模块，记录影响规划可用性的配置问题。
        public void CaptureCompatibility()
        {
#if UNITY_EDITOR
            search.DebugFrame = CharacterMatchDebugCapture.Begin(animator);
#endif
            string issue = mode == CharacterMatchMode.Rules ? null : resources.ValidateMatchLibrary();
            if (issue != null && issue != compatibilityError) Debug.LogWarning(issue, animator);
            compatibilityError = issue;
        }

        // 保存本帧候选试算前的状态，供重新规划恢复。
        internal void SaveSelectionHistory()
        {
            beforeSelection = history;
        }

        // 恢复候选试算前的状态，避免同一帧推进匹配历史两次。
        internal void RestoreSelectionHistory()
        {
            history = beforeSelection;
        }

        // 清除待用入点与上次匹配的步态、转向和续播历史。
        public void Reset()
        {
            history = beforeSelection = new GroundMatchHistory { PreviewVersion = -1 };
        }

        // 为本轮选姿准备交还候选，实际归属、步态和承重脚均吻合时才允许使用。
        internal void PrepareReturn(CharacterMovementForecast forecast, double now, CharacterAnimationDemand owner,
            bool lowerCoversFeet, bool matchRecovery, CharacterAnimationMotion motion, CharacterFootObservation feet,
            in CharacterBehaviorFrameResult behavior, CharacterLocomotionPhase phase)
        {
            policy.ClearTrace();
            if (forecast == null || !forecast.Available || mode != CharacterMatchMode.Apply || compatibilityError != null ||
                (lowerCoversFeet && !matchRecovery) || !motion.IsValid || motion.IsBlocked || !feet.ReadyForMatch)
            {
                history.PreviewTrack = null;
                history.PreviewVersion = -1;
                return;
            }
            if (history.PreviewVersion != forecast.Version || now > history.PreviewTime + 0.12 || feet.Support != history.PreviewSupport)
                history.PreviewTrack = null;
            if (owner.IsLocomotion || !forecast.TrySample(now, 0f, out var origin) || // 已获准的实际交还直接在 Evaluate 查询。
                !origin.AnticipatesMovementReturn ||
                history.PreviewTrack != null || !forecast.TryReturnToMovement(now, out float ahead, out var next) ||
                !observations.TryAnyGoal(ahead, out var goal)) return;
            history.PreviewGait = next.Gait == CharacterMoveGait.Walk ? CharacterAnimationIntentConverter.WalkKey :
                next.Gait == CharacterMoveGait.Sprint ? CharacterAnimationIntentConverter.SprintKey : CharacterAnimationIntentConverter.MoveKey;
            double returnTime = now + ahead;
            if (history.PreviewVersion == forecast.Version && history.PreviewSupport == feet.Support && Mathf.Abs((float)(history.PreviewTime - returnTime)) < 0.0001f)
                return;
            float speed = Vector3.ProjectOnPlane(next.Velocity, Vector3.up).magnitude;
            var query = new GroundPoseQuery(GroundMotionUse.Cycle, GroundPoseQuery.GaitOf(history.PreviewGait), speed, goal.FarYaw);
            query = new GroundPoseQuery(query, policy.Select(query, behavior, phase, feet.Support, true));
            if (query.Selection.Skip) return;
            var frame = new GroundSearchFrame(feet, goal, tuning.Profile(query.Use), speed, speed, default, resources.PlaybackClockScale, observations, query.Selection.Features, ahead);
            history.Preview = Find(ref query, ref frame, out history.PreviewTrack, false);
            history.PreviewRule = query.Selection.Rule;
            history.PreviewVersion = forecast.Version;
            history.PreviewSupport = feet.Support;
            history.PreviewTime = returnTime;
        }

        // 在当前身体归属和地面状态下按搜索决策选择续播或匹配入点。
        public CharacterMatchResult Evaluate(CharacterAnimationIntentConverter intent, CharacterAnimationDemand owner, bool lowerCoversFeet, bool matchRecovery,
            in CharacterPlaybackObservation seen, in CharacterAnimationPlaybackPlan last,
            CharacterAnimationMotion motion, CharacterFootObservation feet, float dt, in CharacterBehaviorFrameResult behavior)
        {
#if UNITY_EDITOR
            var debug = search.DebugFrame;
            CharacterMotionMatchDiagnostics.Begin(debug, libraryNames, libraryIssues, intent.InputTurn, history.HandledInputTurn,
                feet.Support, behavior.ActiveBehaviors);
            policy.ClearTrace();
            decisions.ClearTrace();
#endif
            if (mode == CharacterMatchMode.Rules) return Fallback("已配置常规规则", isFallback: false);
            bool stopping = intent.Phase == CharacterLocomotionPhase.BrakeLeadIn;
            bool recovering = lowerCoversFeet && matchRecovery && !stopping && intent.Phase != CharacterLocomotionPhase.Stopping;
            bool reverse = !recovering && intent.ReverseReleaseStep != 0 && history.HandledReverse != intent.ReverseReleaseStep;
            bool turn = !recovering && intent.DirectTurnStep != 0 && history.HandledTurn != intent.DirectTurnStep;
            history.ObserveDirectTurn(intent.DirectTurnStep);
            bool idleCrossing = reverse && owner.Key == CharacterAnimationIntentConverter.IdleKey;
            bool baselineUnavailable = !owner.IsLocomotion && !idleCrossing;
            if (lowerCoversFeet || baselineUnavailable)
            {
                history.HandledInputTurn = intent.InputTurn?.Step ?? 0;
                history.HandledTurn = intent.DirectTurnStep;
                history.HandledReverse = intent.ReverseReleaseStep;
            }
            if ((lowerCoversFeet && !recovering) || baselineUnavailable)
            {
                history.Playing = null;
                history.BaseInstance = 0;
                return Fallback("下身或基础动作已接管");
            }
            if (!motion.Trajectory.Match.Valid && observations.TryAnyGoal(null, out var alternative))
                motion = new CharacterAnimationMotion(motion, motion.Trajectory.WithForecast(motion.Trajectory.FutureVelocity, alternative));
            if (compatibilityError != null || !motion.IsValid || !motion.Trajectory.Match.Valid ||
                motion.IsBlocked || motion.Trajectory.IsBrakeObstructed || (motion.CollisionFlags & CollisionFlags.Below) == 0)
            {
                history.Playing = null;
                history.HandledTurn = intent.DirectTurnStep;
                history.HandledReverse = intent.ReverseReleaseStep;
                history.BaseInstance = 0;
                return Fallback(compatibilityError ?? (!motion.IsValid || !motion.Trajectory.Match.Valid ? "运动观测或预测轨迹无效" : "受阻或未着地"));
            }
            if (!feet.ReadyForMatch || !seen.Full.Ready)
            {
                history.Playing = null;
                history.BaseInstance = 0;
                return Fallback(!feet.ReadyForMatch ? "缺少连续有效的双脚观测" : "播放观测尚未就绪");
            }
            if (!reverse) history.Hold = 0f;
            if (intent.Phase == CharacterLocomotionPhase.Stopping)
            {
                CharacterMatchResult keep = default;
                if (history.StopTrack != null)
                {
                    var allowed = policy.Select(history.EntryQuery, behavior, history.EntryPhase, history.EntrySupport, false);
                    keep.RejectPreviousStop = allowed.Rule != history.EntryQuery.Selection.Rule ||
                        !(history.EntryQuery.Selection.Relaxed ? allowed.WithoutGroups() : allowed).Allows(history.StopTrack);
                    if (keep.RejectPreviousStop) history.StopTrack = null;
                }
#if UNITY_EDITOR
                CharacterMotionMatchDiagnostics.Fallback(debug, keep.RejectPreviousStop ? "原收步候选已离开当前规则范围" : "沿用制动时钟",
                    keep.RejectPreviousStop);
#endif
                return keep;
            }
            var current = seen.Full.Destination;
            bool hasCurrent = states.TryGetValue(current.fullPathHash, out var source) &&
                current.fullPathHash == last.FullBody.StateHash && resources.HasGroundTrack(current.fullPathHash, source);
            var input = intent.InputTurn;
            long inputStep = !recovering && !stopping ? input?.Step ?? 0 : 0;
            bool checkInput = inputStep != 0 && history.HandledInputTurn != inputStep;
            GroundPoseQuery query = default;
            bool inputTurn = checkInput && policy.TrySelectInputTurn(input.Query, behavior, intent.Phase, feet.Support, out query);
#if UNITY_EDITOR
            if (checkInput) debug?.Trace.FinishInput(inputTurn, policy.InputTurnDecision);
            else if (recovering || stopping) debug?.Trace.FinishInput(false, recovering ? "下身动作正交还地面移动" : "已进入前置制动");
#endif
            // 候选表的拒绝和接收都随本次选姿历史一起提交或撤回。
            if (checkInput && !inputTurn) history.HandledInputTurn = inputStep;
            turn |= inputTurn;
            if (!inputTurn)
            {
                if (stopping)
                    query = new GroundPoseQuery(GroundMotionUse.Stop, GroundPoseQuery.GaitOf(intent.StopGait), intent.StopEntryVelocity.magnitude);
                else if (turn || reverse)
                    query = new GroundPoseQuery(GroundMotionUse.Turn, GroundPoseQuery.GaitOf(turn ? intent.DirectTurnGait : intent.ReverseGait),
                        turn ? intent.DirectTurnSpeed : intent.ReverseSpeed, turn ? intent.DirectTurnAngle : intent.ReverseAngle);
                else
                {
                    bool starting = !recovering && intent.Phase == CharacterLocomotionPhase.Starting;
                    if (starting && history.Playing != null && !history.Playing.IsLoop && current.fullPathHash == history.PlayingHash &&
                        current.normalizedTime * history.Playing.Seconds >= history.Playing.ExitTime) starting = false;
                    query = GroundPoseQuery.Moving(starting, intent.RequestedGait, owner.Key, hasCurrent ? source : null, motion, walkLimit, sprintEntry);
                }
                query = new GroundPoseQuery(query, policy.Select(query, behavior, intent.Phase, feet.Support, recovering));
            }

#if UNITY_EDITOR
            CharacterMotionMatchDiagnostics.Describe(debug, query, libraryIssues);
#endif
            if (query.Selection.Skip && query.Selection.Rule != null)
                return SkipSearch(query, inputTurn ? inputStep : 0, intent.DirectTurnStep);
            history.SearchDelay = Mathf.Max(0f, history.SearchDelay - dt);
            bool unchanged = history.BaseInstance == owner.InstanceId && history.LastTarget == query.Target &&
                history.LastAlternate == query.Alternate && history.LastUse == query.Use && history.LastRule == query.Selection.Rule;
            bool moving = !stopping && !turn && !reverse;
            var strategy = decisions.BeforeSearch(!moving, unchanged, history.Playing != null, history.SearchDelay);
            if (strategy.Delay)
                return Fallback("地面搜索策略延后本次查询", isFallback: false);
            var frame = Frame(query, feet, motion);
            if (!inputTurn && history.Playing != null && strategy.AllowKeepPlaying &&
                frame.TryLibrary(history.Playing.LibraryIndex, out _, out _, out _) &&
                KeepPlaying(query, unchanged, seen.Full, last.FullBody, intent.RequestedGait,
                    history.InputDirected && input != null ? input.Direction : motion.ExpectedVelocity, behavior, recovering))
            {
#if UNITY_EDITOR
                if (debug != null)
                {
                    debug.Reason = "仍在续播窗口";
                    debug.Trace.ComparisonNote = "搜索前确认续播，本次没有新入点比较";
                    if (hasCurrent)
                    {
                        var continuingQuery = source.IsLoop ? query : history.EntryQuery;
                        var continuingFrame = Frame(continuingQuery, feet, motion);
                        if (!source.IsLoop && history.EntryWithoutPast) continuingFrame = continuingFrame.WithoutHistory();
                        debug.Trace.ContinuingIsReference = search.TryScoreCurrent(source, current, last, continuingFrame, continuingQuery, out _);
                    }
                    else CharacterMotionMatchDiagnostics.ContinuingTrackUnavailable(debug, source, current.fullPathHash, last.FullBody.StateHash);
                }
#endif
                return new CharacterMatchResult { Choice = CharacterMatchChoice.Continue };
            }

            if (query.Selection.Skip) return SkipSearch(query);
            history.Playing = null;
            history.SearchDelay = strategy.Interval;
            history.LastUse = query.Use;
            history.LastRule = query.Selection.Rule;
            history.LastTarget = query.Target;
            history.LastAlternate = query.Alternate;
            var best = Find(ref query, ref frame, out var winner, true);
            if ((query.Use == GroundMotionUse.Cycle || query.Use == GroundMotionUse.Transition) &&
                history.PreviewTrack != null && history.PreviewRule == query.Selection.Rule && query.Selection.Allows(history.PreviewTrack) &&
                query.Target == GroundPoseQuery.GaitOf(history.PreviewGait) &&
                resources.HasGroundTrack(history.Preview.Binding.StateHash, history.PreviewTrack) &&
                frame.TryLibrary(history.PreviewTrack.LibraryIndex, out var previewFrame, out _, out _))
            {
                int index = history.PreviewTrack.Index(history.Preview.Time / history.Preview.Length);
                var cost = previewFrame.Score(history.PreviewTrack, index, resources.Pace(history.Preview.Binding.StateHash), out var rate);
                cost.Entry = query.EntryPenalty(history.PreviewTrack);
                if (search.Filter(history.PreviewTrack, history.Preview.Binding.StateHash, query, feet.Support) == GroundMatchFilter.None &&
                    cost.Total < float.PositiveInfinity && cost.Total <= best.Cost + margin)
                {
                    best = history.Preview;
                    best.Components = cost;
                    best.Rate = rate;
                    best.Cost = cost.Total;
                    winner = history.PreviewTrack;
#if UNITY_EDITOR
                    if (debug != null)
                    {
                        debug.Reason = "预测交还入点仍在宽限内";
                        debug.Trace.ChosenScoring = previewFrame.Features.Active ? GroundMatchScoring.Schema : GroundMatchScoring.Compatibility;
                    }
#endif
                }
                history.PreviewTrack = null;
            }
            float keepCost = 0f;
            bool currentAllowed = hasCurrent && query.Selection.Allows(source);
#if UNITY_EDITOR
            if (debug != null && !currentAllowed)
            {
                if (hasCurrent) debug.Trace.ContinuingUnavailableReason = $"库 {source.LibraryLabel} 的轨道 {source.TrackLabel} 不在当前候选范围";
                else CharacterMotionMatchDiagnostics.ContinuingTrackUnavailable(debug, source, current.fullPathHash, last.FullBody.StateHash);
            }
#endif
            hasCurrent = currentAllowed;
            bool canScoreCurrent = hasCurrent && search.TryScoreCurrent(source, current, last, frame, query, out keepCost);
            bool canWait = stopping ? !intent.MainBrakeDue : turn ? history.TurnWait + dt < maxHold :
                !reverse || history.Hold + dt < maxHold;
            bool eligibleCycle = hasCurrent && source.IsLoop && search.Filter(source, current.fullPathHash, query, feet.Support) == GroundMatchFilter.None;
            bool sameCycle = moving && eligibleCycle && source == winner;
            bool canKeep = canScoreCurrent && last.FullBody.Demand.IsLocomotion && (!moving || eligibleCycle);
#if UNITY_EDITOR
            CharacterMotionMatchDiagnostics.DescribeComparison(debug, canScoreCurrent, canWait, canKeep, winner != null);
#endif
            if (decisions.KeepCurrent(canWait, canKeep, sameCycle, idleCrossing, winner != null, moving, unchanged, keepCost, best.Cost, margin))
            {
                best.Choice = CharacterMatchChoice.Continue;
#if UNITY_EDITOR
                if (debug != null) debug.Reason = sameCycle ? "同一循环保持相位" :
                    idleCrossing ? "反向过零保持原步" : "地面搜索策略保留当前片段";
#endif
            }
            if (turn)
            {
                history.TurnWait += dt;
                if (best.Choice != CharacterMatchChoice.Continue)
                    history.CompleteTurn(inputTurn ? inputStep : 0, intent.DirectTurnStep);
            }
            if (reverse && (best.Choice == CharacterMatchChoice.Continue || idleCrossing && canWait)) history.Hold += dt;
            else if (reverse) history.HandledReverse = intent.ReverseReleaseStep;
            if (owner.Key == CharacterAnimationIntentConverter.IdleKey && best.HasEntry)
            {
                best.Choice = CharacterMatchChoice.Rules;
#if UNITY_EDITOR
                CharacterMotionMatchDiagnostics.Fallback(debug, "输入已回到待机，未提交新入点", true);
#endif
            }
            if (!stopping && !idleCrossing) history.BaseInstance = owner.InstanceId;
            if (best.Choice == CharacterMatchChoice.Continue && hasCurrent && source.IsLoop && query.AllowsGait(source) &&
                moving)
            {
                history.Playing = source;
                history.PlayingHash = current.fullPathHash;
                history.ObservedPlaying = true;
            }
            if (best.HasEntry && winner != null)
            {
                history.StopTrack = winner.IsStop ? winner : null;
                if (winner.Use == GroundMotionUse.Turn)
                {
                    history.InputDirected = inputTurn;
                    history.TurnDirection = inputTurn ? input.Direction : motion.ExpectedVelocity.normalized;
                }
                history.EntryQuery = query;
                history.EntryPhase = intent.Phase;
                history.EntryInput = intent.RequestedGait;
                history.EntrySupport = feet.Support;
                history.EntryWithoutPast = !frame.AllowPast;
                history.Playing = winner.IsStop ? null : winner;
                history.PlayingHash = best.Binding.StateHash;
                history.ObservedPlaying = false;
            }
            return best;
        }

        // 入场和预搜共用条件表的空组回退，解除范围后仍保留基本合法条件。
        private CharacterMatchResult Find(ref GroundPoseQuery query, ref GroundSearchFrame frame, out CharacterMotionTrack winner, bool record)
        {
            var found = search.FindScope(ref query, ref frame, out winner, record);
            if (winner != null || !query.Selection.Relax) return found;
            query = new GroundPoseQuery(query, query.Selection.WithoutGroups());
            return search.FindScope(ref query, ref frame, out winner, record);
        }

        // 判断当前步态、转向或一次性过渡是否仍在允许的续播窗口。
        private bool KeepPlaying(in GroundPoseQuery query, bool unchanged, in CharacterLayerObservation seen,
            in CharacterAnimationLayerPlan last, CharacterMoveGait requested, Vector3 direction,
            in CharacterBehaviorFrameResult behavior, bool recovering)
        {
            var track = history.Playing;
            if (track == null || last.StateHash != history.PlayingHash || !last.Demand.IsLocomotion) return false;
            var actual = seen.Destination;
            bool selected = actual.fullPathHash == history.PlayingHash;
            if (selected) history.ObservedPlaying = true;
            bool pending = !history.ObservedPlaying && last.Command != CharacterAnimationPlaybackCommand.Keep;
            if (!selected && !pending) return false;
            if (track.Use == GroundMotionUse.Turn)
            {
                if (Vector3.Dot(history.TurnDirection, direction.normalized) < 0.7f) return false;
            }
            else if (!query.AllowsGait(track)) return false;
            if (track.IsLoop) return unchanged && search.Filter(track, history.PlayingHash, query, GroundTrackSide.Either) == GroundMatchFilter.None && history.SearchDelay > 0f;
            if (requested != history.EntryInput) return false;
            // 已进入的一次性动作保留入场用途与阶段，重新核对本帧实际授权和规则范围。
            var entry = history.EntryQuery;
            var selection = policy.Select(entry, behavior, history.EntryPhase, history.EntrySupport, recovering);
            if (entry.Selection.Rule != selection.Rule || selection.Skip ||
                !(entry.Selection.Relaxed ? selection.WithoutGroups() : selection).Allows(track)) return false;
            return selected && actual.normalizedTime * track.Seconds < track.ExitTime || pending;
        }

        // 固定查询用途及实速、起步和制动的预测输入。
        private GroundSearchFrame Frame(in GroundPoseQuery query, in CharacterFootObservation feet, in CharacterAnimationMotion motion) =>
            new GroundSearchFrame(feet, motion.Trajectory.Match, tuning.Profile(query.Use), motion.ActualSpeed,
                GroundPlaybackPace.StartSpeed(motion, resources.WalkSpeed), motion.Trajectory, resources.PlaybackClockScale,
                observations, query.Selection.Features, null
#if UNITY_EDITOR
                , search.DebugFrame != null
#endif
                );

        // 回退时清除匹配缓存，并结算当前已接受的输入急转。
        private CharacterMatchResult SkipSearch(in GroundPoseQuery query, long inputStep = 0, long directStep = 0)
        {
            if (inputStep != 0) history.CompleteTurn(inputStep, directStep);
            history.Playing = history.PreviewTrack = null;
            history.SearchDelay = 0f;
#if UNITY_EDITOR
            CharacterMotionMatchDiagnostics.SkipSearch(search.DebugFrame, query.Selection);
#endif
            return Fallback(query.Selection.Label);
        }

        // 返回常规规则，并仅在诊断开启时保留具体原因。
        private CharacterMatchResult Fallback(string reason, bool isFallback = true)
        {
#if UNITY_EDITOR
            CharacterMotionMatchDiagnostics.Fallback(search.DebugFrame, reason, isFallback);
#endif
            return default;
        }

#if UNITY_EDITOR
        // 将当前试选的候选范围和搜索表达式记录交给动画诊断。
        internal void AppendDecisionTrace(System.Text.StringBuilder trace)
        {
            policy.AppendTrace(trace);
            decisions.AppendTrace(trace);
        }

        // 根据实际已提交的播放计划发布本帧诊断，丢弃未被采用的试算结果。
        internal void Publish(in CharacterMatchResult result, in CharacterAnimationPlaybackPlan plan,
            in CharacterAnimationDebugSnapshot playback, double time, long motionStep)
        {
            CharacterMotionMatchDiagnostics.Publish(animator, search.DebugFrame, result, plan, playback, time, motionStep);
            search.DebugFrame = null;
        }
#endif
    }
}
