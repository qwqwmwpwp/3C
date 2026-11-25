using ThreeC.FollowCamera.Contracts;
using UnityEngine;

namespace ThreeC.FollowCamera
{
    /// <summary>采集目标朝向、根位置及固定步采样，为渲染帧生成观察焦点。</summary>
    [DefaultExecutionOrder(100)] // 在角色固定步之后采集移动结果。
    [DisallowMultipleComponent]
    [AddComponentMenu("3C/跟随相机/目标输入")]
    public sealed class CameraTargetInput : MonoBehaviour
    {
        [SerializeField, Tooltip("跟随目标的角色根节点。其自身和子节点的碰撞体都会从遮挡中排除。")]
        private Transform target;
        [SerializeField, Tooltip("焦点相对于根节点的世界竖直偏移，单位为米。")]
        private float pivotHeight = 1.55f;
        [SerializeField, Tooltip("目标未提供模型显示位置时，使用相邻固定步位置生成平滑的渲染焦点。")]
        private bool interpolateFixedSteps = true;
        [SerializeField, Min(0.01f), Tooltip("两次采样间的位移达到该距离时视为传送，单位为米。")]
        private float teleportDistance = 3f;

        private Transform trackedTarget;
        private CameraRenderPositionSource renderSource;
        private ICameraTargetContinuity continuity;
        private int observedCut;
        private int targetVersion;
        private int cutVersion;
        private bool hasRawPosition;
        private Vector3 lastRawPosition;
        private bool hasFixedSample;
        private Vector3 previousFixedPosition;
        private Vector3 latestFixedPosition;
        private double latestFixedTime;

        /// <summary>重新绑定跟随目标。传入空引用时暂停相机跟随。</summary>
        public void SetTarget(Transform newTarget)
        {
            if (target == newTarget)
                return;

            target = newTarget;
            ClearTarget();
        }

        /// <summary>外部系统主动传送角色后可调用，立即结束上一段位置插值和遮挡过渡。</summary>
        public void MarkTeleported() => ClearHistory();

        internal bool TryRead(bool renderPosition, out CameraTargetSample sample)
        {
            sample = default;
            if (!TryReadRawPosition(out Vector3 position))
                return false;

            if (renderPosition && renderSource != null)
            {
                position = renderSource.RenderPosition; // 与模型共用显示位置，避免二次插值。
            }
            else if (renderPosition && interpolateFixedSteps && hasFixedSample && position == latestFixedPosition)
            {
                float phase = (float)((Time.timeAsDouble - latestFixedTime) / Time.fixedDeltaTime);
                position = Vector3.Lerp(previousFixedPosition, latestFixedPosition, phase); // 延迟一个固定步，使渲染帧平滑跟随。
            }

            sample = new CameraTargetSample(position + Vector3.up * pivotHeight, trackedTarget.forward,
                targetVersion, cutVersion);
            return true;
        }

        internal bool Owns(Transform candidate)
        {
            return trackedTarget != null && (candidate == trackedTarget || candidate.IsChildOf(trackedTarget));
        }

        private void FixedUpdate()
        {
            if (!interpolateFixedSteps || !TryReadRawPosition(out Vector3 position) || renderSource != null)
            {
                hasFixedSample = false;
                return;
            }

            previousFixedPosition = hasFixedSample ? latestFixedPosition : position;
            latestFixedPosition = position;
            latestFixedTime = Time.fixedTimeAsDouble;
            hasFixedSample = true;
        }

        // 读取目标实际位置，在显式位置边界或距离跳变时撤销旧插值。
        private bool TryReadRawPosition(out Vector3 position)
        {
            position = default;
            if (!isActiveAndEnabled || target == null || !target.gameObject.activeInHierarchy)
            {
                if (hasRawPosition)
                    ClearTarget();
                return false;
            }

            if (trackedTarget != target)
            {
                ClearTarget();
                trackedTarget = target;
                renderSource = target.GetComponent<CameraRenderPositionSource>();
                continuity = target.GetComponent<ICameraTargetContinuity>();
                observedCut = continuity?.CameraCutVersion ?? 0;
            }
            else if (continuity is Object source && source && observedCut != continuity.CameraCutVersion)
            {
                observedCut = continuity.CameraCutVersion;
                ClearHistory();
            }

            position = trackedTarget.position;
            float threshold = Mathf.Max(0.01f, teleportDistance);
            if (hasRawPosition && (position - lastRawPosition).sqrMagnitude >= threshold * threshold)
                ClearHistory();

            lastRawPosition = position;
            hasRawPosition = true;
            return true;
        }

        private void OnDisable() => ClearTarget();

        // 解除目标连续性来源，并清理切换前的跟随历史。
        private void ClearTarget()
        {
            trackedTarget = null;
            renderSource = null;
            continuity = null;
            targetVersion++;
            ClearHistory();
        }

        private void ClearHistory()
        {
            hasRawPosition = false;
            hasFixedSample = false;
            cutVersion++;
        }
    }

    internal readonly struct CameraTargetSample
    {
        internal readonly Vector3 Pivot;
        internal readonly Vector3 Facing;
        internal readonly int TargetVersion;
        internal readonly int CutVersion;

        internal CameraTargetSample(Vector3 pivot, Vector3 facing, int targetVersion, int cutVersion)
        {
            Pivot = pivot;
            Facing = facing;
            TargetVersion = targetVersion;
            CutVersion = cutVersion;
        }
    }
}
