using System.Collections.Generic;
using UnityEngine;

namespace Battalion.View
{
    /// <summary>
    /// 毎フレーム作り直す、頂点色つきの三角形のメッシュ（駒・経路・円・矢印）。線は幅を持った四角形で描く。
    /// </summary>
    public sealed class ShapeMesh
    {
        readonly Mesh _mesh;
        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<Color32> _c = new List<Color32>();
        readonly List<int> _t = new List<int>();

        public GameObject Object { get; }

        public ShapeMesh(string name, int sortingOrder, Transform parent)
        {
            Object = new GameObject(name);
            Object.transform.SetParent(parent, false);
            _mesh = new Mesh { name = name };
            _mesh.MarkDynamic();
            Object.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = Object.AddComponent<MeshRenderer>();
            r.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            r.sortingOrder = sortingOrder;
        }

        public void Clear()
        {
            _v.Clear();
            _c.Clear();
            _t.Clear();
        }

        public void Apply()
        {
            _mesh.Clear();
            _mesh.indexFormat = _v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            _mesh.SetVertices(_v);
            _mesh.SetColors(_c);
            _mesh.SetTriangles(_t, 0);
            _mesh.RecalculateBounds();
        }

        public void Triangle(Vector2 a, Vector2 b, Vector2 c, Color32 col)
        {
            int i = _v.Count;
            _v.Add(a);
            _v.Add(b);
            _v.Add(c);
            _c.Add(col);
            _c.Add(col);
            _c.Add(col);
            _t.Add(i);
            _t.Add(i + 1);
            _t.Add(i + 2);
        }

        public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color32 col)
        {
            Triangle(a, b, c, col);
            Triangle(a, c, d, col);
        }

        public void Line(Vector2 a, Vector2 b, float width, Color32 col)
        {
            var d = b - a;
            if (d.sqrMagnitude < 1e-6f) return;
            var n = new Vector2(-d.y, d.x).normalized * (width / 2);
            Quad(a - n, b - n, b + n, a + n, col);
        }

        /// <summary>破線（<paramref name="dash"/> の長さで描いて同じ長さだけ空ける）。</summary>
        public void DashedLine(Vector2 a, Vector2 b, float width, float dash, Color32 col)
        {
            float len = Vector2.Distance(a, b);
            if (len < 1e-3f) return;
            var dir = (b - a) / len;
            for (float s = 0; s < len; s += dash * 2)
                Line(a + dir * s, a + dir * Mathf.Min(len, s + dash), width, col);
        }

        public void Polyline(IList<Vector2> pts, float width, Color32 col)
        {
            for (int k = 1; k < pts.Count; k++) Line(pts[k - 1], pts[k], width, col);
        }

        public void Circle(Vector2 center, float radius, float width, Color32 col, int segments = 64)
        {
            for (int k = 0; k < segments; k++)
            {
                float a0 = Mathf.PI * 2 * k / segments, a1 = Mathf.PI * 2 * (k + 1) / segments;
                Line(center + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radius,
                    center + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius, width, col);
            }
        }

        public void Arrow(Vector2 from, Vector2 to, float width, Color32 col)
        {
            var d = to - from;
            if (d.sqrMagnitude < 1e-6f) return;
            Line(from, to, width, col);
            var u = d.normalized;
            var n = new Vector2(-u.y, u.x);
            float head = width * 4;
            Triangle(to + u * head * 0.5f, to - u * head + n * head * 0.6f, to - u * head - n * head * 0.6f, col);
        }
    }
}
