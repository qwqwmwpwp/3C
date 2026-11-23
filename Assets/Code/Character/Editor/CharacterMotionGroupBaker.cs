using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal static class CharacterMotionGroupBaker
    {
        private const string SampleVersion = "group/1";

        internal readonly struct Result
        {
            public readonly CharacterClipLibrary Playback;
            public readonly CharacterMotionTrack[] Tracks;
            public readonly int Resampled;

            public Result(CharacterClipLibrary playback, CharacterMotionTrack[] tracks, int resampled)
            {
                Playback = playback;
                Tracks = tracks;
                Resampled = resampled;
            }

            public void Apply(CharacterMotionDatabase db)
            {
                db.Playback = Playback;
                db.BakedTracks = Tracks;
                db.BakedAvatar = db.Avatar;
                db.BakedController = null;
                db.Format = 1;
            }
        }

        public static void Bake(CharacterMotionDatabase db, bool changedOnly)
        {
            var result = Compile(db, changedOnly);
            CharacterAnimationOverrideAudit.Inspect(db, result.Playback, result.Tracks, null);
            Undo.RecordObject(db, "重新采样地面动作数据组");
            result.Apply(db);
            EditorUtility.SetDirty(db);
            AssetDatabase.SaveAssetIfDirty(db);
            Debug.Log($"{db.name}：已校验 {result.Tracks.Length} 条地面轨道，重新采样 {result.Resampled} 条。", db);
        }

        private sealed class SourceClip
        {
            public readonly CharacterClipCurves Curves;
            public readonly string Fingerprint;

            public SourceClip(AnimationClip clip)
            {
                Fingerprint = FingerprintOf(clip);
                Curves = CharacterClipLibraryBaker.ReadCurves(clip);
            }
        }

        private sealed class TrackRecipe
        {
            public readonly CharacterMotionTrack Track;
            public readonly CharacterMotionBlend Blend;
            public readonly CharacterClipCurves FirstCurves, SecondCurves;

            public TrackRecipe(CharacterMotionTrack track, CharacterMotionBlend blend, SourceClip first, SourceClip second)
            {
                Track = track;
                Blend = blend;
                FirstCurves = first.Curves;
                SecondCurves = second?.Curves;
            }
        }

        internal static Result Compile(CharacterMotionDatabase db, bool changedOnly = false)
        {
            if (!db.Groups) throw new InvalidOperationException("请选择基础动画数据组。");
            if (!db.Avatar || !db.Avatar.isHuman || !db.Avatar.isValid)
                throw new InvalidOperationException("请选择有效的 Humanoid 模型 Avatar。");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(db.Avatar));
            if (!model) throw new InvalidOperationException("请选择导入的 Humanoid 模型所包含的 Avatar。");
            string avatarHash = FingerprintOf(db.Avatar);
            var playback = CreatePlayback(db);
            var sources = new Dictionary<AnimationClip, SourceClip>();
            var states = new Dictionary<int, CharacterClipState>();
            var orderedStates = new List<CharacterClipState>();
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
                        AddState(new CharacterClipState { Path = path, Clip = group.First, Layer = db.GroundLayer, Blend = recipe.Blend });
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
            int resampled = Sample(model, db.Avatar, recipes, previous);
            var tracks = new CharacterMotionTrack[recipes.Count];
            for (int i = 0; i < tracks.Length; i++) tracks[i] = recipes[i].Track;
            return new Result(playback, tracks, resampled);
        }

        private static CharacterClipLibrary CreatePlayback(CharacterMotionDatabase db)
        {
            var authored = db.Groups.Layers;
            if (authored?.Length != 2 || db.GroundLayer != 0)
                throw new InvalidOperationException("数据组需要按下身、上身排列的两层，地面动作所在层填 0。");
            var library = new CharacterClipLibrary
            {
                FromGroups = true,
                BodyOutputs = true,
                Layers = new CharacterClipLayer[authored.Length]
            };
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

        private static void ValidateGroup(GroundAnimationGroup group)
        {
            if (!Finite(group.FirstRate) || group.FirstRate <= 0f ||
                !Finite(group.SecondRate) || group.SecondRate <= 0f ||
                !Unit(group.Weights.x) || !Unit(group.Weights.y) || !Unit(group.Weights.z))
                throw new InvalidOperationException($"{group.Name}：源片段速率必须大于 0，混合权重范围为 0 至 1。");
            if (!group.Second && group.FirstRate != 1f)
                throw new InvalidOperationException($"{group.Name}：使用单片段时，源片段速率请填 1。");
        }

        private static TrackRecipe CreateRecipe(GroundAnimationGroup group, CharacterMotionTrack variant, SourceClip first,
            SourceClip second, string avatarHash, string label)
        {
            if (variant == null || variant.States == null || variant.States.Length == 0 ||
                !Unit(variant.FirstPhase) || !Unit(variant.SecondPhase) || !Unit(variant.Blend) ||
                !(variant.ReferenceSpeed > 0f) || variant.BrakeSeconds < 0f ||
                variant.ExitSeconds < 0f || variant.MaximumSpeed < 0f ||
                !Enum.IsDefined(typeof(GroundMotionUse), variant.Use) || !Enum.IsDefined(typeof(GroundTrackGait), variant.Gait) ||
                !Enum.IsDefined(typeof(GroundTrackGait), variant.FromGait) || !Enum.IsDefined(typeof(GroundTrackSide), variant.Side) ||
                !Enum.IsDefined(typeof(GroundPathSource), variant.PathSource))
                throw new InvalidOperationException($"{label}：需要状态、有效的用途、步态、参考速度、时间与相位。");
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
            if (!Finite(track.Seconds) || track.Seconds <= 0f)
                throw new InvalidOperationException($"{label}：动作时长必须大于 0。");
            try { CharacterMotionDatabaseBaker.Validate(track); }
            catch (InvalidOperationException error) { throw new InvalidOperationException($"{label}：{error.Message}", error); }
            var blend = track.Mixed ? new CharacterMotionBlend
            {
                Second = group.Second, Weights = group.Weights, Seconds = track.Seconds, Loop = track.IsLoop,
                FirstPhase = track.FirstPhase, SecondPhase = track.SecondPhase, FirstRate = group.FirstRate, SecondRate = group.SecondRate
            } : default;
            track.SampleSourceHash = SampleHash(avatarHash, first, second, track, blend);
            return new TrackRecipe(track, blend, first, second);
        }

        private static SourceClip GetSource(AnimationClip clip, Dictionary<AnimationClip, SourceClip> sources, string label)
        {
            if (!clip || clip.legacy || !Finite(clip.length) || clip.length <= 0f || !Finite(clip.frameRate) || clip.frameRate <= 0f)
                throw new InvalidOperationException($"{label}：需要有效且有时长的非 Legacy 基础片段。");
            if (!sources.TryGetValue(clip, out var source)) sources.Add(clip, source = new SourceClip(clip));
            return source;
        }

        private static void ValidateContact(SourceClip first, SourceClip second, string label)
        {
            foreach (string name in new[] { CharacterFootSampler.LeftContactCurve, CharacterFootSampler.RightContactCurve })
            {
                var a = first.Curves.FindPlaybackCurve(name);
                var b = second?.Curves.FindPlaybackCurve(name);
                if (a == null && b == null) throw new InvalidOperationException($"{label}：基础片段缺少 {name} 曲线。");
                foreach (var entry in new[] { a, b })
                {
                    if (entry == null) continue;
                    foreach (var key in entry.Curve.keys)
                        if (!Finite(key.time) || !Finite(key.value) || float.IsNaN(key.inTangent) || float.IsNaN(key.outTangent))
                            throw new InvalidOperationException($"{label}：足部曲线 {name} 包含无效的关键帧。");
                }
            }
        }

        private static void ValidateStatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path != path.Trim() ||
                Array.Exists(path.Split('.'), segment => string.IsNullOrWhiteSpace(segment) || segment != segment.Trim()))
                throw new InvalidOperationException($"动作标识 {path} 不能为空，也不能有空段或首尾空格。");
        }

        private static Dictionary<string, CharacterMotionTrack> IndexPrevious(CharacterMotionDatabase db, bool changedOnly)
        {
            var result = new Dictionary<string, CharacterMotionTrack>(StringComparer.Ordinal);
            if (!changedOnly || db.Playback?.FromGroups != true || db.BakedAvatar != db.Avatar || db.BakedTracks == null)
                return result;
            foreach (var old in db.BakedTracks)
                if (old?.States?.Length > 0 && !string.IsNullOrEmpty(old.States[0]) && old.ReadyForSearch)
                    result[old.States[0]] = old;
            return result;
        }

        private static int Sample(GameObject model, Avatar avatar, List<TrackRecipe> recipes,
            Dictionary<string, CharacterMotionTrack> previous)
        {
            var pending = new List<TrackRecipe>();
            foreach (var recipe in recipes)
            {
                var track = recipe.Track;
                if (previous.TryGetValue(track.States[0], out var old) && old.SampleSourceHash == track.SampleSourceHash)
                {
                    track.Poses = old.Poses;
                    track.RootPath = old.RootPath;
                }
                else pending.Add(recipe);
            }
            if (pending.Count == 0) return 0;
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = PrefabUtility.InstantiatePrefab(model, scene) as GameObject;
                var animator = instance ? instance.GetComponentInChildren<Animator>() : null;
                if (!animator || animator.avatar != avatar)
                    throw new InvalidOperationException("模型上的 Animator 与采样 Avatar 不一致。");
                animator.runtimeAnimatorController = null;
                animator.fireEvents = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                foreach (var recipe in pending)
                {
                    var track = recipe.Track;
                    try
                    {
                        track.Poses = CharacterMotionDatabaseBaker.Sample(animator, track, recipe.FirstCurves, recipe.Blend, recipe.SecondCurves);
                        if (!track.ReadyForSearch || Array.Exists(track.Poses, p => !ValidPose(p)))
                            throw new InvalidOperationException("采样得到无效的脚部姿势、速度或接触值。");
                    }
                    catch (Exception error) { throw new InvalidOperationException($"{track.States[0]}：{error.Message}", error); }
                }
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
            return pending.Count;
        }

        private static string SampleHash(string avatarHash, SourceClip first, SourceClip second, CharacterMotionTrack track, CharacterMotionBlend blend)
        {
            string timing = FormattableString.Invariant($"{track.Seconds:R}/{track.PathSource}/{track.IsLoop}");
            string mixing = FormattableString.Invariant($"{blend.FirstPhase:R}/{blend.SecondPhase:R}/{blend.FirstRate:R}/{blend.SecondRate:R}/{blend.Weights.x:R}/{blend.Weights.y:R}/{blend.Weights.z:R}");
            return Hash128.Compute($"{SampleVersion}/{avatarHash}/{first.Fingerprint}/{second?.Fingerprint}/{timing}/{mixing}").ToString();
        }

        private static string FingerprintOf(UnityEngine.Object asset)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long fileId))
                throw new InvalidOperationException($"{asset.name} 需要是项目资源。");
            var dependency = AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(asset));
            return $"{guid}/{fileId}/{dependency}";
        }

        private static bool ValidPose(CharacterFootPose pose) => Finite(pose.Left) && Finite(pose.Right) &&
            Finite(pose.LeftVelocity) && Finite(pose.RightVelocity) && Unit(pose.Contact.x) && Unit(pose.Contact.y);
        private static bool Unit(float value) => value >= 0f && value <= 1f;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
