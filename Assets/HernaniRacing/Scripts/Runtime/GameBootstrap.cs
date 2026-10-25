using System.Collections;
using HernaniRacing.Net;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Punto de entrada: carga los datos reales, construye Hernani y alterna entre
    /// el selector de circuito y la carrera. Basta con un GameObject vacío con este componente.
    /// </summary>
    public class GameBootstrap : MonoBehaviour
    {
        public Camera MainCamera { get; private set; }
        public World world;
        CircuitSelector selector;
        MainMenu menu;
        RaceManager race;
        FreeRoamManager free;
        public NetSession net;
        /// <summary>Se está en los menús (presentación, coche, circuito, paseo, online).</summary>
        public bool InMenu { get { return ready && menu != null && menu.IsOpen; } }

        string loadingMsg = "Cargando datos de Hernani...";
        float loadingProgress;
        string fatal;
        bool ready;

        IEnumerator Start()
        {
            Application.targetFrameRate = 120;
            SetupCamera();
            SetupEnvironment();
            yield return null;

            world = new World();
            try
            {
                loadingMsg = "Leyendo datos b5m / geoEuskadi...";
                world.data = HernaniDataSet.Load();
            }
            catch (System.Exception e)
            {
                fatal = e.Message;
                Debug.LogException(e);
                yield break;
            }
            yield return null;
            yield return WorldBuilder.Build(world, (m, p) => { loadingMsg = m; loadingProgress = p; });

            loadingMsg = "Dibujando el plano de Hernani...";
            loadingProgress = 1f;
            yield return null;
            new MapView(world, null);   // plano base (para los resúmenes del menú y el minimapa)
            GroundSurface.Create(gameObject, world);   // colores u ortofoto (según la última elección)

            menu = gameObject.AddComponent<MainMenu>();
            menu.game = this;
            selector = gameObject.AddComponent<CircuitSelector>();
            selector.game = this;
            selector.menu = menu;
            menu.map = selector;
            race = gameObject.AddComponent<RaceManager>();
            race.game = this;
            free = gameObject.AddComponent<FreeRoamManager>();
            free.game = this;
            net = gameObject.AddComponent<NetSession>();
            net.game = this;
            var lobby = gameObject.AddComponent<NetLobbyUI>();
            lobby.game = this;
            lobby.net = net;
            lobby.menu = menu;
            menu.net = net;
            menu.lobby = lobby;
            menu.Init();
            ready = true;
            ShowSelector();
        }

        void SetupCamera()
        {
            MainCamera = Camera.main;
            if (MainCamera == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                MainCamera = go.AddComponent<Camera>();
            }
            if (MainCamera.GetComponent<AudioListener>() == null) MainCamera.gameObject.AddComponent<AudioListener>();
            MainCamera.farClipPlane = 4000f;
            MainCamera.transform.position = new Vector3(2600, 1500, 10944);   // sobre Hernani (desde la v15 el origen está 6144 m más al sur)
            MainCamera.transform.rotation = Quaternion.Euler(90, 0, 0);
            if (RenderSettings.skybox == null)
            {
                MainCamera.clearFlags = CameraClearFlags.SolidColor;
                MainCamera.backgroundColor = new Color(0.62f, 0.75f, 0.88f);
            }
        }

        void SetupEnvironment()
        {
#if UNITY_2023_1_OR_NEWER
            var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
#else
#if UNITY_2023_1_OR_NEWER
            var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
#else
            var lights = Object.FindObjectsOfType<Light>();
#endif
#endif
            foreach (var l in lights) if (l.type == LightType.Directional) l.gameObject.SetActive(false);
            var sun = new GameObject("Sol (Hernani 43.27N)").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.15f;
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.75f;
            sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
            RenderSettings.sun = sun;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.70f, 0.82f);
            RenderSettings.ambientEquatorColor = new Color(0.52f, 0.55f, 0.52f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.30f, 0.26f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.70f, 0.78f, 0.86f);
            RenderSettings.fogStartDistance = 700f;
            RenderSettings.fogEndDistance = 3200f;
            QualitySettings.shadowDistance = 350f;
        }

        public void ShowSelector()
        {
            if (!ready) return;
            race.End();
            free.End();
            RenderSettings.fog = false;
            menu.Open();
        }

        public void StartRace(Circuit c, RaceSettings s)
        {
            if (c == null || c.error != null) return;
            menu.Close();
            RenderSettings.fog = true;
            race.Begin(world, c, s);
        }

        /// <summary>Paseo libre (sin carrera) empezando junto al cruce 'startNode' (o en el centro si es -1).</summary>
        public void StartFreeRoam(RaceSettings s, int startNode)
        {
            menu.Close();
            race.End();
            RenderSettings.fog = true;
            free.Begin(world, s, startNode);
        }

        /// <summary>Empieza la partida online que ha lanzado el líder de la sala.</summary>
        public bool StartOnline(RoomInfo room)
        {
            if (!ready) return false;
            var s = net.SettingsForMe(room.settings);
            if (room.phase == RoomPhase.Race)
            {
                var c = Circuit.Build(world.graph, room.settings.nodes, room.settings.closed);
                if (c == null || c.error != null)
                {
                    net.ShowToast("No se puede montar el circuito de la sala" + (c != null ? ": " + c.error : ""), 5f);
                    return false;
                }
                menu.Close();
                free.End();
                RenderSettings.fog = true;
                race.BeginOnline(world, c, s, net, room);
                return true;
            }
            if (room.phase == RoomPhase.Free)
            {
                menu.Close();
                race.End();
                RenderSettings.fog = true;
                free.BeginOnline(world, s, room.settings.freeStartNode, net, room);
                return true;
            }
            return false;
        }

        void OnGUI()
        {
            if (ready) return;
            float ui = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(ui, ui, 1));
            float W = Screen.width / ui;
            GUI.DrawTexture(new Rect(0, 0, W, 1080), Tex.Solid(new Color(0.06f, 0.07f, 0.09f)));
            var t = new GUIStyle(GUI.skin.label) { fontSize = 64, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            t.normal.textColor = new Color(1f, 0.85f, 0.25f);
            GUI.Label(new Rect(0, 340, W, 90), "HERNANI RACING", t);
            var s = new GUIStyle(GUI.skin.label) { fontSize = 24, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            s.normal.textColor = Color.white;
            if (fatal != null)
            {
                s.normal.textColor = new Color(1f, 0.4f, 0.35f);
                GUI.Label(new Rect(W / 2 - 600, 460, 1200, 200), "Error: " + fatal, s);
            }
            else
            {
                GUI.Label(new Rect(0, 460, W, 40), loadingMsg, s);
                GUI.DrawTexture(new Rect(W / 2 - 300, 520, 600, 10), Tex.Solid(new Color(1, 1, 1, 0.15f)));
                GUI.DrawTexture(new Rect(W / 2 - 300, 520, 600 * loadingProgress, 10), Tex.Solid(new Color(1f, 0.85f, 0.25f)));
            }
            GUI.matrix = Matrix4x4.identity;
        }
    }
}
