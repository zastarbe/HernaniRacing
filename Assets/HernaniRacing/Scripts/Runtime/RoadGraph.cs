using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HernaniRacing
{
    public class RoadEdge
    {
        public int index, a, b;
        public string name, type;
        public float width, length;
        public Vector3[] pts;
        public int[] flags;
        public float[] cum;   // distancia acumulada por punto
        public int ow;        // 0 doble sentido, 1 sólo a->b, -1 sólo b->a (OpenStreetMap)
        public int lanes = 1; // carriles por sentido
        public bool roundabout;
        /// <summary>v16: 0 calle o carretera, 1 camino de monte / pista, 2 bidegorri, 3 vía de tren.</summary>
        public int kind;
        public bool IsRoad { get { return kind == KindRoad; } }
        public const int KindRoad = 0, KindPath = 1, KindBike = 2, KindRail = 3;
        public static int KindOf(string type)
        {
            switch (type) { case "Camino": return KindPath; case "Bidegorri": return KindBike; case "Tren": return KindRail; default: return KindRoad; }
        }

        /// <summary>¿Se puede circular por la arista saliendo del nodo 'node'?</summary>
        public bool AllowsFrom(int node) { return ow == 0 || (ow == 1 ? node == a : node == b); }
        /// <summary>¿Se puede circular en el sentido a->b (forward) o b->a?</summary>
        public bool Allows(bool forward) { return ow == 0 || (ow == 1) == forward; }
        public bool OneWay { get { return ow != 0; } }

        /// <summary>Dirección xz de la polilínea en s (sentido a->b).</summary>
        public Vector3 TangentAt(float s)
        {
            Vector3 p0 = PointAt(Mathf.Max(0, s - 1.5f), true), p1 = PointAt(Mathf.Min(length, s + 1.5f), true);
            Vector3 d = p1 - p0; d.y = 0;
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
        }

        public bool IsBridgeAt(int i) { return flags[i] == 1; }
        public bool IsTunnelAt(int i) { return flags[i] == 2; }

        public Vector3 PointAt(float s, bool fromA)
        {
            if (!fromA) s = length - s;
            s = Mathf.Clamp(s, 0, length);
            int i = System.Array.BinarySearch(cum, s);
            if (i >= 0) return pts[i];
            i = ~i;
            if (i <= 0) return pts[0];
            if (i >= pts.Length) return pts[pts.Length - 1];
            float t = (s - cum[i - 1]) / Mathf.Max(cum[i] - cum[i - 1], 1e-5f);
            return Vector3.Lerp(pts[i - 1], pts[i], t);
        }

        public int Other(int node) { return node == a ? b : a; }
    }

    /// <summary>Grafo viario de Hernani (b5m) con búsqueda de caminos y consultas espaciales.</summary>
    public class RoadGraph
    {
        public Vector3[] nodes;
        public RoadEdge[] edges;
        /// <summary>Índices de arista por nodo: SÓLO calles y carreteras (circuitos, tráfico, cruces, mobiliario...).</summary>
        public List<int>[] adj;
        /// <summary>v16: todas las aristas por nodo (también caminos, bidegorris y tren).</summary>
        public List<int>[] adjAll;
        public bool[] isJunction;
        public List<int> junctions = new List<int>();
        public int[] nodeSignal;                                   // 0, 1 semáforos, 2 rotonda
        public readonly Dictionary<int, int> signs = new Dictionary<int, int>();   // (edge*2+end) -> 1 STOP, 2 CEDA
        public List<CrossingData>[] crossings;                     // pasos de peatones por arista

        // rejilla espacial de puntos de aristas
        const float Cell = 25f;
        readonly Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();   // codifica edge<<16 | punto

        public RoadGraph(RoadsData data)
        {
            nodes = data.nodes.Select(n => new Vector3(n.x, n.y, n.z)).ToArray();
            edges = new RoadEdge[data.edges.Length];
            adj = new List<int>[nodes.Length];
            adjAll = new List<int>[nodes.Length];
            for (int i = 0; i < nodes.Length; i++) { adj[i] = new List<int>(); adjAll[i] = new List<int>(); }
            for (int i = 0; i < data.edges.Length; i++)
            {
                var e = data.edges[i];
                int n = e.p.Length / 3;
                var re = new RoadEdge
                {
                    index = i, a = e.a, b = e.b, name = string.IsNullOrEmpty(e.name) ? "Calle sin nombre" : e.name,
                    type = e.type, width = e.width, pts = new Vector3[n], flags = new int[n], cum = new float[n],
                    kind = RoadEdge.KindOf(e.type)
                };
                for (int k = 0; k < n; k++)
                {
                    re.pts[k] = new Vector3(e.p[k * 3], e.p[k * 3 + 1], e.p[k * 3 + 2]);
                    re.flags[k] = (e.f != null && k < e.f.Length) ? e.f[k] : 0;
                    if (k > 0) re.cum[k] = re.cum[k - 1] + Vector3.Distance(re.pts[k - 1], re.pts[k]);
                }
                re.length = re.cum[n - 1];
                re.ow = e.ow; re.lanes = Mathf.Max(1, e.lanes); re.roundabout = e.rb != 0;
                edges[i] = re;
                if (re.a != re.b)
                {
                    adjAll[re.a].Add(i);
                    adjAll[re.b].Add(i);
                    if (re.IsRoad) { adj[re.a].Add(i); adj[re.b].Add(i); }
                }
                for (int k = 0; k < n; k++)
                {
                    long key = Key(re.pts[k].x, re.pts[k].z);
                    List<int> l;
                    if (!grid.TryGetValue(key, out l)) grid[key] = l = new List<int>();
                    l.Add((i << 16) | Mathf.Min(k, 65535));
                }
            }
            nodeSignal = new int[nodes.Length];
            if (data.nsig != null && data.nsig.Length == nodes.Length) nodeSignal = (int[])data.nsig.Clone();
            if (data.signs != null)
                foreach (var sg in data.signs)
                    if (sg.e >= 0 && sg.e < edges.Length) signs[sg.e * 2 + sg.end] = sg.t;
            crossings = new List<CrossingData>[edges.Length];
            for (int i = 0; i < edges.Length; i++) crossings[i] = new List<CrossingData>();
            if (data.crossings != null)
                foreach (var c in data.crossings)
                    if (c.e >= 0 && c.e < edges.Length) crossings[c.e].Add(c);
            isJunction = new bool[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                var nb = new HashSet<int>();
                foreach (int e in adj[i]) nb.Add(edges[e].Other(i));
                isJunction[i] = nb.Count >= 3;
                if (isJunction[i]) junctions.Add(i);
            }
        }

        static long Key(float x, float z)
        {
            long ix = Mathf.FloorToInt(x / Cell), iz = Mathf.FloorToInt(z / Cell);
            return (ix << 32) ^ (iz & 0xffffffffL);
        }

        /// <summary>Calle o carretera más cercana a un punto (devuelve -1 si no hay ninguna a menos de maxDist).</summary>
        public int NearestEdge(Vector3 p, float maxDist, out float dist) { return NearestEdge(p, maxDist, out dist, 1 << RoadEdge.KindRoad); }

        /// <summary>Arista más cercana de los tipos de 'kindMask' (bit 1 &lt;&lt; kind).</summary>
        public int NearestEdge(Vector3 p, float maxDist, out float dist, int kindMask)
        {
            dist = float.MaxValue;
            int best = -1;
            int r = Mathf.CeilToInt(maxDist / Cell);
            int cx = Mathf.FloorToInt(p.x / Cell), cz = Mathf.FloorToInt(p.z / Cell);
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    long key = ((long)(cx + dx) << 32) ^ ((cz + dz) & 0xffffffffL);
                    List<int> l;
                    if (!grid.TryGetValue(key, out l)) continue;
                    foreach (int code in l)
                    {
                        int e = code >> 16, k = code & 0xffff;
                        if ((kindMask & (1 << edges[e].kind)) == 0) continue;
                        var pts = edges[e].pts;
                        // distancia al segmento k..k+1
                        Vector3 a = pts[k], b = pts[Mathf.Min(k + 1, pts.Length - 1)];
                        float d = DistXZ(p, a, b);
                        if (d < dist) { dist = d; best = e; }
                    }
                }
            if (dist > maxDist) return -1;
            return best;
        }

        public static float DistXZ(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector2 P = new Vector2(p.x, p.z), A = new Vector2(a.x, a.z), B = new Vector2(b.x, b.z);
            Vector2 AB = B - A;
            float t = AB.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector2.Dot(P - A, AB) / AB.sqrMagnitude) : 0f;
            return Vector2.Distance(P, A + AB * t);
        }

        public int NearestJunction(Vector3 p, float maxDist = 1e9f)
        {
            int best = -1;
            float bd = maxDist * maxDist;
            foreach (int j in junctions)
            {
                float dx = nodes[j].x - p.x, dz = nodes[j].z - p.z;
                float d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = j; }
            }
            return best;
        }

        public string JunctionLabel(int node)
        {
            var names = new List<string>();
            foreach (int e in adj[node])
                if (!names.Contains(edges[e].name)) names.Add(edges[e].name);
            return string.Join(" × ", names.Take(3).ToArray());
        }

        /// <summary>Señal (1 STOP, 2 CEDA, 0 nada) al llegar al nodo 'node' por la arista 'edge'.</summary>
        public int SignAt(int edge, int node)
        {
            int v;
            var e = edges[edge];
            return signs.TryGetValue(edge * 2 + (node == e.a ? 0 : 1), out v) ? v : 0;
        }

        /// <summary>Prioridad de una vía para ceder el paso: carreteras y rotondas mandan.</summary>
        public int Priority(int edge)
        {
            var e = edges[edge];
            if (e.roundabout) return 3;
            return e.type == "Carretera" ? 2 : (e.width >= 8 ? 1 : 0);
        }

        public float NodeRadius(int node)
        {
            float w = 4f;
            foreach (int e in adj[node]) w = Mathf.Max(w, edges[e].width * 0.5f);
            return w;
        }

        /// <summary>Dirección (xz) de salida de una arista desde un nodo, medida a 'dist' metros.</summary>
        public Vector3 DepartDir(int edge, int node, float dist = 6f)
        {
            var e = edges[edge];
            bool fromA = e.a == node;
            Vector3 p = e.PointAt(Mathf.Min(dist, e.length * 0.5f), fromA);
            Vector3 d = p - nodes[node]; d.y = 0;
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
        }

        // ------------------------------------------------------------ Dijkstra
        public struct Step { public int edge; public bool forward; public int from, to; }

        /// <summary>Camino más corto (por longitud). Las aristas ya usadas cuestan 'penalty' veces más.</summary>
        public List<Step> ShortestPath(int from, int to, HashSet<int> used, float penalty)
        {
            int n = nodes.Length;
            var dist = new float[n];
            var prevEdge = new int[n];
            for (int i = 0; i < n; i++) { dist[i] = float.MaxValue; prevEdge[i] = -1; }
            var heap = new MinHeap();
            dist[from] = 0;
            heap.Push(from, 0);
            while (heap.Count > 0)
            {
                float d; int u = heap.Pop(out d);
                if (d > dist[u]) continue;
                if (u == to) break;
                foreach (int ei in adj[u])
                {
                    var e = edges[ei];
                    int v = e.Other(u);
                    float w = e.length * (used != null && used.Contains(ei) ? penalty : 1f);
                    float nd = d + w;
                    if (nd < dist[v]) { dist[v] = nd; prevEdge[v] = ei; heap.Push(v, nd); }
                }
            }
            if (dist[to] == float.MaxValue) return null;
            var path = new List<Step>();
            int cur = to;
            while (cur != from)
            {
                int ei = prevEdge[cur];
                var e = edges[ei];
                int prev = e.Other(cur);
                path.Add(new Step { edge = ei, forward = e.a == prev, from = prev, to = cur });
                cur = prev;
            }
            path.Reverse();
            return path;
        }

        class MinHeap
        {
            readonly List<int> items = new List<int>();
            readonly List<float> keys = new List<float>();
            public int Count { get { return items.Count; } }

            public void Push(int item, float key)
            {
                items.Add(item); keys.Add(key);
                int i = items.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (keys[p] <= keys[i]) break;
                    Swap(i, p); i = p;
                }
            }

            public int Pop(out float key)
            {
                int top = items[0]; key = keys[0];
                int last = items.Count - 1;
                items[0] = items[last]; keys[0] = keys[last];
                items.RemoveAt(last); keys.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < items.Count && keys[l] < keys[m]) m = l;
                    if (r < items.Count && keys[r] < keys[m]) m = r;
                    if (m == i) break;
                    Swap(i, m); i = m;
                }
                return top;
            }

            void Swap(int a, int b)
            {
                int t = items[a]; items[a] = items[b]; items[b] = t;
                float k = keys[a]; keys[a] = keys[b]; keys[b] = k;
            }
        }
    }
}
