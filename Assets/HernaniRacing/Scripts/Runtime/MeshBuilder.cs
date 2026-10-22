using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HernaniRacing
{
    /// <summary>Constructor de mallas con submallas y sombreado plano (estética low poly).</summary>
    public class MeshBuilder
    {
        public readonly List<Vector3> verts = new List<Vector3>();
        public readonly List<Vector3> normals = new List<Vector3>();
        public readonly List<Vector2> uvs = new List<Vector2>();
        public readonly List<int>[] tris;

        public MeshBuilder(int submeshes = 1)
        {
            tris = new List<int>[submeshes];
            for (int i = 0; i < submeshes; i++) tris[i] = new List<int>();
        }

        public int VertexCount { get { return verts.Count; } }

        public int V(Vector3 p, Vector3 n, Vector2 uv)
        {
            verts.Add(p); normals.Add(n); uvs.Add(uv);
            return verts.Count - 1;
        }

        public void Tri(int sub, int a, int b, int c)
        {
            tris[sub].Add(a); tris[sub].Add(b); tris[sub].Add(c);
        }

        /// <summary>Triángulo plano orientado hacia 'facing' (se corrige el sentido automáticamente).</summary>
        public void FlatTri(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 facing, Vector2 ua, Vector2 ub, Vector2 uc)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 1e-12f) return;
            if (Vector3.Dot(n, facing) < 0) { var t = b; b = c; c = t; var tu = ub; ub = uc; uc = tu; n = -n; }
            n.Normalize();
            int i = V(a, n, ua), j = V(b, n, ub), k = V(c, n, uc);
            Tri(sub, i, j, k);
        }

        /// <summary>Cuadrilátero a-b-c-d (en orden de contorno) orientado hacia 'facing'.</summary>
        public void Quad(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 facing,
                         Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, bool doubleSided = false)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 1e-12f) n = Vector3.Cross(c - a, d - a);
            if (n.sqrMagnitude < 1e-12f) return;
            bool flip = Vector3.Dot(n, facing) < 0;
            Vector3 nn = (flip ? -n : n).normalized;
            int i0 = V(a, nn, ua), i1 = V(b, nn, ub), i2 = V(c, nn, uc), i3 = V(d, nn, ud);
            if (!flip) { Tri(sub, i0, i1, i2); Tri(sub, i0, i2, i3); }
            else { Tri(sub, i0, i2, i1); Tri(sub, i0, i3, i2); }
            if (doubleSided)
            {
                int j0 = V(a, -nn, ua), j1 = V(b, -nn, ub), j2 = V(c, -nn, uc), j3 = V(d, -nn, ud);
                if (!flip) { Tri(sub, j0, j2, j1); Tri(sub, j0, j3, j2); }
                else { Tri(sub, j0, j1, j2); Tri(sub, j0, j2, j3); }
            }
        }

        /// <summary>Caja orientada (centro, ejes y semitamaños).</summary>
        public void Box(int sub, Vector3 c, Vector3 right, Vector3 up, Vector3 fwd, Vector3 half, float uvScale = 1f)
        {
            Vector3 r = right.normalized * half.x, u = up.normalized * half.y, f = fwd.normalized * half.z;
            Vector3[] p =
            {
                c - r - u - f, c + r - u - f, c + r + u - f, c - r + u - f,
                c - r - u + f, c + r - u + f, c + r + u + f, c - r + u + f
            };
            float sx = half.x * 2 * uvScale, sy = half.y * 2 * uvScale, sz = half.z * 2 * uvScale;
            Quad(sub, p[0], p[1], p[2], p[3], -f, new Vector2(0, 0), new Vector2(sx, 0), new Vector2(sx, sy), new Vector2(0, sy));
            Quad(sub, p[5], p[4], p[7], p[6], f, new Vector2(0, 0), new Vector2(sx, 0), new Vector2(sx, sy), new Vector2(0, sy));
            Quad(sub, p[4], p[0], p[3], p[7], -r, new Vector2(0, 0), new Vector2(sz, 0), new Vector2(sz, sy), new Vector2(0, sy));
            Quad(sub, p[1], p[5], p[6], p[2], r, new Vector2(0, 0), new Vector2(sz, 0), new Vector2(sz, sy), new Vector2(0, sy));
            Quad(sub, p[3], p[2], p[6], p[7], u, new Vector2(0, 0), new Vector2(sx, 0), new Vector2(sx, sz), new Vector2(0, sz));
            Quad(sub, p[4], p[5], p[1], p[0], -u, new Vector2(0, 0), new Vector2(sx, 0), new Vector2(sx, sz), new Vector2(0, sz));
        }

        /// <summary>
        /// Extruye un perfil transversal (u = desplazamiento lateral a la derecha, v = altura)
        /// a lo largo de una polilínea. El perfil debe recorrerse de modo que su normal
        /// (-dv, du) apunte hacia la cara visible.
        /// </summary>
        public void Extrude(int sub, IList<Vector3> path, IList<Vector2> profile, float uvAlong = 1f,
                            bool doubleSided = false, float uvAcrossScale = 0f, float sStart = 0f)
        {
            int n = path.Count;
            if (n < 2) return;
            var right = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 t = path[Mathf.Min(i + 1, n - 1)] - path[Mathf.Max(i - 1, 0)];
                t.y = 0;
                if (t.sqrMagnitude < 1e-8f) t = Vector3.forward;
                t.Normalize();
                Vector3 r = new Vector3(t.z, 0, -t.x);
                // inglete limitado en vértices interiores
                if (i > 0 && i < n - 1)
                {
                    Vector3 t0 = path[i] - path[i - 1]; t0.y = 0;
                    Vector3 t1 = path[i + 1] - path[i]; t1.y = 0;
                    if (t0.sqrMagnitude > 1e-8f && t1.sqrMagnitude > 1e-8f)
                    {
                        Vector3 r0 = new Vector3(t0.normalized.z, 0, -t0.normalized.x);
                        float c = Mathf.Max(0.5f, Vector3.Dot(r, r0));
                        r /= c;
                    }
                }
                right[i] = r;
            }
            float s = sStart;
            for (int i = 0; i < n - 1; i++)
            {
                float segLen = Vector3.Distance(path[i], path[i + 1]);
                float s1 = s + segLen;
                for (int j = 0; j < profile.Count - 1; j++)
                {
                    Vector2 p0 = profile[j], p1 = profile[j + 1];
                    Vector3 a = path[i] + right[i] * p0.x + Vector3.up * p0.y;
                    Vector3 b = path[i + 1] + right[i + 1] * p0.x + Vector3.up * p0.y;
                    Vector3 c = path[i + 1] + right[i + 1] * p1.x + Vector3.up * p1.y;
                    Vector3 d = path[i] + right[i] * p1.x + Vector3.up * p1.y;
                    Vector2 n2 = new Vector2(-(p1.y - p0.y), p1.x - p0.x);
                    Vector3 rr = (right[i] + right[i + 1]).normalized;
                    Vector3 facing = rr * n2.x + Vector3.up * n2.y;
                    float v0 = uvAcrossScale > 0 ? p0.x * uvAcrossScale : j;
                    float v1 = uvAcrossScale > 0 ? p1.x * uvAcrossScale : j + 1;
                    Quad(sub, a, b, c, d, facing,
                        new Vector2(s * uvAlong, v0), new Vector2(s1 * uvAlong, v0),
                        new Vector2(s1 * uvAlong, v1), new Vector2(s * uvAlong, v1), doubleSided);
                }
                s = s1;
            }
        }

        /// <summary>
        /// Rejilla de vértices compartidos (filas a lo largo, columnas a lo ancho), con normales suaves.
        /// Se usa para el asfalto y las aceras que se adaptan al terreno.
        /// </summary>
        public void Grid(int sub, Vector3[,] p, Vector2[,] uv)
        {
            int rows = p.GetLength(0), cols = p.GetLength(1);
            if (rows < 2 || cols < 2) return;
            int start = verts.Count;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    Vector3 along = p[Mathf.Min(r + 1, rows - 1), c] - p[Mathf.Max(r - 1, 0), c];
                    Vector3 across = p[r, Mathf.Min(c + 1, cols - 1)] - p[r, Mathf.Max(c - 1, 0)];
                    Vector3 n = Vector3.Cross(along, across);
                    if (n.y < 0) n = -n;
                    if (n.sqrMagnitude < 1e-10f) n = Vector3.up;
                    V(p[r, c], n.normalized, uv[r, c]);
                }
            for (int r = 0; r < rows - 1; r++)
                for (int c = 0; c < cols - 1; c++)
                {
                    int a = start + r * cols + c, b = a + cols, d = a + 1, e = b + 1;
                    // orientación hacia arriba: comprobamos con la normal geométrica
                    Vector3 nrm = Vector3.Cross(verts[b] - verts[a], verts[d] - verts[a]);
                    if (nrm.y >= 0) { Tri(sub, a, b, d); Tri(sub, d, b, e); }
                    else { Tri(sub, a, d, b); Tri(sub, d, e, b); }
                }
        }

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name };
            if (verts.Count > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(verts);
            m.SetNormals(normals);
            m.SetUVs(0, uvs);
            m.subMeshCount = tris.Length;
            for (int i = 0; i < tris.Length; i++) m.SetTriangles(tris[i], i, false);
            m.RecalculateBounds();
            return m;
        }

        public static GameObject Spawn(string name, Mesh mesh, Material[] mats, Transform parent, bool collider)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            if (collider) go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.isStatic = true;
            return go;
        }
    }
}
