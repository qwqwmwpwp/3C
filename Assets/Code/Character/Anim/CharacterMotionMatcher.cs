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
        public bool HasEntry => Choice == CharacterMatchChoice.Stop || Choice == CharacterMatchChoice.Restart;
    }

    // 根据当前脚姿和预测轨迹搜索烘焙的地面片段，并保存续播历史。
    internal sealed class CharacterMotionMatcher
    {
        private readonly Animator animator;
        private readonly CharacterAnimationResourceCatalog resources;
        private const float CycleSearchInterval = 0.10f;
        private readonly float margin, maxHold, walkLimit, sprintEntry;
        private readonly GroundMatchTuning tuning;
        private readonly GroundFeatureLayout layout;
        private readonly GroundCandidatePolicy policy;
        internal GroundFeatureQuery Features { private get; set; }
        internal Vector2 Windows => layout?.Active == true ? layout.Windows : tuning.Windows;
        private readonly GroundPoseSearch search;
        private readonly Dictionary<int, CharacterMotionTrack> states = new Dictionary<int, CharacterMotionTrack>();

        private CharacterMatchMode mode;
        // 保存匹配时延、当前播放与预测交还的候选快照。
        private struct MatchHistory
        {
            internal long HandledTurn, HandledReverse, BaseInstance, PendingTurn;
            internal float Hold, TurnWait, SearchDelay;
            internal GroundTrackGait LastTarget, LastAlternate;
            internal GroundMotionUse LastUse;
            internal GroundCandidateRule LastRule;
            internal GroundPoseQuery EntryQuery;
            internal CharacterLocomotionPhase EntryPhase;
            internal bool EntryWithoutPast;
            internal CharacterMoveGait EntryInput;
            internal GroundTrackSide EntrySupport;
            internal Vector3 TurnDirection;
            internal CharacterMotionTrack Playing, PreviewTrack;
            internal int PlayingHash;
            internal bool ObservedPlaying;
            internal long PreviewVersion;
            internal CharacterMatchResult Preview;
            internal string PreviewGait;
            internal GroundTrackSide PreviewSupport;
            internal double PreviewTime;
        }

        private MatchHistory history = new MatchHistory { PreviewVersion = -1 };
        private MatchHistory beforeSelection;
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

        // 为当前烘焙库建立可搜索的状态和轨道快照。
        public CharacterMotionMatcher(Animator animator, CharacterAnimationResourceCatalog resources,
            CharacterAnimationConfiguration config)
        {
            this.animator = animator;
            this.resources = resources;
            tuning = config.MatchCosts;
            layout = config.FeatureLayout;
            policy = config.CandidatePolicy ?? new GroundCandidatePolicy(null, null);
            search = new GroundPoseSearch(resources);
            walkLimit = Mathf.Max(0.01f, config.RunEnterSpeed);
            sprintEntry = Mathf.Max(walkLimit, config.SprintEnterSpeed);
            var database = config.MatchDatabase;
            margin = InputMath.NonNegative(config.MatchMargin, 0.12f);
            maxHold = InputMath.NonNegative(config.MatchReverseHold, 0.08f);
            if (!database || database.Format != CharacterMotionDatabase.CurrentFormat || database.BakedTracks == null) return;
            foreach (var track in database.BakedTracks)
            {
                if (track == null || track.States == null || track.States.Length == 0 ||
                    string.IsNullOrWhiteSpace(track.States[0])) continue;
                var variant = track.IsStop
                    ? (track.Side == GroundTrackSide.Left ? CharacterLocomotionVariant.StopLeft : CharacterLocomotionVariant.StopRight)
                    : track.Use == GroundMotionUse.Start ? CharacterLocomotionVariant.Start :
                    track.Use == GroundMotionUse.Turn ? (track.Side == GroundTrackSide.Left ?
                        CharacterLocomotionVariant.TurnLeft : CharacterLocomotionVariant.TurnRight) : CharacterLocomotionVariant.None;
                string stateName = track.States[0];
                resources.TryGetPhase(Animator.StringToHash(stateName), out var phase);
                var binding = track.IsStop
                    ? CharacterAnimationBinding.Stop(stateName, variant, config.Transition, track.BrakeSeconds, track)
                    : new CharacterAnimationBinding(new CharacterAnimationActionSettings
                        { StateName = stateName, StateTransitionDuration = track.Blend },
                        variant, phase);
                var frozen = resources.Snapshot(track);
                search.Add(frozen, binding);
                if (!track.ReadyForSearch) continue;
                foreach (string state in track.States)
                    if (!string.IsNullOrWhiteSpace(state)) states[Animator.StringToHash(state)] = frozen;
            }
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
            history.PreviewTrack = null;
            history.PreviewVersion = -1;
            history.HandledTurn = history.HandledReverse = history.BaseInstance = 0;
            history.Hold = history.TurnWait = history.SearchDelay = 0f;
            history.LastTarget = history.LastAlternate = GroundTrackGait.Any;
            history.PendingTurn = 0;
            history.Playing = null;
            history.ObservedPlaying = false;
        }

        // 在突进或攻击交还部位之前只缓存候选；实际归属、步态和承重脚均吻合时才允许使用。
        internal void PrepareReturn(CharacterMovementForecast forecast, double now, CharacterAnimationDemand owner,
            bool lowerCoversFeet, bool matchRecovery, CharacterAnimationMotion motion, CharacterFootObservation feet,
            in CharacterBehaviorFrameResult behavior, CharacterLocomotionPhase phase)
        {
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
                !CharacterForecastQuery.TryMatch(forecast, now, ahead, Windows, out var goal)) return;
            history.PreviewGait = next.Gait == CharacterMoveGait.Walk ? CharacterAnimationIntentConverter.WalkKey :
                next.Gait == CharacterMoveGait.Sprint ? CharacterAnimationIntentConverter.SprintKey : CharacterAnimationIntentConverter.MoveKey;
            double returnTime = now + ahead;
            if (history.PreviewVersion == forecast.Version && history.PreviewSupport == feet.Support && Mathf.Abs((float)(history.PreviewTime - returnTime)) < 0.0001f)
                return;
            float speed = Vector3.ProjectOnPlane(next.Velocity, Vector3.up).magnitude;
            var query = new GroundPoseQuery(GroundMotionUse.Cycle, GroundPoseQuery.GaitOf(history.PreviewGait), speed, goal.FarYaw);
            query = new GroundPoseQuery(query, policy.Select(query, behavior, phase, feet.Support, true));
            if (query.Selection.Skip) return;
            var frame = new GroundSearchFrame(feet, goal, tuning.Profile(query.Use), speed, speed, default, resources.PlaybackClockScale, Features, query.Selection.Features, true, layout?.LegacyPelvis == true);
            history.Preview = Find(ref query, ref frame, out history.PreviewTrack, false);
            history.PreviewVersion = forecast.Version;
            history.PreviewSupport = feet.Support;
            history.PreviewTime = returnTime;
        }

        // 在当前身体归属和地面状态下选择续播或匹配入点。
        public CharacterMatchResult Evaluate(CharacterAnimationIntentConverter intent, CharacterAnimationDemand owner, bool lowerCoversFeet, bool matchRecovery,
            in CharacterPlaybackObservation seen, in CharacterAnimationPlaybackPlan last,
            CharacterAnimationMotion motion, CharacterFootObservation feet, float dt, in CharacterBehaviorFrameResult behavior)
        {
#if UNITY_EDITOR
            search.DebugFrame?.Reset();
#endif
            if (mode == CharacterMatchMode.Rules) return Fallback("已配置常规规则");
            bool stopping = intent.Phase == CharacterLocomotionPhase.BrakeLeadIn;
            bool recovering = lowerCoversFeet && matchRecovery && !stopping && intent.Phase != CharacterLocomotionPhase.Stopping;
            bool reverse = !recovering && intent.ReverseReleaseStep != 0 && history.HandledReverse != intent.ReverseReleaseStep;
            bool turn = !recovering && intent.DirectTurnStep != 0 && history.HandledTurn != intent.DirectTurnStep;
            if (history.PendingTurn != intent.DirectTurnStep)
            {
                history.PendingTurn = intent.DirectTurnStep;
                history.TurnWait = 0f;
            }
            bool idleCrossing = reverse && owner.Key == CharacterAnimationIntentConverter.IdleKey;
            bool baselineUnavailable = !owner.IsLocomotion && !idleCrossing;
            if (lowerCoversFeet || baselineUnavailable)
            {
                history.HandledTurn = intent.DirectTurnStep;
                history.HandledReverse = intent.ReverseReleaseStep;
            }
            if ((lowerCoversFeet && !recovering) || baselineUnavailable)
            {
                history.Playing = null;
                history.BaseInstance = 0;
                return Fallback("下身或基础动作已接管");
            }
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
            if (intent.Phase == CharacterLocomotionPhase.Stopping) return Fallback("沿用制动时钟");
            var current = seen.Full.Destination;
            bool hasCurrent = states.TryGetValue(current.fullPathHash, out var source) &&
                current.fullPathHash == last.FullBody.StateHash && resources.HasGroundTrack(current.fullPathHash, source);
            if (!hasCurrent) source = null;
            bool starting = !recovering && intent.Phase == CharacterLocomotionPhase.Starting;
            GroundPoseQuery query;
            if (stopping)
                query = new GroundPoseQuery(GroundMotionUse.Stop, GroundPoseQuery.GaitOf(intent.StopGait), intent.StopEntryVelocity.magnitude);
            else if (turn || reverse)
                query = new GroundPoseQuery(GroundMotionUse.Turn, GroundPoseQuery.GaitOf(turn ? intent.DirectTurnGait : intent.ReverseGait),
                    turn ? intent.DirectTurnSpeed : intent.ReverseSpeed, turn ? intent.DirectTurnAngle : intent.ReverseAngle);
            else
                query = GroundPoseQuery.Moving(starting, intent.RequestedGait, owner.Key, source, motion, walkLimit, sprintEntry);

            if (!stopping && !turn && !reverse && starting && history.Playing != null &&
                !history.Playing.IsLoop && current.fullPathHash == history.PlayingHash && current.normalizedTime * history.Playing.Seconds >= history.Playing.ExitTime)
                query = GroundPoseQuery.Moving(false, intent.RequestedGait, owner.Key, source, motion, walkLimit, sprintEntry);
            query = new GroundPoseQuery(query, policy.Select(query, behavior, intent.Phase, feet.Support, recovering));

#if UNITY_EDITOR
            Describe(query);
#endif
            history.SearchDelay = Mathf.Max(0f, history.SearchDelay - dt);
            bool unchanged = history.BaseInstance == owner.InstanceId && history.LastTarget == query.Target &&
                history.LastAlternate == query.Alternate && history.LastUse == query.Use && history.LastRule == query.Selection.Rule;
            if (!stopping && !turn && !reverse && unchanged && history.Playing == null && history.SearchDelay > 0f)
                return Fallback("等待下一次周期查询");
            if (!stopping && !turn && !reverse &&
                KeepPlaying(query, unchanged, seen.Full, last.FullBody, intent.RequestedGait, motion.ExpectedVelocity, behavior, recovering))
            {
#if UNITY_EDITOR
                if (search.DebugFrame != null && hasCurrent)
                {
                    var continuingQuery = source.IsLoop ? query : history.EntryQuery;
                    Describe(continuingQuery);
                    var continued = Frame(continuingQuery, feet, motion);
                    if (!source.IsLoop && history.EntryWithoutPast) continued = continued.WithoutHistory();
                    RecordContinuing(source, current, last, continued, continuingQuery);
                    search.DebugFrame.Reason = "仍在续播窗口";
                }
#endif
                return new CharacterMatchResult { Choice = CharacterMatchChoice.Continue };
            }

            if (query.Selection.Skip)
            {
                history.Playing = history.PreviewTrack = null;
                history.SearchDelay = 0f;
                return Fallback(query.Selection.Label);
            }
            history.Playing = null;
            history.SearchDelay = CycleSearchInterval;
            history.LastUse = query.Use;
            history.LastRule = query.Selection.Rule;
            history.LastTarget = query.Target;
            history.LastAlternate = query.Alternate;
            var frame = Frame(query, feet, motion);
#if UNITY_EDITOR
            Describe(query);
#endif
            var best = Find(ref query, ref frame, out var winner, true);
            if ((query.Use == GroundMotionUse.Cycle || query.Use == GroundMotionUse.Transition) &&
                history.PreviewTrack != null &&
                query.Target == GroundPoseQuery.GaitOf(history.PreviewGait) &&
                resources.HasGroundTrack(history.Preview.Binding.StateHash, history.PreviewTrack))
            {
                int index = history.PreviewTrack.Index(history.Preview.Time / history.Preview.Length);
                var cost = frame.Score(history.PreviewTrack, index, resources.Pace(history.Preview.Binding.StateHash), out var rate);
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
                    if (search.DebugFrame != null) search.DebugFrame.Reason = "预测交还入点仍在宽限内";
#endif
                }
                history.PreviewTrack = null;
            }
            float keepCost = 0f;
            if (hasCurrent)
            {
                var cost = frame.ScoreCurrent(source, current.normalizedTime, resources.Pace(current.fullPathHash),
                    out float rate, out float time, last.StopEntryTime, last.StopEntryTravelPhase);
                cost.Entry = query.EntryPenalty(source);
                keepCost = cost.Total;
#if UNITY_EDITOR
                if (search.DebugFrame != null)
                    search.DebugFrame.Continuing = new CharacterMatchDebugEntry { State = last.FullBody.Binding.StateName,
                        Time = time, Rate = rate, Cost = cost };
#endif
            }
            bool canWait = stopping ? !intent.MainBrakeDue : turn ? history.TurnWait + dt < maxHold :
                !reverse || history.Hold + dt < maxHold;
            bool moving = !turn && !reverse && !stopping;
            bool eligibleCycle = hasCurrent && source.IsLoop && search.Filter(source, current.fullPathHash, query, feet.Support) == GroundMatchFilter.None;
            bool sameCycle = moving && eligibleCycle && source == winner;
            bool canKeep = hasCurrent && last.FullBody.Demand.IsLocomotion && (!moving || eligibleCycle);
            if (canWait && canKeep && (sameCycle || idleCrossing && winner == null || moving && unchanged && winner != null && keepCost <= best.Cost + margin))
            {
                best.Choice = CharacterMatchChoice.Continue;
#if UNITY_EDITOR
                if (search.DebugFrame != null) search.DebugFrame.Reason = sameCycle ? "同一循环保持相位" :
                    idleCrossing ? "反向过零保持原步" : "续播代价在切换宽限内";
#endif
            }
            if (turn)
            {
                history.TurnWait += dt;
                if (best.Choice != CharacterMatchChoice.Continue)
                    history.HandledTurn = intent.DirectTurnStep;
            }
            if (reverse && (best.Choice == CharacterMatchChoice.Continue || idleCrossing && canWait)) history.Hold += dt;
            else if (reverse) history.HandledReverse = intent.ReverseReleaseStep;
            if (owner.Key == CharacterAnimationIntentConverter.IdleKey && best.HasEntry)
                best.Choice = CharacterMatchChoice.Rules;
            if (!stopping && !idleCrossing) history.BaseInstance = owner.InstanceId;
            if (best.Choice == CharacterMatchChoice.Continue && hasCurrent && source.IsLoop && query.AllowsGait(source) &&
                !stopping && !reverse && !turn)
            {
                history.Playing = source;
                history.PlayingHash = current.fullPathHash;
                history.ObservedPlaying = true;
            }
            if (best.HasEntry && winner != null && !winner.IsStop)
            {
                if (winner.Use == GroundMotionUse.Turn)
                {
                    history.TurnDirection = motion.ExpectedVelocity.normalized;
                }
                history.EntryQuery = query;
                history.EntryPhase = intent.Phase;
                history.EntryInput = intent.RequestedGait;
                history.EntrySupport = feet.Support;
                history.EntryWithoutPast = !frame.AllowPast;
                history.Playing = winner;
                history.PlayingHash = best.Binding.StateHash;
                history.ObservedPlaying = false;
            }
            return best;
        }

        // 入场和预搜共用条件表的空组回退，解除范围后仍保留基本合法条件。
        private CharacterMatchResult Find(ref GroundPoseQuery query, ref GroundSearchFrame frame, out CharacterMotionTrack winner, bool record)
        {
            var found = search.Find(query, ref frame, out winner, record);
            if (winner != null || !query.Selection.Relax) return found;
            query = new GroundPoseQuery(query, query.Selection.WithoutGroups());
#if UNITY_EDITOR
            if (record) Describe(query);
#endif
            return search.Find(query, ref frame, out winner, record);
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
                !selection.Allows(track) && !entry.Selection.Relaxed) return false;
            return selected && actual.normalizedTime * track.Seconds < track.ExitTime || pending;
        }

        // 固定查询用途及实速、起步和制动的预测输入。
        private GroundSearchFrame Frame(in GroundPoseQuery query, in CharacterFootObservation feet, in CharacterAnimationMotion motion) =>
            new GroundSearchFrame(feet, motion.Trajectory.Match, tuning.Profile(query.Use), motion.ActualSpeed,
                GroundPlaybackPace.StartSpeed(motion, resources.WalkSpeed), motion.Trajectory, resources.PlaybackClockScale,
                Features, query.Selection.Features, true, layout?.LegacyPelvis == true
#if UNITY_EDITOR
                , search.DebugFrame != null
#endif
                );

        // 返回常规规则，并仅在诊断开启时保留具体原因。
        private CharacterMatchResult Fallback(string reason)
        {
#if UNITY_EDITOR
            if (search.DebugFrame != null) search.DebugFrame.Reason = reason;
#endif
            return default;
        }

#if UNITY_EDITOR
        // 记录最终试算使用的查询用途及来源、目标步态。
        private void Describe(in GroundPoseQuery query)
        {
            if (search.DebugFrame == null) return;
            search.DebugFrame.Query = $"{query.Use} · {query.From} → {query.Target} · {query.Selection.Label} · {layout?.Status ?? "兼容脚姿"}";
        }

        // 只有监听角色需要为直接续播的窗口补算当前姿势代价。
        private void RecordContinuing(CharacterMotionTrack source, CharacterClipProgress current,
            in CharacterAnimationPlaybackPlan last, in GroundSearchFrame frame, in GroundPoseQuery query)
        {
            search.DebugFrame.StartScoring(frame);
            var cost = frame.ScoreCurrent(source, current.normalizedTime, resources.Pace(current.fullPathHash),
                out float rate, out float time, last.StopEntryTime, last.StopEntryTravelPhase);
            cost.Entry = query.EntryPenalty(source);
            search.DebugFrame.Continuing = new CharacterMatchDebugEntry { State = last.FullBody.Binding.StateName,
                Time = time, Rate = rate, Cost = cost };
        }

        // 根据实际已提交的播放计划发布本帧诊断，丢弃未被采用的试算结果。
        internal void Publish(in CharacterMatchResult result, in CharacterAnimationPlaybackPlan plan)
        {
            var debug = search.DebugFrame;
            if (debug == null) return;
            var layer = plan.FullBody;
            if (result.HasEntry && layer.Reason == CharacterAnimationPlaybackReason.MatchPose && layer.StateHash == result.Binding.StateHash)
            {
                debug.Choice = result.Choice.ToString();
                debug.Reason ??= "最低代价有效入点";
                debug.Chosen = new CharacterMatchDebugEntry { State = layer.Binding.StateName, Time = result.Time,
                    Rate = result.Rate, Cost = result.Components };
            }
            else if (result.Choice == CharacterMatchChoice.Continue && layer.Reason == CharacterAnimationPlaybackReason.ContinueMatch)
            {
                debug.Choice = "Continue";
                debug.Reason ??= "等待当前片段进入播放观测";
                debug.Chosen = debug.Continuing;
            }
            else
            {
                debug.Choice = "Rules";
                if (result.HasEntry || result.Choice == CharacterMatchChoice.Continue)
                    debug.Reason = "最终播放归属或制动规则已更新";
                else debug.Reason ??= "无可用匹配入点";
            }
            CharacterMatchDebugCapture.Publish(animator, debug);
            search.DebugFrame = null;
        }
#endif
    }
}
