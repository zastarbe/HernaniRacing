using System.Collections.Generic;
using System.Linq;
using HernaniRacing.Net;
using UnityEngine;

namespace HernaniRacing
{
    public class RaceSettings
    {
        public int laps = 3;
        public int opponents = 3;
        public int difficulty = 1;   // 0 fácil, 1 normal, 2 difícil
        public string carId = "turismo";                 // modelo (Resources/HernaniCars); los rivales llevan el mismo
        public Color paint = new Color(0.85f, 0.08f, 0.1f);
        public bool traffic = false, pedestrians = true;          // carrera
        public bool freeTraffic = true, freePedestrians = true;   // paseo libre
        // cantidad (0 = no ... 5 = a tope, ver TrafficSystem.LevelNames); se guarda en este PC, no viaja por la red
        public int trafficLevel = 0, pedLevel = 2, freeTrafficLevel = 2, freePedLevel = 2;
        public bool realTimeTraffic = false;                      // ajustar la cantidad de coches a la hora real
        public int RaceCars { get { return traffic ? Mathf.Max(1, trafficLevel) : 0; } }
        public int RacePeds { get { return pedestrians ? Mathf.Max(1, pedLevel) : 0; } }
        public int FreeCars { get { return freeTraffic ? Mathf.Max(1, freeTrafficLevel) : 0; } }
        public int FreePeds { get { return freePedestrians ? Mathf.Max(1, freePedLevel) : 0; } }
        public static readonly string[] DifficultyNames = { "Fácil", "Normal", "Difícil" };
    }

    public class Racer
    {
        public string name;
        public bool isPlayer;
        public Color color;
        public CarController car;
        public AIDriver ai;
        public int idx = -1;
        public float lastS, progress;
        public int lapsDone;
        public float lapStart;
        public List<float> lapTimes = new List<float>();
        public float bestLap = -1;
        public bool finished;
        public float finishTime;
        public int finishOrder;
        public bool estimated;       // no llegó a tiempo: tiempo estimado por su ritmo
        public bool ghosted;         // ya no choca con los demás coches de la carrera (tras la meta)
        public float offTimer, wrongTimer, stuckTimer, flipTimer;
        public bool offTrack, wrongWay;
        public int position;
        public float distToLine;
        public float nextRejoinCheck;
        // online
        public NetCar net;
        public bool remote;          // lo simula otro PC (otro jugador, o la IA si no soy el líder)
        public bool gone;            // se ha desconectado
        public int aiIndex = -1;
    }

    /// <summary>Gestión de la carrera: parrilla, cuenta atrás, vueltas, posiciones, HUD y resultados.</summary>
    public class RaceManager : MonoBehaviour
    {
        public GameBootstrap game;
        World world;
        Circuit circuit;
        RaceSettings settings;
        GameObject circuitRoot;
        readonly List<Racer> racers = new List<Racer>();
        Racer player;
        ChaseCamera chase;

        enum State { Countdown, Racing, Finished }
        State state;
        bool paused;
        float countdown, raceStart, raceTime, finishedAt;
        int finishCounter;
        bool showMinimap = true;
        string message; float messageUntil;
        System.Action pending;

        MapView map;
        Vector2 mapCenter; float mapSpan;
        int mapZoom;          // 0 = circuito entero; 1.. = MapView.Zooms[mapZoom - 1] alrededor del coche
        TrafficSystem traffic;
        GuideArrows arrows;

        static readonly string[] AINames = { "Ane", "Iker", "Maialen", "Unai", "Leire", "Jon", "Nahia", "Aitor" };
        static readonly Color[] AIColors =
        {
            new Color(0.12f, 0.35f, 0.85f), new Color(0.1f, 0.65f, 0.25f), new Color(0.95f, 0.55f, 0.05f),
            new Color(0.55f, 0.2f, 0.75f), new Color(0.92f, 0.92f, 0.92f), new Color(0.1f, 0.75f, 0.8f),
            new Color(0.95f, 0.4f, 0.65f), new Color(0.15f, 0.15f, 0.17f)
        };
        public static readonly Color PlayerColor = new Color(0.85f, 0.08f, 0.1f);

        public bool Active { get; private set; }

        // ---- online
        bool online;
        NetSession net;
        NetWorldSync sync;
        double netStartAt;
        string aiCarId;
        /// <summary>Reloj de la carrera: el del servidor si es online (igual para todos), si no el del juego.</summary>
        float Clock { get { return online && net != null ? (float)(net.Core.ServerTime - netStartAt) : Time.time; } }

        public void Begin(World w, Circuit c, RaceSettings s)
        {
            End();
            online = false; net = null;
            aiCarId = s.carId;
            BeginCommon(w, c, s, null);
        }

        /// <summary>Carrera online: los jugadores de la sala (y la IA del líder) en la misma parrilla.</summary>
        public void BeginOnline(World w, Circuit c, RaceSettings s, NetSession ns, RoomInfo room)
        {
            End();
            online = true; net = ns;
            netStartAt = room.startAt;
            aiCarId = string.IsNullOrEmpty(room.settings.aiCar) ? s.carId : room.settings.aiCar;
            BeginCommon(w, c, s, room);
        }

        void BeginCommon(World w, Circuit c, RaceSettings s, RoomInfo room)
        {
            world = w; circuit = c; settings = s;
            Active = true;
            // perfil de velocidad de la IA según el coche de la IA (offline: el mismo que el jugador)
            var md = CarModels.Get(aiCarId);
            float mu = md != null ? Mathf.Clamp(1.05f * md.grip / 1.25f, 0.9f, 1.9f) : 1.05f;
            float dec = md != null ? Mathf.Clamp(7.5f * (md.brake / Mathf.Max(md.mass, 1f)) / 9.6f, 6f, 12f) : 7.5f;
            float vmax = md != null ? md.maxSpeed * 0.95f : 42f;
            circuit.ComputeSpeedProfile(mu, dec, vmax);
            circuitRoot = CircuitVisuals.Build(circuit);
            BuildMinimap();

            if (room != null) BuildOnlineRacers(room);
            else
            {
            int total = s.opponents + 1;
            for (int k = 0; k < total; k++)
            {
                bool isPlayer = k == total - 1;
                var r = new Racer
                {
                    isPlayer = isPlayer,
                    name = isPlayer ? "Tú" : AINames[k % AINames.Length],
                    color = isPlayer ? s.paint : AIColors[k % AIColors.Length]
                };
                r.car = CarModels.Create(s.carId, isPlayer ? "Coche jugador" : "Coche " + r.name, r.color, isPlayer);
                if (isPlayer)
                {
                    r.car.gameObject.AddComponent<PlayerDriver>().car = r.car;
                    player = r;
                }
                else SetupAI(r, k);
                racers.Add(r);
            }
            }
            var cars = racers.Select(x => x.car).ToList();
            foreach (var r in racers) if (r.ai != null) r.ai.others = cars;

            PlaceOnGrid();

            var cam = game.MainCamera;
            cam.orthographic = false;
            cam.fieldOfView = 62f;
            cam.nearClipPlane = 0.2f;
            chase = cam.GetComponent<ChaseCamera>();
            if (chase == null) chase = cam.gameObject.AddComponent<ChaseCamera>();
            chase.enabled = true;
            chase.target = player.car.transform;
            chase.world = world;
            chase.Snap();

            if (s.traffic || s.pedestrians)
            {
                traffic = TrafficSystem.Create(world, player.car.transform, s.traffic, s.pedestrians);
                traffic.online = online;
                traffic.SetDensity(s.RaceCars, s.RacePeds, s.realTimeTraffic);
                foreach (var r in racers) traffic.drivers.Add(r.car);
            }
            if (online)
            {
                sync = NetWorldSync.Create(net, world, traffic, player.car.transform, false);
                foreach (var r in racers)
                {
                    byte owner = r.aiIndex >= 0 ? NetProto.AiOwner : (r.isPlayer ? net.MyId : r.net.owner);
                    byte local = (byte)(r.aiIndex >= 0 ? r.aiIndex : 0);
                    r.net = sync.Register(owner, local, r.car, r.remote, r.name, r.color);
                }
                net.BecameLeader += AdoptAI;
            }
            arrows = GuideArrows.Create(circuit);
            mapZoom = 0;
            if (world.trees != null) { world.trees.drawAll = false; world.trees.drawDistance = 900f; }

            state = State.Countdown;
            countdown = online ? (float)(netStartAt - net.Core.ServerTime) : 3.99f;
            paused = false;
            Time.timeScale = 1f;
            finishCounter = 0;
            foreach (var r in racers) r.car.frozen = true;
        }

        static readonly float[] Skills = { 0.80f, 0.90f, 0.99f };

        void SetupAI(Racer r, int k)
        {
            r.ai = r.car.gameObject.AddComponent<AIDriver>();
            r.ai.car = r.car;
            r.ai.circuit = circuit;
            r.ai.skill = Skills[Mathf.Clamp(settings.difficulty, 0, 2)] + (k % 3 - 1) * 0.025f;
            r.ai.lane = (k % 2 == 0 ? -1 : 1) * 0.8f;
        }

        /// <summary>Parrilla online: delante la IA y detrás los jugadores en el orden de la sala.</summary>
        void BuildOnlineRacers(RoomInfo room)
        {
            bool leader = room.leaderId == net.MyId;
            int nAi = settings.opponents;
            for (int k = 0; k < nAi; k++)
            {
                var r = new Racer { name = AINames[k % AINames.Length] + " (IA)", color = AIColors[k % AIColors.Length], aiIndex = k };
                r.car = CarModels.Create(aiCarId, "Coche " + r.name, r.color, false);
                if (leader) SetupAI(r, k); else r.remote = true;
                racers.Add(r);
            }
            foreach (var p in room.players.OrderBy(x => x.slot))
            {
                if (!p.inGame) continue;
                bool me = p.id == net.MyId;
                var col = me ? settings.paint : NetSession.IntToColor(p.profile.color);
                var r = new Racer { name = p.profile.nick, isPlayer = me, color = col, remote = !me };
                r.car = CarModels.Create(me ? settings.carId : p.profile.carId, me ? "Coche jugador" : "Coche " + p.profile.nick, col, me);
                if (me) { r.car.gameObject.AddComponent<PlayerDriver>().car = r.car; player = r; }
                else r.net = new NetCar { owner = p.id };
                racers.Add(r);
            }
            if (player == null)
            {
                // no debería pasar: por si acaso, un coche propio al final
                var r = new Racer { name = net.nick, isPlayer = true, color = settings.paint };
                r.car = CarModels.Create(settings.carId, "Coche jugador", settings.paint, true);
                r.car.gameObject.AddComponent<PlayerDriver>().car = r.car;
                player = r;
                racers.Add(r);
            }
        }

        /// <summary>El líder se ha ido y ahora lo soy yo: la IA pasa a simularse en este PC.</summary>
        void AdoptAI()
        {
            if (!Active || !online || sync == null) return;
            var cars = racers.Select(x => x.car).ToList();
            foreach (var r in racers)
            {
                if (r.aiIndex < 0 || !r.remote || r.car == null) continue;
                sync.TakeOver(r.net);
                r.remote = false;
                SetupAI(r, r.aiIndex);
                r.ai.others = cars;
                r.progress = r.net.progress; r.lapsDone = r.net.laps; r.finished = r.net.Finished;
                r.finishTime = r.net.finishTime; r.bestLap = r.net.bestLap;
                float d;
                r.idx = circuit.Closest(r.car.transform.position, -1, 0, out d);
                r.lastS = circuit.ProjectS(r.car.transform.position, r.idx);
                r.lapStart = Clock;
                r.ai.ResetTracking();
                if (state != State.Racing) r.car.frozen = true;
            }
            Flash("Ahora eres el líder de la sala", 2f);
        }

        void PlaceOnGrid()
        {
            int total = racers.Count;
            for (int k = 0; k < total; k++)
            {
                int row = k / 2;
                int side = k % 2 == 0 ? -1 : 1;
                float s = -(7f + row * 9f);
                float sw = Mathf.Repeat(s, circuit.length);
                int idx = circuit.IndexAt(sw);
                float hw = circuit.width[idx] * 0.5f;
                Vector3 p = circuit.PosAt(sw, true);
                Vector3 d = circuit.DirAt(sw);
                Vector3 flat = new Vector3(d.x, 0, d.z).normalized;
                Vector3 right = new Vector3(flat.z, 0, -flat.x);
                p += right * side * Mathf.Min(hw * 0.45f, 1.8f);
                p = Ground(p) + Vector3.up * 0.25f;
                var r = racers[k];
                r.car.Teleport(p, Quaternion.LookRotation(d, Vector3.up));
                r.idx = idx;
                r.lastS = sw;
                r.progress = s;
                r.lapsDone = 0;
                r.lapTimes.Clear();
                r.bestLap = -1;
                r.finished = false;
                r.offTimer = r.wrongTimer = r.stuckTimer = r.flipTimer = 0;
                if (r.ai != null) r.ai.ResetTracking();
            }
        }

        static readonly RaycastHit[] groundHits = new RaycastHit[8];

        /// <summary>Superficie de rodadura bajo p (terreno o tablero), ignorando coches y sin saltar a un puente superior.</summary>
        static Vector3 Ground(Vector3 p)
        {
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 2.5f, Vector3.down, groundHits, 12f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; Vector3 pt = p;
            for (int i = 0; i < n; i++)
            {
                if (groundHits[i].rigidbody != null) continue;
                if (groundHits[i].distance < best) { best = groundHits[i].distance; pt = groundHits[i].point; }
            }
            return pt;
        }

        public void End()
        {
            if (net != null) net.BecameLeader -= AdoptAI;
            if (sync != null) Destroy(sync.gameObject);
            sync = null;
            foreach (var r in racers) if (r.car != null) Destroy(r.car.gameObject);
            racers.Clear();
            player = null;
            if (circuitRoot != null) Destroy(circuitRoot);
            if (traffic != null) Destroy(traffic.gameObject);
            traffic = null;
            if (arrows != null) Destroy(arrows.gameObject);
            arrows = null;
            if (map != null) map.Dispose();
            map = null;
            if (chase != null) chase.enabled = false;
            Time.timeScale = 1f;
            Active = false;
        }

        void Update()
        {
            if (pending != null) { var pa = pending; pending = null; pa(); }
            if (!Active) return;
            if (GameInput.PausePressed && (state != State.Finished || online))
            {
                paused = !paused;
                if (!online) Time.timeScale = paused ? 0f : 1f;     // online no se puede parar el tiempo
            }
            if (GameInput.MapPressed) showMinimap = !showMinimap;
            if (GameInput.MapZoomPressed) mapZoom = (mapZoom + 1) % (MapView.Zooms.Length + 1);
            if (GameInput.ArrowsPressed && arrows != null) arrows.show = !arrows.show;
            if (paused && !online) return;

            if (state == State.Countdown)
            {
                if (online) countdown = (float)(netStartAt - net.Core.ServerTime);
                else countdown -= Time.deltaTime;
                if (countdown <= 0f)
                {
                    state = State.Racing;
                    raceStart = online ? 0f : Time.time;
                    foreach (var r in racers) { r.car.frozen = false; r.lapStart = raceStart; }
                    Flash("¡YA!", 1.2f);
                }
            }
            if (state != State.Countdown) raceTime = Clock - raceStart;

            foreach (var r in racers) Track(r);
            if (online)
                foreach (var r in racers)
                {
                    if (r.remote || r.net == null) continue;
                    r.net.progress = r.progress; r.net.laps = (byte)Mathf.Clamp(r.lapsDone, 0, 255);
                    r.net.finishTime = r.finishTime; r.net.bestLap = r.bestLap;
                    byte f = 0;
                    if (r.finished) f |= CarState.Finished;
                    if (r.offTrack) f |= CarState.OffTrack;
                    if (r.wrongWay) f |= CarState.WrongWay;
                    r.net.flags = f;
                }
            if (arrows != null && player != null)
                arrows.Place(player.lastS, !player.offTrack && !player.finished && !player.wrongWay);

            // posiciones
            // online: el orden de llegada sale del tiempo de meta (reloj común), igual en todos los PCs
            // (el puesto queda fijado al cruzar la meta: los que han llegado van siempre delante, ordenados por
            //  su tiempo de llegada; nadie puede adelantarles en la clasificación después)
            var order = new List<Racer>(racers);
            order.Sort(CompareRacers);
            for (int i = 0; i < order.Count; i++) order[i].position = i + 1;
            foreach (var r in racers) if (r.finished && !r.ghosted) Ghost(r);

            if (player != null && state == State.Racing && GameInput.ResetPressed) ResetRacer(player);
            // los rivales siguen corriendo hasta cruzar la meta (su tiempo es el real); si alguno tarda
            // muchísimo, se le da un tiempo estimado por su ritmo y se para poco a poco
            if (state == State.Finished && Clock - finishedAt > EstimateAfter)
                foreach (var r in racers)
                    if (!r.finished && !r.remote && !r.gone && !r.isPlayer) EstimateFinish(r);
            if (online) CheckGone();
        }

        const float EstimateAfter = 150f;

        int CompareRacers(Racer a, Racer b)
        {
            if (a == b) return 0;
            if (a.gone != b.gone) return a.gone ? 1 : -1;
            if (a.finished != b.finished) return a.finished ? -1 : 1;
            int c;
            if (a.finished)
            {
                if (a.estimated != b.estimated) return a.estimated ? 1 : -1;
                c = a.finishTime.CompareTo(b.finishTime);
                if (c == 0) c = a.finishOrder.CompareTo(b.finishOrder);
            }
            else c = b.progress.CompareTo(a.progress);
            return c != 0 ? c : racers.IndexOf(a).CompareTo(racers.IndexOf(b));
        }

        /// <summary>Tras la meta el coche ya no choca con los demás de la carrera (así nadie se lo come al frenar).</summary>
        void Ghost(Racer r)
        {
            r.ghosted = true;
            if (r.car == null) return;
            var mine = r.car.GetComponentsInChildren<Collider>();
            foreach (var o in racers)
            {
                if (o == r || o.car == null) continue;
                var theirs = o.car.GetComponentsInChildren<Collider>();
                foreach (var a in mine) foreach (var b in theirs) if (a != null && b != null) Physics.IgnoreCollision(a, b, true);
            }
        }

        /// <summary>Piloto automático de después de la meta: frena poco a poco y se aparta a la derecha.</summary>
        void StartCoolDown(Racer r)
        {
            if (r.remote || r.car == null) return;
            r.car.ClearPenalty();
            if (r.isPlayer)
            {
                var pd = r.car.GetComponent<PlayerDriver>();
                if (pd != null) pd.enabled = false;
                var ai = r.car.GetComponent<AIDriver>();
                if (ai == null) ai = r.car.gameObject.AddComponent<AIDriver>();
                ai.car = r.car; ai.circuit = circuit; ai.skill = 0.85f; ai.lane = 0f;
                var cars = new List<CarController>();
                foreach (var o in racers) if (o.car != null && !o.gone) cars.Add(o.car);
                ai.others = cars;
                ai.ResetTracking();
                ai.StartCoolDown(7f);
                r.ai = ai;
                // carrera terminada: se apaga el motor (el sonido se va apagando a medida que el coche se para)
                var ea = r.car.GetComponent<EngineAudio>();
                if (ea != null) ea.ShutDown();
            }
            else if (r.ai != null) r.ai.StartCoolDown(7f);
        }

        void EstimateFinish(Racer r)
        {
            float avg = raceTime > 1 ? Mathf.Max(r.progress, 1f) / raceTime : 10f;
            float remaining = Mathf.Max(0f, settings.laps * circuit.length - r.progress);
            r.finished = true; r.estimated = true;
            r.finishTime = raceTime + remaining / Mathf.Max(avg, 1f);
            r.finishOrder = ++finishCounter;
            StartCoolDown(r);
        }

        void CheckGone()
        {
            foreach (var r in racers)
            {
                if (r.gone || r.net == null || !r.net.gone) continue;
                r.gone = true;
                foreach (var o in racers) if (o.ai != null && o.ai.others != null) o.ai.others.Remove(r.car);
            }
        }

        void Track(Racer r)
        {
            if (r.remote)
            {
                if (r.net == null || r.gone) return;
                r.progress = r.net.progress; r.lapsDone = r.net.laps; r.bestLap = r.net.bestLap;
                if (r.net.Finished && !r.finished) { r.finished = true; r.finishTime = r.net.finishTime; r.finishOrder = ++finishCounter; }
                r.offTrack = (r.net.flags & CarState.OffTrack) != 0;
                r.wrongWay = (r.net.flags & CarState.WrongWay) != 0;
                return;
            }
            var car = r.car;
            Vector3 p = car.transform.position;
            float d;
            float L = circuit.length;
            // Fuera del circuito (sólo jugador): el progreso se congela en el punto por donde salió.
            // Al volver a la pista se acepta si vuelve cerca; si ha atajado, vuelve a donde salió.
            bool lockedOff = r.isPlayer && r.offTrack && state == State.Racing && !r.finished && r.idx >= 0;
            if (lockedOff)
            {
                Vector3 ex = circuit.pos[r.idx];
                d = new Vector2(ex.x - p.x, ex.z - p.z).magnitude;
                if (Time.time >= r.nextRejoinCheck)
                {
                    r.nextRejoinCheck = Time.time + 0.2f;
                    float gd;
                    int gi = circuit.Closest(p, -1, 0, out gd);
                    if (gd < circuit.width[gi] * 0.5f + 3f && Mathf.Abs(circuit.pos[gi].y - p.y) < 3f)
                    {
                        float ahead = Mathf.Repeat((gi - r.idx) * circuit.SampleSpacing, L);
                        if (ahead > L * 0.5f) ahead -= L;          // negativo = ha vuelto por detrás
                        if (ahead <= 100f && ahead >= -400f) { r.idx = gi; d = gd; lockedOff = false; r.offTrack = false; }
                        else { r.offTrack = false; ResetRacer(r, "Atajo: vuelves al punto donde saliste del circuito"); return; }
                    }
                }
            }
            else r.idx = circuit.Closest(p, r.idx, 25, out d);
            r.distToLine = d;
            if (lockedOff)
            {
                r.offTimer += Time.deltaTime;
                r.flipTimer = Vector3.Dot(car.transform.up, Vector3.up) < 0.3f ? r.flipTimer + Time.deltaTime : 0f;
                r.wrongWay = false; r.wrongTimer = 0f;
                if (r.flipTimer > 2.5f || p.y < -50f) ResetRacer(r);
                return;
            }
            float s = circuit.ProjectS(p, r.idx);
            float ds = s - r.lastS;
            if (ds > L * 0.5f) ds -= L;
            if (ds < -L * 0.5f) ds += L;
            r.lastS = s;
            if (state == State.Countdown) return;
            if (!r.finished) r.progress += ds;

            int laps = Mathf.FloorToInt(r.progress / L);
            if (!r.finished && laps > r.lapsDone)
            {
                float lt = Clock - r.lapStart;
                r.lapTimes.Add(lt);
                if (r.bestLap < 0 || lt < r.bestLap) r.bestLap = lt;
                r.lapStart = Clock;
                r.lapsDone = laps;
                if (r.isPlayer && laps < settings.laps)
                    Flash(laps == settings.laps - 1 ? "¡ÚLTIMA VUELTA!  " + FormatTime(lt) : "Vuelta " + laps + "  " + FormatTime(lt), 2.5f);
                if (r.lapsDone >= settings.laps)
                {
                    // tiempo exacto del cruce de la línea (interpolado dentro del fotograma)
                    float over = r.progress - settings.laps * L;
                    float back = ds > 0.01f ? Time.deltaTime * Mathf.Clamp01(over / ds) : 0f;
                    r.finished = true;
                    r.finishTime = Clock - raceStart - back;
                    r.finishOrder = ++finishCounter;
                    r.lapTimes[r.lapTimes.Count - 1] -= back;
                    if (r.bestLap > r.lapTimes[r.lapTimes.Count - 1]) r.bestLap = r.lapTimes[r.lapTimes.Count - 1];
                    Ghost(r);
                    StartCoolDown(r);
                    if (r.isPlayer)
                    {
                        state = State.Finished;
                        finishedAt = Clock;
                        Flash("¡META!", 3f);
                    }
                }
            }

            // fuera de pista / sentido contrario / atascado / volcado
            float hw = circuit.width[r.idx] * 0.5f;
            r.offTrack = d > hw + 10f;
            r.offTimer = r.offTrack ? r.offTimer + Time.deltaTime : 0f;
            Vector3 dir = circuit.dir[r.idx];
            bool wrong = Vector3.Dot(car.transform.forward, dir) < -0.3f && car.ForwardSpeed > 4f;
            r.wrongTimer = wrong ? r.wrongTimer + Time.deltaTime : 0f;
            r.wrongWay = r.wrongTimer > 1.2f;
            bool blocked = r.ai != null && r.ai.Blocked;
            r.stuckTimer = (!car.frozen && car.PenaltyLeft <= 0f && car.Speed < 1.2f && !blocked) ? r.stuckTimer + Time.deltaTime : 0f;
            r.flipTimer = Vector3.Dot(car.transform.up, Vector3.up) < 0.3f ? r.flipTimer + Time.deltaTime : 0f;

            if (r.finished)
            {   // después de la meta sólo se recoloca si vuelca o se cae del mapa
                if (r.flipTimer > 3f || p.y < -50f) ResetRacer(r);
                return;
            }
            if (r.isPlayer)
            {
                // sin cuenta atrás por salirse: sólo se recoloca si vuelca o cae fuera del mapa (o pulsando R)
                if (r.flipTimer > 2.5f || p.y < -50f) ResetRacer(r);
            }
            else
            {
                if (r.offTimer > 2.5f || r.stuckTimer > 4f || r.flipTimer > 2f || r.wrongTimer > 3f || p.y < -50f) ResetRacer(r);
            }
        }

        void ResetRacer(Racer r) { ResetRacer(r, null); }

        void ResetRacer(Racer r, string why)
        {
            float s = Mathf.Repeat(r.idx * circuit.SampleSpacing - 4f, circuit.length);
            Vector3 p = Ground(circuit.PosAt(s, true)) + Vector3.up * 0.4f;
            r.car.Teleport(p, Quaternion.LookRotation(circuit.DirAt(s), Vector3.up));
            r.offTimer = r.stuckTimer = r.flipTimer = r.wrongTimer = 0f;
            float d;
            r.idx = circuit.Closest(p, r.idx, 25, out d);
            float ns = circuit.ProjectS(p, r.idx);
            float delta = ns - r.lastS;
            if (delta > circuit.length * 0.5f) delta -= circuit.length;
            if (delta < -circuit.length * 0.5f) delta += circuit.length;
            if (!r.finished) r.progress += delta;
            r.lastS = ns;
            if (r.ai != null) r.ai.ResetTracking();
            r.offTrack = false;
            if (r.isPlayer) Flash(why ?? "Recolocado en el circuito", why != null ? 3f : 1.5f);
        }

        void Flash(string m, float t) { message = m; messageUntil = Time.unscaledTime + t; }

        public static string FormatTime(float t)
        {
            if (t < 0) return "--:--.---";
            int m = (int)(t / 60f);
            float sec = t - m * 60;
            return m.ToString("00") + ":" + sec.ToString("00.000");
        }

        // ------------------------------------------------------------------ minimapa
        void BuildMinimap()
        {
            Vector2 mn = new Vector2(float.MaxValue, float.MaxValue), mx = new Vector2(float.MinValue, float.MinValue);
            foreach (var p in circuit.raw) { mn = Vector2.Min(mn, new Vector2(p.x, p.z)); mx = Vector2.Max(mx, new Vector2(p.x, p.z)); }
            mapSpan = Mathf.Max(mx.x - mn.x, mx.y - mn.y) + 80f;
            mapCenter = (mn + mx) * 0.5f;
            if (map != null) map.Dispose();
            map = new MapView(world, circuit);
        }

        // ------------------------------------------------------------------ HUD
        GUIStyle big, mid, small, label, box, centerHuge, right;
        float uiScale;

        void Styles()
        {
            if (big != null) return;
            big = new GUIStyle(GUI.skin.label) { fontSize = 46, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            big.normal.textColor = Color.white;
            mid = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
            mid.normal.textColor = Color.white;
            small = new GUIStyle(GUI.skin.label) { fontSize = 17 };
            small.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
            label = new GUIStyle(GUI.skin.label) { fontSize = 20 };
            label.normal.textColor = Color.white;
            right = new GUIStyle(label) { alignment = TextAnchor.MiddleRight };
            box = new GUIStyle(GUI.skin.box);
            box.normal.background = Tex.Solid(new Color(0, 0, 0, 0.55f));
            centerHuge = new GUIStyle(GUI.skin.label) { fontSize = 96, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            centerHuge.normal.textColor = new Color(1f, 0.85f, 0.2f);
        }

        void OnGUI()
        {
            if (!Active || player == null) return;
            Styles();
            uiScale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1));
            float W = Screen.width / uiScale, H = 1080f;

            // ---- arriba izquierda: vuelta, posición y tiempos
            GUI.Box(new Rect(20, 20, 330, 200), GUIContent.none, box);
            int lapShow = Mathf.Clamp(player.lapsDone + 1, 1, settings.laps);
            GUI.Label(new Rect(36, 28, 300, 34), "VUELTA  " + lapShow + " / " + settings.laps, mid);
            GUI.Label(new Rect(36, 62, 300, 34), "POSICIÓN  " + player.position + " / " + racers.Count, mid);
            float cur = state == State.Countdown ? 0 : (player.finished ? player.finishTime : raceTime);
            GUI.Label(new Rect(36, 100, 300, 26), "Tiempo      " + FormatTime(cur), label);
            float lapNow = state == State.Racing && !player.finished ? Clock - player.lapStart : 0;
            GUI.Label(new Rect(36, 126, 300, 26), "Vuelta       " + FormatTime(lapNow), label);
            GUI.Label(new Rect(36, 152, 300, 26), "Última       " + FormatTime(player.lapTimes.Count > 0 ? player.lapTimes[player.lapTimes.Count - 1] : -1), label);
            GUI.Label(new Rect(36, 178, 300, 26), "Mejor        " + FormatTime(player.bestLap), label);

            // ---- arriba centro: información geográfica real
            Vector3 pp = player.car.transform.position;
            string street = circuit.StreetAt(player.idx);
            if (player.offTrack)
            {
                float dd;
                int e = world.graph.NearestEdge(pp, 40f, out dd);
                street = e >= 0 ? world.graph.edges[e].name : "Fuera de la red viaria";
            }
            var zone = world.ZoneAt(pp);
            int i0 = circuit.Wrap(player.idx - 5), i1 = circuit.Wrap(player.idx + 5);
            Vector3 a = circuit.raw[i0], b = circuit.raw[i1];
            float hd = new Vector2(b.x - a.x, b.z - a.z).magnitude;
            float grade = hd > 1 ? (b.y - a.y) / hd * 100f : 0f;
            float gw = 560;
            GUI.Box(new Rect(W / 2 - gw / 2, 20, gw, 92), GUIContent.none, box);
            var cst = new GUIStyle(mid) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(W / 2 - gw / 2, 24, gw, 34), street, cst);
            var cs2 = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(W / 2 - gw / 2, 56, gw, 26), (zone != null ? zone.name : "Hernani") + "  ·  Hernani (Gipuzkoa)", cs2);
            var utm = world.data.ToUTM(pp);
            var cs3 = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(W / 2 - gw / 2, 82, gw, 24),
                "Altitud " + pp.y.ToString("F0") + " m  ·  Pendiente " + grade.ToString("+0;-0") + "%  ·  UTM " + utm.x.ToString("F0") + " / " + utm.y.ToString("F0"), cs3);

            // ---- abajo derecha: velocímetro de aguja + digital
            float kmh = Mathf.Abs(player.car.ForwardSpeed) * 3.6f;
            Speedometer.Draw(new Rect(W - 300, H - 300, 280, 280), kmh, player.car.maxSpeed * 3.6f, player.car.Reversing && player.car.ForwardSpeed < -0.5f);

            // ---- clasificación en vivo
            var order = racers.OrderBy(r => r.position).ToList();
            float ly = 240;
            GUI.Box(new Rect(20, ly, 330, 30 + order.Count * 26), GUIContent.none, box);
            GUI.Label(new Rect(36, ly + 4, 300, 24), "CLASIFICACIÓN", small);
            for (int i = 0; i < order.Count; i++)
            {
                var r = order[i];
                float y = ly + 28 + i * 26;
                GUI.DrawTexture(new Rect(36, y + 6, 14, 14), Tex.Solid(r.color));
                string gap = "";
                if (r != player && !r.finished && !player.finished) gap = (r.progress - player.progress).ToString("+0;-0") + " m";
                if (r.finished) gap = FormatTime(r.finishTime);
                if (r.gone) gap = "fuera";
                var st = r.isPlayer ? mid : label;
                GUI.Label(new Rect(58, y, 160, 26), r.position + ". " + r.name, st);
                GUI.Label(new Rect(190, y, 150, 26), gap, right);
            }

            // ---- minimapa
            if (showMinimap && map != null)
            {
                float ms = 300;
                Rect mr = new Rect(W - ms - 20, 20, ms, ms);
                GUI.Box(new Rect(mr.x - 3, mr.y - 3, ms + 6, ms + 6), GUIContent.none, box);
                Vector3 pp2 = player.car.transform.position;
                // zoom: el circuito entero o una ventana de 5 km a 100 m alrededor del coche (tecla N)
                float span = mapZoom > 0 ? MapView.Zooms[mapZoom - 1] : mapSpan;
                if (mapZoom > 0) map.Draw(mr, new Vector2(pp2.x, pp2.z), span);
                else map.Draw(mr, mapCenter, mapSpan);
                if (traffic != null)
                {
                    float ts = MapView.MarkerSize(span, 9, 3);
                    foreach (var tc in traffic.cars) if (tc != null) map.Marker(tc.transform.position, tc.transform.forward, new Color(0.75f, 0.75f, 0.75f), false, ts);
                }
                foreach (var r in racers)
                    if (!r.isPlayer && !r.gone) map.Marker(r.car.transform.position, r.car.transform.forward, r.color, false, MapView.MarkerSize(span, 14, 9));
                map.Marker(pp2, player.car.transform.forward, player.color, true, MapView.MarkerSize(span, 26, 18));
                GUI.Label(new Rect(mr.x, mr.yMax + 4, ms, 22), (mapZoom > 0 ? "Zoom " + MapView.ZoomLabel(span) : "Circuito: " + circuit.length.ToString("F0") + " m · desnivel +" + circuit.elevationGain.ToString("F0") + " m") + "  ·  N zoom · M ocultar", small);
            }
            if (chase != null && chase.RearView)
            {
                var rv = new GUIStyle(mid) { alignment = TextAnchor.MiddleCenter };
                GUI.Box(new Rect(W / 2 - 140, H - 120, 280, 40), GUIContent.none, box);
                GUI.Label(new Rect(W / 2 - 140, H - 120, 280, 40), "VISTA TRASERA", rv);
            }

            // ---- avisos
            if (state == State.Countdown)
            {
                int n = Mathf.CeilToInt(countdown - 1f);
                if (n >= 4) GUI.Label(new Rect(0, H / 2 - 160, W, 160), "Preparados...", new GUIStyle(centerHuge) { fontSize = 64 });
                else GUI.Label(new Rect(0, H / 2 - 160, W, 160), n >= 1 ? n.ToString() : "¡YA!", centerHuge);
            }
            else if (Time.unscaledTime < messageUntil && !string.IsNullOrEmpty(message))
            {
                var ms2 = new GUIStyle(centerHuge) { fontSize = 56 };
                GUI.Label(new Rect(0, H / 2 - 200, W, 100), message, ms2);
            }
            if (state == State.Racing && !player.finished)
            {
                var warn = new GUIStyle(centerHuge) { fontSize = 44 };
                warn.normal.textColor = new Color(1f, 0.3f, 0.25f);
                if (player.wrongWay) GUI.Label(new Rect(0, H / 2 - 60, W, 60), "¡SENTIDO CONTRARIO!", warn);
                else if (player.offTrack)
                {
                    GUI.Label(new Rect(0, H / 2 - 60, W, 60), "FUERA DEL CIRCUITO", warn);
                    var hint = new GUIStyle(centerHuge) { fontSize = 22 };
                    hint.normal.textColor = new Color(1f, 0.85f, 0.8f);
                    GUI.Label(new Rect(0, H / 2 - 8, W, 34), "Vuelve a la pista por donde saliste · R para recolocar", hint);
                }
            }
            PenaltyHud.Draw(W, H, player.car);
            GUI.Label(new Rect(20, H - 34, 1500, 26), "W/S acelerar/frenar · A/D girar · Espacio freno de mano · R recolocar · C cámara · B mirar atrás · N zoom mapa · F flechas · Esc pausa", small);

            if (paused) PauseMenu(W, H);
            else if (state == State.Finished) Results(W, H);
            GUI.matrix = Matrix4x4.identity;
        }

        void PauseMenu(float W, float H)
        {
            Rect r = new Rect(W / 2 - 200, H / 2 - 150, 400, 300);
            GUI.Box(r, GUIContent.none, box);
            GUI.Label(new Rect(r.x, r.y + 20, r.width, 50), "PAUSA", new GUIStyle(mid) { alignment = TextAnchor.MiddleCenter, fontSize = 36 });
            if (online)
            {
                GUI.Label(new Rect(r.x, r.y + 56, r.width, 30), "(online: la carrera sigue)", new GUIStyle(small) { alignment = TextAnchor.MiddleCenter });
                if (Button(new Rect(r.x + 60, r.y + 90, 280, 50), "Continuar")) paused = false;
                if (Button(new Rect(r.x + 60, r.y + 150, 280, 50), "Salir a la sala")) { paused = false; pending = net.LeaveGame; }
                if (net.IsLeader && Button(new Rect(r.x + 60, r.y + 210, 280, 50), "Terminar para todos")) { paused = false; pending = net.Core.EndGame; }
                return;
            }
            if (Button(new Rect(r.x + 60, r.y + 90, 280, 50), "Continuar")) { paused = false; Time.timeScale = 1f; }
            if (Button(new Rect(r.x + 60, r.y + 150, 280, 50), "Reiniciar carrera")) { paused = false; pending = () => Begin(world, circuit, settings); }
            if (Button(new Rect(r.x + 60, r.y + 210, 280, 50), "Elegir otro circuito")) { paused = false; pending = game.ShowSelector; }
        }

        void Results(float W, float H)
        {
            if (Clock - finishedAt < 2.5f) return;
            var order = racers.OrderBy(r => r.position).ToList();
            float h = 200 + order.Count * 34;
            Rect r = new Rect(W / 2 - 330, H / 2 - h / 2, 660, h);
            GUI.Box(r, GUIContent.none, box);
            GUI.Label(new Rect(r.x, r.y + 14, r.width, 50), "RESULTADOS — " + player.position + "º puesto", new GUIStyle(mid) { alignment = TextAnchor.MiddleCenter, fontSize = 34 });
            for (int i = 0; i < order.Count; i++)
            {
                var x = order[i];
                float y = r.y + 76 + i * 34;
                GUI.DrawTexture(new Rect(r.x + 40, y + 8, 16, 16), Tex.Solid(x.color));
                GUI.Label(new Rect(r.x + 66, y, 220, 30), x.position + ". " + x.name, x.isPlayer ? mid : label);
                string t;
                if (x.finished) t = (x.estimated ? "≈" : "") + FormatTime(x.finishTime) + (x.estimated ? " (estimado)" : "");
                else if (x.gone) t = "abandona";
                else
                {
                    float avg = raceTime > 1 ? Mathf.Max(x.progress, 1f) / raceTime : 10f;
                    float remaining = settings.laps * circuit.length - x.progress;
                    t = "~" + FormatTime(raceTime + remaining / Mathf.Max(avg, 1f)) + " (en pista)";
                }
                GUI.Label(new Rect(r.x + 280, y, 220, 30), t, label);
                GUI.Label(new Rect(r.x + 480, y, 160, 30), "mejor " + FormatTime(x.bestLap), small);
            }
            if (online)
            {
                if (net.IsLeader)
                {
                    if (Button(new Rect(r.x + 60, r.yMax - 80, 250, 52), "Volver a la sala")) pending = net.Core.EndGame;
                    GUI.Label(new Rect(r.x + 330, r.yMax - 80, 300, 52), "Todos vuelven a la sala para\nelegir otra carrera", small);
                }
                else
                {
                    if (Button(new Rect(r.x + 60, r.yMax - 80, 250, 52), "Salir a la sala")) pending = net.LeaveGame;
                    GUI.Label(new Rect(r.x + 330, r.yMax - 80, 300, 52), "El líder decide la siguiente\ncarrera", small);
                }
                return;
            }
            if (Button(new Rect(r.x + 60, r.yMax - 80, 250, 52), "Repetir carrera")) pending = () => Begin(world, circuit, settings);
            if (Button(new Rect(r.x + 350, r.yMax - 80, 250, 52), "Elegir otro circuito")) pending = game.ShowSelector;
        }

        static GUIStyle buttonStyle;
        public static bool Button(Rect r, string text)
        {
            if (buttonStyle == null)
            {
                buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 22, fontStyle = FontStyle.Bold };
            }
            return GUI.Button(r, text, buttonStyle);
        }
    }
}
