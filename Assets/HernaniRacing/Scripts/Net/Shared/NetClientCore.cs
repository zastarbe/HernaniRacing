// Cliente de red sin dependencias de Unity (lo usa el juego y también las pruebas automáticas).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace HernaniRacing.Net
{
    public enum ClientState { Offline, Connecting, Connected }

    public class NetClientCore : INetEventListener
    {
        readonly NetManager net;
        NetPeer server;
        readonly NetDataWriter w = new NetDataWriter();
        readonly Stopwatch clock = Stopwatch.StartNew();

        public ClientState State { get; private set; }
        public string LastError { get; private set; }
        public byte MyId { get; private set; }
        public string ServerName { get; private set; } = "";
        public string Motd { get; private set; } = "";
        public RoomInfo Room { get; private set; }
        public List<RoomListEntry> Rooms { get; private set; } = new List<RoomListEntry>();
        public int Ping { get { return server != null ? server.RoundTripTime : 0; } }
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
        public event Action Disconnected;
        public delegate void RelayHandler(byte from, RelayKind kind, double time, int count, NetDataReader r);
        public event RelayHandler RelayReceived;

        public NetClientCore()
        {
            net = new NetManager(this)
            {
                ChannelsCount = (byte)NetProto.Channels,
                DisconnectTimeout = 10000,
                UpdateTime = 10,
                AutoRecycle = true,
                UnsyncedEvents = false,
                IPv6Enabled = false
            };
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
            if (!net.IsRunning) net.Start();
            var info = new ConnectInfo { password = password ?? "", dataSig = dataSig ?? "", profile = Profile };
            w.Reset();
            info.Write(w);
            IPAddress ip;
            try
            {
                if (!IPAddress.TryParse(host.Trim(), out ip))
                {
                    ip = null;
                    foreach (var a in Dns.GetHostAddresses(host.Trim()))
                        if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                    if (ip == null) throw new Exception("sin IPv4");
                }
            }
            catch (Exception)
            {
                LastError = "No encuentro el servidor \"" + host + "\". Revisa la dirección.";
                State = ClientState.Offline;
                return;
            }
            State = ClientState.Connecting;
            server = net.Connect(new IPEndPoint(ip, port), w);
            clockSynced = false; bestRtt = 1e9; samples.Clear(); nextTimeReq = 0;
        }

        public void Disconnect()
        {
            if (server != null) server.Disconnect();
            server = null;
            Room = null;
            Rooms.Clear();
            State = ClientState.Offline;
        }

        public void Shutdown()
        {
            Disconnect();
            if (net.IsRunning) net.Stop();
        }

        /// <summary>Llamar en cada frame: procesa lo recibido y sincroniza el reloj.</summary>
        public void Poll()
        {
            if (!net.IsRunning) return;
            net.PollEvents();
            if (State == ClientState.Connected && LocalTime >= nextTimeReq)
            {
                nextTimeReq = LocalTime + (samples.Count < 6 ? 0.25 : 2.0);
                w.Reset(); w.Put((byte)Msg.TimeReq); w.Put(LocalTime);
                server.Send(w, NetProto.ChReliable, DeliveryMethod.Unreliable);
            }
        }

        // ------------------------------------------------------------------ peticiones
        void SendReliable() { if (server != null && State == ClientState.Connected) server.Send(w, NetProto.ChReliable, DeliveryMethod.ReliableOrdered); }

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

        /// <summary>Manda un paquete Relay ya montado con RelayCodec (no fiable: si se pierde llega el siguiente).</summary>
        public void SendRelay(NetDataWriter packet, RelayKind kind)
        {
            if (server == null || State != ClientState.Connected) return;
            byte ch = kind == RelayKind.Cars ? NetProto.ChCars : (kind == RelayKind.Traffic ? NetProto.ChTraffic : NetProto.ChPeds);
            server.Send(packet, ch, DeliveryMethod.Unreliable);
        }

        public int MaxUnreliable { get { return server != null ? server.GetMaxSinglePacketSize(DeliveryMethod.Unreliable) : 500; } }

        // ------------------------------------------------------------------ eventos de LiteNetLib
        public void OnPeerConnected(NetPeer peer) { }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (peer != server) return;
            bool wasConnected = State == ClientState.Connected;
            string reason = null;
            if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
            {
                try { reason = info.AdditionalData.GetString(200); } catch (Exception) { reason = null; }
            }
            if (reason == null)
            {
                switch (info.Reason)
                {
                    case DisconnectReason.ConnectionFailed:
                    case DisconnectReason.Timeout:
                        reason = wasConnected ? "Se ha perdido la conexión con el servidor." :
                            "El servidor no responde. ¿Está encendido y con el puerto UDP abierto?"; break;
                    case DisconnectReason.HostUnreachable:
                    case DisconnectReason.NetworkUnreachable:
                        reason = "No hay conexión a Internet o el servidor no es accesible."; break;
                    case DisconnectReason.RemoteConnectionClose:
                        reason = "El servidor ha cerrado la conexión."; break;
                    case DisconnectReason.DisconnectPeerCalled:
                        reason = null; break;
                    default: reason = "Desconectado (" + info.Reason + ")."; break;
                }
            }
            LastError = reason;
            server = null;
            Room = null;
            State = ClientState.Offline;
            Disconnected?.Invoke();
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { LastError = "Error de red: " + socketError; }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader r, byte channel, DeliveryMethod method)
        {
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

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        public void OnConnectionRequest(ConnectionRequest request) { request.Reject(); }
    }
}
