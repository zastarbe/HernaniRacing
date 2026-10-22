using System.Collections.Generic;
using System.Linq;
using HernaniRacing.Net;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Pantalla de mapa: el jugador elige 4 cruces reales de Hernani y el circuito se cierra
    /// uniendo 1→2→3→4→1 por la red viaria b5m. También hay circuitos predefinidos y aleatorios.
    /// </summary>
    public class CircuitSelector : MonoBehaviour
    {
        public GameBootstrap game;
        World world;
        readonly List<int> sel = new List<int>();
        Circuit circuit;
        readonly RaceSettings settings = new RaceSettings();
        LineRenderer routeLine;
        bool open;
        int hover = -1;
        Vector3 camTarget;
        float orthoSize = 900f;
        bool showZones = true, showLandmarks = true;
        Vector2 dragStart; Vector3 dragCam; bool dragging;
        Rect panelScreenRect;
        Vector2 scroll;
        string info;
        System.Action pending;   // las acciones se aplican fuera de OnGUI (evita errores de layout)
        public bool IsOpen { get { return open; } }

        // ---- online: el líder elige, los demás ven lo mismo
        string presetName = "";
        RoomSettings lastSent, applied;
        float nextSend;
        NetSession Net { get { return NetSession.I; } }
        bool OnlineRoom { get { return Net != null && Net.InRoom; } }
        bool Locked { get { return OnlineRoom && !Net.IsLeader; } }

        static readonly Color[] PaintChoices =
        {
            new Color(0.85f, 0.08f, 0.1f), new Color(0.1f, 0.3f, 0.85f), new Color(0.95f, 0.95f, 0.95f), new Color(0.1f, 0.1f, 0.12f),
            new Color(1f, 0.75f, 0.1f), new Color(0.1f, 0.6f, 0.25f)
        };

        static readonly Color[] SelColors =
        {
            new Color(0.1f, 0.85f, 0.2f), new Color(0.15f, 0.55f, 1f), new Color(1f, 0.6f, 0.1f), new Color(0.85f, 0.2f, 0.9f)
        };

        public void Open()
        {
            world = game.world;
            open = true;
            enabled = true;
            var cam = game.MainCamera;
            var cc = cam.GetComponent<ChaseCamera>();
            if (cc != null) cc.enabled = false;
            cam.orthographic = true;
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 4000f;
            if (camTarget == Vector3.zero) camTarget = world.TownCenter;
            if (world.trees != null) { world.trees.drawAll = true; }
            if (routeLine == null)
            {
                var go = new GameObject("Trazado (vista previa)");
                routeLine = go.AddComponent<LineRenderer>();
                routeLine.sharedMaterial = Mat.UnlitColor(new Color(1f, 0.15f, 0.1f));
                routeLine.useWorldSpace = true;
                routeLine.numCornerVertices = 2;
                routeLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                routeLine.receiveShadows = false;
            }
            routeLine.enabled = true;
            UpdateRouteLine();
        }

        public void Close()
        {
            open = false;
            if (routeLine != null) routeLine.enabled = false;
            if (world != null && world.trees != null) world.trees.drawAll = false;
        }

        void Update()
        {
            if (!open || world == null) return;
            if (pending != null) { var p = pending; pending = null; p(); }
            if (OnlineRoom) SyncOnline(); else { lastSent = null; applied = null; }
            var cam = game.MainCamera;
            float k = (GameInput.Fast ? 2.5f : 1f) * orthoSize * 1.2f * Time.unscaledDeltaTime;
            bool typing = GUIUtility.keyboardControl != 0;
            Vector2 pan = typing ? Vector2.zero : GameInput.Pan;
            camTarget += new Vector3(pan.x, 0, pan.y) * k;
            orthoSize = Mathf.Clamp(orthoSize * (1f - (typing ? 0f : GameInput.Zoom) * Time.unscaledDeltaTime * 1.5f), 80f, MaxOrtho());
            camTarget.x = Mathf.Clamp(camTarget.x, 0, world.sizeX);
            camTarget.z = Mathf.Clamp(camTarget.z, 0, world.sizeZ);
            cam.orthographicSize = orthoSize;
            cam.transform.position = new Vector3(camTarget.x, 1500f, camTarget.z);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            if (routeLine != null) routeLine.widthMultiplier = Mathf.Max(2.5f, orthoSize * 0.009f);
        }

        /// <summary>Zoom máximo: se ve todo el área (de Martutene a Ugaldetxo).</summary>
        float MaxOrtho()
        {
            float aspect = Mathf.Max(0.5f, Screen.width / (float)Mathf.Max(1, Screen.height));
            return Mathf.Max(1700f, Mathf.Max(world.sizeZ * 0.5f, world.sizeX * 0.5f / aspect) * 1.05f);
        }

        // ------------------------------------------------------------------ selección
        void Toggle(int node)
        {
            if (Locked) return;
            presetName = "";
            int i = sel.IndexOf(node);
            if (i >= 0) sel.RemoveAt(i);
            else if (sel.Count < 4) sel.Add(node);
            else { sel[3] = node; }
            Rebuild();
        }

        void Rebuild()
        {
            circuit = null;
            info = null;
            if (sel.Count == 4)
            {
                circuit = Circuit.Build(world.graph, sel.ToArray());
                if (circuit.error != null) info = circuit.error;
            }
            UpdateRouteLine();
        }

        void UpdateRouteLine()
        {
            if (routeLine == null || world == null) return;
            var pts = new List<Vector3>();
            if (circuit != null && circuit.raw != null)
            {
                foreach (var p in circuit.raw) pts.Add(p + Vector3.up * 3f);
                pts.Add(circuit.raw[0] + Vector3.up * 3f);
            }
            else if (sel.Count >= 2)
            {
                var used = new HashSet<int>();
                for (int i = 0; i + 1 < sel.Count; i++)
                {
                    var path = world.graph.ShortestPath(sel[i], sel[i + 1], used, 4f);
                    if (path == null) continue;
                    foreach (var st in path)
                    {
                        used.Add(st.edge);
                        var e = world.graph.edges[st.edge];
                        for (int k = 0; k < e.pts.Length; k++)
                            pts.Add(e.pts[st.forward ? k : e.pts.Length - 1 - k] + Vector3.up * 3f);
                    }
                }
            }
            routeLine.positionCount = pts.Count;
            routeLine.SetPositions(pts.ToArray());
        }

        public void ApplyPreset(PresetData p)
        {
            if (Locked) return;
            presetName = p.name;
            sel.Clear();
            for (int i = 0; i + 1 < p.points.Length && sel.Count < 4; i += 2)
            {
                int n = world.graph.NearestJunction(new Vector3(p.points[i], 0, p.points[i + 1]));
                if (n >= 0 && !sel.Contains(n)) sel.Add(n);
            }
            Rebuild();
            if (circuit != null) FocusCircuit();
        }

        void RandomCircuit()
        {
            if (Locked) return;
            presetName = "Aleatorio";
            var g = world.graph;
            // cruces alrededor de lo que se está mirando en el mapa (radio 1.3 km; si hay pocos, alrededor del centro de Hernani)
            Vector3 c = camTarget;
            var pool = g.junctions.Where(j => (new Vector2(g.nodes[j].x - c.x, g.nodes[j].z - c.z)).magnitude < 1300f).ToList();
            if (pool.Count < 12)
            {
                c = world.TownCenter;
                pool = g.junctions.Where(j => (new Vector2(g.nodes[j].x - c.x, g.nodes[j].z - c.z)).magnitude < 1300f).ToList();
            }
            var rnd = new System.Random();
            Circuit best = null; List<int> bestSel = null; float bestScore = float.MaxValue;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                var pick = new List<int>();
                int guard = 0;
                while (pick.Count < 4 && guard++ < 200)
                {
                    int j = pool[rnd.Next(pool.Count)];
                    bool far = pick.All(o => Vector3.Distance(g.nodes[o], g.nodes[j]) > 280f);
                    if (far) pick.Add(j);
                }
                if (pick.Count < 4) continue;
                // ordenar por ángulo alrededor del centroide para que el bucle no se cruce
                Vector3 cen = Vector3.zero;
                foreach (int j in pick) cen += g.nodes[j];
                cen /= 4f;
                pick = pick.OrderBy(j => Mathf.Atan2(g.nodes[j].z - cen.z, g.nodes[j].x - cen.x)).ToList();
                var cc = Circuit.Build(g, pick.ToArray());
                if (cc.error != null) continue;
                float score = cc.reusedLength / cc.length * 10f + Mathf.Abs(cc.length - 3000f) / 3000f + (cc.hasUTurn ? 2f : 0f);
                if (score < bestScore) { bestScore = score; best = cc; bestSel = pick; }
            }
            if (best != null)
            {
                sel.Clear(); sel.AddRange(bestSel);
                circuit = best; info = null;
                UpdateRouteLine();
                FocusCircuit();
            }
        }

        void FocusCircuit()
        {
            Vector3 mn = Vector3.one * float.MaxValue, mx = Vector3.one * float.MinValue;
            foreach (var p in circuit.raw) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            camTarget = (mn + mx) * 0.5f;
            camTarget.x -= 0.18f * Mathf.Max(mx.x - mn.x, mx.z - mn.z);   // deja sitio al panel
            orthoSize = Mathf.Clamp(Mathf.Max(mx.z - mn.z, (mx.x - mn.x) / game.MainCamera.aspect) * 0.75f + 150f, 200f, 1700f);
        }

        // ------------------------------------------------------------------ GUI
        GUIStyle title, h2, txt, small, tagSt, tagBold, box, tip;

        // ------------------------------------------------------------------ online
        RoomSettings CurrentSettings()
        {
            var rs = NetSession.MakeSettings(sel, settings, presetName);
            int maxAi = Mathf.Max(0, NetProto.MaxCarsPerRace - Net.Room.players.Count);
            if (rs.opponents > maxAi) rs.opponents = (byte)maxAi;
            return rs;
        }

        void SendSettingsNow()
        {
            if (!OnlineRoom || !Net.IsLeader) return;
            var rs = CurrentSettings();
            Net.Core.SendSettings(rs);
            lastSent = rs;
        }

        void SyncOnline()
        {
            var room = Net.Room;
            Net.SetCarAndColor(settings.carId, settings.paint);
            if (Net.IsLeader)
            {
                applied = null;
                if (room.phase != RoomPhase.Lobby || Time.unscaledTime < nextSend) return;
                var rs = CurrentSettings();
                if (!rs.SameAs(room.settings) && (lastSent == null || !rs.SameAs(lastSent)))
                {
                    Net.Core.SendSettings(rs);
                    lastSent = rs;
                    nextSend = Time.unscaledTime + 0.25f;
                }
                return;
            }
            lastSent = null;
            var r = room.settings;
            if (applied != null && r.SameAs(applied)) return;
            applied = r.Clone();
            sel.Clear();
            foreach (int n in r.nodes) if (n >= 0 && n < world.graph.nodes.Length && !sel.Contains(n)) sel.Add(n);
            presetName = r.circuitName;
            settings.laps = r.laps; settings.opponents = r.opponents; settings.difficulty = r.difficulty;
            settings.traffic = r.traffic; settings.pedestrians = r.pedestrians;
            settings.freeTraffic = r.freeTraffic; settings.freePedestrians = r.freePedestrians;
            Rebuild();
            if (circuit != null) FocusCircuit();
        }

        void OnlineButtons(GUIStyle go, GUIStyle go2)
        {
            var room = Net.Room;
            var leader = room.Leader;
            string leaderNick = leader != null ? leader.profile.nick : "el líder";
            var me = room.Find(Net.MyId);
            if (room.phase == RoomPhase.Lobby)
            {
                if (Net.IsLeader)
                {
                    GUI.enabled = circuit != null && circuit.error == null;
                    if (GUILayout.Button("¡CARRERA ONLINE!", go)) pending = () => { SendSettingsNow(); Net.Core.StartGame(GameMode.Race); };
                    GUI.enabled = true;
                    GUILayout.Space(6);
                    if (GUILayout.Button("PASEO LIBRE ONLINE", go2)) pending = () => { SendSettingsNow(); Net.Core.StartGame(GameMode.Free); };
                    GUILayout.Label("Eres el líder de la sala " + room.code + " (" + room.players.Count + " jugador" + (room.players.Count == 1 ? "" : "es") + "). En el paseo se empieza junto al cruce 1.", small);
                }
                else
                {
                    GUILayout.Label("Esperando a que " + leaderNick + " empiece la partida...", txt);
                    GUILayout.Label("El líder elige el circuito, las vueltas, la IA y el tráfico. Tú eliges tu coche y tu color.", small);
                }
            }
            else
            {
                if (room.phase == RoomPhase.Free && me != null && !me.inGame)
                {
                    if (GUILayout.Button("ENTRAR AL PASEO", go)) pending = () => Net.Core.EnterGame();
                }
                else if (room.phase == RoomPhase.Race && me != null && !me.inGame)
                    GUILayout.Label("Hay una carrera en marcha. Podrás correr en la siguiente.", txt);
                if (Net.IsLeader && GUILayout.Button("Terminar la partida para todos", go2)) pending = () => Net.Core.EndGame();
            }
            GUILayout.Label("Para jugar tú solo, sal de la sala (panel de la derecha).", small);
        }

        void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold };
            title.normal.textColor = new Color(1f, 0.85f, 0.25f);
            h2 = new GUIStyle(GUI.skin.label) { fontSize = 21, fontStyle = FontStyle.Bold };
            h2.normal.textColor = Color.white;
            txt = new GUIStyle(GUI.skin.label) { fontSize = 17, wordWrap = true };
            txt.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
            small = new GUIStyle(txt) { fontSize = 13 };
            small.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
            tagSt = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            tagSt.normal.textColor = Color.white;
            tagBold = new GUIStyle(tagSt) { fontSize = 18, fontStyle = FontStyle.Bold };
            tagBold.normal.textColor = new Color(1f, 0.95f, 0.6f);
            box = new GUIStyle(GUI.skin.box);
            box.normal.background = Tex.Solid(new Color(0.05f, 0.06f, 0.08f, 0.82f));
            tip = new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.MiddleLeft, wordWrap = false };
            tip.normal.background = Tex.Solid(new Color(0, 0, 0, 0.8f));
            tip.normal.textColor = Color.white;
        }

        void OnGUI()
        {
            if (!open || world == null) return;
            Styles();
            var cam = game.MainCamera;
            float ui = Screen.height / 1080f;
            Event ev = Event.current;
            Vector2 mouse = ev.mousePosition;
            bool overPanel = panelScreenRect.Contains(mouse) || NetLobbyUI.Blocks(mouse);

            // ---- etiquetas de barrios y lugares (coordenadas de pantalla reales)
            GUI.matrix = Matrix4x4.identity;
            if (showZones)
                foreach (var z in world.data.scenery.zones)
                {
                    if (!z.primary && orthoSize > 700f) continue;
                    Vector3 sp = cam.WorldToScreenPoint(new Vector3(z.x, z.y, z.z));
                    var st = z.primary ? tagBold : tagSt;
                    var size = st.CalcSize(new GUIContent(z.name));
                    Rect r = new Rect(sp.x - size.x / 2 - 4, Screen.height - sp.y - size.y / 2, size.x + 8, size.y);
                    GUI.Box(r, GUIContent.none, box);
                    GUI.Label(r, z.name, st);
                }
            if (showLandmarks && orthoSize < 330f)
                foreach (var l in world.data.scenery.landmarks)
                {
                    Vector3 sp = cam.WorldToScreenPoint(new Vector3(l.x, l.y, l.z));
                    if (sp.x < 0 || sp.x > Screen.width || sp.y < 0 || sp.y > Screen.height) continue;
                    string n = l.name.Split('/')[0].Trim();
                    GUI.Label(new Rect(sp.x - 90, Screen.height - sp.y - 10, 180, 20), "· " + n, small);
                }

            // ---- cruces
            hover = -1;
            float bestD = 16f;
            var g = world.graph;
            float dot = Mathf.Clamp(900f / orthoSize * 4f, 4f, 11f);
            var yellow = Tex.Solid(new Color(1f, 0.85f, 0.1f, 0.95f));
            var dark = Tex.Solid(new Color(0, 0, 0, 0.85f));
            foreach (int j in g.junctions)
            {
                Vector3 sp = cam.WorldToScreenPoint(g.nodes[j]);
                if (sp.x < -20 || sp.x > Screen.width + 20 || sp.y < -20 || sp.y > Screen.height + 20) continue;
                Vector2 gp = new Vector2(sp.x, Screen.height - sp.y);
                GUI.DrawTexture(new Rect(gp.x - dot / 2 - 1, gp.y - dot / 2 - 1, dot + 2, dot + 2), dark);
                GUI.DrawTexture(new Rect(gp.x - dot / 2, gp.y - dot / 2, dot, dot), yellow);
                float d = Vector2.Distance(gp, mouse);
                if (d < bestD && !overPanel) { bestD = d; hover = j; }
            }
            // seleccionados
            for (int i = 0; i < sel.Count; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(g.nodes[sel[i]]);
                Vector2 gp = new Vector2(sp.x, Screen.height - sp.y);
                GUI.DrawTexture(new Rect(gp.x - 17, gp.y - 17, 34, 34), Tex.Solid(Color.black));
                GUI.DrawTexture(new Rect(gp.x - 15, gp.y - 15, 30, 30), Tex.Solid(SelColors[i]));
                var ns = new GUIStyle(tagBold) { fontSize = 20 }; ns.normal.textColor = Color.black;
                GUI.Label(new Rect(gp.x - 15, gp.y - 15, 30, 30), (i + 1).ToString(), ns);
            }
            if (hover >= 0)
            {
                Vector3 sp = cam.WorldToScreenPoint(g.nodes[hover]);
                Vector2 gp = new Vector2(sp.x, Screen.height - sp.y);
                GUI.DrawTexture(new Rect(gp.x - 9, gp.y - 9, 18, 18), Tex.Solid(Color.white));
                var zone = world.ZoneAt(g.nodes[hover]);
                string t = " " + g.JunctionLabel(hover) + "  ·  " + (zone != null ? zone.name : "") + "  ·  " + g.nodes[hover].y.ToString("F0") + " m ";
                var size = tip.CalcSize(new GUIContent(t));
                GUI.Box(new Rect(mouse.x + 16, mouse.y + 12, size.x, 30), t, tip);
            }

            // ---- ratón
            if (ev.type == EventType.ScrollWheel && !overPanel)
            {
                float f = ev.delta.y > 0 ? 1.12f : 1f / 1.12f;
                // zoom hacia el cursor
                Vector3 before = cam.ScreenToWorldPoint(new Vector3(mouse.x, Screen.height - mouse.y, 0));
                orthoSize = Mathf.Clamp(orthoSize * f, 80f, 1700f);
                cam.orthographicSize = orthoSize;
                Vector3 after = cam.ScreenToWorldPoint(new Vector3(mouse.x, Screen.height - mouse.y, 0));
                camTarget += new Vector3(before.x - after.x, 0, before.z - after.z);
                ev.Use();
            }
            if (ev.type == EventType.MouseDown && !overPanel)
            {
                if (ev.button == 0 && hover >= 0) { int h = hover; pending = () => Toggle(h); ev.Use(); }
                else if (ev.button == 1 || ev.button == 2 || ev.button == 0) { dragging = true; dragStart = mouse; dragCam = camTarget; ev.Use(); }
            }
            if (ev.type == EventType.MouseDrag && dragging)
            {
                float worldPerPx = orthoSize * 2f / Screen.height;
                Vector2 dlt = mouse - dragStart;
                camTarget = dragCam + new Vector3(-dlt.x * worldPerPx, 0, dlt.y * worldPerPx);
                ev.Use();
            }
            if (ev.type == EventType.MouseUp) dragging = false;
            if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Backspace && sel.Count > 0 && !Locked && GUIUtility.keyboardControl == 0) { pending = () => { if (sel.Count > 0) { sel.RemoveAt(sel.Count - 1); Rebuild(); } }; ev.Use(); }

            // ---- panel
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(ui, ui, 1));
            float H = 1080f;
            Rect panel = new Rect(16, 16, 460, H - 32);
            panelScreenRect = new Rect(panel.x * ui, panel.y * ui, panel.width * ui, panel.height * ui);
            GUI.Box(panel, GUIContent.none, box);
            GUILayout.BeginArea(new Rect(panel.x + 18, panel.y + 12, panel.width - 36, panel.height - 24));
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Label("HERNANI RACING", title);
            GUILayout.Label("Datos reales de b5m (Diputación Foral de Gipuzkoa) y geoEuskadi", small);
            GUILayout.Space(8);
            GUILayout.Label("Haz clic en 4 cruces (puntos amarillos) en el orden de carrera. El circuito se cierra uniendo 1 → 2 → 3 → 4 → 1 por calles reales. Puedes salirte de la pista, pero el progreso no cuenta hasta que vuelvas por donde saliste.", txt);
            GUILayout.Label("Ratón: arrastrar = mover, rueda = zoom · WASD/flechas, Q/E · Retroceso = quitar último", small);
            GUILayout.Space(8);

            bool lockUI = Locked;
            if (lockUI) GUILayout.Label("Sala online: el circuito lo elige el líder.", txt);
            GUI.enabled = !lockUI;
            GUILayout.Label("Cruces elegidos", h2);
            for (int i = 0; i < 4; i++)
            {
                GUILayout.BeginHorizontal();
                var c = GUI.color; GUI.color = SelColors[i];
                GUILayout.Label("●", h2, GUILayout.Width(22));
                GUI.color = c;
                if (i < sel.Count)
                {
                    var z = world.ZoneAt(g.nodes[sel[i]]);
                    GUILayout.Label((i + 1) + ". " + g.JunctionLabel(sel[i]) + (z != null ? "  (" + z.name + ")" : ""), txt);
                    if (GUILayout.Button("✕", GUILayout.Width(30))) { int ri = i; pending = () => { if (ri < sel.Count) { sel.RemoveAt(ri); Rebuild(); } }; }
                }
                else GUILayout.Label((i + 1) + ". —", txt);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(6);

            if (circuit != null && circuit.error == null)
            {
                GUILayout.Label("Circuito", h2);
                var streets = new HashSet<string>();
                foreach (var st in circuit.steps) streets.Add(g.edges[st.edge].name);
                GUILayout.Label("Longitud de vuelta: " + (circuit.length / 1000f).ToString("F2") + " km   ·   Total: " + (circuit.length * settings.laps / 1000f).ToString("F1") + " km", txt);
                GUILayout.Label("Desnivel acumulado: +" + circuit.elevationGain.ToString("F0") + " m   ·   Pendiente máx.: " + circuit.maxGradePct.ToString("F0") + " %", txt);
                GUILayout.Label("Cotas: " + circuit.minElev.ToString("F0") + " – " + circuit.maxElev.ToString("F0") + " m   ·   " + streets.Count + " calles distintas", txt);
                GUILayout.Label("Tramos: " + string.Join(" / ", circuit.legLength.Select(l => l.ToString("F0") + " m").ToArray()), small);
                if (circuit.reusedLength > 1f)
                    GUILayout.Label("⚠ Se repiten " + circuit.reusedLength.ToString("F0") + " m de calle (ida y vuelta). Prueba otro orden de cruces.", txt);
                if (circuit.hasUTurn) GUILayout.Label("⚠ Incluye un giro de 180º.", txt);
                GUILayout.Label("Calles: " + string.Join(", ", streets.Take(10).ToArray()) + (streets.Count > 10 ? "…" : ""), small);
            }
            else if (!string.IsNullOrEmpty(info)) GUILayout.Label("⚠ " + info, txt);
            else GUILayout.Label(sel.Count < 4 ? "Faltan " + (4 - sel.Count) + " cruces." : "", txt);

            GUILayout.Space(8);
            GUILayout.Label("Carrera", h2);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Vueltas: " + settings.laps, txt, GUILayout.Width(160));
            if (GUILayout.Button("−", GUILayout.Width(40))) settings.laps = Mathf.Max(1, settings.laps - 1);
            if (GUILayout.Button("+", GUILayout.Width(40))) settings.laps = Mathf.Min(20, settings.laps + 1);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Rivales IA: " + settings.opponents, txt, GUILayout.Width(160));
            if (GUILayout.Button("−", GUILayout.Width(40))) settings.opponents = Mathf.Max(0, settings.opponents - 1);
            if (GUILayout.Button("+", GUILayout.Width(40))) settings.opponents = Mathf.Min(OnlineRoom ? Mathf.Max(0, NetProto.MaxCarsPerRace - Net.Room.players.Count) : 5, settings.opponents + 1);
            GUILayout.EndHorizontal();
            settings.difficulty = GUILayout.Toolbar(settings.difficulty, RaceSettings.DifficultyNames);

            GUI.enabled = true;
            GUILayout.Space(8);
            GUILayout.Label("Coche", h2);
            var carsSel = CarModels.Selectable;
            if (carsSel.Count == 0) GUILayout.Label("(sin modelos: se usa el coche low poly)", small);
            for (int i = 0; i < carsSel.Count; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int k = i; k < Mathf.Min(i + 2, carsSel.Count); k++)
                {
                    var ce = carsSel[k];
                    bool on = settings.carId == ce.id;
                    var st = new GUIStyle(GUI.skin.button) { fontSize = 15, fontStyle = on ? FontStyle.Bold : FontStyle.Normal };
                    if (on) st.normal.textColor = st.hover.textColor = new Color(1f, 0.85f, 0.25f);
                    if (GUILayout.Button((on ? "► " : "") + ce.name + "\n" + ce.category, st, GUILayout.Height(44))) settings.carId = ce.id;
                }
                GUILayout.EndHorizontal();
            }
            var md = CarModels.Get(settings.carId);
            if (md != null)
                GUILayout.Label(md.name + ": " + md.size[2].ToString("F1") + " m · " + md.mass.ToString("F0") + " kg · punta ~" + (md.maxSpeed * 3.6f).ToString("F0") +
                                " km/h · agarre " + md.grip.ToString("F1") + "  (los rivales llevan el mismo coche)", small);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Color", txt, GUILayout.Width(60));
            foreach (var pc in PaintChoices)
            {
                var c0 = GUI.backgroundColor;
                GUI.backgroundColor = pc;
                bool on = (settings.paint - pc).maxColorComponent < 0.02f && (pc - settings.paint).maxColorComponent < 0.02f;
                if (GUILayout.Button(on ? "●" : "", GUILayout.Width(34), GUILayout.Height(26))) settings.paint = pc;
                GUI.backgroundColor = c0;
            }
            GUILayout.EndHorizontal();

            GUI.enabled = !lockUI;
            GUILayout.Space(6);
            GUILayout.Label("Tráfico y peatones", h2);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Carrera:", txt, GUILayout.Width(90));
            settings.traffic = GUILayout.Toggle(settings.traffic, " Tráfico");
            settings.pedestrians = GUILayout.Toggle(settings.pedestrians, " Peatones");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Paseo:", txt, GUILayout.Width(90));
            settings.freeTraffic = GUILayout.Toggle(settings.freeTraffic, " Tráfico");
            settings.freePedestrians = GUILayout.Toggle(settings.freePedestrians, " Peatones");
            GUILayout.EndHorizontal();
            GUILayout.Label("Los coches respetan sentidos únicos, semáforos, STOP, ceda el paso, rotondas y pasos de cebra (datos OpenStreetMap).", small);

            GUILayout.Space(8);
            GUILayout.Label("Circuitos predefinidos", h2);
            if (world.data.meta.presets != null)
                foreach (var p in world.data.meta.presets)
                    if (GUILayout.Button(p.name + (p.length > 0 ? "  (" + (p.length / 1000f).ToString("F1") + " km)" : ""))) { var pp = p; pending = () => ApplyPreset(pp); }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Aleatorio")) pending = RandomCircuit;
            if (GUILayout.Button("Borrar")) pending = () => { sel.Clear(); Rebuild(); };
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            GUILayout.BeginHorizontal();
            showZones = GUILayout.Toggle(showZones, " Barrios");
            showLandmarks = GUILayout.Toggle(showLandmarks, " Lugares (zoom)");
            GUILayout.EndHorizontal();

            GUI.enabled = true;
            GUILayout.Space(10);
            var go = new GUIStyle(GUI.skin.button) { fontSize = 28, fontStyle = FontStyle.Bold, fixedHeight = 60 };
            var go2 = new GUIStyle(GUI.skin.button) { fontSize = 22, fontStyle = FontStyle.Bold, fixedHeight = 48 };
            if (OnlineRoom) OnlineButtons(go, go2);
            else
            {
            GUI.enabled = circuit != null && circuit.error == null;
            if (GUILayout.Button("¡A CORRER!", go)) { var c = circuit; pending = () => game.StartRace(c, settings); }
            GUI.enabled = true;
            GUILayout.Space(6);
            if (GUILayout.Button("PASEO LIBRE (sin carrera)", go2)) { int start = sel.Count > 0 ? sel[0] : -1; pending = () => game.StartFreeRoam(settings, start); }
            GUILayout.Label(sel.Count > 0 ? "Empiezas junto al cruce 1." : "Empiezas en el centro de Hernani (o elige un cruce para empezar allí).", small);
            }

            GUILayout.Space(10);
            foreach (var a in world.data.meta.attribution) GUILayout.Label(a, small);
            GUILayout.Label("Generado: " + world.data.meta.generated + " · " + world.data.meta.crs, small);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            GUI.matrix = Matrix4x4.identity;
        }
    }
}
