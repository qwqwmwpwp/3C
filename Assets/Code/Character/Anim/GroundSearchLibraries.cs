using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 在主线程冻结角色显式登记的搜索库及独立播放身份，保留逐库拒绝原因。
    internal sealed class GroundSearchLibraries
    {
        internal const string NoDeclaredLibraries = "地面匹配已启用，但“角色地面库绑定”为空，将使用常规步态规则。";
#if UNITY_EDITOR
        internal static Func<CharacterMotionDatabase, IReadOnlyDictionary<string, string>> EditorBakeIssues;
        private readonly IReadOnlyDictionary<string, string>[] bakeIssues;
#endif
        private static int revision;
        internal static int Revision => System.Threading.Volatile.Read(ref revision);
        private readonly int boundRevision;
        private readonly GroundLibraryBindings bindingAsset;
        private readonly Dictionary<string, int> libraryIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string>[] groupBindings;
        internal readonly string[] LibraryKeys;
        internal readonly string BindingIssue;
        private readonly BindingStamp[] stamps;
        private readonly BindingStamp primaryStamp;
        private readonly CharacterMotionDatabase primary;
        internal readonly CharacterMotionDatabase[] Databases;
        internal readonly GroundFeatureLayout[] Layouts;
        internal readonly string[] Names, Issues;
        internal readonly int[] TrackCounts;
        internal readonly GroundFeatureLayout Layout;
        internal readonly string Issue;
        internal readonly List<GroundLibraryTrack> Tracks = new List<GroundLibraryTrack>();
        private readonly Dictionary<string, int>[] keys;
        private readonly Dictionary<string, int>[] groupIndices;

        // 记录来源组与发布库的结构身份，引用改变时重装角色。
        private readonly struct BindingStamp
        {
            private readonly Avatar avatar, bakedAvatar;
            private readonly CharacterMotionTrack[] tracks;
            private readonly CharacterClipState[] states;
            private readonly CharacterClipCurves[] curves;
            private readonly CharacterClipLayer[] layers;
            private readonly CharacterMotionGroups groups;
            private readonly GroundAnimationGroup[] groupDefinitions;
            private readonly int format;

            // 记录发布数据的不可变数组及其所属资源。
            internal BindingStamp(CharacterMotionDatabase db)
            {
                avatar = db ? db.Avatar : null;
                bakedAvatar = db ? db.BakedAvatar : null;
                tracks = db ? db.BakedTracks : null;
                states = db ? db.Playback?.States : null;
                curves = db ? db.Playback?.Clips : null;
                layers = db ? db.Playback?.Layers : null;
                groups = db ? db.Groups : null;
                groupDefinitions = groups ? groups.Groups : null;
                format = db ? db.Format : -1;
            }

            // 检查组定义、发布数组、资产引用或 Avatar 是否已更换。
            internal bool Current(CharacterMotionDatabase db) => db ? db.Format == format &&
                db.Avatar == avatar && db.BakedAvatar == bakedAvatar && db.Groups == groups && db.BakedTracks == tracks &&
                (groups ? groups.Groups : null) == groupDefinitions && db.Playback?.States == states &&
                db.Playback?.Layers == layers && db.Playback?.Clips == curves : format == -1;
        }

        // 编辑器在源配方、导入素材或表配置变化时通知角色重新冻结资源。
        internal static void InvalidateBindings() => System.Threading.Interlocked.Increment(ref revision);

        // 核对显式库引用与顺序，未声明和空列表都对应零库的快照。
        internal bool Current(GroundLibraryBindings bindings)
        {
            if (boundRevision != Revision || bindingAsset != bindings || !primaryStamp.Current(primary)) return false;
            var declared = bindings ? bindings.Libraries : null;
            if ((declared?.Length ?? 0) != Databases.Length) return false;
            for (int i = 0; i < Databases.Length; i++)
                if (Databases[i] != declared[i]?.Database || !stamps[i].Current(Databases[i])) return false;
            return true;
        }

        // 校验显式登记库的组身份、Avatar、烘焙状态和评分布局，保留原声明索引。
        internal GroundSearchLibraries(CharacterMotionDatabase primary, GroundLibraryBindings bindings, bool snapshotSamples = true)
        {
            this.primary = primary;
            primaryStamp = new BindingStamp(primary);
            bindingAsset = bindings;
            var declared = bindings && bindings.Libraries != null ? bindings.Libraries : Array.Empty<GroundLibraryBinding>();
            Databases = Array.ConvertAll(declared, entry => entry?.Database);
            LibraryKeys = Array.ConvertAll(declared, entry => entry?.Key);
            int count = Databases.Length;
            boundRevision = Revision;
            groupBindings = new Dictionary<string, string>[count];
            BindingIssue = IndexBindings(declared);
            stamps = new BindingStamp[count];
#if UNITY_EDITOR
            bakeIssues = new IReadOnlyDictionary<string, string>[count];
#endif
            Names = new string[count];
            Issues = new string[count];
            TrackCounts = new int[count];
            Layouts = new GroundFeatureLayout[count];
            keys = new Dictionary<string, int>[count];
            groupIndices = new Dictionary<string, int>[count];
            var used = new HashSet<int> { 0 };
            foreach (var state in primary ? primary.Playback?.States ?? Array.Empty<CharacterClipState>() : Array.Empty<CharacterClipState>())
                if (!string.IsNullOrWhiteSpace(state?.Path)) used.Add(Animator.StringToHash(state.Path));
            var seen = new HashSet<CharacterMotionDatabase>();
            for (int i = 0; i < count; i++)
            {
                var db = Databases[i];
                stamps[i] = new BindingStamp(db);
                Names[i] = $"[{i}] {LibraryKeys[i]} → {(db ? db.name : "未配置")}";
                Issues[i] = !db ? "未配置数据库" : !seen.Add(db) ? "重复登记同一数据库" : null;
                if (Issues[i] != null) continue;
                if (db.Groups) groupIndices[i] = db.Groups.IndexGroups(out Issues[i]);
                Issues[i] ??= db.ValidatePlayback() ??
                    (!primary || db.BakedAvatar != primary.BakedAvatar ? "与角色播放库的烘焙 Avatar 不同" : null);
                if (Issues[i] != null) continue;
#if UNITY_EDITOR
                bakeIssues[i] = EditorBakeIssues?.Invoke(db);
                if (bakeIssues[i] != null && bakeIssues[i].TryGetValue(string.Empty, out var stale))
                { Issues[i] = stale; continue; }
#endif
                var eligible = Array.FindAll(db.BakedTracks, t => BoundTrackIssue(i, t) == null);
                if (eligible.Length == 0)
                { Issues[i] = "没有可用轨道：" + (db.BakedTracks.Length > 0 ? BoundTrackIssue(i, db.BakedTracks[0]) : "未烘焙候选"); continue; }
                Layouts[i] = new GroundFeatureLayout(db.Groups.Schema, eligible);
                if (!Layouts[i].Active && db.Groups.Schema && db.Groups.Schema.Channels?.Length > 0)
                { Issues[i] = Layouts[i].Status; continue; }
                if (Layout == null || db == primary) Layout = Layouts[i];
            }
            var notes = new List<string>();
            for (int i = 0; i < count; i++)
            {
                if (Issues[i] == null) Register(i, used, notes, snapshotSamples);
                if (Issues[i] != null) notes.Add(Names[i] + " " + Issues[i]);
            }
            Issue = notes.Count == 0 ? null : string.Join("；", notes);
        }

        // 固定逻辑库和组的对应关系，在角色装配前拒绝无法区分的声明。
        private string IndexBindings(GroundLibraryBinding[] entries)
        {
            var issues = new List<string>();
            var databases = new HashSet<CharacterMotionDatabase>();
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                string key = entry?.Key;
                if (string.IsNullOrWhiteSpace(key)) issues.Add($"第 {i + 1} 个地面库绑定缺少逻辑库键");
                else if (libraryIndices.ContainsKey(key)) issues.Add($"重复的逻辑库键 {key}");
                else libraryIndices.Add(key, i);
                if (entry != null && entry.Database && !databases.Add(entry.Database)) issues.Add($"逻辑库 {key} 重复绑定数据库 {entry.Database.name}");
                var groupMap = new Dictionary<string, string>(StringComparer.Ordinal);
                groupBindings[i] = groupMap;
                foreach (var group in entry?.Groups ?? Array.Empty<GroundGroupBinding>())
                {
                    if (string.IsNullOrWhiteSpace(group?.Key)) issues.Add($"库 {key} 的组绑定缺少逻辑组键");
                    else if (groupMap.ContainsKey(group.Key)) issues.Add($"库 {key} 内重复的逻辑组键 {group.Key}");
                    else groupMap.Add(group.Key, group.GroupId);
                }
            }
            return issues.Count == 0 ? null : string.Join("；", issues);
        }

        // 从角色绑定解析逻辑库下标，保留作者在绑定资源中设置的顺序。
        internal int FindLibrary(string key) =>
            !string.IsNullOrWhiteSpace(key) && libraryIndices.TryGetValue(key, out int index) ? index : -1;

        // 仅在本逻辑库内解析角色绑定的稳定组 ID，并定位缺失的绑定或来源组。
        internal GroundAnimationGroup FindGroup(int libraryIndex, string key, out string issue)
        {
            if (string.IsNullOrWhiteSpace(key) || !groupBindings[libraryIndex].TryGetValue(key, out string groupId))
                issue = $"缺少逻辑组绑定 {key}";
            else if (groupId == null || groupIndices[libraryIndex] == null ||
                !groupIndices[libraryIndex].TryGetValue(groupId, out int index))
                issue = $"逻辑组 {key} 的库内组 ID 无效或已失效（{groupId}）";
            else
            {
                issue = null;
                return Databases[libraryIndex].Groups.Groups[index];
            }
            return null;
        }

#if UNITY_EDITOR
        // 编辑器也检查当前规则尚未使用的组映射，便于提前发现过期的稳定 ID。
        internal IEnumerable<string> GroupBindingIssues()
        {
            for (int i = 0; i < groupBindings.Length; i++)
                foreach (string key in groupBindings[i].Keys)
                    if (FindGroup(i, key, out string issue) == null) yield return $"{Names[i]}：{issue}";
        }
#endif

        // 校验同一轮候选的通道、单位、历史和未来时窗、权重、尺度及有效维度。
        internal static string ComparisonIssue(GroundFeatureLayout a, GroundFeatureLayout b)
        {
            if (a == null || b == null) return "缺少可用的评分布局";
            if (a.Active != b.Active || a.Available != b.Available || a.Fingerprint != b.Fingerprint ||
                a.LegacyPelvis != b.LegacyPelvis || a.Windows != b.Windows || a.Channels.Length != b.Channels.Length)
                return "评分通道、有效维度或采样时间窗与参考库不同";
            for (int i = 0; i < a.Channels.Length; i++)
            {
                var x = a.Channels[i];
                var y = b.Channels[i];
                if (x.Group != y.Group || x.Weight != y.Weight || x.Scale != y.Scale)
                    return $"评分通道 {i} 的分组、权重或尺度与参考库不同";
            }
            return null;
        }

        // 在编辑器核对源资产指纹；Player 使用发布快照与实际播放素材核验。
        private string BoundTrackIssue(int index, CharacterMotionTrack track)
        {
#if UNITY_EDITOR
            if (track?.States?.Length > 0 && !string.IsNullOrEmpty(track.States[0]) && bakeIssues[index] != null &&
                bakeIssues[index].TryGetValue(track.States[0], out string issue)) return issue;
#endif
            return TrackIssue(Databases[index], track);
        }

        // 将播放路径解析为实际库内的素材和烘焙轨道，返回首个不可用原因。
        internal static string TrackIssue(CharacterMotionDatabase db, CharacterMotionTrack track)
        {
            if (track == null || track.States == null || track.States.Length == 0 || string.IsNullOrWhiteSpace(track.States[0]))
                return "缺少候选状态路径";
            if (!track.Clip) return "缺少动画片段";
            if (string.IsNullOrEmpty(track.SampleSourceHash)) return "缺少源采样指纹，请重新烘焙";
            if (!track.ReadyForSearch) return "采样、时长、入场窗或参考轨迹失效";
            if (!db || db.Playback?.States == null) return "缺少播放索引";
            foreach (var state in db.Playback.States)
                if (state != null && state.Path == track.States[0])
                    return PlaybackIssue(db, track, state);
            return "轨道缺少本库播放绑定";
        }

        // 用作者配置的组与变体核对播放层、两路素材以及整套混合配方。
        internal static string PlaybackIssue(CharacterMotionDatabase db, CharacterMotionTrack track, CharacterClipState state)
        {
            if (state.Layer != 0) return "地面轨道未绑定至下身基础层";
            if (!(state.Speed > 0f && state.Speed < float.PositiveInfinity) || !state.MatchesTrack(track, state.Clip))
                return "播放倍率、素材或混合配方与烘焙轨道不同";
            if (!db.Groups) return "缺少源动作组";
            foreach (var group in db.Groups.Groups ?? Array.Empty<GroundAnimationGroup>())
            {
                if (group == null) continue;
                foreach (var variant in group.Variants ?? Array.Empty<CharacterMotionTrack>())
                {
                    if (variant?.States == null || variant.States.Length == 0 || variant.States[0] != track.States[0]) continue;
                    if (Array.IndexOf(variant.States, state.Path) < 0) return "此状态路径不在源变体内";
                    if (state.Clip != group.First || state.Blend.Second != group.Second) return "真实播放素材与源组不同";
                    if (!group.Second) return group.FirstRate == 1f ? null : "单片段源组倍率无效";
                    var blend = state.Blend;
                    if (!blend.Weights.Equals(group.Weights) || blend.FirstRate != group.FirstRate || blend.SecondRate != group.SecondRate ||
                        blend.FirstPhase != variant.FirstPhase || blend.SecondPhase != variant.SecondPhase ||
                        blend.Seconds != variant.Seconds || blend.Loop != variant.Loop)
                        return "真实混合权重、倍率、时长、相位或循环与源变体不同";
                    return null;
                }
            }
            return "缺少此轨道对应的源组变体";
        }

        // 冻结有效候选及其全部合法播放别名，拒绝重复的轨道身份。
        private void Register(int index, HashSet<int> used, List<string> notes, bool snapshotSamples)
        {
            var db = Databases[index];
            var paths = new Dictionary<string, int>(StringComparer.Ordinal);
            var stateByPath = new Dictionary<string, CharacterClipState>(StringComparer.Ordinal);
            foreach (var state in db.Playback.States)
            {
                if (string.IsNullOrWhiteSpace(state?.Path)) continue;
                if (stateByPath.ContainsKey(state.Path))
                { Issues[index] = $"播放路径 {state.Path} 重复"; return; }
                stateByPath.Add(state.Path, state);
                int hash = db == primary ? Animator.StringToHash(state.Path) :
                    Animator.StringToHash(db.GetInstanceID().ToString() + "/" + state.Path);
                if (db != primary)
                    while (!used.Add(hash)) hash = unchecked(hash + 1);
                paths.Add(state.Path, hash);
            }
            keys[index] = paths;
            var claimed = new HashSet<int>();
            for (int n = 0; n < db.BakedTracks.Length; n++)
            {
                var source = db.BakedTracks[n];
                string issue = BoundTrackIssue(index, source);
                string label = $"轨道 #{n + 1} · {(source?.States?.Length > 0 ? source.States[0] : "缺少路径")} · {(source?.Clip ? source.Clip.name : "缺少片段")}";
                if (issue != null) { notes.Add($"{Names[index]} {label}：{issue}"); continue; }
                var aliases = new List<int>();
                foreach (string path in source.States)
                {
                    if (string.IsNullOrWhiteSpace(path) || !stateByPath.TryGetValue(path, out var state) ||
                        PlaybackIssue(db, source, state) != null || claimed.Contains(paths[path]) || aliases.Contains(paths[path]))
                    { notes.Add($"{Names[index]} {label}：别名 {path} 素材不符、重复或缺少绑定"); continue; }
                    aliases.Add(paths[path]);
                }
                if (aliases.Count == 0 || aliases[0] != paths[source.States[0]]) continue;
                foreach (int key in aliases) claimed.Add(key);
                if (snapshotSamples) Tracks.Add(new GroundLibraryTrack(index, Names[index], label, source, aliases.ToArray()));
                TrackCounts[index]++;
            }
        }

        // 按原库路径查询冻结时分配的播放索引。
        internal bool TryKey(int index, string path, out int hash)
        {
            hash = 0;
            return keys[index] != null && keys[index].TryGetValue(path, out hash);
        }
    }

    // 保存主线程验证过的源素材、独立轨道快照和库内有效别名。
    internal sealed class GroundLibraryTrack
    {
        internal readonly CharacterMotionTrack Source, Frozen;
        internal readonly int[] Hashes;

        // 深拷贝后台读取的采样、路径和特征，剥离 Unity 素材引用。
        internal GroundLibraryTrack(int index, string library, string label, CharacterMotionTrack source, int[] hashes)
        {
            Source = source;
            Hashes = hashes;
            Frozen = source.CopyForBake();
            Frozen.Poses = (CharacterFootPose[])source.Poses.Clone();
            Frozen.RootPath = (GroundPathKey[])source.RootPath?.Clone();
            Frozen.FeatureValues = (Vector3[])source.FeatureValues?.Clone();
            Frozen.Clip = null;
            Frozen.LibraryIndex = index;
            Frozen.LibraryLabel = library;
            Frozen.TrackLabel = label;
        }

        // 给选中的库内轨道创建播放绑定，外部库的步相来自自身的烘焙采样。
        internal CharacterAnimationBinding Bind(CharacterAnimationConfiguration config, CharacterAnimationResourceCatalog resources)
        {
            var track = Frozen;
            string stateName = track.States[0];
            int key = Hashes[0];
            var side = track.Side == GroundTrackSide.Left;
            if (track.IsStop)
                return CharacterAnimationBinding.Stop(stateName, side ? CharacterLocomotionVariant.StopLeft :
                    CharacterLocomotionVariant.StopRight, config.Transition, track.BrakeSeconds, track, key);
            var variant = track.Use == GroundMotionUse.Start ? CharacterLocomotionVariant.Start :
                track.Use == GroundMotionUse.Turn ? (side ? CharacterLocomotionVariant.TurnLeft :
                    CharacterLocomotionVariant.TurnRight) : CharacterLocomotionVariant.None;
            resources.TryGetPhase(key, out var phase);
            return new CharacterAnimationBinding(new CharacterAnimationActionSettings
            { StateName = stateName, StateTransitionDuration = track.Blend }, variant, phase, playbackKey: key, source: track);
        }
    }
}
