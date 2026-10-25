using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// v16: "¿dónde estoy?" con precisión: calle (o camino / bidegorri / vía), barrio, monte, pueblo y provincia.
    /// Pueblo y barrio salen de los límites administrativos de OpenStreetMap (polígonos), el monte de las
    /// cimas de b5m/OSM, la calle del grafo viario. Se recalcula como mucho 4 veces por segundo.
    /// </summary>
    public class Toponymy
    {
        public struct Place
        {
            public string street, barrio, monte, town, province;
            public float monteDist;
            /// <summary>"Barrio · Pueblo (Provincia)" sin repetir lo que coincide.</summary>
            public string Line2()
            {
                string s = "";
                if (!string.IsNullOrEmpty(barrio) && barrio != town) s = barrio + " · ";
                s += string.IsNullOrEmpty(town) ? "Hernani" : town;
                if (!string.IsNullOrEmpty(province)) s += " (" + province + ")";
                return s;
            }
        }

        class Area
        {
            public string name, prov; public int level;
            public Vector2[][] rings;
            public Rect bounds;
        }

        readonly World world;
        readonly List<Area> towns = new List<Area>(), barrios = new List<Area>();
        Place last;
        float lastT = -10f;
        Vector3 lastP = new Vector3(1e9f, 0, 0);

        public Toponymy(World w)
        {
            world = w;
            var areas = w.data.scenery.areas;
            if (areas == null) return;
            foreach (var a in areas)
            {
                if (a == null || a.r == null || a.p == null || string.IsNullOrEmpty(a.name)) continue;
                var ar = new Area { name = a.name, prov = a.prov, level = a.level, rings = new Vector2[a.r.Length][] };
                float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
                int k = 0;
                for (int i = 0; i < a.r.Length; i++)
                {
                    var ring = new Vector2[a.r[i]];
                    for (int j = 0; j < ring.Length && k + 1 < a.p.Length; j++, k += 2)
                    {
                        ring[j] = new Vector2(a.p[k], a.p[k + 1]);
                        x0 = Mathf.Min(x0, ring[j].x); x1 = Mathf.Max(x1, ring[j].x);
                        z0 = Mathf.Min(z0, ring[j].y); z1 = Mathf.Max(z1, ring[j].y);
                    }
                    ar.rings[i] = ring;
                }
                ar.bounds = Rect.MinMaxRect(x0, z0, x1, z1);
                (a.level <= 8 ? towns : barrios).Add(ar);
            }
        }

        static bool Inside(Vector2[] ring, Vector2 p)
        {
            bool c = false;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                Vector2 a = ring[i], b = ring[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) c = !c;
            }
            return c;
        }

        static Area Find(List<Area> list, Vector2 p)
        {
            Area best = null;
            foreach (var a in list)
            {
                if (!a.bounds.Contains(p)) continue;
                bool inside = false;
                foreach (var r in a.rings) if (r.Length >= 3 && Inside(r, p)) inside = !inside;   // agujeros: paridad
                if (!inside) continue;
                if (best == null || a.level > best.level) best = a;     // el más detallado (10 dentro de 9)
            }
            return best;
        }

        /// <summary>Nombre visible del tramo: las calles con su nombre; caminos, bidegorris y vías con su tipo.</summary>
        public static string EdgeLabel(RoadEdge e)
        {
            string n = e.name ?? "";
            switch (e.kind)
            {
                case RoadEdge.KindPath:
                    return string.IsNullOrEmpty(n) || n == "Camino" || n == "Sendero" ? "Camino de monte" : (n.StartsWith("Camino") || n.StartsWith("Sendero") || n.StartsWith("Bide") ? n : "Camino · " + n);
                case RoadEdge.KindBike:
                    return string.IsNullOrEmpty(n) || n == "Bidegorri" ? "Bidegorri" : (n.StartsWith("Bidegorri") ? n : "Bidegorri · " + n);
                case RoadEdge.KindRail:
                    return string.IsNullOrEmpty(n) || n == "Tren" || n == "Ferrocarril" ? "Vía del tren" : (n.StartsWith("Vía") || n.StartsWith("Línea") ? n : "Vía del tren · " + n);
                default:
                    return string.IsNullOrEmpty(n) ? (e.type == "Carretera" ? "Carretera" : "Calle sin nombre") : n;
            }
        }

        /// <summary>Lugar en p. 'edge' es el tramo más cercano de cualquier tipo (o -1).</summary>
        public Place At(Vector3 p, int edge)
        {
            if (Time.unscaledTime - lastT < 0.25f && (p - lastP).sqrMagnitude < 25f && last.street != null) return last;
            lastT = Time.unscaledTime; lastP = p;
            var g = world.graph;
            var pl = new Place();
            pl.street = edge >= 0 ? EdgeLabel(g.edges[edge]) : "Fuera de la red viaria";
            var q = new Vector2(p.x, p.z);
            var t = Find(towns, q);
            var b = Find(barrios, q);
            pl.town = t != null ? t.name : "";
            pl.province = t != null && !string.IsNullOrEmpty(t.prov) ? t.prov : (b != null ? b.prov : "");
            if (b != null) pl.barrio = b.name;
            else
            {
                var z = world.ZoneAt(p);           // barrios de b5m (puntos): el más cercano
                if (z != null && (t == null || t.name == "Hernani")) pl.barrio = z.name;
            }
            if (string.IsNullOrEmpty(pl.town) && towns.Count == 0) { pl.town = "Hernani"; pl.province = "Gipuzkoa"; }
            // monte: fuera de las calles, la cima más cercana (a menos de 1,2 km)
            bool urban = edge >= 0 && g.edges[edge].type == "Calle";
            var peaks = world.data.scenery.peaks;
            if (!urban && peaks != null)
            {
                float bd = 1200f * 1200f;
                foreach (var pk in peaks)
                {
                    float d = (pk.x - p.x) * (pk.x - p.x) + (pk.z - p.z) * (pk.z - p.z);
                    if (d < bd) { bd = d; pl.monte = pk.name; }
                }
                if (pl.monte != null) pl.monteDist = Mathf.Sqrt(bd);
            }
            last = pl;
            return pl;
        }

        public const int AllKinds = 0xF;
    }
}
