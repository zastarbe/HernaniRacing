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
        /// <summary>PC/móvil: UDP (LiteNetLib). Versión web: WebSocket (el navegador no permite UDP). Mismo servidor y mismas salas.</summary>
        public readonly NetClientCore Core = new NetClientCore(
#if UNITY_WEBGL && !UNITY_EDITOR
            new WebSocketTransport()
#else
            new UdpTransport()
#endif
        );
        public static bool IsWebBuild
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            get { return true; }
#else
            get { return false; }
#endif
        }

        // datos guardados en el PC
        public string nick;
        /// <summary>Usar el servidor por defecto (Resources/HernaniNet/server_default.json). Viene activado.</summary>
        public bool useDefault = true;
        /// <summary>Servidor escrito a mano en "Opciones de conexión".</summary>
        public string customHost, customPassword;
        public int customPort;
        NetConfig.ServerDefaults defaults;

        public bool HasDefault { get { return defaults != null && !string.IsNullOrEmpty(defaults.host); } }
        bool UseDefaultNow { get { return HasDefault && useDefault; } }
        public string EffectiveHost { get { return (UseDefaultNow ? defaults.host : customHost ?? "").Trim(); } }
        public int EffectivePort
        {
            get
            {
                if (IsWebBuild) return UseDefaultNow ? (defaults.webPort > 0 ? defaults.webPort : NetProto.DefaultWebPort) : customPort;
                return UseDefaultNow ? (defaults.port > 0 ? defaults.port : NetConfig.DefaultPort) : customPort;
            }
        }
        /// <summary>Lo que se le pasa al transporte: en la web, la URL del WebSocket.</summary>
        string ConnectTarget
        {
            get
            {
                if (!IsWebBuild) return EffectiveHost;
                if (UseDefaultNow && !string.IsNullOrEmpty(defaults.webUrl)) return defaults.webUrl.Trim();
                string h = EffectiveHost;
                if (h.StartsWith("ws://") || h.StartsWith("wss://")) return h;
                // página servida por https: el navegador exige WebSocket seguro (wss)
                string page = Application.absoluteURL ?? "";
                string scheme = page.StartsWith("https://") ? "wss://" : "ws://";
                return scheme + h + ":" + EffectivePort + NetProto.WebSocketPath;
            }
        }
        string EffectivePassword { get { return UseDefaultNow ? defaults.password ?? "" : customPassword ?? ""; } }
        public string DefaultLabel { get { return !HasDefault ? "" : (string.IsNullOrEmpty(defaults.name) ? defaults.host : defaults.name); } }
        public string ServerLabel { get { return UseDefaultNow ? DefaultLabel + " (por defecto)" : EffectiveHost + ":" + EffectivePort; } }
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
            defaults = NetConfig.LoadDefaults();
            useDefault = PlayerPrefs.GetInt("net.useDefault", 1) == 1;
            customHost = PlayerPrefs.GetString("net.host", "");
            customPort = IsWebBuild ? PlayerPrefs.GetInt("net.webport", NetProto.DefaultWebPort) : PlayerPrefs.GetInt("net.port", NetConfig.DefaultPort);
            customPassword = PlayerPrefs.GetString("net.password", "");
            nick = PlayerPrefs.GetString("net.nick", "Piloto" + Random.Range(10, 99));
            myCar = PlayerPrefs.GetString("net.car", "turismo");
            myColor = PlayerPrefs.GetInt("net.color", 0xD9141A);
            Core.RoomChanged += OnRoomChanged;
            Core.ErrorReceived += e => ShowToast(e, 5f);
            Core.InfoReceived += e => ShowToast(e, 5f);
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

        public void SaveConnectionPrefs()
        {
            PlayerPrefs.SetInt("net.useDefault", useDefault ? 1 : 0);
            PlayerPrefs.SetString("net.host", (customHost ?? "").Trim());
            PlayerPrefs.SetInt(IsWebBuild ? "net.webport" : "net.port", customPort);
            PlayerPrefs.SetString("net.password", customPassword ?? "");
            PlayerPrefs.SetString("net.nick", nick ?? "");
            PlayerPrefs.Save();
        }

        public void Connect()
        {
            if (Core.State != ClientState.Offline) return;
            SaveConnectionPrefs();
            UpdateProfile(false);
            Status = "Conectando con " + ServerLabel + "...";
            Core.Connect(ConnectTarget, EffectivePort, EffectivePassword, DataSignature());
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
            if (Core.State == ClientState.Connecting) Status = "Conectando con " + ServerLabel + "...";
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
        public static RoomSettings MakeSettings(MenuState st)
        {
            var rs = MakeSettings(st.sel, st.settings, st.presetName, st.freeStart);
            rs.closed = st.closed;
            rs.preset = st.isPreset;
            rs.circuitCreator = st.creator ?? "";
            return rs;
        }

        public static RoomSettings MakeSettings(List<int> nodes, RaceSettings s, string circuitName, int freeStart)
        {
            return new RoomSettings
            {
                nodes = nodes.ToArray(), laps = (byte)Mathf.Clamp(s.laps, 1, 20), opponents = (byte)Mathf.Clamp(s.opponents, 0, NetProto.MaxCarsPerRace - 1),
                difficulty = (byte)Mathf.Clamp(s.difficulty, 0, 2), traffic = s.traffic, pedestrians = s.pedestrians,
                freeTraffic = s.freeTraffic, freePedestrians = s.freePedestrians, aiCar = s.carId, freeStartNode = freeStart,
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
        public const int DefaultPort = NetProto.DefaultPort;

        /// <summary>
        /// Servidor por defecto: Assets/HernaniRacing/Resources/HernaniNet/server_default.json
        /// (no se sube a GitHub; hay un ejemplo en Server/server_default.example.json).
        /// </summary>
        [System.Serializable]
        public class ServerDefaults
        {
            public string name = "";
            public string host = "";
            public int port = NetProto.DefaultPort;
            public string password = "";
            /// <summary>Versión web: puerto TCP de la página y del WebSocket (por defecto 8080).</summary>
            public int webPort = NetProto.DefaultWebPort;
            /// <summary>Versión web (opcional): URL completa del WebSocket, p. ej. wss://mi-dominio/ws.</summary>
            public string webUrl = "";
        }

        public static ServerDefaults LoadDefaults()
        {
            var ta = Resources.Load<TextAsset>("HernaniNet/server_default");
            if (ta == null) return null;
            try { return JsonUtility.FromJson<ServerDefaults>(ta.text); }
            catch (System.Exception e) { Debug.LogWarning("server_default.json no válido: " + e.Message); return null; }
        }
    }
}
