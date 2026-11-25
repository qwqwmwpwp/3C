using ThreeC.FollowCamera.Contracts;
using UnityEngine;

namespace ThreeC.FollowCamera
{
    /// <summary>向角色发布本渲染帧的水平相机方向。写入口仅供相机运行时程序集使用。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("3C/跟随相机/操作方向输出")]
    public sealed class CameraControlOutput : CameraControlSource
    {
        private Vector3 forward;
        private Vector3 right;
        private int publishedFrame;
        private CameraControlInput publishedInput;

        internal bool HasCurrentBasis => isActiveAndEnabled && Time.frameCount == publishedFrame
            && publishedInput != null && publishedInput.HasContinuousSample;

        public override bool TryGetBasis(out Vector3 forward, out Vector3 right)
        {
            if (HasCurrentBasis)
            {
                forward = this.forward;
                right = this.right;
                return true;
            }

            forward = default;
            right = default;
            return false;
        }

        internal void Publish(Vector3 forward, Vector3 right, CameraControlInput input)
        {
            this.forward = forward;
            this.right = right;
            publishedFrame = Time.frameCount;
            publishedInput = input;
        }

        internal void Invalidate() => publishedInput = null;

        private void OnDisable() => Invalidate();
    }
}
