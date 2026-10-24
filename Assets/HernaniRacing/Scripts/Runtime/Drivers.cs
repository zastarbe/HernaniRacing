using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>Piloto humano: lee teclado/mando y lo pasa al coche.</summary>
    public class PlayerDriver : MonoBehaviour
    {
        public CarController car;

        void Update()
        {
            if (car == null) return;
            car.throttle = GameInput.Throttle;
            car.brake = GameInput.Brake;
            car.steer = GameInput.Steer;
            car.handbrake = GameInput.Handbrake;
        }
    }

    /// <summary>
    /// Piloto IA: persecución pura sobre la trazada del circuito + perfil de velocidad por curvatura.
    /// Elige en cada momento el "carril" lateral más libre mirando a los demás rivales, al tráfico (también el
    /// que viene de frente) y a los peatones que cruzan: adelanta sólo si cabe y, si no, se queda detrás
    /// guardando la distancia. Tras cruzar la meta (coolDown) frena poco a poco y se aparta a la derecha.
    /// </summary>
    public class AIDriver : MonoBehaviour
    {
        public CarController car;
        public Circuit circuit;
        public float skill = 0.9f;          // fracción de la velocidad máxima segura
        public float lane;                  // desplazamiento lateral preferido (m)
        public List<CarController> others;
        /// <summary>Carrera terminada: frena suavemente hasta pararse en el lado derecho.</summary>
        public bool coolDown;
        public float coolDownTime = 7f;
        float coolT = -1f, coolV0;
        int idx = -1;
        float laneNow;
        /// <summary>Tiene un coche delante que le tapa el paso (no cuenta como atascado).</summary>
        public bool Blocked { get; private set; }
        /// <summary>Ya se ha parado después de la meta.</summary>
        public bool Parked { get; private set; }

        struct Obs { public float gap, lat, v, hw, hl; }
        readonly List<Obs> obs = new List<Obs>(48);
        const int NC = 9;
        readonly float[] cand = new float[NC];

        void AddObstacle(Vector3 q, Vector3 vq, float hw, float hl, float sMe, Vector3 p, bool pedestrian)
        {
            Vector3 d = q - p;
            if (Mathf.Abs(d.y) > 4.5f) return;                  // puente / túnel: otro nivel
            d.y = 0;
            if (d.sqrMagnitude > 75f * 75f) return;
            int j = Nearest(q);
            float lat = circuit.Lateral(q, j);
            float hwRoad = circuit.width[j] * 0.5f;
            if (Mathf.Abs(lat) > hwRoad + (pedestrian ? 0.2f : 1.5f) + hw) return;   // fuera de mi calzada
            float L = circuit.length;
            float gap = Mathf.Repeat(circuit.ProjectS(q, j) - sMe + L * 0.5f, L) - L * 0.5f;
            if (gap < -15f || gap > 75f) return;
            Vector3 dj = circuit.dir[j]; dj.y = 0; dj.Normalize();
            obs.Add(new Obs { gap = gap, lat = lat, v = Vector3.Dot(vq, dj), hw = hw, hl = hl });
        }

        /// <summary>Muestra de la trazada más cercana a q cerca de la mía (sin preferir delante/detrás).</summary>
        int Nearest(Vector3 q)
        {
            int best = idx; float bd = float.MaxValue;
            float sp = Mathf.Max(0.5f, circuit.SampleSpacing);
            int kb = Mathf.CeilToInt(18f / sp), kf = Mathf.CeilToInt(80f / sp);
            for (int k = -kb; k <= kf; k++)
            {
                int i = circuit.Wrap(idx + k);
                Vector3 c = circuit.pos[i];
                float dd = (c.x - q.x) * (c.x - q.x) + (c.z - q.z) * (c.z - q.z) + Mathf.Abs(c.y - q.y) * 4f;
                if (dd < bd) { bd = dd; best = i; }
            }
            return best;
        }

        void FixedUpdate()
        {
            if (car == null || circuit == null) return;
            if (car.frozen || car.PenaltyLeft > 0f) { car.throttle = 0; car.brake = 1; car.steer = 0; return; }
            float dt = Time.fixedDeltaTime;
            Vector3 p = car.transform.position;
            float d;
            idx = circuit.Closest(p, idx, idx < 0 ? 0 : 20, out d);
            float speed = car.ForwardSpeed;
            float sp = circuit.SampleSpacing;
            float sMe = circuit.ProjectS(p, idx);
            float myHW = car.HalfWidth, myHL = car.HalfLength;

            // --- obstáculos sobre mi calzada ---
            obs.Clear();
            if (others != null)
                foreach (var o in others)
                {
                    if (o == null || o == car || !o.gameObject.activeInHierarchy) continue;
                    AddObstacle(o.transform.position, o.Body != null ? o.Body.Vel() : Vector3.zero, o.HalfWidth, o.HalfLength, sMe, p, false);
                }
            var ts = TrafficSystem.Active;
            if (ts != null)
            {
                foreach (var tcar in ts.cars)
                    if (tcar != null) AddObstacle(tcar.transform.position, tcar.Velocity, 1.0f, tcar.HalfLength, sMe, p, false);
                foreach (var rc in ts.remoteCars)
                    if (rc != null) AddObstacle(rc.transform.position, rc.Velocity, 1.0f, rc.HalfLength > 0.5f ? rc.HalfLength : 2.2f, sMe, p, false);
                foreach (var pd in ts.peds)
                    if (pd != null && !pd.KnockedDown) AddObstacle(pd.transform.position, Vector3.zero, 0.45f, 0.45f, sMe, p, true);
                foreach (var pd in ts.remotePeds)
                    if (pd != null && pd.gameObject.activeSelf) AddObstacle(pd.transform.position, Vector3.zero, 0.45f, 0.45f, sMe, p, true);
            }

            // --- elegir el carril más libre ---
            float maxLane = Mathf.Max(0f, circuit.width[idx] * 0.5f - myHW - 0.25f);
            float pref = coolDown ? maxLane : Mathf.Clamp(lane, -maxLane, maxLane);
            float best = laneNow, bestCost = float.MaxValue;
            for (int k = 0; k < NC; k++)
            {
                float c = maxLane <= 0.01f ? 0f : -maxLane + 2f * maxLane * k / (NC - 1);
                cand[k] = c;
                float cost = Mathf.Abs(c - pref) * 0.25f + Mathf.Abs(c - laneNow) * 0.5f;
                // cambiar de lado cruzando por delante de un obstáculo cercano tampoco vale
                foreach (var o in obs)
                {
                    float need = myHW + o.hw + 0.55f;
                    float sumL = myHL + o.hl;
                    float lo = Mathf.Min(c, laneNow), hi = Mathf.Max(c, laneNow);
                    bool alongside = o.gap > -sumL - 1.5f && o.gap < sumL + 1.5f;
                    if (alongside)
                    {
                        // coche a mi lado: no puedo pasar a través de él para llegar a ese carril
                        if (o.lat + need > lo && o.lat - need < hi)
                        {
                            bool crosses = (c - o.lat) * (laneNow - o.lat) < 0f;     // tendría que atravesarlo
                            cost += crosses ? 60f : 5f + 30f * (1f - Mathf.Clamp01(Mathf.Abs(c - o.lat) / need));
                        }
                        continue;
                    }
                    if (o.gap < 0f) continue;                    // por detrás: no me afecta
                    float latDist = Mathf.Abs(c - o.lat);
                    if (latDist >= need) continue;
                    float g = o.gap - sumL;
                    float closing = Mathf.Max(0f, speed - o.v);
                    float horizon = 10f + closing * 3.5f;
                    if (g > horizon) continue;
                    float w = 1f - Mathf.Clamp01(g / horizon);
                    cost += 3f + 25f * w * (1f - 0.5f * latDist / need) + (o.v < -1f ? 15f * w : 0f);
                }
                if (cost < bestCost - 0.01f) { bestCost = cost; best = c; }
            }
            float rate = Mathf.Abs(speed) > 6f ? 2.6f : 1.4f;
            laneNow = Mathf.MoveTowards(Mathf.Clamp(laneNow, -maxLane, maxLane), best, dt * rate);

            // --- seguir al de delante sin empujarle (también al tráfico y a los peatones) ---
            float followSpeed = float.MaxValue;
            foreach (var o in obs)
            {
                if (o.gap <= 0f) continue;
                float need = myHW + o.hw + 0.35f;
                if (Mathf.Abs(laneNow - o.lat) >= need && Mathf.Abs(best - o.lat) >= need) continue;
                float g = o.gap - (myHL + o.hl);
                if (g < -1f) continue;                           // está a mi lado, no delante
                float vo = Mathf.Max(o.v, 0f);
                float safe = 2.5f + Mathf.Max(speed, 0f) * 0.3f;
                float allow = vo + Mathf.Sqrt(2f * 5.5f * Mathf.Max(0f, g - safe));
                if (g < safe) allow = vo * Mathf.Clamp01(g / safe) - 0.5f;
                followSpeed = Mathf.Min(followSpeed, allow);
            }
            Blocked = followSpeed < 2f;

            float look = Mathf.Clamp(5f + Mathf.Abs(speed) * 0.5f, 7f, 26f);
            float sAhead = idx * sp + look;
            Vector3 target = circuit.PosAt(sAhead);
            Vector3 dir = circuit.DirAt(sAhead); dir.y = 0; dir.Normalize();
            target += new Vector3(dir.z, 0, -dir.x) * laneNow;

            Vector3 local = car.transform.InverseTransformPoint(target);
            float angle = Mathf.Atan2(local.x, Mathf.Max(local.z, 0.1f)) * Mathf.Rad2Deg;
            float maxSteer = Mathf.Lerp(car.maxSteerLow, car.maxSteerHigh, Mathf.Clamp01(Mathf.Abs(speed) / car.maxSpeed));
            car.steer = Mathf.Clamp(angle / maxSteer, -1f, 1f);

            float vt = circuit.maxSpeed[circuit.Wrap(idx + 4)] * skill;
            if (coolDown)
            {
                if (coolT < 0f) { coolT = 0f; coolV0 = Mathf.Max(speed, 4f); }
                coolT += dt;
                vt = Mathf.Min(vt, coolV0 * Mathf.Max(0f, 1f - coolT / coolDownTime));
                if (vt < 0.6f && speed < 1.2f)
                {   // parado a un lado: freno de mano y quieto
                    Parked = true;
                    car.throttle = 0f; car.brake = 0f; car.handbrake = true; car.steer = 0f;
                    return;
                }
            }
            if (followSpeed < vt) vt = Mathf.Max(followSpeed, 0f);
            car.handbrake = false;
            if (vt < 1f && speed < 2f)
            {   // le han tapado el paso: espera parado (freno de mano; el freno normal metería la marcha atrás)
                car.throttle = 0f; car.brake = 0f; car.handbrake = true;
                return;
            }
            if (speed < vt - 1f) { car.throttle = coolDown ? 0.5f : 1f; car.brake = 0f; }
            else if (speed > vt + 1.5f)
            {
                car.throttle = 0f;
                float hard = followSpeed < speed - 6f ? 1f : 0.3f;
                car.brake = speed > 2f ? Mathf.Clamp((speed - vt) / 5f, coolDown ? 0.15f : hard, 1f) : 0f;
            }
            else { car.throttle = coolDown ? 0.2f : 0.45f; car.brake = 0f; }
            if (Mathf.Abs(angle) > 30f && speed > 14f) car.throttle *= 0.4f;
        }

        public void ResetTracking() { idx = -1; laneNow = lane; }

        /// <summary>Activa la vuelta de enfriamiento tras la meta.</summary>
        public void StartCoolDown(float seconds)
        {
            coolDown = true; coolDownTime = seconds; coolT = -1f; Parked = false;
        }
    }

    /// <summary>
    /// Cámara de persecución (C cambia de modo). Siempre por encima del coche y del terreno: nunca mira
    /// el coche desde abajo. Marcha atrás: se coloca delante y ves el coche de frente. Mantén B (o LB en el
    /// mando) para mirar hacia atrás.
    /// </summary>
    public class ChaseCamera : MonoBehaviour
    {
        public Transform target;
        public int mode;
        public World world;
        public bool RearView { get; private set; }
        Vector3 vel;
        float facing = 1f;          // 1 = detrás del coche, -1 = delante (marcha atrás)
        Vector3 smoothFwd = Vector3.forward;
        static readonly Vector3[] offsets = { new Vector3(0, 2.6f, -6.8f), new Vector3(0, 4.2f, -11f), new Vector3(0, 1.25f, 0.3f) };
        static readonly RaycastHit[] hits = new RaycastHit[8];

        Camera cam;

        /// <summary>Cámara de persecución siempre en perspectiva (el mapa de selección la deja cenital/ortográfica).</summary>
        void EnsurePerspective()
        {
            if (cam == null) cam = GetComponent<Camera>();
            if (cam == null) return;
            if (cam.orthographic) cam.orthographic = false;
            if (Mathf.Abs(cam.fieldOfView - 62f) > 0.01f) cam.fieldOfView = 62f;
            if (cam.nearClipPlane > 0.3f) cam.nearClipPlane = 0.2f;
            if (cam.farClipPlane < 4000f) cam.farClipPlane = 4000f;
        }

        void LateUpdate()
        {
            if (target == null) return;
            EnsurePerspective();
            if (GameInput.CameraPressed) mode = (mode + 1) % offsets.Length;
            RearView = GameInput.RearView;
            var rb = target.GetComponent<Rigidbody>();
            var car = target.GetComponent<CarController>();
            Vector3 carFwd = target.forward; carFwd.y = 0;
            if (carFwd.sqrMagnitude < 1e-4f) carFwd = Vector3.forward;
            carFwd.Normalize();

            // marcha atrás: la cámara pasa delante del coche (y vuelve detrás al avanzar)
            float fs = car != null ? car.ForwardSpeed : 0f;
            bool reversing = car != null && car.Reversing && fs < -0.8f;
            float wantFacing = reversing ? -1f : (fs > 1f || car == null ? 1f : facing);
            if (RearView) wantFacing = -1f;
            facing = Mathf.MoveTowards(facing, wantFacing, Time.deltaTime * (RearView ? 20f : 2.2f));

            if (mode == 2 && !RearView)
            {
                // vista desde el capó (mirando atrás si va marcha atrás)
                transform.position = target.TransformPoint(offsets[2]);
                Vector3 look = target.forward * (facing < 0 ? -1f : 1f);
                transform.rotation = Quaternion.LookRotation(look, target.up);
                return;
            }

            // dirección de referencia: el morro del coche, suavizada con la velocidad hacia delante
            Vector3 f = carFwd;
            if (rb != null && fs > 2f)
            {
                Vector3 v = rb.Vel(); v.y = 0;
                if (v.sqrMagnitude > 4f) f = Vector3.Slerp(carFwd, v.normalized, 0.35f);
            }
            smoothFwd = Vector3.Slerp(smoothFwd, f, Time.deltaTime * 8f);
            smoothFwd.y = 0; smoothFwd.Normalize();

            // posición orbitando alrededor del coche según 'facing' (-1 delante, 1 detrás)
            Vector3 off = offsets[Mathf.Min(mode, 1)];
            float ang = (1f - facing) * 90f;   // 0 = detrás, 180 = delante
            Vector3 dir = Quaternion.AngleAxis(ang, Vector3.up) * -smoothFwd;
            Vector3 pivot = target.position + Vector3.up * 1.2f;
            Vector3 desired = target.position + dir * (-off.z) + Vector3.up * off.y;

            // obstáculos (edificios, terreno): acercarse, pero sin bajar
            RaycastHit hit;
            if (Physics.Linecast(pivot, desired, out hit, ~0, QueryTriggerInteraction.Ignore) && hit.rigidbody == null)
            {
                Vector3 p = hit.point + (pivot - hit.point).normalized * 0.4f;
                desired = new Vector3(p.x, Mathf.Max(p.y, desired.y), p.z);
            }
            // siempre por encima del coche (mirando hacia abajo) y del terreno
            float minY = target.position.y + Mathf.Max(1.8f, off.y * 0.75f);
            desired.y = Mathf.Max(desired.y, minY);
            desired.y = Mathf.Max(desired.y, GroundBelow(desired) + 1.4f);

            float smooth = RearView ? 0.02f : 0.09f;
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref vel, smooth);
            if (transform.position.y < minY) transform.position = new Vector3(transform.position.x, minY, transform.position.z);
            Vector3 lookAt = target.position + Vector3.up * 0.9f - dir * 2.5f;
            Vector3 ld = lookAt - transform.position;
            // nunca mirar hacia arriba al coche
            if (ld.y > -0.05f * ld.magnitude) ld.y = -0.05f * ld.magnitude;
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(ld, Vector3.up), Time.deltaTime * (RearView ? 30f : 10f));
        }

        /// <summary>Cota del suelo bajo la cámara (terreno o tablero), sin contar los coches.</summary>
        float GroundBelow(Vector3 p)
        {
            float best = float.MinValue;
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 30f, Vector3.down, hits, 80f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                if (hits[i].rigidbody != null) continue;
                // ignora techos de túnel y tableros que quedan por encima del coche
                if (target != null && hits[i].point.y > target.position.y + 3.0f) continue;
                best = Mathf.Max(best, hits[i].point.y);
            }
            if (best == float.MinValue && world != null) best = world.GroundHeight(p);
            return best;
        }

        public void Snap()
        {
            if (target == null) return;
            EnsurePerspective();
            facing = 1f;
            smoothFwd = target.forward; smoothFwd.y = 0;
            if (smoothFwd.sqrMagnitude < 1e-4f) smoothFwd = Vector3.forward;
            smoothFwd.Normalize();
            var off = offsets[mode == 2 ? 0 : mode];
            transform.position = target.position - smoothFwd * (-off.z) + Vector3.up * off.y;
            transform.LookAt(target.position + Vector3.up * 0.9f);
            vel = Vector3.zero;
        }
    }
}
