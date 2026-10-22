using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>Carriles y cota de rodadura de las calles (compartido por marcas viales, tráfico y peatones).</summary>
    public static class Lanes
    {
        /// <summary>Distancia del nodo a la línea de detención de un cruce.</summary>
        public static float StopDist(RoadGraph g, int node)
        {
            return g.adj[node].Count >= 3 ? g.NodeRadius(node) + 2.6f : 0f;
        }

        /// <summary>Recorte del tramo en el nodo (donde empieza la plataforma del cruce).</summary>
        public static float Trim(RoadGraph g, int node)
        {
            return g.adj[node].Count >= 3 ? g.NodeRadius(node) + 2.0f : 0f;
        }

        public static int LaneCount(RoadEdge e)
        {
            return Mathf.Max(1, e.lanes);
        }

        /// <summary>Desplazamiento del centro del carril 'lane' (0 = el de la derecha) a la derecha del sentido de marcha.</summary>
        public static float Offset(RoadEdge e, int lane)
        {
            float hw = e.width * 0.5f;
            int n = LaneCount(e);
            float lw = e.OneWay ? e.width / n : hw / n;
            lane = Mathf.Clamp(lane, 0, n - 1);
            return hw - lw * (lane + 0.5f);
        }

        /// <summary>Vector derecho (xz) del tramo en s, en el sentido a->b.</summary>
        public static Vector3 Right(RoadEdge e, float s)
        {
            Vector3 t = e.TangentAt(s);
            return new Vector3(t.z, 0, -t.x);
        }

        /// <summary>Tipo de estructura en s: 0 a nivel, 1 puente, 2 túnel, 3 paso cubierto.</summary>
        public static int FlagAt(RoadEdge e, float s)
        {
            int i = System.Array.BinarySearch(e.cum, Mathf.Clamp(s, 0, e.length));
            if (i < 0) i = ~i;
            i = Mathf.Clamp(i, 1, e.pts.Length - 1);
            return e.flags[i];
        }

        static readonly RaycastHit[] hits = new RaycastHit[8];

        /// <summary>Cota del asfalto en el punto q (lateral) del tramo e en s.</summary>
        public static float SurfaceY(World w, RoadEdge e, float s, Vector3 q, bool raycastDecks)
        {
            Vector3 c = e.PointAt(s, true);
            int f = FlagAt(e, s);
            if (f == 2) return c.y + 0.03f;
            if (f == 1)
            {
                if (raycastDecks)
                {
                    int n = Physics.RaycastNonAlloc(new Vector3(q.x, c.y + 1.2f, q.z), Vector3.down, hits, 3f, ~0, QueryTriggerInteraction.Ignore);
                    float best = float.MaxValue, y = c.y + 0.02f;
                    for (int i = 0; i < n; i++)
                    {
                        if (hits[i].rigidbody != null) continue;
                        float d = Mathf.Abs(hits[i].point.y - (c.y + 0.02f));
                        if (d < best) { best = d; y = hits[i].point.y; }
                    }
                    return y;
                }
                return c.y + 0.02f;
            }
            return w.SurfaceHeight(q, c.y) + 0.06f;
        }
    }

    /// <summary>
    /// Mobiliario vial generado a partir de b5m + OpenStreetMap: marcas viales (eje continuo/discontinuo,
    /// bordes, carriles, líneas de detención, pasos de cebra), señales (STOP, CEDA, dirección prohibida,
    /// paso de peatones) y semáforos con su controlador de fases.
    /// </summary>
    public static class RoadFurniture
    {
        const float Lift = 0.022f;           // marcas sobre el asfalto
        const float DashOn = 3.5f, DashOff = 5.5f;
        const float LineHalf = 0.06f;        // líneas de 12 cm (calles); 15 cm en carreteras
        public static readonly Color Paint = new Color(0.93f, 0.93f, 0.9f);

        public static void Build(World w)
        {
            Physics.SyncTransforms();
            var g = w.graph;
            var parent = new GameObject("Señalización (OSM)").transform;
            parent.SetParent(w.root, false);
            int nc = w.ChunkTotal;
            var marks = new MeshBuilder[nc];
            var signs = new MeshBuilder[nc];
            for (int i = 0; i < nc; i++) { marks[i] = new MeshBuilder(1); signs[i] = new MeshBuilder(3); }

            for (int ei = 0; ei < g.edges.Length; ei++)
                EdgeMarkings(w, g, g.edges[ei], marks[Chunk(w, g.edges[ei].pts[g.edges[ei].pts.Length / 2])]);

            // ---- señales verticales
            var placed = new HashSet<int>();
            foreach (var kv in g.signs)
            {
                int ei = kv.Key / 2, end = kv.Key % 2;
                var e = g.edges[ei];
                int node = end == 0 ? e.a : e.b;
                if (g.nodeSignal[node] == 1) continue;
                if (!e.Allows(end == 1)) continue;     // nadie llega al cruce por ese extremo
                ApproachSign(w, g, e, node, kv.Value == 1 ? SignType.Stop : SignType.GiveWay, signs, marks);
                placed.Add(kv.Key);
            }
            // rotondas: ceda el paso en todas las entradas
            for (int n = 0; n < g.nodes.Length; n++)
            {
                if (g.nodeSignal[n] != 2) continue;
                foreach (int ei in g.adj[n])
                {
                    var e = g.edges[ei];
                    if (e.roundabout) continue;
                    int key = ei * 2 + (e.a == n ? 0 : 1);
                    if (placed.Contains(key) || !e.Allows(e.b == n)) continue;
                    placed.Add(key);
                    g.signs[key] = 2;   // también para la IA del tráfico
                    ApproachSign(w, g, e, n, SignType.GiveWay, signs, marks);
                }
            }
            // dirección prohibida en la boca de salida de las calles de sentido único
            for (int ei = 0; ei < g.edges.Length; ei++)
            {
                var e = g.edges[ei];
                if (!e.OneWay || e.length < 12f || e.roundabout) continue;
                int node = e.ow == 1 ? e.b : e.a;
                if (g.adj[node].Count < 2) continue;
                bool fromA = node == e.a;
                float d = Mathf.Max(Lanes.Trim(g, node) + 1.5f, 3f);
                if (d > e.length * 0.5f) continue;
                float s = fromA ? d : e.length - d;
                if (Lanes.FlagAt(e, s) != 0) continue;
                Vector3 travel = e.TangentAt(s) * (fromA ? 1f : -1f);       // quien entraría desde el nodo
                Vector3 right = new Vector3(travel.z, 0, -travel.x);
                float hw = e.width * 0.5f;
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector3 q = e.PointAt(s, true) + right * side * (hw + 0.6f);
                    q.y = Lanes.SurfaceY(w, e, s, q, false) + (e.type == "Calle" ? 0.07f : 0f);
                    Sign(signs[Chunk(w, q)], q, -travel, SignType.NoEntry);
                }
            }
            // pasos de peatones: señal antes del paso en cada sentido permitido
            for (int ei = 0; ei < g.edges.Length; ei++)
            {
                var e = g.edges[ei];
                foreach (var c in g.crossings[ei])
                {
                    if (Lanes.FlagAt(e, c.s) != 0) continue;
                    for (int dir = 0; dir < 2; dir++)
                    {
                        bool fwd = dir == 0;
                        if (!e.Allows(fwd)) continue;
                        float s = c.s + (fwd ? -3.5f : 3.5f);
                        if (s < 1f || s > e.length - 1f) continue;
                        Vector3 travel = e.TangentAt(s) * (fwd ? 1f : -1f);
                        Vector3 right = new Vector3(travel.z, 0, -travel.x);
                        Vector3 q = e.PointAt(s, true) + right * (e.width * 0.5f + 0.6f);
                        q.y = Lanes.SurfaceY(w, e, s, q, false) + (e.type == "Calle" ? 0.07f : 0f);
                        Sign(signs[Chunk(w, q)], q, -travel, SignType.Crossing);
                    }
                }
            }

            var paint = Mat.Color(Paint, 0.25f);
            var metal = Mat.Color(new Color(0.55f, 0.57f, 0.6f), 0.5f);
            var face = Mat.Textured("signs_atlas", SignAtlas(), Color.white, 0.35f);
            var back = Mat.Color(new Color(0.62f, 0.63f, 0.65f), 0.3f);
            for (int i = 0; i < nc; i++)
            {
                if (marks[i].VertexCount > 0)
                {
                    var go = MeshBuilder.Spawn("MarcasViales_" + i, marks[i].Build("marks_" + i), new[] { paint }, parent, false);
                    go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                if (signs[i].VertexCount > 0)
                    MeshBuilder.Spawn("Señales_" + i, signs[i].Build("signs_" + i), new[] { metal, face, back }, parent, false);
            }

            // ---- semáforos
            var tc = new GameObject("Semáforos (OSM)").AddComponent<TrafficControl>();
            tc.transform.SetParent(w.root, false);
            tc.Init(w);
            w.signals = tc;
            // líneas de detención en los cruces con semáforo
            foreach (var j in tc.Junctions)
                foreach (int ei in j.approaches)
                    StopLine(w, g, g.edges[ei], j.node, false, marks[0]);
            if (marks[0].VertexCount > 0)
            {
                var go = MeshBuilder.Spawn("MarcasSemaforos", marks[0].Build("marks_sig"), new[] { paint }, parent, false);
                go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        static int Chunk(World w, Vector3 p)
        {
            return w.ChunkIndex(p);
        }

        // ------------------------------------------------------------------ marcas longitudinales
        static void EdgeMarkings(World w, RoadGraph g, RoadEdge e, MeshBuilder mb)
        {
            float s0 = Lanes.Trim(g, e.a), s1 = e.length - Lanes.Trim(g, e.b);
            if (s1 - s0 < 2f || e.width < 5f) return;
            float hw = e.width * 0.5f;
            bool road = e.type == "Carretera" || e.width >= 8f;
            float lh = road ? 0.075f : LineHalf;
            bool jA = g.adj[e.a].Count >= 3, jB = g.adj[e.b].Count >= 3;

            // eje: doble sentido -> discontinuo, continuo en curvas cerradas, puentes/túneles y antes de los cruces
            if (!e.OneWay && !e.roundabout)
            {
                System.Func<float, bool> solid = s =>
                {
                    if ((jA && s < s0 + 15f) || (jB && s > s1 - 15f)) return true;
                    int f = Lanes.FlagAt(e, s);
                    if (f == 1 || f == 2) return true;
                    return CurveRadius(e, s) < 60f;
                };
                Pattern(w, e, mb, s0, s1, 0f, lh, solid);
                // carriles adicionales por sentido
                int n = Lanes.LaneCount(e);
                for (int k = 1; k < n; k++)
                {
                    float x = hw - hw / n * k;
                    Pattern(w, e, mb, s0, s1, x, lh, s => false);
                    Pattern(w, e, mb, s0, s1, -x, lh, s => false);
                }
            }
            else
            {
                int n = Lanes.LaneCount(e);
                for (int k = 1; k < n; k++)
                {
                    float x = -hw + e.width / n * k;
                    Pattern(w, e, mb, s0, s1, x, lh, s => (jB && s > s1 - 12f && e.ow == 1) || (jA && s < s0 + 12f && e.ow == -1));
                }
            }
            // bordes continuos en carreteras y autovías
            if (road)
            {
                Strip(w, e, mb, s0, s1, -hw + 0.3f, lh);
                Strip(w, e, mb, s0, s1, hw - 0.3f, lh);
            }
            // pasos de cebra (OSM)
            foreach (var c in g.crossings[e.index])
                if (c.s > s0 + 2.5f && c.s < s1 - 2.5f) Zebra(w, e, mb, c.s);
            // flechas de sentido único pintadas en la calzada (cada ~40 m, al menos una)
            if (e.OneWay && !e.roundabout && s1 - s0 > 9f)
            {
                float first = Mathf.Min(s0 + 10f, (s0 + s1) * 0.5f);
                for (float sa = first; sa <= s1 - 4f || sa == first; sa += 40f)
                {
                    bool nearZebra = false;
                    foreach (var c in g.crossings[e.index]) if (Mathf.Abs(c.s - sa) < 6f) nearZebra = true;
                    if (!nearZebra && Lanes.FlagAt(e, sa) != 2) Arrow(w, e, mb, sa, e.ow == 1 ? 1f : -1f);
                    if (sa > s1 - 4f) break;
                }
            }
        }

        /// <summary>Flecha recta pintada (5 m) centrada en s, apuntando en el sentido dir (+1 = a->b).</summary>
        static void Arrow(World w, RoadEdge e, MeshBuilder mb, float s, float dir)
        {
            float tail = s - 2.5f * dir, neck = s + 0.8f * dir, tip = s + 2.5f * dir;
            if (Mathf.Min(tail, tip) < 0.5f || Mathf.Max(tail, tip) > e.length - 0.5f) return;
            Patch(w, e, mb, Mathf.Min(tail, neck), Mathf.Max(tail, neck), -0.15f, 0.15f);
            Vector3 right = Lanes.Right(e, s);
            System.Func<float, float, Vector3> P = (ss, x) =>
            {
                Vector3 q = e.PointAt(ss, true) + right * x;
                q.y = Lanes.SurfaceY(w, e, ss, q, true) + Lift + 0.003f;
                return q;
            };
            mb.FlatTri(0, P(neck, -0.6f), P(neck, 0.6f), P(tip, 0f), Vector3.up, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 1));
        }

        static float CurveRadius(RoadEdge e, float s)
        {
            float d = 8f;
            if (e.length < 2 * d) return 1e9f;
            float sa = Mathf.Clamp(s - d, 0, e.length), sb = Mathf.Clamp(s + d, 0, e.length);
            Vector3 ta = e.TangentAt(sa), tb = e.TangentAt(sb);
            float ang = Vector3.Angle(ta, tb) * Mathf.Deg2Rad;
            return ang < 1e-3f ? 1e9f : (sb - sa) / ang;
        }

        /// <summary>Línea discontinua (o continua donde solid(s)) en el desplazamiento lateral x (sentido a->b).</summary>
        static void Pattern(World w, RoadEdge e, MeshBuilder mb, float s0, float s1, float x, float half, System.Func<float, bool> solid)
        {
            const float step = 0.5f;
            float runStart = -1f;
            for (float s = s0; s <= s1 + 1e-3f; s += step)
            {
                bool on = solid(s) || Mathf.Repeat(s, DashOn + DashOff) < DashOn;
                if (on && runStart < 0) runStart = s;
                if ((!on || s + step > s1) && runStart >= 0)
                {
                    float end = on ? Mathf.Min(s, s1) : s;
                    if (end - runStart > 0.4f) Strip(w, e, mb, runStart, end, x, half);
                    runStart = -1f;
                }
            }
        }

        /// <summary>Banda de pintura pegada al asfalto entre s0 y s1, centrada en el desplazamiento lateral x.</summary>
        static void Strip(World w, RoadEdge e, MeshBuilder mb, float s0, float s1, float x, float half)
        {
            float L = s1 - s0;
            if (L < 0.2f) return;
            int rows = Mathf.Max(1, Mathf.CeilToInt(L / 2f)) + 1;
            var P = new Vector3[rows, 2];
            var UV = new Vector2[rows, 2];
            for (int r = 0; r < rows; r++)
            {
                float s = s0 + L * r / (rows - 1);
                Vector3 c = e.PointAt(s, true);
                Vector3 right = Lanes.Right(e, s);
                for (int k = 0; k < 2; k++)
                {
                    Vector3 q = c + right * (x + (k == 0 ? -half : half));
                    q.y = Lanes.SurfaceY(w, e, s, q, true) + Lift;
                    P[r, k] = q;
                    UV[r, k] = new Vector2(k, s);
                }
            }
            mb.Grid(0, P, UV);
        }

        /// <summary>Rectángulo transversal (s0..s1 a lo largo, x0..x1 a lo ancho) pegado al asfalto.</summary>
        static void Patch(World w, RoadEdge e, MeshBuilder mb, float s0, float s1, float x0, float x1)
        {
            int cols = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(x1 - x0) / 1.5f) + 1);
            var P = new Vector3[2, cols];
            var UV = new Vector2[2, cols];
            for (int r = 0; r < 2; r++)
            {
                float s = r == 0 ? s0 : s1;
                Vector3 c = e.PointAt(s, true);
                Vector3 right = Lanes.Right(e, (s0 + s1) * 0.5f);
                for (int k = 0; k < cols; k++)
                {
                    float x = Mathf.Lerp(x0, x1, k / (float)(cols - 1));
                    Vector3 q = c + right * x;
                    q.y = Lanes.SurfaceY(w, e, s, q, true) + Lift + 0.002f;
                    P[r, k] = q;
                    UV[r, k] = new Vector2(x, s);
                }
            }
            mb.Grid(0, P, UV);
        }

        static void Zebra(World w, RoadEdge e, MeshBuilder mb, float s)
        {
            float hw = e.width * 0.5f - 0.25f;
            const float depth = 2.0f;       // 4 m de paso (2 a cada lado)
            for (float x = -hw; x + 0.5f <= hw + 1e-3f; x += 1.0f)
                Patch(w, e, mb, s - depth, s + depth, x, x + 0.5f);
        }

        /// <summary>Línea de detención (continua) o de ceda el paso (discontinua) en la llegada al nodo.</summary>
        static void StopLine(World w, RoadGraph g, RoadEdge e, int node, bool dashed, MeshBuilder mb)
        {
            bool atB = node == e.b;
            float d = Lanes.StopDist(g, node) - 0.4f;
            if (d <= 0 || d > e.length - 1f) return;
            float s = atB ? e.length - d : d;
            if (Lanes.FlagAt(e, s) == 2) return;
            float hw = e.width * 0.5f - 0.2f;
            // la llegada ocupa la mitad derecha (sentido hacia el nodo) o todo el ancho si es sentido único
            float x0, x1;
            if (e.OneWay) { x0 = -hw; x1 = hw; }
            else if (atB) { x0 = 0.1f; x1 = hw; }       // a->b: la derecha es +x
            else { x0 = -hw; x1 = -0.1f; }
            float t = dashed ? 0.2f : 0.25f;
            if (!dashed) { Patch(w, e, mb, s - t, s + t, x0, x1); return; }
            for (float x = x0; x < x1 - 0.3f; x += 1.0f)
                Patch(w, e, mb, s - t, s + t, x, Mathf.Min(x + 0.6f, x1));
        }

        // ------------------------------------------------------------------ señales verticales
        public enum SignType { Stop = 0, GiveWay = 1, NoEntry = 2, Crossing = 3 }

        static void ApproachSign(World w, RoadGraph g, RoadEdge e, int node, SignType t, MeshBuilder[] signs, MeshBuilder[] marks)
        {
            bool atB = node == e.b;
            float d = Lanes.StopDist(g, node);
            if (d > e.length - 1f) d = e.length * 0.5f;
            float s = atB ? e.length - d : d;
            if (Lanes.FlagAt(e, s) == 2) return;
            Vector3 travel = e.TangentAt(s) * (atB ? 1f : -1f);
            Vector3 right = new Vector3(travel.z, 0, -travel.x);
            Vector3 q = e.PointAt(s, true) + right * (e.width * 0.5f + 0.6f);
            q.y = Lanes.SurfaceY(w, e, s, q, false) + (e.type == "Calle" ? 0.07f : 0f);
            Sign(signs[Chunk(w, q)], q, -travel, t);
            StopLine(w, g, e, node, t == SignType.GiveWay, marks[Chunk(w, e.PointAt(s, true))]);
        }

        /// <summary>Poste + placa. 'facing' = hacia dónde mira la cara de la señal (hacia el conductor).</summary>
        public static void Sign(MeshBuilder mb, Vector3 foot, Vector3 facing, SignType t)
        {
            facing.y = 0; facing.Normalize();
            Vector3 right = new Vector3(facing.z, 0, -facing.x);
            float plateY = t == SignType.GiveWay ? 2.05f : 2.0f;
            mb.Box(0, foot + Vector3.up * (plateY * 0.5f), right, Vector3.up, facing, new Vector3(0.035f, plateY * 0.5f + 0.05f, 0.035f));
            Vector3 c = foot + Vector3.up * plateY + facing * 0.05f;
            // contorno de la placa en coordenadas (-1..1)
            var shape = new List<Vector2>();
            float size;
            switch (t)
            {
                case SignType.Stop:
                    size = 0.33f;
                    for (int i = 0; i < 8; i++) { float a = (22.5f + 45f * i) * Mathf.Deg2Rad; shape.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a))); }
                    break;
                case SignType.GiveWay:
                    size = 0.5f;
                    shape.Add(new Vector2(-1f, 0.62f)); shape.Add(new Vector2(0f, -1.1f)); shape.Add(new Vector2(1f, 0.62f));
                    break;
                case SignType.NoEntry:
                    size = 0.32f;
                    for (int i = 0; i < 20; i++) { float a = i / 20f * Mathf.PI * 2f; shape.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a))); }
                    break;
                default:
                    size = 0.32f;
                    shape.Add(new Vector2(-1, -1)); shape.Add(new Vector2(1, -1)); shape.Add(new Vector2(1, 1)); shape.Add(new Vector2(-1, 1));
                    break;
            }
            int cell = (int)t;
            System.Func<Vector2, Vector2> uv = p => new Vector2((cell + (Mathf.Clamp(p.x, -1, 1) + 1f) * 0.5f) / 4f, (Mathf.Clamp(p.y, -1.1f, 1f) + 1f) * 0.5f);
            System.Func<Vector2, Vector3> P = p => c - right * p.x * size + Vector3.up * p.y * size;   // -right: la cara mira a 'facing'
            Vector3 back = -facing * 0.012f;
            Vector2 cen = Vector2.zero;
            foreach (var p in shape) cen += p;
            cen /= shape.Count;
            for (int i = 0; i < shape.Count; i++)
            {
                Vector2 a = shape[i], b = shape[(i + 1) % shape.Count];
                mb.FlatTri(1, P(cen), P(a), P(b), facing, uv(cen), uv(a), uv(b));
                mb.FlatTri(2, P(cen) + back, P(a) + back, P(b) + back, -facing, Vector2.zero, Vector2.zero, Vector2.zero);
            }
        }

        // ------------------------------------------------------------------ texturas de las señales
        static Texture2D atlas;

        /// <summary>Atlas 4 x 1: STOP, CEDA EL PASO, dirección prohibida, paso de peatones (dibujadas en código).</summary>
        public static Texture2D SignAtlas()
        {
            if (atlas != null) return atlas;
            const int S = 128;
            atlas = new Texture2D(S * 4, S, TextureFormat.RGBA32, true) { name = "HR_signs", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var px = new Color32[S * 4 * S];
            Color32 red = new Color32(196, 18, 28, 255), white = new Color32(245, 245, 245, 255), blue = new Color32(0, 84, 166, 255), black = new Color32(20, 20, 20, 255);
            for (int cell = 0; cell < 4; cell++)
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        float u = (x + 0.5f) / S * 2f - 1f, v = (y + 0.5f) / S * 2f - 1f;
                        Color32 col = white;
                        switch (cell)
                        {
                            case 0:   // octógono rojo con borde blanco
                                {
                                    float m = 0f;
                                    for (int i = 0; i < 8; i++)
                                    {
                                        float a = (45f * i) * Mathf.Deg2Rad;
                                        m = Mathf.Max(m, u * Mathf.Cos(a) + v * Mathf.Sin(a));
                                    }
                                    col = m < 0.84f ? red : white;
                                    break;
                                }
                            case 1:   // triángulo invertido: borde rojo, interior blanco
                                {
                                    // distancia a los lados del triángulo (-1,0.62) (0,-1.1) (1,0.62)
                                    float top = 0.62f - v;
                                    float l = Side(u, v, new Vector2(-1f, 0.62f), new Vector2(0f, -1.1f));
                                    float r = Side(u, v, new Vector2(0f, -1.1f), new Vector2(1f, 0.62f));
                                    float inside = Mathf.Min(top, Mathf.Min(l, r));
                                    col = inside > 0.22f ? white : red;
                                    break;
                                }
                            case 2:   // círculo rojo con barra blanca
                                col = (Mathf.Abs(v) < 0.2f && Mathf.Abs(u) < 0.72f) ? white : red;
                                break;
                            default:  // cuadrado azul con triángulo blanco y peatón
                                {
                                    col = blue;
                                    float l = Side(u, v, new Vector2(0f, 0.78f), new Vector2(-0.8f, -0.62f));
                                    float r = Side(u, v, new Vector2(0.8f, -0.62f), new Vector2(0f, 0.78f));
                                    float b = v + 0.62f;
                                    if (Mathf.Min(l, Mathf.Min(r, b)) > 0f) col = white;
                                    if (Pedestrian(u, v)) col = black;
                                    break;
                                }
                        }
                        px[y * S * 4 + cell * S + x] = col;
                    }
            // letras STOP (fuente 5x7) sobre el octógono
            string[] glyphS = { "01111", "10000", "10000", "01110", "00001", "00001", "11110" };
            string[] glyphT = { "11111", "00100", "00100", "00100", "00100", "00100", "00100" };
            string[] glyphO = { "01110", "10001", "10001", "10001", "10001", "10001", "01110" };
            string[] glyphP = { "11110", "10001", "10001", "11110", "10000", "10000", "10000" };
            var word = new[] { glyphS, glyphT, glyphO, glyphP };
            const int scale = 4;
            int wpx = 4 * 5 * scale + 3 * scale * 1;
            int x0 = (S - wpx) / 2, y0 = (S - 7 * scale) / 2;
            for (int k = 0; k < 4; k++)
                for (int gy = 0; gy < 7; gy++)
                    for (int gx = 0; gx < 5; gx++)
                    {
                        if (word[k][gy][gx] != '1') continue;
                        for (int dy = 0; dy < scale; dy++)
                            for (int dx = 0; dx < scale; dx++)
                            {
                                int x = x0 + k * (5 * scale + scale) + gx * scale + dx;
                                int y = y0 + (6 - gy) * scale + dy;
                                px[y * S * 4 + x] = white;
                            }
                    }
            atlas.SetPixels32(px);
            atlas.Apply(true);
            return atlas;
        }

        /// <summary>Distancia con signo al lado a->b (positiva a la izquierda del recorrido a->b).</summary>
        static float Side(float u, float v, Vector2 a, Vector2 b)
        {
            Vector2 d = (b - a).normalized;
            Vector2 n = new Vector2(-d.y, d.x);
            return Vector2.Dot(new Vector2(u, v) - a, n);
        }

        static bool Pedestrian(float u, float v)
        {
            // figura sencilla: cabeza, cuerpo, piernas y franjas de cebra
            if ((new Vector2(u - 0.02f, v - 0.36f)).magnitude < 0.09f) return true;
            if (Mathf.Abs(u + 0.0f - (v - 0.2f) * -0.15f) < 0.06f && v > -0.05f && v < 0.26f) return true;
            if (v < -0.05f && v > -0.42f && (Mathf.Abs(u - (v + 0.05f) * 0.5f) < 0.05f || Mathf.Abs(u + (v + 0.05f) * 0.5f) < 0.05f)) return true;
            if (v > -0.56f && v < -0.48f && Mathf.Repeat(u * 5f, 1f) < 0.5f && Mathf.Abs(u) < 0.55f) return true;
            return false;
        }
    }

    /// <summary>
    /// Semáforos de los cruces regulados (OSM highway=traffic_signals). Dos grupos de fases según la
    /// orientación de los accesos: verde 12 s, ámbar 3 s, todo rojo 1.5 s.
    /// </summary>
    public class TrafficControl : MonoBehaviour
    {
        public enum LightState { None, Green, Amber, Red }
        public const float Green = 12f, Amber = 3f, AllRed = 1.5f;
        public const float Cycle = 2f * (Green + Amber + AllRed);

        public class Junction
        {
            public int node;
            public List<int> approaches = new List<int>();
            public Dictionary<int, int> group = new Dictionary<int, int>();
            public float offset;
            public List<Head> heads = new List<Head>();
        }

        public class Head
        {
            public int group;
            public Renderer red, amber, green;
            public LightState shown = LightState.None;
        }

        readonly Dictionary<int, Junction> junctions = new Dictionary<int, Junction>();
        public IEnumerable<Junction> Junctions { get { return junctions.Values; } }
        World world;
        Material onR, onA, onG, off;

        public void Init(World w)
        {
            world = w;
            var g = w.graph;
            onR = Mat.Emissive(new Color(1f, 0.12f, 0.08f));
            onA = Mat.Emissive(new Color(1f, 0.65f, 0.05f));
            onG = Mat.Emissive(new Color(0.15f, 1f, 0.35f));
            off = Mat.Color(new Color(0.12f, 0.12f, 0.12f), 0.6f);
            var housing = Mat.Color(new Color(0.15f, 0.16f, 0.17f), 0.4f);
            var pole = Mat.Color(new Color(0.55f, 0.57f, 0.6f), 0.5f);
            var rnd = new System.Random(5);
            for (int n = 0; n < g.nodes.Length; n++)
            {
                if (g.nodeSignal[n] != 1) continue;
                var j = new Junction { node = n, offset = (float)rnd.NextDouble() * Cycle };
                Vector3 refDir = Vector3.zero;
                foreach (int ei in g.adj[n])
                {
                    var e = g.edges[ei];
                    if (!e.Allows(e.b == n)) continue;      // sólo los tramos por los que se llega al cruce
                    Vector3 d = g.DepartDir(ei, n, 10f);
                    if (refDir == Vector3.zero) refDir = d;
                    j.approaches.Add(ei);
                    j.group[ei] = Mathf.Abs(Vector3.Dot(d, refDir)) > 0.6f ? 0 : 1;
                }
                if (j.approaches.Count == 0) continue;
                junctions[n] = j;
                foreach (int ei in j.approaches) j.heads.Add(BuildHead(w, g, g.edges[ei], n, j.group[ei], housing, pole));
            }
        }

        Head BuildHead(World w, RoadGraph g, RoadEdge e, int node, int group, Material housing, Material poleMat)
        {
            bool atB = node == e.b;
            float d = Mathf.Min(Lanes.StopDist(g, node), e.length * 0.5f);
            float s = atB ? e.length - d : d;
            Vector3 travel = e.TangentAt(s) * (atB ? 1f : -1f);
            Vector3 right = new Vector3(travel.z, 0, -travel.x);
            Vector3 foot = e.PointAt(s, true) + right * (e.width * 0.5f + 0.5f);
            foot.y = Lanes.SurfaceY(w, e, s, foot, false) + (e.type == "Calle" ? 0.07f : 0f);
            var root = new GameObject("Semaforo_" + node + "_" + e.index).transform;
            root.SetParent(transform, false);
            root.position = foot;
            root.rotation = Quaternion.LookRotation(-travel, Vector3.up);   // +Z de la cabeza mira al conductor
            var mb = new MeshBuilder(2);
            mb.Box(1, new Vector3(0, 1.6f, 0), Vector3.right, Vector3.up, Vector3.forward, new Vector3(0.06f, 1.6f, 0.06f));
            mb.Box(0, new Vector3(0, 2.9f, 0.12f), Vector3.right, Vector3.up, Vector3.forward, new Vector3(0.17f, 0.5f, 0.12f));
            mb.Box(0, new Vector3(0, 2.9f, 0.2f), Vector3.right, Vector3.up, Vector3.forward, new Vector3(0.24f, 0.58f, 0.02f));
            MeshBuilder.Spawn("poste", mb.Build("semaforo"), new[] { housing, poleMat }, root, false);
            var h = new Head { group = group };
            h.red = Lamp(root, 3.22f); h.amber = Lamp(root, 2.9f); h.green = Lamp(root, 2.58f);
            return h;
        }

        Renderer Lamp(Transform root, float y)
        {
            var mb = new MeshBuilder(1);
            const int seg = 12;
            Vector3 c = new Vector3(0, y, 0.245f);
            for (int i = 0; i < seg; i++)
            {
                float a0 = i / (float)seg * Mathf.PI * 2f, a1 = (i + 1) / (float)seg * Mathf.PI * 2f;
                mb.FlatTri(0, c, c + new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0) * 0.11f, c + new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0) * 0.11f,
                           Vector3.forward, Vector2.zero, Vector2.zero, Vector2.zero);
            }
            var go = MeshBuilder.Spawn("luz", mb.Build("lamp"), new[] { off }, root, false);
            go.isStatic = false;
            var r = go.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return r;
        }

        public bool IsSignalized(int node) { return junctions.ContainsKey(node); }

        /// <summary>Estado del semáforo para quien llega al nodo por la arista 'edge'.</summary>
        public LightState StateFor(int edge, int node)
        {
            Junction j;
            if (!junctions.TryGetValue(node, out j)) return LightState.None;
            int grp;
            if (!j.group.TryGetValue(edge, out grp)) return LightState.None;
            return Phase(j, grp);
        }

        static LightState Phase(Junction j, int grp)
        {
            float t = Mathf.Repeat(NetClock.Time + j.offset + (grp == 1 ? Green + Amber + AllRed : 0f), Cycle);
            if (t < Green) return LightState.Green;
            if (t < Green + Amber) return LightState.Amber;
            return LightState.Red;
        }

        void Update()
        {
            foreach (var j in junctions.Values)
                foreach (var h in j.heads)
                {
                    var st = Phase(j, h.group);
                    if (st == h.shown) continue;
                    h.shown = st;
                    h.red.sharedMaterial = st == LightState.Red ? onR : off;
                    h.amber.sharedMaterial = st == LightState.Amber ? onA : off;
                    h.green.sharedMaterial = st == LightState.Green ? onG : off;
                }
        }
    }
}
