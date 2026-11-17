using System;
using UnityEngine;

namespace ThreeC.AnimationCamera
{
    /// <summary>在镜头采集目标变换之前，依据镜头时钟采样导入角色的动作。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("3C/Uma 角色动作播放")]
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
        [Tooltip("按动作自身时长循环采样。关闭后将采样时间限制在动作首尾之间。")]
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

        /// <summary>运行时修改 Player 或 Model Root 后调用，重建订阅并按当前镜头时间采样。</summary>
        [ContextMenu("应用配置并按镜头时间采样")]
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
                ReportError("请指定镜头播放器 Player，再调用 RefreshBindings 或重新启用本组件。");
                return;
            }

            SampleAt(player.CurrentTime);
        }

        /// <summary>按从零开始的有效索引切换动作，并在暂停期间也按当前镜头时间采样。返回采样是否成功。</summary>
        public bool SetClip(int index)
        {
            if (clips == null || index < 0 || index >= clips.Length || clips[index] == null)
                return ReportError("SetClip 需要有效索引，且 Clips 对应位置已指定动作。");

            selectedClip = index;
            return SampleAt(player != null ? player.CurrentTime : 0f);
        }

        /// <summary>恢复缓存的初始姿态后，按绝对时间（秒）采样所选动作。循环时取模，否则限制在动作时长内。返回采样是否成功。</summary>
        public bool SampleAt(float time)
        {
            if (float.IsNaN(time) || float.IsInfinity(time))
                return ReportError("采样时间必须为有限数值，单位为秒。");
            if (cachedRoot != modelRoot)
                ReleaseModel();
            if (modelRoot == null)
            {
                ReleaseModel();
                return ReportError("请将导入的 Character 预制体实例指定为 Model Root。");
            }
            if (clips == null || selectedClip < 0 || selectedClip >= clips.Length || clips[selectedClip] == null)
                return ReportError("请配置 Clips 并选择有效的动作索引。");

            AnimationClip clip = clips[selectedClip];
            if (clip.isHumanMotion)
                return ReportError("当前采样入口支持包含 Transform 骨骼曲线的 Generic 或 Legacy 动作，请使用对应格式导出。");
            if (float.IsNaN(clip.length) || float.IsInfinity(clip.length) || clip.length <= 0f)
                return ReportError("所选动作的时长必须为有限正数。");
            if (motionRoot != null && motionRoot != modelRoot.transform && !motionRoot.IsChildOf(modelRoot.transform))
                return ReportError("Motion Root 必须位于 Model Root 层级内。");

            if (cachedRoot != modelRoot)
                CacheModel();

            // 恢复新动作或其他采样时刻未覆盖的通道，复用已缓存的姿态数组。
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
                return ReportError("所选动作采样失败，原始错误为 " + exception.Message);
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

        /// <summary>恢复缓存的局部位置、旋转、缩放和混合形状权重，镜头时钟继续由播放器管理。</summary>
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

        /// <summary>恢复模型副本的初始姿态及本组件接管的自动播放状态，并释放缓存。</summary>
        [ContextMenu("恢复模型并释放采样状态")]
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
