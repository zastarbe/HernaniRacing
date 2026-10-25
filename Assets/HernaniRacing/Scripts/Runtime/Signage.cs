using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// v16: toponimia en el mundo.
    ///  · Montes: cartel de ~5 m de alto en cada cima con nombre (b5m / IDENA / OSM), letras de más de 1 m.
    ///  · Casas y edificios con nombre (b5m TOPON + OSM): placa en la fachada principal (la que da a la calle).
    /// Los tableros y postes van fusionados en una malla por trozo de mapa; los textos son TextMesh que sólo se
    /// dibujan cerca de la cámara (las placas a ~140 m, los montes a ~2,5 km).
    /// </summary>
    public class Signage : MonoBehaviour
    {
        const float PlateRange = 140f, PeakRange = 2500f;
        const float PeakLetter = 1.15f;        // altura de las mayúsculas en el cartel del monte (m)
        const float PlateLetter = 0.22f;       // en las placas de las casas
        static readonly Color PeakBoard = new Color(0.10f, 0.33f, 0.20f);
        static readonly Color PlateBoard = new Color(0.95f, 0.94f, 0.90f);
        static readonly Color PlateText = new Color(0.08f, 0.20f, 0.55f);

        struct Item { public Renderer r; public Vector3 p; public float range2; }
        readonly List<Item> items = new List<Item>();
        int cursor;
        public int PeakCount { get; private set; }
        public int PlateCount { get; private set; }

        public static Signage Build(World w)
        {
            var go = new GameObject("Toponimia (carteles y placas)");
            go.transform.SetParent(w.root, false);
            var s = go.AddComponent<Signage>();
            var font = CircuitVisuals.BuiltinFont();
            if (font == null) { Debug.LogWarning("Sin fuente integrada: no hay carteles de toponimia"); return s; }
            int nc = w.ChunkTotal;
            var wood = new MeshBuilder[nc];
            for (int i = 0; i < nc; i++) wood[i] = new MeshBuilder(3);   // 0 poste, 1 tablero verde, 2 placa blanca
            try { s.BuildPeaks(w, font, wood); } catch (System.Exception e) { Debug.LogWarning("Carteles de montes: " + e.Message); }
            try { s.BuildPlates(w, font, wood); } catch (System.Exception e) { Debug.LogWarning("Placas de casas: " + e.Message); }
            var post = Mat.Color(new Color(0.40f, 0.30f, 0.20f), 0.2f);
            var board = Mat.Color(PeakBoard, 0.3f);
            var plate = Mat.Color(PlateBoard, 0.45f);
            for (int i = 0; i < nc; i++)
                if (wood[i].VertexCount > 0)
                    MeshBuilder.Spawn("Carteles_" + i, wood[i].Build("signage_" + i), new[] { post, board, plate }, go.transform, false);
            Debug.Log("Toponimia: " + s.PeakCount + " carteles de monte, " + s.PlateCount + " placas en fachadas");
            return s;
        }

        static void BoxR(MeshBuilder mb, int sub, Vector3 c, Vector3 size, Quaternion rot)
        {
            mb.Box(sub, c, rot * Vector3.right, rot * Vector3.up, rot * Vector3.forward, size * 0.5f);
        }

        static Vector3 Closest(RoadEdge e, Vector3 p)
        {
            Vector3 best = e.pts[0]; float bd = float.MaxValue;
            for (int i = 0; i + 1 < e.pts.Length; i++)
            {
                Vector3 a = e.pts[i], b = e.pts[i + 1];
                Vector3 ab = b - a; ab.y = 0;
                Vector3 ap = p - a; ap.y = 0;
                float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(ap, ab) / ab.sqrMagnitude) : 0f;
                Vector3 q = a + (b - a) * t;
                float d = (q.x - p.x) * (q.x - p.x) + (q.z - p.z) * (q.z - p.z);
                if (d < bd) { bd = d; best = q; }
            }
            return best;
        }

        // ------------------------------------------------------------------ medidas de texto
        static float TextWidth(Font f, string t, int size, float charSize)
        {
            f.RequestCharactersInTexture(t, size);
            float px = 0;
            foreach (char c in t)
            {
                CharacterInfo ci;
                if (f.GetCharacterInfo(c, out ci, size)) px += ci.advance;
                else px += size * 0.5f;
            }
            return px * charSize * 0.1f;
        }

        /// <summary>characterSize para que las mayúsculas midan 'cap' metros con la fuente a 'size' píxeles.</summary>
        static float CharSizeFor(Font f, int size, float cap)
        {
            f.RequestCharactersInTexture("H", size);
            CharacterInfo ci;
            float glyph = f.GetCharacterInfo('H', out ci, size) && ci.glyphHeight > 0 ? ci.glyphHeight : size * 0.72f;
            return cap / (glyph * 0.1f);
        }

        TextMesh Text(Transform parent, Font f, string t, int size, float charSize, Color c, Vector3 pos, Vector3 facing, float range)
        {
            var go = new GameObject(t);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(-facing, Vector3.up);   // el texto se lee mirando hacia 'facing'
            var tm = go.AddComponent<TextMesh>();
            tm.font = f; tm.fontSize = size; tm.characterSize = charSize;
            tm.text = t; tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center; tm.color = c;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = f.material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = false;
            items.Add(new Item { r = r, p = pos, range2 = range * range });
            return tm;
        }

        // ------------------------------------------------------------------ montes
        void BuildPeaks(World w, Font f, MeshBuilder[] mb)
        {
            var peaks = w.data.scenery.peaks;
            if (peaks == null) return;
            const int size = 64;
            float cs = CharSizeFor(f, size, PeakLetter);
            foreach (var pk in peaks)
            {
                if (string.IsNullOrEmpty(pk.name)) continue;
                var c = new Vector3(pk.x, 0, pk.z);
                c.y = w.SurfaceHeight(c, pk.y);
                // el cartel mira al camino o calle más cercano (si lo hay a menos de 300 m); si no, al sur
                Vector3 facing = Vector3.back;
                float d;
                int ei = w.graph.NearestEdge(c, 300f, out d, Toponymy.AllKinds);
                if (ei >= 0)
                {
                    Vector3 q = Closest(w.graph.edges[ei], c);
                    Vector3 dir = q - c; dir.y = 0;
                    if (dir.sqrMagnitude > 1f) facing = dir.normalized;
                }
                Vector3 right = new Vector3(facing.z, 0, -facing.x);
                float tw = TextWidth(f, pk.name, size, cs);
                float bw = Mathf.Max(3f, tw + 1.2f), bh = PeakLetter + 0.9f;
                float top = 5.2f, bottom = top - bh;
                int ch = w.ChunkIndex(c);
                // dos postes (la base hundida para que no floten en ladera)
                for (int k = -1; k <= 1; k += 2)
                {
                    Vector3 pp = c + right * (k * (bw * 0.5f - 0.35f));
                    float gy = w.SurfaceHeight(pp, c.y);
                    BoxR(mb[ch], 0, new Vector3(pp.x, gy - 0.6f + (top - 0.1f + 0.6f + (c.y - gy)) * 0.5f, pp.z),
                               new Vector3(0.22f, top - 0.1f + 0.6f + (c.y - gy), 0.22f), Quaternion.LookRotation(facing));
                }
                BoxR(mb[ch], 1, c + Vector3.up * (bottom + bh * 0.5f), new Vector3(bw, bh, 0.12f), Quaternion.LookRotation(facing));
                Vector3 mid = c + Vector3.up * (bottom + bh * 0.5f);
                Text(transform, f, pk.name, size, cs, Color.white, mid + facing * 0.075f, facing, PeakRange);
                Text(transform, f, pk.name, size, cs, Color.white, mid - facing * 0.075f, -facing, PeakRange);
                PeakCount++;
            }
        }

        // ------------------------------------------------------------------ placas en las fachadas
        void BuildPlates(World w, Font f, MeshBuilder[] mb)
        {
            var bs = w.data.buildings.buildings;
            const int size = 32;
            float cs = CharSizeFor(f, size, PlateLetter);
            foreach (var b in bs)
            {
                if (string.IsNullOrEmpty(b.pn) || b.ps == null || b.ps.Length < 6) continue;
                Vector3 n = new Vector3(b.ps[3], 0, b.ps[4]);
                if (n.sqrMagnitude < 0.5f) continue;
                n.Normalize();
                string name = b.pn;
                float tw = TextWidth(f, name, size, cs);
                float maxW = Mathf.Max(1.2f, b.ps[5] - 0.4f);
                float scale = 1f;
                if (tw + 0.3f > maxW) { scale = Mathf.Max(0.55f, (maxW - 0.3f) / tw); }
                float pw = Mathf.Min(maxW, tw * scale + 0.3f), ph = PlateLetter * scale + 0.22f;
                Vector3 c = new Vector3(b.ps[0], b.ps[2], b.ps[1]) + n * 0.05f;
                int ch = w.ChunkIndex(c);
                BoxR(mb[ch], 2, c, new Vector3(pw, ph, 0.04f), Quaternion.LookRotation(n));
                Text(transform, f, name, size, cs * scale, PlateText, c + n * 0.025f, n, PlateRange);
                PlateCount++;
            }
        }

        // ------------------------------------------------------------------ sólo cerca de la cámara
        void Update()
        {
            var cam = Camera.main;
            if (cam == null || items.Count == 0) return;
            Vector3 cp = cam.transform.position;
            // repartido: ~1/8 de los textos por fotograma
            int n = Mathf.Max(64, items.Count / 8);
            for (int k = 0; k < n && k < items.Count; k++)
            {
                cursor = (cursor + 1) % items.Count;
                var it = items[cursor];
                if (it.r == null) continue;
                bool on = (it.p - cp).sqrMagnitude < it.range2;
                if (it.r.enabled != on) it.r.enabled = on;
            }
        }
    }
}
