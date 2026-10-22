using System.Collections.Generic;
using HernaniRacing.Net;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>Hora común de la partida: la del servidor si se juega online; si no, la del juego.</summary>
    public static class NetClock
    {
        /// <summary>Segundos (float) para ciclos repetitivos como los semáforos (iguales en todos los PCs).</summary>
        public static float Time
        {
            get
            {
                var s = NetSession.I;
                if (s != null && s.Core.State == ClientState.Connected && s.Core.ClockSynced) return (float)(s.Core.ServerTime % 86400.0);
                return UnityEngine.Time.time;
            }
        }
    }

    /// <summary>
    /// Conexión con el servidor propio (Ubuntu): salas, perfil del jugador y cambio entre sala / carrera / paseo.
    /// Está siempre en el objeto del GameBootstrap; si no se conecta, el juego funciona igual que antes (sin red).
    /// </summary>
    public class NetSession : MonoBehaviour
    {
        public static NetSession I { get; private set; }
        public GameBootstrap game;
        public readonly NetClientCore Core = new NetClientCore();

        // datos guardados en el PC
        public string host, password, nick;
        public int port;
        public int myColor = 0xD9141A;
        public string myCar = "turismo";

        public string Status { get; private set; } = "";
        public string Toast { get; private set; }
        float toastUntil;
        int startedSerial = -1;
        RoomPhase lastPhase = RoomPhase.Lobby;
        bool lastLeader;
        public event System.Action BecameLeader;

        public bool Connected { get { return Core.State == ClientState.Connected; } }
        public bool InRoom { get { return Connected && Core.InRoom; } }
        public bool IsLeader { get { return InRoom && Core.IsLeader; } }
        public byte MyId { get { return Core.MyId; } }
        public RoomInfo Room { get { return Core.Room; } }
        /// <summary>Se está jugando una partida online (carrera o paseo) ahora mismo.</summary>
        public bool InOnlineGame { get; private set; }

        void Awake()
        {
            I = this;
            // si el juego pierde el foco (Alt+Tab) tiene que seguir enviando y recibiendo, o los demás verían el coche parado
            Application.runInBackground = true;
            host = PlayerPrefs.GetString("net.host", NetConfig.DefaultHost);
            port = PlayerPrefs.GetInt("net.port", NetConfig.DefaultPort);
            password = PlayerPrefs.GetString("net.password", "");
            nick = PlayerPrefs.GetString("net.nick", "Piloto" + Random.Range(10, 99));
            myCar = PlayerPrefs.GetString("net.car", "turismo");
            myColor = PlayerPrefs.GetInt("net.color", 0xD9141A);
            Core.RoomChanged += OnRoomChanged;
            Core.ErrorReceived += e => ShowToast(e, 5f);
            Core.Disconnected += OnDisconnected;
        }

        void OnDestroy()
        {
            Core.Shutdown();
            if (I == this) I = null;
        }

        void OnApplicationQuit() { Core.Shutdown(); }

        public void ShowToast(string text, float secs) { Toast = text; toastUntil = Time.unscaledTime + secs; }
        public bool ToastVisible { get { return !string.IsNullOrEmpty(Toast) && Time.unscaledTime < toastUntil; } }

        public string DataSignature()
        {
            if (game == null || game.world == null || game.world.data == null) return "";
            var w = game.world;
            return (w.data.meta != null ? w.data.meta.generated : "") + "|" + w.graph.edges.Length + "|" + w.graph.nodes.Length;
        }

        public void Connect()
        {
            PlayerPrefs.SetString("net.host", host.Trim());
            PlayerPrefs.SetInt("net.port", port);
            PlayerPrefs.SetString("net.password", password);
            PlayerPrefs.SetString("net.nick", nick);
            PlayerPrefs.Save();
            UpdateProfile(false);
            Status = "Conectando con " + host + ":" + port + "...";
            Core.Connect(host, port, password, DataSignature());
            if (Core.State == ClientState.Offline) Status = Core.LastError;
        }

        public void Disconnect()
        {
            Core.Disconnect();
            Status = "Desconectado.";
            if (InOnlineGame) { InOnlineGame = false; game.ShowSelector(); }
        }

        /// <summary>Guarda el coche, el color y el apodo y, si estamos conectados, avisa al servidor.</summary>
        public void SetCarAndColor(string carId, Color paint)
        {
            int c = ColorToInt(paint);
            if (carId == myCar && c == myColor) return;
            myCar = carId; myColor = c;
            PlayerPrefs.SetString("net.car", myCar);
            PlayerPrefs.SetInt("net.color", myColor);
            UpdateProfile(true);
        }

        public void UpdateProfile(bool send)
        {
            Core.Profile.nick = PlayerProfile.CleanNick(nick);
            Core.Profile.carId = myCar;
            Core.Profile.color = myColor;
            if (send && Connected) Core.SendProfile();
        }

        public static int ColorToInt(Color c)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255), g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255), b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            return (r << 16) | (g << 8) | b;
        }

        public static Color IntToColor(int v)
        {
            return new Color(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
        }

        void Update()
        {
            Core.Poll();
            if (Core.State == ClientState.Connecting) Status = "Conectando con " + host + ":" + port + "...";
            else if (Core.State == ClientState.Connected) Status = "Conectado a " + Core.ServerName + " · ping " + Core.Ping + " ms";
            if (InRoom)
            {
                bool lead = Core.IsLeader;
                if (lead && !lastLeader && BecameLeader != null) BecameLeader();
                lastLeader = lead;
            }
        }

        void OnDisconnected()
        {
            Status = Core.LastError ?? "Desconectado.";
            if (!string.IsNullOrEmpty(Core.LastError)) ShowToast(Core.LastError, 6f);
            if (InOnlineGame)
            {
                InOnlineGame = false;
                game.ShowSelector();
            }
        }

        // ------------------------------------------------------------------ flujo sala -> partida -> sala
        void OnRoomChanged()
        {
            var room = Core.Room;
            if (room == null)
            {
                lastLeader = false;
                if (InOnlineGame) { InOnlineGame = false; game.ShowSelector(); }
                return;
            }
            var me = room.Find(Core.MyId);
            bool shouldPlay = me != null && me.inGame && room.phase != RoomPhase.Lobby;
            if (shouldPlay && (!InOnlineGame || startedSerial != room.gameSerial))
            {
                startedSerial = room.gameSerial;
                InOnlineGame = true;
                if (!game.StartOnline(room)) { InOnlineGame = false; Core.LeaveGame(); }
            }
            else if (!shouldPlay && InOnlineGame)
            {
                InOnlineGame = false;
                game.ShowSelector();
                if (lastPhase != RoomPhase.Lobby && room.phase == RoomPhase.Lobby) ShowToast("De vuelta en la sala " + room.name, 3f);
            }
            lastPhase = room.phase;
        }

        /// <summary>Salir de la partida en curso (me quedo en la sala).</summary>
        public void LeaveGame()
        {
            Core.LeaveGame();
            if (InOnlineGame) { InOnlineGame = false; game.ShowSelector(); }
        }

        /// <summary>Ajustes de la sala a partir de lo elegido en el selector (sólo el líder).</summary>
        public static RoomSettings MakeSettings(List<int> nodes, RaceSettings s, string circuitName)
        {
            return new RoomSettings
            {
                nodes = nodes.ToArray(), laps = (byte)Mathf.Clamp(s.laps, 1, 20), opponents = (byte)Mathf.Clamp(s.opponents, 0, NetProto.MaxCarsPerRace - 1),
                difficulty = (byte)Mathf.Clamp(s.difficulty, 0, 2), traffic = s.traffic, pedestrians = s.pedestrians,
                freeTraffic = s.freeTraffic, freePedestrians = s.freePedestrians, aiCar = s.carId, freeStartNode = nodes.Count > 0 ? nodes[0] : -1,
                circuitName = circuitName ?? ""
            };
        }

        public RaceSettings SettingsForMe(RoomSettings rs)
        {
            return new RaceSettings
            {
                laps = rs.laps, opponents = rs.opponents, difficulty = rs.difficulty,
                carId = myCar, paint = IntToColor(myColor),
                traffic = rs.traffic, pedestrians = rs.pedestrians, freeTraffic = rs.freeTraffic, freePedestrians = rs.freePedestrians
            };
        }
    }

    public static class NetConfig
    {
        /// <summary>Dirección por defecto del servidor (se puede cambiar en el juego; se guarda en el PC).</summary>
        public const string DefaultHost = "";
        public const int DefaultPort = NetProto.DefaultPort;
    }
}
