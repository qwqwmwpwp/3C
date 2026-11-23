using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace ThreeC.CharacterBehavior
{
    internal static class CharacterMotionDatabaseBaker
    {
        private const string BakeMenu = "Assets/3C/烘焙地面动作库";
        private const string SamplesMenu = "Assets/3C/查看动作库采样数";

        [MenuItem(BakeMenu, true)]
        private static bool CanBake() => HasSelectedDatabase() && !EditorApplication.isPlayingOrWillChangePlaymode;

        [MenuItem(BakeMenu)]
        private static void BakeSelected()
        {
            var database = (CharacterMotionDatabase)Selection.activeObject;
            try { Bake(database); }
            catch (Exception error) { Debug.LogError($"动作数据库采样失败：{error.Message}", database); }
        }

        private const string ChangedMenu = "Assets/3C/烘焙变更的地面轨道";

        [MenuItem(ChangedMenu, true)]
        private static bool CanBakeChanged() => CanBake();

        [MenuItem(ChangedMenu)]
        private static void BakeChanged()
        {
            var database = (CharacterMotionDatabase)Selection.activeObject;
            try { Bake(database, true); }
            catch (Exception error) { Debug.LogError($"动作数据库采样失败：{error.Message}", database); }
        }

        [MenuItem(SamplesMenu, true)]
        private static bool HasSelectedDatabase() =>
            Selection.objects.Length == 1 && Selection.activeObject is CharacterMotionDatabase;

        [MenuItem(SamplesMenu)]
        private static void ShowSamples()
        {
            var database = (CharacterMotionDatabase)Selection.activeObject;
            var summary = new System.Text.StringBuilder(database.name);
            string avatarHash = DependencyHash(database.Avatar);
            foreach (var track in database.Groups ? database.BakedTracks ?? Array.Empty<CharacterMotionTrack>() : database.Tracks ?? Array.Empty<CharacterMotionTrack>())
            {
                if (track == null || !track.Clip) continue;
                var baked = database.Groups ? track : Array.Find(database.BakedTracks ?? database.Tracks, t => t != null && t.Clip == track.Clip);
                string label = database.Groups && track.States?.Length > 0 ? $"{track.States[0]} · {track.Clip.name}" : track.Clip.name;
                summary.AppendLine().Append(label).Append(" · ").Append(baked?.Poses?.Length ?? 0).Append(" 个姿势")
                    .Append(database.Groups ? " · 数据组" : baked?.SampleSourceHash == SampleHash(track, avatarHash) ? " · 当前采样" : " · 待校验或重采样");
            }
            AppendCoverage(summary, database.BakedTracks ?? Array.Empty<CharacterMotionTrack>());
            Debug.Log(summary.ToString(), database);
        }

        private static void AppendCoverage(System.Text.StringBuilder summary, CharacterMotionTrack[] tracks)
        {
            foreach (var gait in new[] { GroundTrackGait.Walk, GroundTrackGait.Run, GroundTrackGait.Sprint })
            {
                bool Has(GroundMotionUse use, GroundTrackSide side = GroundTrackSide.Either, float angle = 0f) =>
                    Array.Exists(tracks, t => t != null && t.ReadyForSearch && (t.Gait == gait || t.Gait == GroundTrackGait.Any) &&
                        t.Use == use && (side == GroundTrackSide.Either || t.Side == side) &&
                        (angle <= 0f || Mathf.Abs(t.TurnDegrees - angle) < 5f));

                if (!Has(GroundMotionUse.Cycle)) summary.AppendLine().Append($"{gait} 缺少循环");
                if (!Has(GroundMotionUse.Start)) summary.AppendLine().Append($"{gait} 缺少独立起步");
                foreach (var side in new[] { GroundTrackSide.Left, GroundTrackSide.Right })
                {
                    if (!Has(GroundMotionUse.Stop, side)) summary.AppendLine().Append($"{gait} 缺少 {side} 脚收步");
                    if (!Has(GroundMotionUse.Cycle, side)) summary.AppendLine().Append($"{gait} 缺少 {side} 弧线循环");
                    foreach (int angle in new[] { 90, 180 })
                        if (!Has(GroundMotionUse.Turn, side, angle))
                            summary.AppendLine().Append($"{gait} 缺少 {side} {angle}° 转向");
                }
                foreach (var from in new[] { GroundTrackGait.Walk, GroundTrackGait.Run, GroundTrackGait.Sprint })
                    if (Math.Abs((int)from - (int)gait) == 1 && !Array.Exists(tracks, t => t != null &&
                        t.ReadyForSearch && t.Use == GroundMotionUse.Transition && t.Gait == gait && t.FromGait == from))
                        summary.AppendLine().Append($"{from} → {gait} 缺少换步");
            }
        }

        private static string DependencyHash(UnityEngine.Object asset) =>
            asset ? AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(asset)).ToString() : "";

        private static string SampleHash(CharacterMotionTrack track, string avatarHash) =>
            $"1/{avatarHash}/{DependencyHash(track.Clip)}/{track.PathSource}/{track.IsLoop}";

        private static void Bake(CharacterMotionDatabase db, bool changedOnly = false)
        {
            if (db.Groups) { CharacterMotionGroupBaker.Bake(db, changedOnly); return; }
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(db.Avatar));
            if (!model || !db.Avatar.isHuman || !(db.Controller is AnimatorController controller) ||
                db.Tracks == null || db.Tracks.Length == 0)
                throw new InvalidOperationException("需要 Humanoid 模型 Avatar、Animator Controller 和候选片段。");

            bool bodyParts = db.PlaybackMode == CharacterPlaybackBakeMode.BodyParts;
            bool direct = bodyParts || db.PlaybackMode == CharacterPlaybackBakeMode.DirectClips ||
                db.PlaybackMode == CharacterPlaybackBakeMode.Auto && controller.parameters.Length == 0;
            var curves = new Dictionary<AnimationClip, CharacterClipCurves>();
            var playback = direct ? CharacterClipLibraryBaker.Build(controller, curves, bodyParts) : null;
            var paths = new Dictionary<string, AnimatorState>();
            if (db.GroundLayer < 0 || db.GroundLayer >= controller.layers.Length)
                throw new InvalidOperationException("地面动作所在层超出控制器层范围。");
            var layer = controller.layers[db.GroundLayer];
            Collect(layer.name, layer.stateMachine, paths);
            var used = new HashSet<string>();
            var clips = new HashSet<AnimationClip>();
            var baked = new CharacterMotionTrack[db.Tracks.Length];
            string avatarHash = DependencyHash(db.Avatar);
            int resampled = 0;
            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
                var animator = instance.GetComponentInChildren<Animator>();
                if (!animator || animator.avatar != db.Avatar)
                    throw new InvalidOperationException("模型上的 Animator 与采样 Avatar 不一致。");
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false;
                animator.fireEvents = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                for (int i = 0; i < db.Tracks.Length; i++)
                {
                    var track = db.Tracks[i];
                    if (track == null || !track.Clip || !clips.Add(track.Clip) || track.States == null || track.States.Length == 0 ||
                        track.ReferenceSpeed <= 0f || track.LastEntrySeconds < 0f || track.BrakeSeconds < 0f)
                        throw new InvalidOperationException($"第 {i + 1} 项的片段重复或配置无效。");
                    var next = track.CopyForBake();
                    if (db.Format == 0 && next.Use == GroundMotionUse.Legacy) UpgradeLegacy(next);
                    Validate(next);
                    foreach (string path in next.States)
                    {
                        if (string.IsNullOrWhiteSpace(path) || !used.Add(path) || !paths.TryGetValue(path, out var state) ||
                            state.motion != next.Clip)
                            throw new InvalidOperationException($"状态 {path} 缺失、重复或使用了不同的片段。");
                        ValidatePlayback(next, state, path, direct);
                        bool scale = next.IsLoop && (direct ? !next.FixedCycleSpeed : state.speedParameterActive);
                        if (path == next.States[0]) next.ScalesWithSpeed = scale;
                        else if (next.IsLoop && next.ScalesWithSpeed != scale)
                            throw new InvalidOperationException($"{path}：同一轨道的别名需要一致的速度驱动方式。");
                    }
                    try
                    {
                        if (!curves.TryGetValue(next.Clip, out var clipCurves))
                            clipCurves = CharacterClipLibraryBaker.ReadCurves(next.Clip);
                        next.SampleSourceHash = SampleHash(next, avatarHash);
                        var previous = changedOnly && db.BakedAvatar == db.Avatar && db.BakedTracks != null ?
                            Array.Find(db.BakedTracks, row => row?.Clip == next.Clip) : null;
                        if (previous != null && previous.IsReady && previous.SampleSourceHash == next.SampleSourceHash)
                        {
                            next.Poses = previous.Poses;
                            next.RootPath = previous.RootPath;
                        }
                        else
                        {
                            next.Poses = Sample(animator, next, clipCurves);
                            resampled++;
                        }
                    }
                    catch (Exception error) { throw new InvalidOperationException($"{next.Clip.name}：{error.Message}", error); }
                    next.Seconds = next.Clip.length;
                    baked[i] = next;
                }
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }

            if (playback != null) CharacterAnimationOverrideAudit.Inspect(db, playback, baked, curves);
            // 全部采样成功后一次性写入；某个片段失败不会留下半个新库。
            Undo.RecordObject(db, "重新采样地面动作库");
            db.Playback = playback;
            db.BakedTracks = baked;
            db.BakedAvatar = db.Avatar;
            db.BakedController = db.Controller;
            db.Format = 1;
            for (int i = 0; i < baked.Length; i++)
            {
                var author = baked[i].CopyForBake();
                author.Poses = null;
                author.RootPath = null;
                author.SampleSourceHash = null;
                db.Tracks[i] = author;
            }
            EditorUtility.SetDirty(db);
            AssetDatabase.SaveAssetIfDirty(db);
            Debug.Log($"{db.name}：已校验 {baked.Length} 条轨道，重新采样 {resampled} 条。", db);
        }

        private static void UpgradeLegacy(CharacterMotionTrack track)
        {
            string state = track.States[0];
            if (state == "Base Layer.walk" || state == "Base Layer.move" || state == "Base Layer.sprint")
            {
                track.Use = GroundMotionUse.Cycle;
                track.Loop = true;
                track.Gait = state == "Base Layer.walk" ? GroundTrackGait.Walk :
                    state == "Base Layer.sprint" ? GroundTrackGait.Sprint : GroundTrackGait.Run;
            }
            else if (state == "Base Layer.run_stop_left" || state == "Base Layer.run_stop_right")
            {
                track.Use = GroundMotionUse.Stop;
                track.Gait = GroundTrackGait.Run;
                track.Side = state.EndsWith("_left", StringComparison.Ordinal) ? GroundTrackSide.Left : GroundTrackSide.Right;
            }
            else throw new InvalidOperationException($"{state}：请显式选择用途、步态与循环方式。");
        }

        internal static void Validate(CharacterMotionTrack track)
        {
            string clip = track.Clip.name;
            float length = track.Mixed ? track.Seconds : track.Clip.length;
            if (!Finite(length) || !Finite(track.ReferenceSpeed) || !Finite(track.BrakeSeconds) ||
                !Finite(track.FirstEntrySeconds) || !Finite(track.LastEntrySeconds) || !Finite(track.ExitSeconds) ||
                !Finite(track.MinimumSpeed) || !Finite(track.MaximumSpeed) || !Finite(track.Blend))
                throw new InvalidOperationException($"{clip}：时间、速度和混合参数必须是有限数值。");
            if (track.Use == GroundMotionUse.Legacy || (!track.Mixed && track.Loop != track.Clip.isLooping) ||
                track.Loop != (track.Use == GroundMotionUse.Cycle))
                throw new InvalidOperationException($"{clip}：请检查用途、片段循环设置与 Loop。");
            if (track.MinimumSpeed < 0f || track.MaximumSpeed > 0f && track.MaximumSpeed < track.MinimumSpeed ||
                track.FirstEntrySeconds < 0f || track.LastEntrySeconds < track.FirstEntrySeconds ||
                track.LastEntrySeconds > length + 0.001f || track.ExitSeconds > length + 0.001f ||
                !track.IsLoop && track.ExitSeconds > 0f && track.ExitSeconds <= track.LastEntrySeconds)
                throw new InvalidOperationException($"{clip}：速度、入点或退出区间无效。");
            if (track.IsStop && (track.Side == GroundTrackSide.Either || track.BrakeSeconds <= track.FirstEntrySeconds ||
                track.BrakeSeconds > length || track.ExitSeconds > 0f && track.ExitSeconds < track.BrakeSeconds))
                throw new InvalidOperationException($"{clip}：收步需要承重脚及有效的主制动标记。");
            if (!track.IsLoop && !track.IsStop && track.LastEntrySeconds + CharacterMatchTrajectory.FarSeconds > length + 0.001f)
                throw new InvalidOperationException($"{clip}：末入点之后必须覆盖远端预测时间窗。");
            if (track.Use == GroundMotionUse.Transition && (track.FromGait == GroundTrackGait.Any ||
                track.Gait == GroundTrackGait.Any || track.FromGait == track.Gait || track.PathSource == GroundPathSource.Straight))
                throw new InvalidOperationException($"{clip}：换步需要不同的来源与目标步态及有效速度轨迹。");
            if (!Finite(track.TurnDegrees) || track.TurnDegrees < 0f || track.TurnDegrees > 180f)
                throw new InvalidOperationException($"{clip}：参考转角必须在 0 至 180 度。");
            if (track.Use == GroundMotionUse.Turn && track.Side == GroundTrackSide.Either)
                throw new InvalidOperationException($"{clip}：请标明转向侧。");
            if (track.Use == GroundMotionUse.Turn && track.PathSource == GroundPathSource.Straight)
                throw new InvalidOperationException($"{clip}：转向需要根轨迹或作者参考轨迹。");
            if (track.PathSource != GroundPathSource.Authored) return;
            var path = track.ReferencePath;
            if (path == null || path.Length < 2 || path[0].Seconds != 0f || path[0].Position.sqrMagnitude > 0.000001f ||
                Mathf.Abs(path[0].Yaw) > 0.001f || path[path.Length - 1].Seconds < length - 0.001f)
                throw new InvalidOperationException($"{clip}：参考轨迹应从局部原点开始并覆盖整个片段。");
            for (int i = 0; i < path.Length; i++)
                if (!Finite(path[i].Seconds) || !Finite(path[i].Position.x) || !Finite(path[i].Position.y) ||
                    !Finite(path[i].Yaw) || i > 0 && !(path[i].Seconds > path[i - 1].Seconds))
                    throw new InvalidOperationException($"{clip}：参考轨迹时间必须严格递增。");
        }

        private static void ValidatePlayback(CharacterMotionTrack track, AnimatorState state, string path, bool direct)
        {
            if (Mathf.Abs(state.speed - 1f) > 0.0001f || state.timeParameterActive || state.mirror || state.mirrorParameterActive ||
                state.cycleOffsetParameterActive || Mathf.Abs(state.cycleOffset) > 0.0001f)
                throw new InvalidOperationException($"{path}：入点采样要求状态原速、无镜像、无偏移和外部时间驱动。");
            if (direct) return; // 独立片段时钟由 C# 根据轨道与播放计划调速。
            if (track.IsStop && (!state.speedParameterActive || state.speedParameter != "StopRate"))
                throw new InvalidOperationException($"{path}：收步状态需要 StopRate 播放参数。");
            if (!track.IsLoop && !track.IsStop && state.speedParameterActive)
                throw new InvalidOperationException($"{path}：单次起步、转向按素材原速播放。");
            if (!track.IsLoop || !state.speedParameterActive) return;
            string gaitRate = track.Gait == GroundTrackGait.Walk ? "WalkRate" :
                track.Gait == GroundTrackGait.Sprint ? "SprintRate" : track.Gait == GroundTrackGait.Run ? "RunRate" : "LocomotionRate";
            if (state.speedParameter != gaitRate && state.speedParameter != "LocomotionRate")
                throw new InvalidOperationException($"{path}：循环状态请选择 LocomotionRate 或 {gaitRate}。");
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static void Collect(string prefix, AnimatorStateMachine machine, Dictionary<string, AnimatorState> paths)
        {
            foreach (var state in machine.states) paths.Add(prefix + "." + state.state.name, state.state);
            foreach (var child in machine.stateMachines) Collect(prefix + "." + child.stateMachine.name, child.stateMachine, paths);
        }

        internal static CharacterFootPose[] Sample(Animator animator, CharacterMotionTrack track, CharacterClipCurves curves,
            CharacterMotionBlend blend = default, CharacterClipCurves secondary = null)
        {
            var clip = track.Clip;
            bool extractRoot = track.PathSource == GroundPathSource.RootMotion;
            animator.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            animator.applyRootMotion = extractRoot;
            float length = blend.Second ? blend.Seconds : clip.length;
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var left = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var right = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (!hips || !left || !right) throw new InvalidOperationException("Avatar 缺少髋骨或脚骨。");
            int intervals = Mathf.Max(2, Mathf.RoundToInt(length * clip.frameRate));
            var poses = new CharacterFootPose[intervals + 1];
            var graph = PlayableGraph.Create("地面动作采样");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var motion = new CharacterMotionPlayable(graph, clip, blend, curves, secondary);
                motion.Root.SetSpeed(extractRoot ? 1d : 0d);
                var output = AnimationPlayableOutput.Create(graph, "姿态", animator);
                output.SetSourcePlayable(motion.Root);
                graph.Play();
                track.RootPath = extractRoot ? new GroundPathKey[intervals + 1] : null;
                Vector3 origin = default;
                Quaternion basis = Quaternion.identity;
                float previousYaw = 0f;
                for (int i = 0; i <= intervals; i++)
                {
                    float phase = (float)i / intervals;
                    if (!extractRoot || i == 0) motion.Seek(phase * length);
                    graph.Evaluate(extractRoot && i > 0 ? length / intervals : 0f);
                    if (extractRoot)
                    {
                        if (i == 0)
                        {
                            origin = animator.transform.position;
                            basis = Quaternion.Inverse(animator.transform.rotation);
                        }
                        Vector3 local = basis * (animator.transform.position - origin);
                        Vector3 forward = basis * animator.transform.forward;
                        float yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                        previousYaw += Mathf.DeltaAngle(previousYaw, yaw);
                        track.RootPath[i] = new GroundPathKey { Seconds = phase * length,
                            Position = new Vector2(local.x, local.z), Yaw = previousYaw };
                    }
                    var inverse = Quaternion.Inverse(animator.transform.rotation);
                    if (!motion.TryRead(CharacterFootSampler.LeftContactCurve, phase * length, out float leftValue) ||
                        !motion.TryRead(CharacterFootSampler.RightContactCurve, phase * length, out float rightValue))
                        throw new InvalidOperationException($"{clip.name} 缺少足部接触曲线。");
                    poses[i] = new CharacterFootPose
                    {
                        Left = inverse * (left.position - hips.position),
                        Right = inverse * (right.position - hips.position),
                        Contact = new Vector2(leftValue, rightValue)
                    };
                }
                if (extractRoot && Array.TrueForAll(track.RootPath, k => k.Position.sqrMagnitude < 0.000001f && Mathf.Abs(k.Yaw) < 0.1f))
                    throw new InvalidOperationException("根轨迹为空，请检查根运动导入或指定参考轨迹。");
                float step = length / intervals;
                for (int i = 0; i <= intervals; i++)
                {
                    int prev = i > 0 ? i - 1 : track.IsLoop ? intervals - 1 : 0;
                    int next = i < intervals ? i + 1 : track.IsLoop ? 1 : intervals;
                    float dt = step * (!track.IsLoop && (i == 0 || i == intervals) ? 1f : 2f);
                    poses[i].SetVelocity(poses[prev], poses[next], dt);
                }
            }
            finally { graph.Destroy(); }
            return poses;
        }
    }
}
