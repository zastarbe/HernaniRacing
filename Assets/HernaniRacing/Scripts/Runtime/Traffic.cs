using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Tráfico y peatones alrededor del jugador. Los coches circulan por su carril (por la derecha), respetan
    /// los sentidos únicos de OSM, los semáforos, los STOP (parada completa), los CEDA EL PASO, la prioridad de
    /// las rotondas y de la derecha, y los pasos de cebra; siguen al de delante (modelo IDM) y frenan ante ti.
    /// Si los golpeas fuerte pasan a ser físicos (se les puede empujar) y desaparecen al rato.
    /// </summary>
    public class TrafficSystem : MonoBehaviour
    {
        public static TrafficSystem Active;
        public World world;
        public Transform player;
        public readonly List<CarController> drivers = new List<CarController>();   // jugador y rivales (obstáculos)
        public bool carsOn = true, pedestriansOn = true;
        public int maxCars = 24, maxPeds = 40;
        public bool online;                 // online: límite más bajo (cada jugador simula el tráfico de su zona)
        int carLevel = 2, pedLevel = 2;
        bool realTime;
        float realFactorCars = 1f, realFactorPeds = 1f, nextRealUpdate;

        // ------------------------------------------------------------------ densidad del tráfico
        public static readonly string[] LevelNames = { "No", "Poco", "Normal", "Mucho", "Muchísimo", "A tope" };
        public static readonly int[] CarsByLevel = { 0, 12, 24, 45, 70, 100 };
        public static readonly int[] PedsByLevel = { 0, 15, 40, 70, 100, 140 };
        public const int OnlineMaxCars = 60, OnlineMaxPeds = 80;

        /// <summary>
        /// Vehículos/hora medios de la A-15 en Hernani (aforador 259-ETD, Diputación Foral de Gipuzkoa, mediana de 2023,
        /// datos abiertos de Open Data Euskadi), por día de la semana (lunes..domingo) y hora (0..23). Media: 1810.
        /// No hay datos de tráfico en tiempo real abiertos y gratuitos, así que se usa el perfil típico de cada hora.
        /// </summary>
        static readonly int[] A15 =
        {
            333, 292, 175, 170, 215, 285, 1034, 1662, 3603, 3566, 3015, 2683, 2587, 2738, 3005, 3335, 3270, 3412, 3545, 3159, 2371, 1670, 1223, 952,
            370, 191, 130, 131, 166, 289, 1042, 1762, 3625, 3666, 3088, 2641, 2749, 2662, 3055, 3405, 3395, 3419, 3487, 3158, 2379, 1662, 1293, 972,
            427, 218, 161, 147, 166, 280, 1032, 1717, 3602, 3644, 3108, 2607, 2569, 2736, 3163, 3431, 3337, 3496, 3524, 3291, 2315, 1800, 1260, 1001,
            472, 230, 152, 145, 164, 265, 1018, 1641, 3600, 3595, 3069, 2587, 2631, 2658, 3041, 3271, 3243, 3351, 3382, 3076, 2350, 1815, 1272, 996,
            600, 271, 158, 162, 172, 285, 1030, 1831, 3334, 3391, 2949, 2642, 2745, 2853, 3319, 3942, 3404, 3304, 3336, 3056, 2606, 2012, 1416, 1096,
            718, 480, 271, 212, 193, 201, 400, 619, 938, 1310, 1844, 2127, 2354, 2606, 2596, 1985, 1638, 1785, 2122, 2238, 2164, 1886, 1356, 948,
            489, 503, 355, 266, 182, 180, 245, 324, 436, 703, 1019, 1371, 1750, 1852, 1982, 1493, 1336, 1645, 2006, 2388, 2453, 1978, 1336, 868
        };
        const float A15Mean = 1810f;

        public static float A15At(System.DateTime t)
        {
            int d = ((int)t.DayOfWeek + 6) % 7;                 // lunes = 0
            int h = t.Hour, h2 = (h + 1) % 24;
            int d2 = h2 == 0 ? (d + 1) % 7 : d;
            float f = t.Minute / 60f;
            return Mathf.Lerp(A15[d * 24 + h], A15[d2 * 24 + h2], f);
        }

        /// <summary>Multiplicador de coches según la hora real (1 = media del día). Peatones: más suave.</summary>
        public static float RealTimeFactor(System.DateTime t, bool peds = false)
        {
            float r = A15At(t) / A15Mean;
            return peds ? Mathf.Clamp(Mathf.Pow(r, 0.5f), 0.25f, 1.5f) : Mathf.Clamp(Mathf.Pow(r, 0.8f), 0.15f, 2f);
        }

        static readonly string[] DayNames = { "lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo" };

        /// <summary>Texto para el menú: "Ahora (jueves 22:30): poco tráfico (×0,6)".</summary>
        public static string RealTimeLabel(int level)
        {
            var t = System.DateTime.Now;
            float f = RealTimeFactor(t);
            int n = Mathf.RoundToInt(CarsByLevel[Mathf.Clamp(level, 0, 5)] * f);
            string how = f < 0.35f ? "casi vacío" : f < 0.75f ? "poco tráfico" : f < 1.25f ? "tráfico normal" : f < 1.7f ? "mucho tráfico" : "hora punta";
            return "Ahora (" + DayNames[((int)t.DayOfWeek + 6) % 7] + " " + t.ToString("HH:mm") + "): " + how +
                   " · ×" + f.ToString("0.0") + " → unos " + n + " coches";
        }

        /// <summary>Nivel de coches y peatones (0..5) y si se ajusta a la hora real.</summary>
        public void SetDensity(int cars, int peds, bool byRealTime)
        {
            carLevel = Mathf.Clamp(cars, 0, 5); pedLevel = Mathf.Clamp(peds, 0, 5); realTime = byRealTime;
            carsOn = carLevel > 0; pedestriansOn = pedLevel > 0;
            nextRealUpdate = 0f;
            UpdateDensity();
        }

        void UpdateDensity()
        {
            if (Time.unscaledTime < nextRealUpdate) return;
            nextRealUpdate = Time.unscaledTime + 30f;
            var now = System.DateTime.Now;
            realFactorCars = realTime ? RealTimeFactor(now) : 1f;
            realFactorPeds = realTime ? RealTimeFactor(now, true) : 1f;
            int c = Mathf.RoundToInt(CarsByLevel[carLevel] * realFactorCars);
            int p = Mathf.RoundToInt(PedsByLevel[pedLevel] * realFactorPeds);
            if (carLevel > 0) c = Mathf.Max(c, 3);
            if (pedLevel > 0) p = Mathf.Max(p, 4);
            if (online) { c = Mathf.Min(c, OnlineMaxCars); p = Mathf.Min(p, OnlineMaxPeds); }
            maxCars = c; maxPeds = p;
        }

        // tramos cercanos al jugador (para no sortear entre los miles de tramos de todo el mapa)
        Rect[] edgeBox;
        readonly List<int> nearEdges = new List<int>();
        Vector3 nearCenter = new Vector3(1e9f, 0, 1e9f);
        float nearR, nearTime;

        List<int> NearEdges(Vector3 c, float r)
        {
            var g = world.graph;
            if (edgeBox == null || edgeBox.Length != g.edges.Length)
            {
                edgeBox = new Rect[g.edges.Length];
                for (int i = 0; i < g.edges.Length; i++)
                {
                    var pts = g.edges[i].pts;
                    float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
                    foreach (var q in pts) { x0 = Mathf.Min(x0, q.x); z0 = Mathf.Min(z0, q.z); x1 = Mathf.Max(x1, q.x); z1 = Mathf.Max(z1, q.z); }
                    edgeBox[i] = Rect.MinMaxRect(x0, z0, x1, z1);
                }
            }
            if (Flat(c - nearCenter) < 40f && Mathf.Abs(r - nearR) < 1f && Time.time - nearTime < 4f) return nearEdges;
            nearCenter = c; nearR = r; nearTime = Time.time;
            nearEdges.Clear();
            float R = r + 40f;
            for (int i = 0; i < edgeBox.Length; i++)
            {
                var b = edgeBox[i];
                float dx = Mathf.Max(b.xMin - c.x, 0f, c.x - b.xMax), dz = Mathf.Max(b.yMin - c.z, 0f, c.z - b.yMax);
                if (dx * dx + dz * dz < R * R) nearEdges.Add(i);
            }
            return nearEdges;
        }

        /// <summary>Probabilidad de que salga un coche en cada tipo de vía (más en la autovía y carreteras).</summary>
        static float RoadWeight(RoadEdge e)
        {
            if (e.type == "Carretera") return e.lanes >= 2 ? 1f : 0.6f;
            return e.lanes >= 2 ? 0.35f : 0.2f;
        }
        public readonly List<TrafficCar> cars = new List<TrafficCar>();
        public readonly List<Pedestrian> peds = new List<Pedestrian>();
        // online: tráfico/peatones que simulan otros jugadores y coches de los otros jugadores
        public readonly List<TrafficProxy> remoteCars = new List<TrafficProxy>();
        public readonly List<PedProxy> remotePeds = new List<PedProxy>();
        public readonly List<Transform> others = new List<Transform>();
        ushort nextNetId = 1;
        readonly Dictionary<long, int> crossingBusy = new Dictionary<long, int>();
        readonly Dictionary<long, float> crossingWaiting = new Dictionary<long, float>();
        System.Random rnd = new System.Random();
        float nextSpawn;
        List<string> models;
        Transform root;

        static readonly Color[] Paints =
        {
            new Color(0.85f, 0.85f, 0.86f), new Color(0.1f, 0.1f, 0.12f), new Color(0.55f, 0.57f, 0.6f), new Color(0.7f, 0.08f, 0.08f),
            new Color(0.1f, 0.25f, 0.6f), new Color(0.95f, 0.95f, 0.95f), new Color(0.3f, 0.32f, 0.35f), new Color(0.15f, 0.4f, 0.25f),
            new Color(0.8f, 0.6f, 0.2f), new Color(0.45f, 0.15f, 0.12f)
        };

        public static TrafficSystem Create(World w, Transform player, bool cars, bool peds)
        {
            var go = new GameObject("Tráfico y peatones");
            var t = go.AddComponent<TrafficSystem>();
            t.world = w; t.player = player; t.carsOn = cars; t.pedestriansOn = peds;
            t.root = go.transform;
            t.models = CarModels.TrafficIds;
            Active = t;
            return t;
        }

        void OnDestroy()
        {
            if (Active == this) Active = null;
        }

        static long Key(int edge, float s) { return ((long)edge << 20) | (long)Mathf.Clamp(Mathf.RoundToInt(s * 4f), 0, (1 << 20) - 1); }
        public void SetCrossingBusy(int edge, float s, int delta)
        {
            long k = Key(edge, s); int v; crossingBusy.TryGetValue(k, out v);
            crossingBusy[k] = Mathf.Max(0, v + delta);
        }
        public bool CrossingBusy(int edge, float s) { int v; return crossingBusy.TryGetValue(Key(edge, s), out v) && v > 0; }
        public void SetCrossingWaiting(int edge, float s) { crossingWaiting[Key(edge, s)] = Time.time + 0.3f; }
        public bool CrossingWaiting(int edge, float s) { float t; return crossingWaiting.TryGetValue(Key(edge, s), out t) && t > Time.time; }

        void Update()
        {
            if (player == null || world == null) return;
            Vector3 pp = player.position;
            // retirar los lejanos
            for (int i = cars.Count - 1; i >= 0; i--)
            {
                var c = cars[i];
                if (c == null) { cars.RemoveAt(i); continue; }
                float dMine = Flat(c.transform.position - pp);
                float d = NearestPlayer(c.transform.position, dMine);
                bool expired = c.Crashed && Time.time - c.crashTime > 12f && d > 25f;
                if (d > 340f || dMine > 700f || c.WantsDespawn || expired || !carsOn) { Destroy(c.gameObject); cars.RemoveAt(i); }
            }
            for (int i = peds.Count - 1; i >= 0; i--)
            {
                var p = peds[i];
                if (p == null) { peds.RemoveAt(i); continue; }
                float dMine = Flat(p.transform.position - pp);
                if (NearestPlayer(p.transform.position, dMine) > 230f || dMine > 500f || p.WantsDespawn || !pedestriansOn) { p.Release(); Destroy(p.gameObject); peds.RemoveAt(i); }
            }
            UpdateDensity();
            if (Time.time < nextSpawn) return;
            nextSpawn = Time.time + (maxCars > 40 || maxPeds > 70 ? 0.12f : 0.25f);
            // online: los coches y peatones que ya trae otro jugador cerca de mí cuentan para la densidad
            int nCars = cars.Count, nPeds = peds.Count;
            foreach (var rc in remoteCars) if (rc != null && Flat(rc.transform.position - pp) < 260f) nCars++;
            foreach (var rp in remotePeds) if (rp != null && Flat(rp.transform.position - pp) < 170f) nPeds++;
            if (carsOn && nCars < maxCars) TrySpawnCar(pp, nCars < maxCars / 2 ? 45f : 75f, maxCars > 50 ? 280f : 250f);
            if (pedestriansOn && nPeds < maxPeds) TrySpawnPed(pp, 30f, 160f);
        }

        static float Flat(Vector3 v) { v.y = 0; return v.magnitude; }

        /// <summary>Distancia al jugador más cercano (yo u otro jugador online).</summary>
        float NearestPlayer(Vector3 p, float dMine)
        {
            float d = dMine;
            foreach (var o in others) if (o != null) d = Mathf.Min(d, Flat(o.position - p));
            return d;
        }

        /// <summary>No hacer aparecer nada delante de las narices de otro jugador.</summary>
        bool NearOther(Vector3 p, float r)
        {
            foreach (var o in others) if (o != null && Flat(o.position - p) < r) return true;
            return false;
        }

        ushort NewNetId()
        {
            if (nextNetId == 0 || nextNetId == 0xFFFF) nextNetId = 1;
            return nextNetId++;
        }

        void TrySpawnCar(Vector3 pp, float rMin, float rMax)
        {
            var g = world.graph;
            var near = NearEdges(pp, rMax);
            if (near.Count == 0) return;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                int ei = near[rnd.Next(near.Count)];
                var e = g.edges[ei];
                if (e.length < 18f) continue;
                if (rnd.NextDouble() > RoadWeight(e)) continue;
                float s = Mathf.Lerp(8f, e.length - 8f, (float)rnd.NextDouble());
                Vector3 p = e.PointAt(s, true);
                float d = Flat(p - pp);
                if (d < rMin || d > rMax) continue;
                if (Lanes.FlagAt(e, s) == 2) continue;
                bool fwd = rnd.Next(2) == 0;
                if (!e.Allows(fwd)) fwd = !fwd;
                bool clear = true;
                foreach (var c in cars) if (c != null && Flat(c.transform.position - p) < 18f) { clear = false; break; }
                foreach (var dr in drivers) if (dr != null && Flat(dr.transform.position - p) < 25f) { clear = false; break; }
                foreach (var rc in remoteCars) if (rc != null && Flat(rc.transform.position - p) < 18f) { clear = false; break; }
                if (clear && NearOther(p, rMin)) clear = false;
                if (!clear) continue;
                string id = CarModels.PickTraffic(rnd);
                var md = CarModels.Get(id);
                if (md == null) return;
                Color paint = CarModels.FixedLivery(id) ? CarModels.DefaultPaint(id) : Paints[rnd.Next(Paints.Length)];
                var go = CarModels.CreateVisual(id, paint, root);
                var tc = go.AddComponent<TrafficCar>();
                tc.netId = NewNetId();
                tc.modelIndex = models.IndexOf(id);
                tc.paint = paint;
                tc.Init(this, md, ei, fwd, s, rnd.Next());
                cars.Add(tc);
                return;
            }
        }

        void TrySpawnPed(Vector3 pp, float rMin, float rMax)
        {
            var g = world.graph;
            var near = NearEdges(pp, Mathf.Max(rMax, nearR));
            if (near.Count == 0) return;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int ei = near[rnd.Next(near.Count)];
                var e = g.edges[ei];
                float s0, s1;
                if (!Pedestrian.Sidewalk(g, e, out s0, out s1)) continue;
                float s = Mathf.Lerp(s0, s1, (float)rnd.NextDouble());
                Vector3 p = e.PointAt(s, true);
                float d = Flat(p - pp);
                if (d < rMin || d > rMax) continue;
                if (NearOther(p, rMin)) continue;
                var go = new GameObject("Peaton");
                go.transform.SetParent(root, false);
                var ped = go.AddComponent<Pedestrian>();
                ped.netId = NewNetId();
                ped.Init(this, ei, rnd.Next(2) == 0, rnd.Next(2) == 0 ? 1 : -1, s, rnd.Next());
                peds.Add(ped);
                return;
            }
        }

        /// <summary>Vehículos (tráfico + coches de jugadores) para que los peatones se aparten y los coches cedan.</summary>
        public IEnumerable<KeyValuePair<Vector3, Vector3>> Vehicles()
        {
            foreach (var c in cars) if (c != null) yield return new KeyValuePair<Vector3, Vector3>(c.transform.position, c.Velocity);
            foreach (var d in drivers) if (d != null && d.Body != null && d.gameObject.activeInHierarchy) yield return new KeyValuePair<Vector3, Vector3>(d.transform.position, d.Body.Vel());
            foreach (var r in remoteCars) if (r != null) yield return new KeyValuePair<Vector3, Vector3>(r.transform.position, r.Velocity);
        }
    }

    /// <summary>Coche del tráfico: cinemático, sigue su carril por el grafo con las normas de circulación.</summary>
    public class TrafficCar : MonoBehaviour
    {
        class Piece
        {
            public bool turn;
            public int edge, node;          // node: nodo al final del tramo (o del giro)
            public bool fwd;
            public float sStart, length, lane;
            public Vector3 p0, p1, p2;      // giro: curva de Bézier cuadrática
            public float yA, yB;
            public bool flatGround;
        }

        TrafficSystem sys;
        World world;
        RoadGraph g;
        CarModelData model;
        Rigidbody rb;
        readonly List<Piece> route = new List<Piece>();
        float t;                     // distancia recorrida en route[0]
        float v, v0Factor;
        System.Random rnd;
        float halfLen, wheelR;
        Transform[] wheels = new Transform[4];
        float spin;
        float stopTimer, waitTimer;
        bool stoppedAtSign, committed;
        int lastNodeChecked = -1;
        public bool Crashed { get; private set; }
        public float crashTime;
        public ushort netId;            // online: identificador para los demás jugadores
        public int modelIndex;
        public Color paint;
        public bool WantsDespawn { get; private set; }
        public Vector3 Velocity { get { return Crashed && rb != null ? rb.Vel() : transform.forward * v; } }
        public float Speed { get { return v; } }

        // información para que los demás cedan el paso
        public int ApproachNode { get { return route.Count > 0 && !route[0].turn ? route[0].node : -1; } }
        public int ApproachEdge { get { return route.Count > 0 && !route[0].turn ? route[0].edge : -1; } }
        public int JunctionNode { get { return route.Count > 0 && route[0].turn ? route[0].node : -1; } }
        public float DistToEnd { get { return route.Count > 0 ? route[0].length - t : 0f; } }

        public void Init(TrafficSystem s, CarModelData m, int edge, bool fwd, float sEdge, int seed)
        {
            sys = s; world = s.world; g = world.graph; model = m;
            rnd = new System.Random(seed);
            v0Factor = 0.85f + (float)rnd.NextDouble() * 0.25f;
            halfLen = (m.boundsMax[2] - m.boundsMin[2]) * 0.5f;
            wheelR = m.wheelRadius;
            for (int i = 0; i < 4; i++) wheels[i] = transform.Find("Rueda" + i);
            rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.mass = m.mass;
            var col = gameObject.AddComponent<BoxCollider>();
            Vector3 lo = new Vector3(m.boundsMin[0], m.boundsMin[1], m.boundsMin[2]), hi = new Vector3(m.boundsMax[0], m.boundsMax[1], m.boundsMax[2]);
            float y0 = Mathf.Max(lo.y, 0.25f);
            col.center = new Vector3(0, (y0 + hi.y * 0.95f) * 0.5f, (lo.z + hi.z) * 0.5f);
            col.size = new Vector3((hi.x - lo.x) * 0.95f, hi.y * 0.95f - y0, (hi.z - lo.z) * 0.97f);

            var e = g.edges[edge];
            int toNode = fwd ? e.b : e.a;
            float endS = fwd ? e.length - Lanes.Trim(g, e.b) : Lanes.Trim(g, e.a);
            float len = Mathf.Abs(endS - sEdge);
            if (len < 1f) { WantsDespawn = true; return; }
            route.Add(EdgePiece(edge, fwd, sEdge, len, toNode));
            t = 0f;
            v = BaseSpeed(e) * 0.6f;
            Extend();
            Pose(true);
        }

        Piece EdgePiece(int edge, bool fwd, float sStart, float len, int toNode)
        {
            var e = g.edges[edge];
            int lane = 0;   // el carril de la derecha
            return new Piece { turn = false, edge = edge, fwd = fwd, sStart = sStart, length = Mathf.Max(0.1f, len), node = toNode, lane = Lanes.Offset(e, lane) };
        }

        float BaseSpeed(RoadEdge e)
        {
            float vmax = e.width >= 10f ? 25f : (e.type == "Carretera" ? 13.9f : 8.6f);
            if (e.roundabout) vmax = Mathf.Min(vmax, 8f);
            return vmax * v0Factor;
        }

        /// <summary>Añade tramos y giros hasta tener al menos 3 piezas por delante.</summary>
        void Extend()
        {
            int guard = 0;
            while (route.Count < 4 && guard++ < 6)
            {
                var last = route[route.Count - 1];
                if (last.turn) break;
                int node = last.node;
                var cur = g.edges[last.edge];
                int next = ChooseNext(last.edge, node);
                if (next < 0) { WantsDespawn = route.Count <= 1; break; }
                var ne = g.edges[next];
                bool nfwd = ne.a == node;
                if (next == last.edge) nfwd = !last.fwd;          // cambio de sentido en un fondo de saco
                float trimN = Mathf.Min(Lanes.Trim(g, node), ne.length * 0.45f);
                int far = nfwd ? ne.b : ne.a;
                float trimF = Mathf.Min(Lanes.Trim(g, far), ne.length * 0.45f);
                float sStart = nfwd ? trimN : ne.length - trimN;
                float len = ne.length - trimN - trimF;
                if (len < 0.5f) { WantsDespawn = route.Count <= 1; break; }
                var np = EdgePiece(next, nfwd, sStart, len, far);
                // giro dentro del cruce entre el final del tramo actual y el principio del siguiente
                Vector3 a, da, b, db;
                PoseOnEdge(last, last.length, out a, out da);
                PoseOnEdge(np, 0f, out b, out db);
                float gap = Vector3.Distance(new Vector3(a.x, 0, a.z), new Vector3(b.x, 0, b.z));
                if (gap > 0.3f)
                {
                    Vector3 c = Intersect(a, da, b, db, gap);
                    var turn = new Piece { turn = true, node = node, p0 = a, p1 = c, p2 = b, yA = a.y, yB = b.y };
                    turn.flatGround = Lanes.FlagAt(cur, last.fwd ? cur.length : 0f) != 1 && Lanes.FlagAt(ne, nfwd ? 0f : ne.length) != 1;
                    turn.length = BezierLength(turn);
                    route.Add(turn);
                }
                route.Add(np);
            }
        }

        static Vector3 Intersect(Vector3 a, Vector3 da, Vector3 b, Vector3 db, float gap)
        {
            // intersección de las rectas a + da*t y b - db*u en planta
            float det = da.x * (-db.z) - da.z * (-db.x);
            if (Mathf.Abs(det) > 0.15f)
            {
                Vector3 r = b - a;
                float tt = (r.x * (-db.z) - r.z * (-db.x)) / det;
                if (tt > 0.2f && tt < gap * 1.5f) { var c = a + da * tt; c.y = (a.y + b.y) * 0.5f; return c; }
            }
            return (a + b) * 0.5f + da * gap * 0.25f - db * gap * 0.25f;
        }

        static Vector3 Bez(Piece p, float u)
        {
            float k = 1 - u;
            return k * k * p.p0 + 2 * k * u * p.p1 + u * u * p.p2;
        }

        static float BezierLength(Piece p)
        {
            float L = 0; Vector3 prev = p.p0;
            for (int i = 1; i <= 8; i++) { var q = Bez(p, i / 8f); L += Vector3.Distance(prev, q); prev = q; }
            return Mathf.Max(L, 0.3f);
        }

        int ChooseNext(int curEdge, int node)
        {
            var cur = g.edges[curEdge];
            Vector3 inDir = -g.DepartDir(curEdge, node, 8f);
            float total = 0;
            var cand = new List<KeyValuePair<int, float>>();
            foreach (int ei in g.adj[node])
            {
                if (ei == curEdge) continue;
                var e = g.edges[ei];
                if (!e.AllowsFrom(node) || e.length < 3f) continue;
                Vector3 od = g.DepartDir(ei, node, 8f);
                float ang = Vector3.Angle(inDir, od);
                float wgt = ang < 35f ? 3f : (ang < 135f ? 1.2f : 0.15f);
                if (e.type == "Carretera") wgt *= 1.4f;
                if (g.adj[e.Other(node)].Count <= 1) wgt *= 0.15f;   // fondos de saco: poco probables
                cand.Add(new KeyValuePair<int, float>(ei, wgt));
                total += wgt;
            }
            if (cand.Count == 0) return cur.Allows(cur.b != node) ? curEdge : -1;   // sin salida legal: media vuelta si la calle es de doble sentido
            float r = (float)rnd.NextDouble() * total;
            foreach (var c in cand) { r -= c.Value; if (r <= 0) return c.Key; }
            return cand[cand.Count - 1].Key;
        }

        void PoseOnEdge(Piece p, float tt, out Vector3 pos, out Vector3 dir)
        {
            var e = g.edges[p.edge];
            float s = p.fwd ? p.sStart + tt : p.sStart - tt;
            s = Mathf.Clamp(s, 0, e.length);
            Vector3 c = e.PointAt(s, true);
            Vector3 tan = e.TangentAt(s);
            Vector3 right = new Vector3(tan.z, 0, -tan.x);
            dir = p.fwd ? tan : -tan;
            pos = c + right * (p.fwd ? p.lane : -p.lane);
            pos.y = Lanes.SurfaceY(world, e, s, pos, false);
        }

        void PoseAt(int pi, float tt, out Vector3 pos, out Vector3 dir)
        {
            var p = route[pi];
            if (!p.turn) { PoseOnEdge(p, tt, out pos, out dir); return; }
            float u = Mathf.Clamp01(tt / p.length);
            pos = Bez(p, u);
            Vector3 d = Bez(p, Mathf.Min(1, u + 0.05f)) - Bez(p, Mathf.Max(0, u - 0.05f));
            d.y = 0;
            dir = d.sqrMagnitude > 1e-6f ? d.normalized : transform.forward;
            float yl = Mathf.Lerp(p.yA, p.yB, u);
            pos.y = p.flatGround ? Mathf.Clamp(world.SurfaceHeight(pos, yl) + 0.065f, yl - 1.5f, yl + 1.5f) : yl;
        }

        /// <summary>Posición a 'ahead' metros por delante (o detrás si es negativo) del punto actual.</summary>
        Vector3 Sample(float ahead)
        {
            int pi = 0; float tt = t + ahead;
            while (tt > route[pi].length && pi + 1 < route.Count) { tt -= route[pi].length; pi++; }
            if (tt < 0) tt = 0;
            Vector3 p, d; PoseAt(pi, Mathf.Min(tt, route[pi].length), out p, out d);
            return p;
        }

        void Pose(bool snap)
        {
            Vector3 p, d; PoseAt(0, t, out p, out d);
            Vector3 front = Sample(1.3f), back = Sample(-0f) ;
            Vector3 fwd = front - back;
            if (fwd.sqrMagnitude < 1e-4f) fwd = d;
            Quaternion rot = Quaternion.LookRotation(Vector3.Lerp(d, fwd.normalized, 0.5f), Vector3.up);
            if (snap) { transform.position = p; transform.rotation = rot; rb.position = p; rb.rotation = rot; }
            else { rb.MovePosition(p); rb.MoveRotation(Quaternion.Slerp(rb.rotation, rot, 0.5f)); }
        }

        void FixedUpdate()
        {
            if (WantsDespawn || Crashed || route.Count == 0) return;
            float dt = Time.fixedDeltaTime;
            var cur = route[0];
            float vDes = BaseSpeed(g.edges[CurrentEdge()]);
            float acc = IDM(v, vDes, 1e9f, 0f);

            // curvas: velocidad máxima en los giros de los cruces y en las curvas cerradas del tramo
            float dist = cur.length - t;
            for (int i = 1; i < route.Count && dist < 60f; i++)
            {
                var p = route[i];
                if (p.turn)
                {
                    float ang = Vector3.Angle(p.p1 - p.p0, p.p2 - p.p1) * Mathf.Deg2Rad;
                    float R = ang > 0.05f ? p.length / ang : 1e4f;
                    float vt = Mathf.Max(3.5f, Mathf.Sqrt(2.6f * R));
                    acc = Mathf.Min(acc, BrakeTo(vt, dist));
                }
                dist += p.length;
            }

            // líder: coches del tráfico y de los jugadores delante en mi carril
            float gap = 1e9f, lv = 0f;
            Vector3 myPos = transform.position;
            foreach (var o in sys.cars)
            {
                if (o == null || o == this) continue;
                Leader(o.transform, o.Velocity, o.HalfLength, ref gap, ref lv);
            }
            foreach (var rc in sys.remoteCars)
                if (rc != null) Leader(rc.transform, rc.Velocity, rc.HalfLength, ref gap, ref lv);
            foreach (var d in sys.drivers)
            {
                if (d == null || !d.gameObject.activeInHierarchy) continue;
                float hl = d.model != null ? (d.model.boundsMax[2] - d.model.boundsMin[2]) * 0.5f : 2.1f;
                Leader(d.transform, d.Body != null ? d.Body.Vel() : Vector3.zero, hl, ref gap, ref lv);
            }
            if (gap < 1e8f) acc = Mathf.Min(acc, IDM(v, vDes, gap, v - lv));

            // cruce al final del tramo actual: semáforo, STOP, ceda el paso, prioridad
            if (!cur.turn && g.adj[cur.node].Count >= 3)
            {
                float toLine = cur.length - t - halfLen - 0.4f;   // morro 0.2 m antes de la línea de detención
                if (lastNodeChecked != cur.node) { lastNodeChecked = cur.node; stoppedAtSign = false; committed = false; waitTimer = 0f; }
                if (!committed)
                {
                    bool mustStop = false;
                    var light = world.signals != null ? world.signals.StateFor(cur.edge, cur.node) : TrafficControl.LightState.None;
                    if (light == TrafficControl.LightState.Red) mustStop = true;
                    else if (light == TrafficControl.LightState.Amber) mustStop = toLine > v * v / 7f + 1f;
                    else if (light == TrafficControl.LightState.None)
                    {
                        int sign = g.SignAt(cur.edge, cur.node);
                        if (sign == 1 && !stoppedAtSign)
                        {
                            mustStop = true;
                            if (toLine < 2.5f && v < 0.4f) { stopTimer += dt; if (stopTimer > 1.0f) stoppedAtSign = true; }
                            else stopTimer = 0f;
                        }
                        if (!mustStop && toLine < 30f) mustStop = MustYield(cur.edge, cur.node, sign);
                    }
                    else if (light == TrafficControl.LightState.Green && toLine < 25f) mustStop = JunctionOccupied(cur.node);
                    if (mustStop)
                    {
                        waitTimer += v < 0.5f ? dt : 0f;
                        if (waitTimer > 9f && light != TrafficControl.LightState.Red && !JunctionOccupied(cur.node)) mustStop = false;   // evita bloqueos
                    }
                    if (mustStop) acc = Mathf.Min(acc, IDM(v, vDes, Mathf.Max(toLine, 0.05f), v));
                    else if (toLine < 1.5f) committed = true;
                }
            }

            // pasos de cebra con peatones cruzando (o esperando, si da tiempo a parar)
            float off = 0f;
            for (int i = 0; i < route.Count && off < 40f; i++)
            {
                var p = route[i];
                if (!p.turn)
                {
                    float tt0 = i == 0 ? t : 0f;
                    foreach (var c in g.crossings[p.edge])
                    {
                        float along = p.fwd ? c.s - p.sStart : p.sStart - c.s;
                        if (along < tt0 || along > p.length + 2f) continue;
                        float dd = off + along - tt0 - 2.6f - halfLen;
                        if (dd < -1f) continue;
                        bool busy = sys.CrossingBusy(p.edge, c.s);
                        bool waiting = sys.CrossingWaiting(p.edge, c.s) && (dd > v * v / 6f + 1f || v < 0.5f);
                        if (busy || waiting) acc = Mathf.Min(acc, IDM(v, vDes, Mathf.Max(dd, 0.05f), v));
                    }
                }
                off += p.length - (i == 0 ? t : 0f);
            }

            acc = Mathf.Clamp(acc, -9f, 2.2f);
            v = Mathf.Max(0f, v + acc * dt);
            t += v * dt;
            while (route.Count > 1 && t > route[0].length)
            {
                t -= route[0].length;
                route.RemoveAt(0);
                Extend();
            }
            if (route.Count == 1 && t >= route[0].length) { t = route[0].length; v = 0; WantsDespawn = true; }
            Pose(false);
            spin += v / Mathf.Max(wheelR, 0.2f) * Mathf.Rad2Deg * dt;
            foreach (var w in wheels) if (w != null) w.localRotation = Quaternion.Euler(spin, 0, 0);
        }

        public float HalfLength { get { return halfLen; } }

        int CurrentEdge()
        {
            foreach (var p in route) if (!p.turn) return p.edge;
            return 0;
        }

        void Leader(Transform o, Vector3 ov, float otherHalf, ref float gap, ref float lv)
        {
            Vector3 rel = transform.InverseTransformPoint(o.position);
            if (rel.z <= 0f || rel.z > 45f) return;
            float lat = 1.5f + Mathf.Min(rel.z * 0.03f, 0.6f);
            if (Mathf.Abs(rel.x) > lat || Mathf.Abs(rel.y) > 3f) return;
            float along = Vector3.Dot(ov, transform.forward);
            if (along < -2f && Mathf.Abs(rel.x) > 1.0f) return;   // viene de frente por su carril
            float gp = rel.z - halfLen - otherHalf;
            if (gp < gap) { gap = gp; lv = Mathf.Max(0f, along); }
        }

        static float IDM(float v, float v0, float gap, float dv)
        {
            const float a = 1.6f, b = 2.6f, s0 = 2.2f, T = 1.2f;
            float free = 1f - Mathf.Pow(v / Mathf.Max(v0, 0.1f), 4f);
            if (gap > 1e8f) return a * free;
            float sStar = s0 + Mathf.Max(0f, v * T + v * dv / (2f * Mathf.Sqrt(a * b)));
            return a * (free - (sStar / Mathf.Max(gap, 0.05f)) * (sStar / Mathf.Max(gap, 0.05f)));
        }

        float BrakeTo(float vt, float dist)
        {
            if (v <= vt) return 2f;
            float need = (v * v - vt * vt) / (2f * Mathf.Max(dist, 0.5f));
            return need > 0.8f ? -need : 0.5f;
        }

        // ---------------------------------------------------------------- prioridad en los cruces
        bool MustYield(int myEdge, int node, int sign)
        {
            if (JunctionOccupied(node)) return true;
            int myPri = g.Priority(myEdge);
            Vector3 myDir = -g.DepartDir(myEdge, node, 8f);
            Vector3 nodeP = g.nodes[node];
            foreach (var o in sys.cars)
            {
                if (o == null || o == this || o.Crashed) continue;
                if (o.ApproachNode != node || o.ApproachEdge == myEdge) continue;
                float eta = o.DistToEnd / Mathf.Max(o.Speed, 0.5f);
                if (o.DistToEnd > 30f || (o.Speed < 0.3f && o.DistToEnd > 4f)) continue;
                if (Conflicts(sign, myPri, myDir, o.ApproachEdge, -g.DepartDir(o.ApproachEdge, node, 8f), eta)) return true;
            }
            foreach (var rc in sys.remoteCars)
            {
                if (rc == null || rc.crashed) continue;
                Vector3 rel = nodeP - rc.transform.position; rel.y = 0;
                float dist = rel.magnitude;
                if (dist > 35f || dist < 1f) continue;
                Vector3 vel = rc.Velocity; vel.y = 0;
                float closing = Vector3.Dot(vel, rel / dist);
                if (closing < 1.5f) continue;
                float dd;
                int de = g.NearestEdge(rc.transform.position, 10f, out dd);
                if (de == myEdge) continue;
                if (Conflicts(sign, myPri, myDir, de, vel.normalized, dist / closing)) return true;
            }
            foreach (var d in sys.drivers)
            {
                if (d == null || d.Body == null || !d.gameObject.activeInHierarchy) continue;
                Vector3 rel = nodeP - d.transform.position; rel.y = 0;
                float dist = rel.magnitude;
                if (dist > 35f || dist < 1f) continue;
                Vector3 vel = d.Body.Vel(); vel.y = 0;
                float closing = Vector3.Dot(vel, rel / dist);
                if (closing < 1.5f) continue;
                float dd;
                int de = g.NearestEdge(d.transform.position, 10f, out dd);
                if (de == myEdge) continue;
                if (Conflicts(sign, myPri, myDir, de, vel.normalized, dist / closing)) return true;
            }
            return false;
        }

        bool Conflicts(int sign, int myPri, Vector3 myDir, int otherEdge, Vector3 otherDir, float eta)
        {
            if (eta > 5f) return false;
            if (sign == 1 || sign == 2) return true;                      // STOP / CEDA: cedo a todos
            int op = otherEdge >= 0 ? g.Priority(otherEdge) : 1;
            if (otherEdge >= 0 && g.SignAt(otherEdge, lastNodeChecked) != 0) op = -1;   // el otro tiene señal: cede él
            if (op > myPri) return true;
            if (op < myPri) return false;
            return Vector3.Cross(myDir, otherDir).y < -0.3f;              // prioridad de la derecha
        }

        bool JunctionOccupied(int node)
        {
            foreach (var o in sys.cars)
                if (o != null && o != this && o.JunctionNode == node && o.Speed < 6f && Vector3.Dot(o.Velocity, transform.forward) < 3f)
                    return true;
            foreach (var rc in sys.remoteCars)
                if (rc != null && rc.junction == node && rc.speed < 6f && Vector3.Dot(rc.Velocity, transform.forward) < 3f)
                    return true;
            return false;
        }

        // ---------------------------------------------------------------- golpes
        void OnCollisionEnter(Collision c)
        {
            if (Crashed || c.rigidbody == null || c.rigidbody.isKinematic) return;
            if (c.relativeVelocity.magnitude < 3f) return;
            Crashed = true;
            crashTime = Time.time;
            rb.isKinematic = false;
            rb.SetDamping(0.4f, 1.5f);
            rb.SetVel(transform.forward * v);
        }
    }

    /// <summary>Peatón low poly: pasea por las aceras, cruza por los pasos de cebra y se aparta de los coches.</summary>
    public class Pedestrian : MonoBehaviour
    {
        enum State { Walk, Corner, Wait, Cross }
        TrafficSystem sys;
        World world;
        RoadGraph g;
        System.Random rnd;
        State state;
        int edge; bool fwd; int side; float s;
        float speed, phase;
        Vector3 cornerFrom, cornerTo; float cornerT, cornerLen;
        int nextEdge; bool nextFwd; int nextSide;
        float crossS, crossT, crossWidth; int crossFrom;
        float lastCrossS = -999f;
        float waitTimer;
        Vector3 dodge, dodgeVel;
        float hop;
        Transform legL, legR, armL, armR, body;
        public bool WantsDespawn { get; private set; }
        public ushort netId;       // online
        public int lookSeed;       // aspecto (ropa, piel, altura): lo reciben los demás jugadores

        /// <summary>Estado para la red: 0 andando, 1 esperando en un paso de cebra, 2 cruzando.</summary>
        public byte NetState(out int crossEdge, out float crossPos)
        {
            crossEdge = edge; crossPos = crossS;
            if (state == State.Wait) return 1;
            if (state == State.Cross) return 2;
            return 0;
        }

        static Mesh cube;

        public static bool Sidewalk(RoadGraph g, RoadEdge e, out float s0, out float s1)
        {
            s0 = s1 = 0;
            if (e.type != "Calle") return false;
            foreach (int f in e.flags) if (f == 1 || f == 2) return false;
            s0 = g.adj[e.a].Count >= 3 ? g.NodeRadius(e.a) + 2.5f : 0f;
            s1 = e.length - (g.adj[e.b].Count >= 3 ? g.NodeRadius(e.b) + 2.5f : 0f);
            return s1 - s0 > 4f;
        }

        public void Init(TrafficSystem ts, int e, bool forward, int sd, float sEdge, int seed)
        {
            sys = ts; world = ts.world; g = world.graph;
            rnd = new System.Random(seed);
            edge = e; fwd = forward; side = sd; s = sEdge;
            speed = 1.1f + (float)rnd.NextDouble() * 0.5f;
            phase = (float)rnd.NextDouble() * 6f;
            lookSeed = rnd.Next();
            BuildBody();
            transform.position = SidewalkPoint(edge, s, side);
        }

        void BuildBody()
        {
            body = BuildRig(transform, lookSeed, out legL, out legR, out armL, out armR);
        }

        /// <summary>Monta el muñeco low poly con el aspecto que da la semilla (igual en todos los PCs).</summary>
        public static Transform BuildRig(Transform parent, int look, out Transform legL, out Transform legR, out Transform armL, out Transform armR)
        {
            if (cube == null)
            {
                var mb = new MeshBuilder(1);
                mb.Box(0, Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, Vector3.one * 0.5f);
                cube = mb.Build("ped_cube");
            }
            var rnd = new System.Random(look);
            Color[] shirts = { new Color(0.8f, 0.2f, 0.2f), new Color(0.2f, 0.4f, 0.8f), new Color(0.95f, 0.95f, 0.9f), new Color(0.2f, 0.6f, 0.3f), new Color(0.9f, 0.7f, 0.2f), new Color(0.15f, 0.15f, 0.18f), new Color(0.6f, 0.3f, 0.6f) };
            Color[] pants = { new Color(0.15f, 0.2f, 0.35f), new Color(0.1f, 0.1f, 0.1f), new Color(0.45f, 0.4f, 0.3f), new Color(0.3f, 0.3f, 0.32f) };
            Color[] skins = { new Color(0.96f, 0.8f, 0.66f), new Color(0.85f, 0.65f, 0.5f), new Color(0.6f, 0.42f, 0.3f), new Color(0.4f, 0.28f, 0.2f) };
            float hgt = 0.9f + (float)rnd.NextDouble() * 0.15f;
            var shirt = Mat.Color(shirts[rnd.Next(shirts.Length)], 0.2f);
            var pant = Mat.Color(pants[rnd.Next(pants.Length)], 0.2f);
            var skin = Mat.Color(skins[rnd.Next(skins.Length)], 0.3f);
            var body = new GameObject("cuerpo").transform;
            body.SetParent(parent, false);
            body.localScale = Vector3.one * hgt;
            Part(body, "torso", new Vector3(0, 1.22f, 0), new Vector3(0.42f, 0.6f, 0.24f), shirt);
            Part(body, "cabeza", new Vector3(0, 1.66f, 0), new Vector3(0.22f, 0.25f, 0.23f), skin);
            legL = Limb(body, new Vector3(-0.11f, 0.92f, 0), new Vector3(0.16f, 0.9f, 0.18f), pant);
            legR = Limb(body, new Vector3(0.11f, 0.92f, 0), new Vector3(0.16f, 0.9f, 0.18f), pant);
            armL = Limb(body, new Vector3(-0.27f, 1.48f, 0), new Vector3(0.11f, 0.62f, 0.12f), shirt);
            armR = Limb(body, new Vector3(0.27f, 1.48f, 0), new Vector3(0.11f, 0.62f, 0.12f), shirt);
            return body;
        }

        static void Part(Transform parent, string name, Vector3 pos, Vector3 size, Material m)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            go.AddComponent<MeshFilter>().sharedMesh = cube;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }

        static Transform Limb(Transform parent, Vector3 pivot, Vector3 size, Material m)
        {
            var p = new GameObject("articulacion").transform;
            p.SetParent(parent, false);
            p.localPosition = pivot;
            Part(p, "miembro", new Vector3(0, -size.y * 0.5f, 0), size, m);
            return p;
        }

        Vector3 SidewalkPoint(int ei, float sEdge, int sd)
        {
            var e = g.edges[ei];
            Vector3 c = e.PointAt(sEdge, true);
            Vector3 q = c + Lanes.Right(e, sEdge) * sd * (e.width * 0.5f + 1.0f);
            q.y = world.SurfaceHeight(q, c.y) + 0.13f;
            return q;
        }

        public void Release()
        {
            if (state == State.Cross && sys != null) sys.SetCrossingBusy(edge, crossS, -1);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0) return;
            var e = g.edges[edge];
            Vector3 pos = transform.position, dir = transform.forward;
            bool moving = true;
            switch (state)
            {
                case State.Walk:
                    {
                        float s0, s1;
                        if (!Sidewalk(g, e, out s0, out s1)) { WantsDespawn = true; return; }
                        s += (fwd ? 1 : -1) * speed * dt;
                        // ¿paso de cebra aquí? a veces cruza
                        foreach (var c in g.crossings[edge])
                        {
                            if (Mathf.Abs(c.s - s) < 0.4f && Mathf.Abs(c.s - lastCrossS) > 3f)
                            {
                                lastCrossS = c.s;
                                if (rnd.NextDouble() < 0.5) { state = State.Wait; crossS = c.s; waitTimer = 0f; }
                                break;
                            }
                        }
                        if ((fwd && s >= s1) || (!fwd && s <= s0)) { s = Mathf.Clamp(s, s0, s1); StartCorner(); }
                        pos = SidewalkPoint(edge, s, side);
                        dir = e.TangentAt(s) * (fwd ? 1 : -1);
                        break;
                    }
                case State.Corner:
                    {
                        cornerT += speed * dt;
                        float u = Mathf.Clamp01(cornerT / Mathf.Max(cornerLen, 0.01f));
                        pos = Vector3.Lerp(cornerFrom, cornerTo, u);
                        pos.y = world.SurfaceHeight(pos, pos.y) + 0.1f;
                        dir = cornerTo - cornerFrom;
                        if (u >= 1f) { edge = nextEdge; fwd = nextFwd; side = nextSide; e = g.edges[edge]; s = fwd ? NearS(true) : NearS(false); state = State.Walk; lastCrossS = -999f; }
                        break;
                    }
                case State.Wait:
                    {
                        moving = false;
                        sys.SetCrossingWaiting(edge, crossS);
                        waitTimer += dt;
                        pos = SidewalkPoint(edge, crossS, side);
                        dir = -Lanes.Right(e, crossS) * side;
                        if (SafeToCross(e) || waitTimer > 20f)
                        {
                            state = State.Cross; crossT = 0f; crossFrom = side; crossWidth = e.width + 2.0f;
                            sys.SetCrossingBusy(edge, crossS, 1);
                        }
                        break;
                    }
                case State.Cross:
                    {
                        crossT += speed * 1.1f * dt;
                        float x = side * (e.width * 0.5f + 1.0f) - side * Mathf.Min(crossT, crossWidth);
                        Vector3 c = e.PointAt(crossS, true);
                        Vector3 r = Lanes.Right(e, crossS);
                        pos = c + r * x;
                        pos.y = world.SurfaceHeight(pos, c.y) + (Mathf.Abs(x) > e.width * 0.5f ? 0.13f : 0.08f);
                        dir = -r * side;
                        if (crossT >= crossWidth)
                        {
                            sys.SetCrossingBusy(edge, crossS, -1);
                            side = -side; s = crossS; state = State.Walk;
                            if (rnd.NextDouble() < 0.5) fwd = !fwd;
                        }
                        break;
                    }
            }

            // apartarse de los coches que vienen hacia mí (sin colisionador: no son obstáculos físicos)
            foreach (var kv in sys.Vehicles())
            {
                Vector3 rel = pos - kv.Key; rel.y = 0;
                Vector3 vv = kv.Value; vv.y = 0;
                float sp = vv.magnitude;
                if (sp < 2f || rel.sqrMagnitude > 900f) continue;
                Vector3 vd = vv / sp;
                float along = Vector3.Dot(rel, vd);
                float lat = (rel - vd * along).magnitude;
                // sólo si el coche viene derecho hacia mí (en su trayectoria y a menos de ~0.7 s)
                if (along < 0f || along > 2.5f + sp * 0.7f || lat > 1.6f) continue;
                Vector3 away = Vector3.Cross(Vector3.up, vd);
                if (Vector3.Dot(away, rel) < 0) away = -away;
                dodgeVel = away * 5f;
                if (hop <= 0f) hop = 0.45f;
            }
            dodge += dodgeVel * dt;
            dodgeVel = Vector3.Lerp(dodgeVel, Vector3.zero, dt * 4f);
            dodge = Vector3.Lerp(dodge, Vector3.zero, dt * 0.6f);
            if (dodge.magnitude > 3.5f) dodge = dodge.normalized * 3.5f;
            float jump = 0f;
            if (hop > 0f) { hop -= dt; jump = Mathf.Sin(Mathf.Clamp01(1f - hop / 0.45f) * Mathf.PI) * 0.35f; }

            Vector3 fp = pos + dodge;
            if (dodge.sqrMagnitude > 0.01f) fp.y = world.SurfaceHeight(fp, pos.y) + 0.1f;
            transform.position = fp + Vector3.up * jump;
            dir.y = 0;
            if (dir.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir.normalized), dt * 8f);

            // balanceo de piernas y brazos
            if (moving) phase += dt * speed * 5.5f;
            float sw = moving ? Mathf.Sin(phase) * 28f : 0f;
            legL.localRotation = Quaternion.Euler(sw, 0, 0);
            legR.localRotation = Quaternion.Euler(-sw, 0, 0);
            armL.localRotation = Quaternion.Euler(-sw * 0.8f, 0, 0);
            armR.localRotation = Quaternion.Euler(sw * 0.8f, 0, 0);
        }

        float NearS(bool startAtA)
        {
            float s0, s1;
            Sidewalk(g, g.edges[edge], out s0, out s1);
            return startAtA ? s0 : s1;
        }

        void StartCorner()
        {
            var e = g.edges[edge];
            int node = fwd ? e.b : e.a;
            var opts = new List<int>();
            foreach (int ei in g.adj[node])
            {
                float a, b;
                if (ei != edge && Sidewalk(g, g.edges[ei], out a, out b)) opts.Add(ei);
            }
            Vector3 from = SidewalkPoint(edge, s, side);
            if (opts.Count == 0)
            {
                // fondo de saco: media vuelta por la misma acera
                fwd = !fwd;
                return;
            }
            int ne = opts[rnd.Next(opts.Count)];
            var n = g.edges[ne];
            bool nf = n.a == node;
            float a0, b0;
            Sidewalk(g, n, out a0, out b0);
            float sStart = nf ? a0 : b0;
            Vector3 pL = SidewalkPoint(ne, sStart, -1), pR = SidewalkPoint(ne, sStart, 1);
            int ns = (pL - from).sqrMagnitude < (pR - from).sqrMagnitude ? -1 : 1;
            nextEdge = ne; nextFwd = nf; nextSide = ns;
            cornerFrom = from; cornerTo = ns < 0 ? pL : pR;
            cornerLen = Vector3.Distance(cornerFrom, cornerTo); cornerT = 0f;
            state = State.Corner;
        }

        bool SafeToCross(RoadEdge e)
        {
            Vector3 c = e.PointAt(crossS, true);
            foreach (var kv in sys.Vehicles())
            {
                Vector3 rel = c - kv.Key; rel.y = 0;
                float d = rel.magnitude;
                if (d > 40f) continue;
                Vector3 vv = kv.Value; vv.y = 0;
                float closing = d > 0.1f ? Vector3.Dot(vv, rel / d) : 0f;
                if (d < 6f && vv.magnitude > 1f) return false;
                if (closing > 1f && d / closing < 4.5f && d > 4f + closing * closing / 8f) return false;   // le da tiempo a parar: espera igualmente
                if (closing > 1f && d / closing < 3f) return false;
            }
            return true;
        }
    }
}
