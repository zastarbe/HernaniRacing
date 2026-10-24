using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Velocímetro de aguja (como el de un coche de verdad) con la velocidad también en números digitales.
    /// La escala se adapta al coche: 0–180 km/h para un utilitario, hasta 360 km/h para el Fórmula 1.
    /// Todo se dibuja con IMGUI y texturas generadas por código (sin recursos externos).
    /// </summary>
    public static class Speedometer
    {
        const int FaceRes = 512;
        const float Sweep = 270f;           // grados de recorrido de la aguja
        const float StartAngle = -135f;     // 0 km/h abajo a la izquierda (0º = arriba, sentido horario)

        static readonly Dictionary<int, Texture2D> faces = new Dictionary<int, Texture2D>();
        static Texture2D needle, hub;
        static GUIStyle num, lcd, unit, gear;
        static float shown, lastStyleScale = -1f;

        /// <summary>Escala del dial para una velocidad punta (km/h): fondo de escala, paso de los números y de las marcas.</summary>
        public static void Scale(float topKmh, out int max, out int major, out int minor)
        {
            float want = Mathf.Max(120f, topKmh * 1.08f);
            major = want <= 200f ? 20 : (want <= 320f ? 40 : 50);
            max = Mathf.CeilToInt(want / major) * major;
            minor = major == 20 ? 5 : 10;
        }

        /// <summary>Ángulo de la aguja (grados, 0 = arriba, positivo en sentido horario) para una velocidad.</summary>
        public static float Angle(float kmh, int max)
        {
            return StartAngle + Sweep * Mathf.Clamp(kmh / max, 0f, 1.02f);
        }

        /// <summary>
        /// Dibuja el velocímetro en 'r' (cuadrado, coordenadas de GUI ya escaladas). 'topKmh' es la velocidad punta del
        /// coche (marca roja a partir de ahí).
        /// </summary>
        public static void Draw(Rect r, float kmh, float topKmh, bool reverse)
        {
            int max, major, minor;
            Scale(topKmh, out max, out major, out minor);
            if (Event.current.type == EventType.Repaint)
                shown = Mathf.Lerp(shown, kmh, 1f - Mathf.Exp(-14f * Mathf.Min(Time.unscaledDeltaTime, 0.1f)));
            float D = Mathf.Min(r.width, r.height), R = D * 0.5f;
            Vector2 c = new Vector2(r.x + r.width * 0.5f, r.y + r.height * 0.5f);
            Styles(D);

            GUI.DrawTexture(new Rect(c.x - R, c.y - R, D, D), Face(max, major, minor, topKmh));

            // números de la escala
            for (int v = 0; v <= max; v += major)
            {
                float a = Angle(v, max) * Mathf.Deg2Rad;
                Vector2 p = c + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * (R * 0.615f);
                GUI.Label(new Rect(p.x - 40, p.y - 16, 80, 32), v.ToString(), num);
            }

            // pantalla digital
            Rect lr = new Rect(c.x - R * 0.27f, c.y + R * 0.30f, R * 0.54f, R * 0.26f);
            GUI.DrawTexture(new Rect(lr.x - 2, lr.y - 2, lr.width + 4, lr.height + 4), Tex.Solid(new Color(0.55f, 0.58f, 0.62f, 0.9f)));
            GUI.DrawTexture(lr, Tex.Solid(new Color(0.03f, 0.06f, 0.05f, 1f)));
            GUI.Label(lr, Mathf.RoundToInt(kmh).ToString(), lcd);
            if (reverse) GUI.Label(new Rect(lr.x - R * 0.16f, lr.y, R * 0.16f, lr.height), "R", gear);
            GUI.Label(new Rect(c.x - R * 0.4f, lr.yMax + R * 0.02f, R * 0.8f, R * 0.16f), "km/h", unit);

            // aguja (girada alrededor del centro del dial) y tapa central
            if (needle == null) needle = MakeNeedle();
            if (hub == null) hub = MakeHub();
            float nl = R * 0.84f;                                   // del eje a la punta
            float h = nl * 256f / 206f, w = h * 24f / 256f;         // el eje está a 50 px de la base de la textura
            Rect nr = new Rect(c.x - w * 0.5f, c.y - h * (206f / 256f), w, h);
            Matrix4x4 m = GUI.matrix;
            GUI.matrix = m * Matrix4x4.TRS(c, Quaternion.Euler(0, 0, Angle(shown, max)), Vector3.one) * Matrix4x4.TRS(-c, Quaternion.identity, Vector3.one);
            GUI.DrawTexture(nr, needle);
            GUI.matrix = m;
            float hr = R * 0.13f;
            GUI.DrawTexture(new Rect(c.x - hr, c.y - hr, hr * 2, hr * 2), hub);
        }

        static void Styles(float D)
        {
            if (num != null && Mathf.Abs(lastStyleScale - D) < 0.5f) return;
            lastStyleScale = D;
            num = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = Mathf.RoundToInt(D * 0.064f) };
            num.normal.textColor = new Color(0.96f, 0.96f, 0.96f);
            lcd = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = Mathf.RoundToInt(D * 0.105f) };
            lcd.normal.textColor = new Color(1f, 0.68f, 0.18f);
            gear = new GUIStyle(lcd) { alignment = TextAnchor.MiddleLeft, fontSize = Mathf.RoundToInt(D * 0.07f) };
            gear.normal.textColor = new Color(1f, 0.3f, 0.25f);
            unit = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = Mathf.RoundToInt(D * 0.055f) };
            unit.normal.textColor = new Color(0.75f, 0.78f, 0.82f);
        }

        // ------------------------------------------------------------------ texturas
        static float Cov(float edgeDist) { return Mathf.Clamp01(edgeDist + 0.5f); }

        static Texture2D Face(int max, int major, int minor, float topKmh)
        {
            int red = Mathf.Clamp(Mathf.RoundToInt(topKmh / 5f) * 5, 0, max);
            int key = max * 10000 + red;
            Texture2D t;
            if (faces.TryGetValue(key, out t) && t != null) return t;
            t = new Texture2D(FaceRes, FaceRes, TextureFormat.RGBA32, true) { name = "HR_velocimetro_" + max, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var px = new Color32[FaceRes * FaceRes];
            float ctr = (FaceRes - 1) * 0.5f, Rp = FaceRes * 0.5f;
            Color white = new Color(0.95f, 0.95f, 0.95f), redC = new Color(0.95f, 0.18f, 0.12f);
            for (int y = 0; y < FaceRes; y++)
                for (int x = 0; x < FaceRes; x++)
                {
                    float dx = x - ctr, dy = y - ctr;               // dy > 0 hacia arriba
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float rn = dist / Rp;
                    float alpha = Cov((1f - rn) * Rp);
                    if (alpha <= 0f) { px[y * FaceRes + x] = new Color32(0, 0, 0, 0); continue; }

                    // fondo: degradado radial oscuro
                    Color col = Color.Lerp(new Color(0.12f, 0.13f, 0.15f), new Color(0.03f, 0.03f, 0.04f), Mathf.Pow(rn, 1.6f));
                    // reflejo suave del cristal arriba
                    float gl = Mathf.Clamp01(1f - new Vector2(dx / (Rp * 0.9f), (dy - Rp * 0.45f) / (Rp * 0.5f)).magnitude);
                    col = Color.Lerp(col, Color.white, gl * gl * 0.08f);

                    float ang = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;   // 0 = arriba, horario
                    float rel = ang - StartAngle;
                    if (rel < -180f) rel += 360f;
                    bool inSweep = rel >= -1.5f && rel <= Sweep + 1.5f;
                    float v = rel / Sweep * max;
                    float degPx = dist * Mathf.Deg2Rad;              // píxeles por grado a este radio

                    if (inSweep)
                    {
                        // arco fino blanco de toda la escala y arco rojo desde la velocidad punta
                        float arcEnds = Mathf.Min(rel + 0.4f, Sweep + 0.4f - rel) * degPx;
                        float ac = Cov(Mathf.Min(arcEnds, Mathf.Min((rn - 0.872f) * Rp, (0.884f - rn) * Rp)));
                        if (ac > 0) col = Color.Lerp(col, v >= red ? redC : white, ac * 0.9f);
                        if (v >= red)
                        {
                            float rc = Cov(Mathf.Min((Sweep + 0.4f - rel) * degPx, Mathf.Min((rn - 0.886f) * Rp, (0.915f - rn) * Rp)));
                            if (rc > 0) col = Color.Lerp(col, redC, rc);
                        }

                        // marcas: grandes cada 'major', pequeñas cada 'minor'
                        float mi = Mathf.Round(v / minor);
                        int tv = Mathf.RoundToInt(mi * minor);
                        if (tv >= 0 && tv <= max)
                        {
                            float tAng = (float)tv / max * Sweep;
                            float off = Mathf.Abs(rel - tAng) * degPx;
                            bool big = tv % major == 0;
                            bool half = !big && (tv * 2) % major == 0;
                            float hw = big ? 2.6f : (half ? 1.7f : 1.1f);
                            float r0 = big ? 0.74f : (half ? 0.79f : 0.815f);
                            float tc = Cov(Mathf.Min(hw - off, Mathf.Min((rn - r0) * Rp, (0.872f - rn) * Rp)));
                            if (tc > 0) col = Color.Lerp(col, tv >= red ? redC : white, tc);
                        }
                    }

                    // bisel metálico exterior (más claro arriba)
                    float bez = Cov(Mathf.Min((rn - 0.935f) * Rp, (1f - rn) * Rp + 0.5f));
                    if (bez > 0)
                    {
                        float lit = Mathf.Clamp01(0.5f + 0.5f * dy / Rp);
                        Color metal = Color.Lerp(new Color(0.22f, 0.23f, 0.25f), new Color(0.82f, 0.84f, 0.87f), lit);
                        col = Color.Lerp(col, metal, bez);
                    }
                    col.a = alpha * 0.96f;
                    px[y * FaceRes + x] = col;
                }
            t.SetPixels32(px);
            t.Apply(true, true);
            faces[key] = t;
            return t;
        }

        /// <summary>Aguja: 24 x 256 px, el eje a 50 px de la base, punta arriba.</summary>
        static Texture2D MakeNeedle()
        {
            const int W = 24, H = 256, P = 50;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, true) { name = "HR_aguja", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var px = new Color32[W * H];
            float cx = (W - 1) * 0.5f;
            for (int y = 0; y < H; y++)
            {
                float hw;
                if (y >= P) hw = Mathf.Lerp(4.6f, 1.1f, (y - P) / (float)(H - 1 - P));   // del eje a la punta
                else hw = Mathf.Lerp(3.2f, 4.6f, y / (float)P);                          // contrapeso
                for (int x = 0; x < W; x++)
                {
                    float d = Mathf.Abs(x - cx);
                    float a = Cov(hw - d) * Cov(Mathf.Min(y + 0.5f, H - 1 - y + 0.5f));
                    Color col = y >= P ? new Color(1f, 0.32f, 0.12f) : new Color(0.75f, 0.2f, 0.1f);
                    col = Color.Lerp(col, new Color(1f, 0.85f, 0.7f), Mathf.Clamp01(1f - d / 1.2f) * 0.35f);   // brillo central
                    col.a = a;
                    px[y * W + x] = col;
                }
            }
            t.SetPixels32(px);
            t.Apply(true, true);
            return t;
        }

        static Texture2D MakeHub()
        {
            const int S = 64;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, true) { name = "HR_eje", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var px = new Color32[S * S];
            float c = (S - 1) * 0.5f, R = S * 0.5f;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = x - c, dy = y - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Cov(R - d);
                    float lit = Mathf.Clamp01(0.5f + 0.5f * (dy - dx * 0.4f) / R);
                    Color col = Color.Lerp(new Color(0.08f, 0.08f, 0.09f), new Color(0.45f, 0.46f, 0.5f), lit);
                    if (d < R * 0.62f) col = Color.Lerp(new Color(0.1f, 0.1f, 0.11f), new Color(0.22f, 0.22f, 0.24f), lit);
                    col.a = a;
                    px[y * S + x] = col;
                }
            t.SetPixels32(px);
            t.Apply(true, true);
            return t;
        }
    }
}
