using System;
using UnityEngine;

namespace ThreeC.AnimationCamera
{
    /// <summary>Samples an imported character from the camera clock before its targets are captured.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("3C/Uma Character Playback")]
    public sealed class UmaCharacterPlayback : MonoBehaviour
    {
        [Tooltip("唯一播放时钟；Play、Seek 和 Stop 均在镜头读取骨骼前采样动作。")]
        public AnimationCameraPlayer player;
        [Tooltip("导入的 Character.prefab 实例根节点，必须与动作曲线的相对路径一致。")]
        public GameObject modelRoot;
        [Tooltip("使用原始 Transform 骨骼路径的 Generic 或 Legacy 动作。")]
        public AnimationClip[] clips = Array.Empty<AnimationClip>();
        [Min(0), Tooltip("Clips 数组中的动作序号；通过 SetClip 切换时会立即按当前镜头时间采样。")]
        public int selectedClip;
        public bool loopClip = true;
        [Tooltip("默认关闭，保留原始根运动。开启后只锁根节点和 Motion Root 的水平位移，保留高度与旋转。")]
        public bool inPlace;
        [Tooltip("可选的 Hip/运动骨骼，必须位于 Model Root 内。留空时仅锁角色根节点的水平位移。")]
        public Transform motionRoot;

        private AnimationCameraPlayer subscribedPlayer;
        private GameObject cachedRoot;
        private TransformPose[] initialPose = Array.Empty<TransformPose>();
        private AnimatorState[] animatorStates = Array.Empty<AnimatorState>();
        private LegacyState[] legacyStates = Array.Empty<LegacyState>();
        private BlendShapeState[] blendShapeStates = Array.Empty<BlendShapeState>();
        private string lastError;

        private struct TransformPose
        {
            public Transform transform;
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 scale;
        }

        private struct AnimatorState
        {
            public Animator animator;
            public bool enabled;
        }

        private struct LegacyState
        {
            public Animation animation;
            public bool enabled;
            public bool playAutomatically;
        }

        private struct BlendShapeState
        {
            public SkinnedMeshRenderer renderer;
            public Mesh mesh;
            public float[] weights;
        }

        private void OnEnable()
        {
            RefreshBindings();
        }

        private void OnDisable()
        {
            Unsubscribe();
            ReleaseModel();
        }

        private void OnDestroy()
        {
            Unsubscribe();
            ReleaseModel();
        }

        /// <summary>Call after changing Player or Model Root at runtime; does not advance a clock.</summary>
        [ContextMenu("Apply Configuration / Sample At Camera Time")]
        public void RefreshBindings()
        {
            Unsubscribe();
            if (isActiveAndEnabled && player != null)
            {
                subscribedPlayer = player;
                subscribedPlayer.BeforeSample += OnBeforeCameraSample;
            }

            if (player == null)
            {
                ReportError("Assign the camera Player, then call RefreshBindings or enable this component again.");
                return;
            }

            SampleAt(player.CurrentTime);
        }

        /// <summary>Switches actions and samples the camera's current time, including while paused.</summary>
        public bool SetClip(int index)
        {
            if (clips == null || index < 0 || index >= clips.Length || clips[index] == null)
                return ReportError("SetClip requires a valid index and an assigned clip in Clips.");

            selectedClip = index;
            return SampleAt(player != null ? player.CurrentTime : 0f);
        }

        /// <summary>Restores the cached starting pose, then samples the selected clip at an absolute time.</summary>
        public bool SampleAt(float time)
        {
            if (float.IsNaN(time) || float.IsInfinity(time))
                return ReportError("Sample time must be finite.");
            if (cachedRoot != modelRoot)
                ReleaseModel();
            if (modelRoot == null)
            {
                ReleaseModel();
                return ReportError("Assign the imported Character prefab instance as Model Root.");
            }
            if (clips == null || selectedClip < 0 || selectedClip >= clips.Length || clips[selectedClip] == null)
                return ReportError("Assign Clips and select an existing clip index.");

            AnimationClip clip = clips[selectedClip];
            if (clip.isHumanMotion)
                return ReportError("Humanoid clips are unsupported. Export a Generic or Legacy clip with Transform bone curves.");
            if (float.IsNaN(clip.length) || float.IsInfinity(clip.length) || clip.length <= 0f)
                return ReportError("The selected clip must have a finite, positive length.");
            if (motionRoot != null && motionRoot != modelRoot.transform && !motionRoot.IsChildOf(modelRoot.transform))
                return ReportError("Motion Root must belong to Model Root.");

            if (cachedRoot != modelRoot)
                CacheModel();

            // Reset channels omitted by the new clip or by an earlier/later sample. Arrays are cached.
            RestoreInitialPose();
            DisableAutomaticPlayback();
            float sampleTime = loopClip ? Mathf.Repeat(time, clip.length) : Mathf.Clamp(time, 0f, clip.length);
            try
            {
                clip.SampleAnimation(modelRoot, sampleTime);
            }
            catch (Exception exception)
            {
                RestoreInitialPose();
                return ReportError("Could not sample the selected clip: " + exception.Message);
            }

            if (inPlace)
            {
                LockHorizontalPosition(modelRoot.transform);
                if (motionRoot != null && motionRoot != modelRoot.transform)
                    LockHorizontalPosition(motionRoot);
            }

            lastError = null;
            return true;
        }

        /// <summary>Restores cached Transform TRS and blend shapes without changing the camera clock.</summary>
        public void RestoreInitialPose()
        {
            for (int i = 0; i < initialPose.Length; i++)
            {
                TransformPose pose = initialPose[i];
                if (pose.transform == null) continue;
                pose.transform.localPosition = pose.position;
                pose.transform.localRotation = pose.rotation;
                pose.transform.localScale = pose.scale;
            }

            for (int i = 0; i < blendShapeStates.Length; i++)
            {
                BlendShapeState state = blendShapeStates[i];
                if (state.renderer == null || state.renderer.sharedMesh != state.mesh) continue;
                for (int weight = 0; weight < state.weights.Length; weight++)
                    state.renderer.SetBlendShapeWeight(weight, state.weights[weight]);
            }
        }

        /// <summary>Returns the clone's starting pose and the automatic-player states owned by this sampler.</summary>
        [ContextMenu("Restore Model / Release Sampled State")]
        public void RestoreModel()
        {
            ReleaseModel();
        }

        private void OnBeforeCameraSample(float time)
        {
            SampleAt(time);
        }

        private void Unsubscribe()
        {
            if (subscribedPlayer != null)
                subscribedPlayer.BeforeSample -= OnBeforeCameraSample;
            subscribedPlayer = null;
        }

        private void CacheModel()
        {
            ReleaseModel();
            cachedRoot = modelRoot;
            Transform[] transforms = cachedRoot.GetComponentsInChildren<Transform>(true);
            initialPose = new TransformPose[transforms.Length];
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform node = transforms[i];
                initialPose[i] = new TransformPose
                {
                    transform = node, position = node.localPosition,
                    rotation = node.localRotation, scale = node.localScale
                };
            }

            Animator[] animators = cachedRoot.GetComponentsInChildren<Animator>(true);
            animatorStates = new AnimatorState[animators.Length];
            for (int i = 0; i < animators.Length; i++)
                animatorStates[i] = new AnimatorState { animator = animators[i], enabled = animators[i].enabled };

            Animation[] legacy = cachedRoot.GetComponentsInChildren<Animation>(true);
            legacyStates = new LegacyState[legacy.Length];
            for (int i = 0; i < legacy.Length; i++)
                legacyStates[i] = new LegacyState
                {
                    animation = legacy[i], enabled = legacy[i].enabled,
                    playAutomatically = legacy[i].playAutomatically
                };

            SkinnedMeshRenderer[] renderers = cachedRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            blendShapeStates = new BlendShapeState[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                Mesh mesh = renderers[i].sharedMesh;
                float[] weights = mesh != null ? new float[mesh.blendShapeCount] : Array.Empty<float>();
                for (int weight = 0; weight < weights.Length; weight++)
                    weights[weight] = renderers[i].GetBlendShapeWeight(weight);
                blendShapeStates[i] = new BlendShapeState { renderer = renderers[i], mesh = mesh, weights = weights };
            }

            DisableAutomaticPlayback();
        }

        private void DisableAutomaticPlayback()
        {
            for (int i = 0; i < animatorStates.Length; i++)
                if (animatorStates[i].animator != null)
                    animatorStates[i].animator.enabled = false;
            for (int i = 0; i < legacyStates.Length; i++)
            {
                Animation animation = legacyStates[i].animation;
                if (animation == null) continue;
                animation.playAutomatically = false;
                if (animation.isPlaying) animation.Stop();
                animation.enabled = false;
            }
        }

        private void LockHorizontalPosition(Transform node)
        {
            for (int i = 0; i < initialPose.Length; i++)
            {
                if (initialPose[i].transform != node) continue;
                Vector3 position = node.localPosition;
                node.localPosition = new Vector3(initialPose[i].position.x, position.y, initialPose[i].position.z);
                return;
            }
        }

        private void ReleaseModel()
        {
            RestoreInitialPose();
            for (int i = 0; i < animatorStates.Length; i++)
                if (animatorStates[i].animator != null)
                    animatorStates[i].animator.enabled = animatorStates[i].enabled;
            for (int i = 0; i < legacyStates.Length; i++)
            {
                LegacyState state = legacyStates[i];
                if (state.animation == null) continue;
                state.animation.playAutomatically = state.playAutomatically;
                state.animation.enabled = state.enabled;
            }

            cachedRoot = null;
            initialPose = Array.Empty<TransformPose>();
            animatorStates = Array.Empty<AnimatorState>();
            legacyStates = Array.Empty<LegacyState>();
            blendShapeStates = Array.Empty<BlendShapeState>();
        }

        private bool ReportError(string message)
        {
            if (lastError != message)
                Debug.LogError("[UmaCharacterPlayback] " + message, this);
            lastError = message;
            return false;
        }
    }
}
