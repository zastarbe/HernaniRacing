using System.Collections.Generic;
using System.Linq;
using HernaniRacing.Net;
using UnityEngine;

namespace HernaniRacing
{
    public enum MenuScreen { Title, Car, Circuit, Free, Online }

    /// <summary>
    /// Menús del juego:
    ///  · Presentación: vuelo sobre Hernani de fondo, botones a las demás pantallas y resumen de coche, circuito y carrera.
    ///  · Coche: dibujos y características de cada modelo, modelo y color.
    ///  · Circuito y carrera / Paseo libre: pantallas de mapa (<see cref="CircuitSelector"/>).
    ///  · Jugar online: conexión, salas y sala (<see cref="NetLobbyUI"/>).
    /// </summary>
    public class MainMenu : MonoBehaviour
    {
        public GameBootstrap game;
        public NetSession net;
        public CircuitSelector map;
        public NetLobbyUI lobby;
        public readonly MenuState state = new MenuState();
        Flyover fly;
        CarStudio studio;
        MenuScreen screen = MenuScreen.Title;
        bool open;
        System.Action pending;
        // v17: modo avión (sólo en la pantalla principal): se ocultan los menús salvo los botones de la izquierda
        bool planeMode, planeManual;
        Toponymy planeTopo;

        public bool IsOpen { get { return open; } }
        public MenuScreen Screen { get { return screen; } }
        public bool InRoom { get { return net != null && net.InRoom; } }
        public bool IsLeader { get { return net != null && net.IsLeader; } }
        /// <summary>En una sala online (sin ser el líder): el coche del líder, cuyas prestaciones usan todos.</summary>
        public CarModelData RoomPerfCar
        {
            get
            {
                if (!InRoom || IsLeader || net.Room == null || net.Room.settings == null) return null;
                return CarModels.Get(net.Room.settings.aiCar);
            }
        }
        /// <summary>En una sala online y sin ser el líder: el circuito y la carrera los decide el líder.</summary>
        public bool RaceLocked { get { return InRoom && !net.IsLeader; } }
        MenuScreen Hub { get { return InRoom ? MenuScreen.Online : MenuScreen.Title; } }

        public void Init()
        {
            state.Init(game.world);
            fly = gameObject.AddComponent<Flyover>();
            fly.world = game.world;
            fly.cam = game.MainCamera;
            studio = CarStudio.Create(game.MainCamera);
        }

        // ------------------------------------------------------------------ abrir / cerrar / navegar
        public void Open()
        {
            open = true;
            enabled = true;
            Show(Hub);
        }

        public void Close()
        {
            if (!open) return;
            open = false;
            map.Close();
            SetPlaneMode(false);
            fly.End();
            state.Save();
        }

        void SetPlaneMode(bool on)
        {
            planeMode = on;
            if (fly != null) fly.SetManual(on && planeManual);
        }

        void Show(MenuScreen s)
        {
            screen = s;
            if (s != MenuScreen.Title) SetPlaneMode(false);
            if (s == MenuScreen.Circuit || s == MenuScreen.Free)
            {
                fly.End();
                map.Open(s == MenuScreen.Circuit ? CircuitSelector.MapMode.Circuit : CircuitSelector.MapMode.Free);
            }
            else
            {
                map.Close();
                fly.Begin();
            }
            if (s == MenuScreen.Online) lobby.OnShow();
        }

        public void Go(MenuScreen s) { pending = () => { state.Save(); Show(s); }; }

        /// <summary>Ejecuta la acción en el próximo Update (fuera de OnGUI, para no romper el layout de IMGUI).</summary>
        public void Queue(System.Action a) { pending = a; }

        /// <summary>Volver: desde coche/circuito/paseo a la pantalla principal (o a la sala si se está online).</summary>
        public void Back()
        {
            state.Save();
            if (screen == MenuScreen.Online) Show(InRoom ? MenuScreen.Online : MenuScreen.Title);
            else Show(Hub);
        }

        // ------------------------------------------------------------------ empezar partidas
        public void StartRace()
        {
            if (!state.CircuitOk || InRoom) return;
            state.Save();
            game.StartRace(state.circuit, state.settings);
        }

        public void StartFree()
        {
            if (InRoom) return;
            state.Save();
            game.StartFreeRoam(state.settings, state.freeStart);
        }

        public void StartOnlineRace()
        {
            if (!IsLeader || !state.CircuitOk) return;
            state.Save();
            SendSettingsNow();
            net.Core.StartGame(GameMode.Race);
        }

        public void StartOnlineFree()
        {
            if (!IsLeader) return;
            state.Save();
            SendSettingsNow();
            net.Core.StartGame(GameMode.Free);
        }

        // ------------------------------------------------------------------ online: el líder elige, los demás ven lo mismo
        RoomSettings lastSent, applied;
        float nextSend;

        public int MaxOpponents()
        {
            if (!InRoom) return MenuState.MaxOpponents;
            return Mathf.Clamp(NetProto.MaxCarsPerRace - net.Room.players.Count, 0, MenuState.MaxOpponents);
        }

        RoomSettings CurrentSettings()
        {
            var rs = NetSession.MakeSettings(state);
            int maxAi = MaxOpponents();
            if (rs.opponents > maxAi) rs.opponents = (byte)maxAi;
            return rs;
        }

        void SendSettingsNow()
        {
            if (!IsLeader) return;
            var rs = CurrentSettings();
            net.Core.SendSettings(rs);
            lastSent = rs;
        }

        void SyncOnline()
        {
            var room = net.Room;
            if (net.IsLeader)
            {
                applied = null;
                if (room.phase != RoomPhase.Lobby || Time.unscaledTime < nextSend) return;
                var rs = CurrentSettings();
                if (!rs.SameAs(room.settings) && (lastSent == null || !rs.SameAs(lastSent)))
                {
                    net.Core.SendSettings(rs);
                    lastSent = rs;
                    nextSend = Time.unscaledTime + 0.25f;
                }
                return;
            }
            lastSent = null;
            var r = room.settings;
            if (applied != null && r.SameAs(applied)) return;
            applied = r.Clone();
            var s = state.settings;
            s.laps = r.laps; s.opponents = r.opponents; s.difficulty = r.difficulty;
            s.traffic = r.traffic; s.pedestrians = r.pedestrians;
            s.freeTraffic = r.freeTraffic; s.freePedestrians = r.freePedestrians;
            state.freeStart = r.freeStartNode;
            state.SetNodes(r.nodes ?? new int[0], r.closed, r.circuitName, r.circuitCreator, r.preset);
        }

        void Update()
        {
            if (!open) return;
            if (pending != null) { var p = pending; pending = null; p(); }
            if (net != null)
            {
                net.SetCarAndColor(state.settings.carId, state.settings.paint);
                if (net.Connected)
                {
                    if (!net.Core.CircuitsLoaded && Time.unscaledTime >= nextCircuitReq) { nextCircuitReq = Time.unscaledTime + 5f; net.Core.RequestCircuits(); }
                    // un circuito hecho a mano que ya está guardado en el servidor toma su nombre y su creador
                    if (seenCircuits != net.Core.CircuitsVersion || seenState != state.version)
                    {
                        seenCircuits = net.Core.CircuitsVersion; seenState = state.version;
                        if (state.CircuitOk && state.IsCustom && !RaceLocked)
                        {
                            string key = state.Key;
                            foreach (var e in net.Core.Circuits)
                                if (e.key == key) { state.presetName = e.name; state.creator = e.creator; state.isPreset = e.preset; state.version++; seenState = state.version; break; }
                        }
                    }
                }
                if (InRoom) SyncOnline(); else { lastSent = null; applied = null; }
                // si me echan de la sala (o se cae la conexión) estando en una pantalla de sala, vuelvo a la principal
                if (!InRoom && (screen == MenuScreen.Circuit || screen == MenuScreen.Free) && wasInRoom) Show(MenuScreen.Online);
                if (InRoom && screen == MenuScreen.Title) Show(MenuScreen.Online);
                if (RaceLocked && (screen == MenuScreen.Circuit || screen == MenuScreen.Free)) Show(MenuScreen.Online);
                wasInRoom = InRoom;
            }
            if (state.settings.opponents > MaxOpponents()) state.settings.opponents = MaxOpponents();
            bool typing = GUIUtility.keyboardControl != 0;
            if (!typing && GameInput.PausePressed)
            {
                if (screen == MenuScreen.Title && planeMode) SetPlaneMode(false);
                else if (screen == MenuScreen.Online && lobby.ShowingOptions) lobby.CloseOptions();
                else if (screen == MenuScreen.Online && lobby.ShowingRankings) lobby.CloseRankings();
                else if (screen != MenuScreen.Title && !(screen == MenuScreen.Online && InRoom)) Back();
            }
        }
        bool wasInRoom;
        float nextCircuitReq;
        int seenCircuits = -1, seenState = -1;

        // ------------------------------------------------------------------ estilos
        GUIStyle titleBig, titleShadow, subtitle, h1, h2, txt, small, panel, cardBox, bigBtn, midBtn, onBtn, btn, statVal, carName;
        bool styled;

        void Styles()
        {
            if (styled) return;
            styled = true;
            titleBig = new GUIStyle(GUI.skin.label) { fontSize = 78, fontStyle = FontStyle.Bold };
            titleBig.normal.textColor = new Color(1f, 0.84f, 0.22f);
            titleShadow = new GUIStyle(titleBig);
            titleShadow.normal.textColor = new Color(0, 0, 0, 0.6f);
            subtitle = new GUIStyle(GUI.skin.label) { fontSize = 22, wordWrap = true };
            subtitle.normal.textColor = new Color(0.95f, 0.97f, 1f);
            h1 = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold };
            h1.normal.textColor = new Color(1f, 0.84f, 0.22f);
            h2 = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            h2.normal.textColor = new Color(1f, 0.84f, 0.22f);
            txt = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true };
            txt.normal.textColor = new Color(0.94f, 0.95f, 0.97f);
            small = new GUIStyle(txt) { fontSize = 14 };
            small.normal.textColor = new Color(0.78f, 0.8f, 0.84f);
            panel = new GUIStyle(GUI.skin.box);
            panel.normal.background = Tex.Solid(new Color(0.04f, 0.05f, 0.07f, 0.80f));
            cardBox = new GUIStyle(GUI.skin.box);
            cardBox.normal.background = Tex.Solid(new Color(0.05f, 0.06f, 0.09f, 0.86f));
            btn = new GUIStyle(GUI.skin.button) { fontSize = 17 };
            bigBtn = new GUIStyle(GUI.skin.button) { fontSize = 34, fontStyle = FontStyle.Bold };
            midBtn = new GUIStyle(GUI.skin.button) { fontSize = 24, fontStyle = FontStyle.Bold };
            onBtn = new GUIStyle(GUI.skin.button) { fontSize = 17, fontStyle = FontStyle.Bold };
            onBtn.normal.textColor = onBtn.hover.textColor = new Color(1f, 0.85f, 0.25f);
            statVal = new GUIStyle(txt) { alignment = TextAnchor.MiddleRight, fontSize = 17 };
            carName = new GUIStyle(GUI.skin.label) { fontSize = 36, fontStyle = FontStyle.Bold };
            carName.normal.textColor = Color.white;
        }

        public GUIStyle TextStyle { get { Styles(); return txt; } }

        float ui, W;

        void OnGUI()
        {
            if (!open || screen == MenuScreen.Circuit || screen == MenuScreen.Free) return;
            Styles();
            ui = UnityEngine.Screen.height / 1080f;
            W = UnityEngine.Screen.width / ui;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(ui, ui, 1));
            switch (screen)
            {
                case MenuScreen.Title: TitleScreen(); break;
                case MenuScreen.Car: CarScreen(); break;
                case MenuScreen.Online: lobby.DrawScreen(this, W); break;
            }
            GUI.matrix = Matrix4x4.identity;
        }

        static void Accent(Rect r)
        {
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 4), Tex.Solid(new Color(1f, 0.78f, 0.15f, 0.95f)));
        }

        public void Title(float x, float y)
        {
            GUI.Label(new Rect(x + 4, y + 4, 900, 100), "HERNANI RACING", titleShadow);
            GUI.Label(new Rect(x, y, 900, 100), "HERNANI RACING", titleBig);
        }

        // ------------------------------------------------------------------ pantalla de presentación
        void TitleScreen()
        {
            bool wide = W >= 1700f;
            if (!planeMode)
            {
                Title(56, 30);
                GUI.Label(new Rect(62, 118, 1100, 60), "Carreras por las calles reales de Hernani, Urnieta y Astigarraga", subtitle);
            }

            // botones principales
            string[] labels = { "¡A CORRER!", "PASEO LIBRE", "JUGAR ONLINE", planeMode ? "SALIR DEL MODO AVIÓN" : "MODO AVIÓN", "SALIR" };
            Rect[] br = new Rect[5];
            if (wide)
            {
                br[0] = new Rect(60, 220, 420, 92);
                br[1] = new Rect(60, 326, 420, 70);
                br[2] = new Rect(60, 408, 420, 70);
                br[3] = new Rect(60, 490, 420, 52);
                br[4] = new Rect(60, 556, 420, 52);
            }
            else
            {
                float bw = (W - 120 - 4 * 14) / 5f;
                for (int i = 0; i < 5; i++) br[i] = new Rect(60 + i * (bw + 14), 200, bw, i == 0 ? 84 : 70);
            }
            GUI.enabled = state.CircuitOk;
            if (GUI.Button(br[0], labels[0], bigBtn)) pending = StartRace;
            GUI.enabled = true;
            if (GUI.Button(br[1], labels[1], midBtn)) Go(MenuScreen.Free);
            if (GUI.Button(br[2], labels[2], midBtn)) Go(MenuScreen.Online);
            if (GUI.Button(br[3], labels[3], planeMode ? onBtn : btn)) { bool on = !planeMode; pending = () => SetPlaneMode(on); }
            if (GUI.Button(br[4], labels[4], btn)) pending = Quit;
            if (planeMode)
            {
                PlanePanel(wide ? new Rect(60, 626, 420, 250) : new Rect(60, 300, Mathf.Min(560f, W - 120f), 250));
                Credits();
                return;     // modo avión: nada más (los botones, el panel del vuelo y los créditos de los datos)
            }
            SurfaceChooser(wide ? new Rect(60, 690, 420, 92) : new Rect(60, 300, Mathf.Min(560f, W - 120f), 92));
            if (wide)
            {
                string what;
                if (!state.CircuitOk) what = "Elige un circuito para correr.";
                else if (!state.circuit.closed) what = "Carrera de la salida a la meta en " + state.CircuitName + ".";
                else what = "Carrera de " + state.settings.laps + " vuelta" + (state.settings.laps == 1 ? "" : "s") + " en " + state.CircuitName + ".";
                GUI.Label(new Rect(64, 616, 420, 60), what, small);
            }
            // teclas, bien grandes (todas las del juego)
            if (wide) ControlsPanel(new Rect(540, 200, W - 600, 390));
            else ControlsPanel(new Rect(Mathf.Min(640f, W * 0.45f), 300, W - 60 - Mathf.Min(640f, W * 0.45f), 285));

            // resúmenes
            float gap = 24f, cardH = 430f;
            float cardW = wide ? Mathf.Min(450f, (W - 540f - 60f - 2 * gap) / 3f) : (W - 120f - 2 * gap) / 3f;
            float x0 = W - 60f - 3 * cardW - 2 * gap;
            float y = 1080f - 50f - cardH;
            DrawCards(x0, y, cardW, cardH, gap, false);

            Credits();
        }

        void Credits()
        {
            GUI.Label(new Rect(60, 1080 - 34, W - 120, 24),
                "Datos y ortofoto: © b5m Gipuzkoa · © geoEuskadi · © IGN (MDT05) · SITNA – Gobierno de Navarra (CC BY) · © OpenStreetMap (ODbL) · tráfico: DFG / Open Data Euskadi · coches CC0", small);
        }

        static readonly string[][] KeysDrive =
        {
            new[] { "W  ↑", "Acelerar" }, new[] { "S  ↓", "Frenar / marcha atrás" }, new[] { "A D  ← →", "Girar" },
            new[] { "Espacio", "Freno de mano" }, new[] { "R", "Recolocar el coche" }, new[] { "Esc  P", "Pausa / menú" },
            new[] { null, "MODO AVIÓN (manual)" },
            new[] { "A D  ← →", "Girar" }, new[] { "W S  ↑ ↓", "Más / menos velocidad" }, new[] { "Q E", "Bajar / subir" }
        };
        static readonly string[][] KeysView =
        {
            new[] { "C", "Cambiar de cámara" }, new[] { "B", "Mirar atrás" }, new[] { "Tab", "Mapa: ver / ocultar" },
            new[] { "N", "Zoom del mapa" }, new[] { "F", "Flechas de guía (carrera)" }, new[] { "O", "Suelo: colores / foto aérea" },
            new[] { "I", "Instrumentos: ver / ocultar" }, new[] { "M", "Micrófono on/off (online)" }
        };
        static readonly string[][] KeysMap =
        {
            new[] { "Clic", "Cruce (en el 1 = cerrar)" }, new[] { "Arrastrar", "Mover el mapa" }, new[] { "Rueda  Q E", "Acercar / alejar" },
            new[] { "WASD", "Mover (Mayús = rápido)" }, new[] { "Retroceso", "Quitar el último cruce" }, new[] { "Esc", "Volver" }
        };
        GUIStyle keyCap, keyTxt, keyHead;
        int keyFont = -1;

        /// <summary>Lista de teclas legible en la pantalla principal.</summary>
        void ControlsPanel(Rect r)
        {
            if (r.width < 420) return;
            var cols = new[] { KeysDrive, KeysView, KeysMap };
            int rows = 0;
            foreach (var col in cols) rows = Mathf.Max(rows, col.Length);
            int fs = r.width >= 1150 ? 20 : (r.width >= 900 ? 17 : 14);
            // que quepan todas las filas (cabecera + teclas + nota del mando) sin barras de desplazamiento
            while (fs > 11 && 12 + fs * 1.75f * (rows + 1) + fs * 2.7f > r.height) fs--;
            if (keyFont != fs)
            {
                keyFont = fs;
                keyCap = new GUIStyle(GUI.skin.box) { fontSize = fs, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false };
                keyCap.normal.background = Tex.Solid(new Color(0.93f, 0.93f, 0.95f, 0.97f));
                keyCap.normal.textColor = new Color(0.08f, 0.09f, 0.12f);
                keyTxt = new GUIStyle(GUI.skin.label) { fontSize = fs, alignment = TextAnchor.MiddleLeft, wordWrap = false };
                keyTxt.normal.textColor = Color.white;
                keyHead = new GUIStyle(GUI.skin.label) { fontSize = fs + 2, fontStyle = FontStyle.Bold };
                keyHead.normal.textColor = new Color(1f, 0.84f, 0.22f);
            }
            GUI.Box(r, GUIContent.none, panel);
            Accent(r);
            float pad = 18f, colGap = 22f;
            float colW = (r.width - 2 * pad - 2 * colGap) / 3f;
            float rowH = fs * 1.75f;
            float capW = Mathf.Min(colW * 0.42f, fs * 6.2f);
            string[] heads = { "CONDUCIR", "CÁMARA Y MAPA", "PANTALLA DEL MAPA" };
            for (int c = 0; c < 3; c++)
            {
                float x = r.x + pad + c * (colW + colGap);
                float y = r.y + 12;
                GUI.Label(new Rect(x, y, colW, fs * 1.6f), heads[c], keyHead);
                y += fs * 1.75f;
                foreach (var k in cols[c])
                {
                    if (k[0] == null) { GUI.Label(new Rect(x, y + rowH * 0.15f, colW, rowH), k[1], keyHead); y += rowH; continue; }   // subtítulo
                    GUI.Box(new Rect(x, y + 2, capW, rowH - 6), k[0], keyCap);
                    GUI.Label(new Rect(x + capW + 10, y, colW - capW - 10, rowH - 2), k[1], keyTxt);
                    y += rowH;
                }
            }
            var padSt = new GUIStyle(small) { fontSize = Mathf.Max(13, fs - 3), wordWrap = true };
            padSt.normal.textColor = new Color(0.85f, 0.88f, 0.92f);
            GUI.Label(new Rect(r.x + pad, r.yMax - fs * 2.6f, r.width - 2 * pad, fs * 2.4f),
                "Mando: gatillo derecho acelerar · gatillo izquierdo frenar · stick girar · A freno de mano · Y recolocar · X cámara · LB mirar atrás · Start pausa · cruceta ↑ zoom, ↓ flechas, ← suelo, → instrumentos", padSt);
        }

        /// <summary>Modo avión: vista automática (el paseo de siempre) o manual (se pilota con las teclas) y dónde se está.</summary>
        void PlanePanel(Rect r)
        {
            GUI.Box(r, GUIContent.none, panel);
            Accent(r);
            GUI.Label(new Rect(r.x + 14, r.y + 8, r.width - 28, 28), "VISTA DEL AVIÓN", h2);
            float bw = (r.width - 38) / 2f;
            if (GUI.Button(new Rect(r.x + 14, r.y + 42, bw, 42), (planeManual ? "" : "● ") + "Automático", planeManual ? btn : onBtn)) { pending = () => { planeManual = false; SetPlaneMode(true); }; }
            if (GUI.Button(new Rect(r.x + 24 + bw, r.y + 42, bw, 42), (planeManual ? "● " : "") + "Manual", planeManual ? onBtn : btn)) { pending = () => { planeManual = true; SetPlaneMode(true); }; }
            string help = planeManual
                ? "A D / ← → girar · W S / ↑ ↓ más o menos velocidad · Q E bajar / subir · Esc salir"
                : "El avión pasea solo de barrio en barrio. Elige Manual para pilotarlo. Esc salir";
            GUI.Label(new Rect(r.x + 14, r.y + 92, r.width - 28, 46), help, small);
            // dónde estamos
            if (fly != null && game.world != null)
            {
                if (planeTopo == null) planeTopo = new Toponymy(game.world);
                Vector3 p = fly.Position;
                var pl = planeTopo.At(p, -1);
                string where = pl.Line2() + (string.IsNullOrEmpty(pl.monte) || pl.monteDist > 600f ? "" : " · Monte " + pl.monte);
                GUI.Label(new Rect(r.x + 14, r.y + 140, r.width - 28, 50), "Sobrevolando " + where, txt);
                GUI.Label(new Rect(r.x + 14, r.y + 194, r.width - 28, 46),
                    Instruments.PointName(fly.HeadingDeg) + " " + Mathf.RoundToInt(fly.HeadingDeg) % 360 + "º · " + Mathf.RoundToInt(fly.SpeedKmh) + " km/h · " +
                    Mathf.RoundToInt(fly.HeightAboveGround) + " m sobre el suelo" + (fly.AtEdge ? " · borde del mapa: volviendo" : ""), small);
            }
        }

        /// <summary>Suelo: los colores de siempre o la ortofoto real de b5m (también con la tecla O en el juego).</summary>
        void SurfaceChooser(Rect r)
        {
            var gs = GroundSurface.Instance;
            if (gs == null) return;
            GUI.Box(r, GUIContent.none, panel);
            GUI.Label(new Rect(r.x + 14, r.y + 6, r.width - 28, 26), "SUPERFICIE DEL SUELO", h2);
            float bw = (r.width - 28 - 10) / 2f;
            bool ortho = gs.Current == GroundSurface.Mode.Ortho;
            if (GUI.Button(new Rect(r.x + 14, r.y + 38, bw, 40), (ortho ? "" : "● ") + "Colores", ortho ? btn : onBtn))
                pending = () => gs.Set(GroundSurface.Mode.Colors, true);
            GUI.enabled = gs.Available;
            if (GUI.Button(new Rect(r.x + 24 + bw, r.y + 38, bw, 40), (ortho ? "● " : "") + gs.OrthoLabel, ortho ? onBtn : btn))
                pending = () => gs.Set(GroundSurface.Mode.Ortho, true);
            GUI.enabled = true;
            GUI.Label(new Rect(r.x + 14, r.yMax + 2, r.width - 28, 24), gs.Available ? "Foto aérea real de b5m (0,5 m por píxel) · tecla O en el juego" : "Falta la ortofoto: DataPipeline/build_ortho.py", small);
        }

        void Quit()
        {
            state.Save();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        /// <summary>Las tres tarjetas de resumen (coche, circuito, carrera). También se usan en la sala online.</summary>
        public void DrawCards(float x0, float y, float cardW, float cardH, float gap, bool online)
        {
            Styles();
            CarCard(new Rect(x0, y, cardW, cardH));
            CircuitCard(new Rect(x0 + cardW + gap, y, cardW, cardH), !RaceLocked);
            RaceCard(new Rect(x0 + 2 * (cardW + gap), y, cardW, cardH), RaceLocked);
        }

        void CarCard(Rect r)
        {
            GUI.Box(r, GUIContent.none, cardBox);
            Accent(r);
            var s = state.settings;
            GUI.Label(new Rect(r.x + 18, r.y + 12, r.width - 36, 30), "COCHE", h2);
            float iw = r.width - 36, ih = Mathf.Min(iw * 0.5625f, r.height - 190);
            iw = ih / 0.5625f;
            Rect img = new Rect(r.x + (r.width - iw) / 2, r.y + 48, iw, ih);
            var tex = studio.Get("card", s.carId, s.paint, Mathf.RoundToInt(iw * ui), Mathf.RoundToInt(ih * ui), true);
            GUI.DrawTexture(img, tex);
            var md = CarModels.Get(s.carId);
            float ty = img.yMax + 8;
            GUI.Label(new Rect(r.x + 18, ty, r.width - 36, 28), md != null ? md.name : s.carId, new GUIStyle(h2) { normal = { textColor = Color.white } });
            var rp = RoomPerfCar;
            if (rp != null)
                GUI.Label(new Rect(r.x + 18, ty + 30, r.width - 36, 46),
                    "Prestaciones de la sala: " + rp.name + " · " + (rp.maxSpeed * 3.6f).ToString("F0") + " km/h", small);
            else if (md != null)
                GUI.Label(new Rect(r.x + 18, ty + 30, r.width - 36, 46),
                    md.category + " · " + (md.maxSpeed * 3.6f).ToString("F0") + " km/h · " + md.mass.ToString("F0") + " kg", small);
            if (GUI.Button(new Rect(r.x + 18, r.yMax - 64, r.width - 36, 48), "Cambiar coche", midBtn)) Go(MenuScreen.Car);
        }

        Texture2D thumb;
        int thumbVersion = -1;

        void CircuitCard(Rect r, bool canChange)
        {
            GUI.Box(r, GUIContent.none, cardBox);
            Accent(r);
            GUI.Label(new Rect(r.x + 18, r.y + 12, r.width - 36, 30), "CIRCUITO", h2);
            float side = Mathf.Min(r.width - 36, r.height - 210);
            Rect img = new Rect(r.x + (r.width - side) / 2, r.y + 48, side, side);
            if (thumbVersion != state.version || thumb == null)
            {
                thumbVersion = state.version;
                Vector2 center; float span;
                if (state.CircuitOk)
                {
                    Vector2 mn = new Vector2(1e9f, 1e9f), mx = new Vector2(-1e9f, -1e9f);
                    foreach (var p in state.circuit.raw) { mn = Vector2.Min(mn, new Vector2(p.x, p.z)); mx = Vector2.Max(mx, new Vector2(p.x, p.z)); }
                    span = Mathf.Max(mx.x - mn.x, mx.y - mn.y) * 1.15f + 140f;
                    center = (mn + mx) * 0.5f;
                }
                else
                {
                    var c = game.world.TownCenter;
                    center = new Vector2(c.x, c.z);
                    span = 2500f;
                }
                thumb = MapView.Thumbnail(game.world, state.CircuitOk ? state.circuit : null, center, span, 320, thumb);
            }
            GUI.DrawTexture(img, thumb);
            if (!state.CircuitOk) GUI.Label(img, "Sin circuito", new GUIStyle(h2) { alignment = TextAnchor.MiddleCenter });
            float ty = img.yMax + 8;
            GUI.Label(new Rect(r.x + 18, ty, r.width - 36, 28), state.CircuitName, new GUIStyle(h2) { normal = { textColor = Color.white } });
            if (state.CircuitOk)
            {
                var c = state.circuit;
                string by = string.IsNullOrEmpty(state.creator) ? "" : "  ·  de " + state.creator;
                GUI.Label(new Rect(r.x + 18, ty + 30, r.width - 36, 46),
                    (c.LapLength / 1000f).ToString("F2") + (c.closed ? " km/vuelta" : " km de A a B") + " · +" + c.elevationGain.ToString("F0") + " m" + by + "\n" + state.CircuitZones(), small);
            }
            GUI.enabled = canChange;
            if (GUI.Button(new Rect(r.x + 18, r.yMax - 64, r.width - 36, 48), canChange ? "Cambiar circuito" : "Lo elige el líder", midBtn)) Go(MenuScreen.Circuit);
            GUI.enabled = true;
        }

        void RaceCard(Rect r, bool locked)
        {
            GUI.Box(r, GUIContent.none, cardBox);
            Accent(r);
            GUILayout.BeginArea(new Rect(r.x + 18, r.y + 12, r.width - 36, r.height - 24));
            GUILayout.Label("CARRERA", h2);
            GUILayout.Space(10);
            RaceControls(txt, locked);
            GUILayout.Space(14);
            var s = state.settings;
            GUILayout.Label("Tráfico: " + TrafficSystem.LevelNames[s.RaceCars].ToLower() + (s.realTimeTraffic && s.traffic ? " (según la hora)" : "") +
                            "   ·   Peatones: " + TrafficSystem.LevelNames[s.RacePeds].ToLower(), txt);
            GUILayout.Label("(se cambia en la pantalla del circuito)", small);
            if (state.CircuitOk)
            {
                GUILayout.Space(8);
                GUILayout.Label("Total: " + (state.circuit.LapLength * (state.circuit.closed ? s.laps : 1) / 1000f).ToString("F1") + " km con " + (s.opponents + (InRoom ? net.Room.players.Count : 1)) + " coches", txt);
            }
            if (locked) GUILayout.Label("En la sala, la carrera la prepara el líder.", small);
            GUILayout.EndArea();
        }

        /// <summary>Vueltas, rivales de la IA (hasta 8) y dificultad. Se usa en la tarjeta y en la pantalla del circuito.</summary>
        public void RaceControls(GUIStyle label, bool locked)
        {
            Styles();
            var s = state.settings;
            bool prev = GUI.enabled;
            GUI.enabled = prev && !locked;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Vueltas", label, GUILayout.Width(130));
            if (state.CircuitOk && !state.circuit.closed)
                GUILayout.Label("1 (de A a B)", new GUIStyle(label) { fontStyle = FontStyle.Bold }, GUILayout.Height(34));
            else
            {
                if (GUILayout.Button("−", btn, GUILayout.Width(44), GUILayout.Height(34))) s.laps = Mathf.Max(1, s.laps - 1);
                GUILayout.Label(s.laps.ToString(), new GUIStyle(label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 22 }, GUILayout.Width(50), GUILayout.Height(34));
                if (GUILayout.Button("+", btn, GUILayout.Width(44), GUILayout.Height(34))) s.laps = Mathf.Min(20, s.laps + 1);
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Rivales IA", label, GUILayout.Width(130));
            if (GUILayout.Button("−", btn, GUILayout.Width(44), GUILayout.Height(34))) s.opponents = Mathf.Max(0, s.opponents - 1);
            GUILayout.Label(s.opponents.ToString(), new GUIStyle(label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 22 }, GUILayout.Width(50), GUILayout.Height(34));
            if (GUILayout.Button("+", btn, GUILayout.Width(44), GUILayout.Height(34))) s.opponents = Mathf.Min(MaxOpponents(), s.opponents + 1);
            GUILayout.EndHorizontal();
            GUILayout.Label("Dificultad", label);
            s.difficulty = GUILayout.Toolbar(s.difficulty, RaceSettings.DifficultyNames, GUILayout.Height(34));
            GUI.enabled = prev;
        }

        // ------------------------------------------------------------------ pantalla de coche
        static readonly Dictionary<string, string> CarText = new Dictionary<string, string>
        {
            { "turismo", "Deportivo equilibrado: rápido, ágil y fácil de llevar." },
            { "berlina", "Cómoda y estable. Ideal para empezar." },
            { "utilitario", "Ligero y manejable en las calles estrechas del casco." },
            { "furgoneta", "Pesada y lenta, pero empuja fuerte en los choques." },
            { "f1", "Velocidad y agarre enormes. Para pilotos expertos." },
            { "f1futuro", "Lo último en Fórmula 1: todavía más rápido." },
            { "suv", "Alto y robusto. Cuidado en las curvas y en las bajadas." },
        };

        float maxSpd = 1, maxAcc = 1, maxBrk = 1, maxGrip = 1;

        void ComputeMaxStats()
        {
            if (maxSpd > 1) return;
            foreach (var e in CarModels.Selectable)
            {
                var d = CarModels.Get(e.id);
                if (d == null) continue;
                maxSpd = Mathf.Max(maxSpd, d.maxSpeed);
                maxAcc = Mathf.Max(maxAcc, d.engine / Mathf.Max(1f, d.mass));
                maxBrk = Mathf.Max(maxBrk, d.brake / Mathf.Max(1f, d.mass));
                maxGrip = Mathf.Max(maxGrip, d.grip);
            }
        }

        void CarScreen()
        {
            ComputeMaxStats();
            var s = state.settings;
            var cars = CarModels.Selectable;
            bool wide = W >= 1700f;
            GUI.Label(new Rect(60, 30, 900, 56), "ELIGE TU COCHE", h1);

            int cols = wide ? 3 : 2;
            float tw = wide ? 290f : 250f, imgH = tw * 0.5625f, th = imgH + 50f, gap = 14f;
            float gx = 60f, gy = 112f;
            for (int i = 0; i < cars.Count; i++)
            {
                var e = cars[i];
                Rect tr = new Rect(gx + (i % cols) * (tw + gap), gy + (i / cols) * (th + gap), tw, th);
                bool on = s.carId == e.id;
                GUI.Box(tr, GUIContent.none, cardBox);
                if (on)
                {
                    var y = Tex.Solid(new Color(1f, 0.8f, 0.15f));
                    GUI.DrawTexture(new Rect(tr.x - 3, tr.y - 3, tr.width + 6, 3), y);
                    GUI.DrawTexture(new Rect(tr.x - 3, tr.yMax, tr.width + 6, 3), y);
                    GUI.DrawTexture(new Rect(tr.x - 3, tr.y, 3, tr.height), y);
                    GUI.DrawTexture(new Rect(tr.xMax, tr.y, 3, tr.height), y);
                }
                var tex = studio.Get("tile_" + e.id, e.id, s.paint, Mathf.RoundToInt(tw * ui), Mathf.RoundToInt(imgH * ui), false);
                GUI.DrawTexture(new Rect(tr.x, tr.y, tw, imgH), tex);
                var ns = new GUIStyle(txt) { fontStyle = FontStyle.Bold, wordWrap = false };
                if (on) ns.normal.textColor = new Color(1f, 0.85f, 0.25f);
                GUI.Label(new Rect(tr.x + 10, tr.y + imgH + 4, tw - 20, 24), e.name, ns);
                GUI.Label(new Rect(tr.x + 10, tr.y + imgH + 26, tw - 20, 20), e.category, small);
                if (GUI.Button(tr, GUIContent.none, GUIStyle.none)) { var id = e.id; s.carId = id; }
            }

            // ficha del coche elegido
            float px = gx + cols * (tw + gap) + 16f;
            Rect pr = new Rect(px, 112, W - px - 60, 1080 - 112 - 50);
            GUI.Box(pr, GUIContent.none, panel);
            Accent(pr);
            float iw = pr.width - 40, ih = Mathf.Min(iw * 0.5625f, 430f);
            iw = ih / 0.5625f;
            Rect img = new Rect(pr.x + (pr.width - iw) / 2, pr.y + 20, iw, ih);
            GUI.DrawTexture(img, studio.Get("big", s.carId, s.paint, Mathf.RoundToInt(iw * ui), Mathf.RoundToInt(ih * ui), true));

            var md = CarModels.Get(s.carId);
            GUILayout.BeginArea(new Rect(pr.x + 24, img.yMax + 10, pr.width - 48, pr.yMax - img.yMax - 20));
            GUILayout.Label(md != null ? md.name : s.carId, carName);
            string desc;
            if (md != null && !string.IsNullOrEmpty(md.description)) desc = md.description;
            else CarText.TryGetValue(s.carId, out desc);
            GUILayout.Label((md != null ? md.category : "") + (string.IsNullOrEmpty(desc) ? "" : " · " + desc), txt);
            GUILayout.Space(6);
            var roomCar = RoomPerfCar;
            if (roomCar != null)
            {
                // online: todos corren con las prestaciones del coche del líder; aquí sólo se elige el aspecto
                GUILayout.Label("En esta sala todos corréis con la potencia y las características del coche del líder: " + roomCar.name +
                                ". Aquí eliges el aspecto: el modelo y el color.", txt);
                var rc = roomCar;
                Stat("Velocidad punta", rc.maxSpeed / maxSpd, (rc.maxSpeed * 3.6f).ToString("F0") + " km/h");
                Stat("Aceleración", (rc.engine / rc.mass) / maxAcc, "");
                Stat("Frenada", (rc.brake / rc.mass) / maxBrk, "");
                Stat("Agarre", rc.grip / maxGrip, rc.grip.ToString("F2") + " g");
                if (md != null)
                    GUILayout.Label("Peso " + rc.mass.ToString("F0") + " kg  ·  Largo " + md.size[2].ToString("F1") + " m  ·  Ancho " + md.size[0].ToString("F1") + " m", small);
            }
            else if (md != null)
            {
                Stat("Velocidad punta", md.maxSpeed / maxSpd, (md.maxSpeed * 3.6f).ToString("F0") + " km/h");
                Stat("Aceleración", (md.engine / md.mass) / maxAcc, "");
                Stat("Frenada", (md.brake / md.mass) / maxBrk, "");
                Stat("Agarre", md.grip / maxGrip, md.grip.ToString("F2") + " g");
                GUILayout.Label("Peso " + md.mass.ToString("F0") + " kg  ·  Largo " + md.size[2].ToString("F1") + " m  ·  Ancho " + md.size[0].ToString("F1") + " m", small);
            }
            GUILayout.Space(10);
            GUILayout.Label("Color", h2);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < MenuState.PaintChoices.Length; i++)
            {
                var pc = MenuState.PaintChoices[i];
                var c0 = GUI.backgroundColor;
                GUI.backgroundColor = pc;
                bool on = Mathf.Abs(s.paint.r - pc.r) < 0.02f && Mathf.Abs(s.paint.g - pc.g) < 0.02f && Mathf.Abs(s.paint.b - pc.b) < 0.02f;
                var sw = new GUIStyle(GUI.skin.button) { fontSize = 20, fontStyle = FontStyle.Bold };
                sw.normal.background = sw.hover.background = sw.active.background = Tex.Solid(pc);
                sw.normal.textColor = sw.hover.textColor = pc.grayscale > 0.6f ? Color.black : Color.white;
                if (GUILayout.Button(on ? "✓" : "", sw, GUILayout.Width(44), GUILayout.Height(44))) s.paint = pc;
                GUI.backgroundColor = c0;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(InRoom ? (IsLeader ? "Los demás verán tu coche con este color. Todos los de la sala correrán con la potencia y las características de este coche."
                                              : "Los demás jugadores verán tu coche con este color.") : "Los rivales de la IA llevan el mismo modelo, cada uno con su color.", small);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("LISTO", midBtn, GUILayout.Height(60))) pending = Back;
            GUILayout.EndArea();
        }

        void Stat(string name, float frac, string val)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, txt, GUILayout.Width(170), GUILayout.Height(28));
            Rect r = GUILayoutUtility.GetRect(100, 2000, 28, 28);
            Rect bar = new Rect(r.x, r.y + 8, r.width, 14);
            GUI.DrawTexture(bar, Tex.Solid(new Color(1, 1, 1, 0.12f)));
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(frac), bar.height), Tex.Solid(Color.Lerp(new Color(0.3f, 0.75f, 1f), new Color(1f, 0.8f, 0.15f), Mathf.Clamp01(frac))));
            GUILayout.Label(val, statVal, GUILayout.Width(110), GUILayout.Height(28));
            GUILayout.EndHorizontal();
        }
    }
}
