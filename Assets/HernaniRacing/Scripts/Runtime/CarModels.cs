using System;
using System.Collections.Generic;
using UnityEngine;

namespace HernaniRacing
{
    // ------------------------------------------------------------------
    //  Coches propios generados por código (DataPipeline/build_cars.py, CC0) en
    //  Resources/HernaniCars/<id>.json (coordenadas Unity, metros, origen en el suelo, morro hacia +Z).
    //  Cada submalla lleva su tipo de material (kind: paint, glass, chrome, light...). Hay un modelo
    //  detallado y otro ligero (LOD) que Unity usa automáticamente cuando el coche se ve pequeño.
    // ------------------------------------------------------------------
    [Serializable] public class CarSub { public float[] color; public int paint; public string kind; public float smooth, metal; public int[] t; }
    [Serializable] public class CarPart { public float[] v, n; public CarSub[] sub; }

    [Serializable]
    public class CarModelData
    {
        public string id, name, category, source, description;
        public bool selectable;
        public float[] size, boundsMin, boundsMax;
        public float wheelRadius;
        public float[] wheels;            // FL, FR, RL, RR: centro de cada rueda (x,y,z)
        public CarPart body, wheelL, wheelR;
        public CarPart bodyLod, wheelLodL, wheelLodR;   // versión ligera (puede faltar)
        public float[] paintColor;
        public float trafficWeight;
        public float mass, maxSpeed, engine, brake, grip, downforce;
        public bool IsF1 { get { return category == "F1"; } }
    }

    [Serializable] public class CarIndexEntry { public string id, name, category, description; public bool selectable; public float trafficWeight; }
    [Serializable] public class CarIndex { public CarIndexEntry[] cars; public string credits; }

    public static class CarModels
    {
        const string Folder = "HernaniCars/";
        static CarIndex index;
        static readonly Dictionary<string, CarModelData> data = new Dictionary<string, CarModelData>();
        static readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();

        public static CarIndex Index
        {
            get
            {
                if (index == null)
                {
                    var ta = Resources.Load<TextAsset>(Folder + "cars");
                    index = ta != null ? JsonUtility.FromJson<CarIndex>(ta.text) : new CarIndex { cars = new CarIndexEntry[0], credits = "" };
                }
                return index;
            }
        }

        public static List<CarIndexEntry> Selectable
        {
            get
            {
                var l = new List<CarIndexEntry>();
                foreach (var c in Index.cars) if (c.selectable) l.Add(c);
                return l;
            }
        }

        static List<string> trafficIds;
        static float[] trafficCum;

        /// <summary>Modelos que salen en el tráfico (trafficWeight > 0), en el orden del índice.
        /// El índice dentro de esta lista viaja por la red (TrafficState.model).</summary>
        public static List<string> TrafficIds
        {
            get
            {
                if (trafficIds == null)
                {
                    trafficIds = new List<string>();
                    var w = new List<float>();
                    foreach (var c in Index.cars)
                        if (c.trafficWeight > 0f) { trafficIds.Add(c.id); w.Add(c.trafficWeight); }
                    if (trafficIds.Count == 0)          // índice antiguo sin pesos: todos menos los F1
                        foreach (var c in Index.cars) if (c.category != "F1") { trafficIds.Add(c.id); w.Add(1f); }
                    trafficCum = new float[w.Count];
                    float acc = 0f;
                    for (int i = 0; i < w.Count; i++) { acc += w[i]; trafficCum[i] = acc; }
                }
                return new List<string>(trafficIds);
            }
        }

        /// <summary>Modelo de tráfico al azar según su frecuencia real (muchos compactos, pocos superdeportivos).</summary>
        public static string PickTraffic(System.Random rnd)
        {
            var ids = TrafficIds;
            if (ids.Count == 0) return null;
            float r = (float)rnd.NextDouble() * trafficCum[trafficCum.Length - 1];
            for (int i = 0; i < trafficCum.Length; i++) if (r <= trafficCum[i]) return ids[i];
            return ids[ids.Count - 1];
        }

        /// <summary>Coches con librea propia (no se repintan en el tráfico).</summary>
        public static bool FixedLivery(string id)
        {
            return id == "taxi" || id == "policia" || id == "ambulancia" || id == "reparto";
        }

        public static CarModelData Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            CarModelData d;
            if (data.TryGetValue(id, out d)) return d;
            var ta = Resources.Load<TextAsset>(Folder + id);
            d = ta != null ? JsonUtility.FromJson<CarModelData>(ta.text) : null;
            data[id] = d;
            return d;
        }

        static Mesh PartMesh(string key, CarPart p)
        {
            Mesh m;
            if (meshes.TryGetValue(key, out m) && m != null) return m;
            int nv = p.v.Length / 3;
            var v = new Vector3[nv];
            var n = new Vector3[nv];
            for (int i = 0; i < nv; i++)
            {
                v[i] = new Vector3(p.v[i * 3], p.v[i * 3 + 1], p.v[i * 3 + 2]);
                n[i] = new Vector3(p.n[i * 3], p.n[i * 3 + 1], p.n[i * 3 + 2]);
            }
            m = new Mesh { name = "car_" + key };
            m.vertices = v;
            m.normals = n;
            m.subMeshCount = p.sub.Length;
            for (int s = 0; s < p.sub.Length; s++) m.SetTriangles(p.sub[s].t, s, false);
            m.RecalculateBounds();
            meshes[key] = m;
            return m;
        }

        static Material[] PartMaterials(CarPart p, Color paint)
        {
            var mats = new Material[p.sub.Length];
            for (int s = 0; s < p.sub.Length; s++)
            {
                var sb = p.sub[s];
                Color c = new Color(sb.color[0], sb.color[1], sb.color[2]);
                if (sb.paint != 0) { mats[s] = Mat.Color(paint, 0.72f); continue; }
                if (!string.IsNullOrEmpty(sb.kind))
                {
                    switch (sb.kind)
                    {
                        case "light": mats[s] = Mat.Emissive(c, 1.2f); break;
                        case "tail": mats[s] = Mat.Emissive(c, 0.6f); break;
                        case "amber": mats[s] = Mat.Emissive(c, 0.5f); break;
                        case "beacon": mats[s] = Mat.Emissive(c, 1.6f); break;
                        case "glass": mats[s] = Mat.Metal(c, 0.95f, 0.25f); break;
                        default:
                            mats[s] = sb.metal > 0.01f ? Mat.Metal(c, sb.smooth, sb.metal) : Mat.Color(c, sb.smooth);
                            break;
                    }
                    continue;
                }
                float sat = Mathf.Max(c.r, c.g, c.b) - Mathf.Min(c.r, c.g, c.b);
                bool glass = c.b > 0.75f && c.b >= c.r && sat < 0.35f && c.g > 0.7f;
                bool light = c.r > 0.9f && c.g > 0.65f && c.b < 0.4f;          // faros ámbar/amarillos
                if (light) mats[s] = Mat.Emissive(c);
                else mats[s] = Mat.Color(glass ? new Color(0.35f, 0.45f, 0.55f) : c, glass ? 0.9f : 0.35f);
            }
            return mats;
        }

        /// <summary>
        /// Crea un coche con su modelo, colisionadores y físicas propias. Si el modelo no existe se usa el
        /// coche low poly generado en código.
        /// </summary>
        public static CarController Create(string id, string goName, Color paint, bool isPlayer)
        {
            var d = Get(id);
            if (d == null || d.body == null) return LowPolyCar.Create(goName, paint, isPlayer);

            var go = new GameObject(goName);
            go.AddComponent<Rigidbody>();
            var car = go.AddComponent<CarController>();
            car.model = d;

            var lod0 = new List<Renderer>();
            var lod1 = new List<Renderer>();
            lod0.Add(AddPart(go.transform, "Carroceria", d.id + "_body", d.body, paint));
            if (d.bodyLod != null && d.bodyLod.v != null && d.bodyLod.v.Length > 0)
                lod1.Add(AddPart(go.transform, "CarroceriaLod", d.id + "_bodylod", d.bodyLod, paint));

            // colisionadores: bajos de la carrocería + habitáculo (en los F1 sólo el monocasco)
            Vector3 lo = new Vector3(d.boundsMin[0], d.boundsMin[1], d.boundsMin[2]);
            Vector3 hi = new Vector3(d.boundsMax[0], d.boundsMax[1], d.boundsMax[2]);
            float h = hi.y - lo.y;
            float y0 = Mathf.Max(lo.y, 0.22f);
            var pm = PhysicsMat();
            var c1 = go.AddComponent<BoxCollider>();
            float yTop1 = d.IsF1 ? hi.y * 0.8f : lo.y + h * 0.55f;
            c1.center = new Vector3((lo.x + hi.x) * 0.5f, (y0 + yTop1) * 0.5f, (lo.z + hi.z) * 0.5f);
            c1.size = new Vector3((hi.x - lo.x) * 0.95f, Mathf.Max(0.2f, yTop1 - y0), (hi.z - lo.z) * 0.97f);
            c1.sharedMaterial = pm;
            if (!d.IsF1)
            {
                var c2 = go.AddComponent<BoxCollider>();
                c2.center = new Vector3(c1.center.x, (yTop1 + hi.y * 0.97f) * 0.5f, c1.center.z - (hi.z - lo.z) * 0.05f);
                c2.size = new Vector3((hi.x - lo.x) * 0.78f, Mathf.Max(0.2f, hi.y * 0.97f - yTop1), (hi.z - lo.z) * 0.6f);
                c2.sharedMaterial = pm;
            }

            // físicas propias del modelo (rivales con el mismo modelo = mismas físicas)
            car.ApplyModel(d);

            // ruedas
            string[] wn = { "Rueda_DI", "Rueda_DD", "Rueda_TI", "Rueda_TD" };
            for (int i = 0; i < 4; i++)
            {
                Vector3 c = new Vector3(d.wheels[i * 3], d.wheels[i * 3 + 1], d.wheels[i * 3 + 2]);
                car.wheelAnchors[i] = c + Vector3.up * car.StaticWheelOffset;
                var w = new GameObject(wn[i]);
                w.transform.SetParent(go.transform, false);
                w.transform.localPosition = c;
                AddWheel(d, w.transform, c.x < 0, paint, lod0, lod1);
                car.wheelVisuals[i] = w.transform;
            }
            SetupLod(go, lod0, lod1);
            if (isPlayer) go.AddComponent<EngineAudio>().car = car;
            return car;
        }

        /// <summary>Sólo el aspecto (para peatones/escaparate): sin físicas.</summary>
        public static GameObject CreateVisual(string id, Color paint, Transform parent, bool noLod = false)
        {
            var d = Get(id);
            var go = new GameObject("Coche_" + id);
            go.transform.SetParent(parent, false);
            if (d == null) return go;
            var lod0 = new List<Renderer>();
            var lod1 = new List<Renderer>();
            lod0.Add(AddPart(go.transform, "Carroceria", d.id + "_body", d.body, paint));
            if (d.bodyLod != null && d.bodyLod.v != null && d.bodyLod.v.Length > 0)
                lod1.Add(AddPart(go.transform, "CarroceriaLod", d.id + "_bodylod", d.bodyLod, paint));
            for (int i = 0; i < 4; i++)
            {
                Vector3 c = new Vector3(d.wheels[i * 3], d.wheels[i * 3 + 1], d.wheels[i * 3 + 2]);
                var w = new GameObject("Rueda" + i);
                w.transform.SetParent(go.transform, false);
                w.transform.localPosition = c;
                AddWheel(d, w.transform, c.x < 0, paint, lod0, lod1);
            }
            if (!noLod) SetupLod(go, lod0, lod1);
            else foreach (var r in lod1) r.gameObject.SetActive(false);
            return go;
        }

        static Renderer AddPart(Transform parent, string name, string key, CarPart p, Color paint)
        {
            var g = new GameObject(name);
            g.transform.SetParent(parent, false);
            g.AddComponent<MeshFilter>().sharedMesh = PartMesh(key, p);
            var r = g.AddComponent<MeshRenderer>();
            r.sharedMaterials = PartMaterials(p, paint);
            return r;
        }

        static void AddWheel(CarModelData d, Transform w, bool left, Color paint, List<Renderer> lod0, List<Renderer> lod1)
        {
            lod0.Add(AddPart(w, "mesh", d.id + (left ? "_wl" : "_wr"), left ? d.wheelL : d.wheelR, paint));
            var pl = left ? d.wheelLodL : d.wheelLodR;
            if (pl != null && pl.v != null && pl.v.Length > 0)
                lod1.Add(AddPart(w, "lod", d.id + (left ? "_wll" : "_wlr"), pl, paint));
        }

        /// <summary>Modelo detallado de cerca y ligero de lejos (ahorra muchos triángulos con tráfico denso).</summary>
        static void SetupLod(GameObject go, List<Renderer> lod0, List<Renderer> lod1)
        {
            if (lod1.Count == 0) return;
            var g = go.AddComponent<LODGroup>();
            g.SetLODs(new[] { new LOD(0.09f, lod0.ToArray()), new LOD(0.004f, lod1.ToArray()) });
            g.fadeMode = LODFadeMode.None;
            g.RecalculateBounds();
        }

#if UNITY_6000_0_OR_NEWER
        static PhysicsMaterial pmat;
        static PhysicsMaterial PhysicsMat()
        {
            if (pmat == null) pmat = new PhysicsMaterial("car") { dynamicFriction = 0.15f, staticFriction = 0.15f, bounciness = 0.1f, frictionCombine = PhysicsMaterialCombine.Minimum };
            return pmat;
        }
#else
        static PhysicMaterial pmat;
        static PhysicMaterial PhysicsMat()
        {
            if (pmat == null) pmat = new PhysicMaterial("car") { dynamicFriction = 0.15f, staticFriction = 0.15f, bounciness = 0.1f, frictionCombine = PhysicMaterialCombine.Minimum };
            return pmat;
        }
#endif

        public static Color DefaultPaint(string id)
        {
            var d = Get(id);
            return d != null && d.paintColor != null && d.paintColor.Length >= 3
                ? new Color(d.paintColor[0], d.paintColor[1], d.paintColor[2]) : RaceManager.PlayerColor;
        }
    }
}
