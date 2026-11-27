using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>局部静态表面的只读快照。只有包围盒能排除碰撞、或扫掠完全位于单个平面内部时直接求解。</summary>
    internal sealed class CharacterForecastWorld
    {
        private readonly struct Surface
        {
            internal readonly Bounds Bounds;
            private readonly bool rectangular, plane;
            private readonly Vector3 center, x, y, z, half;

            internal Surface(Collider collider)
            {
                this = default;
                Bounds = collider.bounds;
                if (collider.attachedRigidbody) return;
                Vector3 localCenter, size;
                if (collider is BoxCollider box)
                {
                    localCenter = box.center;
                    size = box.size;
                }
                else if (collider is MeshCollider mesh && !mesh.convex && CharacterForecastPlane.IsRectangle(mesh.sharedMesh))
                {
                    var bounds = mesh.sharedMesh.bounds;
                    localCenter = bounds.center;
                    size = bounds.size;
                    plane = true;
                }
                else return;
                var matrix = collider.transform.localToWorldMatrix;
                if (matrix.determinant <= 0f) return;
                center = matrix.MultiplyPoint3x4(localCenter);
                Vector3 u = matrix.MultiplyVector(Vector3.right), v = matrix.MultiplyVector(Vector3.up);
                Vector3 w = matrix.MultiplyVector(Vector3.forward);
                x = u.normalized; y = v.normalized; z = w.normalized;
                rectangular = Mathf.Abs(Vector3.Dot(x,y)) < 0.000001f && Mathf.Abs(Vector3.Dot(x,z)) < 0.000001f &&
                    Mathf.Abs(Vector3.Dot(y,z)) < 0.000001f;
                half = Vector3.Scale(size, new Vector3(u.magnitude, v.magnitude, w.magnitude)) * 0.5f;
            }

            private Vector3 Local(Vector3 point)
            {
                var delta = point - center;
                return new Vector3(Vector3.Dot(delta,x), Vector3.Dot(delta,y), Vector3.Dot(delta,z));
            }

            internal bool Sample(in ForecastQuery q, ref ForecastHit nearest)
            {
                if (!rectangular) return false;
                var a = Local(q.A);
                var b = q.Ray ? a : Local(q.B);
                var dir = new Vector3(Vector3.Dot(q.Direction,x), Vector3.Dot(q.Direction,y), Vector3.Dot(q.Direction,z));
                Vector3 radius = Vector3.one * q.Radius;
                Vector3 low = Vector3.Min(a,b) - radius, high = Vector3.Max(a,b) + radius;
                Vector3 sweep = dir * q.Distance;
                var min = low + Vector3.Min(sweep,Vector3.zero);
                var max = high + Vector3.Max(sweep,Vector3.zero);
                for (int k = 0; k < 3; k++)
                    if (min[k] > half[k] || max[k] < -half[k]) return true;
                for (int k = 0; k < 3; k++)
                {
                    int u = (k + 1) % 3, v = (k + 2) % 3;
                    if (min[u] <= -half[u] || max[u] >= half[u] || min[v] <= -half[v] || max[v] >= half[v]) continue;
                    bool positive = low[k] >= half[k];
                    if (!positive && high[k] > -half[k] || plane && (!positive || k != 1)) return false;
                    float approach = positive ? -dir[k] : dir[k];
                    if (approach <= (q.Ray ? 0f : 0.0001f)) return true;
                    float distance = (positive ? low[k] - half[k] : -half[k] - high[k]) / approach;
                    if (distance > q.Distance || nearest.Found && distance >= nearest.Distance) return true;
                    Vector3 normal = (k == 0 ? x : k == 1 ? y : z) * (positive ? 1f : -1f);
                    var tip = q.Ray || (positive ? a[k] <= b[k] : a[k] >= b[k]) ? q.A : q.B;
                    nearest = new ForecastHit(normal, tip + q.Direction * distance - normal * q.Radius, distance);
                    return true;
                }
                return false;
            }
        }

        private readonly Bounds area;
        private readonly Surface[] surfaces;

        internal CharacterForecastWorld(CharacterForecastGeometry geometry, Vector3 origin, float reach)
        {
            area = new Bounds(origin, Vector3.one * (2f * reach));
            var buffer = geometry.Nearby;
            int count = geometry.Scene.OverlapBox(origin, area.extents, buffer, Quaternion.identity, geometry.Mask,
                QueryTriggerInteraction.Ignore);
            if (count == buffer.Length) return; // 拥挤区域交给批量物理查询。
            var included = new List<Surface>(count);
            for (int i = 0; i < count; i++)
            {
                var collider = buffer[i];
                buffer[i] = null;
                if (collider && geometry.Includes(collider)) included.Add(new Surface(collider));
            }
            surfaces = included.ToArray();
        }

        internal bool TryQuery(in ForecastQuery query, out ForecastHit nearest)
        {
            nearest = default;
            if (surfaces == null) return false;
            Vector3 travel = query.Direction * query.Distance;
            Vector3 radius = Vector3.one * query.Radius;
            Vector3 b = query.Ray ? query.A : query.B;
            Vector3 startMin = Vector3.Min(query.A, b) - radius;
            Vector3 startMax = Vector3.Max(query.A, b) + radius;
            Vector3 min = startMin + Vector3.Min(Vector3.zero, travel);
            Vector3 max = startMax + Vector3.Max(Vector3.zero, travel);
            if (!area.Contains(min) || !area.Contains(max)) return false;
            foreach (var surface in surfaces)
            {
                Vector3 lo = surface.Bounds.min, hi = surface.Bounds.max;
                if (min.x > hi.x || max.x < lo.x || min.y > hi.y || max.y < lo.y ||
                    min.z > hi.z || max.z < lo.z) continue;
                if (!surface.Sample(query, ref nearest)) return false;
            }
            return true;
        }
    }
}
