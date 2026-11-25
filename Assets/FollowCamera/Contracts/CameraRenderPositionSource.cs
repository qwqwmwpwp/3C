using UnityEngine;

namespace ThreeC.FollowCamera.Contracts
{
    // 提供目标位置连续性的代次，使小于距离阈值的显式瞬移同样切断相机插值。
    public interface ICameraTargetContinuity
    {
        int CameraCutVersion { get; }
    }

    /// <summary>跟随目标可在自身根节点提供模型本帧的世界位置，供镜头直接取样。</summary>
    public abstract class CameraRenderPositionSource : MonoBehaviour
    {
        public abstract Vector3 RenderPosition { get; }
    }
}
