// Cliente de red sin dependencias de Unity (lo usa el juego y también las pruebas automáticas).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteNetLib.Utils;

namespace HernaniRacing.Net
{
    public enum ClientState { Offline, Connecting, Connected }

    public class NetClientCore
    {
        readonly INetTransport transport;
        bool linked;                 // hay conexión (o intento) abierta en el transporte
        double lastHeard;
        int lastRttMs;
        readonly NetDataWriter w = new NetDataWriter();
        readonly Stopwatch clock = Stopwatch.StartNew();

        public ClientState State { get; private set; }
        public string LastError { get; private set; }
        public void ClearError() { LastError = null; }
        public byte MyId { get; private set; }
        public string ServerName { get; private set; } = "";
        public string Motd { get; private set; } = "";
        public RoomInfo Room { get; private set; }
        public List<RoomListEntry> Rooms { get; private set; } = new List<RoomListEntry>();
        /// <summary>Circuitos guardados en el servidor (con sus rankings). Se piden con RequestCircuits.</summary>
        public List<CircuitEntry> Circuits { get; private set; } = new List<CircuitEntry>();
        /// <summary>Cambia cada vez que llega la lista de circuitos.</summary>
        public int CircuitsVersion { get; private set; }
        public bool CircuitsLoaded { get; private set; }
        public int Ping { get { int t = linked ? transport.Rtt : -1; return t >= 0 ? t : lastRttMs; } }
        /// <summary>Conexión por WebSocket (versión web) en vez de UDP.</summary>
        public bool IsWeb { get { return transport.IsWeb; } }
        public PlayerProfile Profile = new PlayerProfile();

        // reloj del servidor
        double offset;           // hora servidor = hora local + offset
        double bestRtt = 1e9;
        bool clockSynced;
        double nextTimeReq;
        readonly List<KeyValuePair<double, double>> samples = new List<KeyValuePair<double, double>>();   // (rtt, offset)

        public event Action RoomChanged;            // cambia la sala (jugadores, ajustes, fase...)
        public event Action RoomListChanged;
        public event Action<string> ErrorReceived;
        public event Action<string> InfoReceived;    // avisos buenos del servidor (circuito guardado, récord...)
        public event Action CircuitsChanged;
        public event Action Disconnected;
        public delegate void RelayHandler(byte from, RelayKind kind, double time, int count, NetDataReader r);
        public event RelayHandler RelayReceived;

        public NetClientCore() : this(new UdpTransport()) { }

        public NetClientCore(INetTransport t)
        {
            transport = t;
            transport.Received = OnReceive;
            transport.Closed = OnClosed;
        }

        public double LocalTime { get { return clock.Elapsed.TotalSeconds; } }
        /// <summary>Hora del servidor (segundos), igual para todos los jugadores.</summary>
        public double ServerTime { get { return LocalTime + offset; } }
        public bool ClockSynced { get { return clockSynced; } }
        public bool InRoom { get { return Room != null; } }
        public bool IsLeader { get { return Room != null && Room.leaderId == MyId; } }
        public PlayerInfo Me { get { return Room != null ? Room.Find(MyId) : null; } }

        public void Connect(string host, int port, string password, string dataSig)
        {
            Disconnect();
            LastError = null;
            var info = new ConnectInfo { password = password ?? "", dataSig = dataSig ?? "", profile = Profile };
            w.Reset();
            info.Write(w);
            string err = (host ?? "").Trim().Length == 0 ? "Falta la dirección del servidor." : transport.Connect(host, port, w);
            if (err != null)
            {
                LastError = err;
                State = ClientState.Offline;
                return;
            }
            linked = true;
            lastHeard = LocalTime;
            State = ClientState.Connecting;
            clockSynced = false; bestRtt = 1e9; samples.Clear(); nextTimeReq = 0;
        }

        public void Disconnect()
        {
            if (linked) transport.Disconnect();
            linked = false;
            Room = null;
            Rooms.Clear();
            Circuits = new List<CircuitEntry>();
            CircuitsLoaded = false;
            State = ClientState.Offline;
        }

        public void Shutdown()
        {
            Disconnect();
            transport.Shutdown();
        }

        /// <summary>Llamar en cada frame: procesa lo recibido y sincroniza el reloj.</summary>
        public void Poll()
        {
            if (!linked) return;
            transport.Poll();
            if (!linked) return;
            // WebSocket: el servidor manda un ping cada segundo; si no llega nada en 15 s, la conexión se ha perdido
            if (transport.IsWeb && LocalTime - lastHeard > 15.0)
            {
                bool was = State == ClientState.Connected;
                transport.Disconnect();
                OnClosed(was ? CloseKind.Lost : CloseKind.NoResponse, null, "sin datos");
                return;
            }
            if (State == ClientState.Connected && LocalTime >= nextTimeReq)
            {
                nextTimeReq = LocalTime + (samples.Count < 6 ? 0.25 : 2.0);
                w.Reset(); w.Put((byte)Msg.TimeReq); w.Put(LocalTime);
                transport.Send(w, NetProto.ChReliable, false);
            }
        }

        // ------------------------------------------------------------------ peticiones
        void SendReliable() { if (linked && State == ClientState.Connected) transport.Send(w, NetProto.ChReliable, true); }

        public void RequestRooms() { w.Reset(); w.Put((byte)Msg.ListRooms); SendReliable(); }
        public void CreateRoom(string name) { w.Reset(); w.Put((byte)Msg.CreateRoom); w.Put(name ?? ""); SendReliable(); }
        public void JoinRoom(string code) { w.Reset(); w.Put((byte)Msg.JoinRoom); w.Put((code ?? "").Trim().ToUpperInvariant()); SendReliable(); }
        public void LeaveRoom() { w.Reset(); w.Put((byte)Msg.LeaveRoom); SendReliable(); Room = null; RoomChanged?.Invoke(); }
        public void SendProfile() { w.Reset(); w.Put((byte)Msg.Profile); Profile.Write(w); SendReliable(); }
        public void SendSettings(RoomSettings s) { w.Reset(); w.Put((byte)Msg.Settings); s.Write(w); SendReliable(); }
        public void StartGame(GameMode mode) { w.Reset(); w.Put((byte)Msg.Start); w.Put((byte)mode); SendReliable(); }
        public void EnterGame() { w.Reset(); w.Put((byte)Msg.EnterGame); SendReliable(); }
        public void LeaveGame() { w.Reset(); w.Put((byte)Msg.LeaveGame); SendReliable(); }
        public void EndGame() { w.Reset(); w.Put((byte)Msg.EndGame); SendReliable(); }

        // ---- circuitos del servidor y rankings
        public void RequestCircuits() { w.Reset(); w.Put((byte)Msg.ListCircuits); SendReliable(); }

        /// <summary>Guarda un circuito en el servidor (el creador es el nombre con el que estás conectado).</summary>
        public void SaveCircuit(string name, IList<int> nodes, bool closed, float length)
        {
            w.Reset(); w.Put((byte)Msg.SaveCircuit);
            w.Put(name ?? "");
            var arr = new int[nodes.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = nodes[i];
            w.PutArray(arr);
            w.Put(closed); w.Put(length);
            SendReliable();
        }

        /// <summary>Al cruzar la meta en una carrera online: mi mejor vuelta y mi tiempo total (para los rankings).</summary>
        public void SendRaceResult(float bestLap, float totalTime, string carId, byte laps)
        {
            w.Reset(); w.Put((byte)Msg.RaceResult);
            w.Put(bestLap); w.Put(totalTime); w.Put(carId ?? ""); w.Put(laps);
            SendReliable();
        }

        /// <summary>Borra un circuito del servidor (sólo su creador).</summary>
        public void DeleteCircuit(string key) { w.Reset(); w.Put((byte)Msg.DeleteCircuit); w.Put(key ?? ""); SendReliable(); }

        /// <summary>Manda un paquete Relay ya montado con RelayCodec (no fiable: si se pierde llega el siguiente).</summary>
        public void SendRelay(NetDataWriter packet, RelayKind kind)
        {
            if (!linked || State != ClientState.Connected) return;
            byte ch = kind == RelayKind.Cars ? NetProto.ChCars : (kind == RelayKind.Traffic ? NetProto.ChTraffic : NetProto.ChPeds);
            transport.Send(packet, ch, false);
        }

        public int MaxUnreliable { get { return linked ? transport.MaxUnreliable : 500; } }

        // ------------------------------------------------------------------ eventos del transporte
        void OnClosed(CloseKind kind, string serverText, string detail)
        {
            if (!linked) return;
            bool wasConnected = State == ClientState.Connected;
            string reason = serverText;
            if (reason == null)
            {
                switch (kind)
                {
                    case CloseKind.NoResponse:
                    case CloseKind.Lost:
                        reason = wasConnected ? "Se ha perdido la conexión con el servidor." :
                            (transport.IsWeb ? "El servidor no responde. ¿Está encendido y con el puerto web (TCP) abierto?"
                                             : "El servidor no responde. ¿Está encendido y con el puerto UDP abierto?"); break;
                    case CloseKind.Unreachable:
                        reason = "No hay conexión a Internet o el servidor no es accesible."; break;
                    case CloseKind.RemoteClose:
                        reason = "El servidor ha cerrado la conexión."; break;
                    case CloseKind.Local:
                        reason = null; break;
                    default: reason = "Desconectado (" + detail + ")."; break;
                }
            }
            LastError = reason;
            linked = false;
            Room = null;
            State = ClientState.Offline;
            Disconnected?.Invoke();
        }

        void OnReceive(NetDataReader r)
        {
            lastHeard = LocalTime;
            if (r.AvailableBytes < 1) return;
            var m = (Msg)r.GetByte();
            try
            {
                switch (m)
                {
                    case Msg.Welcome:
                        MyId = r.GetByte(); ServerName = r.GetString(64); Motd = r.GetString(300);
                        State = ClientState.Connected;
                        nextTimeReq = 0;
                        RequestRooms();
                        break;
                    case Msg.TimeResp:
                        {
                            double sent = r.GetDouble(), st = r.GetDouble();
                            double now = LocalTime, rtt = now - sent;
                            if (rtt < 0 || rtt > 3) break;
                            lastRttMs = (int)Math.Round(rtt * 1000.0);
                            double off = st + rtt * 0.5 - now;
                            samples.Add(new KeyValuePair<double, double>(rtt, off));
                            if (samples.Count > 16) samples.RemoveAt(0);
                            // la muestra con menor RTT es la más fiable
                            double br = 1e9, bo = off;
                            foreach (var s in samples) if (s.Key < br) { br = s.Key; bo = s.Value; }
                            if (!clockSynced || Math.Abs(bo - offset) > 0.25) offset = bo;
                            else offset += (bo - offset) * 0.2;   // corrección suave
                            bestRtt = br;
                            clockSynced = true;
                            break;
                        }
                    case Msg.RoomList:
                        {
                            int n = r.GetUShort();
                            Rooms = new List<RoomListEntry>();
                            for (int i = 0; i < n; i++) Rooms.Add(RoomListEntry.Read(r));
                            RoomListChanged?.Invoke();
                            break;
                        }
                    case Msg.RoomState:
                        Room = RoomInfo.Read(r);
                        RoomChanged?.Invoke();
                        break;
                    case Msg.LeftRoom:
                        Room = null;
                        RoomChanged?.Invoke();
                        RequestRooms();
                        break;
                    case Msg.Error:
                        {
                            string e = r.GetString(300);
                            LastError = e;
                            ErrorReceived?.Invoke(e);
                            break;
                        }
                    case Msg.CircuitList:
                        {
                            int n = r.GetUShort();
                            var list = new List<CircuitEntry>(n);
                            for (int i = 0; i < n; i++) list.Add(CircuitEntry.Read(r));
                            Circuits = list;
                            CircuitsVersion++;
                            CircuitsLoaded = true;
                            CircuitsChanged?.Invoke();
                            break;
                        }
                    case Msg.Info:
                        InfoReceived?.Invoke(r.GetString(300));
                        break;
                    case Msg.RelayFrom:
                        {
                            byte from = r.GetByte();
                            var kind = (RelayKind)r.GetByte();
                            double t = r.GetDouble();
                            int count = r.GetByte();
                            RelayReceived?.Invoke(from, kind, t, count, r);
                            break;
                        }
                }
            }
            catch (Exception e)
            {
                LastError = "Mensaje de red dañado (" + m + "): " + e.Message;
            }
        }

    }
}
