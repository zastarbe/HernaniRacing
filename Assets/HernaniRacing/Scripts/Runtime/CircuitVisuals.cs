using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Elementos del circuito: línea de salida/meta y pórtico. Sin vallas ni bordillos:
    /// las calles laterales quedan abiertas y nada impide el paso de los coches.
    /// </summary>
    public static class CircuitVisuals
    {
        public static GameObject Build(Circuit c)
        {
            var root = new GameObject("Circuito");
            BuildStart(c, root.transform);
            return root;
        }

        /// <summary>Altura de la superficie de rodadura (terreno o tablero de puente) bajo p.</summary>
        static float SurfaceY(Vector3 p)
        {
            RaycastHit hit;
            if (Physics.Raycast(p + Vector3.up * 2.5f, Vector3.down, out hit, 8f, ~0, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            return p.y;
        }

        static void BuildStart(Circuit c, Transform parent)
        {
            Vector3 p = c.raw[0];
            Vector3 d = c.raw[1] - c.raw[c.raw.Length - 1]; d.y = 0; d.Normalize();
            p.y = SurfaceY(p);
            float hw = c.width[0] * 0.5f;
            var mb = new MeshBuilder(1);
            Vector3 rgt = new Vector3(d.z, 0, -d.x);
            const int cols = 9;
            var P = new Vector3[2, cols];
            var UV = new Vector2[2, cols];
            for (int r = 0; r < 2; r++)
                for (int k = 0; k < cols; k++)
                {
                    float x = Mathf.Lerp(-hw, hw, k / (float)(cols - 1));
                    Vector3 q = p + d * (r == 0 ? -0.8f : 0.8f) + rgt * x;
                    q.y = SurfaceY(q) + 0.09f;
                    P[r, k] = q;
                    UV[r, k] = new Vector2(r * 0.5f, x / hw);   // mismas proporciones de damero que antes
                }
            mb.Grid(0, P, UV);
            var lineMat = Mat.Textured("checker", Tex.Checker(), Color.white, 0.2f);
            MeshBuilder.Spawn("Linea de meta", mb.Build("startline"), new[] { lineMat }, parent, false);

            // pórtico
            Vector3 right = new Vector3(d.z, 0, -d.x);
            var gantry = new GameObject("Portico salida");
            gantry.transform.SetParent(parent, false);
            var dark = Mat.Color(new Color(0.15f, 0.15f, 0.17f), 0.3f);
            var red = Mat.Color(new Color(0.75f, 0.08f, 0.1f), 0.4f);
            float off = hw + 1.0f;
            for (int s = -1; s <= 1; s += 2)
            {
                var pil = GameObject.CreatePrimitive(PrimitiveType.Cube);
                pil.transform.SetParent(gantry.transform, false);
                pil.transform.position = p + right * (s * off) + Vector3.up * 3f;
                pil.transform.rotation = Quaternion.LookRotation(d);
                pil.transform.localScale = new Vector3(0.5f, 6f, 0.5f);
                pil.GetComponent<MeshRenderer>().sharedMaterial = dark;
                Object.Destroy(pil.GetComponent<Collider>());   // nada bloquea el paso
            }
            var beam = GameObject.CreatePrimitive(PrimitiveType.Cube);
            beam.transform.SetParent(gantry.transform, false);
            beam.transform.position = p + Vector3.up * 5.6f;
            beam.transform.rotation = Quaternion.LookRotation(d);
            beam.transform.localScale = new Vector3(off * 2f + 0.5f, 1.3f, 0.4f);
            beam.GetComponent<MeshRenderer>().sharedMaterial = red;
            Object.Destroy(beam.GetComponent<Collider>());

            var font = BuiltinFont();
            if (font != null)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    var tgo = new GameObject("Texto");
                    tgo.transform.SetParent(gantry.transform, false);
                    tgo.transform.position = p + Vector3.up * 5.6f + d * (side * 0.22f);
                    tgo.transform.rotation = Quaternion.LookRotation(side < 0 ? d : -d);
                    var tm = tgo.AddComponent<TextMesh>();
                    tm.text = "HERNANI GP";
                    tm.font = font;
                    tm.fontSize = 64;
                    tm.characterSize = 0.06f;
                    tm.anchor = TextAnchor.MiddleCenter;
                    tm.alignment = TextAlignment.Center;
                    tm.color = Color.white;
                    tgo.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                }
            }
        }

        public static Font BuiltinFont()
        {
            Font f = null;
#if UNITY_2022_2_OR_NEWER
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
#endif
            if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { } }
            return f;
        }
    }
}
