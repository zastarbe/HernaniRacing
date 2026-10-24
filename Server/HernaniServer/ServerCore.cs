// Servidor de Hernani Racing: salas de hasta 8 jugadores, reloj común y reenvío de las posiciones.
// No simula física: cada jugador simula su coche (y el tráfico/peatones que ha creado a su alrededor)
// y el líder de la sala simula los coches de la IA. El servidor ordena la sala y reparte los datos.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using HernaniRacing.Net;
using LiteNetLib;
using LiteNetLib.Utils;

namespace HernaniRacing.Server
{
    public class ServerConfig
    {
        public int port = NetProto.DefaultPort;
        public string password = "";
        public string serverName = "Servidor Hernani Racing";
        public string motd = "¡Bienvenidos! Conducid con cuidado por Hernani.";
        public int maxPlayers = 64;
        public int maxRooms = 16;
        public bool logRelay = false;
        /// <summary>Versión web: puerto TCP de la página del juego y del WebSocket (0 = sin versión web).</summary>
        public int webPort = NetProto.DefaultWebPort;
        /// <summary>Carpeta con el build WebGL de Unity (index.html, Build/, TemplateData/).</summary>
        public string webRoot = "/srv/hernani-web";
    }

    class Player
    {
        public IConn conn;
        public byte id;
        public PlayerProfile profile;
        public string dataSig;
        public Room room;
        public byte slot;
        public bool inGame;
        public long joinedSeq;
        public double lastRelay;
        public int relayCount;
    }

    class Room
    {
        public string code, name, dataSig;
        public Player leader;
        public RoomPhase phase;
        public double startAt;
        public int gameSerial;
        public RoomSettings settings = new RoomSettings();
        public readonly List<Player> players = new List<Player>();
    }

    public class ServerCore : INetEventListener
    {
        readonly ServerConfig cfg;
        readonly NetManager net;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Dictionary<IConn, Player> players = new Dictionary<IConn, Player>();
        readonly Dictionary<int, UdpConn> udpConns = new Dictionary<int, UdpConn>();
        WebHost web;
        double nextWsPing;
        readonly NetDataReader wsReader = new NetDataReader();
        readonly NetDataWriter pingW = new NetDataWriter();
        readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();
        readonly NetDataWriter w = new NetDataWriter();
        readonly NetDataWriter relay = new NetDataWriter();
        readonly Random rnd = new Random();
        long seq;
        double nextPingBroadcast;
        public Action<string> Log = Console.WriteLine;

        public ServerCore(ServerConfig c)
        {
            cfg = c;
            net = new NetManager(this)
            {
                ChannelsCount = (byte)NetProto.Channels,
                DisconnectTimeout = 10000,
                UpdateTime = 5,
                AutoRecycle = true,
                UnsyncedEvents = false,
                IPv6Enabled = false
            };
        }

        public double Now { get { return clock.Elapsed.TotalSeconds; } }
        public int PlayerCount { get { return players.Count; } }
        public int RoomCount { get { return rooms.Count; } }

        public bool Start()
        {
            bool ok = net.Start(IPAddress.Any, IPAddress.IPv6Any, cfg.port);
            if (ok) Log($"[{Stamp()}] Escuchando en UDP {cfg.port} · contraseña {(string.IsNullOrEmpty(cfg.password) ? "NO (cualquiera puede entrar)" : "sí")} · máx. {cfg.maxPlayers} jugadores");
            else Log($"[{Stamp()}] ERROR: no puedo abrir el puerto UDP {cfg.port} (¿ya hay otro servidor?)");
            if (ok && cfg.webPort > 0)
            {
                web = new WebHost(cfg, () => Now) { Log = Log };
                if (!web.Start()) web = null;      // sin versión web, pero el juego de PC sigue funcionando
            }
            return ok;
        }

        public void Stop() { net.Stop(); web?.Stop(); }

        /// <summary>Bucle principal: llamar a menudo (cada 2-5 ms).</summary>
        public void Tick()
        {
            net.PollEvents();
            if (web != null) PollWeb();
            if (Now >= nextPingBroadcast)
            {
                nextPingBroadcast = Now + 3.0;
                foreach (var r in rooms.Values) BroadcastRoom(r);
            }
        }

        static string Stamp() { return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); }

        // ------------------------------------------------------------------ conexiones
        /// <summary>Comprueba los datos de conexión: null si puede entrar, o el motivo del rechazo.</summary>
        string Check(NetDataReader data, out ConnectInfo info)
        {
            info = null;
            try { info = ConnectInfo.Read(data); }
            catch (Exception) { return "Petición no válida."; }
            if (info.game != NetProto.Game) return "Esto no es un cliente de Hernani Racing.";
            if (info.version != NetProto.Version)
                return info.version < NetProto.Version
                    ? "Tu juego es más antiguo que el servidor. Actualiza el juego."
                    : "El servidor es más antiguo que tu juego. Hay que actualizar el servidor.";
            if (!string.IsNullOrEmpty(cfg.password) && info.password != cfg.password) return "Contraseña del servidor incorrecta.";
            if (players.Count >= cfg.maxPlayers) return "El servidor está lleno.";
            return null;
        }

        Player AddPlayer(IConn conn, ConnectInfo info, byte id)
        {
            var p = new Player { conn = conn, id = id, profile = info.profile, dataSig = info.dataSig };
            players[conn] = p;
            Log($"[{Stamp()}] + {p.profile.nick} (#{id}) desde {conn.Remote} ({conn.Kind}) · {players.Count} conectados");
            w.Reset(); w.Put((byte)Msg.Welcome); w.Put(id); w.Put(cfg.serverName); w.Put(cfg.motd);
            conn.Send(w, NetProto.ChReliable, true);
            return p;
        }

        void RemovePlayer(IConn conn, string why)
        {
            Player p;
            if (!players.TryGetValue(conn, out p)) return;
            players.Remove(conn);
            Log($"[{Stamp()}] - {p.profile.nick} (#{p.id}) {why} · {players.Count} conectados");
            LeaveRoom(p, false);
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            ConnectInfo info;
            string why = Check(request.Data, out info);
            byte id = why == null ? FreeId() : (byte)0;
            if (why == null && id == 0) why = "El servidor está lleno.";
            if (why != null) { Reject(request, why); return; }
            var peer = request.Accept();
            var conn = new UdpConn(peer, request.RemoteEndPoint.ToString());
            udpConns[peer.Id] = conn;
            AddPlayer(conn, info, id);
        }

        void Reject(ConnectionRequest req, string why)
        {
            var rw = new NetDataWriter(); rw.Put(why);
            req.Reject(rw);
            Log($"[{Stamp()}] x rechazado {req.RemoteEndPoint}: {why}");
        }

        // ------------------------------------------------------------------ jugadores de la versión web (WebSocket)
        void PollWeb()
        {
            WsEvent ev;
            int guard = 0;
            while (guard++ < 5000 && web.events.TryDequeue(out ev))
            {
                var c = ev.conn;
                if (ev.data == null) { RemovePlayer(c, "web: desconectado"); continue; }
                c.lastRecv = Now;
                var d = ev.data;
                if (d[0] == NetProto.WsPing)
                {
                    if (d.Length >= 9)
                    {
                        double rtt = Now - BitConverter.ToDouble(d, 1);
                        if (rtt >= 0 && rtt < 10) c.rttSmoothed = c.rttSmoothed < 0 ? rtt : c.rttSmoothed + (rtt - c.rttSmoothed) * 0.3;
                    }
                    continue;
                }
                if (!c.accepted)
                {
                    if (d[0] != NetProto.WsHello) { c.Abort(); continue; }
                    wsReader.SetSource(d, 1, d.Length);
                    ConnectInfo info;
                    string why = Check(wsReader, out info);
                    byte id = why == null ? FreeId() : (byte)0;
                    if (why == null && id == 0) why = "El servidor está lleno.";
                    if (why != null) { Log($"[{Stamp()}] x rechazado {c.Remote} (web): {why}"); c.Reject(why); continue; }
                    c.accepted = true;
                    AddPlayer(c, info, id);
                    continue;
                }
                Player p;
                if (!players.TryGetValue(c, out p)) continue;
                wsReader.SetSource(d, 0, d.Length);
                Handle(p, wsReader);
            }
            if (Now >= nextWsPing)
            {
                nextWsPing = Now + 1.0;
                pingW.Reset(); pingW.Put(NetProto.WsPing); pingW.Put(Now);
                var lost = new List<IConn>();
                foreach (var c in players.Keys)
                {
                    var wc = c as WsConn;
                    if (wc == null) continue;
                    if (Now - wc.lastRecv > 20.0 || wc.IsClosed) { lost.Add(wc); continue; }
                    wc.SendBytes(pingW.Data, pingW.Length, false);
                }
                foreach (var c in lost) { RemovePlayer(c, "web: sin respuesta"); ((WsConn)c).Abort(); }
            }
        }

        byte FreeId()
        {
            var used = new HashSet<byte>(players.Values.Select(p => p.id));
            for (int i = 0; i < 250; i++)
            {
                byte c = (byte)(1 + rnd.Next(250));
                if (!used.Contains(c)) return c;
            }
            for (byte c = 1; c <= 250; c++) if (!used.Contains(c)) return c;
            return 0;
        }

        public void OnPeerConnected(NetPeer peer) { }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            UdpConn c;
            if (!udpConns.TryGetValue(peer.Id, out c)) return;
            udpConns.Remove(peer.Id);
            RemovePlayer(c, info.Reason.ToString());
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { Log($"[{Stamp()}] error de red {endPoint}: {socketError}"); }
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

        // ------------------------------------------------------------------ mensajes
        public void OnNetworkReceive(NetPeer peer, NetPacketReader r, byte channel, DeliveryMethod method)
        {
            UdpConn c;
            Player p;
            if (!udpConns.TryGetValue(peer.Id, out c) || !players.TryGetValue(c, out p)) return;
            Handle(p, r);
        }

        void Handle(Player p, NetDataReader r)
        {
            if (r.AvailableBytes < 1) return;
            var m = (Msg)r.GetByte();
            try
            {
                switch (m)
                {
                    case Msg.TimeReq:
                        {
                            double t = r.GetDouble();
                            w.Reset(); w.Put((byte)Msg.TimeResp); w.Put(t); w.Put(Now);
                            p.conn.Send(w, NetProto.ChReliable, false);
                            break;
                        }
                    case Msg.ListRooms: SendRoomList(p); break;
                    case Msg.CreateRoom: CreateRoom(p, r.GetString(48)); break;
                    case Msg.JoinRoom: JoinRoom(p, r.GetString(16)); break;
                    case Msg.LeaveRoom: LeaveRoom(p, true); break;
                    case Msg.Profile:
                        p.profile = PlayerProfile.Read(r);
                        if (p.room != null) BroadcastRoom(p.room);
                        break;
                    case Msg.Settings:
                        {
                            var s = RoomSettings.Read(r);
                            var room = p.room;
                            if (room == null || room.leader != p || room.phase != RoomPhase.Lobby) break;
                            room.settings = s;
                            BroadcastRoom(room);
                            break;
                        }
                    case Msg.Start: StartGame(p, (GameMode)r.GetByte()); break;
                    case Msg.EnterGame:
                        if (p.room != null && p.room.phase == RoomPhase.Free && !p.inGame) { p.inGame = true; BroadcastRoom(p.room); }
                        break;
                    case Msg.LeaveGame:
                        if (p.room != null && p.inGame)
                        {
                            p.inGame = false;
                            CheckGameOver(p.room);
                            BroadcastRoom(p.room);
                        }
                        break;
                    case Msg.EndGame:
                        if (p.room != null && p.room.leader == p && p.room.phase != RoomPhase.Lobby) EndGame(p.room, "el líder ha vuelto a la sala");
                        break;
                    case Msg.Relay: Relay(p, r); break;
                }
            }
            catch (Exception e)
            {
                Log($"[{Stamp()}] mensaje {m} dañado de {p.profile.nick}: {e.Message}");
            }
        }

        void SendError(Player p, string text)
        {
            w.Reset(); w.Put((byte)Msg.Error); w.Put(text);
            p.conn.Send(w, NetProto.ChReliable, true);
        }

        void SendRoomList(Player p)
        {
            w.Reset(); w.Put((byte)Msg.RoomList);
            var list = rooms.Values.Where(x => x.dataSig == p.dataSig || string.IsNullOrEmpty(p.dataSig)).ToList();
            w.Put((ushort)list.Count);
            foreach (var x in list)
                new RoomListEntry { code = x.code, name = x.name, leaderNick = x.leader != null ? x.leader.profile.nick : "", players = (byte)x.players.Count, phase = x.phase }.Write(w);
            p.conn.Send(w, NetProto.ChReliable, true);
        }

        void BroadcastRoomList()
        {
            foreach (var p in players.Values) if (p.room == null) SendRoomList(p);
        }

        string NewCode()
        {
            const string letters = "ABCDEFGHJKLMNPRSTUVWXYZ";   // sin I, O, Q (se confunden)
            for (int k = 0; k < 1000; k++)
            {
                var c = new char[4];
                for (int i = 0; i < 4; i++) c[i] = letters[rnd.Next(letters.Length)];
                string s = new string(c);
                if (!rooms.ContainsKey(s)) return s;
            }
            return null;
        }

        void CreateRoom(Player p, string name)
        {
            if (rooms.Count >= cfg.maxRooms) { SendError(p, "No caben más salas en el servidor."); return; }
            LeaveRoom(p, false);
            name = PlayerProfile.CleanNick(name);
            if (name == "Piloto") name = "Sala de " + p.profile.nick;
            var room = new Room { code = NewCode(), name = name, dataSig = p.dataSig, leader = p, phase = RoomPhase.Lobby };
            room.settings.aiCar = p.profile.carId;
            rooms[room.code] = room;
            AddToRoom(p, room);
            Log($"[{Stamp()}] sala {room.code} \"{room.name}\" creada por {p.profile.nick}");
            BroadcastRoomList();
        }

        void JoinRoom(Player p, string code)
        {
            Room room;
            code = (code ?? "").Trim().ToUpperInvariant();
            if (!rooms.TryGetValue(code, out room)) { SendError(p, "No existe la sala " + code + "."); return; }
            if (room == p.room) { BroadcastRoom(room); return; }
            if (room.players.Count >= NetProto.MaxPlayersPerRoom) { SendError(p, "La sala está llena (máximo " + NetProto.MaxPlayersPerRoom + ")."); return; }
            if (!string.IsNullOrEmpty(room.dataSig) && room.dataSig != p.dataSig) { SendError(p, "Tu juego tiene otra versión de los datos de Hernani. Usad todos la misma versión."); return; }
            LeaveRoom(p, false);
            AddToRoom(p, room);
            Log($"[{Stamp()}] {p.profile.nick} entra en {room.code}");
            BroadcastRoomList();
        }

        void AddToRoom(Player p, Room room)
        {
            var used = new HashSet<byte>(room.players.Select(x => x.slot));
            byte slot = 0;
            while (used.Contains(slot)) slot++;
            p.slot = slot;
            p.room = room;
            p.inGame = false;
            p.joinedSeq = ++seq;
            room.players.Add(p);
            BroadcastRoom(room);
        }

        void LeaveRoom(Player p, bool notify)
        {
            var room = p.room;
            if (room == null) return;
            room.players.Remove(p);
            p.room = null;
            p.inGame = false;
            if (notify)
            {
                w.Reset(); w.Put((byte)Msg.LeftRoom);
                p.conn.Send(w, NetProto.ChReliable, true);
            }
            if (room.players.Count == 0)
            {
                rooms.Remove(room.code);
                Log($"[{Stamp()}] sala {room.code} cerrada (vacía)");
            }
            else
            {
                if (room.leader == p)
                {
                    room.leader = room.players.OrderBy(x => x.joinedSeq).First();
                    Log($"[{Stamp()}] {room.code}: nuevo líder {room.leader.profile.nick}");
                }
                CheckGameOver(room);
                BroadcastRoom(room);
            }
            BroadcastRoomList();
        }

        void StartGame(Player p, GameMode mode)
        {
            var room = p.room;
            if (room == null || room.leader != p) return;
            if (room.phase != RoomPhase.Lobby) { SendError(p, "Ya hay una partida en marcha."); return; }
            if (mode == GameMode.Race && (room.settings.nodes == null || room.settings.nodes.Length < 2)) { SendError(p, "Elige primero el circuito (4 cruces)."); return; }
            int humans = room.players.Count;
            if (mode == GameMode.Race && humans + room.settings.opponents > NetProto.MaxCarsPerRace)
                room.settings.opponents = (byte)Math.Max(0, NetProto.MaxCarsPerRace - humans);
            room.phase = mode == GameMode.Race ? RoomPhase.Race : RoomPhase.Free;
            room.startAt = Now + (mode == GameMode.Race ? 7.0 : 0.0);   // 7 s para cargar y la cuenta atrás
            room.gameSerial++;
            foreach (var x in room.players) x.inGame = true;
            Log($"[{Stamp()}] {room.code}: empieza {(mode == GameMode.Race ? "CARRERA" : "PASEO LIBRE")} con {humans} jugadores");
            BroadcastRoom(room);
            BroadcastRoomList();
        }

        void EndGame(Room room, string why)
        {
            room.phase = RoomPhase.Lobby;
            foreach (var x in room.players) x.inGame = false;
            Log($"[{Stamp()}] {room.code}: vuelta a la sala ({why})");
            BroadcastRoom(room);
            BroadcastRoomList();
        }

        void CheckGameOver(Room room)
        {
            if (room.phase != RoomPhase.Lobby && !room.players.Any(x => x.inGame)) EndGame(room, "no queda nadie jugando");
        }

        RoomInfo Info(Room room)
        {
            var ri = new RoomInfo
            {
                code = room.code, name = room.name, leaderId = room.leader != null ? room.leader.id : (byte)0, phase = room.phase,
                startAt = room.startAt, gameSerial = room.gameSerial, settings = room.settings
            };
            foreach (var x in room.players.OrderBy(x => x.slot))
                ri.players.Add(new PlayerInfo { id = x.id, profile = x.profile, slot = x.slot, inGame = x.inGame, ping = (ushort)Math.Min(9999, x.conn.Rtt) });
            return ri;
        }

        void BroadcastRoom(Room room)
        {
            w.Reset(); w.Put((byte)Msg.RoomState);
            Info(room).Write(w);
            foreach (var x in room.players) x.conn.Send(w, NetProto.ChReliable, true);
        }

        // ------------------------------------------------------------------ reenvío de posiciones
        void Relay(Player p, NetDataReader r)
        {
            var room = p.room;
            if (room == null || !p.inGame || room.phase == RoomPhase.Lobby) return;
            int n = r.AvailableBytes;
            if (n < 10 || n > 1400) return;
            // límite de seguridad: como mucho ~120 paquetes por segundo y jugador
            double now = Now;
            if (now - p.lastRelay > 1.0) { p.lastRelay = now; p.relayCount = 0; }
            if (++p.relayCount > 120) return;
            var kind = (RelayKind)r.PeekByte();
            relay.Reset();
            relay.Put((byte)Msg.RelayFrom);
            relay.Put(p.id);
            relay.Put(r.RawData, r.Position, n);
            byte ch = kind == RelayKind.Cars ? NetProto.ChCars : (kind == RelayKind.Traffic ? NetProto.ChTraffic : NetProto.ChPeds);
            foreach (var x in room.players)
                if (x != p && x.inGame) x.conn.Send(relay, ch, false);
        }

        public string Status()
        {
            int webN = players.Keys.Count(c => c is WsConn);
            var lines = new List<string> { $"{players.Count} jugadores conectados ({webN} desde la web), {rooms.Count} salas" };
            foreach (var r in rooms.Values)
                lines.Add($"  {r.code} \"{r.name}\" {r.phase} · líder {r.leader?.profile.nick} · " + string.Join(", ", r.players.Select(x => x.profile.nick + (x.inGame ? "*" : ""))));
            return string.Join(Environment.NewLine, lines);
        }
    }
}
