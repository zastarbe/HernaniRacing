using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Pantallas de mapa (vista cenital de Hernani):
    ///  · Circuito: circuitos predefinidos o 4 cruces reales elegidos con el ratón (1→2→3→4→1 por calles reales),
    ///    vueltas, rivales y tráfico/peatones de la carrera.
    ///  · Paseo libre: punto de salida (un cruce o un barrio) y tráfico/peatones del paseo.
    /// Lo elegido se guarda en <see cref="MenuState"/>, que comparten todas las pantallas.
    /// </summary>
    public class CircuitSelector : MonoBehaviour
    {
        public enum MapMode { Circuit, Free }

        public GameBootstrap game;
        public MainMenu menu;
        World world;
        MenuState state;
        MapMode mode;
        LineRenderer routeLine;
        bool open;
        int hover = -1;
        Vector3 camTarget;
        float orthoSize = 900f;
        bool showZones = true, showLandmarks = true;
        Vector2 dragStart; Vector3 dragCam; bool dragging;
        Rect panelScreenRect;
        Vector2 scroll;
        int shownVersion = -1;
        System.Action pending;   // las acciones se aplican fuera de OnGUI (evita errores de layout)

        public bool IsOpen { get { return open; } }
        public MapMode Mode { get { return mode; } }
        bool Locked { get { return menu != null && menu.RaceLocked; } }

        static readonly Color[] SelColors =
        {
            new Color(0.1f, 0.85f, 0.2f), new Color(0.15f, 0.55f, 1f), new Color(1f, 0.6f, 0.1f), new Color(0.85f, 0.2f, 0.9f)
        };

        public void Open(MapMode m)
        {
            world = game.world;
            state = menu.state;
            mode = m;
            open = true;
            enabled = true;
            scroll = Vector2.zero;
            var cam = game.MainCamera;
            var cc = cam.GetComponent<ChaseCamera>();
            if (cc != null) cc.enabled = false;
            cam.orthographic = true;
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 4000f;
            RenderSettings.fog = false;
            if (world.trees != null) world.trees.drawAll = true;
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
            routeLine.enabled = mode == MapMode.Circuit;
            UpdateRouteLine();
            if (mode == MapMode.Circuit && state.CircuitOk) FocusCircuit();
            else if (mode == MapMode.Free) FocusPoint(state.FreeStartPos, 650f);
            else if (camTarget == Vector3.zero) camTarget = world.TownCenter;
            ApplyCamera();
        }

        public void Close()
        {
            if (!open) return;
            open = false;
            dragging = false;
            if (routeLine != null) routeLine.enabled = false;
            if (world != null && world.trees != null) world.trees.drawAll = false;
        }

        void Update()
        {
            if (!open || world == null) return;
            if (pending != null) { var p = pending; pending = null; p(); }
            if (shownVersion != state.version) UpdateRouteLine();
            var cam = game.MainCamera;
            float k = (GameInput.Fast ? 2.5f : 1f) * orthoSize * 1.2f * Time.unscaledDeltaTime;
            bool typing = GUIUtility.keyboardControl != 0;
            Vector2 pan = typing ? Vector2.zero : GameInput.Pan;
            camTarget += new Vector3(pan.x, 0, pan.y) * k;
            orthoSize = Mathf.Clamp(orthoSize * (1f - (typing ? 0f : GameInput.Zoom) * Time.unscaledDeltaTime * 1.5f), 80f, MaxOrtho());
            ApplyCamera();
            if (routeLine != null) routeLine.widthMultiplier = Mathf.Max(2.5f, orthoSize * 0.009f);
        }

        void ApplyCamera()
        {
            var cam = game.MainCamera;
            camTarget.x = Mathf.Clamp(camTarget.x, 0, world.sizeX);
            camTarget.z = Mathf.Clamp(camTarget.z, 0, world.sizeZ);
            cam.orthographic = true;
            cam.orthographicSize = orthoSize;
            cam.transform.position = new Vector3(camTarget.x, 1500f, camTarget.z);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
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
            if (mode == MapMode.Free) { state.freeStart = node; return; }
            state.presetName = "";
            var sel = state.sel;
            int i = sel.IndexOf(node);
            if (i >= 0) sel.RemoveAt(i);
            else if (sel.Count < 4) sel.Add(node);
            else sel[3] = node;
            state.Rebuild();
        }

        void UpdateRouteLine()
        {
            shownVersion = state.version;
            if (routeLine == null || world == null) return;
            var pts = new List<Vector3>();
            var sel = state.sel;
            if (state.circuit != null && state.circuit.raw != null)
            {
                foreach (var p in state.circuit.raw) pts.Add(p + Vector3.up * 3f);
                pts.Add(state.circuit.raw[0] + Vector3.up * 3f);
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

        void ApplyPreset(PresetData p)
        {
            if (Locked) return;
            state.ApplyPreset(p);
            if (state.CircuitOk) FocusCircuit();
        }

        void RandomCircuit()
        {
            if (Locked) return;
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
            List<int> bestSel = null; float bestScore = float.MaxValue;
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
                if (score < bestScore) { bestScore = score; bestSel = pick; }
            }
            if (bestSel != null)
            {
                state.SetNodes(bestSel, "Aleatorio");
                if (state.CircuitOk) FocusCircuit();
            }
        }

        void FocusCircuit()
        {
            Vector3 mn = Vector3.one * float.MaxValue, mx = Vector3.one * float.MinValue;
            foreach (var p in state.circuit.raw) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            camTarget = (mn + mx) * 0.5f;
            float aspect = Mathf.Max(0.5f, game.MainCamera.aspect);
            orthoSize = Mathf.Clamp(Mathf.Max(mx.z - mn.z, (mx.x - mn.x) / aspect) * 0.75f + 150f, 200f, 1700f);
            camTarget.x -= PanelShift();
        }

        void FocusPoint(Vector3 p, float size)
        {
            orthoSize = size;
            camTarget = p;
            camTarget.x -= PanelShift();
        }

        /// <summary>Desplaza el centro de la vista para que lo importante no quede tapado por el panel de la izquierda.</summary>
        float PanelShift()
        {
            float ui = Screen.height / 1080f;
            float panelPx = 520f * ui;
            float worldPerPx = orthoSize * 2f / Mathf.Max(1, Screen.height);
            return panelPx * 0.5f * worldPerPx;
        }

        // ------------------------------------------------------------------ GUI
        GUIStyle title, h2, txt, small, tagSt, tagBold, box, tip, bigBtn, midBtn, onBtn;

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
            box.normal.background = Tex.Solid(new Color(0.05f, 0.06f, 0.08f, 0.85f));
            tip = new GUIStyle(GUI.skin.box) { fontSize = 15, alignment = TextAnchor.MiddleLeft, wordWrap = false };
            tip.normal.background = Tex.Solid(new Color(0, 0, 0, 0.8f));
            tip.normal.textColor = Color.white;
            bigBtn = new GUIStyle(GUI.skin.button) { fontSize = 28, fontStyle = FontStyle.Bold, fixedHeight = 62 };
            midBtn = new GUIStyle(GUI.skin.button) { fontSize = 19, fontStyle = FontStyle.Bold, fixedHeight = 44 };
            onBtn = new GUIStyle(GUI.skin.button) { fontSize = 16, fontStyle = FontStyle.Bold };
            onBtn.normal.textColor = onBtn.hover.textColor = new Color(1f, 0.85f, 0.25f);
        }

        void OnGUI()
        {
            if (!open || world == null) return;
            Styles();
            var cam = game.MainCamera;
            float ui = Screen.height / 1080f;
            Event ev = Event.current;
            Vector2 mouse = ev.mousePosition;
            bool overPanel = panelScreenRect.Contains(mouse);
            var g = world.graph;

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
            float dot = Mathf.Clamp(900f / orthoSize * 4f, 4f, 11f);
            var yellow = Tex.Solid(new Color(1f, 0.85f, 0.1f, 0.95f));
            var dark = Tex.Solid(new Color(0, 0, 0, 0.85f));
            bool canPick = !Locked;
            foreach (int j in g.junctions)
            {
                Vector3 sp = cam.WorldToScreenPoint(g.nodes[j]);
                if (sp.x < -20 || sp.x > Screen.width + 20 || sp.y < -20 || sp.y > Screen.height + 20) continue;
                Vector2 gp = new Vector2(sp.x, Screen.height - sp.y);
                GUI.DrawTexture(new Rect(gp.x - dot / 2 - 1, gp.y - dot / 2 - 1, dot + 2, dot + 2), dark);
                GUI.DrawTexture(new Rect(gp.x - dot / 2, gp.y - dot / 2, dot, dot), yellow);
                float d = Vector2.Distance(gp, mouse);
                if (d < bestD && !overPanel && canPick) { bestD = d; hover = j; }
            }
            if (mode == MapMode.Circuit)
            {
                var sel = state.sel;
                for (int i = 0; i < sel.Count; i++)
                {
                    Vector3 sp = cam.WorldToScreenPoint(g.nodes[sel[i]]);
                    Vector2 gp = new Vector2(sp.x, Screen.height - sp.y);
                    GUI.DrawTexture(new Rect(gp.x - 17, gp.y - 17, 34, 34), Tex.Solid(Color.black));
                    GUI.DrawTexture(new Rect(gp.x - 15, gp.y - 15, 30, 30), Tex.Solid(SelColors[i]));
                    var ns = new GUIStyle(tagBold) { fontSize = 20 }; ns.normal.textColor = Color.black;
                    GUI.Label(new Rect(gp.x - 15, gp.y - 15, 30, 30), (i + 1).ToString(), ns);
                }
            }
            else
            {
                // salida del paseo
                Vector3 sp = cam.WorldToScreenPoint(state.FreeStartPos);
                Vector2 gp = new Vector2(sp.x, Screen.height - sp.y);
                GUI.DrawTexture(new Rect(gp.x - 19, gp.y - 19, 38, 38), Tex.Solid(Color.black));
                GUI.DrawTexture(new Rect(gp.x - 17, gp.y - 17, 34, 34), Tex.Solid(new Color(0.1f, 0.85f, 0.2f)));
                var ns = new GUIStyle(tagBold) { fontSize = 22 }; ns.normal.textColor = Color.black;
                GUI.Label(new Rect(gp.x - 17, gp.y - 17, 34, 34), "S", ns);
                var lt = new GUIStyle(tip) { fontStyle = FontStyle.Bold };
                string label = " SALIDA ";
                var ls = lt.CalcSize(new GUIContent(label));
                GUI.Box(new Rect(gp.x - ls.x / 2, gp.y - 52, ls.x, 28), label, lt);
            }
            if (hover >= 0)
            {
                Vector3 sp = cam.WorldToScreenPoint(g.nodes[hover]);
                Vector2 gp = new Vector2(sp.x, Screen.height - sp.y);
                GUI.DrawTexture(new Rect(gp.x - 9, gp.y - 9, 18, 18), Tex.Solid(Color.white));
                var zone = world.ZoneAt(g.nodes[hover]);
                string t = " " + g.JunctionLabel(hover) + "  ·  " + (zone != null ? zone.name : "") + "  ·  " + g.nodes[hover].y.ToString("F0") + " m " +
                           (mode == MapMode.Free ? " · clic = salir de aquí " : "");
                var size = tip.CalcSize(new GUIContent(t));
                GUI.Box(new Rect(mouse.x + 16, mouse.y + 12, size.x, 30), t, tip);
            }

            // ---- ratón
            if (ev.type == EventType.ScrollWheel && !overPanel)
            {
                float f = ev.delta.y > 0 ? 1.12f : 1f / 1.12f;
                // zoom hacia el cursor
                Vector3 before = cam.ScreenToWorldPoint(new Vector3(mouse.x, Screen.height - mouse.y, 0));
                orthoSize = Mathf.Clamp(orthoSize * f, 80f, MaxOrtho());
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
            if (mode == MapMode.Circuit && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Backspace && state.sel.Count > 0 && !Locked && GUIUtility.keyboardControl == 0)
            {
                pending = () => { if (state.sel.Count > 0) { state.sel.RemoveAt(state.sel.Count - 1); state.presetName = ""; state.Rebuild(); } };
                ev.Use();
            }

            // ---- panel
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(ui, ui, 1));
            Rect panel = new Rect(16, 16, 480, 1080 - 32);
            panelScreenRect = new Rect(panel.x * ui, panel.y * ui, panel.width * ui, panel.height * ui);
            GUI.Box(panel, GUIContent.none, box);
            GUILayout.BeginArea(new Rect(panel.x + 18, panel.y + 12, panel.width - 36, panel.height - 24));
            scroll = GUILayout.BeginScrollView(scroll);
            if (mode == MapMode.Circuit) CircuitPanel(); else FreePanel();
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            GUI.matrix = Matrix4x4.identity;
        }

        // ------------------------------------------------------------------ paneles
        void CircuitPanel()
        {
            var g = world.graph;
            var sel = state.sel;
            var s = state.settings;
            bool lockUI = Locked;
            GUILayout.Label("CIRCUITO Y CARRERA", title);
            if (lockUI) GUILayout.Label("Sala online: el circuito lo elige el líder.", txt);
            GUI.enabled = !lockUI;

            GUILayout.Label("Circuitos predefinidos", h2);
            foreach (var p in state.Presets)
            {
                bool on = p.name == state.presetName;
                if (GUILayout.Button((on ? "► " : "") + p.name + (p.length > 0 ? "  (" + (p.length / 1000f).ToString("F1") + " km)" : ""), on ? onBtn : GUI.skin.button, GUILayout.Height(30)))
                { var pp = p; pending = () => ApplyPreset(pp); }
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Aleatorio (aquí)", GUILayout.Height(30))) pending = RandomCircuit;
            if (GUILayout.Button("Borrar cruces", GUILayout.Height(30))) pending = () => { state.SetNodes(new int[0], ""); };
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("O haz tu circuito", h2);
            GUILayout.Label("Haz clic en 4 cruces (puntos amarillos) en el orden de carrera: se unen 1 → 2 → 3 → 4 → 1 por calles reales.", txt);
            GUILayout.Label("Ratón: arrastrar = mover, rueda = zoom · WASD/flechas, Q/E · Retroceso = quitar el último", small);
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
                    if (GUILayout.Button("✕", GUILayout.Width(30))) { int ri = i; pending = () => { if (ri < state.sel.Count) { state.sel.RemoveAt(ri); state.presetName = ""; state.Rebuild(); } }; }
                }
                else GUILayout.Label((i + 1) + ". —", txt);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(4);
            var circuit = state.circuit;
            if (state.CircuitOk)
            {
                var streets = new HashSet<string>();
                foreach (var st in circuit.steps) streets.Add(g.edges[st.edge].name);
                GUILayout.Label(state.CircuitName, h2);
                GUILayout.Label("Vuelta: " + (circuit.length / 1000f).ToString("F2") + " km   ·   Total: " + (circuit.length * s.laps / 1000f).ToString("F1") + " km", txt);
                GUILayout.Label("Desnivel: +" + circuit.elevationGain.ToString("F0") + " m   ·   Pendiente máx.: " + circuit.maxGradePct.ToString("F0") + " %", txt);
                GUILayout.Label("Cotas: " + circuit.minElev.ToString("F0") + " – " + circuit.maxElev.ToString("F0") + " m   ·   " + streets.Count + " calles distintas", txt);
                if (circuit.reusedLength > 1f)
                    GUILayout.Label("⚠ Se repiten " + circuit.reusedLength.ToString("F0") + " m de calle (ida y vuelta). Prueba otro orden de cruces.", txt);
                if (circuit.hasUTurn) GUILayout.Label("⚠ Incluye un giro de 180º.", txt);
                GUILayout.Label("Calles: " + string.Join(", ", streets.Take(10).ToArray()) + (streets.Count > 10 ? "…" : ""), small);
            }
            else if (!string.IsNullOrEmpty(state.info)) GUILayout.Label("⚠ " + state.info, txt);
            else GUILayout.Label(sel.Count < 4 ? "Faltan " + (4 - sel.Count) + " cruces." : "", txt);

            GUILayout.Space(8);
            GUILayout.Label("Carrera", h2);
            menu.RaceControls(txt, lockUI);

            GUILayout.Space(6);
            GUILayout.Label("Tráfico y peatones en la carrera", h2);
            s.traffic = YesNo("Tráfico", s.traffic);
            s.pedestrians = YesNo("Peatones", s.pedestrians);
            GUILayout.Label("Los coches respetan sentidos, semáforos, STOP, ceda el paso, rotondas y pasos de cebra (OpenStreetMap).", small);

            GUI.enabled = true;
            GUILayout.BeginHorizontal();
            showZones = GUILayout.Toggle(showZones, " Barrios");
            showLandmarks = GUILayout.Toggle(showLandmarks, " Lugares (zoom)");
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            bool online = menu.InRoom;
            if (!online)
            {
                GUI.enabled = state.CircuitOk;
                if (GUILayout.Button("¡A CORRER!", bigBtn)) pending = menu.StartRace;
                GUI.enabled = true;
                GUILayout.Space(4);
            }
            if (GUILayout.Button(online ? "◄ VOLVER A LA SALA" : "◄ MENÚ PRINCIPAL", midBtn)) pending = menu.Back;
            GUILayout.Space(10);
            foreach (var a in world.data.meta.attribution) GUILayout.Label(a, small);
        }

        void FreePanel()
        {
            var s = state.settings;
            bool online = menu.InRoom;
            bool lockUI = Locked;
            GUILayout.Label(online ? "PASEO LIBRE ONLINE" : "PASEO LIBRE", title);
            GUILayout.Label("Conduce por todo Hernani sin carrera, con tráfico y peatones que respetan las señales.", txt);
            GUI.enabled = !lockUI;

            GUILayout.Space(6);
            GUILayout.Label("Punto de salida", h2);
            GUILayout.Label("Haz clic en un cruce (punto amarillo) del mapa o elige un barrio:", txt);
            var lst = new GUIStyle(txt) { fontStyle = FontStyle.Bold };
            lst.normal.textColor = new Color(0.45f, 1f, 0.5f);
            GUILayout.Label("Salida: " + state.FreeStartLabel, lst);
            if (GUILayout.Button((state.freeStart < 0 ? "► " : "") + "Centro de Hernani", state.freeStart < 0 ? onBtn : GUI.skin.button, GUILayout.Height(30)))
                pending = () => { state.freeStart = -1; FocusPoint(state.FreeStartPos, orthoSize); };
            var zones = world.data.scenery.zones.Where(z => z.primary).OrderBy(z => z.name).ToList();
            for (int i = 0; i < zones.Count; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int k = i; k < Mathf.Min(i + 2, zones.Count); k++)
                {
                    var z = zones[k];
                    if (GUILayout.Button(z.name, GUILayout.Height(28)))
                        pending = () =>
                        {
                            int n = world.graph.NearestJunction(new Vector3(z.x, 0, z.z), 600f);
                            if (n >= 0) { state.freeStart = n; FocusPoint(state.FreeStartPos, Mathf.Min(orthoSize, 700f)); }
                        };
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(10);
            GUILayout.Label("Tráfico y peatones", h2);
            s.freeTraffic = YesNo("Tráfico", s.freeTraffic);
            s.freePedestrians = YesNo("Peatones", s.freePedestrians);
            GUILayout.Label("Ratón: arrastrar = mover, rueda = zoom · WASD/flechas, Q/E", small);
            GUI.enabled = true;
            GUILayout.BeginHorizontal();
            showZones = GUILayout.Toggle(showZones, " Barrios");
            showLandmarks = GUILayout.Toggle(showLandmarks, " Lugares (zoom)");
            GUILayout.EndHorizontal();

            GUILayout.Space(12);
            if (!online)
            {
                if (GUILayout.Button("¡A PASEAR!", bigBtn)) pending = menu.StartFree;
            }
            else if (menu.IsLeader)
            {
                if (GUILayout.Button("¡EMPEZAR PASEO ONLINE!", bigBtn)) pending = menu.StartOnlineFree;
                GUILayout.Label("Todos los de la sala empiezan juntos en esta salida.", small);
            }
            GUILayout.Space(4);
            if (GUILayout.Button(online ? "◄ VOLVER A LA SALA" : "◄ MENÚ PRINCIPAL", midBtn)) pending = menu.Back;
        }

        bool YesNo(string label, bool value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, txt, GUILayout.Width(130));
            if (GUILayout.Button(value ? "► SÍ" : "SÍ", value ? onBtn : GUI.skin.button, GUILayout.Width(110), GUILayout.Height(30))) value = true;
            if (GUILayout.Button(!value ? "► NO" : "NO", !value ? onBtn : GUI.skin.button, GUILayout.Width(110), GUILayout.Height(30))) value = false;
            GUILayout.EndHorizontal();
            return value;
        }
    }
}
