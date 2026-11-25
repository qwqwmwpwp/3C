using UnityEngine;

namespace ThreeC.FollowCamera
{
    /// <summary>将已求解的世界位置和旋转提交到绑定的玩法相机。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("3C/跟随相机/相机姿态输出")]
    public sealed class CameraPoseOutput : MonoBehaviour
    {
        [SerializeField, Tooltip("显式绑定由本模块独占驱动的玩法相机。")]
        private Camera outputCamera;
        [SerializeField, Range(1f, 179f), Tooltip("玩法相机的垂直视野角，单位为度。")]
        private float fieldOfView = 60f;

        internal bool IsAvailable => isActiveAndEnabled && outputCamera != null && outputCamera.isActiveAndEnabled;

        internal void Apply(Vector3 position, Quaternion rotation)
        {
            if (!IsAvailable)
                return;

            outputCamera.transform.SetPositionAndRotation(position, rotation);
            if (outputCamera.fieldOfView != fieldOfView)
                outputCamera.fieldOfView = fieldOfView;
        }

        private void Reset() => outputCamera = GetComponent<Camera>();
    }
}
