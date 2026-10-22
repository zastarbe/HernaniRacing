// Protocolo de red de Hernani Racing (compartido entre el juego y el servidor de Ubuntu).
// Este fichero NO usa UnityEngine: lo compilan tanto Unity como el servidor (.NET 8).
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace HernaniRacing.Net
{
    public static class NetProto
    {
        public const string Game = "HernaniRacing";
        public const int Version = 1;               // súbelo si cambias el formato de los mensajes
        public const int DefaultPort = 7777;
        public const int MaxPlayersPerRoom = 8;
        public const int MaxCarsPerRace = 10;        // jugadores + IA
        public const byte AiOwner = 255;             // "dueño" de los coches de la IA (los simula el líder)

        // canales de LiteNetLib
        public const byte ChReliable = 0, ChCars = 1, ChTraffic = 2, ChPeds = 3;
        public const int Channels = 4;
    }

    public enum Msg : byte
    {
        // cliente -> servidor
        TimeReq = 1, ListRooms = 2, CreateRoom = 3, JoinRoom = 4, LeaveRoom = 5, Profile = 6,
        Settings = 7, Start = 8, EnterGame = 9, LeaveGame = 10, EndGame = 11, Relay = 12,
        // servidor -> cliente
        Welcome = 101, TimeResp = 102, RoomList = 103, RoomState = 104, RelayFrom = 105, Error = 106, LeftRoom = 107
    }

    /// <summary>Qué viaja dentro de un mensaje Relay (el servidor no lo interpreta, sólo lo reenvía).</summary>
    public enum RelayKind : byte { Cars = 1, Traffic = 2, Peds = 3 }

    public enum RoomPhase : byte { Lobby = 0, Race = 1, Free = 2 }
    public enum GameMode : byte { Race = 0, Free = 1 }

    /// <summary>Datos que el cliente manda al conectar (el servidor los comprueba).</summary>
    public class ConnectInfo
    {
        public string game = NetProto.Game;
        public int version = NetProto.Version;
        public string password = "";
        public string dataSig = "";
        public PlayerProfile profile = new PlayerProfile();

        public void Write(NetDataWriter w)
        {
            w.Put(game); w.Put(version); w.Put(password ?? ""); w.Put(dataSig ?? ""); profile.Write(w);
        }

        public static ConnectInfo Read(NetDataReader r)
        {
            var c = new ConnectInfo();
            c.game = r.GetString(64); c.version = r.GetInt(); c.password = r.GetString(128); c.dataSig = r.GetString(128);
            c.profile = PlayerProfile.Read(r);
            return c;
        }
    }

    public class PlayerProfile
    {
        public string nick = "Piloto";
        public string carId = "turismo";
        public int color = 0xD9141A;   // RGB 24 bits

        public void Write(NetDataWriter w) { w.Put(nick ?? ""); w.Put(carId ?? ""); w.Put(color); }

        public static PlayerProfile Read(NetDataReader r)
        {
            var p = new PlayerProfile { nick = r.GetString(32), carId = r.GetString(32), color = r.GetInt() & 0xFFFFFF };
            p.nick = CleanNick(p.nick);
            if (string.IsNullOrEmpty(p.carId)) p.carId = "turismo";
            return p;
        }

        public static string CleanNick(string s)
        {
            if (s == null) return "Piloto";
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s)
            {
                if (char.IsControl(ch)) continue;
                sb.Append(ch);
                if (sb.Length >= 16) break;
            }
            string t = sb.ToString().Trim();
            return t.Length == 0 ? "Piloto" : t;
        }
    }

    /// <summary>Lo que elige el líder de la sala: circuito, vueltas, IA, tráfico...</summary>
    public class RoomSettings
    {
        public int[] nodes = new int[0];     // los 4 cruces del circuito (ids de nodo del grafo)
        public byte laps = 3;
        public byte opponents = 0;           // coches de la IA
        public byte difficulty = 1;
        public bool traffic = true, pedestrians = true;          // carrera
        public bool freeTraffic = true, freePedestrians = true;  // paseo libre
        public string aiCar = "turismo";
        public int freeStartNode = -1;
        public string circuitName = "";

        public void Write(NetDataWriter w)
        {
            w.PutArray(nodes ?? new int[0]);
            w.Put(laps); w.Put(opponents); w.Put(difficulty);
            byte f = 0;
            if (traffic) f |= 1; if (pedestrians) f |= 2; if (freeTraffic) f |= 4; if (freePedestrians) f |= 8;
            w.Put(f);
            w.Put(aiCar ?? "turismo"); w.Put(freeStartNode); w.Put(circuitName ?? "");
        }

        public static RoomSettings Read(NetDataReader r)
        {
            var s = new RoomSettings();
            s.nodes = r.GetIntArray();
            if (s.nodes.Length > 8) Array.Resize(ref s.nodes, 8);
            s.laps = (byte)Math.Max(1, Math.Min(20, (int)r.GetByte()));
            s.opponents = (byte)Math.Min(NetProto.MaxCarsPerRace - 1, (int)r.GetByte());
            s.difficulty = (byte)Math.Min(2, (int)r.GetByte());
            byte f = r.GetByte();
            s.traffic = (f & 1) != 0; s.pedestrians = (f & 2) != 0; s.freeTraffic = (f & 4) != 0; s.freePedestrians = (f & 8) != 0;
            s.aiCar = r.GetString(32); s.freeStartNode = r.GetInt(); s.circuitName = r.GetString(64);
            return s;
        }

        public RoomSettings Clone()
        {
            var w = new NetDataWriter(); Write(w);
            return Read(new NetDataReader(w.CopyData()));
        }

        public bool SameAs(RoomSettings o)
        {
            if (o == null) return false;
            var a = new NetDataWriter(); Write(a);
            var b = new NetDataWriter(); o.Write(b);
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a.Data[i] != b.Data[i]) return false;
            return true;
        }
    }

    public class PlayerInfo
    {
        public byte id;
        public PlayerProfile profile = new PlayerProfile();
        public byte slot;          // 0..7: orden en la parrilla y en las listas
        public bool inGame;        // está dentro de la carrera / paseo en curso
        public ushort ping;

        public void Write(NetDataWriter w) { w.Put(id); profile.Write(w); w.Put(slot); w.Put(inGame); w.Put(ping); }

        public static PlayerInfo Read(NetDataReader r)
        {
            return new PlayerInfo { id = r.GetByte(), profile = PlayerProfile.Read(r), slot = r.GetByte(), inGame = r.GetBool(), ping = r.GetUShort() };
        }
    }

    public class RoomInfo
    {
        public string code = "";
        public string name = "";
        public byte leaderId;
        public RoomPhase phase;
        public double startAt;      // hora del servidor (s) a la que empieza la carrera
        public int gameSerial;      // cambia cada vez que empieza una partida nueva
        public RoomSettings settings = new RoomSettings();
        public List<PlayerInfo> players = new List<PlayerInfo>();

        public void Write(NetDataWriter w)
        {
            w.Put(code); w.Put(name); w.Put(leaderId); w.Put((byte)phase); w.Put(startAt); w.Put(gameSerial);
            settings.Write(w);
            w.Put((byte)players.Count);
            foreach (var p in players) p.Write(w);
        }

        public static RoomInfo Read(NetDataReader r)
        {
            var ri = new RoomInfo
            {
                code = r.GetString(16), name = r.GetString(48), leaderId = r.GetByte(), phase = (RoomPhase)r.GetByte(),
                startAt = r.GetDouble(), gameSerial = r.GetInt(), settings = RoomSettings.Read(r)
            };
            int n = r.GetByte();
            for (int i = 0; i < n; i++) ri.players.Add(PlayerInfo.Read(r));
            return ri;
        }

        public PlayerInfo Find(byte id)
        {
            foreach (var p in players) if (p.id == id) return p;
            return null;
        }

        public PlayerInfo Leader { get { return Find(leaderId); } }
    }

    public class RoomListEntry
    {
        public string code, name, leaderNick;
        public byte players;
        public RoomPhase phase;

        public void Write(NetDataWriter w) { w.Put(code); w.Put(name); w.Put(leaderNick); w.Put(players); w.Put((byte)phase); }
        public static RoomListEntry Read(NetDataReader r)
        {
            return new RoomListEntry { code = r.GetString(16), name = r.GetString(48), leaderNick = r.GetString(32), players = r.GetByte(), phase = (RoomPhase)r.GetByte() };
        }
    }

    // ------------------------------------------------------------------ instantáneas (las monta el cliente)

    /// <summary>Estado de un coche de carrera/paseo (jugador o IA) tal y como lo manda su dueño.</summary>
    public struct CarState
    {
        public byte owner, local;        // owner: id del jugador (o AiOwner); local: 0 = su coche, 1.. = IA
        public float px, py, pz;
        public float qx, qy, qz, qw;
        public float vx, vy, vz;
        public float steer;              // -1..1
        public float progress;           // metros recorridos en la carrera
        public byte laps;
        public byte flags;               // 1 terminado, 2 fuera de pista, 4 sentido contrario, 8 frenando
        public float finishTime, bestLap;

        public const byte Finished = 1, OffTrack = 2, WrongWay = 4, Braking = 8;

        public void Write(NetDataWriter w)
        {
            w.Put(owner); w.Put(local);
            w.Put(px); w.Put(py); w.Put(pz);
            PutQuat(w, qx, qy, qz, qw);
            w.Put(Half(vx, 50f)); w.Put(Half(vy, 50f)); w.Put(Half(vz, 50f));
            w.Put((sbyte)Math.Round(Math.Max(-1f, Math.Min(1f, steer)) * 127f));
            w.Put(progress); w.Put(laps); w.Put(flags);
            if ((flags & Finished) != 0) w.Put(finishTime);
            w.Put(bestLap);
        }

        public static CarState Read(NetDataReader r)
        {
            var c = new CarState();
            c.owner = r.GetByte(); c.local = r.GetByte();
            c.px = r.GetFloat(); c.py = r.GetFloat(); c.pz = r.GetFloat();
            GetQuat(r, out c.qx, out c.qy, out c.qz, out c.qw);
            c.vx = Unhalf(r.GetShort(), 50f); c.vy = Unhalf(r.GetShort(), 50f); c.vz = Unhalf(r.GetShort(), 50f);
            c.steer = r.GetSByte() / 127f;
            c.progress = r.GetFloat(); c.laps = r.GetByte(); c.flags = r.GetByte();
            c.finishTime = (c.flags & Finished) != 0 ? r.GetFloat() : 0f;
            c.bestLap = r.GetFloat();
            return c;
        }

        public const int MaxSize = 2 + 12 + 8 + 6 + 1 + 4 + 2 + 4 + 4;

        // --- compresión: velocidad en short (rango ±range m/s) y cuaternión en 4 shorts
        public static short Half(float v, float range) { return (short)Math.Round(Math.Max(-1f, Math.Min(1f, v / range)) * 32767f); }
        public static float Unhalf(short v, float range) { return v / 32767f * range; }

        public static void PutQuat(NetDataWriter w, float x, float y, float z, float ww)
        {
            float n = (float)Math.Sqrt(x * x + y * y + z * z + ww * ww);
            if (n < 1e-6f) { x = y = z = 0; ww = 1; n = 1; }
            w.Put(Half(x / n, 1f)); w.Put(Half(y / n, 1f)); w.Put(Half(z / n, 1f)); w.Put(Half(ww / n, 1f));
        }

        public static void GetQuat(NetDataReader r, out float x, out float y, out float z, out float w)
        {
            x = Unhalf(r.GetShort(), 1f); y = Unhalf(r.GetShort(), 1f); z = Unhalf(r.GetShort(), 1f); w = Unhalf(r.GetShort(), 1f);
            float n = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            if (n < 1e-6f) { x = y = z = 0; w = 1; } else { x /= n; y /= n; z /= n; w /= n; }
        }
    }

    /// <summary>Coche del tráfico (lo simula el jugador que lo creó).</summary>
    public struct TrafficState
    {
        public ushort id;
        public byte model;           // índice en CarModels.TrafficIds
        public byte r, g, b;         // pintura
        public float px, py, pz;
        public float qx, qy, qz, qw;
        public float speed;
        public byte flags;           // 1 chocado (física libre)
        public ushort junction;      // nodo del cruce que está atravesando (0xFFFF = ninguno)

        public void Write(NetDataWriter w)
        {
            w.Put(id); w.Put(model); w.Put(r); w.Put(g); w.Put(b);
            w.Put(px); w.Put(py); w.Put(pz);
            CarState.PutQuat(w, qx, qy, qz, qw);
            w.Put(CarState.Half(speed, 60f)); w.Put(flags); w.Put(junction);
        }

        public static TrafficState Read(NetDataReader rd)
        {
            var t = new TrafficState();
            t.id = rd.GetUShort(); t.model = rd.GetByte(); t.r = rd.GetByte(); t.g = rd.GetByte(); t.b = rd.GetByte();
            t.px = rd.GetFloat(); t.py = rd.GetFloat(); t.pz = rd.GetFloat();
            CarState.GetQuat(rd, out t.qx, out t.qy, out t.qz, out t.qw);
            t.speed = CarState.Unhalf(rd.GetShort(), 60f); t.flags = rd.GetByte(); t.junction = rd.GetUShort();
            return t;
        }

        public const int Size = 2 + 4 + 12 + 8 + 2 + 1 + 2;
    }

    /// <summary>Peatón (lo simula el jugador que lo creó).</summary>
    public struct PedState
    {
        public ushort id;
        public int look;             // semilla del aspecto (ropa, piel, altura)
        public float px, py, pz;
        public float yaw;            // grados
        public byte state;           // 0 andando, 1 esperando para cruzar, 2 cruzando, 3 parado
        public ushort crossEdge;     // si está esperando/cruzando: tramo y posición del paso de cebra
        public ushort crossS;        // s * 4

        public void Write(NetDataWriter w)
        {
            w.Put(id); w.Put(look); w.Put(px); w.Put(py); w.Put(pz);
            w.Put((ushort)(((yaw % 360f) + 360f) % 360f * 65535f / 360f));
            w.Put(state);
            if (state == 1 || state == 2) { w.Put(crossEdge); w.Put(crossS); }
        }

        public static PedState Read(NetDataReader r)
        {
            var p = new PedState();
            p.id = r.GetUShort(); p.look = r.GetInt(); p.px = r.GetFloat(); p.py = r.GetFloat(); p.pz = r.GetFloat();
            p.yaw = r.GetUShort() * 360f / 65535f;
            p.state = r.GetByte();
            if (p.state == 1 || p.state == 2) { p.crossEdge = r.GetUShort(); p.crossS = r.GetUShort(); }
            return p;
        }

        public const int MaxSize = 2 + 4 + 12 + 2 + 1 + 4;
    }

    /// <summary>Cabecera común de los paquetes Relay: hora del servidor a la que corresponde la instantánea.</summary>
    public static class RelayCodec
    {
        public static void Begin(NetDataWriter w, RelayKind kind, double serverTime, int count)
        {
            w.Reset();
            w.Put((byte)Msg.Relay);
            w.Put((byte)kind);
            w.Put(serverTime);
            w.Put((byte)count);
        }

        /// <summary>Máximo de bytes útiles por paquete no fiable (cabe en una MTU mínima de LiteNetLib).</summary>
        public const int MaxPayload = 1000;
    }
}
