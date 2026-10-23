using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// Lo que el jugador ha elegido en los menús (coche, color, circuito, carrera, tráfico y salida del paseo).
    /// Lo comparten todas las pantallas y se guarda en el PC (PlayerPrefs) para la próxima vez.
    /// </summary>
    public class MenuState
    {
        public const int MaxOpponents = 8;
        public readonly RaceSettings settings = new RaceSettings();
        public readonly List<int> sel = new List<int>();   // cruces del circuito (1..4)
        public Circuit circuit;
        public string info;                                // error del circuito, si lo hay
        public string presetName = "";
        public int freeStart = -1;                         // cruce de salida del paseo libre (-1 = centro de Hernani)
        public int version;                                // cambia cada vez que cambia el circuito
        World world;

        public static readonly Color[] PaintChoices =
        {
            new Color(0.85f, 0.08f, 0.10f), new Color(0.10f, 0.30f, 0.85f), new Color(0.95f, 0.95f, 0.95f), new Color(0.10f, 0.10f, 0.12f),
            new Color(1.00f, 0.75f, 0.10f), new Color(0.10f, 0.60f, 0.25f), new Color(0.98f, 0.45f, 0.05f), new Color(0.50f, 0.18f, 0.72f),
            new Color(0.95f, 0.40f, 0.65f), new Color(0.10f, 0.72f, 0.80f), new Color(0.62f, 0.64f, 0.68f), new Color(0.45f, 0.05f, 0.10f)
        };

        public bool CircuitOk { get { return circuit != null && circuit.error == null; } }

        public void Init(World w)
        {
            world = w;
            Load();
        }

        // ------------------------------------------------------------------ circuito
        public void Rebuild()
        {
            circuit = null;
            info = null;
            if (sel.Count == 4)
            {
                circuit = Circuit.Build(world.graph, sel.ToArray());
                if (circuit.error != null) info = circuit.error;
            }
            version++;
        }

        public void SetNodes(IEnumerable<int> nodes, string name)
        {
            sel.Clear();
            foreach (int n in nodes)
                if (n >= 0 && n < world.graph.nodes.Length && !sel.Contains(n) && sel.Count < 4) sel.Add(n);
            presetName = name ?? "";
            Rebuild();
        }

        public void ApplyPreset(PresetData p)
        {
            var nodes = new List<int>();
            for (int i = 0; i + 1 < p.points.Length && nodes.Count < 4; i += 2)
            {
                int n = world.graph.NearestJunction(new Vector3(p.points[i], 0, p.points[i + 1]));
                if (n >= 0 && !nodes.Contains(n)) nodes.Add(n);
            }
            SetNodes(nodes, p.name);
        }

        public PresetData[] Presets { get { return world.data.meta.presets ?? new PresetData[0]; } }

        public string CircuitName
        {
            get
            {
                if (!CircuitOk) return sel.Count < 4 ? "Sin circuito" : "Circuito no válido";
                return string.IsNullOrEmpty(presetName) ? "Circuito a tu medida" : presetName;
            }
        }

        /// <summary>Barrios por los que pasa el circuito (para el resumen).</summary>
        public string CircuitZones(int max = 4)
        {
            if (!CircuitOk) return "";
            var names = new List<string>();
            foreach (int n in sel)
            {
                var z = world.ZoneAt(world.graph.nodes[n]);
                if (z != null && !names.Contains(z.name)) names.Add(z.name);
            }
            return string.Join(" · ", names.Take(max).ToArray());
        }

        public Vector3 FreeStartPos
        {
            get { return freeStart >= 0 && freeStart < world.graph.nodes.Length ? world.graph.nodes[freeStart] : world.TownCenter; }
        }

        public string FreeStartLabel
        {
            get
            {
                if (freeStart < 0 || freeStart >= world.graph.nodes.Length) return "Centro de Hernani";
                var z = world.ZoneAt(world.graph.nodes[freeStart]);
                return world.graph.JunctionLabel(freeStart) + (z != null ? " (" + z.name + ")" : "");
            }
        }

        // ------------------------------------------------------------------ guardar / cargar
        public void Load()
        {
            var s = settings;
            s.carId = PlayerPrefs.GetString("menu.car", PlayerPrefs.GetString("net.car", s.carId));
            if (CarModels.Get(s.carId) == null)
            {
                var list = CarModels.Selectable;
                s.carId = list.Count > 0 ? list[0].id : "turismo";
            }
            if (PlayerPrefs.HasKey("menu.paint")) s.paint = NetSession.IntToColor(PlayerPrefs.GetInt("menu.paint"));
            s.laps = Mathf.Clamp(PlayerPrefs.GetInt("menu.laps", s.laps), 1, 20);
            s.opponents = Mathf.Clamp(PlayerPrefs.GetInt("menu.opp", s.opponents), 0, MaxOpponents);
            s.difficulty = Mathf.Clamp(PlayerPrefs.GetInt("menu.diff", s.difficulty), 0, 2);
            s.traffic = PlayerPrefs.GetInt("menu.traffic", s.traffic ? 1 : 0) == 1;
            s.pedestrians = PlayerPrefs.GetInt("menu.peds", s.pedestrians ? 1 : 0) == 1;
            s.freeTraffic = PlayerPrefs.GetInt("menu.ftraffic", s.freeTraffic ? 1 : 0) == 1;
            s.freePedestrians = PlayerPrefs.GetInt("menu.fpeds", s.freePedestrians ? 1 : 0) == 1;
            s.trafficLevel = Mathf.Clamp(PlayerPrefs.GetInt("menu.trafficLv", s.traffic ? 2 : 0), 0, 5);
            s.pedLevel = Mathf.Clamp(PlayerPrefs.GetInt("menu.pedsLv", s.pedestrians ? 2 : 0), 0, 5);
            s.freeTrafficLevel = Mathf.Clamp(PlayerPrefs.GetInt("menu.ftrafficLv", s.freeTraffic ? 2 : 0), 0, 5);
            s.freePedLevel = Mathf.Clamp(PlayerPrefs.GetInt("menu.fpedsLv", s.freePedestrians ? 2 : 0), 0, 5);
            s.realTimeTraffic = PlayerPrefs.GetInt("menu.realtime", 0) == 1;
            s.traffic = s.trafficLevel > 0; s.pedestrians = s.pedLevel > 0;
            s.freeTraffic = s.freeTrafficLevel > 0; s.freePedestrians = s.freePedLevel > 0;
            freeStart = PlayerPrefs.GetInt("menu.free", -1);
            if (freeStart >= world.graph.nodes.Length) freeStart = -1;

            // circuito: el último que se usó; si no hay o ya no vale, el primero de los predefinidos
            var nodes = new List<int>();
            foreach (var part in PlayerPrefs.GetString("menu.sel", "").Split(','))
            {
                int n;
                if (int.TryParse(part, out n)) nodes.Add(n);
            }
            SetNodes(nodes, PlayerPrefs.GetString("menu.preset", ""));
            if (!CircuitOk && Presets.Length > 0) ApplyPreset(Presets[0]);
        }

        public void Save()
        {
            var s = settings;
            PlayerPrefs.SetString("menu.car", s.carId);
            PlayerPrefs.SetInt("menu.paint", NetSession.ColorToInt(s.paint));
            PlayerPrefs.SetInt("menu.laps", s.laps);
            PlayerPrefs.SetInt("menu.opp", s.opponents);
            PlayerPrefs.SetInt("menu.diff", s.difficulty);
            PlayerPrefs.SetInt("menu.traffic", s.traffic ? 1 : 0);
            PlayerPrefs.SetInt("menu.peds", s.pedestrians ? 1 : 0);
            PlayerPrefs.SetInt("menu.ftraffic", s.freeTraffic ? 1 : 0);
            PlayerPrefs.SetInt("menu.fpeds", s.freePedestrians ? 1 : 0);
            PlayerPrefs.SetInt("menu.trafficLv", s.trafficLevel);
            PlayerPrefs.SetInt("menu.pedsLv", s.pedLevel);
            PlayerPrefs.SetInt("menu.ftrafficLv", s.freeTrafficLevel);
            PlayerPrefs.SetInt("menu.fpedsLv", s.freePedLevel);
            PlayerPrefs.SetInt("menu.realtime", s.realTimeTraffic ? 1 : 0);
            PlayerPrefs.SetInt("menu.free", freeStart);
            PlayerPrefs.SetString("menu.sel", string.Join(",", sel.Select(n => n.ToString()).ToArray()));
            PlayerPrefs.SetString("menu.preset", presetName ?? "");
            PlayerPrefs.Save();
        }
    }
}

