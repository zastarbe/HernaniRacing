using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Plano de Hernani para el HUD (calles b5m con su anchura, edificios y río). Se dibuja una ventana del
    /// plano: el circuito completo o un zoom de ~200 x 200 m alrededor del jugador (tecla N).
    /// </summary>
    public class MapView
    {
        public const float MetersPerPx = 2.5f;
        static int PxX = 1, PxY = 1;           // tamaño del plano base (todo el área)
        static Texture2D baseTex;
        static Color32[] basePx;
        static World baseWorld;
        public Texture2D tex;                  // recorte con el circuito dibujado (o el plano base)
        Rect texArea;                          // metros que cubre 'tex' (x, z, ancho, alto)
        readonly World world;
        const float scale = 1f / MetersPerPx;  // px por metro
        static Texture2D arrowTex;

        public MapView(World w, Circuit circuit)
        {
            world = w;
            BuildBase(w);
            tex = baseTex;
            texArea = new Rect(0, 0, PxX * MetersPerPx, PxY * MetersPerPx);
            if (circuit == null) return;
            // recorte del plano alrededor del circuito (+400 m) con el trazado encima
            Vector2 mn = new Vector2(1e9f, 1e9f), mx = new Vector2(-1e9f, -1e9f);
            foreach (var p in circuit.raw) { mn = Vector2.Min(mn, new Vector2(p.x, p.z)); mx = Vector2.Max(mx, new Vector2(p.x, p.z)); }
            int x0 = Mathf.Clamp(Mathf.FloorToInt((mn.x - 400f) * scale), 0, PxX - 1), y0 = Mathf.Clamp(Mathf.FloorToInt((mn.y - 400f) * scale), 0, PxY - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((mx.x + 400f) * scale), x0 + 1, PxX), y1 = Mathf.Clamp(Mathf.CeilToInt((mx.y + 400f) * scale), y0 + 1, PxY);
            int cw = x1 - x0, chh = y1 - y0;
            var px = new Color32[cw * chh];
            for (int y = 0; y < chh; y++) System.Array.Copy(basePx, (y0 + y) * PxX + x0, px, y * cw, cw);
            Vector2 off = new Vector2(x0, y0);
            int n = circuit.raw.Length;
            for (int i = 0; i < n; i++)
                Line(px, cw, chh, ToPx(circuit.raw[i]) - off, ToPx(circuit.raw[(i + 1) % n]) - off, 2.0f, new Color32(255, 40, 30, 255));
            Vector2 sp = ToPx(circuit.raw[0]) - off;
            for (int dx = -5; dx <= 5; dx++)
                for (int dy = -5; dy <= 5; dy++)
                    Put(px, cw, chh, (int)sp.x + dx, (int)sp.y + dy, ((dx / 2 + dy / 2) & 1) == 0 ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255));
            tex = new Texture2D(cw, chh, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "HR_map_circuit" };
            tex.SetPixels32(px);
            tex.Apply(false);
            texArea = new Rect(x0 * MetersPerPx, y0 * MetersPerPx, cw * MetersPerPx, chh * MetersPerPx);
        }

        public void Dispose()
        {
            if (tex != null && tex != baseTex) Object.Destroy(tex);
        }

        static Vector2 ToPx(Vector3 p) { return new Vector2(p.x * scale, p.z * scale); }

        static void BuildBase(World w)
        {
            if (baseTex != null && baseWorld == w) return;
            if (baseTex != null) Object.Destroy(baseTex);
            baseWorld = w;
            PxX = Mathf.CeilToInt(w.sizeX * scale); PxY = Mathf.CeilToInt(w.sizeZ * scale);
            int W = PxX, H = PxY;
            const float sc = scale;
            var px = new Color32[W * H];
            var bg = new Color32(58, 74, 52, 235);
            for (int i = 0; i < px.Length; i++) px[i] = bg;
            // edificios (anillo exterior)
            var bcol = new Color32(150, 140, 128, 255);
            foreach (var b in w.data.buildings.buildings)
            {
                if (b.v == null || b.r == null || b.r.Length == 0) continue;
                int n = b.r[0];
                if (n < 3) continue;
                var poly = new Vector2[n];
                Vector2 mn = new Vector2(1e9f, 1e9f), mx = new Vector2(-1e9f, -1e9f);
                for (int i = 0; i < n; i++)
                {
                    poly[i] = new Vector2(b.v[i * 2] * sc, b.v[i * 2 + 1] * sc);
                    mn = Vector2.Min(mn, poly[i]); mx = Vector2.Max(mx, poly[i]);
                }
                for (int y = Mathf.Max(0, (int)mn.y); y <= Mathf.Min(H - 1, (int)mx.y); y++)
                    for (int x = Mathf.Max(0, (int)mn.x); x <= Mathf.Min(W - 1, (int)mx.x); x++)
                        if (Inside(poly, x + 0.5f, y + 0.5f)) px[y * W + x] = bcol;
            }
            // ríos
            if (w.data.scenery.rivers != null)
                foreach (var r in w.data.scenery.rivers)
                    for (int i = 0; i + 5 < r.p.Length; i += 3)
                        Line(px, W, H, new Vector2(r.p[i] * sc, r.p[i + 2] * sc), new Vector2(r.p[i + 3] * sc, r.p[i + 5] * sc),
                             Mathf.Max(0.8f, r.width * 0.5f * sc), new Color32(70, 130, 200, 255));
            // calles (con su anchura real); primero el borde oscuro y luego el relleno
            foreach (var e in w.graph.edges)
            {
                float rad = Mathf.Max(1.0f, e.width * 0.5f * sc);
                for (int i = 0; i + 1 < e.pts.Length; i++)
                    Line(px, W, H, new Vector2(e.pts[i].x * sc, e.pts[i].z * sc), new Vector2(e.pts[i + 1].x * sc, e.pts[i + 1].z * sc), rad + 0.8f, new Color32(30, 30, 32, 255));
            }
            foreach (var e in w.graph.edges)
            {
                float rad = Mathf.Max(0.6f, e.width * 0.5f * sc);
                var col = e.width >= 10f ? new Color32(235, 160, 90, 255) : (e.type == "Carretera" ? new Color32(240, 215, 140, 255) : new Color32(215, 215, 215, 255));
                for (int i = 0; i + 1 < e.pts.Length; i++)
                    Line(px, W, H, new Vector2(e.pts[i].x * sc, e.pts[i].z * sc), new Vector2(e.pts[i + 1].x * sc, e.pts[i + 1].z * sc), rad, col);
            }
            basePx = px;
            baseTex = new Texture2D(W, H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "HR_map" };
            baseTex.SetPixels32(px);
            baseTex.Apply(false);
        }

        static bool Inside(Vector2[] poly, float x, float y)
        {
            bool c = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > y) != (poly[j].y > y) && x < (poly[j].x - poly[i].x) * (y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x) c = !c;
            return c;
        }

        static void Put(Color32[] px, int W, int H, int x, int y, Color32 c)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) return;
            px[y * W + x] = c;
        }

        static void Line(Color32[] px, int W, int H, Vector2 a, Vector2 b, float rad, Color32 c)
        {
            float L = Vector2.Distance(a, b);
            int steps = Mathf.CeilToInt(L * 1.5f) + 1;
            int r = Mathf.CeilToInt(rad);
            float r2 = rad * rad;
            for (int i = 0; i <= steps; i++)
            {
                Vector2 p = Vector2.Lerp(a, b, i / (float)steps);
                int cx = Mathf.RoundToInt(p.x), cy = Mathf.RoundToInt(p.y);
                if (cx < -r || cy < -r || cx >= W + r || cy >= H + r) continue;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                        if (dx * dx + dy * dy <= r2 + 0.25f) Put(px, W, H, cx + dx, cy + dy, c);
            }
        }

        /// <summary>Dibuja la ventana [center ± span/2] del plano en 'r' (norte arriba).</summary>
        public void Draw(Rect r, Vector2 center, float span)
        {
            float half = span * 0.5f;
            float fullX = PxX * MetersPerPx, fullZ = PxY * MetersPerPx;
            center.x = span >= fullX ? fullX * 0.5f : Mathf.Clamp(center.x, half, fullX - half);
            center.y = span >= fullZ ? fullZ * 0.5f : Mathf.Clamp(center.y, half, fullZ - half);
            lastCenter = center; lastSpan = span; lastRect = r;
            // el recorte con el circuito si la ventana cabe en él; si no, el plano completo (sin trazado)
            Texture2D t = tex; Rect a = texArea;
            if (tex != baseTex && !(center.x - half >= a.xMin && center.x + half <= a.xMax && center.y - half >= a.yMin && center.y + half <= a.yMax))
            { t = baseTex; a = new Rect(0, 0, fullX, fullZ); }
            var uv = new Rect((center.x - half - a.x) / a.width, (center.y - half - a.y) / a.height, span / a.width, span / a.height);
            GUI.DrawTextureWithTexCoords(r, t, uv);
        }

        Vector2 lastCenter; float lastSpan; Rect lastRect;

        /// <summary>Posición en pantalla (GUI) de un punto del mundo en el último plano dibujado.</summary>
        public bool ToScreen(Vector3 p, out Vector2 sp)
        {
            float u = (p.x - (lastCenter.x - lastSpan * 0.5f)) / lastSpan;
            float v = (p.z - (lastCenter.y - lastSpan * 0.5f)) / lastSpan;
            sp = new Vector2(lastRect.x + u * lastRect.width, lastRect.y + (1 - v) * lastRect.height);
            return u >= 0 && u <= 1 && v >= 0 && v <= 1;
        }

        /// <summary>Marcador: flecha orientada para el jugador, cuadrado para los demás.</summary>
        public void Marker(Vector3 p, Vector3 forward, Color c, bool arrow, float size)
        {
            Vector2 sp;
            if (!ToScreen(p, out sp)) return;
            if (!arrow)
            {
                GUI.DrawTexture(new Rect(sp.x - size / 2 - 1, sp.y - size / 2 - 1, size + 2, size + 2), Tex.Solid(Color.black));
                GUI.DrawTexture(new Rect(sp.x - size / 2, sp.y - size / 2, size, size), Tex.Solid(c));
                return;
            }
            if (arrowTex == null) arrowTex = ArrowTexture();
            float ang = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(ang, sp);
            var col = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(sp.x - size / 2, sp.y - size / 2, size, size), arrowTex);
            GUI.color = col;
            GUI.matrix = m;
        }

        static Texture2D ArrowTexture()
        {
            const int S = 64;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float u = (x + 0.5f) / S * 2 - 1, v = (y + 0.5f) / S * 2 - 1;   // v hacia arriba
                    // flecha: punta arriba (0,0.95), base (-0.7,-0.8) (0,-0.4) (0.7,-0.8)
                    bool inside = InTri(u, v, new Vector2(0, 0.95f), new Vector2(-0.75f, -0.85f), new Vector2(0, -0.45f)) ||
                                  InTri(u, v, new Vector2(0, 0.95f), new Vector2(0, -0.45f), new Vector2(0.75f, -0.85f));
                    bool edge = !inside && (InTri(u, v, new Vector2(0, 1.12f), new Vector2(-0.92f, -1f), new Vector2(0, -0.6f)) ||
                                            InTri(u, v, new Vector2(0, 1.12f), new Vector2(0, -0.6f), new Vector2(0.92f, -1f)));
                    t.SetPixel(x, y, inside ? Color.white : (edge ? new Color(0, 0, 0, 1) : new Color(0, 0, 0, 0)));
                }
            t.Apply();
            return t;
        }

        static bool InTri(float x, float y, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (x - b.x) * (a.y - b.y) - (a.x - b.x) * (y - b.y);
            float d2 = (x - c.x) * (b.y - c.y) - (b.x - c.x) * (y - c.y);
            float d3 = (x - a.x) * (c.y - a.y) - (c.x - a.x) * (y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }
    }

    /// <summary>Flechas en el suelo que marcan por dónde sigue el circuito (F para ocultarlas).</summary>
    public class GuideArrows : MonoBehaviour
    {
        public Circuit circuit;
        public bool show = true;
        readonly List<Transform> pool = new List<Transform>();
        static Mesh chevron;
        static readonly RaycastHit[] hits = new RaycastHit[8];
        const int Count = 6;
        const float First = 12f, Spacing = 9f;

        public static GuideArrows Create(Circuit c)
        {
            var go = new GameObject("Flechas de guía");
            var g = go.AddComponent<GuideArrows>();
            g.circuit = c;
            if (chevron == null) chevron = BuildChevron();
            var mat = Mat.Emissive(new Color(1f, 0.82f, 0.1f));
            for (int i = 0; i < Count; i++)
            {
                var a = new GameObject("flecha" + i);
                a.transform.SetParent(go.transform, false);
                a.AddComponent<MeshFilter>().sharedMesh = chevron;
                var mr = a.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                g.pool.Add(a.transform);
            }
            return g;
        }

        static Mesh BuildChevron()
        {
            var mb = new MeshBuilder(1);
            // ">" apuntando a +Z, 2.2 m de ancho
            Vector2[] o = { new Vector2(-1.1f, -0.6f), new Vector2(0f, 0.55f), new Vector2(1.1f, -0.6f), new Vector2(0.75f, -0.95f), new Vector2(0f, -0.15f), new Vector2(-0.75f, -0.95f) };
            System.Func<Vector2, Vector3> P = v => new Vector3(v.x, 0, v.y);
            mb.FlatTri(0, P(o[0]), P(o[1]), P(o[4]), Vector3.up, Vector2.zero, Vector2.zero, Vector2.zero);
            mb.FlatTri(0, P(o[0]), P(o[4]), P(o[5]), Vector3.up, Vector2.zero, Vector2.zero, Vector2.zero);
            mb.FlatTri(0, P(o[1]), P(o[2]), P(o[3]), Vector3.up, Vector2.zero, Vector2.zero, Vector2.zero);
            mb.FlatTri(0, P(o[1]), P(o[3]), P(o[4]), Vector3.up, Vector2.zero, Vector2.zero, Vector2.zero);
            return mb.Build("chevron");
        }

        /// <summary>Coloca las flechas por delante de la distancia s del circuito.</summary>
        public void Place(float s, bool visible)
        {
            bool on = show && visible && circuit != null;
            for (int i = 0; i < pool.Count; i++)
            {
                var t = pool[i];
                if (!on) { if (t.gameObject.activeSelf) t.gameObject.SetActive(false); continue; }
                float si = Mathf.Repeat(s + First + i * Spacing, circuit.length);
                Vector3 p = circuit.PosAt(si, true);
                Vector3 d = circuit.DirAt(si); d.y = 0;
                if (d.sqrMagnitude < 1e-4f) continue;
                Vector3 n = Vector3.up;
                float best = float.MaxValue;
                int k = Physics.RaycastNonAlloc(p + Vector3.up * 2.5f, Vector3.down, hits, 8f, ~0, QueryTriggerInteraction.Ignore);
                Vector3 gp = p;
                for (int j = 0; j < k; j++)
                {
                    if (hits[j].rigidbody != null) continue;
                    if (hits[j].distance < best) { best = hits[j].distance; gp = hits[j].point; n = hits[j].normal; }
                }
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                t.position = gp + n * 0.12f;
                t.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(d.normalized, n), n);
                float pulse = 1f + 0.08f * Mathf.Sin(Time.time * 6f - i * 0.9f);
                t.localScale = Vector3.one * pulse;
            }
        }
    }
}
