using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HernaniRacing
{
    /// <summary>
    /// Materiales y texturas procedurales. Funciona con Built-in RP y URP
    /// (detecta el pipeline activo y usa el shader adecuado).
    /// </summary>
    public static class Mat
    {
        static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();
        static Shader lit, unlit;

        public static bool IsSRP { get { return GraphicsSettings.currentRenderPipeline != null; } }

        public static Shader Lit
        {
            get
            {
                if (lit == null)
                {
                    if (IsSRP)
                    {
                        lit = Shader.Find("Universal Render Pipeline/Lit");
                        if (lit == null) lit = Shader.Find("Universal Render Pipeline/Simple Lit");
                        if (lit == null) lit = Shader.Find("HDRP/Lit");
                    }
                    if (lit == null) lit = Shader.Find("Standard");
                    if (lit == null) lit = Shader.Find("Legacy Shaders/Diffuse");
                }
                return lit;
            }
        }

        public static Shader Unlit
        {
            get
            {
                if (unlit == null)
                {
                    if (IsSRP) unlit = Shader.Find("Universal Render Pipeline/Unlit");
                    if (unlit == null) unlit = Shader.Find("Unlit/Color");
                    if (unlit == null) unlit = Shader.Find("Sprites/Default");
                }
                return unlit;
            }
        }

        public static void SetColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        public static void SetTexture(Material m, Texture t, Vector2 tiling)
        {
            if (m.HasProperty("_BaseMap")) { m.SetTexture("_BaseMap", t); m.SetTextureScale("_BaseMap", tiling); }
            if (m.HasProperty("_MainTex")) { m.SetTexture("_MainTex", t); m.SetTextureScale("_MainTex", tiling); }
        }

        public static void SetSmoothness(Material m, float s)
        {
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", s);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", s);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        }

        public static Material Color(Color c, float smooth = 0.1f)
        {
            string key = "c" + ColorUtility.ToHtmlStringRGBA(c) + smooth.ToString("F2");
            Material m;
            if (cache.TryGetValue(key, out m) && m != null) return m;
            m = new Material(Lit) { name = "HR_" + key, enableInstancing = true };
            SetColor(m, c);
            SetSmoothness(m, smooth);
            cache[key] = m;
            return m;
        }

        public static Material Textured(string key, Texture2D tex, Color tint, float smooth = 0.1f)
        {
            Material m;
            if (cache.TryGetValue(key, out m) && m != null) return m;
            m = new Material(Lit) { name = "HR_" + key, enableInstancing = true };
            SetTexture(m, tex, Vector2.one);
            SetColor(m, tint);
            SetSmoothness(m, smooth);
            cache[key] = m;
            return m;
        }

        public static Material UnlitColor(Color c)
        {
            string key = "u" + ColorUtility.ToHtmlStringRGBA(c);
            Material m;
            if (cache.TryGetValue(key, out m) && m != null) return m;
            m = new Material(Unlit) { name = "HR_" + key };
            SetColor(m, c);
            cache[key] = m;
            return m;
        }

        public static Material Emissive(Color c, float intensity = 1.5f)
        {
            string key = "e" + ColorUtility.ToHtmlStringRGBA(c) + intensity.ToString("F2");
            Material m;
            if (cache.TryGetValue(key, out m) && m != null) return m;
            m = new Material(Lit) { name = "HR_" + key, enableInstancing = true };
            SetColor(m, c);
            m.EnableKeyword("_EMISSION");
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", c * intensity);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            cache[key] = m;
            return m;
        }

        /// <summary>Material con brillo metálico (cromados, llantas, cristales).</summary>
        public static Material Metal(Color c, float smooth, float metallic)
        {
            string key = "m" + ColorUtility.ToHtmlStringRGBA(c) + smooth.ToString("F2") + metallic.ToString("F2");
            Material m;
            if (cache.TryGetValue(key, out m) && m != null) return m;
            m = new Material(Lit) { name = "HR_" + key, enableInstancing = true };
            SetColor(m, c);
            SetSmoothness(m, smooth);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            cache[key] = m;
            return m;
        }
    }

    /// <summary>Texturas generadas en código (no hace falta importar assets).</summary>
    public static class Tex
    {
        static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();

        static Texture2D New(string key, int w, int h, FilterMode fm, TextureWrapMode wrap)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "HR_" + key, filterMode = fm, wrapMode = wrap };
            t.anisoLevel = 4;
            cache[key] = t;
            return t;
        }

        public static Texture2D Stripes(Color a, Color b, int n = 2)
        {
            string key = "stripes" + ColorUtility.ToHtmlStringRGB(a) + ColorUtility.ToHtmlStringRGB(b) + n;
            Texture2D t;
            if (cache.TryGetValue(key, out t)) return t;
            t = New(key, 8 * n, 4, FilterMode.Point, TextureWrapMode.Repeat);
            for (int x = 0; x < 8 * n; x++)
                for (int y = 0; y < 4; y++)
                    t.SetPixel(x, y, (x / 8) % 2 == 0 ? a : b);
            t.Apply();
            return t;
        }

        public static Texture2D Checker()
        {
            Texture2D t;
            if (cache.TryGetValue("checker", out t)) return t;
            t = New("checker", 16, 16, FilterMode.Point, TextureWrapMode.Repeat);
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 16; y++)
                    t.SetPixel(x, y, ((x / 4) + (y / 4)) % 2 == 0 ? UnityEngine.Color.white : new Color(0.08f, 0.08f, 0.08f));
            t.Apply();
            return t;
        }

        public static Texture2D Dashes()
        {
            Texture2D t;
            if (cache.TryGetValue("dash", out t)) return t;
            t = New("dash", 4, 32, FilterMode.Bilinear, TextureWrapMode.Repeat);
            for (int x = 0; x < 4; x++)
                for (int y = 0; y < 32; y++)
                    t.SetPixel(x, y, y < 14 ? UnityEngine.Color.white : new Color(1, 1, 1, 0));
            t.Apply();
            return t;
        }

        /// <summary>Fachada en gris claro con ventanas (se tiñe con el color del material).</summary>
        public static Texture2D Facade()
        {
            Texture2D t;
            if (cache.TryGetValue("facade", out t)) return t;
            const int W = 32, H = 32;
            t = New("facade", W, H, FilterMode.Bilinear, TextureWrapMode.Repeat);
            var wall = new Color(0.95f, 0.95f, 0.95f);
            var glass = new Color(0.32f, 0.38f, 0.45f);
            var frame = new Color(0.8f, 0.8f, 0.8f);
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    bool win = x >= 9 && x < 23 && y >= 8 && y < 25;
                    bool fr = win && (x == 9 || x == 22 || y == 8 || y == 24 || x == 16);
                    t.SetPixel(x, y, fr ? frame : (win ? glass : wall));
                }
            t.Apply();
            return t;
        }

        public static Texture2D Noise(string key, Color baseCol, float amount, int size = 64, int seed = 1)
        {
            Texture2D t;
            if (cache.TryGetValue("n" + key, out t)) return t;
            t = New("n" + key, size, size, FilterMode.Bilinear, TextureWrapMode.Repeat);
            var rnd = new System.Random(seed);
            for (int x = 0; x < size; x++)
                for (int y = 0; y < size; y++)
                {
                    float n = (float)rnd.NextDouble() * 2f - 1f;
                    float p = Mathf.PerlinNoise(x * 0.11f + seed, y * 0.11f) * 2f - 1f;
                    float k = 1f + amount * (0.6f * p + 0.4f * n);
                    t.SetPixel(x, y, new Color(baseCol.r * k, baseCol.g * k, baseCol.b * k, 1));
                }
            t.Apply();
            return t;
        }

        public static Texture2D Solid(Color c)
        {
            string key = "s" + ColorUtility.ToHtmlStringRGBA(c);
            Texture2D t;
            if (cache.TryGetValue(key, out t)) return t;
            t = New(key, 2, 2, FilterMode.Point, TextureWrapMode.Clamp);
            for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) t.SetPixel(x, y, c);
            t.Apply();
            return t;
        }
    }
}
