using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Fondo de los menús: la cámara sobrevuela Hernani como en una película, a unos 300 m sobre el terreno
    /// y a 300 km/h, dando un paseo aleatorio de barrio en barrio sin salirse del área cargada.
    /// </summary>
    public class Flyover : MonoBehaviour
    {
        public World world;
        public Camera cam;

        public const float Speed = 300f / 3.6f;    // 83 m/s
        public const float Height = 300f;          // sobre el terreno
        const float EdgeMargin = 1500f;             // no acercarse más a los bordes del área

        bool on, started;
        Vector2 pos;                // x, z
        float heading;              // grados (0 = norte, 90 = este)
        float turnRate, bank, alt, altVel;
        Vector2 target;
        float nextPick, t;
        System.Random rnd = new System.Random();

        // ajustes que se cambian durante el vuelo y se restauran al terminar
        float savedShadow, savedFogStart, savedFogEnd, savedTreeDist;
        bool savedFog;

        public bool Active { get { return on; } }

        public void Begin()
        {
            if (on) return;
            on = true;
            enabled = true;
            if (!started) StartFlight();
            savedShadow = QualitySettings.shadowDistance;
            savedFog = RenderSettings.fog;
            savedFogStart = RenderSettings.fogStartDistance;
            savedFogEnd = RenderSettings.fogEndDistance;
            QualitySettings.shadowDistance = 700f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 1000f;
            RenderSettings.fogEndDistance = 4800f;
            if (world.trees != null)
            {
                savedTreeDist = world.trees.drawDistance;
                world.trees.drawAll = false;
                world.trees.drawDistance = 1700f;
            }
            var cc = cam.GetComponent<ChaseCamera>();
            if (cc != null) cc.enabled = false;
            cam.orthographic = false;
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 2f;
            cam.farClipPlane = 6000f;
            Apply(0f);
        }

        public void End()
        {
            if (!on) return;
            on = false;
            QualitySettings.shadowDistance = savedShadow;
            RenderSettings.fog = savedFog;
            RenderSettings.fogStartDistance = savedFogStart;
            RenderSettings.fogEndDistance = savedFogEnd;
            if (world.trees != null) world.trees.drawDistance = savedTreeDist > 0 ? savedTreeDist : 900f;
        }

        void StartFlight()
        {
            started = true;
            // se empieza sobre un barrio al azar, mirando hacia el centro del área
            var zones = world.data.scenery.zones;
            Vector2 p = new Vector2(world.TownCenter.x, world.TownCenter.z);
            if (zones != null && zones.Length > 0)
            {
                var z = zones[rnd.Next(zones.Length)];
                p = new Vector2(z.x, z.z);
            }
            pos = ClampInside(p, EdgeMargin);
            Vector2 c = new Vector2(world.sizeX * 0.5f, world.sizeZ * 0.5f);
            heading = Mathf.Atan2(c.x - pos.x, c.y - pos.y) * Mathf.Rad2Deg + Rand(-60f, 60f);
            PickTarget();
            alt = GroundAhead() + Height;
        }

        float Rand(float a, float b) { return a + (float)rnd.NextDouble() * (b - a); }

        Vector2 ClampInside(Vector2 p, float m)
        {
            return new Vector2(Mathf.Clamp(p.x, m, world.sizeX - m), Mathf.Clamp(p.y, m, world.sizeZ - m));
        }

        /// <summary>Próximo destino: un barrio o lugar conocido (7 de cada 10 veces) o un punto cualquiera del área.</summary>
        void PickTarget()
        {
            var zones = world.data.scenery.zones;
            Vector2 cand = pos;
            for (int i = 0; i < 12; i++)
            {
                if (zones != null && zones.Length > 0 && rnd.NextDouble() < 0.7)
                {
                    var z = zones[rnd.Next(zones.Length)];
                    cand = new Vector2(z.x, z.z);
                }
                else cand = new Vector2(Rand(0, world.sizeX), Rand(0, world.sizeZ));
                cand = ClampInside(cand, EdgeMargin);
                float d = Vector2.Distance(cand, pos);
                // ni demasiado cerca ni justo detrás (giros de película, no de helicóptero)
                float ang = Mathf.Abs(Mathf.DeltaAngle(heading, Mathf.Atan2(cand.x - pos.x, cand.y - pos.y) * Mathf.Rad2Deg));
                if (d > 1200f && ang < 120f) break;
            }
            target = cand;
            nextPick = t + Rand(25f, 45f);
        }

        float Ground(Vector2 p)
        {
            Vector3 q = new Vector3(Mathf.Clamp(p.x, 1, world.sizeX - 1), 0, Mathf.Clamp(p.y, 1, world.sizeZ - 1));
            return world.GroundHeight(q);
        }

        /// <summary>Cota del terreno que viene por delante (lo más alto, rebajando lo lejano para no subir antes de tiempo).</summary>
        float GroundAhead()
        {
            Vector2 f = new Vector2(Mathf.Sin(heading * Mathf.Deg2Rad), Mathf.Cos(heading * Mathf.Deg2Rad));
            Vector2 r = new Vector2(f.y, -f.x);
            float h = Ground(pos);
            float[] ds = { 120f, 300f, 550f, 900f, 1300f };
            foreach (float d in ds)
                for (int s = -1; s <= 1; s++)
                {
                    float g = Ground(pos + f * d + r * (s * 180f)) - d * 0.06f;
                    if (g > h) h = g;
                }
            return h;
        }

        void LateUpdate()
        {
            if (!on || world == null || cam == null) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            t += dt;
            Step(dt);
            Apply(dt);
        }

        void Step(float dt)
        {
            if (Vector2.Distance(pos, target) < 700f || t > nextPick) PickTarget();
            float want = Mathf.Atan2(target.x - pos.x, target.y - pos.y) * Mathf.Rad2Deg;

            // cerca de un borde y yendo hacia fuera: volver hacia el centro
            Vector2 c = new Vector2(world.sizeX * 0.5f, world.sizeZ * 0.5f);
            Vector2 f = new Vector2(Mathf.Sin(heading * Mathf.Deg2Rad), Mathf.Cos(heading * Mathf.Deg2Rad));
            float em = EdgeMargin * 0.8f;
            bool nearEdge = pos.x < em || pos.y < em || pos.x > world.sizeX - em || pos.y > world.sizeZ - em;
            if (nearEdge && Vector2.Dot(f, c - pos) < 0f)
            {
                want = Mathf.Atan2(c.x - pos.x, c.y - pos.y) * Mathf.Rad2Deg;
                target = ClampInside(c + (pos - c) * 0.2f, EdgeMargin);
            }

            // giro suave: máximo ~7º/s y con aceleración angular limitada (como un dron de cine)
            float diff = Mathf.DeltaAngle(heading, want);
            float desired = Mathf.Clamp(diff * 0.22f, -7f, 7f);
            turnRate = Mathf.MoveTowards(turnRate, desired, 2.5f * dt);
            heading = Mathf.Repeat(heading + turnRate * dt, 360f);

            f = new Vector2(Mathf.Sin(heading * Mathf.Deg2Rad), Mathf.Cos(heading * Mathf.Deg2Rad));
            pos += f * Speed * dt;
            pos = ClampInside(pos, 50f);

            float goal = GroundAhead() + Height;
            alt = Mathf.SmoothDamp(alt, goal, ref altVel, 3.5f, 60f, dt);
            float floor = Ground(pos) + Height * 0.55f;
            if (alt < floor) { alt = floor; altVel = Mathf.Max(altVel, 0f); }

            bank = Mathf.Lerp(bank, -turnRate * 1.8f, 1f - Mathf.Exp(-1.5f * dt));
        }

        void Apply(float dt)
        {
            // mirada hacia delante y abajo, con un balanceo lento para que no sea monótono
            float pitch = 21f + 5f * Mathf.Sin(t * 0.11f) - Mathf.Clamp(altVel * 0.25f, -6f, 6f);
            float yaw = heading + 6f * Mathf.Sin(t * 0.07f + 1.3f);
            cam.transform.position = new Vector3(pos.x, alt, pos.y);
            cam.transform.rotation = Quaternion.Euler(pitch, yaw, bank);
        }
    }
}
