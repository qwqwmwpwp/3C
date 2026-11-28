using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace ThreeC.CharacterBehavior
{
    // 记录编辑器预览使用的根和模型骨骼，骨骼坐标以 Animator 根为基准。
    internal readonly struct CharacterMotionPoseFrame
    {
        public readonly Vector3 Root, Hips, Left, Right;
        public readonly Quaternion RootRotation;

        // 保存取样时同一姿势的根变换和三处骨骼。
        public CharacterMotionPoseFrame(Vector3 root, Quaternion rootRotation, Vector3 hips, Vector3 left, Vector3 right)
        {
            Root = root;
            RootRotation = rootRotation;
            Hips = hips;
            Left = left;
            Right = right;
        }
    }

    // 管理采样专用的隔离场景与 Avatar 实例。
    internal sealed class CharacterMotionSamplingStage : IDisposable
    {
        private readonly Scene scene;
        public readonly Animator Animator;

        // 在预览场景加载与所选 Avatar 对应的模型。
        public CharacterMotionSamplingStage(Avatar avatar)
        {
            if (!avatar || !avatar.isHuman || !avatar.isValid)
                throw new InvalidOperationException("请选择有效的 Humanoid 模型 Avatar。");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(avatar));
            if (!model) throw new InvalidOperationException("请选择导入的 Humanoid 模型所包含的 Avatar。");
            scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = PrefabUtility.InstantiatePrefab(model, scene) as GameObject;
                Animator = instance ? instance.GetComponentInChildren<Animator>() : null;
                if (!Animator || Animator.avatar != avatar)
                    throw new InvalidOperationException("模型上的 Animator 与采样 Avatar 不一致。");
                Animator.runtimeAnimatorController = null;
                Animator.fireEvents = false;
                Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        // 关闭隔离场景并释放其中的模型。
        public void Dispose() => EditorSceneManager.ClosePreviewScene(scene);
    }

    // 通过运行时片段节点采集一条轨道，并按需记录仅用于预览的根和骨骼。
    internal static class CharacterMotionPoseSampler
    {
        // 采集均匀样点，当轨道确实使用根运动时连续推进图以累积根位移。
        internal static CharacterMotionPoseFrame[] Sample(Animator animator, CharacterMotionTrack track,
            CharacterMotionBlend blend, CharacterClipCurves first, CharacterClipCurves second,
            ICollection<string> warnings, bool capturePreview = false, GroundFeatureLayout layout = null)
        {
            var clip = track.Clip;
            bool extractRoot = track.PathSource == GroundPathSource.RootMotion;
            animator.applyRootMotion = extractRoot;
            animator.Rebind();
            var root = animator.transform;
            root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var left = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var right = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (!hips || !left || !right) throw new InvalidOperationException("Avatar 缺少髋骨或脚骨。");

            var graph = PlayableGraph.Create("地面动作采样");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var motion = new CharacterMotionPlayable(graph, clip, blend, first, second);
                float frameRate = blend.Second ? Mathf.Max(clip.frameRate * blend.FirstRate, blend.Second.frameRate * blend.SecondRate) : clip.frameRate;
                float sampleCount = motion.Length * frameRate;
                if (!(sampleCount > 0f) || sampleCount >= int.MaxValue)
                    throw new InvalidOperationException("动作时长和片段速率生成的采样数无效。");
                int intervals = Mathf.Max(2, Mathf.RoundToInt(sampleCount));
                float step = motion.Length / intervals;
                var poses = new CharacterFootPose[intervals + 1];
                var features = layout?.Active == true ? new GroundFeatureBaker(layout, track, animator, poses.Length) : null;
                var roots = extractRoot ? new GroundPathKey[poses.Length] : null;
                var preview = capturePreview ? new CharacterMotionPoseFrame[poses.Length] : null;
                var output = AnimationPlayableOutput.Create(graph, "姿态", animator);
                output.SetSourcePlayable(motion.Root);
                graph.Play();
                Vector3 origin = default;
                Quaternion basis = Quaternion.identity;
                float yaw = 0f;
                for (int i = 0; i <= intervals; i++)
                {
                    float seconds = i * motion.Length / intervals;
                    if (extractRoot && i > 0)
                    {
                        motion.Advance(1d, step);
                        graph.Evaluate(step);
                    }
                    else
                    {
                        motion.Seek(seconds);
                        graph.Evaluate(0f);
                    }

                    if (i == 0)
                    {
                        origin = root.position;
                        basis = Quaternion.Inverse(root.rotation);
                    }
                    Vector3 localRoot = basis * (root.position - origin);
                    Quaternion localRotation = basis * root.rotation;
                    if (extractRoot)
                    {
                        Vector3 forward = localRotation * Vector3.forward;
                        yaw += Mathf.DeltaAngle(yaw, Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg);
                        roots[i] = new GroundPathKey { Seconds = seconds, Position = new Vector2(localRoot.x, localRoot.z), Yaw = yaw };
                    }

                    var inverse = Quaternion.Inverse(root.rotation);
                    features?.Sample(i);
                    poses[i] = ReadPose(motion, seconds, inverse, root, hips, left, right);
                    if (preview != null)
                    {
                        Vector3 localHips = inverse * (hips.position - root.position);
                        if (!CharacterMotionTrackAudit.Finite(localHips) || !CharacterMotionTrackAudit.Finite(localRoot))
                            throw new InvalidOperationException($"{seconds:F3}s 的髋骨或根位置无效。");
                        preview[i] = new CharacterMotionPoseFrame(localRoot, localRotation, localHips,
                            localHips + poses[i].Left, localHips + poses[i].Right);
                    }
                }

                if (extractRoot && Array.TrueForAll(roots, key => key.Position.sqrMagnitude < 0.000001f && Mathf.Abs(key.Yaw) < 0.1f))
                    throw new InvalidOperationException("根轨迹为空，请检查根运动导入或指定参考轨迹。");
                if (track.IsLoop && !extractRoot)
                {
                    // 逻辑时间恰到整周期会回绕，另外查看回绕前一刻的实际姿势。
                    float beforeWrap = motion.Length - Mathf.Min(0.0001f, step * 0.01f);
                    motion.Seek(beforeWrap);
                    graph.Evaluate(0f);
                    var end = ReadPose(motion, beforeWrap, Quaternion.Inverse(root.rotation), root, hips, left, right);
                    CharacterMotionTrackAudit.AddSeamWarning(poses[0], end, warnings);
                }
                features?.Finish(step);
                SetVelocities(track.IsLoop, poses, step);
                track.Poses = poses;
                track.RootPath = roots;
                return preview;
            }
            finally { graph.Destroy(); }
        }

        // 读取脚部相对髋骨的姿势及同一逻辑时刻的双脚接触。
        private static CharacterFootPose ReadPose(CharacterMotionPlayable motion, float seconds, Quaternion inverse,
            Transform root, Transform hips, Transform left, Transform right)
        {
            if (!motion.TryRead(CharacterFootSampler.LeftContactCurve, seconds, out float leftValue) ||
                !motion.TryRead(CharacterFootSampler.RightContactCurve, seconds, out float rightValue))
                throw new InvalidOperationException("基础片段缺少足部接触曲线。");
            return new CharacterFootPose
            {
                HasPelvis = 1, Pelvis = inverse * (hips.position - root.position),
                Left = inverse * (left.position - hips.position), Right = inverse * (right.position - hips.position),
                Contact = new Vector2(leftValue, rightValue)
            };
        }

        // 对循环用跨边界的中心差分，对非循环端点用单边差分。
        private static void SetVelocities(bool loop, CharacterFootPose[] poses, float step)
        {
            int end = poses.Length - 1;
            for (int i = 0; i <= end; i++)
            {
                int prev = i > 0 ? i - 1 : loop ? end - 1 : 0;
                int next = i < end ? i + 1 : loop ? 1 : end;
                float dt = step * (!loop && (i == 0 || i == end) ? 1f : 2f);
                poses[i].SetVelocity(poses[prev], poses[next], dt);
            }
        }
    }
}
