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
        private readonly float margin, maxHold;
        private readonly CharacterLegacyMotionMatcher legacy;
        private readonly Dictionary<int, CharacterMotionTrack> states = new Dictionary<int, CharacterMotionTrack>();
        private readonly List<Entry> entries = new List<Entry>();
        private struct Entry
        {
            public CharacterMotionTrack Track;
            public CharacterAnimationBinding Binding;
        }

        private CharacterMatchMode mode;
        private long handledTurn, handledReverse, baseInstance;
        private float hold;
        private long pendingTurn;
        private float turnWait;
        private CharacterMotionTrack playing;
        private int playingHash;
        private bool observedPlaying;
        private bool UsesLegacy => !database || database.Format == 0 || mode == CharacterMatchMode.LegacyBaseline;
        public CharacterMatchMode Mode
        {
            get => mode;
            set
            {
                if (value == mode) return;
                Reset();
                mode = value;
                legacy.Mode = value == CharacterMatchMode.LegacyBaseline ? CharacterMatchMode.Apply : value;
            }
        }

#if UNITY_EDITOR
        private readonly System.Text.StringBuilder exclusions = new System.Text.StringBuilder();
        private string decision = "等待地面查询";
        public string Describe() => UsesLegacy ? legacy.Describe() :
            mode == CharacterMatchMode.Rules ? "使用原规则" : decision;
#endif

        public CharacterMotionMatcher(Animator animator, CharacterAnimationResourceCatalog resources,
            CharacterAnimationConfiguration config)
        {
            this.animator = animator;
            this.resources = resources;
            database = config.MatchDatabase;
            margin = InputMath.NonNegative(config.MatchMargin, 0.12f);
            maxHold = InputMath.NonNegative(config.MatchReverseHold, 0.08f);
            legacy = new CharacterLegacyMotionMatcher(animator, resources, config);
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
                entries.Add(new Entry { Track = track, Binding = binding });
                if (!track.ReadyForSearch) continue;
                foreach (string state in track.States)
                    if (!string.IsNullOrWhiteSpace(state)) states[Animator.StringToHash(state)] = track;
            }
        }

        public void Reset()
        {
            handledTurn = handledReverse = baseInstance = 0;
            hold = turnWait = 0f;
            pendingTurn = 0;
            playing = null;
            observedPlaying = false;
            legacy?.Reset();
#if UNITY_EDITOR
            decision = "等待地面查询";
#endif
        }

        public CharacterMatchResult Evaluate(CharacterAnimationIntentConverter intent, CharacterAnimationDemand owner, bool lowerCoversFeet,
            in CharacterPlaybackObservation seen, in CharacterAnimationPlaybackPlan last, CharacterReleaseDecision release,
            CharacterAnimationMotion motion, CharacterFootObservation feet, float dt)
        {
            if (UsesLegacy) return legacy.Evaluate(intent, owner, lowerCoversFeet, seen, last, release, motion, feet, dt);
            if (mode == CharacterMatchMode.Rules) return default;
            bool stopping = intent.Phase == CharacterLocomotionPhase.BrakeLeadIn;
            bool reverse = intent.ReverseReleaseStep != 0 && handledReverse != intent.ReverseReleaseStep;
            bool turn = intent.DirectTurnStep != 0 && handledTurn != intent.DirectTurnStep;
            if (pendingTurn != intent.DirectTurnStep)
            {
                pendingTurn = intent.DirectTurnStep;
                turnWait = 0f;
            }
            bool idleCrossing = reverse && owner.Key == CharacterAnimationIntentConverter.IdleKey;
            if (lowerCoversFeet || (!owner.IsLocomotion && !idleCrossing))
            {
                handledTurn = intent.DirectTurnStep;
                handledReverse = intent.ReverseReleaseStep;
                bool interrupt = lowerCoversFeet || owner.Key != CharacterAnimationIntentConverter.IdleKey;
                playing = null;
                baseInstance = 0;
                return interrupt ? Rules("下身归属已改变") : default;
            }
            if (!database || !animator || animator.avatar != database.BakedAvatar ||
                resources.Controller != database.BakedController || !motion.IsValid || !motion.Trajectory.Match.Valid ||
                motion.IsBlocked || motion.Trajectory.IsBrakeObstructed || (motion.CollisionFlags & CollisionFlags.Below) == 0)
            {
                playing = null;
                handledTurn = intent.DirectTurnStep;
                handledReverse = intent.ReverseReleaseStep;
                return Rules("资源或地面预测不可用");
            }
            if (feet.Count < 2 || !seen.Full.Ready) return Rules("等待有效脚姿与播放观测");
            if (!reverse) hold = 0f;
            bool returningToCycle = false;
            if (!stopping && !turn && !reverse && intent.Phase != CharacterLocomotionPhase.Stopping &&
                KeepPlaying(owner, seen.Full, last.FullBody, out returningToCycle))
                return new CharacterMatchResult { Choice = CharacterMatchChoice.Continue };
            playing = null;
            if (intent.Phase == CharacterLocomotionPhase.Stopping) return default;
            var use = GroundMotionUse.Cycle;
            if (stopping) use = GroundMotionUse.Stop;
            else if (turn) use = GroundMotionUse.Turn;
            else if (!reverse && !returningToCycle && intent.Phase == CharacterLocomotionPhase.Starting) use = GroundMotionUse.Start;
            if (!stopping && !turn && !reverse && !returningToCycle && baseInstance == owner.InstanceId) return default;
            string gait = stopping ? intent.StopGait : turn ? intent.DirectTurnGait : owner.Key;
            float entrySpeed = stopping ? intent.StopEntryVelocity.magnitude : turn ? intent.DirectTurnSpeed : motion.ActualSpeed;
            var current = seen.Full.Destination;
            var best = Search(use, gait, entrySpeed, intent.DirectTurnAngle, feet, motion,
                out var winner, out var bestCost);
            GroundMotionCost keepCost = default;
            bool hasCurrent = states.TryGetValue(current.fullPathHash, out var source) &&
                current.fullPathHash == last.FullBody.StateHash && resources.HasGroundTrack(current.fullPathHash, source);
            if (hasCurrent)
            {
                keepCost = new GroundMotionCost(source, source.Index(current.normalizedTime), feet,
                    motion.Trajectory.Match, motion.ActualSpeed);
                best.ContinueCost = keepCost.Total;
            }
            bool canWait = stopping ? !intent.MainBrakeDue : !reverse || hold + dt <= maxHold;
            bool sameCycle = hasCurrent && use == GroundMotionUse.Cycle && winner == source && !reverse &&
                current.fullPathHash == best.Binding.StateHash;
            bool canKeep = hasCurrent && last.FullBody.Demand.IsLocomotion && (turn || stopping || reverse || sameCycle);
            if (canWait && canKeep && (idleCrossing || winner != null && keepCost.Total <= best.Cost + margin))
                best.Choice = CharacterMatchChoice.Continue;
            if (turn)
            {
                turnWait += dt;
                if (best.Choice != CharacterMatchChoice.Continue || turnWait >= maxHold)
                    handledTurn = intent.DirectTurnStep;
            }
            if (reverse && best.Choice == CharacterMatchChoice.Continue) hold += dt;
            else if (reverse) handledReverse = intent.ReverseReleaseStep;
            if (owner.Key == CharacterAnimationIntentConverter.IdleKey && best.HasEntry)
                best.Choice = CharacterMatchChoice.Rules;
            if (!stopping && !idleCrossing) baseInstance = owner.InstanceId;
            if (best.Choice == CharacterMatchChoice.Continue && hasCurrent && source.IsLoop && source.Accepts(owner.Key) &&
                !stopping && !reverse && mode == CharacterMatchMode.Apply)
            {
                playing = source;
                playingHash = current.fullPathHash;
                observedPlaying = true;
            }
            if (best.HasEntry && winner != null && !winner.IsStop && mode == CharacterMatchMode.Apply)
            {
                playing = winner;
                playingHash = best.Binding.StateHash;
                observedPlaying = false;
            }
#if UNITY_EDITOR
            string chosen = best.HasEntry ? $"{best.Binding.StateName} @ {best.Time:F3}s" :
                best.Choice == CharacterMatchChoice.Continue ? "续播" : "回退原规则";
            decision = $"固定步 {motion.FrameNumber} · {use} · {chosen}\n" +
                $"最佳候选 {(winner == null ? "无" : winner.States[0] + $" @ {best.Time:F3}s · " + bestCost)}\n" +
                $"续播 {(hasCurrent ? keepCost.ToString() : "无可比较的当前轨道")}\n{feet.DescribeContacts()}\n{exclusions}";
#endif
            return mode == CharacterMatchMode.Apply ? best : default;
        }

        private bool KeepPlaying(CharacterAnimationDemand owner, in CharacterLayerObservation seen,
            in CharacterAnimationLayerPlan last, out bool returningToCycle)
        {
            returningToCycle = false;
            if (playing == null || last.StateHash != playingHash || !last.Demand.IsLocomotion) return false;
            var actual = seen.Destination;
            bool selected = actual.fullPathHash == playingHash;
            if (selected) observedPlaying = true;
            bool pendingEvaluation = !observedPlaying && last.Command != CharacterAnimationPlaybackCommand.Keep;
            if (playing.IsLoop) return (selected || pendingEvaluation) && baseInstance == owner.InstanceId && playing.Accepts(owner.Key);
            if (selected && actual.normalizedTime * playing.Seconds < playing.ExitTime || pendingEvaluation) return true;
            returningToCycle = true;
            return false;
        }

        private CharacterMatchResult Search(GroundMotionUse use, string gait, float entrySpeed, float angle,
            in CharacterFootObservation feet, CharacterAnimationMotion motion, out CharacterMotionTrack winner, out GroundMotionCost bestCost)
        {
            var best = new CharacterMatchResult { Cost = float.PositiveInfinity, ContinueCost = float.PositiveInfinity };
            bestCost = default;
            winner = null;
#if UNITY_EDITOR
            exclusions.Clear();
#endif
            var support = feet.Support;
            foreach (var entry in entries)
            {
                var track = entry.Track;
                if (track.Use != use) continue;
                string reason = Excluded(entry, gait, entrySpeed, angle, support);
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
                    var cost = new GroundMotionCost(track, i, feet, motion.Trajectory.Match, motion.ActualSpeed);
                    if (!(cost.Total < best.Cost)) continue; // 配置顺序、时间先后确定平分结果。
                    winner = track;
                    bestCost = cost;
                    best.Cost = cost.Total;
                    best.Choice = track.IsStop ? CharacterMatchChoice.Stop : CharacterMatchChoice.Restart;
                    best.Binding = entry.Binding;
                    best.Time = t;
                    best.Length = track.Seconds;
                }
                ReportTrack(track, admitted ? "入选比较" : inWindow ? "入点承重脚冲突" : "无合法入点");
            }
            return best;
        }

        private string Excluded(Entry entry, string gait, float speed, float angle, GroundTrackSide support)
        {
            var track = entry.Track;
            if (!track.ReadyForSearch) return "采样或路径无效";
            if (!resources.HasGroundTrack(entry.Binding.StateHash, track)) return "状态或片段不匹配";
            if (!track.Accepts(gait)) return "步态";
            if (speed < track.MinimumSpeed || track.MaximumSpeed > 0f && speed > track.MaximumSpeed) return "入口速度";
            if (track.Use == GroundMotionUse.Turn && track.Side != GroundTrackSide.Either &&
                (angle < 0f) != (track.Side == GroundTrackSide.Left)) return "方向";
            if (track.IsStop && support != GroundTrackSide.Either && track.Side != support) return "承重脚";
            return null;
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void ReportTrack(CharacterMotionTrack track, string reason)
        {
#if UNITY_EDITOR
            if (exclusions.Length < 1200) exclusions.Append(track.States[0]).Append(" · ").AppendLine(reason);
#endif
        }

        private CharacterMatchResult Rules(string reason)
        {
#if UNITY_EDITOR
            decision = reason;
#endif
            return default;
        }
    }
}
