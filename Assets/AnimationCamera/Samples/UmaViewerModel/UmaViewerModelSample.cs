// 模型与 Legacy 动画来源为 0xcjy/UmaViewer，提交 12c77723169259071ce86e2eab9c871de4a0919d.
using UnityEngine;

namespace ThreeC.AnimationCamera
{
    /// <summary>按镜头时钟采样导入的演示模型姿态，暂停期间也同步当前时间。</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)] // 镜头播放器以默认顺序 0 在 LateUpdate 推进时钟，本组件随后采样。
    [AddComponentMenu("3C/UmaViewer 模型采样")]
    public sealed class UmaViewerModelSample : MonoBehaviour
    {
        [Tooltip("提供采样时间的镜头播放器，本组件在其 LateUpdate 之后同步模型姿态。")]
        public AnimationCameraPlayer player;
        [Tooltip("导入模型的根节点，须与动作曲线中的相对路径对应。")]
        public GameObject modelRoot;
        [Tooltip("用于演示的 Legacy 动作，按镜头时间循环采样。")]
        public AnimationClip clip;
        [Tooltip("用于锁定水平位移的髋骨节点，保留垂直运动和旋转。")]
        public Transform hip;

        private Vector3 rootPosition;
        private Quaternion rootRotation;
        private Vector3 rootScale;
        private Vector2 hipHorizontal;
        private bool initialized;

        private void OnEnable()
        {
            initialized = player != null && modelRoot != null && clip != null && hip != null;
            if (!initialized)
            {
                Debug.LogError("[UmaViewerModelSample] 请指定镜头播放器、导入模型、动作和髋骨节点。", this);
                return;
            }

            // 关闭导入模型的 Legacy 自动播放，使动作采样统一跟随镜头时钟。
            Animation legacy = modelRoot.GetComponent<Animation>();
            if (legacy != null)
            {
                legacy.playAutomatically = false;
                legacy.Stop();
                legacy.enabled = false;
            }

            Transform root = modelRoot.transform;
            rootPosition = root.localPosition;
            rootRotation = root.localRotation;
            rootScale = root.localScale;
            clip.SampleAnimation(modelRoot, 0f);
            hipHorizontal = new Vector2(hip.localPosition.x, hip.localPosition.z);
            SamplePose();
        }

        private void LateUpdate()
        {
            if (initialized) SamplePose();
        }

        private void SamplePose()
        {
            if (player == null || modelRoot == null || clip == null || hip == null) return;
            float time = clip.length > 0f ? Mathf.Repeat(player.CurrentTime, clip.length) : 0f;
            clip.SampleAnimation(modelRoot, time);

            // 将演出固定在原地，以匹配已有特写镜头轨道。
            // 锁定髋骨水平位移，并保留髋骨的垂直运动和旋转。
            Vector3 position = hip.localPosition;
            hip.localPosition = new Vector3(hipHorizontal.x, position.y, hipHorizontal.y);
            Transform root = modelRoot.transform;
            root.localPosition = rootPosition;
            root.localRotation = rootRotation;
            root.localScale = rootScale;
        }
    }
}
