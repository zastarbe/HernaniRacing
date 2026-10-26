using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// v17: cuadro de instrumentos abajo a la derecha (en carrera y en paseo libre):
    ///  · Velocímetro (el de siempre, <see cref="Speedometer"/>).
    ///  · Cuentarrevoluciones del mismo estilo: la aguja sigue la misma caja de cambios simulada que el sonido del
    ///    motor (<see cref="EngineAudio.Simulate"/>), con la zona roja según el coche y la marcha en la pantalla.
    ///  · Brújula: rosa de los vientos que gira (N, NE, E, SE, S, SO, O, NO) y el rumbo en grados.
    ///  · Inclinómetro (como los de los todoterrenos): horizonte artificial con la pendiente adelante/atrás y la
    ///    inclinación lateral, y abajo la pendiente en % (como las señales) y los grados de lado.
    /// Tecla I: enseñar / ocultar cuentarrevoluciones, brújula e inclinómetro (el velocímetro se queda).
    /// Todo con IMGUI y texturas hechas por código.
    /// </summary>
    public static class Instruments
    {
        const string PrefKey = "hr_instruments";
        static int visible = -1;
        static int toggleFrame = -1;

        /// <summary>¿Se ven los instrumentos extra? (se guarda entre partidas)</summary>
        public static bool Visible
        {
            get { if (visible < 0) visible = PlayerPrefs.GetInt(PrefKey, 1); return visible == 1; }
            set { visible = value ? 1 : 0; PlayerPrefs.SetInt(PrefKey, visible); }
        }

        // convergencia de la cuadrícula UTM en Hernani (huso 30, 1º al este del meridiano central, lat 43,27º):
        // el norte de la cuadrícula (eje Z del mapa) está 0,7º al este del norte geográfico
        const float GridConvergence = 0.70f;

        /// <summary>Dibuja el cuadro completo. W x H: tamaño de la pantalla virtual del HUD (H = 1080).</summary>
        public static void DrawCluster(float W, float H, CarController car)
        {
            if (car == null) return;
            if (Time.frameCount != toggleFrame && GameInput.InstrumentsPressed) { toggleFrame = Time.frameCount; Visible = !Visible; }
            float kmh = Mathf.Abs(car.ForwardSpeed) * 3.6f;
            Speedometer.Draw(new Rect(W - 300, H - 300, 280, 280), kmh, car.maxSpeed * 3.6f, car.Reversing && car.ForwardSpeed < -0.5f);
            if (!Visible) return;
            Tachometer(new Rect(W - 545, H - 272, 236, 236), car);
            Transform t = car.transform;
            Vector3 f = t.forward, r = t.right;
            float heading = Mathf.Repeat(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg + GridConvergence, 360f);
            float pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;         // + morro arriba
            float roll = Mathf.Asin(Mathf.Clamp(-r.y, -1f, 1f)) * Mathf.Rad2Deg;         // + inclinado a la derecha
            if (t.up.y < 0f) roll = (roll >= 0 ? 180f : -180f) - roll;                    // volcado
            Compass(new Rect(W - 735, H - 226, 180, 180), heading);
            Inclinometer(new Rect(W - 925, H - 226, 180, 180), pitch, roll);
        }

        // ------------------------------------------------------------------ cuentarrevoluciones
        static float shownRpm;

        /// <summary>Régimen de corte (rpm) según la velocidad punta del coche: F1, deportivos, turismos y furgonetas.</summary>
        public static void EngineSpec(float topKmh, out float idle, out float redline)
        {
            if (topKmh >= 300f) { idle = 4000f; redline = 15000f; }
            else if (topKmh >= 240f) { idle = 900f; redline = 8500f; }
            else if (topKmh >= 185f) { idle = 850f; redline = 7000f; }
            else { idle = 800f; redline = 5500f; }
        }

        public static void Tachometer(Rect r, CarController car)
        {
            float topKmh = car.maxSpeed * 3.6f;
            float idle, redline;
            EngineSpec(topKmh, out idle, out redline);
            var ea = car.GetComponent<EngineAudio>();
            float r01; int gear; float ign = 1f;
            if (ea != null) { r01 = ea.Rpm01; gear = ea.Gear; ign = ea.Ignition; }
            else
            {
                float sp = Mathf.Abs(car.ForwardSpeed);
                EngineAudio.Simulate(sp, car.maxSpeed, out gear, out r01);
                gear = car.ForwardSpeed < -0.5f ? -1 : (sp < 0.3f ? 0 : gear);
            }
            float shift = redline * 0.92f;
            float rpm = r01 <= 0.25f ? idle * r01 / 0.25f : idle + (r01 - 0.25f) / 0.75f * (shift - idle);
            rpm *= ign;
            if (Event.current.type == EventType.Repaint)
                shownRpm = Mathf.Lerp(shownRpm, rpm, 1f - Mathf.Exp(-12f * Mathf.Min(Time.unscaledDeltaTime, 0.1f)));
            // escala en centenas de rpm: números cada 1000 (cada 2000 en el F1)
            int major = redline > 10000f ? 20 : 10;
            int minor = major == 20 ? 5 : 2;
            int max = Mathf.CeilToInt(redline * 1.12f / (major * 100f)) * major;
            int red = Mathf.RoundToInt(redline / 100f);
            string g = ign <= 0.01f ? "" : (gear < 0 ? "R" : (gear == 0 ? "N" : gear.ToString()));
            Speedometer.DrawDial(r, shownRpm / 100f, max, major, minor, red, v => (v / 10).ToString(),
                (Mathf.Round(rpm / 50f) * 50f).ToString("0"), "rpm × 1000", g);
        }

        // ------------------------------------------------------------------ brújula
        static Texture2D rose, compassBezel;
        static GUIStyle cardBig, cardSmall, cardN, lcdSmall, readout;
        static readonly string[] Points = { "N", "NE", "E", "SE", "S", "SO", "O", "NO" };

        static void TextStyles()
        {
            if (cardBig != null) return;
            cardBig = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 17 };
            cardBig.normal.textColor = new Color(0.96f, 0.96f, 0.96f);
            cardN = new GUIStyle(cardBig) { fontSize = 19 };
            cardN.normal.textColor = new Color(1f, 0.32f, 0.22f);
            cardSmall = new GUIStyle(cardBig) { fontSize = 11, fontStyle = FontStyle.Normal };
            cardSmall.normal.textColor = new Color(0.78f, 0.8f, 0.84f);
            lcdSmall = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 15 };
            lcdSmall.normal.textColor = new Color(1f, 0.68f, 0.18f);
            readout = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 14 };
            readout.normal.textColor = Color.white;
        }

        /// <summary>Nombre del rumbo (N, NE, E…; O = oeste) para 0..360º.</summary>
        public static string PointName(float heading)
        {
            return Points[Mathf.RoundToInt(Mathf.Repeat(heading, 360f) / 45f) % 8];
        }

        public static void Compass(Rect r, float heading)
        {
            TextStyles();
            float D = Mathf.Min(r.width, r.height), R = D * 0.5f;
            Vector2 c = r.center;
            if (rose == null) rose = MakeRose();
            if (compassBezel == null) compassBezel = MakeBezel(false);
            Rect full = new Rect(c.x - R, c.y - R, D, D);
            // rosa girada: el rumbo actual queda arriba
            Matrix4x4 m = GUI.matrix;
            GUI.matrix = m * Matrix4x4.TRS(c, Quaternion.Euler(0, 0, -heading), Vector3.one) * Matrix4x4.TRS(-c, Quaternion.identity, Vector3.one);
            GUI.DrawTexture(full, rose);
            GUI.matrix = m;
            // letras derechas (sin girar) en su sitio
            for (int i = 0; i < 8; i++)
            {
                float a = (i * 45f - heading) * Mathf.Deg2Rad;
                bool main = i % 2 == 0;
                float rr = R * (main ? 0.50f : 0.52f);
                Vector2 p = c + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * rr;
                GUI.Label(new Rect(p.x - 20, p.y - 12, 40, 24), Points[i], i == 0 ? cardN : (main ? cardBig : cardSmall));
            }
            GUI.DrawTexture(full, compassBezel);
            // rumbo digital en el centro
            Rect lr = new Rect(c.x - R * 0.36f, c.y - R * 0.14f, R * 0.72f, R * 0.28f);
            GUI.DrawTexture(new Rect(lr.x - 1, lr.y - 1, lr.width + 2, lr.height + 2), Tex.Solid(new Color(0.55f, 0.58f, 0.62f, 0.9f)));
            GUI.DrawTexture(lr, Tex.Solid(new Color(0.03f, 0.06f, 0.05f, 1f)));
            GUI.Label(lr, PointName(heading) + " " + Mathf.RoundToInt(heading) % 360 + "º", lcdSmall);
        }

        // ------------------------------------------------------------------ inclinómetro
        const int DiscRes = 176;
        static Texture2D disc, inclBezel;
        static Color32[] discPx;
        static float lastPitch = 999f, lastRoll = 999f;
        static float shownPitch, shownRoll;

        public static void Inclinometer(Rect r, float pitch, float roll)
        {
            TextStyles();
            if (Event.current.type == EventType.Repaint)
            {
                float k = 1f - Mathf.Exp(-8f * Mathf.Min(Time.unscaledDeltaTime, 0.1f));
                shownPitch = Mathf.Lerp(shownPitch, pitch, k);
                shownRoll = Mathf.Lerp(shownRoll, roll, k);
            }
            float D = Mathf.Min(r.width, r.height), R = D * 0.5f;
            Vector2 c = r.center;
            if (inclBezel == null) inclBezel = MakeBezel(true);
            const float Rin = 0.80f;                  // radio del horizonte (fracción de R)
            const float DegPx = Rin / 38f;             // 38º del centro al borde
            UpdateDisc(shownPitch, shownRoll, Rin, DegPx);
            Rect full = new Rect(c.x - R, c.y - R, D, D);
            GUI.DrawTexture(full, disc);
            // números de la escala de pendiente (10, 20, 30) sobre el horizonte, derechos
            float phi = shownRoll * Mathf.Deg2Rad, cs = Mathf.Cos(phi), sn = Mathf.Sin(phi);
            for (int d = -30; d <= 30; d += 10)
            {
                if (d == 0) continue;
                float v = (d - shownPitch) * DegPx, u = 0.30f;
                for (int s = -1; s <= 1; s += 2)
                {
                    float x = s * u * cs - v * sn, y = s * u * sn + v * cs;
                    if (x * x + y * y > (Rin - 0.08f) * (Rin - 0.08f)) continue;
                    Vector2 p = c + new Vector2(x, -y) * R;
                    GUI.Label(new Rect(p.x - 14, p.y - 9, 28, 18), Mathf.Abs(d).ToString(), cardSmall);
                }
            }
            GUI.DrawTexture(full, inclBezel);
            // lecturas: pendiente en % (como las señales de tráfico) y grados de lado
            float grade = Mathf.Tan(Mathf.Clamp(pitch, -80f, 80f) * Mathf.Deg2Rad) * 100f;
            int side = Mathf.RoundToInt(Mathf.Abs(roll));
            string s1 = "Pendiente " + (Mathf.RoundToInt(grade) == 0 ? "0" : grade.ToString("+0;-0")) + " %";
            string s2 = "Lado " + side + "º" + (side == 0 ? "" : (roll > 0 ? " dcha." : " izda."));
            Rect box = new Rect(r.x - 6, r.y - 44, r.width + 12, 40);
            GUI.DrawTexture(box, Tex.Solid(new Color(0.03f, 0.04f, 0.06f, 0.72f)));
            GUI.Label(new Rect(box.x, box.y + 1, box.width, 20), s1, readout);
            GUI.Label(new Rect(box.x, box.y + 19, box.width, 20), s2, readout);
        }

        /// <summary>Horizonte (cielo, tierra, línea roja, escala de 5 en 5 grados y marca de inclinación) en una textura.</summary>
        static void UpdateDisc(float pitch, float roll, float Rin, float degPx)
        {
            if (disc == null)
            {
                disc = new Texture2D(DiscRes, DiscRes, TextureFormat.RGBA32, false) { name = "HR_inclinometro", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                discPx = new Color32[DiscRes * DiscRes];
            }
            if (Mathf.Abs(pitch - lastPitch) < 0.05f && Mathf.Abs(roll - lastRoll) < 0.05f) return;
            lastPitch = pitch; lastRoll = roll;
            float half = DiscRes * 0.5f, pxR = half;              // 1 unidad = R
            float phi = roll * Mathf.Deg2Rad, cs = Mathf.Cos(phi), sn = Mathf.Sin(phi);
            float h = -pitch * degPx;                               // altura del horizonte (morro arriba: baja)
            Color sky = new Color(0.30f, 0.58f, 0.86f), skyHi = new Color(0.12f, 0.33f, 0.62f);
            Color ground = new Color(0.50f, 0.34f, 0.19f), groundLo = new Color(0.30f, 0.19f, 0.10f);
            Color white = new Color(0.97f, 0.97f, 0.97f), redC = new Color(0.95f, 0.15f, 0.10f);
            float pix = 1f / pxR;                                   // tamaño de un píxel en unidades de R
            for (int y = 0; y < DiscRes; y++)
                for (int x = 0; x < DiscRes; x++)
                {
                    float X = (x + 0.5f - half) / pxR, Y = (y + 0.5f - half) / pxR;   // Y hacia arriba
                    float rr = Mathf.Sqrt(X * X + Y * Y);
                    float a = Mathf.Clamp01((Rin + 0.01f - rr) / pix + 0.5f);
                    if (a <= 0f) { discPx[y * DiscRes + x] = new Color32(0, 0, 0, 0); continue; }
                    // coordenadas del horizonte: u a lo largo, v hacia arriba
                    float u = X * cs + Y * sn, v = -X * sn + Y * cs;
                    float dv = v - h;
                    Color col = dv > 0 ? Color.Lerp(sky, skyHi, Mathf.Clamp01(dv / 1.2f)) : Color.Lerp(ground, groundLo, Mathf.Clamp01(-dv / 1.2f));
                    // escala de pendiente: rayas cada 5º (largas cada 10º)
                    float deg = dv / degPx;
                    float nearest = Mathf.Round(deg / 5f) * 5f;
                    if (Mathf.Abs(nearest) > 0.1f && Mathf.Abs(nearest) <= 30f)
                    {
                        bool big = Mathf.Abs(nearest) % 10f < 0.1f;
                        float len = big ? 0.20f : 0.10f;
                        float dist = Mathf.Abs(deg - nearest) * degPx;
                        float cov = Mathf.Clamp01((0.012f - dist) / pix + 0.5f) * Mathf.Clamp01((len - Mathf.Abs(u)) / pix + 0.5f);
                        if (cov > 0) col = Color.Lerp(col, white, cov * 0.95f);
                    }
                    // línea del horizonte (roja, como en los inclinómetros de verdad)
                    float hl = Mathf.Clamp01((0.016f - Mathf.Abs(dv)) / pix + 0.5f);
                    if (hl > 0) col = Color.Lerp(col, redC, hl);
                    // marca de inclinación lateral: triángulo blanco arriba, gira con el horizonte
                    float tv = v - (Rin - 0.13f), tw = 0.05f * Mathf.Clamp01(1f - tv / 0.09f);
                    if (tv > 0f && tv < 0.09f)
                    {
                        float tc = Mathf.Clamp01((tw - Mathf.Abs(u)) / pix + 0.5f);
                        if (tc > 0) col = Color.Lerp(col, white, tc);
                    }
                    col.a = a;
                    discPx[y * DiscRes + x] = col;
                }
            disc.SetPixels32(discPx);
            disc.Apply(false);
        }

        // ------------------------------------------------------------------ texturas fijas
        static float Cov(float edgeDist) { return Mathf.Clamp01(edgeDist + 0.5f); }

        /// <summary>
        /// Bisel de la brújula / del inclinómetro: aro oscuro con borde metálico, transparente por dentro.
        /// Brújula: marca naranja fija arriba (el rumbo). Inclinómetro: escala lateral (0, 10, 20, 30, 45º) y el
        /// coche visto por detrás en el centro (fijo: el horizonte se mueve detrás de él).
        /// </summary>
        static Texture2D MakeBezel(bool incl)
        {
            const int S = 256;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, true) { name = incl ? "HR_bisel_inclinometro" : "HR_bisel_brujula", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var px = new Color32[S * S];
            float ctr = (S - 1) * 0.5f, Rp = S * 0.5f;
            Color orange = new Color(1f, 0.62f, 0.10f), white = new Color(0.95f, 0.95f, 0.95f), dark = new Color(0.06f, 0.06f, 0.07f);
            float rin = incl ? 0.80f : 0.0f;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = x - ctr, dy = y - ctr, dist = Mathf.Sqrt(dx * dx + dy * dy), rn = dist / Rp;
                    float alpha = Cov((1f - rn) * Rp);
                    Color col = new Color(0, 0, 0, 0);
                    float X = dx / Rp, Y = dy / Rp;
                    if (alpha > 0f)
                    {
                        if (incl)
                        {
                            // aro oscuro entre el horizonte y el borde
                            float ring = Cov((rn - rin) * Rp);
                            if (ring > 0) col = Color.Lerp(col, Color.Lerp(new Color(0.12f, 0.13f, 0.15f), new Color(0.04f, 0.04f, 0.05f), (rn - rin) / (1f - rin)), ring);
                            // escala lateral en el arco de arriba: rayas cada 5º hasta 45º (largas en 0, 10, 20, 30, 45)
                            float ang = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;        // 0 = arriba
                            if (Mathf.Abs(ang) <= 47f)
                            {
                                float nearest = Mathf.Round(ang / 5f) * 5f;
                                if (Mathf.Abs(nearest) <= 45f)
                                {
                                    bool big = Mathf.Abs(nearest) % 10f < 0.1f || Mathf.Abs(nearest) == 45f;
                                    float off = Mathf.Abs(ang - nearest) * Mathf.Deg2Rad * dist;
                                    float r1 = big ? 0.91f : 0.87f;
                                    float tc = Cov(Mathf.Min((big ? 1.8f : 1.1f) - off, Mathf.Min((rn - rin) * Rp, (r1 - rn) * Rp)));
                                    if (tc > 0) col = Color.Lerp(col, nearest == 0f ? orange : white, tc);
                                }
                            }
                            // coche visto por detrás, fijo en el centro (naranja), con las "alas" de referencia
                            float body = Mathf.Min(Mathf.Min((0.30f - Mathf.Abs(X)) * Rp, (Y + 0.10f) * Rp), (0.02f - Y) * Rp);
                            float cabHalf = Mathf.Lerp(0.24f, 0.15f, Mathf.Clamp01((Y - 0.02f) / 0.12f));
                            float cab = Mathf.Min(Mathf.Min((cabHalf - Mathf.Abs(X)) * Rp, (Y - 0.02f) * Rp), (0.14f - Y) * Rp);
                            float win = Mathf.Min(Mathf.Min((cabHalf - 0.04f - Mathf.Abs(X)) * Rp, (Y - 0.045f) * Rp), (0.115f - Y) * Rp);
                            float wheel = Mathf.Min(Mathf.Min((0.29f - Mathf.Abs(X)) * Rp, (Mathf.Abs(X) - 0.19f) * Rp), Mathf.Min((Y + 0.17f) * Rp, (-0.09f - Y) * Rp));
                            float wing = Mathf.Min(Mathf.Min((Mathf.Abs(X) - 0.40f) * Rp, (0.64f - Mathf.Abs(X)) * Rp), (0.018f - Mathf.Abs(Y + 0.04f)) * Rp);
                            float dot = (0.025f - Mathf.Sqrt(X * X + (Y + 0.04f) * (Y + 0.04f))) * Rp;
                            if (Cov(wheel) > 0) col = Color.Lerp(col, dark, Cov(wheel));
                            float sil = Mathf.Max(body, cab);
                            if (Cov(sil) > 0) col = Color.Lerp(col, orange, Cov(sil));
                            if (Cov(win) > 0) col = Color.Lerp(col, new Color(0.15f, 0.12f, 0.08f), Cov(win) * 0.9f);
                            if (Cov(wing) > 0) col = Color.Lerp(col, orange, Cov(wing));
                            if (Cov(dot) > 0) col = Color.Lerp(col, white, Cov(dot));
                        }
                        else
                        {
                            // brújula: marca fija del rumbo arriba (triángulo naranja apuntando al centro)
                            float tv = (Y - 0.80f) / 0.13f;                          // 0..1 de la punta a la base
                            if (tv > 0f && tv < 1f)
                            {
                                float tc = Cov((0.07f * tv - Mathf.Abs(X)) * Rp);
                                if (tc > 0) col = Color.Lerp(col, orange, tc);
                            }
                        }
                        // borde metálico (más claro arriba)
                        float bez = Cov(Mathf.Min((rn - 0.935f) * Rp, (1f - rn) * Rp + 0.5f));
                        if (bez > 0)
                        {
                            float lit = Mathf.Clamp01(0.5f + 0.5f * dy / Rp);
                            Color metal = Color.Lerp(new Color(0.22f, 0.23f, 0.25f), new Color(0.82f, 0.84f, 0.87f), lit);
                            col = Color.Lerp(col, metal, bez);
                            col.a = Mathf.Max(col.a, bez);
                        }
                        col.a *= alpha;
                    }
                    px[y * S + x] = col;
                }
            t.SetPixels32(px);
            t.Apply(true, true);
            return t;
        }

        /// <summary>Rosa de la brújula (gira): fondo oscuro, rayas cada 5º/10º/45º y triángulo rojo en el norte.</summary>
        static Texture2D MakeRose()
        {
            const int S = 256;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, true) { name = "HR_rosa", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var px = new Color32[S * S];
            float ctr = (S - 1) * 0.5f, Rp = S * 0.5f;
            Color white = new Color(0.95f, 0.95f, 0.95f), redC = new Color(0.95f, 0.22f, 0.15f);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = x - ctr, dy = y - ctr, dist = Mathf.Sqrt(dx * dx + dy * dy), rn = dist / Rp;
                    float alpha = Cov((0.94f - rn) * Rp);
                    if (alpha <= 0f) { px[y * S + x] = new Color32(0, 0, 0, 0); continue; }
                    Color col = Color.Lerp(new Color(0.12f, 0.13f, 0.15f), new Color(0.03f, 0.03f, 0.04f), Mathf.Pow(rn, 1.6f));
                    float ang = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;          // 0 = norte (arriba), horario
                    if (ang < 0) ang += 360f;
                    float nearest = Mathf.Round(ang / 5f) * 5f;
                    float off = Mathf.Abs(ang - nearest) * Mathf.Deg2Rad * dist;
                    int nd = Mathf.RoundToInt(nearest) % 360;
                    bool major = nd % 45 == 0, mid = nd % 10 == 0;
                    float r0 = major ? 0.70f : (mid ? 0.77f : 0.81f);
                    float hw = major ? 2.0f : (mid ? 1.3f : 0.9f);
                    float tc = Cov(Mathf.Min(hw - off, Mathf.Min((rn - r0) * Rp, (0.90f - rn) * Rp)));
                    if (tc > 0) col = Color.Lerp(col, white, tc * (major || mid ? 1f : 0.7f));
                    // triángulo rojo del norte, por dentro de las rayas
                    float X = dx / Rp, Y = dy / Rp;
                    float tv = (0.70f - Y) / 0.08f;          // punta hacia fuera, encima de la N
                    if (tv > 0f && tv < 1f)
                    {
                        float nc = Cov((0.06f * tv - Mathf.Abs(X)) * Rp);
                        if (nc > 0) col = Color.Lerp(col, redC, nc);
                    }
                    col.a = alpha * 0.96f;
                    px[y * S + x] = col;
                }
            t.SetPixels32(px);
            t.Apply(true, true);
            return t;
        }
    }
}
