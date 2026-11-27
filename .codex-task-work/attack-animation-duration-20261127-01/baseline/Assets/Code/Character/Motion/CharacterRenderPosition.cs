using ThreeC.FollowCamera.Contracts;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>向相机暴露随刚体插值后的模型位置，模型子节点直接继承物理根的显示姿态。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterBehaviorDriver))]
    [AddComponentMenu("3C/角色显示位置")]
    public sealed class CharacterRenderPosition : CameraRenderPositionSource
    {
        [SerializeField, Tooltip("包含模型、骨架和挂点的显示子节点。")]
        private Transform visualRoot;

        /// <summary>相机直接采样本帧的显示位置，与模型共用刚体插值。</summary>
        public override Vector3 RenderPosition => visualRoot != null ? visualRoot.position : transform.position;
    }
}
