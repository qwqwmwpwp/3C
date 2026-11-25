using UnityEngine;

namespace ThreeC.FollowCamera.Contracts
{
    /// <summary>角色可绑定的只读相机操作方向来源。</summary>
    public abstract class CameraControlSource : MonoBehaviour
    {
        /// <summary>当前渲染帧已发布时，返回世界 XZ 平面上互相正交的单位前向和右向。</summary>
        public abstract bool TryGetBasis(out Vector3 forward, out Vector3 right);
    }
}
