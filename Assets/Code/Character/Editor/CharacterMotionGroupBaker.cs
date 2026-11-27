using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 根据基础动画数据组构建唯一的播放库和地面匹配样本。
    internal static class CharacterMotionGroupBaker
    {
        private const string SampleVersion = "group/4"; // 增加相对模型根的骨盆采样。
        private const int LowerBodyLayer = 0;

        // 保存完整的烘焙结果，供一次性应用。
        internal readonly struct Result
        {
            public readonly CharacterClipLibrary Playback;
            public readonly CharacterMotionTrack[] Tracks;
            public readonly int Resampled;
            private readonly List<string> warnings;

            // 保存一次编译得到的发布数据和质量提示。
            public Result(CharacterClipLibrary playback, CharacterMotionTrack[] tracks, int resampled, List<string> warnings)
            {
                Playback = playback;
                Tracks = tracks;
                Resampled = resampled;
                this.warnings = warnings;
            }

            // 写入同一次烘焙的播放库、轨道和采样 Avatar。
            public void Apply(CharacterMotionDatabase db)
            {
                db.Playback = Playback;
                db.BakedTracks = Tracks;
                db.BakedAvatar = db.Avatar;
                db.Format = CharacterMotionDatabase.CurrentFormat;
            }

            // 在发布成功后报告需要视觉复核的轨道。
            public void ReportQuality(CharacterMotionDatabase db)
            {
                if (warnings.Count != 0) Debug.LogWarning($"{db.name}：地面轨道质量提示\n" + string.Join("\n", warnings), db);
            }
        }

        // 编译播放库与匹配轨道，并在采样成功后保存资源。
        public static void Bake(CharacterMotionDatabase db, bool changedOnly)
        {
            var result = Compile(db, changedOnly);
            Undo.RecordObject(db, "重新采样地面动作数据组");
            result.Apply(db);
            EditorUtility.SetDirty(db);
            AssetDatabase.SaveAssetIfDirty(db);
            CharacterSearchLibraryBakeIssues.Refresh();
            Debug.Log($"{db.name}：已校验 {result.Tracks.Length} 条地面轨道，重新采样 {result.Resampled} 条。", db);
            result.ReportQuality(db);
        }

        // 缓存一个素材的编辑器依赖版本和导入曲线。
        private sealed class SourceClip
        {
            public readonly CharacterClipCurves Curves;
            public readonly string Fingerprint;

            // 按同一份项目素材读取依赖版本及曲线。
            public SourceClip(AnimationClip clip)
            {
                Fingerprint = FingerprintOf(clip);
                Curves = CharacterClipLibraryBaker.ReadCurves(clip);
            }
        }

        // 保存采样一条地面轨道所需的素材、混合和设置。
        private sealed class TrackRecipe
        {
            public readonly CharacterMotionTrack Track;
            public readonly CharacterMotionBlend Blend;
            public readonly CharacterClipCurves FirstCurves, SecondCurves;

            // 将验证后的轨道设置与两个素材的曲线关联。
            public TrackRecipe(CharacterMotionTrack track, CharacterMotionBlend blend, SourceClip first, SourceClip second)
            {
                Track = track;
                Blend = blend;
                FirstCurves = first.Curves;
                SecondCurves = second?.Curves;
            }
        }

        // 验证组身份并编译变更轨道，返回尚未写入的完整数据。
        internal static Result Compile(CharacterMotionDatabase db, bool changedOnly = false)
        {
            if (!db.Groups) throw new InvalidOperationException("请选择基础动画数据组。");
            db.Groups.IndexGroups(out string groupIssue);
            if (groupIssue != null) throw new InvalidOperationException(groupIssue);
            if (!db.Avatar || !db.Avatar.isHuman || !db.Avatar.isValid)
                throw new InvalidOperationException("请选择有效的 Humanoid 模型 Avatar。");
            var layout = BakeLayout(db);
            string avatarHash = FingerprintOf(db.Avatar) + layout.Fingerprint;
            var playback = CreatePlayback(db.Groups);
            var sources = new Dictionary<AnimationClip, SourceClip>();
            var states = new Dictionary<int, CharacterClipState>();
            var orderedStates = new List<CharacterClipState>();
            // 拒绝状态哈希冲突，并保存与配置一致的发布顺序。
            void AddState(CharacterClipState state)
            {
                int hash = Animator.StringToHash(state.Path);
                if (hash == 0) throw new InvalidOperationException($"动作标识 {state.Path} 的哈希为保留值 0。");
                if (states.TryGetValue(hash, out var other))
                    throw new InvalidOperationException($"动作标识 {state.Path} 与 {other.Path} 的哈希重复。");
                states.Add(hash, state);
                orderedStates.Add(state);
            }

            foreach (var binding in db.Groups.Bindings ?? Array.Empty<CharacterClipState>())
            {
                if (binding == null || binding.Layer < 0 || binding.Layer >= playback.Layers.Length ||
                    !Finite(binding.Speed) || binding.Speed <= 0f || binding.Blend.Second)
                    throw new InvalidOperationException($"基础动作 {binding?.Path} 的层、速度或片段绑定无效。");
                ValidateStatePath(binding.Path);
                GetSource(binding.Clip, sources, binding.Path);
                AddState(new CharacterClipState { Path = binding.Path, Clip = binding.Clip, Layer = binding.Layer, Speed = binding.Speed });
            }

            var recipes = new List<TrackRecipe>();
            var groupNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in db.Groups.Groups ?? Array.Empty<GroundAnimationGroup>())
            {
                if (group == null || string.IsNullOrWhiteSpace(group.Name) || !groupNames.Add(group.Name.Trim()) ||
                    group.Variants == null || group.Variants.Length == 0)
                    throw new InvalidOperationException($"地面动作组 {group?.Name} 需要唯一名称和至少一个变体。");
                ValidateGroup(group);
                var first = GetSource(group.First, sources, group.Name);
                var second = group.Second ? GetSource(group.Second, sources, group.Name) : null;
                ValidateContact(first, second, group.Name);
                for (int i = 0; i < group.Variants.Length; i++)
                {
                    string label = $"{group.Name} · 变体 {i + 1}";
                    var recipe = CreateRecipe(group, group.Variants[i], first, second, avatarHash, label);
                    foreach (var path in recipe.Track.States)
                    {
                        ValidateStatePath(path);
                        AddState(new CharacterClipState { Path = path, Clip = group.First, Layer = LowerBodyLayer, Blend = recipe.Blend });
                    }
                    recipes.Add(recipe);
                }
            }
            if (recipes.Count == 0) throw new InvalidOperationException("基础动画数据组需要至少一条地面变体。");
            for (int i = 0; i < playback.Layers.Length; i++)
            {
                string path = playback.Layers[i].DefaultState;
                if (!states.TryGetValue(Animator.StringToHash(path), out var state) || state.Path != path || state.Layer != i)
                    throw new InvalidOperationException($"第 {i + 1} 层的默认状态 {path} 需要绑定在本层。");
            }
            playback.States = orderedStates.ToArray();
            var curves = new List<CharacterClipCurves>(sources.Count);
            foreach (var source in sources.Values) curves.Add(source.Curves);
            playback.Clips = curves.ToArray();

            var previous = IndexPrevious(db, changedOnly);
            var warnings = new List<string>();
            int resampled = Sample(db.Avatar, recipes, previous, warnings, layout);
            var tracks = new CharacterMotionTrack[recipes.Count];
            for (int i = 0; i < tracks.Length; i++) tracks[i] = recipes[i].Track;
            return new Result(playback, tracks, resampled, warnings);
        }

        // 检查来源组 ID 并读取源配方指纹，供已发布动作库审计逐状态比对。
        internal static Dictionary<string, string> ReadExpectedHashes(CharacterMotionDatabase db, List<string> errors)
        {
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!db.Groups) return expected;
            db.Groups.IndexGroups(out string groupIssue);
            if (groupIssue != null) errors.Add(groupIssue);
            if (!db.Avatar) return expected;
            string avatarHash;
            try { avatarHash = FingerprintOf(db.Avatar) + BakeLayout(db).Fingerprint; }
            catch (InvalidOperationException error) { errors.Add(error.Message); return expected; }
            var sources = new Dictionary<AnimationClip, SourceClip>();
            foreach (var group in db.Groups.Groups ?? Array.Empty<GroundAnimationGroup>())
            {
                if (group == null || string.IsNullOrWhiteSpace(group.Name) || group.Variants == null || group.Variants.Length == 0)
                {
                    errors.Add($"地面动作组 {group?.Name} 缺少名称或变体。");
                    continue;
                }
                for (int i = 0; i < group.Variants.Length; i++)
                {
                    string label = $"{group.Name} · 变体 {i + 1}";
                    try
                    {
                        var recipe = ReadRecipe(group, group.Variants[i], sources, avatarHash, label);
                        foreach (string path in recipe.Track.States)
                        {
                            ValidateStatePath(path);
                            if (expected.ContainsKey(path)) errors.Add($"源动作状态 {path} 重复。");
                            else expected.Add(path, recipe.Track.SampleSourceHash);
                        }
                    }
                    catch (InvalidOperationException error) { errors.Add($"{label}：{error.Message}"); }
                }
            }
            return expected;
        }

        // 固定烘焙布局并拒绝无法在当前预测或采样接口表达的通道。
        private static GroundFeatureLayout BakeLayout(CharacterMotionDatabase db)
        {
            var layout = new GroundFeatureLayout(db.Groups.Schema);
            if (db.Groups.Schema && !layout.Active) throw new InvalidOperationException(layout.Status);
            return layout;
        }

        // 临时采样选中的当前源变体，并返回轨道、预览姿势和质量提示。
        internal static CharacterMotionPreviewData Preview(CharacterMotionDatabase db, GroundAnimationGroup group, CharacterMotionTrack variant)
        {
            using var stage = new CharacterMotionSamplingStage(db.Avatar);
            var layout = BakeLayout(db);
            var recipe = ReadRecipe(group, variant, new Dictionary<AnimationClip, SourceClip>(), FingerprintOf(db.Avatar) + layout.Fingerprint, group.Name);
            var warnings = new List<string>();
            var frames = CharacterMotionPoseSampler.Sample(stage.Animator, recipe.Track, recipe.Blend,
                recipe.FirstCurves, recipe.SecondCurves, warnings, true, layout);
            CharacterMotionTrackAudit.ValidateSamples(recipe.Track);
            AddQualityWarnings(recipe, warnings);
            return new CharacterMotionPreviewData(recipe.Track, recipe.Blend, frames, warnings);
        }

        // 解析指定地面变体的片段、混合和采样配方。
        private static TrackRecipe ReadRecipe(GroundAnimationGroup group, CharacterMotionTrack variant,
            Dictionary<AnimationClip, SourceClip> sources, string avatarHash, string label)
        {
            ValidateGroup(group);
            var first = GetSource(group.First, sources, group.Name);
            var second = group.Second ? GetSource(group.Second, sources, group.Name) : null;
            ValidateContact(first, second, group.Name);
            return CreateRecipe(group, variant, first, second, avatarHash, label);
        }

        // 构建固定顺序的双层遮罩并检查层设置。
        private static CharacterClipLibrary CreatePlayback(CharacterMotionGroups groups)
        {
            var authored = groups.Layers;
            if (authored?.Length != 2)
                throw new InvalidOperationException("数据组需要按下身、上身排列的两层。");
            var library = new CharacterClipLibrary { Layers = new CharacterClipLayer[authored.Length] };
            for (int i = 0; i < authored.Length; i++)
            {
                var layer = authored[i];
                if (layer == null || !Unit(layer.Weight) || string.IsNullOrWhiteSpace(layer.DefaultState))
                    throw new InvalidOperationException($"第 {i + 1} 层需要默认状态，权重范围为 0 至 1。");
                library.Layers[i] = new CharacterClipLayer
                {
                    Additive = layer.Additive, Mask = layer.Mask, Weight = layer.Weight, DefaultState = layer.DefaultState
                };
            }
            if (!library.HasBodyMasks())
                throw new InvalidOperationException("双层需要使用互补的下身、上身遮罩和覆盖混合。根和双腿归下身，双臂归上身。");
            return library;
        }

        // 校验当前组两个素材的速率和分段混合权重。
        private static void ValidateGroup(GroundAnimationGroup group)
        {
            if (!Finite(group.FirstRate) || group.FirstRate <= 0f ||
                !Finite(group.SecondRate) || group.SecondRate <= 0f ||
                !Unit(group.Weights.x) || !Unit(group.Weights.y) || !Unit(group.Weights.z))
                throw new InvalidOperationException($"{group.Name}：源片段速率必须大于 0，混合权重范围为 0 至 1。");
            if (!group.Second && group.FirstRate != 1f)
                throw new InvalidOperationException($"{group.Name}：使用单片段时，源片段速率请填 1。");
        }

        // 结合组的片段配置与变体参数，生成一次可重用的采样配方。
        private static TrackRecipe CreateRecipe(GroundAnimationGroup group, CharacterMotionTrack variant, SourceClip first,
            SourceClip second, string avatarHash, string label)
        {
            if (variant?.States == null || variant.States.Length == 0)
                throw new InvalidOperationException($"{label}：需要至少一个状态路径。");
            if (second == null && variant.FirstPhase != 0f)
                throw new InvalidOperationException($"{label}：使用单片段时，起始相位请填 0。");
            if (second != null && variant.PathSource == GroundPathSource.RootMotion)
                throw new InvalidOperationException($"{label}：双片段变体需要使用直线或显式参考轨迹。");
            var track = variant.CopyForBake();
            track.Clip = group.First;
            track.Mixed = second != null;
            track.Seconds = track.Mixed ? variant.Seconds : group.First.length;
            track.ScalesWithSpeed = track.IsLoop && !track.FixedCycleSpeed;
            track.Poses = null;
            track.RootPath = null;
            track.FeatureValues = null;
            track.FeatureMask = 0;
            track.FeatureFingerprint = null;
            try { CharacterMotionTrackAudit.ValidateRecipe(track); }
            catch (InvalidOperationException error) { throw new InvalidOperationException($"{label}：{error.Message}", error); }
            var blend = track.Mixed ? new CharacterMotionBlend
            {
                Second = group.Second, Weights = group.Weights, Seconds = track.Seconds, Loop = track.IsLoop,
                FirstPhase = track.FirstPhase, SecondPhase = track.SecondPhase, FirstRate = group.FirstRate, SecondRate = group.SecondRate
            } : default;
            track.SampleSourceHash = SampleHash(avatarHash, first, second, track, blend);
            return new TrackRecipe(track, blend, first, second);
        }

        // 校验素材导入方式与时间参数，并重用同次操作已读取的曲线。
        private static SourceClip GetSource(AnimationClip clip, Dictionary<AnimationClip, SourceClip> sources, string label)
        {
            if (!clip || clip.legacy || !Finite(clip.length) || clip.length <= 0f || !Finite(clip.frameRate) || clip.frameRate <= 0f)
                throw new InvalidOperationException($"{label}：需要有效且有时长的非 Legacy 基础片段。");
            if (!sources.TryGetValue(clip, out var source)) sources.Add(clip, source = new SourceClip(clip));
            return source;
        }

        // 确保混合动作的每只脚至少有一个来源提供有效的接触曲线。
        private static void ValidateContact(SourceClip first, SourceClip second, string label)
        {
            foreach (string name in new[] { CharacterFootSampler.LeftContactCurve, CharacterFootSampler.RightContactCurve })
            {
                var a = first.Curves.Find(name);
                var b = second?.Curves.Find(name);
                if (a == null && b == null) throw new InvalidOperationException($"{label}：基础片段缺少 {name} 曲线。");
                foreach (var entry in new[] { a, b })
                {
                    if (entry == null) continue;
                    foreach (var key in entry.Curve.keys)
                        if (!Finite(key.time) || !Finite(key.value) || !Finite(key.inWeight) || !Finite(key.outWeight) ||
                            float.IsNaN(key.inTangent) || float.IsNaN(key.outTangent))
                            throw new InvalidOperationException($"{label}：足部曲线 {name} 包含无效的关键帧。");
                }
            }
        }

        // 确保动作状态路径的各段可用于稳定查找和发布。
        private static void ValidateStatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path != path.Trim() ||
                Array.Exists(path.Split('.'), segment => string.IsNullOrWhiteSpace(segment) || segment != segment.Trim()))
                throw new InvalidOperationException($"动作标识 {path} 不能为空，也不能有空段或首尾空格。");
        }

        // 按首个状态找到同一 Avatar 下可供增量烘焙复用的轨道。
        private static Dictionary<string, CharacterMotionTrack> IndexPrevious(CharacterMotionDatabase db, bool changedOnly)
        {
            var result = new Dictionary<string, CharacterMotionTrack>(StringComparer.Ordinal);
            if (!changedOnly || db.ValidatePlayback() != null)
                return result;
            foreach (var old in db.BakedTracks)
            {
                if (!(old?.States?.Length > 0) || string.IsNullOrEmpty(old.States[0]) || string.IsNullOrEmpty(old.SampleSourceHash)) continue;
                try { CharacterMotionTrackAudit.ValidateSamples(old); }
                catch (InvalidOperationException) { continue; }
                result[old.States[0]] = old;
            }
            return result;
        }

        // 复用有效的相同版本样本，并在隔离场景中完成其余轨道采样。
        private static int Sample(Avatar avatar, List<TrackRecipe> recipes,
            Dictionary<string, CharacterMotionTrack> previous, List<string> warnings, GroundFeatureLayout layout)
        {
            var pending = new List<TrackRecipe>();
            foreach (var recipe in recipes)
            {
                var track = recipe.Track;
                if (previous.TryGetValue(track.States[0], out var old) && old.SampleSourceHash == track.SampleSourceHash &&
                    (!layout.Active || old.FeatureFingerprint == layout.Fingerprint && old.FeatureValues?.Length == (long)old.Poses.Length * layout.Channels.Length))
                {
                    track.Poses = old.Poses;
                    track.RootPath = old.RootPath;
                    track.FeatureFingerprint = old.FeatureFingerprint;
                    track.FeatureValues = old.FeatureValues;
                    track.FeatureMask = old.FeatureMask;
                }
                else pending.Add(recipe);
            }
            if (pending.Count > 0)
            {
                using var stage = new CharacterMotionSamplingStage(avatar);
                foreach (var recipe in pending)
                {
                    var feedback = new List<string>();
                    try
                    {
                        CharacterMotionPoseSampler.Sample(stage.Animator, recipe.Track, recipe.Blend,
                            recipe.FirstCurves, recipe.SecondCurves, feedback, layout: layout);
                    }
                    catch (Exception error) { throw new InvalidOperationException($"{recipe.Track.States[0]}：{error.Message}", error); }
                    foreach (string warning in feedback) warnings.Add($"{recipe.Track.States[0]}：{warning}");
                }
            }
            foreach (var recipe in recipes)
            {
                try { CharacterMotionTrackAudit.ValidateSamples(recipe.Track); }
                catch (InvalidOperationException error) { throw new InvalidOperationException($"{recipe.Track.States[0]}：{error.Message}", error); }
                var feedback = new List<string>();
                AddQualityWarnings(recipe, feedback);
                foreach (string warning in feedback) warnings.Add($"{recipe.Track.States[0]}：{warning}");
            }
            return pending.Count;
        }

        // 汇总一条轨道的姿势、时序和双源接触质量提示。
        private static void AddQualityWarnings(TrackRecipe recipe, List<string> warnings)
        {
            CharacterMotionTrackAudit.AddQualityWarnings(recipe.Track, warnings);
            CharacterMotionTrackAudit.AddMixedContactWarnings(recipe.Track, recipe.Blend, recipe.FirstCurves, recipe.SecondCurves, warnings);
        }

        // 生成经过版本化的采样指纹，配置和素材的改动都会使旧指纹失效。
        private static string SampleHash(string avatarHash, SourceClip first, SourceClip second, CharacterMotionTrack track, CharacterMotionBlend blend)
        {
            var hash = new StringBuilder($"{SampleVersion}/{avatarHash}/{first.Fingerprint}/{second?.Fingerprint}");
            hash.Append(FormattableString.Invariant($"/{track.Seconds:R}/{track.PathSource}/{track.IsLoop}/{track.Use}/{track.Gait}/{track.FromGait}/{track.Side}/{track.TurnDegrees:R}"));
            hash.Append(FormattableString.Invariant($"/{track.FirstEntrySeconds:R}/{track.LastEntrySeconds:R}/{track.BrakeSeconds:R}/{track.ExitSeconds:R}/{track.MinimumSpeed:R}/{track.MaximumSpeed:R}/{track.ReferenceSpeed:R}/{track.ScalesWithSpeed}/{track.Blend:R}"));
            hash.Append(FormattableString.Invariant($"/{track.FirstPhase:R}/{track.SecondPhase:R}/{blend.FirstRate:R}/{blend.SecondRate:R}/{blend.Weights.x:R}/{blend.Weights.y:R}/{blend.Weights.z:R}"));
            foreach (string state in track.States) hash.Append(FormattableString.Invariant($"/{state?.Length ?? 0}:{state}"));
            foreach (var key in track.ReferencePath ?? Array.Empty<GroundPathKey>())
                hash.Append(FormattableString.Invariant($"/{key.Seconds:R}/{key.Position.x:R}/{key.Position.y:R}/{key.Yaw:R}"));
            return Hash128.Compute(hash.ToString()).ToString();
        }

        // 读取区分同一导入文件中子素材的持久标识和依赖哈希。
        private static string FingerprintOf(UnityEngine.Object asset)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long fileId))
                throw new InvalidOperationException($"{asset.name} 需要是项目资源。");
            var dependency = AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(asset));
            return FormattableString.Invariant($"{guid}/{fileId}/{dependency}");
        }

        // 校验相位或混合权重是否在单位区间。
        private static bool Unit(float value) => value >= 0f && value <= 1f;

        // 检查素材时间或速率能否参与采样计算。
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
