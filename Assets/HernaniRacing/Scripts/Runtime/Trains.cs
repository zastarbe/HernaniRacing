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

        World world;
        // ruta: polilínea de la línea principal (la cadena de vía más larga)
        Vector3[] rp; float[] rs; float routeLen;
        bool doubleTrack;
        readonly List<float> stops = new List<float>();
        public readonly List<string> stopNames = new List<string>();
        bool hiddenStart, hiddenEnd;

        struct Phase { public float t0, dur, s0, v0, acc; public bool hidden; }
        readonly List<Phase> cycle = new List<Phase>();   // ida + vuelta (s siempre medido desde el inicio de la ruta)
        readonly List<int> cycleDir = new List<int>();
        float cycleLen;

        class Train { public float phase; public Transform[] cars; public Rigidbody[] bodies; public bool visible = true; }
        readonly List<Train> trains = new List<Train>();
        public int Count { get { return trains.Count; } }
        public float RouteLength { get { return routeLen; } }

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
                if (!t.BuildRoute()) { Destroy(go); return null; }
                t.BuildStops();
                t.BuildTimetable();
                t.BuildPlatforms();
                t.SpawnTrains();
                Debug.Log("Trenes: " + t.trains.Count + " en " + (t.routeLen / 1000f).ToString("F1") + " km de vía, paradas: " + string.Join(", ", t.stopNames));
            }
            catch (System.Exception ex) { Debug.LogWarning("Trenes desactivados: " + ex.Message); Destroy(go); return null; }
            return t;
        }

        // ------------------------------------------------------------------ ruta
        bool BuildRoute()
        {
            var g = world.graph;
            // subgrafo de vía: nodo -> lista de (arista, otro nodo)
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
            // diámetro aproximado (doble Dijkstra) = línea principal
            int start = -1;
            foreach (var k in adj.Keys) { start = k; break; }
            int far1; Dictionary<int, int> prev;
            Farthest(adj, start, out far1, out prev);
            int far2;
            Farthest(adj, far1, out far2, out prev);
            // reconstruir far1 -> far2
            var chain = new List<int>();
            int n = far2;
            while (n != far1 && prev.ContainsKey(n)) { int ei = prev[n]; chain.Add(ei); var e = g.edges[ei]; n = e.a == n ? e.b : e.a; }
            chain.Reverse();
            if (chain.Count == 0) return false;
            var pts = new List<Vector3>();
            int cur = far1;
            float wsum = 0, lsum = 0;
            foreach (int ei in chain)
            {
                var e = g.edges[ei];
                bool fwd = e.a == cur;
                int m = e.pts.Length;
                for (int i = 0; i < m; i++)
                {
                    var p = e.pts[fwd ? i : m - 1 - i];
                    if (pts.Count > 0 && (pts[pts.Count - 1] - p).sqrMagnitude < 0.01f) continue;
                    pts.Add(p);
                }
                cur = fwd ? e.b : e.a;
                wsum += e.width * e.length; lsum += e.length;
            }
            doubleTrack = lsum > 0 && wsum / lsum >= 7f;
            // remuestreo cada 2 m (posiciones suaves)
            var cum = new List<float> { 0f };
            for (int i = 1; i < pts.Count; i++) cum.Add(cum[i - 1] + Vector3.Distance(pts[i - 1], pts[i]));
            routeLen = cum[cum.Count - 1];
            if (routeLen < 300f) return false;
            int N = Mathf.CeilToInt(routeLen / 2f) + 1;
            rp = new Vector3[N]; rs = new float[N];
            int j = 0;
            for (int i = 0; i < N; i++)
            {
                float s = Mathf.Min(routeLen, i * 2f);
                while (j < cum.Count - 2 && cum[j + 1] < s) j++;
                float t = cum[j + 1] > cum[j] ? (s - cum[j]) / (cum[j + 1] - cum[j]) : 0f;
                rp[i] = Vector3.Lerp(pts[j], pts[j + 1], t); rs[i] = s;
            }
            // extremos en el borde del mapa: el tren "sale" del mapa y vuelve (escondido mientras tanto)
            hiddenStart = NearBorder(rp[0]); hiddenEnd = NearBorder(rp[N - 1]);
            return true;
        }

        bool NearBorder(Vector3 p)
        {
            return p.x < 200f || p.z < 200f || p.x > world.sizeX - 200f || p.z > world.sizeZ - 200f;
        }

        void Farthest(Dictionary<int, List<int>> adj, int src, out int far, out Dictionary<int, int> prev)
        {
            var g = world.graph;
            var dist = new Dictionary<int, float> { [src] = 0f };
            prev = new Dictionary<int, int>();
            var open = new SortedSet<(float, int)> { (0f, src) };
            far = src;
            while (open.Count > 0)
            {
                var top = open.Min; open.Remove(top);
                int u = top.Item2;
                if (top.Item1 > dist[u]) continue;
                if (top.Item1 > dist[far]) far = u;
                foreach (int ei in adj[u])
                {
                    var e = g.edges[ei];
                    int v = e.a == u ? e.b : e.a;
                    float nd = top.Item1 + e.length;
                    float od;
                    if (dist.TryGetValue(v, out od) && od <= nd) continue;
                    if (dist.ContainsKey(v)) open.Remove((od, v));
                    dist[v] = nd; prev[v] = ei;
                    open.Add((nd, v));
                }
            }
        }

        // ------------------------------------------------------------------ paradas
        void BuildStops()
        {
            var st = world.data.scenery.stations;
            if (st == null) return;
            var found = new List<KeyValuePair<float, string>>();
            foreach (var s in st)
            {
                float best = float.MaxValue, bs = 0;
                for (int i = 0; i < rp.Length; i++)
                {
                    float d = (rp[i].x - s.x) * (rp[i].x - s.x) + (rp[i].z - s.z) * (rp[i].z - s.z);
                    if (d < best) { best = d; bs = rs[i]; }
                }
                if (best > 120f * 120f) continue;                    // estación de otra línea
                bs = Mathf.Clamp(bs, 60f, routeLen - 60f);
                found.Add(new KeyValuePair<float, string>(bs, s.name));
            }
            found.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var kv in found)
            {
                if (stops.Count > 0 && kv.Key - stops[stops.Count - 1] < 250f) continue;
                stops.Add(kv.Key); stopNames.Add(kv.Value);
            }
        }

        // ------------------------------------------------------------------ horario (ida y vuelta)
        void BuildTimetable()
        {
            float t = 0;
            // ida: 0 -> L
            AddLayover(ref t, 0f, +1, hiddenStart);
            var fw = new List<float>(stops); fw.Add(routeLen);
            float s = 0;
            foreach (float target in fw) { AddRun(ref t, s, target, +1); s = target; if (target < routeLen) AddDwell(ref t, s, +1); }
            AddLayover(ref t, routeLen, -1, hiddenEnd);
            // vuelta: L -> 0
            var bw = new List<float>();
            for (int i = stops.Count - 1; i >= 0; i--) bw.Add(stops[i]);
            bw.Add(0f);
            s = routeLen;
            foreach (float target in bw) { AddRun(ref t, s, target, -1); s = target; if (target > 0f) AddDwell(ref t, s, -1); }
            cycleLen = t;
        }

        void AddLayover(ref float t, float s, int dir, bool hidden)
        {
            cycle.Add(new Phase { t0 = t, dur = Layover, s0 = s, v0 = 0, acc = 0, hidden = hidden }); cycleDir.Add(dir); t += Layover;
        }
        void AddDwell(ref float t, float s, int dir)
        {
            cycle.Add(new Phase { t0 = t, dur = Dwell, s0 = s, v0 = 0, acc = 0 }); cycleDir.Add(dir); t += Dwell;
        }
        /// <summary>Tramo entre dos paradas: acelera, va a VMax y frena (o triángulo si es corto).</summary>
        void AddRun(ref float t, float from, float to, int dir)
        {
            float D = Mathf.Abs(to - from);
            if (D < 0.5f) return;
            float da = VMax * VMax / (2f * Accel), db = VMax * VMax / (2f * Decel);
            float v = VMax;
            if (da + db > D) { v = Mathf.Sqrt(2f * D * Accel * Decel / (Accel + Decel)); da = v * v / (2f * Accel); db = v * v / (2f * Decel); }
            float ta = v / Accel, tb = v / Decel, tc = (D - da - db) / v;
            float sgn = dir;
            cycle.Add(new Phase { t0 = t, dur = ta, s0 = from, v0 = 0, acc = Accel * sgn }); cycleDir.Add(dir); t += ta;
            if (tc > 0.01f) { cycle.Add(new Phase { t0 = t, dur = tc, s0 = from + sgn * da, v0 = v * sgn, acc = 0 }); cycleDir.Add(dir); t += tc; }
            cycle.Add(new Phase { t0 = t, dur = tb, s0 = to - sgn * db, v0 = v * sgn, acc = -Decel * sgn }); cycleDir.Add(dir); t += tb;
        }

        /// <summary>Posición de la cabeza del tren (s en la ruta), sentido y si está escondido, a la hora 'time'.</summary>
        void Eval(float time, out float s, out int dir, out bool hidden)
        {
            float tau = Mathf.Repeat(time, cycleLen);
            int lo = 0, hi = cycle.Count - 1;
            while (lo < hi) { int mid = (lo + hi + 1) / 2; if (cycle[mid].t0 <= tau) lo = mid; else hi = mid - 1; }
            var ph = cycle[lo];
            float x = Mathf.Clamp(tau - ph.t0, 0f, ph.dur);
            s = Mathf.Clamp(ph.s0 + ph.v0 * x + 0.5f * ph.acc * x * x, 0f, routeLen);
            dir = cycleDir[lo]; hidden = ph.hidden;
        }

        Vector3 At(float s)
        {
            float f = Mathf.Clamp(s, 0f, routeLen) / 2f;
            int i = Mathf.Min(rp.Length - 2, Mathf.FloorToInt(f));
            return Vector3.Lerp(rp[i], rp[i + 1], Mathf.Clamp01(f - i));
        }

        // ------------------------------------------------------------------ andenes
        void BuildPlatforms()
        {
            if (stops.Count == 0) return;
            var mb = new MeshBuilder(2);
            float side = (doubleTrack ? WorldBuilder.TrackSpacing * 0.5f : 0f) + 1.45f + 1.6f;
            foreach (float s0 in stops)
            {
                for (int k = -1; k <= 1; k += 2)
                {
                    var path = new List<Vector3>();
                    for (float s = s0 - 50f; s <= s0 + 50f + 0.1f; s += 4f)
                    {
                        Vector3 p = At(s), q = At(s + 1f);
                        Vector3 d = q - p; d.y = 0; d.Normalize();
                        Vector3 r = new Vector3(d.z, 0, -d.x);
                        Vector3 c = p + r * (k * side);
                        c.y = Mathf.Max(world.SurfaceHeight(c, p.y), p.y);
                        path.Add(c);
                    }
                    // perfil: caja de 3 m x 0,9 m (borde blanco hacia la vía)
                    float inner = -k * 1.5f, outer = k * 1.5f;
                    float a = Mathf.Min(inner, outer), b = Mathf.Max(inner, outer);
                    mb.Extrude(0, path, new[] { new Vector2(a, -0.4f), new Vector2(a, 0.9f), new Vector2(b, 0.9f), new Vector2(b, -0.4f) }, 0.25f);
                    float e0 = k < 0 ? b - 0.4f : a, e1 = k < 0 ? b : a + 0.4f;
                    mb.Extrude(1, path, new[] { new Vector2(e0, 0.905f), new Vector2(e1, 0.905f) }, 0.25f, true);
                }
            }
            var concrete = Mat.Color(new Color(0.66f, 0.65f, 0.62f), 0.2f);
            var white = Mat.Color(new Color(0.95f, 0.92f, 0.3f), 0.3f);
            MeshBuilder.Spawn("Andenes", mb.Build("platforms"), new[] { concrete, white }, transform, true);
        }

        // ------------------------------------------------------------------ trenes
        void SpawnTrains()
        {
            int n = Mathf.Clamp(Mathf.RoundToInt(routeLen / 4000f), 1, 8);
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
            float side = doubleTrack ? WorldBuilder.TrackSpacing * 0.5f : 0f;
            foreach (var tr in trains)
            {
                float s; int dir; bool hidden;
                Eval(time + tr.phase, out s, out dir, out hidden);
                if (tr.visible == hidden)
                {
                    tr.visible = !hidden;
                    foreach (var c in tr.cars) c.gameObject.SetActive(!hidden);
                }
                if (hidden) continue;
                for (int c = 0; c < tr.cars.Length; c++)
                {
                    float sc = s - dir * (c * (CarLen + CarGap) + CarLen * 0.5f);
                    Vector3 front = At(sc + dir * (CarLen * 0.5f - 3.5f)), back = At(sc - dir * (CarLen * 0.5f - 3.5f));
                    Vector3 fw = front - back;
                    if (fw.sqrMagnitude < 1e-4f) fw = Vector3.forward;
                    Vector3 flat = new Vector3(fw.x, 0, fw.z).normalized;
                    Vector3 right = new Vector3(flat.z, 0, -flat.x);
                    Vector3 pos = (front + back) * 0.5f + right * side + Vector3.up * 0.2f;
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
