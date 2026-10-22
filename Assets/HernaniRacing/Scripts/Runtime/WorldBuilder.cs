using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Construye el mundo de Hernani a partir de los datos reales: terreno (MDT), viales,
    /// puentes, túneles, aceras, cruces, edificios extruidos, río Urumea y arbolado.
    /// </summary>
    public class World
    {
        public HernaniDataSet data;
        public RoadGraph graph;
        public Terrain[] tiles;          // teselas de terreno [tz * tilesX + tx]
        public int tilesX, tilesZ;
        public float tileSize;           // metros por tesela
        public Transform root;
        public float size;               // lado mayor del área
        public float sizeX, sizeZ;       // área real (x = este, z = norte)
        public Vector3 Center { get { return new Vector3(sizeX * 0.5f, 0, sizeZ * 0.5f); } }

        // trozos de malla para viales, edificios, señales... (~500 m)
        public const float ChunkSize = 512f;
        public int chunksX = 1, chunksZ = 1;
        public int ChunkTotal { get { return chunksX * chunksZ; } }
        public int ChunkIndex(Vector3 p)
        {
            int cx = Mathf.Clamp((int)(p.x / ChunkSize), 0, chunksX - 1);
            int cz = Mathf.Clamp((int)(p.z / ChunkSize), 0, chunksZ - 1);
            return cz * chunksX + cx;
        }

        public bool Inside(Vector3 p, float margin = 0f)
        {
            return p.x >= margin && p.z >= margin && p.x <= sizeX - margin && p.z <= sizeZ - margin;
        }

        public Terrain TileAt(Vector3 p)
        {
            if (tiles == null || tiles.Length == 0) return null;
            int tx = Mathf.Clamp(Mathf.FloorToInt(p.x / tileSize), 0, tilesX - 1);
            int tz = Mathf.Clamp(Mathf.FloorToInt(p.z / tileSize), 0, tilesZ - 1);
            return tiles[tz * tilesX + tx];
        }
        public TreeRenderer trees;
        public TrafficControl signals;   // semáforos (OSM)

        // agujeros del terreno (bocas de túnel): celdas de 'holeCell' m, true = agujero
        public bool[] holes;
        public int holeResX, holeResZ;
        public float holeCell = 1f;

        public float GroundHeight(Vector3 p)
        {
            var t = TileAt(p);
            return t != null ? t.SampleHeight(p) + t.transform.position.y : 0f;
        }

        public bool IsHole(Vector3 p)
        {
            if (holes == null) return false;
            int x = Mathf.FloorToInt(p.x / holeCell), z = Mathf.FloorToInt(p.z / holeCell);
            if (x < 0 || z < 0 || x >= holeResX || z >= holeResZ) return false;
            return holes[z * holeResX + x];
        }

        /// <summary>Altura del terreno, o 'fallback' si en ese punto hay un agujero (boca de túnel).</summary>
        public float SurfaceHeight(Vector3 p, float fallback)
        {
            return IsHole(p) ? fallback : GroundHeight(p);
        }

        /// <summary>Centro de Hernani (plaza / herrigunea) en coordenadas locales.</summary>
        public Vector3 TownCenter
        {
            get
            {
                foreach (var z in data.scenery.zones)
                    if (z.full != null && z.full.StartsWith("Hernani, herrigunea")) return new Vector3(z.x, z.y, z.z);
                foreach (var z in data.scenery.zones)
                    if (z.name.Contains("Centro") || z.name.Contains("Erdigune")) return new Vector3(z.x, z.y, z.z);
                // Plaza de los Gudaris / Ayuntamiento (UTM 583095, 4790846)
                var p = new Vector3((float)(583095 - data.meta.originE), 0f, (float)(4790846 - data.meta.originN));
                return Inside(p) ? p : Center;
            }
        }

        public ZoneData ZoneAt(Vector3 p)
        {
            ZoneData best = null;
            float bd = float.MaxValue;
            foreach (var z in data.scenery.zones)
            {
                if (!z.district) continue;
                float d = (z.x - p.x) * (z.x - p.x) + (z.z - p.z) * (z.z - p.z);
                if (z.primary) d *= 0.6f;   // las zonas pedidas pesan un poco más
                if (d < bd) { bd = d; best = z; }
            }
            return best;
        }
    }

    public static class WorldBuilder
    {

        public static readonly Color Asphalt = new Color(0.22f, 0.22f, 0.24f);
        public static readonly Color Sidewalk = new Color(0.64f, 0.62f, 0.58f);
        public static readonly Color Concrete = new Color(0.68f, 0.67f, 0.64f);

        static readonly Color[] WallColors =
        {
            new Color(0.93f, 0.90f, 0.82f),  // crema
            new Color(0.86f, 0.76f, 0.60f),  // ocre claro
            new Color(0.97f, 0.97f, 0.95f),  // blanco
            new Color(0.72f, 0.45f, 0.36f),  // ladrillo
            new Color(0.78f, 0.78f, 0.76f),  // gris
            new Color(0.88f, 0.82f, 0.72f),  // arena
        };

        public static IEnumerator Build(World w, System.Action<string, float> progress)
        {
            w.root = new GameObject("Hernani (datos b5m + geoEuskadi)").transform;
            var mt = w.data.meta;
            w.sizeX = mt.sizeX; w.sizeZ = mt.sizeZ; w.size = Mathf.Max(w.sizeX, w.sizeZ);
            w.chunksX = Mathf.Max(1, Mathf.CeilToInt(w.sizeX / World.ChunkSize));
            w.chunksZ = Mathf.Max(1, Mathf.CeilToInt(w.sizeZ / World.ChunkSize));

            progress("Generando terreno (MDT)...", 0.15f);
            yield return null;
            BuildTerrain(w);

            progress("Construyendo red viaria b5m...", 0.35f);
            yield return null;
            w.graph = new RoadGraph(w.data.roads);
            BuildRoads(w);

            progress("Señalización, semáforos y marcas viales (OSM)...", 0.5f);
            yield return null;
            RoadFurniture.Build(w);

            progress("Río Urumea y regatas...", 0.55f);
            yield return null;
            BuildRivers(w);

            progress("Extruyendo edificios (alturas LiDAR)...", 0.65f);
            yield return null;
            BuildBuildings(w);

            progress("Plantando árboles...", 0.85f);
            yield return null;
            w.trees = TreeRenderer.Create(w);

            progress("Listo", 1f);
            yield return null;
        }

        // ------------------------------------------------------------------ terreno
        static void BuildTerrain(World w)
        {
            var meta = w.data.meta;
            var h = w.data.heights01;
            int gx = meta.gridX, gz = meta.gridZ;
            int tc = meta.tileCells;
            float cell = meta.cell;
            w.tilesX = (gx - 1) / tc;
            w.tilesZ = (gz - 1) / tc;
            w.tileSize = tc * cell;
            w.tiles = new Terrain[w.tilesX * w.tilesZ];

            // túneles de verdad: el monte se conserva encima y sólo se abre el terreno en las bocas
            w.holeResX = gx - 1; w.holeResZ = gz - 1;
            w.holeCell = cell;
            var hl = w.data.roads.holes;
            if (hl != null && hl.Length > 0)
            {
                w.holes = new bool[w.holeResX * w.holeResZ];
                foreach (int idx in hl)
                    if (idx >= 0 && idx < w.holes.Length) w.holes[idx] = true;
            }

            // capas: hierba, bosque, roca/tierra, urbano
            var layers = new[]
            {
                MakeLayer("grass", new Color(0.42f, 0.56f, 0.27f), 0.18f, 7),
                MakeLayer("forest", new Color(0.24f, 0.38f, 0.18f), 0.22f, 11),
                MakeLayer("rock", new Color(0.50f, 0.45f, 0.36f), 0.25f, 13),
                MakeLayer("urban", new Color(0.55f, 0.54f, 0.50f), 0.12f, 17),
            };

            // máscara urbana global a 4 m a partir de edificios y calles
            const float um = 4f;
            int ux = Mathf.CeilToInt(meta.sizeX / um) + 1, uz = Mathf.CeilToInt(meta.sizeZ / um) + 1;
            var urban = new float[uz, ux];
            float k = 1f / um;
            foreach (var b in w.data.buildings.buildings)
                for (int i = 0; i + 1 < b.v.Length; i += 2)
                    Stamp(urban, b.v[i] * k, b.v[i + 1] * k, 6f * k, 1f);
            foreach (var e in w.data.roads.edges)
                if (e.type == "Calle")
                    for (int i = 0; i + 2 < e.p.Length; i += 9)
                        Stamp(urban, e.p[i] * k, e.p[i + 2] * k, (e.width * 0.5f + 5f) * k, 0.8f);
            urban = BoxBlur(urban, 2);

            int ar = Mathf.Max(32, Mathf.ClosestPowerOfTwo(Mathf.RoundToInt(w.tileSize / um)));   // ~4 m por texel
            var tparent = new GameObject("Terreno MDT").transform;
            tparent.SetParent(w.root, false);
            var tileH = new float[tc + 1, tc + 1];
            Material tmat = null;
            if (Mat.IsSRP)
            {
                var sh = Shader.Find("Universal Render Pipeline/Terrain/Lit");
                if (sh != null) tmat = new Material(sh);
            }
            for (int tz = 0; tz < w.tilesZ; tz++)
                for (int tx = 0; tx < w.tilesX; tx++)
                {
                    int x0 = tx * tc, z0 = tz * tc;
                    var td = new TerrainData();
                    td.heightmapResolution = tc + 1;
                    td.size = new Vector3(w.tileSize, meta.heightRange, w.tileSize);
                    for (int z = 0; z <= tc; z++)
                        for (int x = 0; x <= tc; x++) tileH[z, x] = h[z0 + z, x0 + x];
                    td.SetHeights(0, 0, tileH);

                    if (w.holes != null)
                    {
                        bool any = false;
                        var solid = new bool[tc, tc];   // [z, x], true = hay terreno
                        for (int z = 0; z < tc; z++)
                            for (int x = 0; x < tc; x++)
                            {
                                bool hole = w.holes[(z0 + z) * w.holeResX + x0 + x];
                                solid[z, x] = !hole;
                                any |= hole;
                            }
                        if (any) td.SetHoles(0, 0, solid);
                    }

                    td.terrainLayers = layers;
                    td.alphamapResolution = ar;
                    td.baseMapResolution = Mathf.Min(1024, ar * 2);
                    var maps = new float[ar, ar, 4];
                    for (int z = 0; z < ar; z++)
                        for (int x = 0; x < ar; x++)
                        {
                            float wx = tx * w.tileSize + x / (float)(ar - 1) * w.tileSize;
                            float wz = tz * w.tileSize + z / (float)(ar - 1) * w.tileSize;
                            int ix = Mathf.Clamp(Mathf.RoundToInt(wx / cell), 1, gx - 2), iz = Mathf.Clamp(Mathf.RoundToInt(wz / cell), 1, gz - 2);
                            float dx = (h[iz, ix + 1] - h[iz, ix - 1]) * meta.heightRange / (2 * cell);
                            float dz = (h[iz + 1, ix] - h[iz - 1, ix]) * meta.heightRange / (2 * cell);
                            float slope = Mathf.Sqrt(dx * dx + dz * dz);
                            float rock = Mathf.Clamp01((slope - 0.7f) * 2.5f);
                            float noise = Mathf.PerlinNoise(wx * 0.007f, wz * 0.007f);
                            float forest = Mathf.Clamp01((slope - 0.25f) * 2f) * Mathf.Clamp01(noise * 1.6f - 0.2f);
                            int ux_ = Mathf.Clamp(Mathf.RoundToInt(wx / um), 0, ux - 1), uz_ = Mathf.Clamp(Mathf.RoundToInt(wz / um), 0, uz - 1);
                            float u = Mathf.Clamp01(urban[uz_, ux_] * 1.4f);
                            // composición
                            float wr = rock, wf = forest * (1 - rock), wu = u * (1 - rock * 0.5f);
                            float wg = Mathf.Max(0, 1f - wr - wf - wu);
                            float sum = wr + wf + wu + wg + 1e-5f;
                            maps[z, x, 0] = wg / sum; maps[z, x, 1] = wf / sum; maps[z, x, 2] = wr / sum; maps[z, x, 3] = wu / sum;
                        }
                    td.SetAlphamaps(0, 0, maps);

                    var go = Terrain.CreateTerrainGameObject(td);
                    go.name = "Tesela " + tx + "_" + tz;
                    go.transform.SetParent(tparent, false);
                    go.transform.position = new Vector3(tx * w.tileSize, meta.heightMin, tz * w.tileSize);
                    var t = go.GetComponent<Terrain>();
                    t.heightmapPixelError = 1;   // el asfalto va pegado al terreno: mínimo error de LOD
                    t.basemapDistance = 1200;
                    t.drawInstanced = true;
                    t.treeDistance = 0;
                    t.groupingID = 1;
                    t.allowAutoConnect = true;
                    if (tmat != null) t.materialTemplate = tmat;
                    w.tiles[tz * w.tilesX + tx] = t;
                }
            // vecinos: costuras sin grietas entre LOD de teselas contiguas
            for (int tz = 0; tz < w.tilesZ; tz++)
                for (int tx = 0; tx < w.tilesX; tx++)
                {
                    Terrain L = tx > 0 ? w.tiles[tz * w.tilesX + tx - 1] : null;
                    Terrain R = tx < w.tilesX - 1 ? w.tiles[tz * w.tilesX + tx + 1] : null;
                    Terrain B = tz > 0 ? w.tiles[(tz - 1) * w.tilesX + tx] : null;
                    Terrain T = tz < w.tilesZ - 1 ? w.tiles[(tz + 1) * w.tilesX + tx] : null;
                    w.tiles[tz * w.tilesX + tx].SetNeighbors(L, T, R, B);
                }
        }

        static TerrainLayer MakeLayer(string key, Color c, float noise, int seed)
        {
            var l = new TerrainLayer
            {
                diffuseTexture = Tex.Noise("terrain_" + key, c, noise, 64, seed),
                tileSize = new Vector2(6, 6),
                name = key
            };
            return l;
        }

        static void Stamp(float[,] m, float cx, float cz, float r, float v)
        {
            int rz = m.GetLength(0), rx = m.GetLength(1);
            int x0 = Mathf.Max(0, (int)(cx - r)), x1 = Mathf.Min(rx - 1, (int)(cx + r) + 1);
            int z0 = Mathf.Max(0, (int)(cz - r)), z1 = Mathf.Min(rz - 1, (int)(cz + r) + 1);
            float r2 = r * r;
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    if ((x - cx) * (x - cx) + (z - cz) * (z - cz) <= r2) m[z, x] = Mathf.Max(m[z, x], v);
        }

        static float[,] BoxBlur(float[,] m, int r)
        {
            int rz = m.GetLength(0), rx = m.GetLength(1);
            var tmp = new float[rz, rx];
            var outm = new float[rz, rx];
            for (int z = 0; z < rz; z++)
            {
                float acc = 0; int cnt = 0;
                for (int x = -r; x < rx + r; x++)
                {
                    int xa = x + r, xr = x - r - 1;
                    if (xa >= 0 && xa < rx) { acc += m[z, xa]; cnt++; }
                    if (xr >= 0 && xr < rx) { acc -= m[z, xr]; cnt--; }
                    if (x >= 0 && x < rx) tmp[z, x] = acc / Mathf.Max(cnt, 1);
                }
            }
            for (int x = 0; x < rx; x++)
            {
                float acc = 0; int cnt = 0;
                for (int z = -r; z < rz + r; z++)
                {
                    int za = z + r, zr = z - r - 1;
                    if (za >= 0 && za < rz) { acc += tmp[za, x]; cnt++; }
                    if (zr >= 0 && zr < rz) { acc -= tmp[zr, x]; cnt--; }
                    if (z >= 0 && z < rz) outm[z, x] = acc / Mathf.Max(cnt, 1);
                }
            }
            return outm;
        }

        // ------------------------------------------------------------------ viales
        static int ChunkOf(World w, Vector3 p)
        {
            return w.ChunkIndex(p);
        }

        public static List<Vector3> SubPath(RoadEdge e, float s0, float s1)
        {
            var r = new List<Vector3>();
            if (s1 - s0 < 0.5f) return r;
            r.Add(e.PointAt(s0, true));
            for (int i = 0; i < e.pts.Length; i++)
                if (e.cum[i] > s0 + 0.05f && e.cum[i] < s1 - 0.05f) r.Add(e.pts[i]);
            r.Add(e.PointAt(s1, true));
            return r;
        }

        // El coche rueda sobre el TERRENO (una sola superficie continua, tallada a la cota exacta de cada
        // calle en el pipeline). El asfalto, las aceras y los cruces se "pegan" al terreno y son sólo visuales,
        // así no hay escalones ni huecos entre calles. Sólo los puentes tienen tablero con colisión.
        const float DrapeOffset = 0.06f;     // asfalto sobre el terreno (visual)
        const float JunctionOffset = 0.065f;
        const float CurbHeight = 0.07f;

        static float TrimAt(RoadGraph g, int node)
        {
            return g.adj[node].Count >= 3 ? g.NodeRadius(node) + 2.0f : 0f;
        }

        /// <summary>Remuestrea el eje de la calle entre s0 y s1 cada 'step' metros (posiciones XZ).</summary>
        static List<Vector3> Resample(RoadEdge e, float s0, float s1, float step)
        {
            var r = new List<Vector3>();
            float L = s1 - s0;
            if (L < 0.3f) return r;
            int n = Mathf.Max(1, Mathf.CeilToInt(L / step));
            for (int i = 0; i <= n; i++) r.Add(e.PointAt(s0 + L * i / n, true));
            return r;
        }

        static Vector3[] RightVectors(List<Vector3> path)
        {
            int n = path.Count;
            var right = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 t = path[Mathf.Min(i + 1, n - 1)] - path[Mathf.Max(i - 1, 0)];
                t.y = 0;
                if (t.sqrMagnitude < 1e-8f) t = Vector3.forward;
                t.Normalize();
                right[i] = new Vector3(t.z, 0, -t.x);
            }
            return right;
        }

        /// <summary>Banda pegada al terreno entre los desplazamientos laterales x0..x1 (m).</summary>
        static void DrapeStrip(World w, MeshBuilder mb, int sub, List<Vector3> path, float x0, float x1, int cols,
                               float yOff, bool worldUV, float sStart = 0f)
        {
            int rows = path.Count;
            if (rows < 2) return;
            var right = RightVectors(path);
            var P = new Vector3[rows, cols];
            var UV = new Vector2[rows, cols];
            float s = sStart;
            for (int r = 0; r < rows; r++)
            {
                if (r > 0) s += Vector3.Distance(new Vector3(path[r].x, 0, path[r].z), new Vector3(path[r - 1].x, 0, path[r - 1].z));
                for (int c = 0; c < cols; c++)
                {
                    float x = Mathf.Lerp(x0, x1, c / (float)(cols - 1));
                    Vector3 q = path[r] + right[r] * x;
                    q.y = w.SurfaceHeight(q, path[r].y) + yOff;
                    P[r, c] = q;
                    UV[r, c] = worldUV ? new Vector2(q.x, q.z) * 0.25f : new Vector2(x * 0.5f, s * 0.5f);
                }
            }
            mb.Grid(sub, P, UV);
        }

        /// <summary>Cara vertical (bordillo) pegada al terreno en el desplazamiento lateral x, entre y0 e y1.</summary>
        static void DrapeCurb(World w, MeshBuilder mb, int sub, List<Vector3> path, float x, float y0, float y1, bool faceRight)
        {
            int rows = path.Count;
            if (rows < 2) return;
            var right = RightVectors(path);
            var P = new Vector3[rows, 2];
            var UV = new Vector2[rows, 2];
            float s = 0;
            for (int r = 0; r < rows; r++)
            {
                if (r > 0) s += Vector3.Distance(path[r], path[r - 1]);
                Vector3 q = path[r] + right[r] * x;
                float gy = w.GroundHeight(q);
                P[r, 0] = new Vector3(q.x, gy + y0, q.z);
                P[r, 1] = new Vector3(q.x, gy + y1, q.z);
                UV[r, 0] = new Vector2(s * 0.5f, 0); UV[r, 1] = new Vector2(s * 0.5f, 0.1f);
            }
            int start = mb.VertexCount;
            for (int r = 0; r < rows; r++)
            {
                Vector3 n = right[r] * (faceRight ? 1f : -1f);
                mb.V(P[r, 0], n, UV[r, 0]); mb.V(P[r, 1], n, UV[r, 1]);
            }
            for (int r = 0; r < rows - 1; r++)
            {
                int a = start + r * 2, b = a + 1, c = a + 2, d = a + 3;
                Vector3 nrm = Vector3.Cross(mb.verts[c] - mb.verts[a], mb.verts[b] - mb.verts[a]);
                bool ok = Vector3.Dot(nrm, right[r] * (faceRight ? 1f : -1f)) >= 0;
                if (ok) { mb.Tri(sub, a, c, b); mb.Tri(sub, b, c, d); }
                else { mb.Tri(sub, a, b, c); mb.Tri(sub, b, d, c); }
            }
        }

        const float TunnelApron = 1.2f;      // la estructura del túnel sale 1.2 m por delante de cada boca
        const float TunnelTop = 6.0f;        // techo del cajón (el monte queda a >= 6.2 m sobre la calzada)
        const float ParapetDrop = 2.5f;      // pretil sólo si a un lado del tablero hay más de 2.5 m de caída

        static bool IsGrade(int f) { return f == 0 || f == 3; }

        static void BuildRoads(World w)
        {
            var g = w.graph;
            int nc = w.ChunkTotal;
            var road = new MeshBuilder[nc];     // asfalto pegado al terreno (visual)
            var deck = new MeshBuilder[nc];     // puentes: 0 asfalto, 1 hormigón (con colisión)
            var tube = new MeshBuilder[nc];     // túneles: hastiales, bóveda y muros de boca (con colisión)
            var shell = new MeshBuilder[nc];    // túneles: cajón exterior, sólo asoma en las bocas (visual)
            var tfloor = new MeshBuilder[nc];   // túneles: solera 0 asfalto, 1 hormigón (con colisión)
            var lamps = new MeshBuilder[nc];    // túneles: luminarias (visual, emisivo)
            var walk = new MeshBuilder[nc];     // aceras (visual)
            for (int i = 0; i < nc; i++)
            {
                road[i] = new MeshBuilder(1); deck[i] = new MeshBuilder(2);
                tube[i] = new MeshBuilder(1); shell[i] = new MeshBuilder(1);
                tfloor[i] = new MeshBuilder(2); lamps[i] = new MeshBuilder(1);
                walk[i] = new MeshBuilder(1);
            }

            // enlaces en viaducto: nodos a los que sólo llegan tableros (no tocan el terreno)
            var allBridge = new bool[g.nodes.Length];
            for (int n = 0; n < g.nodes.Length; n++)
            {
                int cnt = 0, br = 0;
                foreach (int ei in g.adj[n])
                {
                    var e = g.edges[ei];
                    if (e.a == e.b) continue;
                    int fe = e.a == n ? e.flags[0] : e.flags[e.flags.Length - 1];
                    cnt++;
                    if (fe == 1) br++;
                }
                allBridge[n] = cnt > 0 && br == cnt;
            }

            for (int ei = 0; ei < g.edges.Length; ei++)
            {
                var e = g.edges[ei];
                var ed = w.data.roads.edges[ei];
                int ch = ChunkOf(w, e.pts[e.pts.Length / 2]);
                float hw = e.width * 0.5f;
                int cols = e.width >= 8f ? 5 : 4;
                float trimA = TrimAt(g, e.a), trimB = TrimAt(g, e.b);
                int last = e.pts.Length - 1;
                int tunnelRun = 0;
                int i0 = 0;
                while (i0 < last)
                {
                    int f = e.flags[i0 + 1];
                    int i1 = i0 + 1;
                    while (i1 < last && e.flags[i1 + 1] == f) i1++;
                    float s0 = e.cum[i0], s1 = e.cum[i1];
                    if (f == 1)
                        BuildDeck(w, e, i0, i1, s0, s1, hw, trimA, trimB, allBridge, deck[ch]);
                    else if (f == 2)
                    {
                        // media anchura exterior a cada lado (recortada por el pipeline si hay otra vía al lado)
                        float oL = hw + 3.5f, oR = hw + 3.5f;
                        if (ed.o != null && ed.o.Length >= 2 * tunnelRun + 2)
                        {
                            oL = Mathf.Max(ed.o[2 * tunnelRun], hw + 0.8f);
                            oR = Mathf.Max(ed.o[2 * tunnelRun + 1], hw + 0.8f);
                        }
                        tunnelRun++;
                        BuildTunnel(w, e, s0, s1, hw, oL, oR, tube[ch], shell[ch], tfloor[ch], lamps[ch]);
                    }
                    else
                    {
                        // calzada a nivel o paso cubierto: asfalto pegado al terreno (hasta la estructura del túnel)
                        float d0 = s0, d1 = s1;
                        if (i0 > 0 && e.flags[i0] == 2) d0 += TunnelApron;
                        if (i1 < last && e.flags[i1 + 1] == 2) d1 -= TunnelApron;
                        var path = Resample(e, d0, d1, 2f);
                        DrapeStrip(w, road[ch], 0, path, -hw, hw, cols, DrapeOffset, true);
                    }
                    i0 = i1;
                }

                // aceras en calles urbanas (visuales, pegadas al terreno), recortadas en los cruces
                if (e.type == "Calle")
                {
                    bool anyStruct = false;
                    foreach (int fl in e.flags) if (fl == 1 || fl == 2) { anyStruct = true; break; }
                    float tA = g.adj[e.a].Count >= 3 ? g.NodeRadius(e.a) + 2.5f : 0f;
                    float tB = g.adj[e.b].Count >= 3 ? g.NodeRadius(e.b) + 2.5f : 0f;
                    if (!anyStruct && e.length - tA - tB > 2f)
                    {
                        var path = Resample(e, tA, e.length - tB, 2.5f);
                        var wb = walk[ch];
                        DrapeStrip(w, wb, 0, path, -hw - 2f, -hw, 2, DrapeOffset + CurbHeight, false);
                        DrapeStrip(w, wb, 0, path, hw, hw + 2f, 2, DrapeOffset + CurbHeight, false);
                        DrapeCurb(w, wb, 0, path, -hw, DrapeOffset - 0.05f, DrapeOffset + CurbHeight, true);
                        DrapeCurb(w, wb, 0, path, hw, DrapeOffset - 0.05f, DrapeOffset + CurbHeight, false);
                    }
                }
            }

            // cruces: plataforma pegada al terreno (visual); en los enlaces en viaducto, losa de tablero
            for (int n = 0; n < g.nodes.Length; n++)
            {
                if (g.adj[n].Count < 2) continue;
                int ch = ChunkOf(w, g.nodes[n]);
                if (allBridge[n])
                {
                    if (g.adj[n].Count >= 3) BuildDeckDisc(g, n, deck[ch]);
                    continue;   // con dos tramos el tablero ya es continuo
                }
                BuildJunctionPad(w, g, n, road[ch]);
            }

            var asphalt = Mat.Textured("asphalt", Tex.Noise("asphalt", Asphalt, 0.15f, 64, 3), Color.white, 0.15f);
            var concrete = Mat.Color(Concrete, 0.05f);
            var tunnelMat = Mat.Textured("tunnel", Tex.Noise("tunnel", new Color(0.78f, 0.77f, 0.74f), 0.06f, 32, 9), Color.white, 0.1f);
            var lampMat = Mat.Emissive(new Color(1f, 0.92f, 0.7f));
            var walkMat = Mat.Textured("sidewalk", Tex.Noise("sidewalk", Sidewalk, 0.08f, 32, 5), Color.white, 0.05f);
            var parent = new GameObject("Viales b5m").transform;
            parent.SetParent(w.root, false);
            for (int i = 0; i < nc; i++)
            {
                if (road[i].VertexCount > 0)
                {
                    var go = MeshBuilder.Spawn("Calzada_" + i, road[i].Build("roads_" + i), new[] { asphalt }, parent, false);
                    go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                if (deck[i].VertexCount > 0)
                    MeshBuilder.Spawn("Puentes_" + i, deck[i].Build("bridges_" + i), new[] { asphalt, concrete }, parent, true);
                if (tube[i].VertexCount > 0)
                    MeshBuilder.Spawn("Tuneles_" + i, tube[i].Build("tunnels_" + i), new[] { tunnelMat }, parent, true);
                if (shell[i].VertexCount > 0)
                    MeshBuilder.Spawn("TunelesCajon_" + i, shell[i].Build("tunnelshell_" + i), new[] { concrete }, parent, false);
                if (tfloor[i].VertexCount > 0)
                {
                    var go = MeshBuilder.Spawn("TunelesSolera_" + i, tfloor[i].Build("tunnelfloor_" + i), new[] { asphalt, concrete }, parent, true);
                    go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                if (lamps[i].VertexCount > 0)
                {
                    var go = MeshBuilder.Spawn("TunelesLuces_" + i, lamps[i].Build("tunnellamps_" + i), new[] { lampMat }, parent, false);
                    go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                if (walk[i].VertexCount > 0)
                {
                    var go = MeshBuilder.Spawn("Aceras_" + i, walk[i].Build("walk_" + i), new[] { walkMat }, parent, false);
                    go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            }
        }

        // ------------------------------------------------------------------ puentes
        static Vector3 ClampToGround(World w, Vector3 q)
        {
            q.y = Mathf.Clamp(w.SurfaceHeight(q, q.y) + 0.01f, q.y - 0.5f, q.y + 0.3f);
            return q;
        }

        /// <summary>Tablero de un tramo de puente (con colisión) y pretiles sólo donde hay caída real.</summary>
        static void BuildDeck(World w, RoadEdge e, int i0, int i1, float s0, float s1, float hw,
                              float trimA, float trimB, bool[] allBridge, MeshBuilder mb)
        {
            int last = e.pts.Length - 1;
            // el tablero va del último punto antes del puente al primero después; un extremo se iguala al
            // terreno sólo si allí la vía va a nivel (si sigue en viaducto o entra en un túnel, no)
            int k0 = i0, k1 = Mathf.Min(i1 + 1, last);
            var run = new List<Vector3>();
            for (int k = k0; k <= k1; k++) run.Add(e.pts[k] + Vector3.up * 0.02f);
            bool clampA = k0 == 0 ? !allBridge[e.a] : IsGrade(e.flags[k0]);
            bool clampB = k1 == last ? !allBridge[e.b] : IsGrade(e.flags[k1]);
            if (clampA) run[0] = ClampToGround(w, run[0]);
            if (clampB) run[run.Count - 1] = ClampToGround(w, run[run.Count - 1]);
            var surf = new[] { new Vector2(-hw, -1.4f), new Vector2(-hw, 0f), new Vector2(hw, 0f), new Vector2(hw, -1.4f) };
            mb.Extrude(0, run, surf, 0.25f, false, 0.25f, s0);

            // pretiles: sólo en los tramos en que a ese lado hay más de ParapetDrop m de caída (viaductos,
            // pasos sobre otra vía o sobre un río); nunca en los cruces
            float p0 = Mathf.Max(s0, trimA), p1 = Mathf.Min(s1, e.length - trimB);
            var par = Resample(e, p0, p1, 2f);
            if (par.Count < 2) return;
            var rv = RightVectors(par);
            int n = par.Count;
            var leftProf = new[] { new Vector2(-hw - 0.35f, -1.4f), new Vector2(-hw - 0.35f, 1.0f), new Vector2(-hw, 1.0f), new Vector2(-hw, 0.02f) };
            var rightProf = new[] { new Vector2(hw, 0.02f), new Vector2(hw, 1.0f), new Vector2(hw + 0.35f, 1.0f), new Vector2(hw + 0.35f, -1.4f) };
            for (int side = -1; side <= 1; side += 2)
            {
                var need = new bool[n];
                for (int r = 0; r < n; r++)
                {
                    Vector3 q = par[r] + rv[r] * (side * (hw + 1f));
                    need[r] = par[r].y - w.GroundHeight(q) > ParapetDrop;
                }
                var keep = new bool[n];
                for (int r = 0; r < n; r++)
                    keep[r] = need[r] || (r > 0 && need[r - 1]) || (r < n - 1 && need[r + 1]);
                int a = 0;
                while (a < n)
                {
                    if (!keep[a]) { a++; continue; }
                    int b = a;
                    while (b + 1 < n && keep[b + 1]) b++;
                    if (b - a >= 2)
                        mb.Extrude(1, par.GetRange(a, b - a + 1), side < 0 ? leftProf : rightProf, 0.5f);
                    a = b + 1;
                }
            }
        }

        /// <summary>Losa de tablero en un enlace en viaducto (todas las vías que llegan son puente).</summary>
        static void BuildDeckDisc(RoadGraph g, int n, MeshBuilder mb)
        {
            Vector3 c = g.nodes[n];
            float R = g.NodeRadius(n) + 0.5f;
            // plano ajustado a los tableros que llegan (las ramas de un enlace no son horizontales)
            double sxx = 0, sxz = 0, szz = 0, sxy = 0, szy = 0;
            foreach (int ei in g.adj[n])
                foreach (var p in g.edges[ei].pts)
                {
                    float dx = p.x - c.x, dz = p.z - c.z;
                    if (dx * dx + dz * dz > (R + 2f) * (R + 2f)) continue;
                    float dy = p.y - c.y;
                    sxx += dx * dx; sxz += dx * dz; szz += dz * dz; sxy += dx * dy; szy += dz * dy;
                }
            double det = sxx * szz - sxz * sxz;
            float gx = 0, gz = 0;
            if (det > 1e-3)
            {
                gx = Mathf.Clamp((float)((sxy * szz - szy * sxz) / det), -0.15f, 0.15f);
                gz = Mathf.Clamp((float)((szy * sxx - sxy * sxz) / det), -0.15f, 0.15f);
            }
            const int seg = 24, rings = 3;
            var P = new Vector3[rings + 1, seg + 1];
            var UV = new Vector2[rings + 1, seg + 1];
            for (int r = 0; r <= rings; r++)
                for (int i = 0; i <= seg; i++)
                {
                    float ang = i / (float)seg * Mathf.PI * 2f;
                    float rad = R * r / rings;
                    float dx = Mathf.Cos(ang) * rad, dz = Mathf.Sin(ang) * rad;
                    var q = new Vector3(c.x + dx, c.y + gx * dx + gz * dz - 0.01f, c.z + dz);
                    P[r, i] = q;
                    UV[r, i] = new Vector2(q.x, q.z) * 0.25f;
                }
            mb.Grid(0, P, UV);
            for (int i = 0; i < seg; i++)
            {
                Vector3 a = P[rings, i], b = P[rings, i + 1];
                Vector3 outDir = new Vector3((a.x + b.x) * 0.5f - c.x, 0, (a.z + b.z) * 0.5f - c.z);
                mb.Quad(1, a, b, b + Vector3.down * 1.4f, a + Vector3.down * 1.4f, outDir,
                        Vector2.zero, Vector2.right, Vector2.one, Vector2.up);
            }
        }

        // ------------------------------------------------------------------ túneles
        /// <summary>
        /// Túnel de verdad: el monte sigue encima (el pipeline sólo abre agujeros en el terreno en las bocas) y
        /// aquí se construye el tubo: solera, hastiales y bóveda con colisión, muros de boca y un cajón exterior
        /// que tapa los bordes de los agujeros.
        /// </summary>
        static void BuildTunnel(World w, RoadEdge e, float s0, float s1, float hw, float oL, float oR,
                                MeshBuilder tube, MeshBuilder shell, MeshBuilder floor, MeshBuilder lamps)
        {
            float a0 = Mathf.Max(0f, s0 - TunnelApron), a1 = Mathf.Min(e.length, s1 + TunnelApron);
            var vp = Resample(e, a0, a1, 2f);
            if (vp.Count < 2) return;
            var rv = RightVectors(vp);
            int last = vp.Count - 1;
            float o = hw + 0.8f;
            var inner = new[]
            {
                new Vector2(o, -0.2f), new Vector2(o, 4.0f), new Vector2(o * 0.75f, 4.75f), new Vector2(o * 0.4f, 5.1f),
                new Vector2(0f, 5.2f), new Vector2(-o * 0.4f, 5.1f), new Vector2(-o * 0.75f, 4.75f), new Vector2(-o, 4.0f),
                new Vector2(-o, -0.2f)
            };
            tube.Extrude(0, vp, inner, 0.25f, true);
            shell.Extrude(0, vp, new[] { new Vector2(-oL, -8f), new Vector2(-oL, TunnelTop), new Vector2(oR, TunnelTop), new Vector2(oR, -8f) }, 0.25f);
            Headwall(tube, vp[0], rv[0], inner, oL, oR);
            Headwall(tube, vp[last], rv[last], inner, oL, oR);

            // solera: calzada (asfalto) + franjas laterales hasta el cajón (hormigón), a la cota de la calzada;
            // en los dos extremos se funde con el terreno para entrar sin escalón
            float[] xs = { -oL, -hw - 0.8f, -hw, -hw * 0.5f, 0f, hw * 0.5f, hw, hw + 0.8f, oR };
            int rows = vp.Count;
            var P = new Vector3[rows, xs.Length];
            var UV = new Vector2[rows, xs.Length];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < xs.Length; c++)
                {
                    Vector3 q = vp[r] + rv[r] * xs[c];
                    float y = vp[r].y;
                    if ((r == 0 && a0 < s0) || (r == last && a1 > s1))
                        y = Mathf.Clamp(w.SurfaceHeight(q, y), y - 0.3f, y + 0.3f);
                    P[r, c] = new Vector3(q.x, y + 0.03f, q.z);
                    UV[r, c] = new Vector2(q.x, q.z) * 0.25f;
                }
            floor.Grid(1, SubGrid(P, 0, 2), SubGrid(UV, 0, 2));
            floor.Grid(0, SubGrid(P, 2, 6), SubGrid(UV, 2, 6));
            floor.Grid(1, SubGrid(P, 6, 8), SubGrid(UV, 6, 8));

            // luminarias: una línea de pantallas cada 6 m en la clave de la bóveda
            float acc = 3f;
            for (int r = 1; r < rows; r++)
            {
                Vector3 d = vp[r] - vp[r - 1];
                float L = d.magnitude;
                while (acc <= L && L > 1e-3f)
                {
                    Vector3 p = Vector3.Lerp(vp[r - 1], vp[r], acc / L) + Vector3.up * 5.12f;
                    Vector3 fwd = d / L;
                    Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
                    lamps.Box(0, p, right, Vector3.up, fwd, new Vector3(0.35f, 0.05f, 0.9f));
                    acc += 6f;
                }
                acc -= L;
            }
        }

        static T[,] SubGrid<T>(T[,] a, int c0, int c1)
        {
            int rows = a.GetLength(0), cols = c1 - c0 + 1;
            var r = new T[rows, cols];
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++) r[i, j] = a[i, c0 + j];
            return r;
        }

        /// <summary>Muro de boca: frontal de hormigón de -oL..oR m y TunnelTop m de alto, con el hueco del túnel.</summary>
        static void Headwall(MeshBuilder mb, Vector3 p, Vector3 right, Vector2[] inner, float oL, float oR)
        {
            Vector3 fwd = Vector3.Cross(right, Vector3.up);
            System.Func<float, float, Vector3> P = (x, y) => p + right * x + Vector3.up * y;
            float o = inner[0].x, yb = inner[0].y;
            Vector2 u0 = Vector2.zero, u1 = Vector2.right, u2 = Vector2.one, u3 = Vector2.up;
            mb.Quad(0, P(o, yb), P(oR, yb), P(oR, TunnelTop), P(o, TunnelTop), fwd, u0, u1, u2, u3, true);
            mb.Quad(0, P(-oL, yb), P(-o, yb), P(-o, TunnelTop), P(-oL, TunnelTop), fwd, u0, u1, u2, u3, true);
            for (int i = 1; i + 1 < inner.Length - 1; i++)
            {
                Vector2 a = inner[i], b = inner[i + 1];
                mb.Quad(0, P(a.x, a.y), P(b.x, b.y), P(b.x, TunnelTop), P(a.x, TunnelTop), fwd, u0, u1, u2, u3, true);
            }
        }

        static void BuildJunctionPad(World w, RoadGraph g, int n, MeshBuilder mb)
        {
            Vector3 c = g.nodes[n];
            float R = g.NodeRadius(n) + 0.8f;
            const int seg = 20, rings = 3;
            var P = new Vector3[rings + 1, seg + 1];
            var UV = new Vector2[rings + 1, seg + 1];
            for (int r = 0; r <= rings; r++)
                for (int i = 0; i <= seg; i++)
                {
                    float a = i / (float)seg * Mathf.PI * 2f;
                    float rad = R * r / rings;
                    Vector3 q = new Vector3(c.x + Mathf.Cos(a) * rad, 0, c.z + Mathf.Sin(a) * rad);
                    q.y = w.SurfaceHeight(q, c.y) + JunctionOffset;
                    P[r, i] = q;
                    UV[r, i] = new Vector2(q.x, q.z) * 0.25f;
                }
            mb.Grid(0, P, UV);
        }

        // ------------------------------------------------------------------ ríos
        static void BuildRivers(World w)
        {
            var mb = new MeshBuilder(1);
            foreach (var r in w.data.scenery.rivers)
            {
                int n = r.p.Length / 3;
                if (n < 2) continue;
                var path = new List<Vector3>(n);
                for (int i = 0; i < n; i++) path.Add(new Vector3(r.p[i * 3], r.p[i * 3 + 1], r.p[i * 3 + 2]));
                float hw = r.width * 0.5f;
                mb.Extrude(0, path, new[] { new Vector2(-hw, 0), new Vector2(hw, 0) }, 0.05f);
            }
            if (mb.VertexCount == 0) return;
            var water = Mat.Color(new Color(0.18f, 0.36f, 0.45f), 0.85f);
            var go = MeshBuilder.Spawn("Urumea y regatas", mb.Build("rivers"), new[] { water }, w.root, false);
            go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // ------------------------------------------------------------------ edificios
        static void BuildBuildings(World w)
        {
            int nc = w.ChunkTotal;
            // submallas: 0-5 fachadas, 6 cubierta gris, 7 teja, 8 estructuras
            const int SubRoofGrey = 6, SubRoofTile = 7, SubShed = 8;
            var solid = new MeshBuilder[nc];
            var lifted = new MeshBuilder[nc];
            for (int i = 0; i < nc; i++) { solid[i] = new MeshBuilder(9); lifted[i] = new MeshBuilder(9); }

            var bs = w.data.buildings.buildings;
            for (int bi = 0; bi < bs.Length; bi++)
            {
                var b = bs[bi];
                int nv = b.v.Length / 2;
                if (nv < 3 || b.t == null || b.t.Length < 3) continue;
                var v2 = new Vector2[nv];
                Vector2 cen = Vector2.zero;
                for (int i = 0; i < nv; i++) { v2[i] = new Vector2(b.v[i * 2], b.v[i * 2 + 1]); cen += v2[i]; }
                cen /= nv;
                var mb = (b.l > 0.1f ? lifted : solid)[ChunkOf(w, new Vector3(cen.x, 0, cen.y))];

                float y0 = b.b + b.l, y1 = b.h;
                float hgt = y1 - b.b;
                // área aproximada
                float area = 0;
                for (int i = 0; i < b.t.Length; i += 3)
                {
                    Vector2 A = v2[b.t[i]], B = v2[b.t[i + 1]], C = v2[b.t[i + 2]];
                    area += Mathf.Abs((B.x - A.x) * (C.y - A.y) - (B.y - A.y) * (C.x - A.x)) * 0.5f;
                }
                int wallSub, roofSub;
                if (b.k == 1) { wallSub = SubShed; roofSub = SubShed; }
                else
                {
                    uint hsh = (uint)(bi * 2654435761u);
                    bool house = area < 260f && hgt < 11f;
                    wallSub = house ? (hsh % 3 == 0 ? 0 : 2) : (int)(hsh % 6);
                    roofSub = house || (hsh % 4 == 0 && area < 600f) ? SubRoofTile : SubRoofGrey;
                }

                // paredes (cada anillo: exterior y patios)
                int start = 0;
                foreach (int rl in b.r)
                {
                    float perim = 0;
                    for (int i = 0; i < rl; i++)
                    {
                        Vector2 p0 = v2[start + i], p1 = v2[start + (i + 1) % rl];
                        Vector2 d = p1 - p0;
                        float len = d.magnitude;
                        if (len < 0.05f) continue;
                        Vector3 outward = new Vector3(-d.y, 0, d.x);
                        Vector3 a = new Vector3(p0.x, y0, p0.y), bb = new Vector3(p1.x, y0, p1.y);
                        Vector3 c = new Vector3(p1.x, y1, p1.y), dd = new Vector3(p0.x, y1, p0.y);
                        float u0 = perim / 3.4f, u1 = (perim + len) / 3.4f;
                        float v0 = (y0 - b.b) / 3.0f, v1 = (y1 - b.b) / 3.0f;
                        mb.Quad(wallSub, a, bb, c, dd, outward, new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u1, v1), new Vector2(u0, v1));
                        perim += len;
                    }
                    start += rl;
                }
                // cubierta (y fondo si es un soportal elevado)
                for (int i = 0; i + 2 < b.t.Length; i += 3)
                {
                    Vector2 A = v2[b.t[i]], B = v2[b.t[i + 1]], C = v2[b.t[i + 2]];
                    var pa = new Vector3(A.x, y1, A.y); var pb = new Vector3(B.x, y1, B.y); var pc = new Vector3(C.x, y1, C.y);
                    mb.FlatTri(roofSub, pa, pb, pc, Vector3.up, A * 0.25f, B * 0.25f, C * 0.25f);
                    if (b.l > 0.1f)
                    {
                        var qa = new Vector3(A.x, y0, A.y); var qb = new Vector3(B.x, y0, B.y); var qc = new Vector3(C.x, y0, C.y);
                        mb.FlatTri(SubShed, qa, qb, qc, Vector3.down, A, B, C);
                    }
                }
            }

            var facade = Tex.Facade();
            var mats = new Material[9];
            for (int i = 0; i < 6; i++) mats[i] = Mat.Textured("facade" + i, facade, WallColors[i], 0.1f);
            mats[SubRoofGrey] = Mat.Color(new Color(0.42f, 0.42f, 0.44f), 0.05f);
            mats[SubRoofTile] = Mat.Color(new Color(0.62f, 0.30f, 0.22f), 0.05f);
            mats[SubShed] = Mat.Color(new Color(0.58f, 0.60f, 0.62f), 0.2f);
            var parent = new GameObject("Edificios b5m").transform;
            parent.SetParent(w.root, false);
            for (int i = 0; i < nc; i++)
            {
                if (solid[i].VertexCount > 0) MeshBuilder.Spawn("Edificios_" + i, solid[i].Build("bu_" + i), mats, parent, true);
                if (lifted[i].VertexCount > 0) MeshBuilder.Spawn("Soportales_" + i, lifted[i].Build("bul_" + i), mats, parent, false);
            }
        }
    }
}
