using HernaniRacing.Net;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Paseo libre por Hernani: sin vueltas ni rivales, con tráfico y peatones, nombre de la calle,
    /// aviso de sentido contrario en las calles de sentido único y de semáforos en rojo.
    /// </summary>
    public class FreeRoamManager : MonoBehaviour
    {
        public GameBootstrap game;
        World world;
        RaceSettings settings;
        CarController car;
        ChaseCamera chase;
        TrafficSystem traffic;
        MapView map;
        int mapZoom = 4;      // índice en MapView.Zooms (4 = 200 m)
        bool showMap = true, paused;
        string message; float messageUntil;
        float wrongTimer;
        bool wrongWay;
        int curEdge = -1;
        int approachNode = -1, approachEdge = -1;
        float approachDist;
        bool approachRed, approachStop;
        float stopMinSpeed;
        float distance;
        Vector3 lastPos;
        System.Action pending;
        public bool Active { get; private set; }

        // ---- online
        bool online;
        NetSession net;
        NetWorldSync sync;
        int spawnSlot;

        public void Begin(World w, RaceSettings s, int startNode)
        {
            End();
            online = false; net = null; spawnSlot = 0;
            BeginCommon(w, s, startNode);
        }

        /// <summary>Paseo libre online: todos los de la sala por Hernani a la vez, con tráfico y peatones.</summary>
        public void BeginOnline(World w, RaceSettings s, int startNode, NetSession ns, RoomInfo room)
        {
            End();
            online = true; net = ns;
            var me = room.Find(ns.MyId);
            spawnSlot = me != null ? me.slot : 0;
            BeginCommon(w, s, startNode);
            sync = NetWorldSync.Create(ns, w, traffic, car.transform, true);
            sync.Register(ns.MyId, 0, car, false, ns.nick, s.paint);
            Flash("Paseo libre online · sala " + room.code, 2.5f);
        }

        void BeginCommon(World w, RaceSettings s, int startNode)
        {
            world = w; settings = s;
            Active = true;
            car = CarModels.Create(s.carId, "Coche jugador", s.paint, true);
            car.gameObject.AddComponent<PlayerDriver>().car = car;
            PlaceAtStart(startNode);

            var cam = game.MainCamera;
            cam.orthographic = false;
            cam.fieldOfView = 62f;
            cam.nearClipPlane = 0.2f;
            chase = cam.GetComponent<ChaseCamera>();
            if (chase == null) chase = cam.gameObject.AddComponent<ChaseCamera>();
            chase.enabled = true;
            chase.target = car.transform;
            chase.world = w;
            chase.Snap();
            if (w.trees != null) { w.trees.drawAll = false; w.trees.drawDistance = 900f; }

            traffic = TrafficSystem.Create(w, car.transform, s.freeTraffic, s.freePedestrians);
            traffic.SetDensity(s.FreeCars, s.FreePeds, s.realTimeTraffic);
            traffic.drivers.Add(car);
            if (map == null) map = new MapView(w, null);
            paused = false;
            Time.timeScale = 1f;
            distance = 0f;
            lastPos = car.transform.position;
            Flash("Paseo libre por Hernani", 2.5f);
        }

        public void End()
        {
            if (sync != null) Destroy(sync.gameObject);
            sync = null;
            if (car != null) Destroy(car.gameObject);
            car = null;
            if (traffic != null) Destroy(traffic.gameObject);
            traffic = null;
            if (chase != null) chase.enabled = false;
            Time.timeScale = 1f;
            Active = false;
        }

        /// <summary>En un tramo cercano al nodo elegido (o al centro de Hernani), en un sentido permitido.</summary>
        void PlaceAtStart(int node)
        {
            var g = world.graph;
            Vector3 target = node >= 0 ? g.nodes[node] : CenterOfTown();
            float d;
            int ei = g.NearestEdge(target, 400f, out d);
            if (ei < 0) ei = 0;
            var e = g.edges[ei];
            float s = Mathf.Clamp(e.length * 0.5f, 0, e.length);
            if (node >= 0) s = node == e.a ? Mathf.Min(e.length * 0.5f, Lanes.Trim(g, node) + 8f) : (node == e.b ? Mathf.Max(e.length * 0.5f, e.length - Lanes.Trim(g, node) - 8f) : s);
            if (spawnSlot > 0)
            {
                // online: cada jugador en su hueco (en fila por el tramo; si no cabe, en un tramo vecino)
                float want = s + (node == e.b ? -1f : 1f) * spawnSlot * 9f;
                if (want < 4f || want > e.length - 4f)
                {
                    int alt = -1;
                    int k = 0;
                    foreach (int ej in g.adj[e.a]) if (ej != ei && g.edges[ej].length > 30f) { if (k++ == spawnSlot % 3) { alt = ej; break; } }
                    if (alt < 0) foreach (int ej in g.adj[e.b]) if (ej != ei && g.edges[ej].length > 30f) { alt = ej; break; }
                    if (alt >= 0) { ei = alt; e = g.edges[ei]; want = Mathf.Clamp(e.length * 0.5f + (spawnSlot - 4) * 4f, 6f, e.length - 6f); }
                    else want = Mathf.Clamp(want, 4f, e.length - 4f);
                }
                s = want;
            }
            PlaceOn(ei, s, e.Allows(true));
        }

        Vector3 CenterOfTown()
        {
            return world.TownCenter;
        }

        void PlaceOn(int ei, float s, bool forward)
        {
            var e = world.graph.edges[ei];
            Vector3 dir = e.TangentAt(s) * (forward ? 1f : -1f);
            Vector3 right = new Vector3(dir.z, 0, -dir.x);
            Vector3 p = e.PointAt(s, true) + right * Lanes.Offset(e, 0);
            p.y = Lanes.SurfaceY(world, e, s, p, true) + 0.4f;
            car.Teleport(p, Quaternion.LookRotation(dir, Vector3.up));
        }

        void ResetCar()
        {
            float d;
            var g = world.graph;
            int ei = g.NearestEdge(car.transform.position, 200f, out d);
            if (ei < 0) return;
            var e = g.edges[ei];
            float best = 0, bd = float.MaxValue;
            for (float s = 0; s <= e.length; s += 1f)
            {
                float dd = (e.PointAt(s, true) - car.transform.position).sqrMagnitude;
                if (dd < bd) { bd = dd; best = s; }
            }
            bool fwd = Vector3.Dot(car.transform.forward, e.TangentAt(best)) >= 0;
            if (!e.Allows(fwd)) fwd = !fwd;
            PlaceOn(ei, best, fwd);
            chase.Snap();
            Flash("Recolocado en " + e.name, 1.5f);
        }

        void Flash(string m, float t) { message = m; messageUntil = Time.unscaledTime + t; }

        void Update()
        {
            if (pending != null) { var pa = pending; pending = null; pa(); }
            if (!Active || car == null) return;
            if (GameInput.PausePressed) { paused = !paused; if (!online) Time.timeScale = paused ? 0f : 1f; }
            if (paused && !online) return;
            if (GameInput.MapPressed) showMap = !showMap;
            if (GameInput.MapZoomPressed) mapZoom = (mapZoom + 1) % MapView.Zooms.Length;
            if (GameInput.ResetPressed) ResetCar();
            if (Vector3.Dot(car.transform.up, Vector3.up) < 0.2f && car.Speed < 1f) ResetCar();
            if (car.transform.position.y < -50f) ResetCar();

            Vector3 p = car.transform.position;
            distance += Vector3.Distance(new Vector3(p.x, 0, p.z), new Vector3(lastPos.x, 0, lastPos.z));
            lastPos = p;
            var g = world.graph;
            float d;
            curEdge = g.NearestEdge(p, 14f, out d);

            // sentido contrario en calle de sentido único (fuera de los cruces)
            bool wrong = false;
            Vector3 vel = car.Body.Vel(); vel.y = 0;
            if (curEdge >= 0 && vel.magnitude > 3f)
            {
                var e = g.edges[curEdge];
                bool nearNode = (p - g.nodes[e.a]).sqrMagnitude < Sq(g.NodeRadius(e.a) + 4f) || (p - g.nodes[e.b]).sqrMagnitude < Sq(g.NodeRadius(e.b) + 4f);
                if (e.OneWay && !nearNode)
                {
                    float s = ProjectS(e, p);
                    Vector3 allowed = e.TangentAt(s) * e.ow;
                    wrong = Vector3.Dot(vel.normalized, allowed) < -0.5f;
                }
            }
            wrongTimer = wrong ? wrongTimer + Time.deltaTime : 0f;
            wrongWay = wrongTimer > 0.8f;

            // semáforos en rojo y STOP: ¿te los has saltado?
            if (curEdge >= 0)
            {
                var e = g.edges[curEdge];
                float s = ProjectS(e, p);
                bool towardB = Vector3.Dot(vel, e.TangentAt(s)) > 0;
                int node = towardB ? e.b : e.a;
                float toNode = towardB ? e.length - s : s;
                if (node != approachNode || curEdge != approachEdge)
                {
                    approachNode = node; approachEdge = curEdge; approachRed = false; approachStop = false; stopMinSpeed = 99f;
                }
                approachDist = toNode;
                float stopD = Lanes.StopDist(g, node);
                if (toNode > stopD - 1f && toNode < stopD + 25f)
                {
                    var st = world.signals != null ? world.signals.StateFor(curEdge, node) : TrafficControl.LightState.None;
                    approachRed = st == TrafficControl.LightState.Red;
                    approachStop = g.SignAt(curEdge, node) == 1;
                    if (approachStop && toNode < stopD + 6f) stopMinSpeed = Mathf.Min(stopMinSpeed, vel.magnitude);
                }
                else if (toNode < stopD - 1f && vel.magnitude > 2f)
                {
                    if (approachRed) { Flash("¡Te has saltado un semáforo en rojo!", 2.5f); approachRed = false; }
                    if (approachStop && stopMinSpeed > 1.5f) { Flash("¡No has parado en el STOP!", 2.5f); approachStop = false; }
                }
            }
        }

        static float Sq(float x) { return x * x; }

        static float ProjectS(RoadEdge e, Vector3 p)
        {
            float best = 0, bd = float.MaxValue;
            for (int i = 0; i + 1 < e.pts.Length; i++)
            {
                Vector3 a = e.pts[i], b = e.pts[i + 1];
                Vector2 A = new Vector2(a.x, a.z), B = new Vector2(b.x, b.z), P = new Vector2(p.x, p.z);
                Vector2 AB = B - A;
                float t = AB.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(P - A, AB) / AB.sqrMagnitude) : 0f;
                float dd = (A + AB * t - P).sqrMagnitude;
                if (dd < bd) { bd = dd; best = e.cum[i] + t * (e.cum[i + 1] - e.cum[i]); }
            }
            return best;
        }

        // ------------------------------------------------------------------ HUD
        GUIStyle big, mid, label, small, box, warn;
        void Styles()
        {
            if (big != null) return;
            big = new GUIStyle(GUI.skin.label) { fontSize = 64, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            big.normal.textColor = Color.white;
            mid = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            mid.normal.textColor = Color.white;
            label = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
            label.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
            small = new GUIStyle(GUI.skin.label) { fontSize = 15 };
            small.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            box = new GUIStyle(GUI.skin.box);
            box.normal.background = Tex.Solid(new Color(0.05f, 0.06f, 0.08f, 0.75f));
            warn = new GUIStyle(GUI.skin.label) { fontSize = 44, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            warn.normal.textColor = new Color(1f, 0.3f, 0.25f);
        }

        void OnGUI()
        {
            if (!Active || car == null) return;
            Styles();
            float ui = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(ui, ui, 1));
            float W = Screen.width / ui, H = 1080f;
            var g = world.graph;
            Vector3 p = car.transform.position;

            // calle y barrio
            string street = curEdge >= 0 ? g.edges[curEdge].name : "Fuera de la red viaria";
            string extra = "";
            if (curEdge >= 0)
            {
                var e = g.edges[curEdge];
                if (e.roundabout) extra = "Rotonda";
                else if (e.OneWay) extra = "Sentido único";
                else extra = "Doble sentido";
                if (e.lanes > 1) extra += " · " + e.lanes + " carriles";
            }
            var zone = world.ZoneAt(p);
            float gw = 600;
            GUI.Box(new Rect(W / 2 - gw / 2, 20, gw, 96), GUIContent.none, box);
            GUI.Label(new Rect(W / 2 - gw / 2, 24, gw, 36), street, mid);
            GUI.Label(new Rect(W / 2 - gw / 2, 58, gw, 26), (zone != null ? zone.name : "Hernani") + " · Hernani (Gipuzkoa)", label);
            var utm = world.data.ToUTM(p);
            var sm = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(W / 2 - gw / 2, 86, gw, 24), extra + " · Altitud " + p.y.ToString("F0") + " m · UTM " + utm.x.ToString("F0") + " / " + utm.y.ToString("F0"), sm);

            // velocímetro de aguja + digital
            float kmh = Mathf.Abs(car.ForwardSpeed) * 3.6f;
            Speedometer.Draw(new Rect(W - 300, H - 300, 280, 280), kmh, car.maxSpeed * 3.6f, car.Reversing && car.ForwardSpeed < -0.5f);
            float lim = curEdge >= 0 ? (g.edges[curEdge].width >= 10f ? 120f : (g.edges[curEdge].type == "Carretera" ? 50f : 30f)) : 0f;
            if (lim > 0)
            {
                GUI.Box(new Rect(W - 300, H - 340, 280, 34), GUIContent.none, box);
                GUI.Label(new Rect(W - 300, H - 340, 280, 34), "Límite orientativo " + lim.ToString("0") + (kmh > lim + 5 ? "  ⚠" : ""), new GUIStyle(small) { alignment = TextAnchor.MiddleCenter });
            }
            GUI.Box(new Rect(20, 20, 300, 88), GUIContent.none, box);
            var ll = new GUIStyle(small) { fontSize = 18 };
            GUI.Label(new Rect(34, 28, 280, 26), "PASEO LIBRE", new GUIStyle(ll) { fontStyle = FontStyle.Bold });
            GUI.Label(new Rect(34, 54, 280, 24), "Recorrido: " + (distance / 1000f).ToString("F2") + " km", ll);
            if (traffic != null)
                GUI.Label(new Rect(34, 78, 280, 24), "Coches " + traffic.cars.Count + " · Peatones " + traffic.peds.Count, ll);

            // plano
            if (showMap && map != null)
            {
                float ms = 300;
                Rect mr = new Rect(W - ms - 20, 20, ms, ms);
                GUI.Box(new Rect(mr.x - 3, mr.y - 3, ms + 6, ms + 6), GUIContent.none, box);
                float span = MapView.Zooms[Mathf.Clamp(mapZoom, 0, MapView.Zooms.Length - 1)];
                map.Draw(mr, new Vector2(p.x, p.z), span);
                if (traffic != null)
                {
                    float ts = MapView.MarkerSize(span, 9, 3);
                    foreach (var tc in traffic.cars) if (tc != null) map.Marker(tc.transform.position, tc.transform.forward, new Color(0.85f, 0.85f, 0.9f), false, ts);
                }
                if (world.signals != null && span <= 1000f)
                    foreach (var j in world.signals.Junctions) map.Marker(g.nodes[j.node], Vector3.forward, new Color(1f, 0.6f, 0.1f), false, MapView.MarkerSize(span, 8, 4));
                if (sync != null)
                    foreach (var nc in sync.cars.Values)
                        if (nc.Remote && !nc.gone && nc.car != null) map.Marker(nc.car.transform.position, nc.car.transform.forward, nc.color, false, MapView.MarkerSize(span, 15, 10));
                map.Marker(p, car.transform.forward, settings.paint, true, MapView.MarkerSize(span, 26, 16));
                GUI.Label(new Rect(mr.x, mr.yMax + 4, ms, 22), "Zoom " + MapView.ZoomLabel(span) + " · N zoom · M ocultar", small);
            }

            if (wrongWay)
            {
                GUI.Label(new Rect(0, H / 2 - 70, W, 60), "¡SENTIDO CONTRARIO!", warn);
                var hint = new GUIStyle(label) { fontSize = 22 };
                GUI.Label(new Rect(0, H / 2 - 14, W, 34), "Calle de sentido único", hint);
            }
            else if (Time.unscaledTime < messageUntil && !string.IsNullOrEmpty(message))
            {
                var ms2 = new GUIStyle(warn) { fontSize = 40 };
                ms2.normal.textColor = message.StartsWith("¡") ? new Color(1f, 0.45f, 0.3f) : Color.white;
                GUI.Label(new Rect(0, H / 2 - 200, W, 80), message, ms2);
            }
            if (chase != null && chase.RearView)
            {
                GUI.Box(new Rect(W / 2 - 140, H - 120, 280, 40), GUIContent.none, box);
                GUI.Label(new Rect(W / 2 - 140, H - 120, 280, 40), "VISTA TRASERA", mid);
            }
            GUI.Label(new Rect(20, H - 34, 1400, 26), "W/S acelerar/frenar · A/D girar · Espacio freno de mano · R recolocar · C cámara · B mirar atrás · M mapa · N zoom mapa · O suelo · Esc menú", small);

            if (car != null) PenaltyHud.Draw(W, H, car);
            if (paused)
            {
                Rect r = new Rect(W / 2 - 200, H / 2 - 120, 400, online ? 290 : 240);
                GUI.Box(r, GUIContent.none, box);
                GUI.Label(new Rect(r.x, r.y + 20, r.width, 50), "PAUSA", new GUIStyle(mid) { fontSize = 36 });
                if (online)
                {
                    if (RaceManager.Button(new Rect(r.x + 60, r.y + 90, 280, 50), "Continuar")) paused = false;
                    if (RaceManager.Button(new Rect(r.x + 60, r.y + 150, 280, 50), "Salir a la sala")) { paused = false; pending = net.LeaveGame; }
                    if (net.IsLeader && RaceManager.Button(new Rect(r.x + 60, r.y + 210, 280, 50), "Terminar para todos")) { paused = false; pending = net.Core.EndGame; }
                }
                else
                {
                if (RaceManager.Button(new Rect(r.x + 60, r.y + 90, 280, 50), "Continuar")) { paused = false; Time.timeScale = 1f; }
                if (RaceManager.Button(new Rect(r.x + 60, r.y + 150, 280, 50), "Volver al menú")) { paused = false; pending = game.ShowSelector; }
                }
            }
            GUI.matrix = Matrix4x4.identity;
        }
    }
}
