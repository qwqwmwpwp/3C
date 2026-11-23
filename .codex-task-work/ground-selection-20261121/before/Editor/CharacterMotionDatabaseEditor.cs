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
    [CustomEditor(typeof(CharacterMotionDatabase))]
    internal sealed class CharacterMotionDatabaseEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var database = (CharacterMotionDatabase)target;
            if (database.Tracks != null)
                foreach (var track in database.Tracks)
                    if (track != null && track.Clip)
                        EditorGUILayout.LabelField(track.Clip.name, $"{track.Poses?.Length ?? 0} 个姿势");
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("按角色 Avatar 重新采样"))
                {
                    try { Bake(database); }
                    catch (Exception error) { Debug.LogError($"动作数据库采样失败：{error.Message}", database); }
                }
        }

        private static void Bake(CharacterMotionDatabase db)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(db.Avatar));
            if (!model || !db.Avatar.isHuman || !(db.Controller is AnimatorController controller) ||
                db.Tracks == null || db.Tracks.Length == 0)
                throw new InvalidOperationException("需要 Humanoid 模型 Avatar、Animator Controller 和候选片段。");

            var paths = new Dictionary<string, Motion>();
            foreach (var layer in controller.layers) Collect(layer.name, layer.stateMachine, paths);
            var used = new HashSet<string>();
            var clips = new HashSet<AnimationClip>();
            var baked = new CharacterFootPose[db.Tracks.Length][];
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
                    foreach (string path in track.States)
                        if (string.IsNullOrWhiteSpace(path) || !used.Add(path) || !paths.TryGetValue(path, out var motion) || motion != track.Clip)
                            throw new InvalidOperationException($"状态 {path} 缺失、重复或使用了不同的片段。");
                    baked[i] = Sample(animator, track);
                }
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }

            // 全部采样成功后一次性写入；某个片段失败不会留下半个新库。
            Undo.RecordObject(db, "重新采样地面动作库");
            for (int i = 0; i < baked.Length; i++)
            {
                db.Tracks[i].Seconds = db.Tracks[i].Clip.length;
                db.Tracks[i].Poses = baked[i];
            }
            EditorUtility.SetDirty(db);
            AssetDatabase.SaveAssetIfDirty(db);
        }

        private static void Collect(string prefix, AnimatorStateMachine machine, Dictionary<string, Motion> paths)
        {
            foreach (var state in machine.states) paths.Add(prefix + "." + state.state.name, state.state.motion);
            foreach (var child in machine.stateMachines) Collect(prefix + "." + child.stateMachine.name, child.stateMachine, paths);
        }

        private static CharacterFootPose[] Sample(Animator animator, CharacterMotionTrack track)
        {
            var clip = track.Clip;
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip)) as ModelImporter;
            var source = importer == null ? null : Array.Find(importer.clipAnimations, a => a.name == clip.name);
            var leftContact = Contact(source, "IKLeftFoot");
            var rightContact = Contact(source, "IKRightFoot");
            if (leftContact == null || rightContact == null || clip.length <= 0f)
                throw new InvalidOperationException($"{clip.name} 缺少足部接触曲线或时长。");
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var left = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var right = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (!hips || !left || !right) throw new InvalidOperationException("Avatar 缺少髋骨或脚骨。");
            int intervals = Mathf.Max(2, Mathf.RoundToInt(clip.length * clip.frameRate));
            var poses = new CharacterFootPose[intervals + 1];
            var graph = PlayableGraph.Create("地面动作采样");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var animation = AnimationClipPlayable.Create(graph, clip);
                animation.SetApplyFootIK(false);
                animation.SetApplyPlayableIK(false);
                animation.SetSpeed(0d);
                var output = AnimationPlayableOutput.Create(graph, "姿态", animator);
                output.SetSourcePlayable(animation);
                graph.Play();
                for (int i = 0; i <= intervals; i++)
                {
                    float phase = (float)i / intervals;
                    animation.SetTime(phase * clip.length);
                    graph.Evaluate(0f);
                    var inverse = Quaternion.Inverse(animator.transform.rotation);
                    poses[i] = new CharacterFootPose
                    {
                        Left = inverse * (left.position - hips.position),
                        Right = inverse * (right.position - hips.position),
                        Contact = new Vector2(leftContact.Evaluate(phase), rightContact.Evaluate(phase))
                    };
                }
                float step = clip.length / intervals;
                for (int i = 0; i <= intervals; i++)
                {
                    int prev = i > 0 ? i - 1 : track.IsStop ? 0 : intervals - 1;
                    int next = i < intervals ? i + 1 : track.IsStop ? intervals : 1;
                    float dt = step * (track.IsStop && (i == 0 || i == intervals) ? 1f : 2f);
                    poses[i].SetVelocity(poses[prev], poses[next], dt);
                }
            }
            finally { graph.Destroy(); }
            return poses;
        }

        private static AnimationCurve Contact(ModelImporterClipAnimation source, string name)
        {
            if (source?.curves != null)
                foreach (var entry in source.curves)
                    if (entry.name == name) return entry.curve;
            return null;
        }
    }
}
