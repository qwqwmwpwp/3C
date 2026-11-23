using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal enum CharacterMatchChoice { Rules, Continue, Stop, Restart }

    internal struct CharacterMatchResult
    {
        public CharacterMatchChoice Choice;
        public CharacterAnimationBinding Binding;
        public float Time, Length, Cost, ContinueCost;
        public bool HasEntry => Choice == CharacterMatchChoice.Stop || Choice == CharacterMatchChoice.Restart;
    }

    internal sealed class CharacterMotionMatcher
    {
        private readonly CharacterMotionDatabase database;
        private readonly Animator animator;
        private readonly CharacterAnimationResourceCatalog resources;
        private static readonly Unity.Profiling.ProfilerMarker searchMarker = new Unity.Profiling.ProfilerMarker("3C.Animation.PoseSearch");
        private const float CycleSearchInterval = 0.10f;
        private readonly float margin, maxHold, walkLimit, sprintEntry;
        private CharacterLegacyMotionMatcher legacy;
        private readonly Dictionary<int, CharacterMotionTrack> states = new Dictionary<int, CharacterMotionTrack>();
        private readonly List<Entry> entries = new List<Entry>();
        private struct Entry
        {
            public CharacterMotionTrack Track;
            public CharacterAnimationBinding Binding;
        }

        private CharacterMatchMode mode;
        private struct MatchHistory
        {
            internal long HandledTurn, HandledReverse, BaseInstance, PendingTurn;
            internal float Hold, TurnWait, SearchDelay;
            internal GroundTrackGait LastTarget, LastAlternate;
            internal CharacterMoveGait InputAtTurn;
            internal Vector3 TurnDirection;
            internal CharacterMotionTrack Playing, PreviewTrack;
            internal int PlayingHash;
            internal bool ObservedPlaying;
            internal long PreviewVersion;
            internal CharacterMatchResult Preview;
            internal string PreviewGait;
            internal GroundTrackSide PreviewSupport;
            internal double PreviewTime;
#if UNITY_EDITOR
            internal string Decision;
#endif
        }

        private MatchHistory history = new MatchHistory { PreviewVersion = -1 };
        private MatchHistory beforeSelection;
        private readonly bool legacyDatabase;
        private bool compatible;
        private bool UsesLegacy => legacyDatabase || mode == CharacterMatchMode.LegacyBaseline;
        public CharacterMatchMode Mode
        {
            get => mode;
            set
            {
                if (value == mode) return;
                Reset();
                mode = value;
                if (UsesLegacy && value != CharacterMatchMode.Rules && database)
                {
                    legacy ??= new CharacterLegacyMotionMatcher(animator, resources, database, margin, maxHold);
                    legacy.Mode = value == CharacterMatchMode.LegacyBaseline ? CharacterMatchMode.Apply : value;
                }
            }
        }

#if UNITY_EDITOR
        private readonly System.Text.StringBuilder exclusions = new System.Text.StringBuilder();
        private int diagnosticFrames;
        private int candidates;
        public string Describe()
        {
            diagnosticFrames = 30;
            return mode == CharacterMatchMode.Rules ? "使用原规则" : UsesLegacy ?
                legacy?.Describe() ?? CharacterLegacyMotionMatcher.IncompatibleMessage : history.Decision ?? "等待地面查询";
        }
#endif

        public CharacterMotionMatcher(Animator animator, CharacterAnimationResourceCatalog resources,
            CharacterAnimationConfiguration config)
        {
            this.animator = animator;
            this.resources = resources;
            walkLimit = Mathf.Max(0.01f, config.RunEnterSpeed);
            sprintEntry = Mathf.Max(walkLimit, config.SprintEnterSpeed);
            database = config.MatchDatabase;
            legacyDatabase = !database || database.Format == 0;
            margin = InputMath.NonNegative(config.MatchMargin, 0.12f);
            maxHold = InputMath.NonNegative(config.MatchReverseHold, 0.08f);
            if (!database) return;
            if (database.Format != 1 || database.BakedTracks == null) return;
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
                        track.IsLoop && track.ScalesWithSpeed ? track.ReferenceSpeed : 0f, variant, phase);
                var frozen = resources.Snapshot(track);
                entries.Add(new Entry { Track = frozen, Binding = binding });
                if (!track.ReadyForSearch) continue;
                foreach (string state in track.States)
                    if (!string.IsNullOrWhiteSpace(state)) states[Animator.StringToHash(state)] = frozen;
            }
        }

        public void CaptureCompatibility()
        {
            if (UsesLegacy)
            {
                if (mode != CharacterMatchMode.Rules) legacy?.CaptureCompatibility();
                return;
            }
            compatible = database && animator && animator.avatar == database.BakedAvatar &&
                resources.MatchesLibrary(database);
        }

        internal void SaveSelectionHistory()
        {
            beforeSelection = history;
            legacy?.SaveSelectionHistory();
        }

        internal void RestoreSelectionHistory()
        {
            history = beforeSelection;
            legacy?.RestoreSelectionHistory();
        }

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
            legacy?.Reset();
#if UNITY_EDITOR
            history.Decision = "等待地面查询";
#endif
        }

        // 在突进或攻击交还部位之前只缓存候选；实际归属、步态和承重脚均吻合时才允许使用。
        internal void PrepareReturn(CharacterMovementForecast forecast, double now, CharacterAnimationDemand owner,
            bool lowerCoversFeet, bool matchRecovery, CharacterAnimationMotion motion, CharacterFootObservation feet)
        {
            if (forecast == null || !forecast.Available || mode != CharacterMatchMode.Apply || UsesLegacy || !compatible ||
                (lowerCoversFeet && !matchRecovery) || !motion.IsValid || motion.IsBlocked || feet.Count < 2)
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
                !CharacterForecastQuery.TryMatch(forecast, now, ahead, out var goal)) return;
            history.PreviewGait = next.Gait == CharacterMoveGait.Walk ? CharacterAnimationIntentConverter.WalkKey :
                next.Gait == CharacterMoveGait.Sprint ? CharacterAnimationIntentConverter.SprintKey : CharacterAnimationIntentConverter.MoveKey;
            double returnTime = now + ahead;
            if (history.PreviewVersion == forecast.Version && history.PreviewSupport == feet.Support && Mathf.Abs((float)(history.PreviewTime - returnTime)) < 0.0001f)
                return;
            float speed = Vector3.ProjectOnPlane(next.Velocity, Vector3.up).magnitude;
            var query = new GroundPoseQuery(GroundMotionUse.Cycle, GroundPoseQuery.GaitOf(history.PreviewGait), speed, arcYaw: goal.FarYaw);
            history.Preview = Search(query, feet, goal, speed, out history.PreviewTrack, out _);
            history.PreviewVersion = forecast.Version;
            history.PreviewSupport = feet.Support;
            history.PreviewTime = returnTime;
        }

        public CharacterMatchResult Evaluate(CharacterAnimationIntentConverter intent, CharacterAnimationDemand owner, bool lowerCoversFeet, bool matchRecovery,
            in CharacterPlaybackObservation seen, in CharacterAnimationPlaybackPlan last, CharacterReleaseDecision release,
            CharacterAnimationMotion motion, CharacterFootObservation feet, float dt)
        {
#if UNITY_EDITOR
            if (diagnosticFrames > 0) diagnosticFrames--;
#endif
            if (mode == CharacterMatchMode.Rules) return default;
            if (UsesLegacy) return legacy?.Evaluate(intent, owner, lowerCoversFeet, seen, last, release, motion, feet, dt) ?? default;
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
                bool interrupt = lowerCoversFeet || owner.Key != CharacterAnimationIntentConverter.IdleKey;
                history.Playing = null;
                history.BaseInstance = 0;
                return interrupt ? Rules("下身归属已改变") : default;
            }
            if (!compatible || !motion.IsValid || !motion.Trajectory.Match.Valid ||
                motion.IsBlocked || motion.Trajectory.IsBrakeObstructed || (motion.CollisionFlags & CollisionFlags.Below) == 0)
            {
                history.Playing = null;
                history.HandledTurn = intent.DirectTurnStep;
                history.HandledReverse = intent.ReverseReleaseStep;
                history.BaseInstance = 0;
                return Rules("资源或地面预测不可用");
            }
            if (feet.Count < 2 || !seen.Full.Ready)
            {
                history.Playing = null;
                history.BaseInstance = 0;
                return Rules("等待有效脚姿与播放观测");
            }
            if (!reverse) history.Hold = 0f;
            if (intent.Phase == CharacterLocomotionPhase.Stopping) return default;
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

            history.SearchDelay = Mathf.Max(0f, history.SearchDelay - dt);
            bool unchanged = history.BaseInstance == owner.InstanceId && history.LastTarget == query.Target &&
                history.LastAlternate == query.Alternate;
            if (!stopping && !turn && !reverse && unchanged && history.Playing == null && history.SearchDelay > 0f)
                return default;
            if (!stopping && !turn && !reverse &&
                KeepPlaying(query, unchanged, seen.Full, last.FullBody, intent.RequestedGait, motion.ExpectedVelocity))
                return new CharacterMatchResult { Choice = CharacterMatchChoice.Continue };
            if (!stopping && !turn && !reverse && starting && history.Playing != null &&
                !history.Playing.IsLoop && current.fullPathHash == history.PlayingHash && current.normalizedTime * history.Playing.Seconds >= history.Playing.ExitTime)
                query = GroundPoseQuery.Moving(false, intent.RequestedGait, owner.Key, source, motion, walkLimit, sprintEntry);

            history.Playing = null;
            history.SearchDelay = CycleSearchInterval;
            history.LastTarget = query.Target;
            history.LastAlternate = query.Alternate;
            var best = Search(query, feet, motion.Trajectory.Match, motion.ActualSpeed, out var winner, out var bestCost);
            if (query.Use == GroundMotionUse.Cycle && history.PreviewTrack != null && query.AllowsHeading(history.PreviewTrack) &&
                query.Target == GroundPoseQuery.GaitOf(history.PreviewGait) &&
                resources.HasGroundTrack(history.Preview.Binding.StateHash, history.PreviewTrack))
            {
                int index = history.PreviewTrack.Index(history.Preview.Time / history.Preview.Length);
                var cost = new GroundMotionCost(history.PreviewTrack, index, feet, motion.Trajectory.Match, motion.ActualSpeed);
                var support = CharacterFootObservation.SupportOf(history.PreviewTrack.Poses[index].Contact);
                if (Excluded(new Entry { Track = history.PreviewTrack, Binding = history.Preview.Binding }, query, feet.Support) == null &&
                    (feet.Support == GroundTrackSide.Either || support == GroundTrackSide.Either || support == feet.Support) &&
                    cost.Total <= best.Cost + margin)
                {
                    best = history.Preview;
                    best.Cost = cost.Total;
                    winner = history.PreviewTrack;
                    bestCost = cost;
                }
                history.PreviewTrack = null;
            }
            GroundMotionCost keepCost = default;
            if (hasCurrent)
            {
                keepCost = new GroundMotionCost(source, source.Index(current.normalizedTime), feet,
                    motion.Trajectory.Match, motion.ActualSpeed);
                best.ContinueCost = keepCost.Total;
            }
            bool canWait = stopping ? !intent.MainBrakeDue : turn ? history.TurnWait + dt < maxHold :
                !reverse || history.Hold + dt < maxHold;
            bool moving = !turn && !reverse && !stopping;
            bool eligibleCycle = hasCurrent && source.IsLoop && query.AllowsGait(source) && query.AllowsHeading(source);
            bool sameCycle = moving && eligibleCycle && source == winner;
            bool canKeep = hasCurrent && last.FullBody.Demand.IsLocomotion && (!moving || eligibleCycle);
            if (canWait && canKeep && (sameCycle || idleCrossing || winner != null && keepCost.Total <= best.Cost + margin))
                best.Choice = CharacterMatchChoice.Continue;
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
                !stopping && !reverse && !turn && mode == CharacterMatchMode.Apply)
            {
                history.Playing = source;
                history.PlayingHash = current.fullPathHash;
                history.ObservedPlaying = true;
            }
            if (best.HasEntry && winner != null && !winner.IsStop && mode == CharacterMatchMode.Apply)
            {
                if (winner.Use == GroundMotionUse.Turn)
                {
                    history.InputAtTurn = intent.RequestedGait;
                    history.TurnDirection = motion.ExpectedVelocity.normalized;
                }
                history.Playing = winner;
                history.PlayingHash = best.Binding.StateHash;
                history.ObservedPlaying = false;
            }
#if UNITY_EDITOR
            if (diagnosticFrames > 0)
            {
                string chosen = best.HasEntry ? $"{best.Binding.StateName} @ {best.Time:F3}s" :
                    best.Choice == CharacterMatchChoice.Continue ? "续播" : "回退原规则";
                string currentScore = hasCurrent ? $"{last.FullBody.StateName} · {keepCost}" : "无当前轨道";
                string advantage = hasCurrent && winner != null ? $" · 相对续播改善 {keepCost.Total - best.Cost:F2}" : "";
                history.Decision = $"固定步 {motion.FrameNumber} · 实速 {motion.ActualSpeed:F2} m/s · 意图 {intent.RequestedGait} · {chosen}\n" +
                    $"候选 {query.Use} / {query.From} → {query.Target}（兼容 {query.Alternate}）· {candidates} 帧 · " +
                    $"预测 {motion.Trajectory.Match.Near} / {motion.Trajectory.Match.Far}\n" +
                    $"最佳 {(winner == null ? "无" : winner.States[0] + $" @ {best.Time:F3}s · " + bestCost)}{advantage}\n" +
                    $"续播 {currentScore}\n{feet.DescribeContacts()}\n{exclusions}";
            }
#endif
            return mode == CharacterMatchMode.Apply ? best : default;
        }

        private bool KeepPlaying(in GroundPoseQuery query, bool unchanged, in CharacterLayerObservation seen,
            in CharacterAnimationLayerPlan last, CharacterMoveGait requested, Vector3 direction)
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
                if (requested != history.InputAtTurn || Vector3.Dot(history.TurnDirection, direction.normalized) < 0.7f) return false;
            }
            else if (!query.AllowsGait(track)) return false;
            if (track.IsLoop) return unchanged && query.AllowsHeading(track) && history.SearchDelay > 0f;
            return selected && actual.normalizedTime * track.Seconds < track.ExitTime || pending;
        }

        private CharacterMatchResult Search(in GroundPoseQuery query, in CharacterFootObservation feet,
            CharacterMatchTrajectory goal, float actualSpeed, out CharacterMotionTrack winner, out GroundMotionCost bestCost)
        {
            using var sample = searchMarker.Auto();
            var best = new CharacterMatchResult { Cost = float.PositiveInfinity, ContinueCost = float.PositiveInfinity };
            bestCost = default;
            winner = null;
#if UNITY_EDITOR
            exclusions.Clear();
            candidates = 0;
#endif
            var support = feet.Support;
            foreach (var entry in entries)
            {
                var track = entry.Track;
                if (!query.AllowsUse(track)) continue;
                string reason = Excluded(entry, query, support);
                if (reason != null)
                {
                    ReportTrack(track, reason);
                    continue;
                }
                bool inWindow = false, admitted = false;
                int count = track.Poses.Length - (track.IsLoop ? 1 : 0);
                for (int i = 0; i < count; i++)
                {
                    float t = track.TimeAt(i);
                    if (t < track.FirstEntrySeconds || t > track.LastEntrySeconds + 0.00001f ||
                        track.IsStop && t >= track.BrakeSeconds) continue;
                    inWindow = true;
                    if (support != GroundTrackSide.Either)
                    {
                        var candidateSide = CharacterFootObservation.SupportOf(track.Poses[i].Contact);
                        if (candidateSide != GroundTrackSide.Either && candidateSide != support) continue;
                    }
                    admitted = true;
                    var cost = new GroundMotionCost(track, i, feet, goal, actualSpeed);
#if UNITY_EDITOR
                    candidates++;
#endif
                    float total = cost.Total + query.EntryPenalty(track);
                    if (!(total < best.Cost)) continue; // 配置顺序、时间先后确定平分结果。
                    winner = track;
                    bestCost = cost;
                    best.Cost = total;
                    best.Choice = track.IsStop ? CharacterMatchChoice.Stop : CharacterMatchChoice.Restart;
                    best.Binding = entry.Binding;
                    best.Time = t;
                    best.Length = track.Seconds;
                }
                ReportTrack(track, admitted ? "入选比较" : inWindow ? "入点承重脚冲突" : "无合法入点");
            }
            return best;
        }

        private string Excluded(Entry entry, in GroundPoseQuery query, GroundTrackSide support)
        {
            var track = entry.Track;
            if (!entry.Track.SearchSamplesValid) return "采样或路径无效";
            if (!resources.HasGroundTrack(entry.Binding.StateHash, track)) return "状态或片段不匹配";
            if (!query.AllowsGait(track)) return "步态";
            float speed = query.EntrySpeed;
            if (speed < track.MinimumSpeed || track.MaximumSpeed > 0f && speed > track.MaximumSpeed) return "入口速度";
            if (!query.AllowsHeading(track)) return "转向角度";
            if (track.IsStop && support != GroundTrackSide.Either && track.Side != support) return "承重脚";
            return null;
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void ReportTrack(CharacterMotionTrack track, string reason)
        {
#if UNITY_EDITOR
            if (diagnosticFrames > 0 && exclusions.Length < 1200) exclusions.Append(track.States[0]).Append(" · ").AppendLine(reason);
#endif
        }

        private CharacterMatchResult Rules(string reason)
        {
#if UNITY_EDITOR
            history.Decision = reason;
#endif
            return default;
        }
    }
}
