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
        internal readonly GroundFeatureGroups Groups;
        internal readonly bool AllowPast, LegacyPelvis;
#if UNITY_EDITOR
        private readonly bool captureCosts;
#endif

        // 固定一次搜索及其交还、续播比较所使用的输入。
        internal GroundSearchFrame(in CharacterFootObservation feet, in CharacterMatchTrajectory goal,
            GroundMatchCost profile, float speed, float startSpeed, CharacterGroundTrajectory trajectory, float clockScale,
            GroundFeatureQuery features, GroundFeatureGroups groups, bool allowPast, bool legacyPelvis
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
            Features = features;
            Groups = groups;
            AllowPast = allowPast;
            LegacyPelvis = legacyPelvis;
            Trajectory = trajectory;
#if UNITY_EDITOR
            this.captureCosts = captureCosts;
#endif
        }

        // 所有合法入点均缺少前史时，统一移除过去通道，再比较整组及当前续播。
        internal GroundSearchFrame WithoutHistory() => new GroundSearchFrame(Feet, Goal, Profile, Speed, StartSpeed,
            Trajectory, ClockScale, Features, Groups, false, LegacyPelvis
#if UNITY_EDITOR
            , captureCosts
#endif
            );

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

        // 比较所有满足轨道、入点窗和承重脚要求的采样。
        internal CharacterMatchResult Find(in GroundPoseQuery query, ref GroundSearchFrame frame,
            out CharacterMotionTrack winner, bool record)
        {
            var result = Scan(query, frame, out winner, out bool missingHistory, record);
            if (winner != null || !missingHistory) return result;
            frame = frame.WithoutHistory();
            return Scan(query, frame, out winner, out _, record);
        }

        // 使用相同有效维度扫描整组入点，单个非循环入点不能自行丢掉缺失的历史项。
        private CharacterMatchResult Scan(in GroundPoseQuery query, in GroundSearchFrame frame,
            out CharacterMotionTrack winner, out bool missingHistory, bool record)
        {
            missingHistory = false;
            using var sample = marker.Auto();
#if UNITY_EDITOR
            if (record) DebugFrame?.StartScoring(frame);
#endif
            var best = new CharacterMatchResult { Cost = float.PositiveInfinity };
            winner = null;
            var support = frame.Feet.Support;
            // 详细诊断需要观察所有淘汰原因；未收录的枚举值也沿用完整扫描。
            int[] eligible = null;
#if UNITY_EDITOR
            if (!record || DebugFrame == null)
#endif
                candidates.TryGetValue((query.Use, query.From, query.Target, query.Alternate), out eligible);
            int trackCount = eligible?.Length ?? entries.Count;
            for (int n = 0; n < trackCount; n++)
            {
                var entry = entries[eligible == null ? n : eligible[n]];
                var track = entry.Track;
                var binding = entry.Binding;
                var filter = Filter(track, binding.StateHash, query, support, indexed: eligible != null);
                if (filter != GroundMatchFilter.None)
                {
#if UNITY_EDITOR
                    if (record) DebugFrame?.Reject(filter);
#endif
                    continue;
                }
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
                        if (record) DebugFrame?.Reject(filter);
#endif
                        continue;
                    }
                    float time = track.TimeAt(i);
                    var cost = frame.Score(track, i, pace, out float rate);
                    if (cost.MissingHistory)
                    {
                        missingHistory = true;
#if UNITY_EDITOR
                        if (record) DebugFrame?.Reject(GroundMatchFilter.History);
#endif
                        continue;
                    }
                    cost.Entry = query.EntryPenalty(track);
#if UNITY_EDITOR
                    if (record && DebugFrame != null)
                        DebugFrame.Candidate(new CharacterMatchDebugEntry { State = binding.StateName, Time = time, Cost = cost, Rate = rate });
#endif
                    if (!(cost.Total < best.Cost)) continue;
                    winner = track;
                    best = new CharacterMatchResult
                    {
                        Cost = cost.Total, Components = cost, Rate = rate, Binding = binding, Time = time,
                        Length = track.Seconds, Choice = track.IsStop ? CharacterMatchChoice.Stop : CharacterMatchChoice.Restart
                    };
                }
            }
            return best;
        }
    }
}
