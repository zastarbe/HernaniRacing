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
        public bool ShowingRankings { get { return showRankings && net.Connected; } }
        public void CloseRankings() { showRankings = false; }

        bool showRankings;
        int rankPage;
        string rankKey = "", confirmDelete = "";

        void OpenRankings()
        {
            showRankings = true;
            confirmDelete = "";
            net.Core.RequestCircuits();
        }

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
            if (!net.Connected) { showRankings = false; if (showOptions) OptionsPanel(m, W); else ConnectPanel(m, W); }
            else if (showRankings) RankingsPanel(m, W);
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
            GUILayout.Label(NetSession.IsWebBuild ? "Puerto web" : "Puerto UDP", txt, GUILayout.Width(130));
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
            if (GUILayout.Button("RANKINGS Y CIRCUITOS", midBtn, GUILayout.Width(340), GUILayout.Height(50))) m.Queue(OpenRankings);
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
            if (GUILayout.Button("RANKINGS Y CIRCUITOS", midBtn, GUILayout.Height(50))) m.Queue(OpenRankings);
            GUILayout.Space(4);
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
                    else if (mstate.IsCustom) GUILayout.Label("Este circuito no está guardado en el servidor: no contará en los rankings. Guárdalo con un nombre en «Cambiar circuito».", small);
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

        // ------------------------------------------------------------------ rankings
        static string Date(long unix)
        {
            if (unix <= 0) return "";
            return System.DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("dd/MM/yyyy");
        }

        static string CarName(string id)
        {
            var md = CarModels.Get(id);
            return md != null ? md.name : id;
        }

        /// <summary>Circuitos del servidor ordenados por carreras corridas y, del elegido, sus vueltas más rápidas.</summary>
        void RankingsPanel(MainMenu m, float W)
        {
            var core = net.Core;
            var list = core.Circuits;
            Rect r = Centered(W, 1640, 960, 70);
            GUI.Box(r, GUIContent.none, panel);
            float x0 = r.x + 32, y0 = r.y + 20, iw = r.width - 64;
            GUI.Label(new Rect(x0, y0, 700, 56), "RANKINGS", h1);
            GUI.Label(new Rect(x0 + 260, y0 + 18, iw - 260, 30), "Carreras online en cada circuito del servidor y sus vueltas más rápidas (sólo jugadores; la IA no cuenta).", small);
            float top = y0 + 74;

            // ---- izquierda: ranking de circuitos (los más corridos)
            float lw = Mathf.Floor(iw * 0.52f), rowH = 40f;
            GUI.Label(new Rect(x0, top, lw, 30), "Circuitos más corridos", h2);
            var head = new GUIStyle(small) { fontStyle = FontStyle.Bold };
            float cy = top + 36;
            float cPos = x0, cName = x0 + 44, cBy = x0 + lw * 0.47f, cRaces = x0 + lw * 0.68f, cRec = x0 + lw * 0.80f;
            GUI.Label(new Rect(cPos, cy, 40, 24), "#", head);
            GUI.Label(new Rect(cName, cy, cBy - cName, 24), "Circuito", head);
            GUI.Label(new Rect(cBy, cy, cRaces - cBy, 24), "Creador", head);
            GUI.Label(new Rect(cRaces, cy, cRec - cRaces, 24), "Carreras", head);
            GUI.Label(new Rect(cRec, cy, x0 + lw - cRec, 24), "Récord", head);
            cy += 28;
            const int per = 15;
            int pages = Mathf.Max(1, (list.Count + per - 1) / per);
            rankPage = Mathf.Clamp(rankPage, 0, pages - 1);
            if (!core.CircuitsLoaded) GUI.Label(new Rect(x0, cy, lw, 30), "Cargando...", txt);
            else if (list.Count == 0) GUI.Label(new Rect(x0, cy, lw, 90), "Todavía no hay circuitos en el servidor. Se apuntan solos al correr online un circuito predefinido, y cada uno puede guardar los suyos en la pantalla del circuito.", txt);
            CircuitEntry sel = null;
            foreach (var e in list) if (e.key == rankKey) { sel = e; break; }
            if (sel == null && list.Count > 0) { sel = list[0]; rankKey = sel.key; }
            var cell = new GUIStyle(txt) { wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft };
            for (int i = rankPage * per; i < Mathf.Min(list.Count, (rankPage + 1) * per); i++)
            {
                var e = list[i];
                Rect row = new Rect(x0 - 6, cy, lw + 6, rowH - 4);
                bool on = sel == e;
                GUI.DrawTexture(row, Tex.Solid(on ? new Color(1f, 0.8f, 0.2f, 0.28f) : new Color(1f, 1f, 1f, i % 2 == 0 ? 0.06f : 0.02f)));
                var st = on ? new GUIStyle(cell) { fontStyle = FontStyle.Bold } : cell;
                GUI.Label(new Rect(cPos, cy, 40, rowH - 4), (i + 1) + ".", st);
                GUI.Label(new Rect(cName, cy, cBy - cName - 6, rowH - 4), e.name + (e.closed ? "" : "  (A→B)"), st);
                GUI.Label(new Rect(cBy, cy, cRaces - cBy - 6, rowH - 4), e.creator, st);
                GUI.Label(new Rect(cRaces, cy, cRec - cRaces - 6, rowH - 4), e.races.ToString(), st);
                var b = e.BestLap();
                GUI.Label(new Rect(cRec, cy, x0 + lw - cRec, rowH - 4), b != null ? NetProto.FormatLap(b.time) : "—", st);
                if (GUI.Button(row, GUIContent.none, GUIStyle.none)) { var k = e.key; m.Queue(() => { rankKey = k; confirmDelete = ""; }); }
                cy += rowH;
            }
            if (pages > 1)
            {
                float py = top + 36 + 28 + per * rowH + 6;
                GUI.enabled = rankPage > 0;
                if (GUI.Button(new Rect(x0, py, 70, 36), "◄", btn)) rankPage--;
                GUI.enabled = rankPage < pages - 1;
                if (GUI.Button(new Rect(x0 + lw - 70, py, 70, 36), "►", btn)) rankPage++;
                GUI.enabled = true;
                GUI.Label(new Rect(x0 + 80, py, lw - 160, 36), "página " + (rankPage + 1) + " de " + pages, new GUIStyle(small) { alignment = TextAnchor.MiddleCenter });
            }

            // ---- derecha: vueltas más rápidas del circuito elegido
            float rx = x0 + lw + 40, rw = r.xMax - 32 - rx;
            if (sel != null)
            {
                GUI.Label(new Rect(rx, top, rw, 34), sel.name, new GUIStyle(h2) { fontSize = 28 });
                string km = sel.length > 0 ? (sel.length / 1000f).ToString("F2") + " km · " : "";
                GUI.Label(new Rect(rx, top + 38, rw, 26), "de " + sel.creator + " · " + km + (sel.closed ? "cerrado, a vueltas" : "de A a B") + " · " + sel.nodes.Length + " cruces · " +
                          sel.races + " carrera" + (sel.races == 1 ? "" : "s") + (sel.created > 0 ? " · desde " + Date(sel.created) : ""), small);
                GUI.Label(new Rect(rx, top + 74, rw, 30), sel.closed ? "Vuelta rápida" : "Mejor tiempo (de la salida a la meta)", h2);
                float ty = top + 110;
                float tPos = rx, tNick = rx + 44, tCar = rx + rw * 0.36f, tTime = rx + rw * 0.66f, tDate = rx + rw * 0.83f;
                GUI.Label(new Rect(tPos, ty, 40, 24), "#", head);
                GUI.Label(new Rect(tNick, ty, tCar - tNick, 24), "Piloto", head);
                GUI.Label(new Rect(tCar, ty, tTime - tCar, 24), "Coche", head);
                GUI.Label(new Rect(tTime, ty, tDate - tTime, 24), "Tiempo", head);
                GUI.Label(new Rect(tDate, ty, rx + rw - tDate, 24), "Fecha", head);
                ty += 28;
                if (sel.laps.Count == 0) GUI.Label(new Rect(rx, ty, rw, 60), "Nadie ha terminado todavía una carrera online en este circuito. ¡Sé el primero!", txt);
                for (int i = 0; i < sel.laps.Count; i++)
                {
                    var l = sel.laps[i];
                    bool me = l.nick == PlayerProfile.CleanNick(net.nick);
                    var st = new GUIStyle(cell) { fontStyle = i == 0 || me ? FontStyle.Bold : FontStyle.Normal };
                    if (i == 0) st.normal.textColor = new Color(1f, 0.85f, 0.25f);
                    else if (me) st.normal.textColor = new Color(0.5f, 1f, 0.55f);
                    GUI.DrawTexture(new Rect(rx - 6, ty, rw + 6, 36), Tex.Solid(new Color(1f, 1f, 1f, i % 2 == 0 ? 0.06f : 0.02f)));
                    GUI.Label(new Rect(tPos, ty, 40, 36), (i + 1) + ".", st);
                    GUI.Label(new Rect(tNick, ty, tCar - tNick - 6, 36), l.nick, st);
                    GUI.Label(new Rect(tCar, ty, tTime - tCar - 6, 36), CarName(l.carId), st);
                    GUI.Label(new Rect(tTime, ty, tDate - tTime - 6, 36), NetProto.FormatLap(l.time), st);
                    GUI.Label(new Rect(tDate, ty, rx + rw - tDate, 36), Date(l.when), new GUIStyle(st) { fontSize = 15 });
                    ty += 38;
                }
                // botones del circuito elegido
                float by = r.yMax - 20 - 54 - 70;
                bool canUse = net.InRoom && net.IsLeader && net.Room.phase == RoomPhase.Lobby;
                if (canUse && GUI.Button(new Rect(rx, by, 360, 54), "USAR EN LA SALA", midBtn))
                {
                    var e = sel;
                    m.Queue(() => { m.state.ApplyServer(e); showRankings = false; });
                }
                bool mine = !sel.preset && sel.creator == PlayerProfile.CleanNick(net.nick);
                if (mine)
                {
                    bool sure = confirmDelete == sel.key;
                    if (GUI.Button(new Rect(rx + rw - 300, by, 300, 54), sure ? "¿Seguro? Pulsa otra vez" : "Borrar mi circuito", btn))
                    {
                        var k = sel.key;
                        if (sure) m.Queue(() => { core.DeleteCircuit(k); confirmDelete = ""; });
                        else m.Queue(() => confirmDelete = k);
                    }
                }
            }

            // ---- abajo
            float bottom = r.yMax - 20 - 54;
            if (GUI.Button(new Rect(x0, bottom, 300, 54), "◄ Volver", midBtn)) m.Queue(() => showRankings = false);
            if (GUI.Button(new Rect(x0 + 320, bottom, 220, 54), "↻ Actualizar", midBtn)) m.Queue(core.RequestCircuits);
            GUI.Label(new Rect(x0 + 560, bottom, iw - 560, 54), "Cuentan las carreras online en circuitos guardados o predefinidos. Cada piloto aparece una vez, con su mejor vuelta.", small);
        }
    }
}
