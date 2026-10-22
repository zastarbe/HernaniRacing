using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HernaniRacing
{
    /// <summary>Árboles low poly dibujados con GPU instancing por bloques (sin GameObjects).</summary>
    public class TreeRenderer : MonoBehaviour
    {
        class Chunk
        {
            public Bounds bounds;
            public List<Matrix4x4>[] perType = { new List<Matrix4x4>(), new List<Matrix4x4>() };
            public Matrix4x4[][][] batches;   // [tipo][lote][]
        }

        const float ChunkMeters = 320f;
        int gridX = 1, gridZ = 1;
        Chunk[] chunks;
        Mesh[] meshes = new Mesh[2];
        Material trunkMat, leafMat, pineMat;
        public float drawDistance = 900f;
        public bool drawAll;

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
            meshes[0] = BuildBroadleaf();
            meshes[1] = BuildConifer();
            trunkMat = Mat.Color(new Color(0.36f, 0.25f, 0.16f), 0f);
            leafMat = Mat.Color(new Color(0.30f, 0.50f, 0.20f), 0f);
            pineMat = Mat.Color(new Color(0.16f, 0.34f, 0.20f), 0f);
            trunkMat.enableInstancing = leafMat.enableInstancing = pineMat.enableInstancing = true;

            gridX = Mathf.Max(1, Mathf.CeilToInt(w.sizeX / ChunkMeters));
            gridZ = Mathf.Max(1, Mathf.CeilToInt(w.sizeZ / ChunkMeters));
            float hc = w.data.meta.heightMin + w.data.meta.heightRange * 0.5f;
            chunks = new Chunk[gridX * gridZ];
            for (int i = 0; i < chunks.Length; i++)
            {
                int cx = i % gridX, cz = i / gridX;
                const float cs = ChunkMeters;
                chunks[i] = new Chunk { bounds = new Bounds(new Vector3((cx + 0.5f) * cs, hc, (cz + 0.5f) * cs), new Vector3(cs, w.data.meta.heightRange + 60f, cs)) };
            }
            var t = w.data.scenery.trees;
            var rnd = new System.Random(3);
            for (int i = 0; i + 4 < t.Length; i += 5)
            {
                float x = t[i], z = t[i + 1], y = t[i + 2], s = t[i + 3];
                int type = (int)t[i + 4];
                int cx = Mathf.Clamp((int)(x / ChunkMeters), 0, gridX - 1), cz = Mathf.Clamp((int)(z / ChunkMeters), 0, gridZ - 1);
                float rot = (float)rnd.NextDouble() * 360f;
                var m = Matrix4x4.TRS(new Vector3(x, y - 0.2f, z), Quaternion.Euler(0, rot, 0), new Vector3(s, s * (0.9f + (float)rnd.NextDouble() * 0.3f), s));
                chunks[cz * gridX + cx].perType[type].Add(m);
            }
            foreach (var c in chunks)
            {
                c.batches = new Matrix4x4[2][][];
                for (int ty = 0; ty < 2; ty++)
                {
                    var list = c.perType[ty];
                    int nb = (list.Count + 1022) / 1023;
                    c.batches[ty] = new Matrix4x4[nb][];
                    for (int b = 0; b < nb; b++)
                    {
                        int cnt = Mathf.Min(1023, list.Count - b * 1023);
                        c.batches[ty][b] = list.GetRange(b * 1023, cnt).ToArray();
                    }
                }
            }
        }

        void Update()
        {
            var cam = Camera.main;
            if (cam == null || chunks == null) return;
            Vector3 cp = cam.transform.position;
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
            float d2 = drawDistance * drawDistance;
            foreach (var c in chunks)
            {
                if (!drawAll)
                {
                    Vector3 cl = c.bounds.ClosestPoint(cp);
                    if ((cl - cp).sqrMagnitude > d2) continue;
                }
                if (!GeometryUtility.TestPlanesAABB(planes, c.bounds)) continue;
                for (int ty = 0; ty < 2; ty++)
                    foreach (var batch in c.batches[ty])
                    {
                        Graphics.DrawMeshInstanced(meshes[ty], 0, trunkMat, batch, batch.Length, null, ShadowCastingMode.Off, true);
                        Graphics.DrawMeshInstanced(meshes[ty], 1, ty == 0 ? leafMat : pineMat, batch, batch.Length, null,
                            drawAll ? ShadowCastingMode.Off : ShadowCastingMode.On, true);
                    }
            }
        }

        static Mesh BuildBroadleaf()
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
            return mb.Build("tree_broadleaf");
        }

        static Mesh BuildConifer()
        {
            var mb = new MeshBuilder(2);
            Cylinder(mb, 0, Vector3.zero, 0.22f, 0.16f, 1.6f, 5);
            Cone(mb, 1, new Vector3(0, 1.2f, 0), 2.0f, 3.6f, 6);
            Cone(mb, 1, new Vector3(0, 3.4f, 0), 1.5f, 3.2f, 6);
            Cone(mb, 1, new Vector3(0, 5.3f, 0), 1.0f, 2.6f, 6);
            return mb.Build("tree_conifer");
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
