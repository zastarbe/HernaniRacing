using System.Collections.Generic;
using HernaniRacing.Net;
using LiteNetLib.Utils;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>Un coche de la partida online: el mío, el de otro jugador o uno de la IA.</summary>
    public class NetCar
    {
        public byte owner, local;
        public CarController car;
        public CarProxy proxy;           // != null si lo simula otro PC
        public string nick = "";
        public Color color = Color.white;
        // datos de carrera (los rellena el dueño)
        public float progress, finishTime, bestLap = -1;
        public byte laps, flags;
        public bool gone;
        public int Key { get { return owner << 8 | local; } }
        public bool Remote { get { return proxy != null; } }
        public bool Finished { get { return (flags & CarState.Finished) != 0; } }
    }

    /// <summary>
    /// Sincroniza la partida online: manda mis coches (y el tráfico y los peatones que he creado) y
    /// muestra los de los demás. También dibuja el nombre de cada jugador encima de su coche.
    /// </summary>
    public class NetWorldSync : MonoBehaviour
    {
        public NetSession net;
        public World world;
        public TrafficSystem traffic;
        public Transform localPlayer;
        public bool dynamicPlayers;        // paseo libre: los jugadores entran y salen cuando quieren
        public readonly Dictionary<int, NetCar> cars = new Dictionary<int, NetCar>();
        readonly Dictionary<long, TrafficProxy> tProx = new Dictionary<long, TrafficProxy>();
        readonly Dictionary<long, PedProxy> pProx = new Dictionary<long, PedProxy>();
        readonly NetDataWriter w = new NetDataWriter();
        float nextCars, nextTraffic, nextPeds, nextClean;
        Transform root;
        GUIStyle tagStyle, tagShadow;

        public const float CarRate = 20f, TrafficRate = 8f, PedRate = 5f;
        const float RemoteTrafficRange = 650f;

        public static NetWorldSync Create(NetSession net, World w, TrafficSystem traffic, Transform localPlayer, bool dynamicPlayers)
        {
            var go = new GameObject("Red (online)");
            var s = go.AddComponent<NetWorldSync>();
            s.net = net; s.world = w; s.traffic = traffic; s.localPlayer = localPlayer; s.dynamicPlayers = dynamicPlayers;
            s.root = go.transform;
            net.Core.RelayReceived += s.OnRelay;
            return s;
        }

        void OnDestroy()
        {
            if (net != null) net.Core.RelayReceived -= OnRelay;
            foreach (var p in pProx.Values) if (p != null) { p.Release(); Destroy(p.gameObject); }
            foreach (var t in tProx.Values) if (t != null) Destroy(t.gameObject);
            if (traffic != null) { traffic.remoteCars.Clear(); traffic.remotePeds.Clear(); traffic.others.Clear(); }
            foreach (var c in cars.Values)
                if (dynamicPlayers && c.Remote && c.car != null) Destroy(c.car.gameObject);
        }

        public NetCar Register(byte owner, byte local, CarController car, bool remote, string nick, Color color)
        {
            var nc = new NetCar { owner = owner, local = local, car = car, nick = nick, color = color };
            if (remote) nc.proxy = CarProxy.Attach(car);
            cars[nc.Key] = nc;
            RefreshOthers();
            return nc;
        }

        public NetCar Get(byte owner, byte local)
        {
            NetCar c; cars.TryGetValue(owner << 8 | local, out c);
            return c;
        }

        bool Owned(NetCar c)
        {
            if (c.gone || c.car == null || c.Remote) return false;
            return c.owner == net.MyId || (c.owner == NetProto.AiOwner && net.IsLeader);
        }

        void RefreshOthers()
        {
            if (traffic == null) return;
            traffic.others.Clear();
            foreach (var c in cars.Values)
                if (c.Remote && !c.gone && c.car != null && c.owner != NetProto.AiOwner) traffic.others.Add(c.car.transform);
        }

        bool AnyoneElse()
        {
            var room = net.Room;
            if (room == null) return false;
            foreach (var p in room.players) if (p.id != net.MyId && p.inGame) return true;
            return false;
        }

        // ------------------------------------------------------------------ envío
        void Update()
        {
            if (net == null || !net.InRoom) return;
            float now = Time.unscaledTime;
            double st = net.Core.ServerTime;
            bool others = AnyoneElse();
            if (now >= nextCars)
            {
                nextCars = now + 1f / CarRate;
                if (others) SendCars(st);
            }
            if (traffic != null && now >= nextTraffic)
            {
                nextTraffic = now + 1f / TrafficRate;
                if (others) SendTraffic(st);
            }
            if (traffic != null && now >= nextPeds)
            {
                nextPeds = now + 1f / PedRate;
                if (others) SendPeds(st);
            }
            if (now >= nextClean)
            {
                nextClean = now + 0.5f;
                Clean(st);
            }
        }

        void SendCars(double st)
        {
            var list = new List<NetCar>();
            foreach (var c in cars.Values) if (Owned(c)) list.Add(c);
            if (list.Count == 0) return;
            RelayCodec.Begin(w, RelayKind.Cars, st, list.Count);
            foreach (var c in list)
            {
                var t = c.car.transform;
                var b = c.car.Body;
                Vector3 v = b != null ? b.Vel() : Vector3.zero;
                var q = t.rotation;
                var cs = new CarState
                {
                    owner = c.owner, local = c.local,
                    px = t.position.x, py = t.position.y, pz = t.position.z,
                    qx = q.x, qy = q.y, qz = q.z, qw = q.w,
                    vx = v.x, vy = v.y, vz = v.z,
                    steer = c.car.SmoothedSteer,
                    progress = c.progress, laps = c.laps, flags = c.flags, finishTime = c.finishTime, bestLap = c.bestLap
                };
                if (c.car.brake > 0.2f) cs.flags |= CarState.Braking;
                cs.Write(w);
            }
            net.Core.SendRelay(w, RelayKind.Cars);
        }

        void SendTraffic(double st)
        {
            var list = new List<TrafficCar>();
            foreach (var c in traffic.cars) if (c != null && !c.WantsDespawn) list.Add(c);
            int per = (RelayCodec.MaxPayload - 16) / TrafficState.Size;
            for (int i0 = 0; i0 < list.Count; i0 += per)
            {
                int n = Mathf.Min(per, list.Count - i0);
                RelayCodec.Begin(w, RelayKind.Traffic, st, n);
                for (int i = i0; i < i0 + n; i++)
                {
                    var c = list[i];
                    var t = c.transform;
                    var q = t.rotation;
                    Color32 col = c.paint;
                    int jn = c.JunctionNode;
                    new TrafficState
                    {
                        id = c.netId, model = (byte)Mathf.Clamp(c.modelIndex, 0, 255), r = col.r, g = col.g, b = col.b,
                        px = t.position.x, py = t.position.y, pz = t.position.z, qx = q.x, qy = q.y, qz = q.z, qw = q.w,
                        speed = c.Crashed ? c.Velocity.magnitude : c.Speed, flags = (byte)(c.Crashed ? 1 : 0),
                        junction = jn >= 0 && jn < 0xFFFF ? (ushort)jn : (ushort)0xFFFF
                    }.Write(w);
                }
                net.Core.SendRelay(w, RelayKind.Traffic);
            }
        }

        void SendPeds(double st)
        {
            var list = new List<Pedestrian>();
            foreach (var p in traffic.peds) if (p != null && !p.WantsDespawn) list.Add(p);
            int per = (RelayCodec.MaxPayload - 16) / PedState.MaxSize;
            for (int i0 = 0; i0 < list.Count; i0 += per)
            {
                int n = Mathf.Min(per, list.Count - i0);
                RelayCodec.Begin(w, RelayKind.Peds, st, n);
                for (int i = i0; i < i0 + n; i++)
                {
                    var p = list[i];
                    var t = p.transform;
                    int ce; float cs;
                    byte state = p.NetState(out ce, out cs);
                    new PedState
                    {
                        id = p.netId, look = p.lookSeed, px = t.position.x, py = t.position.y, pz = t.position.z, yaw = t.eulerAngles.y,
                        state = state, crossEdge = (ushort)Mathf.Clamp(ce, 0, 0xFFFF), crossS = (ushort)Mathf.Clamp(Mathf.RoundToInt(cs * 4f), 0, 0xFFFF)
                    }.Write(w);
                }
                net.Core.SendRelay(w, RelayKind.Peds);
            }
        }

        // ------------------------------------------------------------------ recepción
        void OnRelay(byte from, RelayKind kind, double t, int count, NetDataReader r)
        {
            if (this == null) return;
            switch (kind)
            {
                case RelayKind.Cars:
                    for (int i = 0; i < count; i++) ApplyCar(from, t, CarState.Read(r));
                    break;
                case RelayKind.Traffic:
                    if (traffic == null) return;
                    for (int i = 0; i < count; i++) ApplyTraffic(from, t, TrafficState.Read(r));
                    break;
                case RelayKind.Peds:
                    if (traffic == null) return;
                    for (int i = 0; i < count; i++) ApplyPed(from, t, PedState.Read(r));
                    break;
            }
        }

        void ApplyCar(byte from, double t, CarState s)
        {
            if (s.owner == net.MyId) return;
            if (s.owner != NetProto.AiOwner && s.owner != from) return;        // nadie manda coches de otro
            if (s.owner == NetProto.AiOwner && net.Room != null && from != net.Room.leaderId) return;
            NetCar c;
            if (!cars.TryGetValue(s.owner << 8 | s.local, out c))
            {
                if (!dynamicPlayers || s.owner == NetProto.AiOwner || s.local != 0) return;
                c = CreateRemotePlayer(s.owner, s);
                if (c == null) return;
            }
            if (c.gone || c.proxy == null) return;
            c.proxy.Push(t, new Vector3(s.px, s.py, s.pz), new Quaternion(s.qx, s.qy, s.qz, s.qw), new Vector3(s.vx, s.vy, s.vz), s.steer);
            c.progress = s.progress; c.laps = s.laps; c.flags = s.flags; c.finishTime = s.finishTime; c.bestLap = s.bestLap;
        }

        NetCar CreateRemotePlayer(byte id, CarState s)
        {
            var room = net.Room;
            var pi = room != null ? room.Find(id) : null;
            if (pi == null || !pi.inGame) return null;
            var color = NetSession.IntToColor(pi.profile.color);
            var car = CarModels.Create(pi.profile.carId, "Coche " + pi.profile.nick, color, false);
            car.Teleport(new Vector3(s.px, s.py, s.pz), new Quaternion(s.qx, s.qy, s.qz, s.qw));
            var nc = Register(id, 0, car, true, pi.profile.nick, color);
            if (traffic != null) traffic.drivers.Add(car);
            net.ShowToast(pi.profile.nick + " se ha unido al paseo", 2.5f);
            return nc;
        }

        static long TKey(byte owner, ushort id) { return (long)owner << 16 | id; }

        void ApplyTraffic(byte from, double t, TrafficState s)
        {
            long k = TKey(from, s.id);
            TrafficProxy tp;
            Vector3 p = new Vector3(s.px, s.py, s.pz);
            bool near = localPlayer == null || Flat(p - localPlayer.position) < RemoteTrafficRange;
            if (!tProx.TryGetValue(k, out tp) || tp == null)
            {
                if (!near) return;
                tp = TrafficProxy.Create(from, s, root);
                if (tp == null) return;
                tProx[k] = tp;
                traffic.remoteCars.Add(tp);
            }
            tp.Push(t, s);
        }

        void ApplyPed(byte from, double t, PedState s)
        {
            long k = TKey(from, s.id);
            PedProxy pp;
            Vector3 p = new Vector3(s.px, s.py, s.pz);
            bool near = localPlayer == null || Flat(p - localPlayer.position) < RemoteTrafficRange * 0.6f;
            if (!pProx.TryGetValue(k, out pp) || pp == null)
            {
                if (!near) return;
                pp = PedProxy.Create(from, s, root, traffic);
                pProx[k] = pp;
                traffic.remotePeds.Add(pp);
            }
            pp.Push(t, s);
        }

        static float Flat(Vector3 v) { v.y = 0; return v.magnitude; }

        void Clean(double st)
        {
            var room = net.Room;
            // tráfico y peatones que ya no se actualizan (su dueño los ha retirado o se ha ido)
            var dead = new List<long>();
            foreach (var kv in tProx)
            {
                var tp = kv.Value;
                bool far = tp != null && localPlayer != null && Flat(tp.transform.position - localPlayer.position) > RemoteTrafficRange + 100f;
                if (tp == null || st - tp.lastHeard > 1.6 || far || !InGame(room, tp.owner))
                {
                    if (tp != null) { traffic.remoteCars.Remove(tp); Destroy(tp.gameObject); }
                    dead.Add(kv.Key);
                }
            }
            foreach (var k in dead) tProx.Remove(k);
            dead.Clear();
            foreach (var kv in pProx)
            {
                var pp = kv.Value;
                if (pp == null || st - pp.lastHeard > 2.0 || !InGame(room, pp.owner))
                {
                    if (pp != null) { traffic.remotePeds.Remove(pp); pp.Release(); Destroy(pp.gameObject); }
                    dead.Add(kv.Key);
                }
            }
            foreach (var k in dead) pProx.Remove(k);
            if (traffic != null) { traffic.remoteCars.RemoveAll(x => x == null); traffic.remotePeds.RemoveAll(x => x == null); }

            // jugadores que se han ido
            bool changed = false;
            foreach (var c in cars.Values)
            {
                if (c.gone || !c.Remote || c.owner == NetProto.AiOwner) continue;
                if (!InGame(room, c.owner))
                {
                    c.gone = true; changed = true;
                    if (traffic != null) traffic.drivers.Remove(c.car);
                    if (c.car != null)
                    {
                        if (dynamicPlayers) { Destroy(c.car.gameObject); c.car = null; }
                        else c.car.gameObject.SetActive(false);
                    }
                    net.ShowToast(c.nick + (dynamicPlayers ? " ha salido del paseo" : " ha abandonado la carrera"), 3f);
                }
            }
            if (changed)
            {
                if (dynamicPlayers)
                {
                    var rm = new List<int>();
                    foreach (var kv in cars) if (kv.Value.gone) rm.Add(kv.Key);
                    foreach (var k in rm) cars.Remove(k);
                }
                RefreshOthers();
            }
        }

        static bool InGame(RoomInfo room, byte id)
        {
            if (room == null) return false;
            var p = room.Find(id);
            return p != null && p.inGame;
        }

        /// <summary>El líder ha cambiado y la IA pasa a este PC: el coche deja de seguir a la red.</summary>
        public void TakeOver(NetCar c)
        {
            if (c == null || c.proxy == null) return;
            c.proxy.Release();
            c.proxy = null;
            RefreshOthers();
        }

        // ------------------------------------------------------------------ nombres encima de los coches
        void OnGUI()
        {
            var cam = Camera.main;
            if (cam == null || net == null) return;
            if (tagStyle == null)
            {
                tagStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                tagShadow = new GUIStyle(tagStyle);
                tagShadow.normal.textColor = new Color(0, 0, 0, 0.8f);
            }
            foreach (var c in cars.Values)
            {
                if (!c.Remote || c.gone || c.car == null || c.owner == NetProto.AiOwner) continue;
                Vector3 wp = c.car.transform.position + Vector3.up * 2.3f;
                Vector3 sp = cam.WorldToScreenPoint(wp);
                if (sp.z <= 0.5f || sp.z > 350f) continue;
                float scale = Mathf.Clamp(1.4f - sp.z / 300f, 0.6f, 1.3f);
                tagStyle.fontSize = tagShadow.fontSize = Mathf.RoundToInt(18 * scale * Screen.height / 1080f);
                tagStyle.normal.textColor = Color.Lerp(c.color, Color.white, 0.45f);
                Rect r = new Rect(sp.x - 150, Screen.height - sp.y - 16, 300, 32);
                GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), c.nick, tagShadow);
                GUI.Label(r, c.nick, tagStyle);
            }
        }
    }
}
