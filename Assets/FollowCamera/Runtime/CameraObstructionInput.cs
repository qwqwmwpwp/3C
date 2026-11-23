using System;
using UnityEngine;

namespace ThreeC.FollowCamera
{
    /// <summary>沿焦点到理想机位的线段探测场景遮挡，返回可用的最大相机距离。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("3C/跟随相机/遮挡输入")]
    public sealed class CameraObstructionInput : MonoBehaviour
    {
        [SerializeField, Tooltip("纳入相机遮挡探测的场景物理层。触发器和角色根层级下的碰撞体始终排除。")]
        private LayerMask obstructionLayers = Physics.DefaultRaycastLayers;
        [SerializeField, Min(0.01f), Tooltip("沿视线扫过的球体半径，单位为米。按相机近裁剪面设置。")]
        private float probeRadius = 0.2f;
        [SerializeField, Min(0f), Tooltip("在最早命中前从可用距离中减去的额外余量，单位为米。")]
        private float wallClearance = 0.08f;

        private Collider[] overlaps = new Collider[8];
        private RaycastHit[] hits = new RaycastHit[8];

        internal bool IsAvailable => isActiveAndEnabled;

        internal float GetAvailableDistance(Vector3 pivot, Vector3 boomDirection, float desiredDistance,
            CameraTargetInput targetInput)
        {
            float radius = Mathf.Max(0.01f, probeRadius);
            if (desiredDistance <= 0f || HasInitialOverlap(pivot, radius, targetInput))
                return 0f; // 球扫会漏掉起点重叠，此时将机位收回焦点。

            // 命中无序，缓冲区满时可能漏掉最近的遮挡，须扩容重查。
            int count;
            while ((count = Physics.SphereCastNonAlloc(pivot, radius, boomDirection, hits, desiredDistance,
                       obstructionLayers, QueryTriggerInteraction.Ignore)) == hits.Length)
                Array.Resize(ref hits, hits.Length * 2);

            float nearest = desiredDistance;
            for (int index = 0; index < count; index++)
            {
                RaycastHit hit = hits[index];
                if (hit.collider != null && !targetInput.Owns(hit.collider.transform))
                    nearest = Mathf.Min(nearest, Mathf.Max(0f, hit.distance - Mathf.Max(0f, wallClearance)));
            }

            return nearest;
        }

        private bool HasInitialOverlap(Vector3 pivot, float radius, CameraTargetInput targetInput)
        {
            int count;
            do
            {
                count = Physics.OverlapSphereNonAlloc(pivot, radius, overlaps, obstructionLayers,
                    QueryTriggerInteraction.Ignore);
                for (int index = 0; index < count; index++)
                {
                    Collider hit = overlaps[index];
                    if (hit != null && !targetInput.Owns(hit.transform))
                    {
                        Array.Clear(overlaps, 0, count);
                        return true;
                    }
                }

                Array.Clear(overlaps, 0, count);
                if (count < overlaps.Length)
                    return false;

                Array.Resize(ref overlaps, overlaps.Length * 2);
            } while (true);
        }
    }
}
