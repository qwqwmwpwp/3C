using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>识别无孔且绕序朝上的平面矩形网格；不依赖素材名称或内置模型的实例 ID。</summary>
    internal static class CharacterForecastPlane
    {
        private sealed class Classification
        {
            public Classification() { }
            internal bool IsRectangle;
            internal int Frame = -1;
            internal int VertexCount;
            internal Bounds Bounds;
        }

        private static readonly ConditionalWeakTable<Mesh, Classification> verified = new ConditionalWeakTable<Mesh, Classification>();

        internal static bool IsRectangle(Mesh mesh)
        {
            if (!mesh || !mesh.isReadable) return false;
            var entry = verified.GetOrCreateValue(mesh);
            if (entry.Frame != Time.frameCount || entry.VertexCount != mesh.vertexCount || !entry.Bounds.Equals(mesh.bounds))
            {
                entry.Frame = Time.frameCount;
                entry.VertexCount = mesh.vertexCount;
                entry.Bounds = mesh.bounds;
                entry.IsRectangle = Inspect(mesh);
            }
            return entry.IsRectangle;
        }

        private static bool Inspect(Mesh mesh)
        {
            if (mesh.vertexCount < 4 || mesh.vertexCount > 65536 || mesh.bounds.size.y > 0.000001f) return false;
            var vertices = mesh.vertices;
            var xs = new SortedSet<float>();
            var zs = new SortedSet<float>();
            foreach (var p in vertices)
            {
                if (Mathf.Abs(p.y - vertices[0].y) > 0.000001f) return false;
                xs.Add(Grid(p.x));
                zs.Add(Grid(p.z));
            }
            if (xs.Count < 2 || zs.Count < 2) return false;
            var faces = mesh.triangles;
            long cells = (long)(xs.Count - 1) * (zs.Count - 1);
            if (faces.Length != 6 * cells) return false;
            var x = Indices(xs);
            var z = Indices(zs);
            var halves = new byte[(int)cells];
            for (int i = 0; i < faces.Length; i += 3)
            {
                var a = vertices[faces[i]]; var b = vertices[faces[i + 1]]; var c = vertices[faces[i + 2]];
                if (Vector3.Cross(b - a, c - a).y <= 0f) return false;
                int ax = x[Grid(a.x)], bx = x[Grid(b.x)], cx = x[Grid(c.x)], az = z[Grid(a.z)], bz = z[Grid(b.z)], cz = z[Grid(c.z)];
                int left = Mathf.Min(ax, Mathf.Min(bx, cx)), bottom = Mathf.Min(az, Mathf.Min(bz, cz));
                if (Mathf.Max(ax, Mathf.Max(bx, cx)) != left + 1 || Mathf.Max(az, Mathf.Max(bz, cz)) != bottom + 1) return false;
                int corners = 1 << (ax - left + 2 * (az - bottom)) | 1 << (bx - left + 2 * (bz - bottom)) |
                    1 << (cx - left + 2 * (cz - bottom));
                int missing = 15 ^ corners;
                int cell = left + (xs.Count - 1) * bottom;
                if ((halves[cell] & missing) != 0) return false;
                halves[cell] |= (byte)missing;
            }
            foreach (byte pair in halves) if (pair != 9 && pair != 6) return false;
            return true;
        }

        private static float Grid(float value) => Mathf.Round(value * 1000000f) * 0.000001f;

        private static Dictionary<float, int> Indices(SortedSet<float> values)
        {
            var result = new Dictionary<float, int>(values.Count);
            foreach (float value in values) result.Add(value, result.Count);
            return result;
        }
    }
}
