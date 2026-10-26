using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// v16: trenes de Cercanías por la vía de b5m (línea Irun – Brinkola a su paso por Hernani y Urnieta).
    /// Más o menos un tren cada 4 km de vía, a velocidad de tren (hasta 80 km/h), con parada en los apeaderos y
    /// estaciones (OSM). El horario es una función de la hora común de la partida (NetClock), así que online
    /// todos los jugadores ven los mismos trenes en el mismo sitio sin mandar nada por la red.
    /// Los vagones son sólidos (cinemáticos): mejor no cruzarse en la vía.
    /// </summary>
    public class Trains : MonoBehaviour
    {
        public const float VMax = 22f, Accel = 0.75f, Decel = 0.9f, Dwell = 25f, Layover = 70f;
        public const float CarLen = 19.5f, CarGap = 1.0f, CarW = 2.9f, CarH = 3.7f;
        const int CarsPerTrain = 3;
        // largo del tren; el horario mueve su extremo "bajo" p en [0, L - TrainLen] (la cabeza es p + TrainLen en la ida y p en la vuelta)
        const float TrainLen = CarsPerTrain * (CarLen + CarGap) - CarGap;

        World world;

        /// <summary>Recorrido de un sentido: polilínea de su vía remuestreada cada 2 m, con desplazamiento lateral
        /// (sólo donde los datos traen una única línea para la vía doble) y sus paradas.</summary>
        class Route
        {
            public Vector3[] rp; public float[] off; public float len;
            public bool hiddenStart, hiddenEnd;
            public readonly List<float> stops = new List<float>();      // centro del tren en la estación
            public readonly List<string> names = new List<string>();
        }
        // [0] ida (de un extremo de la línea al otro), [1] vuelta (por la otra vía si la hay)
        readonly Route[] routes = new Route[2];
        public readonly List<string> stopNames = new List<string>();

        struct Phase { public float t0, dur, s0, v0, acc; public int route; public bool hidden; }
        readonly List<Phase> cycle = new List<Phase>();   // ida + vuelta; s0 = extremo trasero del tren en su ruta
        float cycleLen;

        class Train { public float phase; public Transform[] cars; public Rigidbody[] bodies; public bool visible = true; }
        readonly List<Train> trains = new List<Train>();
        public int Count { get { return trains.Count; } }
        public float RouteLength { get { return routes[0] != null ? routes[0].len : 0f; } }

        public static Trains Create(World w)
        {
            var g = w.graph;
            bool any = false;
            foreach (var e in g.edges) if (e.kind == RoadEdge.KindRail) { any = true; break; }
            if (!any) return null;
            var go = new GameObject("Trenes (Cercanías)");
            go.transform.SetParent(w.root, false);
            var t = go.AddComponent<Trains>();
            t.world = w;
            try
            {
                if (!t.BuildRoutes()) { Destroy(go); return null; }
                t.BuildStops(t.routes[0]); t.BuildStops(t.routes[1]);
                t.stopNames.AddRange(t.routes[0].names);
                t.BuildTimetable();
                t.BuildPlatforms();
                t.SpawnTrains();
                Debug.Log("Trenes: " + t.trains.Count + " en " + (t.routes[0].len / 1000f).ToString("F1") + " km de vía (vuelta " + (t.routes[1].len / 1000f).ToString("F1") +
                          " km), ciclo " + (t.cycleLen / 60f).ToString("F1") + " min, paradas: " + string.Join(", ", t.stopNames));
            }
            catch (System.Exception ex) { Debug.LogWarning("Trenes desactivados: " + ex.Message); Destroy(go); return null; }
            return t;
        }

        // ------------------------------------------------------------------ rutas
        // b5m dibuja cada vía como una línea: en la vía doble hay dos líneas paralelas (~3,8 m). La ida va de un
        // extremo de la línea al más alejado; la vuelta sale del extremo vecino (la otra vía) y evita las vías de la ida.
        // Donde las dos rutas comparten línea (vía doble dibujada con una sola línea, o vía única) cada sentido
        // se aparta medio entrevía a su lado para que los trenes no se atraviesen.
        bool BuildRoutes()
        {
            var g = world.graph;
            var adj = new Dictionary<int, List<int>>();
            for (int ei = 0; ei < g.edges.Length; ei++)
            {
                var e = g.edges[ei];
                if (e.kind != RoadEdge.KindRail || e.a == e.b) continue;
                if (!adj.ContainsKey(e.a)) adj[e.a] = new List<int>();
                if (!adj.ContainsKey(e.b)) adj[e.b] = new List<int>();
                adj[e.a].Add(ei); adj[e.b].Add(ei);
            }
            if (adj.Count == 0) return false;
            var ends = new List<int>();
            foreach (var kv in adj) if (kv.Value.Count == 1) ends.Add(kv.Key);
            int A = -1, B = -1;
            float best = -1f;
            for (int i = 0; i < ends.Count; i++)
                for (int j = i + 1; j < ends.Count; j++)
                {
                    float d = Flat(g.nodes[ends[i]] - g.nodes[ends[j]]).sqrMagnitude;
                    if (d > best && Dijkstra(adj, ends[i], ends[j], null) != null) { best = d; A = ends[i]; B = ends[j]; }
                }
            if (A < 0)
            {   // sin extremos (anillo): diámetro aproximado
                int start = -1; foreach (var k in adj.Keys) { start = k; break; }
                A = FarthestNode(adj, start); B = FarthestNode(adj, A);
                if (A == B) return false;
            }
            int A2 = Neighbour(ends, A, B), B2 = Neighbour(ends, B, A);
            var pf = Dijkstra(adj, A, B, null);
            if (pf == null || pf.Count == 0) return false;
            var used = new HashSet<int>(pf);
            int startB = B2;
            var pb = Dijkstra(adj, B2, A2, used);
            if (pb == null) { startB = B; pb = Dijkstra(adj, B, A, used); }
            if (pb == null || pb.Count == 0) return false;
            var usedB = new HashSet<int>(pb);
            routes[0] = MakeRoute(A, pf, usedB);
            routes[1] = MakeRoute(startB, pb, used);
            if (routes[0] == null || routes[1] == null) return false;
            Offsets(routes[0], routes[1]); Offsets(routes[1], routes[0]);
            return true;
        }

        static Vector3 Flat(Vector3 v) { v.y = 0; return v; }

        /// <summary>Otro extremo de vía a menos de 60 m de 'a' (la vía paralela); si no hay, el mismo 'a'.</summary>
        int Neighbour(List<int> ends, int a, int other)
        {
            var g = world.graph;
            int bestN = a; float bd = 60f * 60f;
            foreach (int n in ends)
            {
                if (n == a || n == other) continue;
                float d = Flat(g.nodes[n] - g.nodes[a]).sqrMagnitude;
                if (d < bd) { bd = d; bestN = n; }
            }
            return bestN;
        }

        /// <summary>Polilínea de la cadena de aristas desde 'startNode'; marca los puntos sobre aristas de la otra ruta.</summary>
        Route MakeRoute(int startNode, List<int> chain, HashSet<int> otherEdges)
        {
            var g = world.graph;
            var pts = new List<Vector3>(); var sh = new List<bool>();
            int cur = startNode;
            foreach (int ei in chain)
            {
                var e = g.edges[ei];
                bool fwd = e.a == cur;
                bool shared = otherEdges.Contains(ei);
                int m = e.pts.Length;
                for (int i = 0; i < m; i++)
                {
                    var p = e.pts[fwd ? i : m - 1 - i];
                    if (pts.Count > 0 && (pts[pts.Count - 1] - p).sqrMagnitude < 0.01f) { if (i > 0) sh[sh.Count - 1] = shared; continue; }
                    pts.Add(p); sh.Add(shared);
                }
                cur = fwd ? e.b : e.a;
            }
            var cum = new List<float> { 0f };
            for (int i = 1; i < pts.Count; i++) cum.Add(cum[i - 1] + Vector3.Distance(pts[i - 1], pts[i]));
            var r = new Route { len = cum[cum.Count - 1] };
            if (r.len < 300f + TrainLen) return null;
            int N = Mathf.CeilToInt(r.len / 2f) + 1;
            r.rp = new Vector3[N]; r.off = new float[N];
            int j = 0;
            for (int i = 0; i < N; i++)
            {
                float s = Mathf.Min(r.len, i * 2f);
                while (j < cum.Count - 2 && cum[j + 1] < s) j++;
                float t = cum[j + 1] > cum[j] ? (s - cum[j]) / (cum[j + 1] - cum[j]) : 0f;
                r.rp[i] = Vector3.Lerp(pts[j], pts[j + 1], t);
                r.off[i] = sh[j + 1] ? 1f : 0f;          // de momento: 1 = compartido (el signo se pone en Offsets)
            }
            r.hiddenStart = NearBorder(r.rp[0]); r.hiddenEnd = NearBorder(r.rp[N - 1]);
            return r;
        }

        /// <summary>Lado de cada ruta en los tramos compartidos: el contrario a donde queda la otra vía cuando van
        /// en paralelo (así casa con las dos líneas de b5m); si nunca van en paralelo, a la derecha. Rampa de ~60 m.</summary>
        void Offsets(Route r, Route o)
        {
            float acc = 0f;
            for (int i = 0; i < r.rp.Length; i += 5)
            {
                if (r.off[i] > 0.5f) continue;
                Vector3 d = Flat(r.rp[Mathf.Min(i + 1, r.rp.Length - 1)] - r.rp[Mathf.Max(i - 1, 0)]);
                if (d.sqrMagnitude < 1e-4f) continue;
                Vector3 q = NearestPoint(o, r.rp[i]);
                Vector3 to = Flat(q - r.rp[i]);
                if (to.sqrMagnitude > 8f * 8f || to.sqrMagnitude < 1f) continue;
                acc += Mathf.Sign(d.x * to.z - d.z * to.x);           // >0: la otra vía a la izquierda
            }
            float side = acc > 0f ? 1f : (acc < 0f ? -1f : 1f);        // +1 = apartarse a la derecha
            float half = WorldBuilder.TrackSpacing * 0.5f;
            int n = r.rp.Length, W = 15;                                // 15 muestras = 30 m a cada lado
            var raw = new float[n];
            for (int i = 0; i < n; i++) raw[i] = r.off[i] > 0.5f ? side * half : 0f;
            for (int i = 0; i < n; i++)
            {
                float sum = 0; int c = 0;
                for (int k = Mathf.Max(0, i - W); k <= Mathf.Min(n - 1, i + W); k++) { sum += raw[k]; c++; }
                r.off[i] = sum / c;
            }
        }

        static Vector3 NearestPoint(Route o, Vector3 p)
        {
            float best = float.MaxValue; Vector3 q = o.rp[0];
            for (int i = 0; i < o.rp.Length; i++)
            {
                float d = (o.rp[i].x - p.x) * (o.rp[i].x - p.x) + (o.rp[i].z - p.z) * (o.rp[i].z - p.z);
                if (d < best) { best = d; q = o.rp[i]; }
            }
            return q;
        }

        bool NearBorder(Vector3 p)
        {
            return p.x < 200f || p.z < 200f || p.x > world.sizeX - 200f || p.z > world.sizeZ - 200f;
        }

        /// <summary>Camino más corto de 'src' a 'dst' (aristas). Las de 'avoid' cuestan 10 veces más. null si no hay.</summary>
        List<int> Dijkstra(Dictionary<int, List<int>> adj, int src, int dst, HashSet<int> avoid)
        {
            var g = world.graph;
            var dist = new Dictionary<int, float> { [src] = 0f };
            var prev = new Dictionary<int, int>();
            var open = new SortedSet<(float, int)> { (0f, src) };
            while (open.Count > 0)
            {
                var top = open.Min; open.Remove(top);
                int u = top.Item2;
                if (u == dst) break;
                foreach (int ei in adj[u])
                {
                    var e = g.edges[ei];
                    int v = e.a == u ? e.b : e.a;
                    float nd = top.Item1 + e.length * (avoid != null && avoid.Contains(ei) ? 10f : 1f);
                    float od;
                    if (dist.TryGetValue(v, out od) && od <= nd) continue;
                    if (dist.ContainsKey(v)) open.Remove((od, v));
                    dist[v] = nd; prev[v] = ei;
                    open.Add((nd, v));
                }
            }
            if (!dist.ContainsKey(dst) || src == dst) return null;
            var path = new List<int>();
            int n = dst;
            while (n != src) { int ei = prev[n]; path.Add(ei); var e = g.edges[ei]; n = e.a == n ? e.b : e.a; }
            path.Reverse();
            return path;
        }

        int FarthestNode(Dictionary<int, List<int>> adj, int src)
        {
            var g = world.graph;
            var dist = new Dictionary<int, float> { [src] = 0f };
            var open = new SortedSet<(float, int)> { (0f, src) };
            int far = src;
            while (open.Count > 0)
            {
                var top = open.Min; open.Remove(top);
                int u = top.Item2;
                if (top.Item1 > dist[far]) far = u;
                foreach (int ei in adj[u])
                {
                    var e = g.edges[ei];
                    int v = e.a == u ? e.b : e.a;
                    float nd = top.Item1 + e.length, od;
                    if (dist.TryGetValue(v, out od) && od <= nd) continue;
                    if (dist.ContainsKey(v)) open.Remove((od, v));
                    dist[v] = nd; open.Add((nd, v));
                }
            }
            return far;
        }

        // ------------------------------------------------------------------ paradas
        void BuildStops(Route r)
        {
            var st = world.data.scenery.stations;
            if (st == null) return;
            var found = new List<KeyValuePair<float, string>>();
            foreach (var s in st)
            {
                float best = float.MaxValue, bs = 0;
                for (int i = 0; i < r.rp.Length; i++)
                {
                    float d = (r.rp[i].x - s.x) * (r.rp[i].x - s.x) + (r.rp[i].z - s.z) * (r.rp[i].z - s.z);
                    if (d < best) { best = d; bs = i * 2f; }
                }
                if (best > 120f * 120f) continue;                    // estación de otra línea
                bs = Mathf.Clamp(Mathf.Min(bs, r.len), 60f, r.len - 60f);
                found.Add(new KeyValuePair<float, string>(bs, s.name));
            }
            found.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var kv in found)
            {
                if (r.stops.Count > 0 && kv.Key - r.stops[r.stops.Count - 1] < 250f) continue;
                r.stops.Add(kv.Key); r.names.Add(kv.Value);
            }
        }

        // ------------------------------------------------------------------ horario (ida y vuelta)
        void BuildTimetable()
        {
            float t = 0;
            for (int ri = 0; ri < 2; ri++)
            {
                var r = routes[ri];
                float Lp = r.len - TrainLen;                          // recorrido del extremo trasero del tren
                AddPhase(ref t, Layover, 0f, 0f, 0f, ri, r.hiddenStart);
                float s = 0;
                foreach (float st in r.stops)
                {
                    float target = Mathf.Clamp(st - TrainLen * 0.5f, 0f, Lp);   // el centro del tren en la estación
                    if (target <= s + 0.5f) continue;
                    AddRun(ref t, s, target, ri); s = target;
                    AddPhase(ref t, Dwell, s, 0f, 0f, ri, false);
                }
                AddRun(ref t, s, Lp, ri);
                // la espera del final se hace al principio de la ruta siguiente (escondido si es el borde del mapa)
                if (!r.hiddenEnd) AddPhase(ref t, Dwell, Lp, 0f, 0f, ri, false);
            }
            cycleLen = t;
        }

        void AddPhase(ref float t, float dur, float s0, float v0, float acc, int route, bool hidden)
        {
            cycle.Add(new Phase { t0 = t, dur = dur, s0 = s0, v0 = v0, acc = acc, route = route, hidden = hidden }); t += dur;
        }
        /// <summary>Tramo entre dos paradas: acelera, va a VMax y frena (o triángulo si es corto).</summary>
        void AddRun(ref float t, float from, float to, int route)
        {
            float D = to - from;
            if (D < 0.5f) return;
            float da = VMax * VMax / (2f * Accel), db = VMax * VMax / (2f * Decel);
            float v = VMax;
            if (da + db > D) { v = Mathf.Sqrt(2f * D * Accel * Decel / (Accel + Decel)); da = v * v / (2f * Accel); db = v * v / (2f * Decel); }
            float ta = v / Accel, tb = v / Decel, tc = (D - da - db) / v;
            AddPhase(ref t, ta, from, 0f, Accel, route, false);
            if (tc > 0.01f) AddPhase(ref t, tc, from + da, v, 0f, route, false);
            AddPhase(ref t, tb, to - db, v, -Decel, route, false);
        }

        /// <summary>Ruta, posición de la cabeza del tren en ella y si está escondido, a la hora 'time'.</summary>
        void Eval(float time, out Route r, out float head, out bool hidden)
        {
            float tau = Mathf.Repeat(time, cycleLen);
            int lo = 0, hi = cycle.Count - 1;
            while (lo < hi) { int mid = (lo + hi + 1) / 2; if (cycle[mid].t0 <= tau) lo = mid; else hi = mid - 1; }
            var ph = cycle[lo];
            r = routes[ph.route];
            float x = Mathf.Clamp(tau - ph.t0, 0f, ph.dur);
            float p = Mathf.Clamp(ph.s0 + ph.v0 * x + 0.5f * ph.acc * x * x, 0f, Mathf.Max(0f, r.len - TrainLen));
            head = p + TrainLen; hidden = ph.hidden;
        }

        /// <summary>Punto de la vía de la ruta en s (con el desplazamiento lateral ya aplicado).</summary>
        static Vector3 At(Route r, float s)
        {
            float f = Mathf.Clamp(s, 0f, r.len) / 2f;
            int i = Mathf.Min(r.rp.Length - 2, Mathf.FloorToInt(f));
            float t = Mathf.Clamp01(f - i);
            Vector3 p = Vector3.Lerp(r.rp[i], r.rp[i + 1], t);
            float off = Mathf.Lerp(r.off[i], r.off[i + 1], t);
            if (off != 0f)
            {
                Vector3 d = Flat(r.rp[i + 1] - r.rp[i]);
                if (d.sqrMagnitude > 1e-6f) { d.Normalize(); p += new Vector3(d.z, 0, -d.x) * off; }
            }
            return p;
        }

        // ------------------------------------------------------------------ andenes
        void BuildPlatforms()
        {
            var mb = new MeshBuilder(2);
            float gap = 1.45f + 1.6f;                         // del eje de la vía al eje del andén
            int count = 0;
            for (int ri = 0; ri < 2; ri++)
            {
                Route r = routes[ri], o = routes[1 - ri];
                foreach (float s0 in r.stops)
                {
                    // lado exterior: contrario a la otra vía si va en paralelo; si no, a la derecha
                    Vector3 p0 = At(r, s0), d0 = Flat(At(r, s0 + 2f) - At(r, s0 - 2f));
                    Vector3 to = Flat(NearestPoint(o, p0) - p0);
                    int k = 1;
                    if (to.sqrMagnitude < 9f * 9f && to.sqrMagnitude > 0.25f) k = (d0.x * to.z - d0.z * to.x) > 0f ? 1 : -1;
                    else if (Mathf.Abs(r.off[Mathf.Clamp(Mathf.RoundToInt(s0 / 2f), 0, r.off.Length - 1)]) > 0.5f)
                        k = r.off[Mathf.Clamp(Mathf.RoundToInt(s0 / 2f), 0, r.off.Length - 1)] > 0f ? 1 : -1;
                    var path = new List<Vector3>();
                    for (float s = s0 - 50f; s <= s0 + 50f + 0.1f; s += 4f)
                    {
                        Vector3 p = At(r, s), q = At(r, s + 1f);
                        Vector3 d = q - p; d.y = 0; d.Normalize();
                        Vector3 rt = new Vector3(d.z, 0, -d.x);
                        Vector3 c = p + rt * (k * gap);
                        c.y = Mathf.Max(world.SurfaceHeight(c, p.y), p.y);
                        path.Add(c);
                    }
                    // perfil: caja de 3 m x 0,9 m (borde amarillo hacia la vía)
                    float inner = -k * 1.5f, outer = k * 1.5f;
                    float a = Mathf.Min(inner, outer), b = Mathf.Max(inner, outer);
                    mb.Extrude(0, path, new[] { new Vector2(a, -0.4f), new Vector2(a, 0.9f), new Vector2(b, 0.9f), new Vector2(b, -0.4f) }, 0.25f);
                    float e0 = k < 0 ? b - 0.4f : a, e1 = k < 0 ? b : a + 0.4f;
                    mb.Extrude(1, path, new[] { new Vector2(e0, 0.905f), new Vector2(e1, 0.905f) }, 0.25f, true);
                    count++;
                }
            }
            if (count == 0) return;
            var concrete = Mat.Color(new Color(0.66f, 0.65f, 0.62f), 0.2f);
            var white = Mat.Color(new Color(0.95f, 0.92f, 0.3f), 0.3f);
            MeshBuilder.Spawn("Andenes", mb.Build("platforms"), new[] { concrete, white }, transform, true);
        }

        // ------------------------------------------------------------------ trenes
        void SpawnTrains()
        {
            int n = Mathf.Clamp(Mathf.RoundToInt(routes[0].len / 4000f), 1, 8);
            var rnd = new System.Random(4242);        // misma semilla en todos los PCs (online)
            var white = Mat.Color(new Color(0.93f, 0.93f, 0.94f), 0.5f);
            var red = Mat.Color(new Color(0.78f, 0.06f, 0.12f), 0.5f);
            var glass = Mat.Color(new Color(0.08f, 0.10f, 0.13f), 0.85f);
            var dark = Mat.Color(new Color(0.16f, 0.16f, 0.17f), 0.3f);
            var mats = new[] { white, red, glass, dark };
            Mesh head = CarMesh(true), mid = CarMesh(false);
            for (int k = 0; k < n; k++)
            {
                var tr = new Train { phase = cycleLen * (k + (float)rnd.NextDouble() * 0.5f) / n, cars = new Transform[CarsPerTrain], bodies = new Rigidbody[CarsPerTrain] };
                for (int c = 0; c < CarsPerTrain; c++)
                {
                    bool cab = c == 0 || c == CarsPerTrain - 1;
                    var go = MeshBuilder.Spawn("Tren" + k + "_coche" + c, cab ? head : mid, mats, transform, false);
                    go.isStatic = false;
                    var bc = go.AddComponent<BoxCollider>();
                    bc.center = new Vector3(0, CarH * 0.5f + 0.3f, 0); bc.size = new Vector3(CarW, CarH, CarLen);
                    var rb = go.AddComponent<Rigidbody>();
                    rb.isKinematic = true; rb.interpolation = RigidbodyInterpolation.Interpolate;
                    tr.cars[c] = go.transform; tr.bodies[c] = rb;
                }
                trains.Add(tr);
            }
            Place(true);
        }

        /// <summary>Coche de Cercanías low-poly: caja blanca, franja roja, ventanas, techo y bogies. Mira a +Z.</summary>
        static Mesh CarMesh(bool cab)
        {
            var mb = new MeshBuilder(4);
            float hl = CarLen * 0.5f, hw = CarW * 0.5f, y0 = 0.9f;
            Vector3 R = Vector3.right, U = Vector3.up, F = Vector3.forward;
            mb.Box(0, new Vector3(0, y0 + (CarH - 0.6f) * 0.5f, 0), R, U, F, new Vector3(hw, (CarH - 0.6f) * 0.5f, hl - (cab ? 1.2f : 0f)));
            if (cab)   // morro inclinado hacia +Z
            {
                float z0 = hl - 1.2f;
                mb.Box(0, new Vector3(0, y0 + 0.8f, z0 + 0.6f), R, U, F, new Vector3(hw, 0.8f, 0.6f));
                mb.Quad(2, new Vector3(-hw, y0 + 1.6f, hl), new Vector3(hw, y0 + 1.6f, hl), new Vector3(hw - 0.15f, y0 + CarH - 0.6f, z0), new Vector3(-hw + 0.15f, y0 + CarH - 0.6f, z0),
                        new Vector3(0, 0.6f, 1f), Vector2.zero, Vector2.right, Vector2.one, Vector2.up);
                mb.Box(1, new Vector3(0, y0 + 0.45f, hl - 0.02f), R, U, F, new Vector3(hw * 0.9f, 0.25f, 0.05f));
            }
            // franja roja y ventanas a los lados
            for (int s = -1; s <= 1; s += 2)
            {
                float x = s * (hw + 0.01f);
                mb.Box(1, new Vector3(x, y0 + 0.55f, 0), R, U, F, new Vector3(0.012f, 0.18f, hl - (cab ? 1.3f : 0.1f)));
                for (float z = -hl + 2.2f; z < hl - 2.2f - (cab ? 1.2f : 0f); z += 3.1f)
                    mb.Box(2, new Vector3(x, y0 + 1.75f, z + 1.1f), R, U, F, new Vector3(0.015f, 0.55f, 1.0f));
            }
            mb.Box(3, new Vector3(0, y0 + CarH - 0.55f, 0), R, U, F, new Vector3(hw * 0.8f, 0.12f, hl * 0.85f));   // techo/equipos
            mb.Box(3, new Vector3(0, 0.55f, hl - 3.5f), R, U, F, new Vector3(hw * 0.75f, 0.35f, 1.3f));            // bogies
            mb.Box(3, new Vector3(0, 0.55f, -hl + 3.5f), R, U, F, new Vector3(hw * 0.75f, 0.35f, 1.3f));
            return mb.Build(cab ? "train_cab" : "train_mid");
        }

        void Place(bool snap)
        {
            float time = NetClock.Time;
            foreach (var tr in trains)
            {
                Route r; float head; bool hidden;
                Eval(time + tr.phase, out r, out head, out hidden);
                if (tr.visible == hidden)
                {
                    tr.visible = !hidden;
                    foreach (var c in tr.cars) c.gameObject.SetActive(!hidden);
                }
                if (hidden) continue;
                for (int c = 0; c < tr.cars.Length; c++)
                {
                    float sc = head - (c * (CarLen + CarGap) + CarLen * 0.5f);
                    Vector3 front = At(r, sc + (CarLen * 0.5f - 3.5f)), back = At(r, sc - (CarLen * 0.5f - 3.5f));
                    Vector3 fw = front - back;
                    if (fw.sqrMagnitude < 1e-4f) fw = Vector3.forward;
                    Vector3 pos = (front + back) * 0.5f + Vector3.up * 0.2f;
                    // la cabina trasera mira hacia atrás (morro en los dos extremos del tren)
                    bool rearCab = c == tr.cars.Length - 1 && tr.cars.Length > 1;
                    Quaternion rot = Quaternion.LookRotation(rearCab ? -fw : fw, Vector3.up);
                    if (snap) { tr.cars[c].SetPositionAndRotation(pos, rot); }
                    else { tr.bodies[c].MovePosition(pos); tr.bodies[c].MoveRotation(rot); }
                }
            }
        }

        void FixedUpdate() { if (trains.Count > 0) Place(false); }

        /// <summary>Posiciones y sentido de las cabezas (para el plano).</summary>
        public void Heads(List<KeyValuePair<Vector3, Vector3>> outList)
        {
            outList.Clear();
            foreach (var tr in trains)
                if (tr.visible && tr.cars[0] != null) outList.Add(new KeyValuePair<Vector3, Vector3>(tr.cars[0].position, tr.cars[0].forward));
        }
    }
}
