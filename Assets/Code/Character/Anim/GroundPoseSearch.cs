using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 记录本次查询的统一特征系数和物理时钟输入。
    internal readonly struct GroundSearchFrame
    {
        internal readonly CharacterFootObservation Feet;
        internal readonly CharacterMatchTrajectory Goal;
        internal readonly GroundMatchCost Profile;
        internal readonly CharacterGroundTrajectory Trajectory;
        internal readonly float Speed, StartSpeed, ClockScale;
        internal readonly GroundFeatureQuery Features;
        private readonly GroundLibraryObservations observations;
        private readonly float? ahead;
        internal readonly GroundFeatureGroups Groups;
        internal readonly bool AllowPast, LegacyPelvis;
#if UNITY_EDITOR
        private readonly bool captureCosts;
#endif

        // 固定一次搜索及其交还、续播比较所使用的输入。
        internal GroundSearchFrame(in CharacterFootObservation feet, in CharacterMatchTrajectory goal,
            GroundMatchCost profile, float speed, float startSpeed, CharacterGroundTrajectory trajectory, float clockScale,
            GroundLibraryObservations observations, GroundFeatureGroups groups, float? ahead = null
#if UNITY_EDITOR
            , bool captureCosts = false
#endif
            )
        {
            Feet = feet;
            Goal = goal;
            Profile = profile;
            Speed = speed;
            StartSpeed = startSpeed;
            ClockScale = clockScale;
            this.observations = observations;
            this.ahead = ahead;
            Features = default;
            Groups = groups;
            AllowPast = true;
            LegacyPelvis = false;
            Trajectory = trajectory;
#if UNITY_EDITOR
            this.captureCosts = captureCosts;
#endif
        }

        // 保留冻结的运动与时钟，仅切换到一个库真实可用的观测和计分参数。
        private GroundSearchFrame(in GroundSearchFrame frame, in CharacterMatchTrajectory goal, GroundFeatureQuery features,
            bool pelvis, bool past)
        {
            this = frame;
            Goal = goal;
            Features = features;
            LegacyPelvis = pelvis;
            AllowPast = past;
        }

        // 查找当前库的专属评分输入；失败时诊断保留库下标和缺失通道。
        internal bool TryLibrary(int index, out GroundSearchFrame frame, out string reason, out int channel)
        {
            bool ready = observations.TryRead(index, ahead, Groups, Goal, out var goal, out var features,
                out bool pelvis, out reason, out channel);
            frame = ready ? new GroundSearchFrame(this, goal, features, pelvis, AllowPast) : default;
            return ready;
        }

        // 所有合法入点均缺少前史时，使用同一库相同的其余评分维度。
        internal GroundSearchFrame WithoutHistory() => new GroundSearchFrame(this, Goal, Features, LegacyPelvis, false);

        // 使用候选真实入点、素材倍率和制动进度计算该姿势的分项贡献。
        internal GroundMatchCost Score(CharacterMotionTrack track, int index, GroundPlaybackPace pace,
            out float rate) => Score(track, track.TimeAt(index), track.Poses[index], pace, out rate);

        // 按实际播放相位插值续播姿势，再走与新入点完全相同的评分。
        internal GroundMatchCost ScoreCurrent(CharacterMotionTrack track, float normalized, GroundPlaybackPace pace,
            out float rate, out float time, float stopEntry, float stopPhase)
        {
            float phase = track.IsLoop ? Mathf.Repeat(normalized, 1f) : Mathf.Clamp01(normalized);
            time = phase * track.Seconds;
            track.TrySamplePose(time, out var sample);
            return Score(track, time, sample, pace, out rate, stopEntry, stopPhase);
        }

        // 用同一素材逻辑时钟推进历史、未来轨迹与当前骨骼速度。
        private GroundMatchCost Score(CharacterMotionTrack track, float time, in CharacterFootPose sample, GroundPlaybackPace pace,
            out float rate, float? stopEntry = null, float? stopPhase = null)
        {
            float braking = 1f;
            if (track.IsStop)
            {
                float distance = Trajectory.MainBrakeTravel + Trajectory.RemainingStopDistance;
                float phase = Trajectory.IsBraking && !Trajectory.IsBrakeLeadIn && distance > 0f ?
                    1f - Mathf.Sqrt(Mathf.Clamp01(Trajectory.RemainingStopDistance / distance)) : 0f;
                braking = GroundPlaybackPace.BrakeRate(Trajectory, track.BrakeSeconds, time, stopEntry ?? time, stopPhase ?? phase);
            }
            rate = pace.Rate(Speed, StartSpeed, braking, out float excess);
            var cost = GroundMotionCost.Evaluate(track, time, sample, Feet, Goal, Speed, rate * ClockScale, excess, Profile, Features.Active, Groups, AllowPast, LegacyPelvis);
            if (Features.Active)
                cost.Features = Features.Score(track, time, rate * ClockScale, Goal, Groups, AllowPast, out cost.MissingHistory
#if UNITY_EDITOR
                    , captureCosts, out cost.FeatureBreakdown
#endif
                    );
            return cost;
        }
    }

    // 标识轨道或入点被过滤的首要原因。
    internal enum GroundMatchFilter { None, Use, Gait, Resource, Speed, Support, Window, Group, History }

    // 维护可搜索的地面入点，按地面规则筛选并用统一误差选出最小值。
    internal sealed class GroundPoseSearch
    {
        // 保存冻结轨道及其可入场的连续采样区间，结束索引不包含在内。
        private readonly struct Entry
        {
            internal readonly CharacterMotionTrack Track;
            internal readonly CharacterAnimationBinding Binding;
            internal readonly int SampleCount, First, End;

            // 按原采样时刻裁剪循环末帧、入场时间窗和收步制动截止点。
            internal Entry(CharacterMotionTrack track, CharacterAnimationBinding binding)
            {
                Track = track;
                Binding = binding;
                SampleCount = track.SearchSamplesValid ? track.Poses.Length - (track.IsLoop ? 1 : 0) : 0;
                int first = 0, end = SampleCount;
                while (first < end && OutsideWindow(track, first)) first++;
                while (end > first && OutsideWindow(track, end - 1)) end--;
                First = first;
                End = end;
            }

            // 以原有秒数容差判断指定采样是否越过入场边界。
            private static bool OutsideWindow(CharacterMotionTrack track, int index)
            {
                float time = track.TimeAt(index);
                return time < track.FirstEntrySeconds || time > track.LastEntrySeconds + 0.00001f ||
                    track.IsStop && time >= track.BrakeSeconds;
            }
        }

        private readonly CharacterAnimationResourceCatalog resources;
        private readonly List<Entry> entries = new List<Entry>();
        private readonly Dictionary<(GroundMotionUse use, GroundTrackGait from, GroundTrackGait target, GroundTrackGait alternate), int[]>
            candidates = new Dictionary<(GroundMotionUse, GroundTrackGait, GroundTrackGait, GroundTrackGait), int[]>();
        private static readonly Unity.Profiling.ProfilerMarker marker = new Unity.Profiling.ProfilerMarker("3C.Animation.PoseSearch");
#if UNITY_EDITOR
        internal CharacterMatchDebugFrame DebugFrame;
#endif

        // 绑定本次角色装配用来核对素材和倍率的资源索引。
        internal GroundPoseSearch(CharacterAnimationResourceCatalog resources) => this.resources = resources;

        // 按配置顺序登记候选以维持平分时的稳定顺序。
        internal void Add(CharacterMotionTrack track, CharacterAnimationBinding binding) => entries.Add(new Entry(track, binding));

        // 装配完成时按原登记顺序为各用途与步态组合建立候选列表，后续查询只读这些列表。
        internal void BuildIndex()
        {
            if (entries.Count == 0) return;
            var uses = (GroundMotionUse[])Enum.GetValues(typeof(GroundMotionUse));
            var gaits = (GroundTrackGait[])Enum.GetValues(typeof(GroundTrackGait));
            var matches = new List<int>(entries.Count);
            foreach (var use in uses)
            foreach (var from in gaits)
            foreach (var target in gaits)
            foreach (var alternate in gaits)
            {
                matches.Clear();
                var query = new GroundPoseQuery(use, target, 0f, from: from, alternate: alternate);
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry.First < entry.End && query.AllowsUse(entry.Track) && query.AllowsGait(entry.Track))
                        matches.Add(i);
                }
                candidates[(use, from, target, alternate)] = matches.Count == 0 ? Array.Empty<int>() : matches.ToArray();
            }
        }

        // 检查轨道的素材、用途、步态、入场速度与收步承重。
        internal GroundMatchFilter Filter(CharacterMotionTrack track, int hash, in GroundPoseQuery query, GroundTrackSide support,
            bool indexed = false)
        {
            if (!query.Selection.Allows(track)) return GroundMatchFilter.Group;
            if (!indexed && !query.AllowsUse(track)) return GroundMatchFilter.Use;
            if (!track.SearchSamplesValid || !resources.HasGroundTrack(hash, track)) return GroundMatchFilter.Resource;
            if (!indexed && !query.AllowsGait(track)) return GroundMatchFilter.Gait;
            if (query.EntrySpeed < track.MinimumSpeed || track.MaximumSpeed > 0f && query.EntrySpeed > track.MaximumSpeed)
                return GroundMatchFilter.Speed;
            return track.IsStop && support != GroundTrackSide.Either && track.Side != support ? GroundMatchFilter.Support : GroundMatchFilter.None;
        }

        // 用当前轨道所在库的输入计算续播相位，不扫描其他入点。
        internal bool TryScoreCurrent(CharacterMotionTrack track, CharacterClipProgress current,
            in CharacterAnimationPlaybackPlan last, in GroundSearchFrame frame, in GroundPoseQuery query, out float total)
        {
            total = 0f;
            if (!frame.TryLibrary(track.LibraryIndex, out var currentFrame, out var reason, out int channel))
            {
#if UNITY_EDITOR
                CharacterMotionMatchDiagnostics.ContinuingLibraryUnavailable(DebugFrame, track, reason, channel);
#endif
                return false;
            }
            var cost = currentFrame.ScoreCurrent(track, current.normalizedTime, resources.Pace(current.fullPathHash),
                out float rate, out float time, last.StopEntryTime, last.StopEntryTravelPhase);
            cost.Entry = query.EntryPenalty(track);
            total = cost.Total;
#if UNITY_EDITOR
            CharacterMotionMatchDiagnostics.SetContinuing(DebugFrame, track, last.FullBody.Binding.StateName, currentFrame, time, rate, cost);
#endif
            return true;
        }

        // 按配置逐库尝试或统一扫描，跳过当前范围没有组候选的库。
        internal CharacterMatchResult FindScope(ref GroundPoseQuery query, ref GroundSearchFrame frame,
            out CharacterMotionTrack winner, bool record)
        {
            var selected = query.Selection;
#if UNITY_EDITOR
            var debug = record ? DebugFrame : null;
            debug?.Trace.BeginScope(selected);
            if (debug != null && selected.Count == 0)
                debug.Trace.RejectScope(selected, "没有可尝试的有效库", listLibraries: false);
#endif
            if (selected.UnionBlocked && !selected.Relaxed)
            {
#if UNITY_EDITOR
                debug?.Trace.RejectScope(selected, $"前置不兼容 · {selected.Scope.UnionIssue}");
#endif
                winner = null;
                return new CharacterMatchResult { Cost = float.PositiveInfinity };
            }
            winner = null;
            var result = new CharacterMatchResult { Cost = float.PositiveInfinity };
#if UNITY_EDITOR
            if (debug != null && !selected.Ordered) debug.Trace.RejectEmptyUnionSources(selected);
#endif
            for (int i = 0; i < selected.Count; i++)
            {
                int index = selected.Scope.Indices[i];
                if (!selected.Scope.AllowsLibrary(index, selected.Relaxed))
                {
#if UNITY_EDITOR
                    if (debug != null && selected.Ordered)
                        debug.Trace.RejectSource(selected, index, "前置不合格 · 严格范围无候选组");
#endif
                    continue;
                }
#if UNITY_EDITOR
                if (debug != null && selected.Ordered) debug.Trace.BeginOrderedSource(selected, i, index);
#endif
                if (!frame.TryLibrary(index, out var scoring, out var reason, out int channel))
                {
#if UNITY_EDITOR
                    if (debug != null)
                    {
                        debug.Trace.RejectSource(selected, index, $"前置不兼容 · {reason}", channel);
                        if (!selected.Ordered)
                            debug.Trace.RejectScope(selected, "联合评分的输入库不可用，未开始扫描");
                    }
#endif
                    if (!selected.Ordered) break;
                    continue;
                }
                var attempt = selected.Ordered ? new GroundPoseQuery(query, selected.ForLibrary(index)) : query;
                result = Find(attempt, ref scoring, out winner, record);
                if (winner == null && selected.Ordered) continue;
#if UNITY_EDITOR
                if (debug != null && selected.Ordered) debug.Trace.SkipRemainingSources(selected, i + 1);
#endif
                query = attempt;
                frame = scoring;
                break;
            }
            return result;
        }

        // 相同分数按规则声明的库顺序决胜，同库保留先扫描的轨道与入点。
        internal static bool Precedes(float candidateCost, int candidateRank, float existingCost, int existingRank) =>
            candidateCost < existingCost || candidateCost == existingCost && candidateRank < existingRank;

        // 比较所有满足轨道、入点窗和承重脚要求的采样。
        internal CharacterMatchResult Find(in GroundPoseQuery query, ref GroundSearchFrame frame,
            out CharacterMotionTrack winner, bool record)
        {
            var result = Scan(query, frame, out winner, out bool missingHistory, record);
            if (winner != null || !missingHistory) return result;
            frame = frame.WithoutHistory();
            return Scan(query, frame, out winner, out _, record, historyRetry: true);
        }

        // 使用相同有效维度扫描整组入点，单个非循环入点不能自行丢掉缺失的历史项。
        private CharacterMatchResult Scan(in GroundPoseQuery query, in GroundSearchFrame frame,
            out CharacterMotionTrack winner, out bool missingHistory, bool record, bool historyRetry = false)
        {
            missingHistory = false;
            using var sample = marker.Auto();
#if UNITY_EDITOR
            var debug = record ? DebugFrame : null;
            debug?.BeginScan(frame);
#endif
            var best = new CharacterMatchResult { Cost = float.PositiveInfinity };
            winner = null;
            int bestRank = -1;
            var support = frame.Feet.Support;
            // 详细诊断需要观察所有淘汰原因；未收录的枚举值也沿用完整扫描。
            int[] eligible = null;
#if UNITY_EDITOR
            if (debug == null)
#endif
                candidates.TryGetValue((query.Use, query.From, query.Target, query.Alternate), out eligible);
            int trackCount = eligible?.Length ?? entries.Count;
            for (int n = 0; n < trackCount; n++)
            {
                var entry = entries[eligible == null ? n : eligible[n]];
                var track = entry.Track;
                var binding = entry.Binding;
                if (!query.Selection.InSearchedLibrary(track.LibraryIndex)) continue;
#if UNITY_EDITOR
                if (debug != null && !query.Selection.Scope.AllowsLibrary(track.LibraryIndex, query.Selection.Relaxed)) continue;
#endif
                var filter = Filter(track, binding.StateHash, query, support, indexed: eligible != null);
                if (filter != GroundMatchFilter.None)
                {
#if UNITY_EDITOR
                    debug?.Reject(filter, track.LibraryIndex);
#endif
                    continue;
                }
                int rank = query.Selection.Scope.Rank(track.LibraryIndex);
                var pace = resources.Pace(binding.StateHash);
                int first = eligible == null ? 0 : entry.First;
                int end = eligible == null ? entry.SampleCount : entry.End;
                for (int i = first; i < end; i++)
                {
                    var side = CharacterFootObservation.SupportOf(track.Poses[i].Contact);
                    if (i < entry.First || i >= entry.End) filter = GroundMatchFilter.Window;
                    else if (track.IsStop && support != GroundTrackSide.Either && side != GroundTrackSide.Either && side != support)
                        filter = GroundMatchFilter.Support;
                    else filter = GroundMatchFilter.None;
                    if (filter != GroundMatchFilter.None)
                    {
#if UNITY_EDITOR
                        debug?.Reject(filter, track.LibraryIndex);
#endif
                        continue;
                    }
                    float time = track.TimeAt(i);
                    var cost = frame.Score(track, i, pace, out float rate);
                    if (cost.MissingHistory)
                    {
                        missingHistory = true;
#if UNITY_EDITOR
                        debug?.Reject(GroundMatchFilter.History, track.LibraryIndex);
#endif
                        continue;
                    }
                    cost.Entry = query.EntryPenalty(track);
#if UNITY_EDITOR
                    if (debug != null)
                        debug.Candidate(new CharacterMatchDebugEntry { State = binding.StateName, Library = track.LibraryLabel,
                            Track = track.TrackLabel, Time = time, Cost = cost, Rate = rate }, track.LibraryIndex, rank);
#endif
                    if (!Precedes(cost.Total, rank, best.Cost, bestRank)) continue;
                    bestRank = rank;
                    winner = track;
                    best = new CharacterMatchResult
                    {
                        Cost = cost.Total, Components = cost, Rate = rate, Binding = binding, Time = time,
                        Length = track.Seconds, Choice = track.IsStop ? CharacterMatchChoice.Stop : CharacterMatchChoice.Restart
                    };
                }
            }
#if UNITY_EDITOR
            debug?.Trace.RecordScan(debug, query.Selection, best, winner, historyRetry);
#endif
            return best;
        }
    }
}
