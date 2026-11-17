// Model and Legacy clip: 0xcjy/UmaViewer, commit 12c77723169259071ce86e2eab9c871de4a0919d.
using UnityEngine;

namespace ThreeC.AnimationCamera
{
    /// <summary>Samples the imported demo pose from the camera clock, including while paused.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)] // Camera player advances its clock in LateUpdate at order zero.
    [AddComponentMenu("3C/UmaViewer Model Sample")]
    public sealed class UmaViewerModelSample : MonoBehaviour
    {
        public AnimationCameraPlayer player;
        public GameObject modelRoot;
        public AnimationClip clip;
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
                Debug.LogError("[UmaViewerModelSample] Assign the camera player, imported model, clip and hip.", this);
                return;
            }

            // The imported Legacy component must never advance a second animation clock.
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

            // Keep this performance in place for the existing close-up camera track.
            // Preserve vertical motion and all rotations; remove only hip horizontal travel.
            Vector3 position = hip.localPosition;
            hip.localPosition = new Vector3(hipHorizontal.x, position.y, hipHorizontal.y);
            Transform root = modelRoot.transform;
            root.localPosition = rootPosition;
            root.localRotation = rootRotation;
            root.localScale = rootScale;
        }
    }
}
