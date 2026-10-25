using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Circuito por calles reales a partir de los cruces elegidos (2 o más):
    ///  · Abierto (de A a B): 1 → 2 → … → N por el camino más corto. Se corre una sola vez, de la salida a la meta.
    ///  · Cerrado (se pulsa otra vez el cruce 1): 1 → 2 → … → N → 1, a vueltas. Con sólo 2 cruces, la vuelta
    ///    va por otra carretera distinta de la ida.
    /// Se penaliza repetir calles. Se remuestrea cada 2 m para seguir el progreso y para la IA.
    /// </summary>
    public class Circuit
    {
        public const float Spacing = 2f;
        public const int MaxPoints = 64;
        /// <summary>Abierto: metros de calle antes del cruce 1 (para la parrilla) y después de la meta (para frenar).</summary>
        public const float LeadIn = 60f, RunOff = 150f, GridNeed = 52f;
        public RoadGraph graph;
        public int[] controlNodes;
        public bool closed = true;
        public List<RoadGraph.Step> steps = new List<RoadGraph.Step>();
        public List<int> legStart = new List<int>();   // índice de step donde empieza cada tramo
        int mainFirst, mainEnd;                         // tramo real elegido (sin la entrada a la parrilla ni la frenada)

        // muestras
        public Vector3[] raw;      // geometría real (para bordillos, etc.)
        public Vector3[] pos;      // trazada suavizada (IA y progreso)
        public Vector3[] dir;
        public float[] width;
        public int[] edgeIdx;
        public int[] flag;
        public float[] maxSpeed;
        public float length;       // longitud de las muestras (abierto: con la entrada y la frenada)
        public int Count { get { return pos.Length; } }
        /// <summary>Línea de salida y meta (distancia sobre las muestras). Cerrado: las dos en 0.</summary>
        public float startS, finishS;
        public int startIdx, finishIdx;
        /// <summary>Lo que hay que recorrer en cada vuelta (abierto: de la salida a la meta).</summary>
        public float LapLength { get { return closed ? length : finishS - startS; } }

        // estadísticas reales (abierto: sólo de la salida a la meta)
        public float reusedLength, elevationGain, maxGradePct, minElev, maxElev;
        public List<float> legLength = new List<float>();
        public bool hasUTurn;
        public string error;
        /// <summary>Aviso que no impide correr (p. ej. la vuelta de un circuito de 2 cruces repite calle).</summary>
        public string note;

        /// <summary>Calles del recorrido elegido (sin la entrada a la parrilla ni la frenada después de la meta).</summary>
        public IEnumerable<RoadGraph.Step> RouteSteps
        {
            get { for (int i = mainFirst; i < mainEnd && i < steps.Count; i++) yield return steps[i]; }
        }

        public static Circuit Build(RoadGraph g, int[] nodes, bool closed = true)
        {
            var c = new Circuit { graph = g, controlNodes = (int[])nodes.Clone(), closed = closed };
            var used = new HashSet<int>();
            int count = nodes.Length;
            if (count < 2) { c.error = "Elige al menos 2 cruces."; return c; }
            if (count > MaxPoints) { c.error = "Demasiados cruces (máximo " + MaxPoints + ")."; return c; }
            int legs = closed ? count : count - 1;
            for (int i = 0; i < legs; i++)
            {
                int a = nodes[i], b = nodes[(i + 1) % count];
                if (a == b) { c.error = "Dos cruces consecutivos son el mismo."; return c; }
                List<RoadGraph.Step> path;
                if (closed && count == 2 && i == 1)
                {
                    // 2 cruces y cerrado: la vuelta no puede ir por la misma carretera que la ida
                    path = g.ShortestPath(a, b, used, float.PositiveInfinity);
                    if (path == null)
                    {
                        // no hay ninguna vuelta del todo distinta: la que menos calle repite
                        path = g.ShortestPath(a, b, used, 1000f);
                        if (path != null)
                        {
                            float rep = 0f;
                            foreach (var st in path) if (used.Contains(st.edge)) rep += g.edges[st.edge].length;
                            c.note = "No hay otra carretera para volver del cruce 2 al 1: la vuelta repite " + rep.ToString("F0") + " m de la ida (lo mínimo posible).";
                        }
                    }
                }
                else path = g.ShortestPath(a, b, used, 4f);
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
                c.legLength.Add(L);
            }
            c.mainFirst = 0; c.mainEnd = c.steps.Count;
            if (!closed) c.AddLeadInAndRunOff(used);
            c.Resample();
            if (closed && c.length < 300f) c.error = "El circuito es demasiado corto (" + c.length.ToString("F0") + " m). Elige cruces más separados.";
            else if (!closed && c.LapLength < 300f) c.error = "El recorrido es demasiado corto (" + c.LapLength.ToString("F0") + " m). Elige cruces más separados.";
            return c;
        }

        // ------------------------------------------------------------------ recorrido abierto: entrada y frenada
        /// <summary>Dirección (xz) de un paso: al salir de su nodo 'from' o al llegar a su nodo 'to'.</summary>
        static Vector3 StepDir(RoadGraph g, RoadGraph.Step st, bool atStart)
        {
            var e = g.edges[st.edge];
            if (atStart) return st.forward ? e.TangentAt(0f) : -e.TangentAt(e.length);
            return st.forward ? e.TangentAt(e.length) : -e.TangentAt(0f);
        }

        /// <summary>Sigue la calle lo más recto posible desde 'node' en la dirección 'dir' hasta 'maxLen' metros.</summary>
        static List<RoadGraph.Step> Straight(RoadGraph g, int node, Vector3 dir, float maxLen, HashSet<int> avoid)
        {
            var res = new List<RoadGraph.Step>();
            var seen = new HashSet<int>();
            float L = 0f;
            int cur = node;
            dir.y = 0;
            while (L < maxLen && res.Count < 40)
            {
                // lo más recto posible; si no hay, se gira en el cruce (nunca se da la vuelta)
                int bestE = -1; float bestA = 125f;
                foreach (int ei in g.adj[cur])
                {
                    var e = g.edges[ei];
                    if (e.a == e.b || avoid.Contains(ei) || seen.Contains(ei)) continue;
                    bool fwd = e.a == cur;
                    Vector3 d = fwd ? e.TangentAt(0f) : -e.TangentAt(e.length);
                    float ang = Vector3.Angle(dir, d);
                    if (ang < bestA) { bestA = ang; bestE = ei; }
                }
                if (bestE < 0) break;
                var be = g.edges[bestE];
                bool f = be.a == cur;
                int nxt = be.Other(cur);
                var st = new RoadGraph.Step { edge = bestE, forward = f, from = cur, to = nxt };
                res.Add(st);
                seen.Add(bestE);
                L += be.length;
                dir = StepDir(g, st, false);
                cur = nxt;
            }
            return res;
        }

        void AddLeadInAndRunOff(HashSet<int> used)
        {
            var g = graph;
            var avoid = new HashSet<int>(used);
            // antes del cruce 1: la calle que viene de detrás, en línea con la salida (ahí va la parrilla)
            var first = steps[0];
            var back = Straight(g, first.from, -StepDir(g, first, true), LeadIn + 10f, avoid);
            var lead = new List<RoadGraph.Step>();
            for (int k = back.Count - 1; k >= 0; k--)
            {
                var s = back[k];
                lead.Add(new RoadGraph.Step { edge = s.edge, forward = !s.forward, from = s.to, to = s.from });
                avoid.Add(s.edge);
            }
            // después del último cruce: la calle que sigue recto, para frenar después de la meta
            var last = steps[steps.Count - 1];
            var run = Straight(g, last.to, StepDir(g, last, false), RunOff + 10f, avoid);
            mainFirst = lead.Count;
            mainEnd = lead.Count + steps.Count;
            for (int i = 0; i < legStart.Count; i++) legStart[i] += lead.Count;
            lead.AddRange(steps);
            lead.AddRange(run);
            steps = lead;
        }

        void Resample()
        {
            var g = graph;
            var poly = new List<Vector3>();
            var pw = new List<float>();
            var pe = new List<int>();
            var pf = new List<int>();
            int pStart = 0, pEnd = 0;
            for (int si = 0; si < steps.Count; si++)
            {
                var st = steps[si];
                if (si == mainFirst) pStart = Mathf.Max(0, poly.Count - 1);
                var e = g.edges[st.edge];
                int n = e.pts.Length;
                for (int k = 0; k < n; k++)
                {
                    int idx = st.forward ? k : n - 1 - k;
                    Vector3 p = e.pts[idx];
                    if (poly.Count > 0 && (poly[poly.Count - 1] - p).sqrMagnitude < 0.01f) continue;
                    poly.Add(p); pw.Add(e.width); pe.Add(st.edge); pf.Add(e.flags[idx]);
                }
                if (si == mainEnd - 1) pEnd = poly.Count - 1;
            }
            if (!closed) { ResampleOpen(poly, pw, pe, pf, pStart, pEnd); return; }
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
            spacing = sp;
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
            startS = 0f; finishS = length; startIdx = finishIdx = 0;
            Finish(0, ns);
        }

        /// <summary>Recorrido abierto: muestras desde 60 m antes del cruce 1 hasta 150 m después del último.</summary>
        void ResampleOpen(List<Vector3> poly, List<float> pw, List<int> pe, List<int> pf, int pStart, int pEnd)
        {
            int m = poly.Count;
            var cum = new float[m];
            for (int i = 1; i < m; i++) cum[i] = cum[i - 1] + Vector3.Distance(poly[i - 1], poly[i]);
            float sA = cum[Mathf.Clamp(pStart, 0, m - 1)], sB = cum[Mathf.Clamp(pEnd, 0, m - 1)];
            float c0 = Mathf.Max(0f, sA - LeadIn), c1 = Mathf.Min(cum[m - 1], sB + RunOff);
            length = Mathf.Max(c1 - c0, 1f);
            int ns = Mathf.Max(8, Mathf.RoundToInt(length / Spacing) + 1);
            float sp = length / (ns - 1);
            spacing = sp;
            raw = new Vector3[ns]; width = new float[ns]; edgeIdx = new int[ns]; flag = new int[ns];
            int seg = 0;
            for (int i = 0; i < ns; i++)
            {
                float s = c0 + i * sp;
                while (seg < m - 2 && cum[seg + 1] < s) seg++;
                int s1 = Mathf.Min(seg + 1, m - 1);
                float t = Mathf.Clamp01((s - cum[seg]) / Mathf.Max(cum[s1] - cum[seg], 1e-5f));
                raw[i] = Vector3.Lerp(poly[seg], poly[s1], t);
                width[i] = pw[seg]; edgeIdx[i] = pe[seg]; flag[i] = pf[seg];
            }
            // la parrilla necesita ~50 m por detrás de la salida: si la calle de antes del cruce 1 es corta,
            // la salida se adelanta un poco
            startS = Mathf.Min(Mathf.Max(sA - c0, GridNeed), length);
            finishS = Mathf.Clamp(sB - c0, startS, length);
            startIdx = Mathf.Clamp(Mathf.RoundToInt(startS / sp), 0, ns - 1);
            finishIdx = Mathf.Clamp(Mathf.RoundToInt(finishS / sp), 0, ns - 1);
            Finish(startIdx, finishIdx);
        }

        /// <summary>Trazada suavizada, direcciones y estadísticas (de la muestra i0 a la i1).</summary>
        void Finish(int i0, int i1)
        {
            int ns = raw.Length;
            float sp = spacing;
            // trazada suavizada (media móvil ±3 muestras en XZ)
            pos = new Vector3[ns];
            for (int i = 0; i < ns; i++)
            {
                Vector3 acc = Vector3.zero;
                for (int k = -3; k <= 3; k++) acc += raw[Wrap(i + k)];
                acc /= 7f;
                pos[i] = new Vector3(acc.x, raw[i].y, acc.z);
            }
            dir = new Vector3[ns];
            for (int i = 0; i < ns; i++)
            {
                Vector3 d = pos[Wrap(i + 1)] - pos[Wrap(i - 1)];
                dir[i] = d.normalized;
            }
            // estadísticas
            minElev = float.MaxValue; maxElev = float.MinValue;
            int last = closed ? i1 : i1 - 1;
            for (int i = i0; i <= Mathf.Max(i0, last); i++)
            {
                if (closed && i >= ns) break;
                Vector3 a = raw[Wrap(i)], b = raw[Wrap(i + 1)];
                if (i < (closed ? ns : i1) && b.y > a.y) elevationGain += b.y - a.y;
                minElev = Mathf.Min(minElev, a.y); maxElev = Mathf.Max(maxElev, a.y);
            }
            int win = Mathf.Max(1, Mathf.RoundToInt(20f / sp));
            for (int i = i0; i < (closed ? ns : i1 - win); i++)
            {
                Vector3 a = raw[i], b = raw[Wrap(i + win)];
                float hd = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                if (hd > 5f) maxGradePct = Mathf.Max(maxGradePct, Mathf.Abs(b.y - a.y) / hd * 100f);
            }
            // giros de 180º
            for (int i = closed ? 0 : i0 + 3; i < (closed ? ns : i1 - 3); i++)
            {
                Vector3 d0 = raw[i] - raw[Wrap(i - 3)], d1 = raw[Wrap(i + 3)] - raw[i];
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

        float spacing = Spacing;
        public float SampleSpacing { get { return spacing; } }

        /// <summary>Índice de muestra válido: cerrado = da la vuelta; abierto = se queda en los extremos.</summary>
        public int Wrap(int i)
        {
            int n = raw.Length;
            if (!closed) return i < 0 ? 0 : (i >= n ? n - 1 : i);
            return ((i % n) + n) % n;
        }

        /// <summary>Distancia s válida sobre las muestras (cerrado: módulo la longitud; abierto: recortada).</summary>
        public float ClampS(float s) { return closed ? Mathf.Repeat(s, length) : Mathf.Clamp(s, 0f, length); }

        /// <summary>Diferencia a - b a lo largo del circuito (cerrado: la más corta dando la vuelta).</summary>
        public float Delta(float a, float b)
        {
            float d = a - b;
            if (!closed) return d;
            if (d > length * 0.5f) d -= length;
            if (d < -length * 0.5f) d += length;
            return d;
        }

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
            return ClampS(s);
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
            for (int pass = 0; pass < (closed ? 2 : 1); pass++)
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
