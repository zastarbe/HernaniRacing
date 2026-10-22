using System.Collections.Generic;
using HernaniRacing.Net;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>Instantánea recibida por red: posición, giro y velocidad a una hora del servidor.</summary>
    public struct NetSnap
    {
        public double t;
        public Vector3 p, v;
        public Quaternion q;
    }

    /// <summary>Buffer de instantáneas con interpolación (y una pequeña extrapolación si faltan datos).</summary>
    public class SnapBuffer
    {
        readonly List<NetSnap> buf = new List<NetSnap>(24);
        public double LastTime { get { return buf.Count > 0 ? buf[buf.Count - 1].t : -1; } }
        public int Count { get { return buf.Count; } }

        public void Add(NetSnap s)
        {
            if (buf.Count > 0 && s.t <= buf[buf.Count - 1].t) return;   // llega tarde o repetida
            buf.Add(s);
            if (buf.Count > 20) buf.RemoveAt(0);
        }

        public bool Sample(double t, float maxExtrapolate, out Vector3 p, out Quaternion q, out Vector3 v)
        {
            p = Vector3.zero; q = Quaternion.identity; v = Vector3.zero;
            if (buf.Count == 0) return false;
            var last = buf[buf.Count - 1];
            if (t >= last.t)
            {
                float dt = Mathf.Min((float)(t - last.t), maxExtrapolate);
                p = last.p + last.v * dt; q = last.q; v = last.v;
                return true;
            }
            if (t <= buf[0].t) { p = buf[0].p; q = buf[0].q; v = buf[0].v; return true; }
            for (int i = buf.Count - 2; i >= 0; i--)
            {
                var a = buf[i];
                if (a.t > t) continue;
                var b = buf[i + 1];
                float span = (float)(b.t - a.t);
                float u = span > 1e-4f ? Mathf.Clamp01((float)(t - a.t) / span) : 1f;
                // Hermite con las velocidades: curvas suaves aunque lleguen pocos paquetes
                float u2 = u * u, u3 = u2 * u;
                float h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
                if (span < 0.6f) p = h00 * a.p + h10 * span * a.v + h01 * b.p + h11 * span * b.v;
                else p = Vector3.Lerp(a.p, b.p, u);
                q = Quaternion.Slerp(a.q, b.q, u);
                v = Vector3.Lerp(a.v, b.v, u);
                return true;
            }
            p = last.p; q = last.q; v = last.v;
            return true;
        }
    }

    /// <summary>
    /// Coche de otro jugador (o de la IA que simula el líder). Es un cuerpo físico con la misma masa que
    /// un coche normal pero sin motor: cada paso de física se le da la velocidad necesaria para seguir la
    /// posición que llega por red. Así los golpes entre jugadores son iguales en los dos PCs (misma masa).
    /// </summary>
    public class CarProxy : MonoBehaviour
    {
        public const float Delay = 0.11f;     // retraso de interpolación (s)
        public CarController car;
        public readonly SnapBuffer snaps = new SnapBuffer();
        public float steer;
        public double lastHeard = -1;
        Rigidbody rb;
        float wheelSpin, smoothSteer;
        bool placed;

        public static CarProxy Attach(CarController c)
        {
            var px = c.gameObject.AddComponent<CarProxy>();
            px.car = c;
            px.rb = c.Body != null ? c.Body : c.GetComponent<Rigidbody>();
            c.enabled = false;                  // sin motor ni suspensión: manda la red
            px.rb.useGravity = false;
            px.rb.isKinematic = false;
            px.rb.interpolation = RigidbodyInterpolation.Interpolate;
            var audio = c.GetComponent<EngineAudio>();
            if (audio != null) Destroy(audio);
            return px;
        }

        /// <summary>Deja de seguir a la red y vuelve a ser un coche normal (cuando el líder cambia y la IA pasa a este PC).</summary>
        public void Release()
        {
            car.enabled = true;
            rb.useGravity = true;
            Destroy(this);
        }

        public Vector3 Velocity { get { return rb != null ? rb.Vel() : Vector3.zero; } }

        public void Push(double t, Vector3 p, Quaternion q, Vector3 v, float st)
        {
            snaps.Add(new NetSnap { t = t, p = p, q = q, v = v });
            steer = st;
            lastHeard = t;
        }

        void FixedUpdate()
        {
            var s = NetSession.I;
            if (s == null || rb == null || snaps.Count == 0) return;
            double rt = s.Core.ServerTime - Delay;
            Vector3 p, v; Quaternion q;
            if (!snaps.Sample(rt, 0.3f, out p, out q, out v)) return;
            float dt = Time.fixedDeltaTime;
            Vector3 err = p - rb.position;
            float ang; Vector3 axis;
            (q * Quaternion.Inverse(rb.rotation)).ToAngleAxis(out ang, out axis);
            if (ang > 180f) ang -= 360f;
            if (!placed || err.sqrMagnitude > 36f || Mathf.Abs(ang) > 70f)
            {
                rb.position = p; rb.rotation = q; transform.position = p; transform.rotation = q;
                rb.SetVel(v); rb.angularVelocity = Vector3.zero;
                placed = true;
                return;
            }
            // velocidad de red + corrección del error (ganancia ~ 1/0.1 s)
            Vector3 vel = v + err * Mathf.Min(10f, 0.5f / dt);
            rb.SetVel(vel);
            if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-6f) rb.angularVelocity = Vector3.zero;
            else rb.angularVelocity = axis.normalized * (ang * Mathf.Deg2Rad * Mathf.Min(10f, 0.5f / dt));
        }

        void LateUpdate()
        {
            if (car == null) return;
            float fwd = Vector3.Dot(Velocity, transform.forward);
            float r = Mathf.Max(car.wheelRadius, 0.1f);
            wheelSpin += fwd / r * Mathf.Rad2Deg * Time.deltaTime;
            smoothSteer = Mathf.MoveTowards(smoothSteer, steer, Time.deltaTime * 4.5f);
            float steerAngle = smoothSteer * Mathf.Lerp(car.maxSteerLow, car.maxSteerHigh, Mathf.Clamp01(Mathf.Abs(fwd) / Mathf.Max(car.maxSpeed, 1f)));
            float off = car.StaticWheelOffset;
            for (int i = 0; i < 4; i++)
            {
                var wv = car.wheelVisuals[i];
                if (wv == null) continue;
                wv.localPosition = car.wheelAnchors[i] + Vector3.down * off;
                wv.localRotation = Quaternion.Euler(0, i < 2 ? steerAngle : 0, 0) * Quaternion.Euler(wheelSpin, 0, 0);
            }
        }
    }

    /// <summary>Coche de tráfico que simula otro jugador: cinemático (como el tráfico propio).</summary>
    public class TrafficProxy : MonoBehaviour
    {
        public const float Delay = 0.22f;
        public byte owner;
        public ushort id;
        public readonly SnapBuffer snaps = new SnapBuffer();
        public double lastHeard = -1;
        public float speed;
        public bool crashed;
        public int junction = -1;
        public float HalfLength { get; private set; }
        Rigidbody rb;
        Transform[] wheels = new Transform[4];
        float wheelR = 0.33f, spin;
        bool placed;
        Vector3 vel;

        public Vector3 Velocity { get { return vel; } }

        public static TrafficProxy Create(byte owner, TrafficState st, Transform parent)
        {
            var ids = CarModels.TrafficIds;
            string mid = ids.Count > 0 ? ids[Mathf.Clamp(st.model, 0, ids.Count - 1)] : null;
            var md = CarModels.Get(mid);
            if (md == null) return null;
            var go = CarModels.CreateVisual(mid, new Color32(st.r, st.g, st.b, 255), parent);
            go.name = "Tráfico (remoto)";
            var tp = go.AddComponent<TrafficProxy>();
            tp.owner = owner; tp.id = st.id;
            tp.HalfLength = (md.boundsMax[2] - md.boundsMin[2]) * 0.5f;
            tp.wheelR = md.wheelRadius;
            for (int i = 0; i < 4; i++) tp.wheels[i] = go.transform.Find("Rueda" + i);
            tp.rb = go.AddComponent<Rigidbody>();
            tp.rb.isKinematic = true;
            tp.rb.interpolation = RigidbodyInterpolation.Interpolate;
            tp.rb.mass = md.mass;
            var col = go.AddComponent<BoxCollider>();
            Vector3 lo = new Vector3(md.boundsMin[0], md.boundsMin[1], md.boundsMin[2]), hi = new Vector3(md.boundsMax[0], md.boundsMax[1], md.boundsMax[2]);
            float y0 = Mathf.Max(lo.y, 0.25f);
            col.center = new Vector3(0, (y0 + hi.y * 0.95f) * 0.5f, (lo.z + hi.z) * 0.5f);
            col.size = new Vector3((hi.x - lo.x) * 0.95f, hi.y * 0.95f - y0, (hi.z - lo.z) * 0.97f);
            go.transform.position = new Vector3(st.px, st.py, st.pz);
            go.transform.rotation = new Quaternion(st.qx, st.qy, st.qz, st.qw);
            return tp;
        }

        public void Push(double t, TrafficState st)
        {
            var q = new Quaternion(st.qx, st.qy, st.qz, st.qw);
            Vector3 v = q * Vector3.forward * st.speed;
            snaps.Add(new NetSnap { t = t, p = new Vector3(st.px, st.py, st.pz), q = q, v = st.flags != 0 ? Vector3.zero : v });
            speed = st.speed;
            crashed = (st.flags & 1) != 0;
            junction = st.junction == 0xFFFF ? -1 : st.junction;
            lastHeard = t;
        }

        void FixedUpdate()
        {
            var s = NetSession.I;
            if (s == null || snaps.Count == 0) return;
            Vector3 p, v; Quaternion q;
            if (!snaps.Sample(s.Core.ServerTime - Delay, 0.4f, out p, out q, out v)) return;
            vel = crashed ? (p - rb.position) / Time.fixedDeltaTime : v;
            if (!placed || (p - rb.position).sqrMagnitude > 100f) { rb.position = p; rb.rotation = q; transform.position = p; transform.rotation = q; placed = true; return; }
            rb.MovePosition(p);
            rb.MoveRotation(q);
        }

        void LateUpdate()
        {
            spin += speed / Mathf.Max(wheelR, 0.1f) * Mathf.Rad2Deg * Time.deltaTime;
            for (int i = 0; i < 4; i++) if (wheels[i] != null) wheels[i].localRotation = Quaternion.Euler(spin, 0, 0);
        }
    }

    /// <summary>Peatón que simula otro jugador.</summary>
    public class PedProxy : MonoBehaviour
    {
        public const float Delay = 0.3f;
        public byte owner;
        public ushort id;
        public double lastHeard = -1;
        readonly SnapBuffer snaps = new SnapBuffer();
        Transform legL, legR, armL, armR;
        float phase;
        TrafficSystem sys;
        int crossEdge = -1; float crossS; byte state;
        bool markedBusy;

        public static PedProxy Create(byte owner, PedState st, Transform parent, TrafficSystem sys)
        {
            var go = new GameObject("Peaton (remoto)");
            go.transform.SetParent(parent, false);
            var pp = go.AddComponent<PedProxy>();
            pp.owner = owner; pp.id = st.id; pp.sys = sys;
            Pedestrian.BuildRig(go.transform, st.look, out pp.legL, out pp.legR, out pp.armL, out pp.armR);
            go.transform.position = new Vector3(st.px, st.py, st.pz);
            go.transform.rotation = Quaternion.Euler(0, st.yaw, 0);
            return pp;
        }

        public void Push(double t, PedState st)
        {
            var q = Quaternion.Euler(0, st.yaw, 0);
            var p = new Vector3(st.px, st.py, st.pz);
            Vector3 v = Vector3.zero;
            if (snaps.Count > 0)
            {
                Vector3 lp; Quaternion lq; Vector3 lv;
                snaps.Sample(snaps.LastTime, 0f, out lp, out lq, out lv);
                float dt = (float)(t - snaps.LastTime);
                if (dt > 1e-3f) v = (p - lp) / dt;
            }
            snaps.Add(new NetSnap { t = t, p = p, q = q, v = v });
            lastHeard = t;
            // pasos de cebra: que el tráfico de este PC también se pare
            int ce = (st.state == 1 || st.state == 2) ? st.crossEdge : -1;
            float cs = st.crossS / 4f;
            if (markedBusy && (st.state != 2 || ce != crossEdge)) { sys.SetCrossingBusy(crossEdge, crossS, -1); markedBusy = false; }
            crossEdge = ce; crossS = cs; state = st.state;
            if (state == 2 && !markedBusy && crossEdge >= 0) { sys.SetCrossingBusy(crossEdge, crossS, 1); markedBusy = true; }
        }

        public void Release()
        {
            if (markedBusy && sys != null) sys.SetCrossingBusy(crossEdge, crossS, -1);
            markedBusy = false;
        }

        void OnDestroy() { Release(); }

        void Update()
        {
            var s = NetSession.I;
            if (s == null || snaps.Count == 0) return;
            if (state == 1 && crossEdge >= 0 && sys != null) sys.SetCrossingWaiting(crossEdge, crossS);
            Vector3 p, v; Quaternion q;
            if (!snaps.Sample(s.Core.ServerTime - Delay, 0.3f, out p, out q, out v)) return;
            transform.position = p;
            transform.rotation = q;
            float sp = new Vector2(v.x, v.z).magnitude;
            bool moving = sp > 0.3f;
            if (moving) phase += Time.deltaTime * sp * 5.5f;
            float sw = moving ? Mathf.Sin(phase) * 28f : 0f;
            legL.localRotation = Quaternion.Euler(sw, 0, 0);
            legR.localRotation = Quaternion.Euler(-sw, 0, 0);
            armL.localRotation = Quaternion.Euler(-sw * 0.8f, 0, 0);
            armR.localRotation = Quaternion.Euler(sw * 0.8f, 0, 0);
        }
    }
}
