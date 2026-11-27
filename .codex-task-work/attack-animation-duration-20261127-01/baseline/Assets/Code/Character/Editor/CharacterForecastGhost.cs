using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在独立预览场景内采样骨架；仅在 Scene 视图的绘制回调中提交网格。</summary>
    internal sealed class CharacterForecastGhost : IDisposable
    {
        private sealed class Part
        {
            internal Renderer Source;
            internal Transform Node;
            internal SkinnedMeshRenderer Skin;
            internal Mesh Geometry;
            internal bool Baked;
        }

        private readonly struct RestPose
        {
            private readonly Transform node;
            private readonly Vector3 position, scale;
            private readonly Quaternion rotation;

            internal RestPose(Transform node)
            {
                this.node = node;
                position = node.localPosition;
                rotation = node.localRotation;
                scale = node.localScale;
            }

            internal void Apply()
            {
                node.SetLocalPositionAndRotation(position, rotation);
                node.localScale = scale;
            }
        }

        private readonly Dictionary<Transform, Transform> nodes = new Dictionary<Transform, Transform>();
        private readonly List<RestPose> rest = new List<RestPose>();
        private readonly List<Part> parts = new List<Part>();
        private Scene scene;
        private Material material;
        private GameObject root;
        private PlayableGraph graph;
        private AnimationPlayableOutput output;
        private CharacterMotionPlayable playable;
        private Animator animator;
        private RestPose animatorRoot;
        private AnimationClip activeClip;
        private CharacterClipState activeGroupState;
        private double lastTime = double.NaN;
        private readonly Transform owner, model;
        private readonly CharacterForecastPose resolver;

        internal CharacterForecastGhost(CharacterBehaviorDriver driver, CharacterBehaviorAnimator binding)
        {
            owner = driver.transform;
            Animator source = binding.PreviewAnimator;
            model = source.transform;
            resolver = new CharacterForecastPose(binding, driver);
            try
            {
                scene = EditorSceneManager.NewPreviewScene();
                root = EditorUtility.CreateGameObjectWithHideFlags("移动预测预览", HideFlags.HideAndDontSave);
                SceneManager.MoveGameObjectToScene(root, scene);
                CopyTree(model, root.transform);
                foreach (var skin in model.GetComponentsInChildren<SkinnedMeshRenderer>(true)) AddSkin(skin);
                foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter && filter.sharedMesh)
                        parts.Add(new Part { Source = renderer, Node = nodes[renderer.transform], Geometry = filter.sharedMesh });
                }
                animator = nodes[model].gameObject.AddComponent<Animator>();
                animator.avatar = source.avatar;
                animator.fireEvents = false;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animatorRoot = new RestPose(animator.transform);
                graph = PlayableGraph.Create("移动预测终点姿势");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                output = AnimationPlayableOutput.Create(graph, "终点", animator);
                graph.Play();
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath("16ca3ac1d9e541cf832f3b44628528cd"));
                if (shader) material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void CopyTree(Transform original, Transform parent)
        {
            var go = EditorUtility.CreateGameObjectWithHideFlags(original.name, HideFlags.HideAndDontSave);
            SceneManager.MoveGameObjectToScene(go, scene);
            Transform node = go.transform;
            node.SetParent(parent, false);
            if (original != model)
            {
                var reference = Application.isPlaying ? PrefabUtility.GetCorrespondingObjectFromSource(original) : null;
                var pose = reference ? reference : original;
                node.SetLocalPositionAndRotation(pose.localPosition, pose.localRotation);
                node.localScale = pose.localScale;
            }
            nodes.Add(original, node);
            rest.Add(new RestPose(node));
            for (int i = 0; i < original.childCount; i++) CopyTree(original.GetChild(i), node);
        }

        private void AddSkin(SkinnedMeshRenderer source)
        {
            if (!source.sharedMesh) return;
            var bones = source.bones;
            for (int i = 0; i < bones.Length; i++)
            {
                if (!bones[i]) continue;
                if (!nodes.TryGetValue(bones[i], out var mapped)) return;
                bones[i] = mapped;
            }
            var node = nodes[source.transform];
            var copy = node.gameObject.AddComponent<SkinnedMeshRenderer>();
            copy.sharedMesh = source.sharedMesh;
            copy.bones = bones;
            if (source.rootBone && nodes.TryGetValue(source.rootBone, out var rootBone)) copy.rootBone = rootBone;
            copy.localBounds = source.localBounds;
            copy.updateWhenOffscreen = true;
            for (int i = 0; i < source.sharedMesh.blendShapeCount; i++)
                copy.SetBlendShapeWeight(i, source.GetBlendShapeWeight(i));
            Mesh mesh = new Mesh { name = "终点蒙皮", hideFlags = HideFlags.HideAndDontSave };
            mesh.MarkDynamic();
            parts.Add(new Part { Source = source, Node = node, Skin = copy, Geometry = mesh });
        }

        private bool SetPose(AnimationClip clip, CharacterClipState groupState, double seconds)
        {
            bool changed = clip != activeClip || groupState != activeGroupState;
            if (changed)
            {
                output.SetSourcePlayable(Playable.Null);
                if (playable != null) graph.DestroySubgraph(playable.Root);
                playable = null;
                foreach (var bone in rest) bone.Apply();
                activeClip = clip;
                activeGroupState = groupState;
                if (clip)
                {
                    playable = new CharacterMotionPlayable(graph, clip, groupState?.Blend ?? default);
                    playable.Root.SetSpeed(0d);
                    output.SetSourcePlayable(playable.Root);
                }
            }
            if (!changed && seconds == lastTime) return false;
            lastTime = seconds;
            if (clip)
            {
                playable.Seek(seconds);
                graph.Evaluate(0f);
                animatorRoot.Apply();
            }
            return true;
        }

        internal void Draw(CharacterForecastSample endpoint, double clock)
        {
            if (!material || !owner || !model) return;
            AnimationClip clip = resolver.Sample(endpoint, clock, out double seconds, out var groupState);
            bool changed = SetPose(clip, groupState, seconds);
            Color tint = CharacterForecastTrail.Normal;
            tint.a = Mathf.Lerp(0.12f, 0.37f, Mathf.InverseLerp(0.05f, 0.65f, Vector3.Distance(owner.position, endpoint.Position)));
            material.SetColor("_Color", tint);
            if (!material.SetPass(0)) return;
            Matrix4x4 placement = Matrix4x4.TRS(endpoint.Position, endpoint.Rotation, owner.lossyScale) *
                owner.worldToLocalMatrix * model.localToWorldMatrix;
            foreach (var part in parts)
            {
                if (!part.Source || !part.Source.enabled || !part.Source.gameObject.activeInHierarchy)
                {
                    part.Baked = false;
                    continue;
                }
                if (part.Skin && (changed || !part.Baked))
                {
                    part.Skin.BakeMesh(part.Geometry, false);
                    part.Baked = true;
                }
                var matrix = placement * part.Node.localToWorldMatrix;
                for (int i = 0; i < part.Geometry.subMeshCount; i++) Graphics.DrawMeshNow(part.Geometry, matrix, i);
            }
        }

        public void Dispose()
        {
            if (graph.IsValid()) graph.Destroy();
            foreach (var part in parts)
                if (part.Skin && part.Geometry) Object.DestroyImmediate(part.Geometry);
            if (material) Object.DestroyImmediate(material);
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
        }
    }
}
