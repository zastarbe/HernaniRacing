using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HernaniRacing
{
    /// <summary>Árboles low poly: una malla por bloque de 320 m (sin GameObjects ni GPU instancing).</summary>
    public class TreeRenderer : MonoBehaviour
    {
        // Los árboles se juntan en una malla por bloque de 320 m y se dibujan con Graphics.DrawMesh normal, sin GPU
        // instancing. Hasta la v14 se usaba Graphics.DrawMeshInstanced, pero en el .exe y en la web Unity puede quitar del
        // build las variantes de shader con instancing y entonces no se ve ningún árbol. Con mallas normales se usa la
        // misma variante del shader que los edificios, que siempre está.
        //  - near: detalle completo (tronco y copa), para los bloques cercanos;
        //  - far: copa simplificada sin tronco, para los bloques lejanos (vuelo, distancia > NearMeters);
        //  - thin: como far pero con la mitad de los árboles, para la vista de mapa (toda el área a la vez).
        class Chunk
        {
            public Bounds bounds;
            public int[] idx;            // índices (de árbol) en scenery.trees
            public Mesh near, far, thin;
            public int lastUsed;
        }

        class Template
        {
            public Vector3[] v, n;
            public int[][] tris;         // por submalla de la malla del bloque: 0 tronco, 1 frondoso, 2 conífera
        }

        const float ChunkMeters = 320f;
        const float NearMeters = 600f;
        const int MaxCachedNear = 60, MaxCachedFar = 260;
        int gridX = 1, gridZ = 1;
        Chunk[] chunks;
        float[] trees;
        Template[] nearT = new Template[2], farT = new Template[2];
        Material[] mats;                 // 0 tronco, 1 frondoso, 2 conífera
        public float drawDistance = 900f;
        public bool drawAll;
        int frame;
        readonly System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();

        public static TreeRenderer Create(World w)
        {
            var go = new GameObject("Arbolado");
            go.transform.SetParent(w.root, false);
            var tr = go.AddComponent<TreeRenderer>();
            tr.Init(w);
            return tr;
        }

        void Init(World w)
        {
            nearT[0] = FromBuilder(BuildBroadleaf(), 1);
            nearT[1] = FromBuilder(BuildConifer(), 2);
            farT[0] = FarBroadleaf();
            farT[1] = FarConifer();
            mats = new[]
            {
                TreeMat(new Color(0.36f, 0.25f, 0.16f)),
                TreeMat(new Color(0.30f, 0.50f, 0.20f)),
                TreeMat(new Color(0.16f, 0.34f, 0.20f))
            };

            gridX = Mathf.Max(1, Mathf.CeilToInt(w.sizeX / ChunkMeters));
            gridZ = Mathf.Max(1, Mathf.CeilToInt(w.sizeZ / ChunkMeters));
            float hc = w.data.meta.heightMin + w.data.meta.heightRange * 0.5f;
            trees = w.data.scenery.trees ?? new float[0];
            var lists = new List<int>[gridX * gridZ];
            chunks = new Chunk[gridX * gridZ];
            for (int i = 0; i < chunks.Length; i++)
            {
                int cx = i % gridX, cz = i / gridX;
                const float cs = ChunkMeters;
                chunks[i] = new Chunk { bounds = new Bounds(new Vector3((cx + 0.5f) * cs, hc, (cz + 0.5f) * cs), new Vector3(cs + 20f, w.data.meta.heightRange + 60f, cs + 20f)) };
                lists[i] = new List<int>();
            }
            for (int k = 0; k * 5 + 4 < trees.Length; k++)
            {
                float x = trees[k * 5], z = trees[k * 5 + 1];
                int cx = Mathf.Clamp((int)(x / ChunkMeters), 0, gridX - 1), cz = Mathf.Clamp((int)(z / ChunkMeters), 0, gridZ - 1);
                lists[cz * gridX + cx].Add(k);
            }
            for (int i = 0; i < chunks.Length; i++) chunks[i].idx = lists[i].ToArray();
        }

        static Material TreeMat(Color c)
        {
            var m = new Material(Mat.Lit) { name = "HR_tree", enableInstancing = false };
            Mat.SetColor(m, c);
            Mat.SetSmoothness(m, 0f);
            return m;
        }

        // ---------- plantillas ----------

        static Template FromBuilder(MeshBuilder mb, int crownSub)
        {
            var t = new Template { v = mb.verts.ToArray(), n = mb.normals.ToArray(), tris = new int[3][] };
            t.tris[0] = mb.tris[0].ToArray();
            t.tris[crownSub] = mb.tris[1].ToArray();
            t.tris[3 - crownSub] = new int[0];
            return t;
        }

        static Template FarBroadleaf()
        {
            // octaedro suave en el lugar de la copa
            Vector3 c = new Vector3(0, 4.0f, 0);
            float r = 2.7f, h = 2.4f;
            var v = new[] { c + Vector3.up * h, c + Vector3.down * h, c + Vector3.right * r, c + Vector3.forward * r, c + Vector3.left * r, c + Vector3.back * r };
            var n = new Vector3[6];
            for (int i = 0; i < 6; i++) n[i] = (v[i] - c).normalized;
            n[1] = (n[1] + Vector3.up * 0.3f).normalized;
            var tr = new List<int>();
            for (int i = 0; i < 4; i++)
            {
                int a = 2 + i, b = 2 + (i + 1) % 4;
                tr.Add(0); tr.Add(b); tr.Add(a);   // arriba (horario visto desde fuera)
                tr.Add(1); tr.Add(a); tr.Add(b);   // abajo
            }
            return new Template { v = v, n = n, tris = new[] { new int[0], tr.ToArray(), new int[0] } };
        }

        static Template FarConifer()
        {
            Vector3 apex = new Vector3(0, 7.9f, 0);
            float y0 = 1.2f, r = 2.0f;
            var v = new Vector3[5];
            var n = new Vector3[5];
            v[0] = apex; n[0] = Vector3.up;
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f;
                var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                v[1 + i] = new Vector3(0, y0, 0) + d * r;
                n[1 + i] = (d + Vector3.up * 0.35f).normalized;
            }
            var tr = new List<int>();
            for (int i = 0; i < 4; i++)
            {
                int a = 1 + i, b = 1 + (i + 1) % 4;
                // orientar hacia fuera
                Vector3 f = Vector3.Cross(v[a] - v[0], v[b] - v[0]);
                Vector3 mid = (v[a] + v[b]) * 0.5f; mid.y = 0;
                if (Vector3.Dot(f, mid) >= 0) { tr.Add(0); tr.Add(a); tr.Add(b); } else { tr.Add(0); tr.Add(b); tr.Add(a); }
            }
            return new Template { v = v, n = n, tris = new[] { new int[0], new int[0], tr.ToArray() } };
        }

        static float Hash01(int k, int salt)
        {
            unchecked
            {
                uint h = (uint)k * 2654435761u ^ (uint)salt * 40503u;
                h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        Mesh BuildChunkMesh(Chunk c, Template[] tpl, int step, string name)
        {
            int nv = 0;
            int[] ni = new int[3];
            for (int j = 0; j < c.idx.Length; j += step)
            {
                var t = tpl[Mathf.Clamp((int)trees[c.idx[j] * 5 + 4], 0, 1)];
                nv += t.v.Length;
                for (int s = 0; s < 3; s++) ni[s] += t.tris[s].Length;
            }
            if (nv == 0) return null;
            var verts = new Vector3[nv];
            var norms = new Vector3[nv];
            var tris = new int[3][];
            for (int s = 0; s < 3; s++) tris[s] = new int[ni[s]];
            int vo = 0;
            int[] to = new int[3];
            for (int j = 0; j < c.idx.Length; j += step)
            {
                int k = c.idx[j];
                float x = trees[k * 5], z = trees[k * 5 + 1], y = trees[k * 5 + 2] - 0.2f, sc = trees[k * 5 + 3];
                var t = tpl[Mathf.Clamp((int)trees[k * 5 + 4], 0, 1)];
                float rot = Hash01(k, 1) * Mathf.PI * 2f, sy = sc * (0.9f + Hash01(k, 2) * 0.3f);
                float cs = Mathf.Cos(rot), sn = Mathf.Sin(rot);
                float isx = 1f / Mathf.Max(sc, 0.01f), isy = 1f / Mathf.Max(sy, 0.01f);
                for (int i = 0; i < t.v.Length; i++)
                {
                    Vector3 p = t.v[i];
                    float px = p.x * sc, pz = p.z * sc;
                    verts[vo + i] = new Vector3(x + px * cs + pz * sn, y + p.y * sy, z - px * sn + pz * cs);
                    Vector3 q = t.n[i];
                    float qx = q.x * isx, qz = q.z * isx;
                    norms[vo + i] = new Vector3(qx * cs + qz * sn, q.y * isy, -qx * sn + qz * cs).normalized;
                }
                for (int s = 0; s < 3; s++)
                {
                    var src = t.tris[s];
                    var dst = tris[s];
                    for (int i = 0; i < src.Length; i++) dst[to[s] + i] = src[i] + vo;
                    to[s] += src.Length;
                }
                vo += t.v.Length;
            }
            var m = new Mesh { name = name };
            if (nv > 65000) m.indexFormat = IndexFormat.UInt32;
            m.vertices = verts;
            m.normals = norms;
            m.subMeshCount = 3;
            for (int s = 0; s < 3; s++) m.SetTriangles(tris[s], s, false);
            m.RecalculateBounds();
            m.UploadMeshData(true);   // libera la copia en memoria (sólo queda en la GPU)
            return m;
        }

        // ---------- dibujo ----------

        void Update()
        {
            var cam = Camera.main;
            if (cam == null || chunks == null) return;
            frame++;
            Vector3 cp = cam.transform.position;
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
            float d2 = drawDistance * drawDistance, n2 = NearMeters * NearMeters;
            sw.Reset(); sw.Start();
            for (int i = 0; i < chunks.Length; i++)
            {
                var c = chunks[i];
                if (c.idx.Length == 0) continue;
                float dd = drawAll ? 0f : (c.bounds.ClosestPoint(cp) - cp).sqrMagnitude;
                if (!drawAll && dd > d2) continue;
                c.lastUsed = frame;   // cerca: se conserva aunque ahora quede detrás de la cámara
                if (!GeometryUtility.TestPlanesAABB(planes, c.bounds)) continue;
                Mesh m;
                bool shadows = false;
                if (drawAll)
                {
                    if (c.thin == null) c.thin = BuildChunkMesh(c, farT, 2, "trees_thin");   // se hacen todos de una vez, son ligeros
                    m = c.thin;
                }
                else if (dd < n2)
                {
                    if (c.near == null && (dd < 250f * 250f || sw.ElapsedMilliseconds < 6))
                        c.near = BuildChunkMesh(c, nearT, 1, "trees_near");
                    m = c.near;
                    shadows = true;
                    if (m == null)
                    {
                        // mientras no está la malla detallada, se ve la sencilla
                        if (c.far == null) c.far = BuildChunkMesh(c, farT, 1, "trees_far");
                        m = c.far;
                        shadows = false;
                    }
                }
                else
                {
                    if (c.far == null && sw.ElapsedMilliseconds < 6) c.far = BuildChunkMesh(c, farT, 1, "trees_far");
                    m = c.far;
                }
                if (m == null) continue;
                for (int s = 0; s < 3; s++)
                {
                    var sh = s == 0 || !shadows ? ShadowCastingMode.Off : ShadowCastingMode.On;
                    Graphics.DrawMesh(m, Matrix4x4.identity, mats[s], 0, null, s, null, sh, true);
                }
            }
            if ((frame & 63) == 0) Trim();
        }

        /// <summary>Libera las mallas de los bloques que hace rato que no se ven.</summary>
        void Trim()
        {
            int nNear = 0, nFar = 0;
            foreach (var c in chunks) { if (c.near != null) nNear++; if (c.far != null) nFar++; }
            if (nNear <= MaxCachedNear && nFar <= MaxCachedFar) return;
            var list = new List<Chunk>(chunks);
            list.Sort((a, b) => a.lastUsed.CompareTo(b.lastUsed));
            foreach (var c in list)
            {
                if (frame - c.lastUsed < 120) break;
                if (nNear > MaxCachedNear && c.near != null) { Destroy(c.near); c.near = null; nNear--; }
                if (nFar > MaxCachedFar && c.far != null) { Destroy(c.far); c.far = null; nFar--; }
            }
        }

        void OnDestroy()
        {
            if (chunks == null) return;
            foreach (var c in chunks)
            {
                if (c.near != null) Destroy(c.near);
                if (c.far != null) Destroy(c.far);
                if (c.thin != null) Destroy(c.thin);
            }
            if (mats != null) foreach (var m in mats) if (m != null) Destroy(m);
        }

        static MeshBuilder BuildBroadleaf()
        {
            var mb = new MeshBuilder(2);
            Cylinder(mb, 0, Vector3.zero, 0.25f, 0.18f, 2.2f, 5);
            // copa: icosaedro deformado
            float r = 2.6f;
            Vector3 c = new Vector3(0, 4.2f, 0);
            float p = (1f + Mathf.Sqrt(5f)) / 2f;
            Vector3[] v =
            {
                new Vector3(-1, p, 0), new Vector3(1, p, 0), new Vector3(-1, -p, 0), new Vector3(1, -p, 0),
                new Vector3(0, -1, p), new Vector3(0, 1, p), new Vector3(0, -1, -p), new Vector3(0, 1, -p),
                new Vector3(p, 0, -1), new Vector3(p, 0, 1), new Vector3(-p, 0, -1), new Vector3(-p, 0, 1)
            };
            int[] f =
            {
                0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1
            };
            for (int i = 0; i < v.Length; i++)
            {
                v[i] = v[i].normalized * r;
                v[i].y *= 0.85f;
                v[i] += c;
            }
            for (int i = 0; i < f.Length; i += 3)
            {
                Vector3 a = v[f[i]], b = v[f[i + 1]], cc = v[f[i + 2]];
                Vector3 mid = (a + b + cc) / 3f - c;
                mb.FlatTri(1, a, b, cc, mid, Vector2.zero, Vector2.zero, Vector2.zero);
            }
            return mb;
        }

        static MeshBuilder BuildConifer()
        {
            var mb = new MeshBuilder(2);
            Cylinder(mb, 0, Vector3.zero, 0.22f, 0.16f, 1.6f, 5);
            Cone(mb, 1, new Vector3(0, 1.2f, 0), 2.0f, 3.6f, 6);
            Cone(mb, 1, new Vector3(0, 3.4f, 0), 1.5f, 3.2f, 6);
            Cone(mb, 1, new Vector3(0, 5.3f, 0), 1.0f, 2.6f, 6);
            return mb;
        }

        static void Cylinder(MeshBuilder mb, int sub, Vector3 b, float r0, float r1, float h, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float a0 = i * Mathf.PI * 2 / n, a1 = (i + 1) * Mathf.PI * 2 / n;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)), d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                mb.Quad(sub, b + d0 * r0, b + d1 * r0, b + d1 * r1 + Vector3.up * h, b + d0 * r1 + Vector3.up * h, (d0 + d1),
                    Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            }
        }

        static void Cone(MeshBuilder mb, int sub, Vector3 b, float r, float h, int n)
        {
            Vector3 top = b + Vector3.up * h;
            for (int i = 0; i < n; i++)
            {
                float a0 = i * Mathf.PI * 2 / n, a1 = (i + 1) * Mathf.PI * 2 / n;
                Vector3 p0 = b + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * r, p1 = b + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * r;
                Vector3 mid = (p0 + p1) * 0.5f - b; mid.y = r * 0.5f;
                mb.FlatTri(sub, p0, p1, top, mid, Vector2.zero, Vector2.zero, Vector2.zero);
                mb.FlatTri(sub, p0, p1, b, Vector3.down, Vector2.zero, Vector2.zero, Vector2.zero);
            }
        }
    }
}
