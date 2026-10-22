using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Circuito cerrado construido a partir de 4 cruces reales: A→B→C→D→A por el camino más corto
    /// (penalizando repetir calles). Se remuestrea cada 2 m para seguimiento de vueltas e IA.
    /// </summary>
    public class Circuit
    {
        public const float Spacing = 2f;
        public RoadGraph graph;
        public int[] controlNodes;
        public List<RoadGraph.Step> steps = new List<RoadGraph.Step>();
        public List<int> legStart = new List<int>();   // índice de step donde empieza cada tramo

        // muestras
        public Vector3[] raw;      // geometría real (para bordillos, etc.)
        public Vector3[] pos;      // trazada suavizada (IA y progreso)
        public Vector3[] dir;
        public float[] width;
        public int[] edgeIdx;
        public int[] flag;
        public float[] maxSpeed;
        public float length;
        public int Count { get { return pos.Length; } }

        // estadísticas reales
        public float reusedLength, elevationGain, maxGradePct, minElev, maxElev;
        public float[] legLength = new float[4];
        public bool hasUTurn;
        public string error;

        public static Circuit Build(RoadGraph g, int[] nodes4)
        {
            var c = new Circuit { graph = g, controlNodes = (int[])nodes4.Clone() };
            var used = new HashSet<int>();
            int count = nodes4.Length;
            for (int i = 0; i < count; i++)
            {
                int a = nodes4[i], b = nodes4[(i + 1) % count];
                if (a == b) { c.error = "Dos cruces consecutivos son el mismo."; return c; }
                var path = g.ShortestPath(a, b, used, 4f);
                if (path == null) { c.error = "No hay conexión viaria entre los cruces " + (i + 1) + " y " + ((i + 1) % count + 1) + "."; return c; }
                c.legStart.Add(c.steps.Count);
                float L = 0;
                foreach (var st in path)
                {
                    float len = g.edges[st.edge].length;
                    if (used.Contains(st.edge)) c.reusedLength += len;
                    used.Add(st.edge);
                    // aristas paralelas (mismos dos nodos) cuentan como usadas: evita ida y vuelta por vías gemelas
                    foreach (int sib in g.adj[st.from])
                        if (sib != st.edge && g.edges[sib].Other(st.from) == st.to) used.Add(sib);
                    L += len;
                    c.steps.Add(st);
                }
                if (i < 4) c.legLength[i] = L;
            }
            c.Resample();
            if (c.length < 300f) c.error = "El circuito es demasiado corto (" + c.length.ToString("F0") + " m). Elige cruces más separados.";
            return c;
        }

        void Resample()
        {
            var g = graph;
            var poly = new List<Vector3>();
            var pw = new List<float>();
            var pe = new List<int>();
            var pf = new List<int>();
            foreach (var st in steps)
            {
                var e = g.edges[st.edge];
                int n = e.pts.Length;
                for (int k = 0; k < n; k++)
                {
                    int idx = st.forward ? k : n - 1 - k;
                    Vector3 p = e.pts[idx];
                    if (poly.Count > 0 && (poly[poly.Count - 1] - p).sqrMagnitude < 0.01f) continue;
                    poly.Add(p); pw.Add(e.width); pe.Add(st.edge); pf.Add(e.flags[idx]);
                }
            }
            if (poly.Count > 1 && (poly[0] - poly[poly.Count - 1]).sqrMagnitude < 0.01f)
            {
                poly.RemoveAt(poly.Count - 1); pw.RemoveAt(pw.Count - 1); pe.RemoveAt(pe.Count - 1); pf.RemoveAt(pf.Count - 1);
            }
            // acumulado (cerrado)
            int m = poly.Count;
            var cum = new float[m + 1];
            for (int i = 0; i < m; i++) cum[i + 1] = cum[i] + Vector3.Distance(poly[i], poly[(i + 1) % m]);
            length = cum[m];
            int ns = Mathf.Max(8, Mathf.RoundToInt(length / Spacing));
            float sp = length / ns;
            raw = new Vector3[ns]; width = new float[ns]; edgeIdx = new int[ns]; flag = new int[ns];
            int seg = 0;
            for (int i = 0; i < ns; i++)
            {
                float s = i * sp;
                while (seg < m - 1 && cum[seg + 1] < s) seg++;
                float t = (s - cum[seg]) / Mathf.Max(cum[seg + 1] - cum[seg], 1e-5f);
                raw[i] = Vector3.Lerp(poly[seg], poly[(seg + 1) % m], t);
                width[i] = pw[seg]; edgeIdx[i] = pe[seg]; flag[i] = pf[seg];
            }
            // desplazar el origen para que la salida quede ~40 m después del cruce 1
            int shift = Mathf.Min(Mathf.RoundToInt(40f / sp), ns / 4);
            raw = Rotate(raw, shift); width = Rotate(width, shift); edgeIdx = Rotate(edgeIdx, shift); flag = Rotate(flag, shift);

            // trazada suavizada (media móvil circular ±3 muestras en XZ)
            pos = new Vector3[ns];
            for (int i = 0; i < ns; i++)
            {
                Vector3 acc = Vector3.zero;
                for (int k = -3; k <= 3; k++) acc += raw[(i + k + ns) % ns];
                acc /= 7f;
                pos[i] = new Vector3(acc.x, raw[i].y, acc.z);
            }
            dir = new Vector3[ns];
            for (int i = 0; i < ns; i++)
            {
                Vector3 d = pos[(i + 1) % ns] - pos[(i - 1 + ns) % ns];
                dir[i] = d.normalized;
            }
            // estadísticas
            minElev = float.MaxValue; maxElev = float.MinValue;
            for (int i = 0; i < ns; i++)
            {
                Vector3 a = raw[i], b = raw[(i + 1) % ns];
                if (b.y > a.y) elevationGain += b.y - a.y;
                minElev = Mathf.Min(minElev, a.y); maxElev = Mathf.Max(maxElev, a.y);
            }
            int win = Mathf.Max(1, Mathf.RoundToInt(20f / sp));
            for (int i = 0; i < ns; i++)
            {
                Vector3 a = raw[i], b = raw[(i + win) % ns];
                float hd = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                if (hd > 5f) maxGradePct = Mathf.Max(maxGradePct, Mathf.Abs(b.y - a.y) / hd * 100f);
            }
            // giros de 180º
            for (int i = 0; i < ns; i++)
            {
                Vector3 d0 = raw[i] - raw[(i - 3 + ns) % ns], d1 = raw[(i + 3) % ns] - raw[i];
                d0.y = d1.y = 0;
                if (d0.sqrMagnitude > 1 && d1.sqrMagnitude > 1 && Vector3.Angle(d0, d1) > 150f) { hasUTurn = true; break; }
            }
        }

        static T[] Rotate<T>(T[] a, int k)
        {
            var r = new T[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[(i + k) % a.Length];
            return r;
        }

        public float SampleSpacing { get { return length / pos.Length; } }

        public int Wrap(int i) { int n = pos.Length; return ((i % n) + n) % n; }

        public Vector3 PosAt(float s, bool useRaw = false)
        {
            var arr = useRaw ? raw : pos;
            float f = s / SampleSpacing;
            int i = Mathf.FloorToInt(f);
            return Vector3.Lerp(arr[Wrap(i)], arr[Wrap(i + 1)], f - i);
        }

        public Vector3 DirAt(float s)
        {
            float f = s / SampleSpacing;
            int i = Mathf.FloorToInt(f);
            return Vector3.Slerp(dir[Wrap(i)], dir[Wrap(i + 1)], f - i).normalized;
        }

        public int IndexAt(float s) { return Wrap(Mathf.RoundToInt(s / SampleSpacing)); }

        /// <summary>Muestra más cercana buscando alrededor de 'hint' (o global si hint &lt; 0).</summary>
        public int Closest(Vector3 p, int hint, int window, out float distXZ)
        {
            int n = pos.Length;
            int best = 0;
            float bd = float.MaxValue;
            if (hint < 0)
            {
                for (int i = 0; i < n; i++)
                {
                    float d = (new Vector2(pos[i].x - p.x, pos[i].z - p.z)).sqrMagnitude + Mathf.Abs(pos[i].y - p.y) * 4f;
                    if (d < bd) { bd = d; best = i; }
                }
            }
            else
            {
                // penaliza saltos de índice: en tramos solapados (ida y vuelta) mantiene la continuidad
                // (retroceder cuesta más que avanzar: los vehículos casi siempre van hacia delante)
                float sp2 = SampleSpacing * SampleSpacing;
                for (int k = -window; k <= window; k++)
                {
                    int i = Wrap(hint + k);
                    float d = (new Vector2(pos[i].x - p.x, pos[i].z - p.z)).sqrMagnitude + k * k * sp2 * (k < 0 ? 0.6f : 0.03f);
                    if (d < bd) { bd = d; best = i; }
                }
            }
            distXZ = new Vector2(pos[best].x - p.x, pos[best].z - p.z).magnitude;
            return best;
        }

        /// <summary>Distancia continua a lo largo del circuito para la posición p cerca de la muestra idx.</summary>
        public float ProjectS(Vector3 p, int idx)
        {
            Vector3 a = pos[idx];
            Vector3 d = dir[idx];
            Vector3 ap = p - a; ap.y = 0;
            Vector3 dd = d; dd.y = 0;
            float t = Vector3.Dot(ap, dd.normalized);
            float s = idx * SampleSpacing + Mathf.Clamp(t, -SampleSpacing, SampleSpacing);
            return Mathf.Repeat(s, length);
        }

        /// <summary>Desplazamiento lateral (positivo = derecha) respecto a la trazada.</summary>
        public float Lateral(Vector3 p, int idx)
        {
            Vector3 d = dir[idx]; d.y = 0; d.Normalize();
            Vector3 r = new Vector3(d.z, 0, -d.x);
            Vector3 ap = p - pos[idx]; ap.y = 0;
            return Vector3.Dot(ap, r);
        }

        /// <summary>Perfil de velocidad máxima (curvatura + frenada anticipada).</summary>
        public void ComputeSpeedProfile(float mu, float brakeDecel, float vmax)
        {
            int n = pos.Length;
            float sp = SampleSpacing;
            maxSpeed = new float[n];
            int k = Mathf.Max(2, Mathf.RoundToInt(6f / sp));
            for (int i = 0; i < n; i++)
            {
                Vector3 d0 = pos[i] - pos[Wrap(i - k)], d1 = pos[Wrap(i + k)] - pos[i];
                d0.y = d1.y = 0;
                float ang = Vector3.Angle(d0, d1) * Mathf.Deg2Rad;
                float dist = (d0.magnitude + d1.magnitude) * 0.5f;
                float curvature = ang / Mathf.Max(dist, 0.1f);
                float v = curvature > 1e-4f ? Mathf.Sqrt(mu * 9.81f / curvature) : vmax;
                maxSpeed[i] = Mathf.Clamp(v, 6f, vmax);
            }
            // pasada hacia atrás (dos vueltas para cerrar el bucle)
            for (int pass = 0; pass < 2; pass++)
                for (int i = n - 1; i >= 0; i--)
                {
                    float next = maxSpeed[Wrap(i + 1)];
                    float lim = Mathf.Sqrt(next * next + 2f * brakeDecel * sp);
                    if (lim < maxSpeed[i]) maxSpeed[i] = lim;
                }
        }

        public string StreetAt(int idx)
        {
            int e = edgeIdx[Wrap(idx)];
            return graph.edges[e].name;
        }
    }
}
