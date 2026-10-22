using System;
using UnityEngine;

namespace HernaniRacing
{
    // ------------------------------------------------------------------
    //  Estructuras que reflejan los JSON generados por DataPipeline/build_hernani_data.py
    //  Coordenadas locales: x = E - originE, z = N - originN, y = altitud (m)
    // ------------------------------------------------------------------
    [Serializable] public class PresetData { public string name; public float length; public float[] points; }

    [Serializable]
    public class MetaData
    {
        public int version;
        public string generated;
        public string crs;
        public double originE, originN;
        public float size;                 // lado mayor (compatibilidad)
        public float sizeX, sizeZ;         // rectángulo en metros (x = este, z = norte)
        public int gridX, gridZ;           // muestras del heightmap por eje
        public float cell;                 // separación entre muestras (m)
        public int tileCells;              // celdas por tesela de terreno de Unity (512 -> 513² muestras)
        public int heightmapRes;           // formato antiguo (cuadrado sin comprimir)
        public float heightMin, heightRange;
        public string dtmSource;
        public string[] attribution;
        public PresetData[] presets;
    }

    [Serializable] public class NodeData { public float x, y, z; }

    [Serializable]
    public class EdgeData
    {
        public int a, b;
        public string name, type;
        public float width, length;
        public float[] p;   // x,y,z,x,y,z...
        public int[] f;     // 0 = a nivel, 1 = puente, 2 = túnel, 3 = paso cubierto
        public float[] o;   // por cada tramo de túnel: media anchura exterior izquierda y derecha del cajón (m)
        public int ow;      // sentido (OpenStreetMap): 0 doble sentido, 1 sólo a->b, -1 sólo b->a
        public int lanes;   // carriles por sentido
        public int rb;      // 1 = forma parte de una rotonda
    }

    /// <summary>Señal vertical en la entrada de un tramo a un cruce (OSM). t: 1 STOP, 2 CEDA EL PASO.</summary>
    [Serializable] public class SignData { public int t, e, end; }

    /// <summary>Paso de peatones sobre un tramo, a s metros desde su nodo a. sig = 1 si tiene semáforo.</summary>
    [Serializable] public class CrossingData { public int e; public float s; public int sig; }

    [Serializable]
    public class RoadsData
    {
        public NodeData[] nodes;
        public EdgeData[] edges;
        public int[] holes;   // celdas del terreno abiertas en las bocas de túnel: índice z * (res - 1) + x
        public int[] nsig;    // por nodo: 0 nada, 1 cruce con semáforos, 2 rotonda
        public SignData[] signs;
        public CrossingData[] crossings;
    }

    [Serializable]
    public class BuildingData
    {
        public float[] v;   // x,z pares (anillo exterior + patios)
        public int[] r;     // nº de vértices de cada anillo
        public int[] t;     // triángulos del tejado (índices sobre v)
        public float b;     // cota base (m)
        public float h;     // cota de cubierta (m)
        public float l;     // elevación de planta baja (soportales)
        public int k;       // 0 = edificio, 1 = otra estructura cubierta
        public string n;    // nombre del edificio (si existe)
        public string a;    // dirección postal
        public int y;       // año de construcción (0 = desconocido)
    }

    [Serializable] public class BuildingsData { public BuildingData[] buildings; }

    [Serializable]
    public class ZoneData
    {
        public string name, full, type;
        public float x, y, z;
        public bool primary, district;
    }

    [Serializable] public class LandmarkData { public string name, type; public float x, y, z; }
    [Serializable] public class RiverData { public string name; public float width; public float[] p; }

    [Serializable]
    public class SceneryData
    {
        public ZoneData[] zones;
        public LandmarkData[] landmarks;
        public RiverData[] rivers;
        public float[] trees;   // x,z,y,escala,tipo ...
    }

    public class HernaniDataSet
    {
        public const string Folder = "HernaniData/";
        public MetaData meta;
        public RoadsData roads;
        public BuildingsData buildings;
        public SceneryData scenery;
        public float[,] heights01;   // [z, x] normalizado 0..1 (listo para TerrainData.SetHeights)

        static T LoadJson<T>(string name)
        {
            var ta = Resources.Load<TextAsset>(Folder + name);
            if (ta == null)
                throw new Exception("No se encuentra Resources/" + Folder + name +
                                    ". Ejecuta DataPipeline/build_hernani_data.py o copia la carpeta HernaniData.");
            return JsonUtility.FromJson<T>(ta.text);
        }

        public static HernaniDataSet Load()
        {
            var d = new HernaniDataSet();
            d.meta = LoadJson<MetaData>("meta");
            d.roads = LoadJson<RoadsData>("roads");
            d.buildings = LoadJson<BuildingsData>("buildings");
            d.scenery = LoadJson<SceneryData>("scenery");

            var hm = Resources.Load<TextAsset>(Folder + "heightmap");
            if (hm == null) throw new Exception("Falta Resources/" + Folder + "heightmap.bytes");
            byte[] bytes = hm.bytes;
            var m = d.meta;
            if (bytes.Length > 12 && bytes[0] == 'H' && bytes[1] == 'M' && bytes[2] == 'Z' && bytes[3] == '1')
                d.heights01 = DecodeHMZ(bytes, out m.gridX, out m.gridZ);
            else
            {
                // formato antiguo: uint16 LE sin comprimir, cuadrado heightmapRes²
                int res = m.heightmapRes;
                if (bytes.Length != res * res * 2)
                    throw new Exception("heightmap.bytes tiene un tamaño inesperado (" + bytes.Length + ")");
                d.heights01 = new float[res, res];
                int k = 0;
                for (int z = 0; z < res; z++)
                    for (int x = 0; x < res; x++, k += 2)
                        d.heights01[z, x] = (bytes[k] | (bytes[k + 1] << 8)) / 65535f;
                m.gridX = m.gridZ = res;
            }
            Resources.UnloadAsset(hm);
            // metadatos del formato antiguo (área cuadrada, un solo terreno)
            if (m.sizeX <= 0) m.sizeX = m.size;
            if (m.sizeZ <= 0) m.sizeZ = m.size;
            if (m.cell <= 0) m.cell = m.sizeX / (m.gridX - 1);
            if (m.tileCells <= 0 || (m.gridX - 1) % m.tileCells != 0 || (m.gridZ - 1) % m.tileCells != 0)
                m.tileCells = Mathf.Min(m.gridX, m.gridZ) - 1;
            m.size = Mathf.Max(m.sizeX, m.sizeZ);
            return d;
        }

        /// <summary>'HMZ1' + gridX + gridZ (int32 LE) + gzip(plano de bytes bajos + plano de bytes altos) de las
        /// diferencias uint16 por filas (la primera columna respecto a la fila anterior).</summary>
        static float[,] DecodeHMZ(byte[] bytes, out int gx, out int gz)
        {
            gx = BitConverter.ToInt32(bytes, 4);
            gz = BitConverter.ToInt32(bytes, 8);
            int n = gx * gz;
            var raw = new byte[n * 2];
            using (var ms = new System.IO.MemoryStream(bytes, 12, bytes.Length - 12))
            using (var zs = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress))
            {
                int off = 0;
                while (off < raw.Length)
                {
                    int r = zs.Read(raw, off, raw.Length - off);
                    if (r <= 0) throw new Exception("heightmap.bytes truncado (" + off + " de " + raw.Length + " bytes)");
                    off += r;
                }
            }
            var h = new float[gz, gx];
            const float inv = 1f / 65535f;
            int first = 0;
            for (int z = 0; z < gz; z++)
            {
                int row = z * gx;
                int v = (first + (raw[row] | (raw[n + row] << 8))) & 0xFFFF;
                first = v;
                h[z, 0] = v * inv;
                for (int x = 1; x < gx; x++)
                {
                    int i = row + x;
                    v = (v + (raw[i] | (raw[n + i] << 8))) & 0xFFFF;
                    h[z, x] = v * inv;
                }
            }
            return h;
        }

        /// <summary>Convierte coordenadas locales a UTM ETRS89 (para mostrar al jugador).</summary>
        public Vector2 ToUTM(Vector3 local)
        {
            return new Vector2((float)(meta.originE + local.x), (float)(meta.originN + local.z));
        }
    }
}
