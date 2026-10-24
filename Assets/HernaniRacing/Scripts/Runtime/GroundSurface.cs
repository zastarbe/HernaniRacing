using System.Collections;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Superficie del suelo: los colores de siempre (hierba, bosque, roca y zonas urbanas) o la ortofoto real de
    /// b5m (vuelo de 2025, guardada a 0,5 m por píxel en Resources/HernaniOrtho, una imagen por tesela de terreno).
    ///
    /// Truco: cada tesela de terreno tiene 4 capas mezcladas con su mapa de pesos. Para la ortofoto se ponen 4 capas
    /// que llevan la MISMA imagen (que cubre la tesela entera), así que da igual cómo estén mezcladas: se ve la foto.
    /// Para volver a los colores se restauran las capas originales. No hay que recalcular nada ni guardar copias.
    /// Las imágenes se cargan poco a poco (sin parar el juego) y se liberan al volver a los colores.
    /// </summary>
    public class GroundSurface : MonoBehaviour
    {
        public enum Mode { Colors = 0, Ortho = 1 }

        public static GroundSurface Instance { get; private set; }
        const string Folder = "HernaniOrtho/";
        const string PrefKey = "menu.surface";

        World world;
        Texture2D[] tex;
        Mode mode = Mode.Colors;
        Coroutine loading;
        int loaded;
        float toastUntil;
        string toast;
        string label = "Ortofoto";
        bool? available;

        public Mode Current { get { return mode; } }
        public bool Loading { get { return loading != null; } }
        /// <summary>Nombre corto de la ortofoto (por ejemplo «Ortofoto 2025»).</summary>
        public string OrthoLabel { get { CheckAvailable(); return label; } }

        /// <summary>¿Están las imágenes de la ortofoto en el proyecto? (DataPipeline/build_ortho.py)</summary>
        public bool Available { get { CheckAvailable(); return available.Value; } }

        void CheckAvailable()
        {
            if (available.HasValue) return;
            var info = Resources.Load<TextAsset>(Folder + "ortho");
            available = info != null && world != null && world.tiles != null;
            if (info != null)
            {
                // "layer": "orto2025" -> «Ortofoto 2025»
                string t = info.text;
                int i = t.IndexOf("\"layer\"");
                if (i >= 0)
                {
                    int q1 = t.IndexOf('"', t.IndexOf(':', i) + 1), q2 = q1 >= 0 ? t.IndexOf('"', q1 + 1) : -1;
                    if (q1 >= 0 && q2 > q1)
                    {
                        string layer = t.Substring(q1 + 1, q2 - q1 - 1);
                        string year = layer.Replace("orto", "");
                        label = "Ortofoto " + year;
                    }
                }
                Resources.UnloadAsset(info);
            }
        }

        public static GroundSurface Create(GameObject host, World w)
        {
            var g = host.AddComponent<GroundSurface>();
            g.world = w;
            Instance = g;
            if (PlayerPrefs.GetInt(PrefKey, 0) == 1 && g.Available) g.Set(Mode.Ortho, false);
            return g;
        }

        public void Toggle() { Set(mode == Mode.Ortho ? Mode.Colors : Mode.Ortho, true); }

        public void Set(Mode m, bool announce)
        {
            if (m == Mode.Ortho && !Available)
            {
                Toast("No está la ortofoto en el proyecto (DataPipeline/build_ortho.py)", 4f);
                return;
            }
            PlayerPrefs.SetInt(PrefKey, m == Mode.Ortho ? 1 : 0);
            if (m == mode) return;
            mode = m;
            if (loading != null) { StopCoroutine(loading); loading = null; }
            if (m == Mode.Ortho) loading = StartCoroutine(LoadOrtho(announce));
            else
            {
                RestoreColors();
                if (announce) Toast("Suelo: colores", 2f);
            }
        }

        IEnumerator LoadOrtho(bool announce)
        {
            int n = world.tilesX * world.tilesZ;
            if (tex == null || tex.Length != n) tex = new Texture2D[n];
            loaded = 0;
            // primero las teselas más cercanas a la cámara: se ve el cambio enseguida donde estás
            var order = new int[n];
            var dist = new float[n];
            Vector3 cp = Camera.main != null ? Camera.main.transform.position : world.Center;
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
                float cx = (i % world.tilesX + 0.5f) * world.tileSize, cz = (i / world.tilesX + 0.5f) * world.tileSize;
                dist[i] = (cx - cp.x) * (cx - cp.x) + (cz - cp.z) * (cz - cp.z);
            }
            System.Array.Sort(dist, order);
            foreach (int i in order)
            {
                if (tex[i] == null)
                {
                    int tx = i % world.tilesX, tz = i / world.tilesX;
                    var rq = Resources.LoadAsync<Texture2D>(Folder + "orto_" + tx + "_" + tz);
                    while (!rq.isDone) yield return null;
                    tex[i] = rq.asset as Texture2D;
                }
                if (mode != Mode.Ortho) yield break;
                ApplyTile(i);
                loaded++;
            }
            loading = null;
            if (announce) Toast("Suelo: " + label.ToLower() + " (b5m)", 2.5f);
        }

        void ApplyTile(int i)
        {
            var t = world.tiles[i];
            if (t == null || tex[i] == null) return;
            tex[i].wrapMode = TextureWrapMode.Clamp;
            var td = t.terrainData;
            int count = td.terrainLayers != null && td.terrainLayers.Length > 0 ? td.terrainLayers.Length : 4;
            var layers = new TerrainLayer[count];
            for (int k = 0; k < count; k++)
            {
                layers[k] = new TerrainLayer
                {
                    name = "orto_" + i + "_" + k,
                    diffuseTexture = tex[i],
                    tileSize = new Vector2(world.tileSize, world.tileSize),
                    tileOffset = Vector2.zero,
                    smoothness = 0f,
                    metallic = 0f,
                    specular = Color.black
                };
#if UNITY_6000_0_OR_NEWER
                layers[k].smoothnessSource = TerrainLayerSmoothnessSource.Constant;   // la foto no es un mapa de brillo
#endif
            }
            td.terrainLayers = layers;
            td.SetBaseMapDirty();
            // con la foto se dibuja siempre la imagen de verdad (con sus mipmaps), también desde el mapa a 1.500 m
            t.basemapDistance = 5000f;
        }

        void RestoreColors()
        {
            if (world == null || world.tiles == null || world.baseLayers == null) return;
            foreach (var t in world.tiles)
            {
                if (t == null) continue;
                var old = t.terrainData.terrainLayers;
                t.terrainData.terrainLayers = world.baseLayers;
                t.terrainData.SetBaseMapDirty();
                t.basemapDistance = 1200f;
                if (old != null) foreach (var l in old) if (l != null && l.name.StartsWith("orto_")) Destroy(l);
            }
            if (tex != null)
                for (int i = 0; i < tex.Length; i++)
                    if (tex[i] != null) { Resources.UnloadAsset(tex[i]); tex[i] = null; }
        }

        void Toast(string msg, float secs)
        {
            toast = msg;
            toastUntil = Time.unscaledTime + secs;
        }

        void Update()
        {
            // tecla O (o cruceta izquierda): cambiar el suelo durante la carrera o el paseo
            if (GUIUtility.keyboardControl == 0 && GameInput.SurfacePressed) Toggle();
        }

        GUIStyle st;

        void OnGUI()
        {
            string msg = null;
            if (loading != null) msg = "Cargando " + label.ToLower() + "...  " + loaded + " / " + (world.tilesX * world.tilesZ);
            else if (Time.unscaledTime < toastUntil) msg = toast;
            if (msg == null) return;
            if (st == null)
            {
                st = new GUIStyle(GUI.skin.box) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
                st.normal.background = Tex.Solid(new Color(0.04f, 0.05f, 0.07f, 0.85f));
                st.normal.textColor = new Color(1f, 0.88f, 0.4f);
            }
            float ui = Screen.height / 1080f;
            Matrix4x4 m = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(ui, ui, 1));
            float W = Screen.width / ui;
            GUI.depth = -10;
            GUI.Box(new Rect(W / 2 - 260, 124, 520, 40), msg, st);
            GUI.matrix = m;
        }
    }
}
