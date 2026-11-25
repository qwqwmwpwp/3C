using UnityEngine;

namespace ThreeC.FollowCamera.Contracts
{
    /// <summary>跟随目标可在自身根节点提供模型本帧的世界位置，供镜头直接取样。</summary>
    public abstract class CameraRenderPositionSource : MonoBehaviour
    {
        public abstract Vector3 RenderPosition { get; }
    }
}
