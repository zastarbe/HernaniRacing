using HernaniRacing.Net;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Pantalla "JUGAR ONLINE" del menú:
    ///  1. Nombre y CONECTAR (el servidor por defecto ya viene configurado; se puede cambiar en "Opciones de conexión").
    ///  2. Crear sala, entrar con código o elegir una de las salas abiertas (la lista se actualiza cada 2 s).
    ///  3. La sala: jugadores, resumen de coche/circuito/carrera y, para el líder, los botones para empezar.
    /// También muestra los avisos de red (toasts) en cualquier momento.
    /// </summary>
    public class NetLobbyUI : MonoBehaviour
    {
        public GameBootstrap game;
        public NetSession net;
        public MainMenu menu;
        bool showOptions;
        string roomName = "", joinCode = "", portText;
        Vector2 roomsScroll, playersScroll;
        float nextRefresh;
        GUIStyle panel, h1, h2, txt, small, codeStyle, toastStyle, field, bigField, bigBtn, midBtn, btn, okStyle;

        public bool ShowingOptions { get { return showOptions && !net.Connected; } }
        public void CloseOptions() { showOptions = false; }

        public void OnShow()
        {
            if (net.Connected && !net.InRoom) { net.Core.RequestRooms(); nextRefresh = Time.unscaledTime + 2f; }
        }

        void Start()
        {
            portText = net.customPort.ToString();
        }

        void Update()
        {
            bool visible = menu != null && menu.IsOpen && menu.Screen == MenuScreen.Online;
            if (visible && net.Connected && !net.InRoom && Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + 2f;
                net.Core.RequestRooms();
            }
        }

        void Styles()
        {
            if (panel != null) return;
            panel = new GUIStyle(GUI.skin.box);
            panel.normal.background = Tex.Solid(new Color(0.04f, 0.05f, 0.07f, 0.84f));
            h1 = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold };
            h1.normal.textColor = new Color(0.45f, 0.85f, 1f);
            h2 = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            h2.normal.textColor = new Color(1f, 0.84f, 0.22f);
            txt = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true };
            txt.normal.textColor = Color.white;
            small = new GUIStyle(txt) { fontSize = 14 };
            small.normal.textColor = new Color(0.8f, 0.85f, 0.9f);
            okStyle = new GUIStyle(txt) { fontStyle = FontStyle.Bold };
            okStyle.normal.textColor = new Color(0.5f, 1f, 0.55f);
            codeStyle = new GUIStyle(GUI.skin.label) { fontSize = 54, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            codeStyle.normal.textColor = new Color(1f, 0.84f, 0.22f);
            toastStyle = new GUIStyle(GUI.skin.box) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            toastStyle.normal.background = Tex.Solid(new Color(0.05f, 0.1f, 0.18f, 0.9f));
            toastStyle.normal.textColor = Color.white;
            field = new GUIStyle(GUI.skin.textField) { fontSize = 20, alignment = TextAnchor.MiddleLeft };
            bigField = new GUIStyle(GUI.skin.textField) { fontSize = 28, alignment = TextAnchor.MiddleLeft };
            bigBtn = new GUIStyle(GUI.skin.button) { fontSize = 32, fontStyle = FontStyle.Bold };
            midBtn = new GUIStyle(GUI.skin.button) { fontSize = 21, fontStyle = FontStyle.Bold };
            btn = new GUIStyle(GUI.skin.button) { fontSize = 17 };
        }

        // avisos (siempre visibles, también en carrera)
        void OnGUI()
        {
            if (!net.ToastVisible) return;
            Styles();
            float ui = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(ui, ui, 1));
            float W = Screen.width / ui;
            var size = new Vector2(Mathf.Min(900, W - 100), 64);
            GUI.depth = -10;
            GUI.Box(new Rect(W / 2 - size.x / 2, 130, size.x, size.y), net.Toast, toastStyle);
            GUI.matrix = Matrix4x4.identity;
        }

        /// <summary>Dibuja la pantalla online (la llama el menú dentro de su OnGUI, ya escalado a 1080 de alto).</summary>
        public void DrawScreen(MainMenu m, float W)
        {
            Styles();
            if (!net.Connected) { if (showOptions) OptionsPanel(m, W); else ConnectPanel(m, W); }
            else if (!net.InRoom) BrowserPanel(m, W);
            else RoomHub(m, W);
        }

        Rect Centered(float W, float w, float h, float y = -1)
        {
            w = Mathf.Min(w, W - 80);
            return new Rect((W - w) / 2, y >= 0 ? y : (1080 - h) / 2 + 40, w, h);
        }

        // ------------------------------------------------------------------ 1. conectar
        void ConnectPanel(MainMenu m, float W)
        {
            m.Title(56, 30);
            Rect r = Centered(W, 760, 660);
            GUI.Box(r, GUIContent.none, panel);
            GUILayout.BeginArea(new Rect(r.x + 36, r.y + 24, r.width - 72, r.height - 48));
            GUILayout.Label("JUGAR ONLINE", h1);
            GUILayout.Label("Juega con tus amigos, cada uno desde su casa: carreras y paseo libre, hasta 8 por sala.", txt);
            GUILayout.Space(10);
            if (string.IsNullOrEmpty(net.EffectiveHost))
                GUILayout.Label("Falta el servidor: entra en «Opciones de conexión» y escribe su dirección.", txt);
            else
                GUILayout.Label("Servidor: " + net.ServerLabel, small);
            GUILayout.Space(12);
            GUILayout.Label("Tu nombre", h2);
            net.nick = GUILayout.TextField(net.nick ?? "", 16, bigField, GUILayout.Height(52));
            GUILayout.Space(14);
            bool busy = net.Core.State == ClientState.Connecting;
            GUI.enabled = !busy && !string.IsNullOrEmpty(net.EffectiveHost) && !string.IsNullOrEmpty((net.nick ?? "").Trim());
            if (GUILayout.Button(busy ? "Conectando..." : "CONECTAR", bigBtn, GUILayout.Height(72))) m.Queue(net.Connect);
            GUI.enabled = true;
            if (busy && GUILayout.Button("Cancelar", btn, GUILayout.Height(34))) m.Queue(net.Disconnect);
            GUILayout.Space(6);
            if (!string.IsNullOrEmpty(net.Status)) GUILayout.Label(net.Status, txt);
            GUILayout.FlexibleSpace();
            GUILayout.Label("No hay chat: sólo se comparten el nombre, el coche y la posición.", small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Opciones de conexión", midBtn, GUILayout.Height(50))) m.Queue(() => showOptions = true);
            if (GUILayout.Button("◄ Menú principal", midBtn, GUILayout.Height(50))) m.Go(MenuScreen.Title);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // ------------------------------------------------------------------ opciones de conexión
        void OptionsPanel(MainMenu m, float W)
        {
            m.Title(56, 30);
            Rect r = Centered(W, 760, 640);
            GUI.Box(r, GUIContent.none, panel);
            GUILayout.BeginArea(new Rect(r.x + 36, r.y + 24, r.width - 72, r.height - 48));
            GUILayout.Label("OPCIONES DE CONEXIÓN", h1);
            GUILayout.Space(8);
            if (net.HasDefault)
            {
                var tst = new GUIStyle(GUI.skin.toggle) { fontSize = 20 };
                tst.normal.textColor = tst.onNormal.textColor = tst.hover.textColor = tst.onHover.textColor = Color.white;
                net.useDefault = GUILayout.Toggle(net.useDefault, "  Usar el servidor por defecto (" + net.DefaultLabel + ")", tst, GUILayout.Height(34));
                GUILayout.Label("Viene activado: es el servidor de la familia. Desactívalo sólo para conectarte a otro.", small);
            }
            else
                GUILayout.Label("Este juego no trae servidor por defecto: escribe los datos del servidor.", txt);
            GUILayout.Space(10);
            bool custom = !net.HasDefault || !net.useDefault;
            GUI.enabled = custom;
            GUILayout.Label("Servidor (IP o dominio)", txt);
            net.customHost = GUILayout.TextField(net.customHost ?? "", 80, field, GUILayout.Height(38));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Puerto UDP", txt, GUILayout.Width(130));
            portText = GUILayout.TextField(portText ?? "", 6, field, GUILayout.Width(110), GUILayout.Height(38));
            GUILayout.EndHorizontal();
            int p;
            if (int.TryParse(portText, out p) && p > 0 && p < 65536) net.customPort = p;
            GUILayout.Label("Contraseña del servidor", txt);
            net.customPassword = GUILayout.PasswordField(net.customPassword ?? "", '*', 64, field, GUILayout.Height(38));
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("◄ Volver", midBtn, GUILayout.Height(54))) m.Queue(() => { net.SaveConnectionPrefs(); showOptions = false; });
            GUILayout.EndArea();
        }

        // ------------------------------------------------------------------ 2. salas
        void BrowserPanel(MainMenu m, float W)
        {
            m.Title(56, 30);
            Rect r = Centered(W, 1240, 800, 150);
            GUI.Box(r, GUIContent.none, panel);
            GUILayout.BeginArea(new Rect(r.x + 32, r.y + 20, r.width - 64, r.height - 40));
            GUILayout.BeginHorizontal();
            GUILayout.Label("JUGAR ONLINE", h1);
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical();
            GUILayout.Label(net.Status, okStyle);
            GUILayout.Label("Jugador: " + PlayerProfile.CleanNick(net.nick), small);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(net.Core.Motd)) GUILayout.Label(net.Core.Motd, txt);
            GUILayout.Space(10);

            GUILayout.BeginHorizontal();
            // izquierda: crear / entrar con código
            GUILayout.BeginVertical(GUILayout.Width(440));
            GUILayout.Label("Crear una sala", h2);
            GUILayout.Label("Nombre de la sala (opcional)", small);
            roomName = GUILayout.TextField(roomName, 24, field, GUILayout.Height(40));
            if (GUILayout.Button("CREAR SALA", midBtn, GUILayout.Height(56))) net.Core.CreateRoom(roomName ?? "");
            GUILayout.Label("Quien crea la sala es el líder: elige el circuito y cuándo se empieza.", small);
            GUILayout.Space(22);
            GUILayout.Label("Entrar con código", h2);
            GUILayout.BeginHorizontal();
            joinCode = GUILayout.TextField((joinCode ?? "").ToUpperInvariant(), 4, bigField, GUILayout.Width(160), GUILayout.Height(52));
            GUI.enabled = joinCode.Trim().Length == 4;
            if (GUILayout.Button("ENTRAR", midBtn, GUILayout.Height(52))) net.Core.JoinRoom(joinCode);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Pide el código de 4 letras a quien ha creado la sala.", small);
            GUILayout.EndVertical();

            GUILayout.Space(30);
            // derecha: salas abiertas
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Salas abiertas", h2);
            GUILayout.FlexibleSpace();
            GUILayout.Label("se actualiza cada 2 s", small);
            GUILayout.EndHorizontal();
            roomsScroll = GUILayout.BeginScrollView(roomsScroll, GUILayout.Height(470));
            if (net.Core.Rooms.Count == 0) GUILayout.Label("No hay ninguna sala abierta. ¡Crea una!", txt);
            foreach (var room in net.Core.Rooms)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);
                string ph = room.phase == RoomPhase.Lobby ? "preparando" : (room.phase == RoomPhase.Race ? "corriendo" : "de paseo");
                GUILayout.Label(room.name + "   [" + room.code + "]\n" + room.players + "/" + NetProto.MaxPlayersPerRoom + " jugadores · " + ph + " · líder " + room.leaderNick, txt);
                GUI.enabled = room.players < NetProto.MaxPlayersPerRoom;
                if (GUILayout.Button("Entrar", midBtn, GUILayout.Width(120), GUILayout.Height(52))) net.Core.JoinRoom(room.code);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Desconectar", midBtn, GUILayout.Width(260), GUILayout.Height(50))) m.Queue(net.Disconnect);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("◄ Menú principal", midBtn, GUILayout.Width(300), GUILayout.Height(50))) m.Go(MenuScreen.Title);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // ------------------------------------------------------------------ 3. la sala
        void RoomHub(MainMenu m, float W)
        {
            var room = net.Room;
            bool wide = W >= 1700f;
            float leftW = wide ? 500f : 400f;

            // jugadores
            Rect lp = new Rect(40, 40, leftW, 1080 - 80);
            GUI.Box(lp, GUIContent.none, panel);
            GUILayout.BeginArea(new Rect(lp.x + 24, lp.y + 18, lp.width - 48, lp.height - 36));
            GUILayout.Label(room.name, h2);
            GUILayout.Label("Código de la sala (díselo a tus amigos):", small);
            GUILayout.Label(room.code, codeStyle, GUILayout.Height(70));
            string ph = room.phase == RoomPhase.Lobby ? "Preparando la partida" : (room.phase == RoomPhase.Race ? "Carrera en marcha" : "Paseo libre en marcha");
            GUILayout.Label(ph, okStyle);
            GUILayout.Space(8);
            GUILayout.Label("Jugadores (" + room.players.Count + "/" + NetProto.MaxPlayersPerRoom + ")", h2);
            playersScroll = GUILayout.BeginScrollView(playersScroll);
            foreach (var p in room.players)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);
                var c0 = GUI.color;
                GUI.color = NetSession.IntToColor(p.profile.color);
                GUILayout.Label("■", new GUIStyle(h2) { fontSize = 30 }, GUILayout.Width(30));
                GUI.color = c0;
                var md = CarModels.Get(p.profile.carId);
                string carName = md != null ? md.name : p.profile.carId;
                string who = p.profile.nick + (p.id == net.MyId ? " (tú)" : "") + (p.id == room.leaderId ? "  · LÍDER" : "");
                GUILayout.Label(who + "\n" + carName + " · " + p.ping + " ms" + (p.inGame ? " · jugando" : ""), txt);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (room.settings.opponents > 0) GUILayout.Label("+ " + room.settings.opponents + " coche(s) de la IA en la carrera", small);
            GUILayout.Space(8);
            if (GUILayout.Button("Salir de la sala", midBtn, GUILayout.Height(50))) m.Queue(net.Core.LeaveRoom);
            GUILayout.EndArea();

            // derecha: estado, botones y resumen
            float rx = lp.xMax + 30, rw = W - rx - 40;
            GUILayout.BeginArea(new Rect(rx, 40, rw, 470));
            GUILayout.Label("SALA ONLINE", h1);
            GUILayout.Label(net.Status, small);
            GUILayout.Space(10);
            var leader = room.Leader;
            string leaderNick = leader != null ? leader.profile.nick : "el líder";
            var me = room.Find(net.MyId);
            if (room.phase == RoomPhase.Lobby)
            {
                if (net.IsLeader)
                {
                    GUILayout.Label("Eres el líder: prepara la carrera (abajo) y empieza cuando estéis todos.", txt);
                    GUILayout.Space(6);
                    GUILayout.BeginHorizontal();
                    var mstate = m.state;
                    GUI.enabled = mstate.CircuitOk;
                    if (GUILayout.Button("¡CARRERA ONLINE!", bigBtn, GUILayout.Height(84), GUILayout.MaxWidth(520))) m.Queue(m.StartOnlineRace);
                    GUI.enabled = true;
                    if (GUILayout.Button("PASEO LIBRE ONLINE", midBtn, GUILayout.Height(84), GUILayout.MaxWidth(360))) m.Go(MenuScreen.Free);
                    GUILayout.EndHorizontal();
                    if (!mstate.CircuitOk) GUILayout.Label("Elige un circuito para poder correr.", small);
                    GUILayout.Label("En el paseo libre eliges la salida, el tráfico y los peatones antes de empezar.", small);
                }
                else
                {
                    GUILayout.Label("Esperando a que " + leaderNick + " empiece la partida...", new GUIStyle(txt) { fontSize = 24, fontStyle = FontStyle.Bold });
                    GUILayout.Label("El líder elige el circuito, las vueltas, la IA y el tráfico. Tú eliges tu coche y tu color.", txt);
                }
            }
            else
            {
                if (room.phase == RoomPhase.Free && me != null && !me.inGame)
                {
                    if (GUILayout.Button("ENTRAR AL PASEO", bigBtn, GUILayout.Height(84), GUILayout.MaxWidth(520))) m.Queue(net.Core.EnterGame);
                }
                else if (room.phase == RoomPhase.Race && me != null && !me.inGame)
                    GUILayout.Label("Hay una carrera en marcha. Podrás correr en la siguiente.", new GUIStyle(txt) { fontSize = 24 });
                if (net.IsLeader && GUILayout.Button("Terminar la partida para todos", midBtn, GUILayout.Height(54), GUILayout.MaxWidth(520))) m.Queue(net.Core.EndGame);
            }
            GUILayout.EndArea();

            float gap = 20f, cardH = 430f;
            float cardW = (rw - 2 * gap) / 3f;
            m.DrawCards(rx, 1080 - 40 - cardH, cardW, cardH, gap, true);
        }
    }
}
