using UnityEngine;

namespace HernaniRacing
{
    static class RbCompat
    {
        public static Vector3 Vel(this Rigidbody rb)
        {
#if UNITY_6000_0_OR_NEWER
            return rb.linearVelocity;
#else
            return rb.velocity;
#endif
        }

        public static void SetVel(this Rigidbody rb, Vector3 v)
        {
#if UNITY_6000_0_OR_NEWER
            rb.linearVelocity = v;
#else
            rb.velocity = v;
#endif
        }

        public static void SetDamping(this Rigidbody rb, float lin, float ang)
        {
#if UNITY_6000_0_OR_NEWER
            rb.linearDamping = lin; rb.angularDamping = ang;
#else
            rb.drag = lin; rb.angularDrag = ang;
#endif
        }
    }

    /// <summary>
    /// Vehículo arcade con suspensión por raycast (estable en calles con mucha pendiente como las de Hernani).
    /// Las entradas (acelerador, freno, dirección, freno de mano) las fija un piloto (jugador o IA).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class CarController : MonoBehaviour
    {
        [Header("Entradas (-1..1)")]
        public float throttle, brake, steer;
        public bool handbrake;
        public bool frozen;
        /// <summary>Sanción (atropello): segundos que quedan parado. Primero frena hasta pararse y luego cuenta.</summary>
        public float PenaltyLeft { get; private set; }
        public bool PenaltyCounting { get; private set; }
        float penaltyBrakeT;
        public void Penalize(float secs)
        {
            if (PenaltyLeft > 0f) return;
            PenaltyLeft = secs; PenaltyCounting = false; penaltyBrakeT = 0f;
        }
        public void ClearPenalty() { PenaltyLeft = 0f; PenaltyCounting = false; }
        /// <summary>Medio ancho / medio largo de la carrocería (para esquivar).</summary>
        public float HalfWidth { get { return model != null && model.boundsMax != null && model.boundsMax.Length > 0 ? Mathf.Max(0.6f, (model.boundsMax[0] - model.boundsMin[0]) * 0.5f) : 0.95f; } }
        public float HalfLength { get { return model != null && model.boundsMax != null && model.boundsMax.Length > 2 ? Mathf.Max(1.2f, (model.boundsMax[2] - model.boundsMin[2]) * 0.5f) : 2.1f; } }

        [Header("Motor")]
        public float maxSpeed = 44f;          // m/s (~160 km/h)
        public float engineForce = 7800f;     // N
        public float brakeForce = 11000f;     // N
        public float reverseSpeed = 9f;

        [Header("Neumáticos")]
        public float grip = 1.25f;
        public float rearGripFactor = 0.95f;
        public float handbrakeGrip = 0.45f;
        public float maxSteerLow = 34f, maxSteerHigh = 9f;

        [Header("Suspensión")]
        public float restLength = 0.42f;
        public float wheelRadius = 0.34f;
        public float spring = 32000f;
        public float damper = 3200f;
        public float antiRoll = 9000f;

        [Header("Aerodinámica")]
        public float downforce = 1.6f;        // N por (m/s)^2
        public float drag = 0.42f;

        public Transform[] wheelVisuals = new Transform[4];   // FL, FR, RL, RR
        public CarModelData model;            // modelo (null = coche low poly generado)
        /// <summary>Marcha atrás engranada (frenando casi parado).</summary>
        public bool Reversing { get; private set; }

        /// <summary>Distancia del anclaje de la suspensión sobre el centro de la rueda en reposo.</summary>
        public float StaticWheelOffset
        {
            get
            {
                float m = Body != null ? Body.mass : 1150f;
                float comp = Mathf.Clamp(m * 9.81f / 4f / spring, 0.02f, restLength * 0.6f);
                return restLength - comp;
            }
        }

        /// <summary>Ajusta masa, motor, adherencia y suspensión al modelo de coche elegido.</summary>
        public void ApplyModel(CarModelData d)
        {
            if (Body == null) Body = GetComponent<Rigidbody>();
            model = d;
            float m = d.mass > 0 ? d.mass : 1150f;
            Body.mass = m;
            float k = m / 1150f;
            maxSpeed = d.maxSpeed > 0 ? d.maxSpeed : maxSpeed;
            engineForce = d.engine > 0 ? d.engine : engineForce;
            brakeForce = d.brake > 0 ? d.brake : brakeForce;
            grip = d.grip > 0 ? d.grip : grip;
            downforce = d.downforce > 0 ? d.downforce : downforce;
            wheelRadius = d.wheelRadius > 0 ? d.wheelRadius : wheelRadius;
            float h = d.boundsMax != null ? d.boundsMax[1] : 1.4f;
            if (d.IsF1)
            {
                restLength = 0.22f; spring = 52000f * k; damper = 4600f * k; antiRoll = 14000f * k;
                maxSteerLow = 30f; maxSteerHigh = 6f; drag = 0.55f;
            }
            else
            {
                restLength = 0.42f; spring = 32000f * k; damper = 3200f * k; antiRoll = 9000f * k;
                drag = 0.42f * Mathf.Lerp(1f, 1.5f, Mathf.Clamp01((h - 1.4f) / 0.8f));   // furgonetas: más resistencia
                if (h > 1.8f) { maxSteerLow = 32f; maxSteerHigh = 8f; }
                if (d.category == "Superdeportivo")
                {
                    // muy bajo: suspensión corta y dura, poca resistencia al aire
                    restLength = 0.3f; spring = 42000f * k; damper = 3900f * k; antiRoll = 12000f * k; drag = 0.36f;
                }
            }
            Body.centerOfMass = new Vector3(0, Mathf.Clamp(0.08f + 0.12f * h, 0.15f, 0.42f), 0.05f);
        }
        public Vector3[] wheelAnchors = new Vector3[4];

        /// <summary>Modelo cuyas prestaciones lleva el coche (online: el del líder). null = las del propio modelo.</summary>
        public CarModelData perfModel;

        /// <summary>
        /// Online: potencia, velocidad punta, frenos, agarre, peso, aerodinámica y dirección de otro modelo (el del líder),
        /// conservando el aspecto, las ruedas y la altura de la suspensión del modelo elegido. Se llama después de colocar
        /// las ruedas: los muelles se escalan con el peso para que el coche no se hunda ni se levante.
        /// </summary>
        public void ApplyPerformance(CarModelData p)
        {
            if (p == null) return;
            if (Body == null) Body = GetComponent<Rigidbody>();
            perfModel = p;
            float m = p.mass > 0 ? p.mass : 1150f;
            float r = m / Mathf.Max(1f, Body.mass);
            Body.mass = m;
            spring *= r; damper *= r; antiRoll *= r;
            if (p.maxSpeed > 0) maxSpeed = p.maxSpeed;
            if (p.engine > 0) engineForce = p.engine;
            if (p.brake > 0) brakeForce = p.brake;
            if (p.grip > 0) grip = p.grip;
            if (p.downforce > 0) downforce = p.downforce;
            float h = p.boundsMax != null ? p.boundsMax[1] : 1.4f;
            maxSteerLow = 34f; maxSteerHigh = 9f;
            if (p.IsF1) { maxSteerLow = 30f; maxSteerHigh = 6f; drag = 0.55f; }
            else
            {
                drag = 0.42f * Mathf.Lerp(1f, 1.5f, Mathf.Clamp01((h - 1.4f) / 0.8f));
                if (h > 1.8f) { maxSteerLow = 32f; maxSteerHigh = 8f; }
                if (p.category == "Superdeportivo") drag = 0.36f;
            }
        }

        public Rigidbody Body { get; private set; }
        public float ForwardSpeed { get; private set; }
        public float Speed { get { return Body != null ? Body.Vel().magnitude : 0f; } }
        public int GroundedWheels { get; private set; }
        public float SlipAmount { get; private set; }
        public float SmoothedSteer { get; private set; }

        readonly float[] compression = new float[4];
        readonly float[] lastCompression = new float[4];
        readonly bool[] grounded = new bool[4];
        readonly RaycastHit[] hits = new RaycastHit[8];
        readonly Vector3[] hitPoints = new Vector3[4];
        readonly Vector3[] hitNormals = new Vector3[4];
        float wheelSpin;

        void Awake()
        {
            Body = GetComponent<Rigidbody>();
            Body.mass = 1150f;
            Body.SetDamping(0.02f, 0.6f);
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            Body.centerOfMass = new Vector3(0, 0.25f, 0.05f);
            Body.maxAngularVelocity = 8f;
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            Vector3 vel = Body.Vel();
            ForwardSpeed = Vector3.Dot(vel, transform.forward);
            float speedRatio = Mathf.Clamp01(Mathf.Abs(ForwardSpeed) / maxSpeed);

            if (PenaltyLeft > 0f)
            {
                if (!PenaltyCounting)
                {
                    penaltyBrakeT += dt;
                    if (vel.magnitude < 1f || penaltyBrakeT > 3.5f) PenaltyCounting = true;
                }
                else if ((PenaltyLeft -= dt) <= 0f) { PenaltyLeft = 0f; PenaltyCounting = false; }
            }
            bool hold = frozen || PenaltyLeft > 0f;
            float thr = hold ? 0 : Mathf.Clamp(throttle, -1, 1);
            float brk = hold ? 1 : Mathf.Clamp01(brake);
            bool hb = hold || handbrake;
            SmoothedSteer = Mathf.MoveTowards(SmoothedSteer, hold ? 0 : Mathf.Clamp(steer, -1, 1), dt * 4.5f);
            float steerAngle = SmoothedSteer * Mathf.Lerp(maxSteerLow, maxSteerHigh, speedRatio);

            // marcha atrás: freno pulsado y casi parado
            bool reversing = false;
            if (brk > 0.1f && thr <= 0.05f && ForwardSpeed < 1.5f && !hold)
            {
                reversing = true;
            }
            Reversing = reversing || ForwardSpeed < -1f;

            float rayLen = restLength + wheelRadius;
            GroundedWheels = 0;
            float slip = 0;
            for (int i = 0; i < 4; i++)
            {
                Vector3 anchor = transform.TransformPoint(wheelAnchors[i]);
                Vector3 down = -transform.up;
                grounded[i] = CastWheel(anchor, down, rayLen, out hitPoints[i], out hitNormals[i], out float dist);
                lastCompression[i] = compression[i];
                compression[i] = grounded[i] ? Mathf.Clamp01((rayLen - dist) / restLength) : 0f;
                if (grounded[i]) GroundedWheels++;
            }

            for (int i = 0; i < 4; i++)
            {
                if (!grounded[i]) continue;
                Vector3 anchor = transform.TransformPoint(wheelAnchors[i]);
                bool front = i < 2;

                // suspensión + barra estabilizadora
                float x = compression[i] * restLength;
                float v = (compression[i] - lastCompression[i]) * restLength / dt;
                int other = i ^ 1;
                float roll = (compression[i] - compression[other]) * antiRoll;
                float fz = Mathf.Max(0, spring * x + damper * v + roll);
                Body.AddForceAtPosition(transform.up * fz, anchor);

                // ejes de la rueda
                Quaternion steerRot = front ? Quaternion.AngleAxis(steerAngle, transform.up) : Quaternion.identity;
                Vector3 fwd = steerRot * transform.forward;
                Vector3 n = hitNormals[i];
                fwd = Vector3.ProjectOnPlane(fwd, n).normalized;
                Vector3 right = Vector3.Cross(n, fwd).normalized;
                Vector3 pv = Body.GetPointVelocity(hitPoints[i]);
                float vLong = Vector3.Dot(pv, fwd);
                float vLat = Vector3.Dot(pv, right);

                // fuerza lateral: anula el deslizamiento con límite de adherencia
                float mu = grip * (front ? 1f : rearGripFactor);
                if (!front && hb) mu = handbrakeGrip;
                float maxF = mu * fz;
                float fLat = -vLat * Body.mass * 0.25f / dt * 0.6f;

                // fuerza longitudinal
                float fLong = 0;
                if (reversing)
                {
                    if (ForwardSpeed > -reverseSpeed) fLong = -engineForce * 0.45f * brk * 0.25f;
                }
                else
                {
                    float curve = 1f - Mathf.Pow(speedRatio, 2.2f);
                    if (thr > 0 && ForwardSpeed < maxSpeed) fLong += thr * engineForce * curve * 0.25f;
                    if (thr < 0) fLong += thr * engineForce * 0.3f * 0.25f;
                    if (brk > 0 && ForwardSpeed > 0.5f) fLong -= brk * brakeForce * 0.25f * Mathf.Sign(vLong);
                }
                if (hb && !front) fLong -= Mathf.Sign(vLong) * Mathf.Min(Mathf.Abs(vLong) * 600f, brakeForce * 0.3f);
                // resistencia a la rodadura
                fLong -= vLong * 18f;
                if (hold) fLong = -vLong * Body.mass * 0.25f / dt * 0.5f;

                Vector2 f = new Vector2(fLat, fLong);
                if (f.magnitude > maxF)
                {
                    slip += (f.magnitude - maxF) / Mathf.Max(maxF, 1f);
                    f = f.normalized * maxF;
                }
                Body.AddForceAtPosition(right * f.x + fwd * f.y, hitPoints[i]);
            }
            SlipAmount = Mathf.Lerp(SlipAmount, Mathf.Clamp01(slip * 0.5f), 0.2f);

            // aerodinámica: resistencia + carga
            float sp = vel.magnitude;
            Body.AddForce(-vel * sp * drag);
            if (GroundedWheels > 0) Body.AddForce(-transform.up * sp * sp * downforce);
            else Body.AddTorque(-Body.angularVelocity * Body.mass * 0.3f); // estabilidad en el aire
        }

        bool CastWheel(Vector3 origin, Vector3 dir, float len, out Vector3 point, out Vector3 normal, out float dist)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, hits, len, ~0, QueryTriggerInteraction.Ignore);
            dist = float.MaxValue; point = Vector3.zero; normal = Vector3.up;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                // las ruedas sólo apoyan en el mundo (terreno, puentes), nunca sobre otro coche
                if (hits[i].rigidbody != null) continue;
                if (hits[i].distance < dist) { dist = hits[i].distance; point = hits[i].point; normal = hits[i].normal; found = true; }
            }
            return found;
        }

        void LateUpdate()
        {
            wheelSpin += ForwardSpeed / wheelRadius * Mathf.Rad2Deg * Time.deltaTime;
            float steerAngle = SmoothedSteer * Mathf.Lerp(maxSteerLow, maxSteerHigh, Mathf.Clamp01(Mathf.Abs(ForwardSpeed) / maxSpeed));
            float rayLen = restLength + wheelRadius;
            for (int i = 0; i < 4; i++)
            {
                var wv = wheelVisuals[i];
                if (wv == null) continue;
                float ext = grounded[i] ? rayLen - compression[i] * restLength : rayLen;
                Vector3 anchor = wheelAnchors[i];
                wv.localPosition = anchor + Vector3.down * (ext - wheelRadius);
                wv.localRotation = Quaternion.Euler(0, i < 2 ? steerAngle : 0, 0) * Quaternion.Euler(wheelSpin, 0, 0);
            }
        }

        /// <summary>Recoloca el coche en un punto con una orientación, parado.</summary>
        public void Teleport(Vector3 pos, Quaternion rot)
        {
            Body.position = pos; transform.position = pos;
            Body.rotation = rot; transform.rotation = rot;
            Body.SetVel(Vector3.zero);
            Body.angularVelocity = Vector3.zero;
            SmoothedSteer = 0;
            for (int i = 0; i < 4; i++) compression[i] = lastCompression[i] = 0;
        }
    }
}
