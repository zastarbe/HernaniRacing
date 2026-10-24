using UnityEngine;

namespace HernaniRacing
{
    /// <summary>Genera coches low poly en código (carrocería, cabina, ruedas, faros).</summary>
    public static class LowPolyCar
    {
        static Mesh wheelMesh;

        public static CarController Create(string name, Color color, bool isPlayer)
        {
            var go = new GameObject(name);
            var rb = go.AddComponent<Rigidbody>();
            var car = go.AddComponent<CarController>();

            // ---------------- carrocería
            var body = new MeshBuilder(4);   // 0 pintura, 1 cristal, 2 faros, 3 pilotos
            Vector2[] bodyProfile =
            {
                new Vector2(-2.05f, 0.34f), new Vector2(2.05f, 0.34f), new Vector2(2.12f, 0.62f),
                new Vector2(1.98f, 0.88f), new Vector2(0.85f, 1.0f), new Vector2(-1.65f, 1.03f), new Vector2(-2.08f, 0.92f)
            };
            ExtrudeX(body, bodyProfile, 0.9f, null, 0);
            Vector2[] cabin =
            {
                new Vector2(-1.38f, 1.0f), new Vector2(0.78f, 1.0f), new Vector2(0.08f, 1.47f), new Vector2(-1.12f, 1.49f)
            };
            // la cara superior (índice 2) es el techo pintado; el resto cristal
            ExtrudeX(body, cabin, 0.74f, new[] { 1, 1, 0, 1 }, 1, 0.66f);
            // faros y pilotos
            body.Box(2, new Vector3(-0.6f, 0.72f, 2.08f), Vector3.right, Vector3.up, Vector3.forward, new Vector3(0.2f, 0.07f, 0.04f));
            body.Box(2, new Vector3(0.6f, 0.72f, 2.08f), Vector3.right, Vector3.up, Vector3.forward, new Vector3(0.2f, 0.07f, 0.04f));
            body.Box(3, new Vector3(-0.62f, 0.8f, -2.08f), Vector3.right, Vector3.up, Vector3.forward, new Vector3(0.22f, 0.06f, 0.03f));
            body.Box(3, new Vector3(0.62f, 0.8f, -2.08f), Vector3.right, Vector3.up, Vector3.forward, new Vector3(0.22f, 0.06f, 0.03f));
            // alerón
            body.Box(0, new Vector3(0, 1.12f, -1.95f), Vector3.right, Vector3.up, Vector3.forward, new Vector3(0.85f, 0.03f, 0.16f));
            var bodyGo = new GameObject("Carroceria");
            bodyGo.transform.SetParent(go.transform, false);
            bodyGo.AddComponent<MeshFilter>().sharedMesh = body.Build(name + "_body");
            bodyGo.AddComponent<MeshRenderer>().sharedMaterials = new[]
            {
                Mat.Color(color, 0.65f), Mat.Color(new Color(0.08f, 0.1f, 0.13f), 0.9f),
                Mat.Emissive(new Color(1f, 0.97f, 0.85f)), Mat.Emissive(new Color(0.9f, 0.08f, 0.05f))
            };

            var col = go.AddComponent<BoxCollider>();
            col.center = new Vector3(0, 0.72f, 0);
            col.size = new Vector3(1.78f, 0.78f, 4.15f);
            var col2 = go.AddComponent<BoxCollider>();
            col2.center = new Vector3(0, 1.22f, -0.3f);
            col2.size = new Vector3(1.4f, 0.5f, 2.0f);
#if UNITY_6000_0_OR_NEWER
            var pm = new PhysicsMaterial("car") { dynamicFriction = 0.15f, staticFriction = 0.15f, bounciness = 0.1f, frictionCombine = PhysicsMaterialCombine.Minimum };
#else
            var pm = new PhysicMaterial("car") { dynamicFriction = 0.15f, staticFriction = 0.15f, bounciness = 0.1f, frictionCombine = PhysicMaterialCombine.Minimum };
#endif
            col.sharedMaterial = pm; col2.sharedMaterial = pm;

            // ---------------- ruedas
            if (wheelMesh == null) wheelMesh = BuildWheel();
            var wheelMats = new[] { Mat.Color(new Color(0.07f, 0.07f, 0.07f), 0.1f), Mat.Color(new Color(0.75f, 0.75f, 0.78f), 0.6f) };
            float wx = 0.8f, wzF = 1.32f, wzR = -1.3f, wy = 0.68f;
            car.wheelAnchors = new[] { new Vector3(-wx, wy, wzF), new Vector3(wx, wy, wzF), new Vector3(-wx, wy, wzR), new Vector3(wx, wy, wzR) };
            string[] wn = { "Rueda_DI", "Rueda_DD", "Rueda_TI", "Rueda_TD" };
            for (int i = 0; i < 4; i++)
            {
                var w = new GameObject(wn[i]);
                w.transform.SetParent(go.transform, false);
                w.transform.localPosition = car.wheelAnchors[i] + Vector3.down * 0.34f;
                var holder = new GameObject("mesh");
                holder.transform.SetParent(w.transform, false);
                if (i % 2 == 0) holder.transform.localRotation = Quaternion.Euler(0, 180, 0);
                holder.AddComponent<MeshFilter>().sharedMesh = wheelMesh;
                holder.AddComponent<MeshRenderer>().sharedMaterials = wheelMats;
                car.wheelVisuals[i] = w.transform;
            }

            if (isPlayer) go.AddComponent<EngineAudio>().car = car;
            return car;
        }

        /// <summary>Extruye un perfil convexo (z,y) a lo ancho (eje X).</summary>
        static void ExtrudeX(MeshBuilder mb, Vector2[] prof, float hw, int[] faceSub, int sideSub, float topHw = -1)
        {
            if (topHw < 0) topHw = hw;
            int n = prof.Length;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in prof) { minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y); }
            System.Func<Vector2, float> width = p => Mathf.Lerp(hw, topHw, Mathf.InverseLerp(minY, maxY, p.y));
            Vector2 c = Vector2.zero;
            foreach (var p in prof) c += p;
            c /= n;
            // laterales (abanico)
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 cc = new Vector3(s * width(c), c.y, c.x);
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = prof[i], b = prof[(i + 1) % n];
                    mb.FlatTri(sideSub, cc, new Vector3(s * width(a), a.y, a.x), new Vector3(s * width(b), b.y, b.x),
                        new Vector3(s, 0, 0), Vector2.zero, Vector2.zero, Vector2.zero);
                }
            }
            // contorno
            for (int i = 0; i < n; i++)
            {
                Vector2 a = prof[i], b = prof[(i + 1) % n];
                Vector2 d = b - a;
                Vector2 nrm = new Vector2(d.y, -d.x);   // perfil antihorario en (z,y) => normal exterior
                Vector3 facing = new Vector3(0, nrm.y, nrm.x);
                int sub = faceSub != null ? faceSub[i % faceSub.Length] : sideSub;
                mb.Quad(sub, new Vector3(-width(a), a.y, a.x), new Vector3(width(a), a.y, a.x),
                    new Vector3(width(b), b.y, b.x), new Vector3(-width(b), b.y, b.x), facing,
                    Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            }
        }

        static Mesh BuildWheel()
        {
            var mb = new MeshBuilder(2);
            const int seg = 10;
            float r = 0.34f, w = 0.13f;
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2 / seg, a1 = (i + 1) * Mathf.PI * 2 / seg;
                Vector3 p0 = new Vector3(0, Mathf.Cos(a0) * r, Mathf.Sin(a0) * r);
                Vector3 p1 = new Vector3(0, Mathf.Cos(a1) * r, Mathf.Sin(a1) * r);
                Vector3 o = Vector3.right * w;
                mb.Quad(0, p0 - o, p1 - o, p1 + o, p0 + o, (p0 + p1), Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
                mb.FlatTri(1, o * 1.01f, p0 * 0.7f + o * 1.01f, p1 * 0.7f + o * 1.01f, Vector3.right, Vector2.zero, Vector2.zero, Vector2.zero);
                mb.Quad(0, p0 * 0.7f + o, p0 + o, p1 + o, p1 * 0.7f + o, Vector3.right, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
                mb.FlatTri(0, -o, p0 - o, p1 - o, Vector3.left, Vector2.zero, Vector2.zero, Vector2.zero);
            }
            return mb.Build("wheel");
        }
    }

    /// <summary>
    /// Sonido de motor sintetizado (no requiere clips de audio). En PC se genera muestra a muestra
    /// (OnAudioFilterRead); en el navegador ese método no existe, así que se crea un bucle de 1 s con el mismo
    /// timbre y se cambia su tono (pitch). Al terminar la carrera, ShutDown() apaga el motor: el sonido baja de
    /// tono y de volumen a medida que el coche se para y, ya parado, se apaga del todo.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class EngineAudio : MonoBehaviour
    {
        public CarController car;
        public float volume = 0.18f;
        float phase, phase2, freq = 40f, targetFreq = 40f, gain;
        int sampleRate;
        readonly System.Random rnd = new System.Random();
        AudioSource src;

        // apagado del motor (tras la meta)
        bool shutdown;
        float shutV0 = 1f, shutT, idleFade = 1f;
        public bool IsOff { get { return shutdown && idleFade <= 0f; } }

#if UNITY_WEBGL && !UNITY_EDITOR
        static readonly bool UseClip = true;
#else
        static readonly bool UseClip = false;
#endif
        const float ClipBase = 80f;     // Hz del bucle (80 ciclos exactos en 1 s: empalme sin chasquidos)
        static AudioClip loopClip;

        void Start()
        {
            sampleRate = AudioSettings.outputSampleRate;
            src = GetComponent<AudioSource>();
            src.playOnAwake = true;
            src.spatialBlend = 0f;
            src.loop = true;
            if (UseClip)
            {
                src.clip = LoopClip();
                src.volume = 0f;
            }
            src.Play();
        }

        /// <summary>Bucle de 1 s con la misma mezcla (sierra + cuadrada a media frecuencia + ruido) a 80 Hz.</summary>
        static AudioClip LoopClip()
        {
            if (loopClip != null) return loopClip;
            const int rate = 44100;
            var data = new float[rate];
            var r = new System.Random(5);
            for (int i = 0; i < rate; i++)
            {
                float ph = (i * ClipBase / rate) % 1f;
                float ph2 = (i * ClipBase * 0.5f / rate) % 1f;
                float saw = ph * 2f - 1f;
                float sq = ph2 < 0.5f ? 0.6f : -0.6f;
                float noise = ((float)r.NextDouble() * 2f - 1f) * 0.08f;
                data[i] = saw * 0.5f + sq * 0.4f + noise;
            }
            loopClip = AudioClip.Create("HR_motor", rate, 1, rate, false);
            loopClip.SetData(data, 0);
            return loopClip;
        }

        /// <summary>Apagar el motor: el ruido baja con la velocidad y se apaga al pararse.</summary>
        public void ShutDown()
        {
            if (shutdown) return;
            shutdown = true;
            shutT = 0f;
            idleFade = 1f;
            shutV0 = Mathf.Max(car != null ? Mathf.Abs(car.ForwardSpeed) : 0f, 4f);
        }

        void Update()
        {
            if (car == null) return;
            float sp = Mathf.Abs(car.ForwardSpeed);
            // 5 marchas simuladas
            float gearSpan = car.maxSpeed / 5f;
            float inGear = (sp % gearSpan) / gearSpan;
            int gear = Mathf.Min(4, (int)(sp / gearSpan));
            float rpm = 0.25f + 0.75f * Mathf.Clamp01(gear == 0 ? sp / gearSpan : 0.35f + inGear * 0.65f);
            float load = Mathf.Clamp01(car.throttle);
            float level = 1f;
            if (shutdown)
            {
                shutT += Time.deltaTime;
                // sin gas (el motor ya no empuja) y cada vez más bajo a medida que el coche se para
                load = 0f;
                float k = Mathf.Clamp01(sp / shutV0);
                // ya casi parado (o pasado un rato): el motor se apaga del todo en ~1,2 s
                if (sp < 1.2f || shutT > 12f) idleFade = Mathf.MoveTowards(idleFade, 0f, Time.deltaTime / 1.2f);
                rpm = Mathf.Lerp(0.12f, rpm, k) * (0.6f + 0.4f * idleFade);
                level = (0.25f + 0.75f * k) * idleFade;
            }
            targetFreq = 38f + rpm * 120f;
            float want = volume * (0.45f + 0.55f * load) * level;
            gain = Mathf.Lerp(gain, want, Time.deltaTime * (shutdown ? 3f : 5f));
            if (shutdown && idleFade <= 0f && gain < 0.002f) gain = 0f;
            if (UseClip && src != null)
            {
                freq = Mathf.Lerp(freq, targetFreq, 1f - Mathf.Exp(-Time.deltaTime * 4f));
                src.pitch = Mathf.Clamp(freq / ClipBase, 0.2f, 3f);
                src.volume = Mathf.Clamp01(gain * 1.6f);
            }
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (UseClip || sampleRate == 0) return;
            float g = gain;
            for (int i = 0; i < data.Length; i += channels)
            {
                freq += (targetFreq - freq) * 0.0005f;
                phase += freq / sampleRate;
                phase2 += freq * 0.5f / sampleRate;
                if (phase > 1) phase -= 1;
                if (phase2 > 1) phase2 -= 1;
                float saw = phase * 2f - 1f;
                float sq = phase2 < 0.5f ? 0.6f : -0.6f;
                float noise = ((float)rnd.NextDouble() * 2f - 1f) * 0.08f;
                float s = (saw * 0.5f + sq * 0.4f + noise) * g;
                for (int c = 0; c < channels; c++) data[i + c] = s;
            }
        }
    }
}
