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
    /// Piloto IA: persecución pura sobre la trazada del circuito + perfil de velocidad por curvatura,
    /// con desplazamiento lateral para adelantar y evitar a otros coches.
    /// </summary>
    public class AIDriver : MonoBehaviour
    {
        public CarController car;
        public Circuit circuit;
        public float skill = 0.9f;          // fracción de la velocidad máxima segura
        public float lane;                  // desplazamiento lateral preferido (m)
        public List<CarController> others;
        int idx = -1;
        float laneNow;
        /// <summary>Tiene un coche delante que le tapa el paso (no cuenta como atascado).</summary>
        public bool Blocked { get; private set; }

        void FixedUpdate()
        {
            if (car == null || circuit == null) return;
            if (car.frozen) { car.throttle = 0; car.brake = 1; car.steer = 0; return; }
            Vector3 p = car.transform.position;
            float d;
            idx = circuit.Closest(p, idx, idx < 0 ? 0 : 20, out d);
            float speed = car.ForwardSpeed;
            float sp = circuit.SampleSpacing;

            // tráfico: los rivales respetan a los demás coches igual que se espera de ti
            // (sin ventajas: no empujan a fondo por detrás, se apartan del coche que tienen al lado)
            float desiredLane = lane;
            float followSpeed = float.MaxValue;
            if (others != null)
            {
                foreach (var o in others)
                {
                    if (o == null || o == car) continue;
                    Vector3 rel = car.transform.InverseTransformPoint(o.transform.position);
                    float hwL = circuit.width[idx] * 0.5f - 1.3f;
                    if (rel.z > 0 && rel.z < 16f && Mathf.Abs(rel.x) < 2.4f)
                    {
                        // coche delante en mi trayectoria: intento adelantar por el lado libre y, si no cabe,
                        // no le empujo: igualo su velocidad (se me puede tapar el paso)
                        desiredLane = rel.x > 0 ? -hwL : hwL;
                        if (rel.z < 9f)
                        {
                            float ov = Vector3.Dot(o.Body != null ? o.Body.Vel() : Vector3.zero, car.transform.forward);
                            followSpeed = Mathf.Min(followSpeed, ov - 0.5f + (rel.z - 6f) * 0.6f);
                        }
                    }
                    else if (Mathf.Abs(rel.z) < 4.8f && Mathf.Abs(rel.x) < 3.4f)
                    {
                        // coche al lado: me aparto en vez de cerrarle
                        desiredLane = laneNow + (rel.x > 0 ? -2.0f : 2.0f);
                    }
                }
            }
            // coches del tráfico (si está activado): no adelanta a lo loco, frena detrás
            var ts = TrafficSystem.Active;
            if (ts != null)
                foreach (var tcar in ts.cars)
                {
                    if (tcar == null) continue;
                    Vector3 rel = car.transform.InverseTransformPoint(tcar.transform.position);
                    if (rel.z > 0 && rel.z < 20f && Mathf.Abs(rel.x) < 2.4f)
                    {
                        float hwL = circuit.width[idx] * 0.5f - 1.3f;
                        desiredLane = rel.x > 0 ? -hwL : hwL;
                        if (rel.z < 12f)
                            followSpeed = Mathf.Min(followSpeed, Vector3.Dot(tcar.Velocity, car.transform.forward) - 0.5f + (rel.z - 7f) * 0.6f);
                    }
                }
            float maxLane = Mathf.Max(0f, circuit.width[idx] * 0.5f - 1.3f);
            desiredLane = Mathf.Clamp(desiredLane, -maxLane, maxLane);
            laneNow = Mathf.MoveTowards(laneNow, desiredLane, Time.fixedDeltaTime * 1.5f);
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
            if (followSpeed < vt) vt = Mathf.Max(followSpeed, 0f);
            car.handbrake = false;
            if (vt < 1f && speed < 2f)
            {   // le han tapado el paso: espera parado (freno de mano; el freno normal metería la marcha atrás)
                car.throttle = 0f; car.brake = 0f; car.handbrake = true;
                return;
            }
            if (speed < vt - 1f) { car.throttle = 1f; car.brake = 0f; }
            else if (speed > vt + 1.5f) { car.throttle = 0f; car.brake = speed > 2f ? Mathf.Clamp((speed - vt) / 5f, 0.3f, 1f) : 0f; }
            else { car.throttle = 0.45f; car.brake = 0f; }
            if (Mathf.Abs(angle) > 30f && speed > 14f) car.throttle *= 0.4f;
        }

        public void ResetTracking() { idx = -1; laneNow = lane; }
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

        void LateUpdate()
        {
            if (target == null) return;
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
