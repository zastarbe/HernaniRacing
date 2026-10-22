using HernaniRacing.Net;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Panel "JUGAR ONLINE" (a la derecha del selector de circuito): conectar al servidor, crear o entrar
    /// en una sala y ver quién hay. El circuito y las opciones se eligen en el panel de la izquierda.
    /// </summary>
    public class NetLobbyUI : MonoBehaviour
    {
        public GameBootstrap game;
        public NetSession net;
        bool expanded;
        string roomName = "", joinCode = "", portText;
        Vector2 scroll;
        float nextRefresh;
        GUIStyle box, title, h2, txt, small, codeStyle, toastStyle;
        static Rect screenRect;

        /// <summary>¿El ratón está sobre este panel? (para que el mapa no reciba el clic)</summary>
        public static bool Blocks(Vector2 guiMouse) { return screenRect.Contains(guiMouse); }

        void Start()
        {
            portText = net.port.ToString();
            expanded = net.InRoom;
        }

        void Styles()
        {
            if (box != null) return;
            box = new GUIStyle(GUI.skin.box);
            box.normal.background = Tex.Solid(new Color(0.06f, 0.08f, 0.1f, 0.9f));
            title = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
            title.normal.textColor = new Color(0.45f, 0.85f, 1f);
            h2 = new GUIStyle(GUI.skin.label) { fontSize = 19, fontStyle = FontStyle.Bold };
            h2.normal.textColor = new Color(1f, 0.85f, 0.25f);
            txt = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
            txt.normal.textColor = Color.white;
            small = new GUIStyle(txt) { fontSize = 13 };
            small.normal.textColor = new Color(0.8f, 0.85f, 0.9f);
            codeStyle = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            codeStyle.normal.textColor = new Color(1f, 0.85f, 0.25f);
            toastStyle = new GUIStyle(GUI.skin.box) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            toastStyle.normal.background = Tex.Solid(new Color(0.05f, 0.1f, 0.18f, 0.88f));
            toastStyle.normal.textColor = Color.white;
        }

        void Update()
        {
            if (net.Connected && !net.InRoom && expanded && Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + 3f;
                net.Core.RequestRooms();
            }
            if (net.InRoom) expanded = true;
        }

        void OnGUI()
        {
            Styles();
            float ui = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(ui, ui, 1));
            float W = Screen.width / ui;

            if (net.ToastVisible)
            {
                var size = new Vector2(Mathf.Min(900, W - 100), 64);
                GUI.Box(new Rect(W / 2 - size.x / 2, 130, size.x, size.y), net.Toast, toastStyle);
            }
            if (!game.InMenu) { screenRect = new Rect(); GUI.matrix = Matrix4x4.identity; return; }

            if (!expanded)
            {
                Rect br = new Rect(W - 300, 16, 284, 54);
                screenRect = new Rect(br.x * ui, br.y * ui, br.width * ui, br.height * ui);
                var bst = new GUIStyle(GUI.skin.button) { fontSize = 22, fontStyle = FontStyle.Bold };
                if (GUI.Button(br, "JUGAR ONLINE", bst)) expanded = true;
                GUI.matrix = Matrix4x4.identity;
                return;
            }

            Rect panel = new Rect(W - 456, 16, 440, net.InRoom ? 640 : 600);
            screenRect = new Rect(panel.x * ui, panel.y * ui, panel.width * ui, panel.height * ui);
            GUI.Box(panel, GUIContent.none, box);
            GUILayout.BeginArea(new Rect(panel.x + 16, panel.y + 10, panel.width - 32, panel.height - 20));
            GUILayout.BeginHorizontal();
            GUILayout.Label("JUGAR ONLINE", title);
            GUILayout.FlexibleSpace();
            if (!net.InRoom && GUILayout.Button("–", GUILayout.Width(34))) expanded = false;
            GUILayout.EndHorizontal();
            scroll = GUILayout.BeginScrollView(scroll);

            if (!net.Connected) ConnectForm();
            else if (!net.InRoom) RoomBrowser();
            else RoomView();

            GUILayout.EndScrollView();
            GUILayout.EndArea();
            GUI.matrix = Matrix4x4.identity;
        }

        void ConnectForm()
        {
            bool busy = net.Core.State == ClientState.Connecting;
            GUILayout.Label("Servidor (IP o dominio)", txt);
            net.host = GUILayout.TextField(net.host ?? "", 80, GUILayout.Height(28));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Puerto UDP", txt, GUILayout.Width(110));
            portText = GUILayout.TextField(portText ?? "", 6, GUILayout.Width(90), GUILayout.Height(28));
            GUILayout.EndHorizontal();
            int p;
            if (int.TryParse(portText, out p) && p > 0 && p < 65536) net.port = p;
            GUILayout.Label("Contraseña del servidor", txt);
            net.password = GUILayout.PasswordField(net.password ?? "", '*', 64, GUILayout.Height(28));
            GUILayout.Label("Tu nombre en la partida", txt);
            net.nick = GUILayout.TextField(net.nick ?? "", 16, GUILayout.Height(28));
            GUILayout.Space(8);
            GUI.enabled = !busy && !string.IsNullOrEmpty(net.host) && !string.IsNullOrEmpty(net.nick);
            if (GUILayout.Button(busy ? "Conectando..." : "CONECTAR", GUILayout.Height(46))) net.Connect();
            GUI.enabled = true;
            if (busy && GUILayout.Button("Cancelar")) net.Disconnect();
            GUILayout.Space(6);
            if (!string.IsNullOrEmpty(net.Status)) GUILayout.Label(net.Status, txt);
            GUILayout.Space(10);
            GUILayout.Label("El coche y el color que elijas a la izquierda son los que verán los demás. Se guarda todo en este PC.", small);
            GUILayout.Label("No hay chat: sólo se comparte el nombre, el coche y la posición.", small);
        }

        void RoomBrowser()
        {
            GUILayout.Label(net.Status, small);
            if (!string.IsNullOrEmpty(net.Core.Motd)) GUILayout.Label(net.Core.Motd, txt);
            GUILayout.Space(6);
            GUILayout.Label("Crear una sala", h2);
            GUILayout.BeginHorizontal();
            roomName = GUILayout.TextField(roomName, 24, GUILayout.Height(28));
            if (GUILayout.Button("Crear", GUILayout.Width(90), GUILayout.Height(28))) net.Core.CreateRoom(string.IsNullOrEmpty(roomName) ? "" : roomName);
            GUILayout.EndHorizontal();
            GUILayout.Label("Quien crea la sala es el líder: elige el circuito y cuándo se empieza.", small);
            GUILayout.Space(8);
            GUILayout.Label("Entrar con código", h2);
            GUILayout.BeginHorizontal();
            joinCode = GUILayout.TextField(joinCode.ToUpperInvariant(), 4, GUILayout.Width(120), GUILayout.Height(28));
            GUI.enabled = joinCode.Trim().Length == 4;
            if (GUILayout.Button("Entrar", GUILayout.Width(90), GUILayout.Height(28))) net.Core.JoinRoom(joinCode);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Salas abiertas", h2);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Actualizar", GUILayout.Width(110))) net.Core.RequestRooms();
            GUILayout.EndHorizontal();
            if (net.Core.Rooms.Count == 0) GUILayout.Label("No hay ninguna sala. ¡Crea una!", txt);
            foreach (var r in net.Core.Rooms)
            {
                GUILayout.BeginHorizontal();
                string ph = r.phase == RoomPhase.Lobby ? "en la sala" : (r.phase == RoomPhase.Race ? "corriendo" : "de paseo");
                GUILayout.Label(r.name + "  [" + r.code + "]\n" + r.players + "/" + NetProto.MaxPlayersPerRoom + " · " + ph + " · líder " + r.leaderNick, txt);
                GUI.enabled = r.players < NetProto.MaxPlayersPerRoom;
                if (GUILayout.Button("Entrar", GUILayout.Width(80), GUILayout.Height(40))) net.Core.JoinRoom(r.code);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(12);
            if (GUILayout.Button("Desconectar")) net.Disconnect();
        }

        void RoomView()
        {
            var room = net.Room;
            GUILayout.Label(room.name, h2);
            GUILayout.Label("Código de la sala (díselo a tus amigos):", small);
            GUILayout.Label(room.code, codeStyle);
            string ph = room.phase == RoomPhase.Lobby ? "Preparando la partida" : (room.phase == RoomPhase.Race ? "Carrera en marcha" : "Paseo libre en marcha");
            GUILayout.Label(ph + (string.IsNullOrEmpty(room.settings.circuitName) ? "" : " · " + room.settings.circuitName), txt);
            GUILayout.Space(6);
            GUILayout.Label("Jugadores (" + room.players.Count + "/" + NetProto.MaxPlayersPerRoom + ")", h2);
            foreach (var p in room.players)
            {
                GUILayout.BeginHorizontal();
                var c0 = GUI.color;
                GUI.color = NetSession.IntToColor(p.profile.color);
                GUILayout.Label("■", h2, GUILayout.Width(22));
                GUI.color = c0;
                var md = CarModels.Get(p.profile.carId);
                string carName = md != null ? md.name : p.profile.carId;
                string who = p.profile.nick + (p.id == net.MyId ? " (tú)" : "") + (p.id == room.leaderId ? "  · LÍDER" : "");
                GUILayout.Label(who + "\n" + carName + " · " + p.ping + " ms" + (p.inGame ? " · jugando" : ""), txt);
                GUILayout.EndHorizontal();
            }
            if (room.settings.opponents > 0) GUILayout.Label("+ " + room.settings.opponents + " coche(s) de la IA en la carrera", small);
            GUILayout.Space(8);
            if (net.IsLeader) GUILayout.Label("Eres el líder: elige circuito, vueltas, IA y tráfico a la izquierda y pulsa ¡CARRERA ONLINE! o PASEO LIBRE ONLINE.", small);
            else GUILayout.Label("El líder elige el circuito. Tú elige tu coche y tu color a la izquierda.", small);
            GUILayout.Space(8);
            if (GUILayout.Button("Salir de la sala", GUILayout.Height(36))) net.Core.LeaveRoom();
            GUILayout.Label(net.Status, small);
        }
    }
}
