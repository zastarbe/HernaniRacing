using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// "Estudio fotográfico" para los menús: cada coche se coloca lejos del mapa (en una capa propia que la
    /// cámara principal no ve) con su cámara apuntándole, y se dibuja en una textura que se muestra en la GUI.
    /// Los coches quietos se dibujan una sola vez; el que se está mirando gira despacio.
    /// </summary>
    public class CarStudio : MonoBehaviour
    {
        public const int Layer = 30;
        static readonly Vector3 Origin = new Vector3(-30000f, 4000f, -30000f);
        static readonly Color Background = new Color(0.12f, 0.14f, 0.18f, 1f);

        class Slot
        {
            public string id;
            public Color paint;
            public Transform root;
            public GameObject car;
            public Camera cam;
            public RenderTexture rt;
            public int lastUse = -10, renderFrames;
            public float yaw = 0f;
            public bool spin;
        }

        readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>();
        int count;
        Light fill;
        Material floorMat;

        public static CarStudio Create(Camera mainCamera)
        {
            var go = new GameObject("Estudio de coches (menús)");
            var st = go.AddComponent<CarStudio>();
            if (mainCamera != null) mainCamera.cullingMask &= ~(1 << Layer);
            // luz de relleno sólo para el estudio (el sol también ilumina)
            var l = new GameObject("Luz de relleno").AddComponent<Light>();
            l.transform.SetParent(go.transform, false);
            l.type = LightType.Directional;
            l.intensity = 0.55f;
            l.color = new Color(0.85f, 0.9f, 1f);
            l.shadows = LightShadows.None;
            l.cullingMask = 1 << Layer;
            l.transform.rotation = Quaternion.Euler(25f, 160f, 0f);
            st.fill = l;
            return st;
        }

        /// <summary>
        /// Textura con el coche 'id' pintado de 'paint'. 'key' identifica el hueco (p. ej. "grid_suv" o "big"),
        /// así un mismo coche puede salir en varios tamaños. Hay que pedirla en cada OnGUI mientras se vea.
        /// </summary>
        public Texture Get(string key, string id, Color paint, int w, int h, bool spin)
        {
            Slot s;
            if (!slots.TryGetValue(key, out s))
            {
                s = new Slot();
                s.root = new GameObject("Hueco " + key).transform;
                s.root.SetParent(transform, false);
                s.root.position = Origin + new Vector3(count * 80f, 0, 0);
                count++;
                s.rt = new RenderTexture(Mathf.Max(16, w), Mathf.Max(16, h), 24) { antiAliasing = 4, name = "HR_studio_" + key };
                var camGo = new GameObject("Cámara");
                camGo.transform.SetParent(s.root, false);
                s.cam = camGo.AddComponent<Camera>();
                s.cam.cullingMask = 1 << Layer;
                s.cam.clearFlags = CameraClearFlags.SolidColor;
                s.cam.backgroundColor = Background;
                s.cam.fieldOfView = 26f;
                s.cam.nearClipPlane = 0.1f;
                s.cam.farClipPlane = 200f;
                s.cam.targetTexture = s.rt;
                s.cam.allowHDR = false;
                s.cam.enabled = false;
                s.cam.depth = -10;
                BuildFloor(s.root);
                slots[key] = s;
            }
            if (s.id != id || !Same(s.paint, paint)) SetCar(s, id, paint);
            if (s.rt.width != w || s.rt.height != h)
            {
                s.cam.targetTexture = null;
                s.rt.Release();
                s.rt.width = Mathf.Max(16, w);
                s.rt.height = Mathf.Max(16, h);
                s.cam.targetTexture = s.rt;
                s.renderFrames = 2;
            }
            s.spin = spin;
            s.lastUse = Time.frameCount;
            return s.rt;
        }

        static bool Same(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.004f && Mathf.Abs(a.g - b.g) < 0.004f && Mathf.Abs(a.b - b.b) < 0.004f;
        }

        void BuildFloor(Transform root)
        {
            if (floorMat == null) floorMat = Mat.Color(new Color(0.20f, 0.22f, 0.26f), 0.35f);
            var mb = new MeshBuilder(1);
            const int n = 40;
            const float r = 4.2f;
            for (int i = 0; i < n; i++)
            {
                float a0 = i * Mathf.PI * 2f / n, a1 = (i + 1) * Mathf.PI * 2f / n;
                Vector3 p0 = new Vector3(Mathf.Cos(a0) * r, 0, Mathf.Sin(a0) * r), p1 = new Vector3(Mathf.Cos(a1) * r, 0, Mathf.Sin(a1) * r);
                mb.FlatTri(0, Vector3.zero, p1, p0, Vector3.up, Vector2.zero, Vector2.zero, Vector2.zero);
            }
            var go = new GameObject("Suelo");
            go.layer = Layer;
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mb.Build("studio_floor");
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = floorMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        void SetCar(Slot s, string id, Color paint)
        {
            if (s.car != null) Destroy(s.car);
            s.id = id;
            s.paint = paint;
            s.car = CarModels.CreateVisual(id, paint, s.root, true);
            SetLayer(s.car.transform);
            var d = CarModels.Get(id);
            float L = 4.2f, H = 1.3f, W = 1.8f, y0 = 0f;
            if (d != null && d.size != null && d.size.Length >= 3) { W = d.size[0]; H = d.size[1]; L = d.size[2]; }
            // el origen del modelo ya está en el suelo (ruedas apoyadas en y = 0)
            // apoyado en el suelo y centrado
            s.car.transform.localPosition = new Vector3(0, -y0, 0);
            s.car.transform.localRotation = Quaternion.Euler(0, s.yaw, 0);
            // encuadre: esfera que contiene el coche aunque gire
            float radius = 0.5f * Mathf.Sqrt(L * L + W * W + H * H) * 0.92f;
            Vector3 center = s.root.position + Vector3.up * (H * 0.45f);
            float dist = radius / Mathf.Sin(s.cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            const float az = 35f, el = 16f;
            Vector3 dir = new Vector3(Mathf.Sin(az * Mathf.Deg2Rad) * Mathf.Cos(el * Mathf.Deg2Rad), Mathf.Sin(el * Mathf.Deg2Rad), Mathf.Cos(az * Mathf.Deg2Rad) * Mathf.Cos(el * Mathf.Deg2Rad));
            s.cam.transform.position = center + dir * dist;
            s.cam.transform.LookAt(center);
            s.renderFrames = 3;
        }

        static void SetLayer(Transform t)
        {
            t.gameObject.layer = Layer;
            for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i));
            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null) { mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false; }
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            foreach (var s in slots.Values)
            {
                bool visible = Time.frameCount - s.lastUse <= 2;
                bool render = false;
                if (visible && s.spin && s.car != null)
                {
                    s.yaw = Mathf.Repeat(s.yaw + 28f * dt, 360f);
                    s.car.transform.localRotation = Quaternion.Euler(0, s.yaw, 0);
                    render = true;
                }
                else if (visible && s.renderFrames > 0) { s.renderFrames--; render = true; }
                s.cam.enabled = render;
            }
        }

        void OnDestroy()
        {
            foreach (var s in slots.Values) if (s.rt != null) s.rt.Release();
        }
    }
}
