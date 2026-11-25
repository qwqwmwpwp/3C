using UnityEngine;

namespace ThreeC.FollowCamera
{
    /// <summary>一次性采集本渲染帧的鼠标角位移和回正按键。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("3C/跟随相机/操作输入")]
    public sealed class CameraControlInput : MonoBehaviour
    {
        [SerializeField, Tooltip("旧版 Input Manager 中的鼠标水平轴。")]
        private string horizontalAxis = "Mouse X";
        [SerializeField, Tooltip("旧版 Input Manager 中的鼠标垂直轴。")]
        private string verticalAxis = "Mouse Y";
        [SerializeField, Tooltip("按下一次，将水平相机视角平滑转到角色当时的面向。")]
        private KeyCode recenterKey = KeyCode.C;
        [SerializeField, Min(0f), Tooltip("水平轴每单位输入产生的偏航角，单位为度。")]
        private float horizontalSensitivity = 2f;
        [SerializeField, Min(0f), Tooltip("垂直轴每单位输入产生的俯仰角，单位为度。")]
        private float verticalSensitivity = 2f;
        [SerializeField, Tooltip("反转鼠标的垂直视角控制。")]
        private bool invertY;

        private bool paused;
        private bool discardFirstSample = true;

        private bool IsAvailable => isActiveAndEnabled && Application.isFocused && !paused && Time.timeScale > 0f;
        internal bool HasContinuousSample => IsAvailable && !discardFirstSample; // 发布后失焦或停用会立即使本帧采样失效。

        internal void DiscardNextSample() => discardFirstSample = true;

        internal bool TryRead(out Vector2 orbitDegrees, out bool recenterPressed, out bool resumed)
        {
            orbitDegrees = default;
            recenterPressed = false;
            resumed = false;
            if (!IsAvailable)
            {
                DiscardNextSample();
                return false;
            }

            if (discardFirstSample) // 恢复时丢弃积累的鼠标位移。
            {
                discardFirstSample = false;
                resumed = true;
                return true;
            }

            float x = string.IsNullOrEmpty(horizontalAxis) ? 0f : Input.GetAxisRaw(horizontalAxis);
            float y = string.IsNullOrEmpty(verticalAxis) ? 0f : Input.GetAxisRaw(verticalAxis);
            orbitDegrees = new Vector2(x * horizontalSensitivity, y * verticalSensitivity * (invertY ? 1f : -1f));
            recenterPressed = recenterKey != KeyCode.None && Input.GetKeyDown(recenterKey);
            return true;
        }

        private void OnEnable() => DiscardNextSample();
        private void OnApplicationFocus(bool focused) => DiscardNextSample();

        private void OnApplicationPause(bool value)
        {
            paused = value;
            DiscardNextSample();
        }
    }
}
