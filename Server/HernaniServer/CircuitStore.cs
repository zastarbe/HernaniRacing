// Circuitos guardados en el servidor (los crean los jugadores online) con sus rankings:
// carreras corridas en cada uno y las mejores vueltas (una por piloto). Se guardan en un fichero JSON.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HernaniRacing.Net;

namespace HernaniRacing.Server
{
    public class CircuitStore
    {
        class FileData
        {
            public int version = 1;
            public List<CircuitEntry> circuits = new List<CircuitEntry>();
        }

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true };
        readonly string path;
        public readonly List<CircuitEntry> All = new List<CircuitEntry>();
        public Action<string> Log = Console.WriteLine;

        public CircuitStore(string file, Action<string> log)
        {
            path = file;
            if (log != null) Log = log;
            try
            {
                if (File.Exists(path))
                {
                    var d = JsonSerializer.Deserialize<FileData>(File.ReadAllText(path), Opts);
                    if (d != null && d.circuits != null)
                        foreach (var c in d.circuits)
                        {
                            if (c == null || c.nodes == null || c.nodes.Length < 2) continue;
                            if (c.laps == null) c.laps = new List<LapRecord>();
                            c.key = NetProto.CircuitKey(c.nodes, c.closed);
                            All.Add(c);
                        }
                    Log("Circuitos: " + All.Count + " en " + path);
                    if (Migrate() > 0) Save();
                }
                else Log("Circuitos: todavía no hay ninguno (se guardarán en " + path + ")");
            }
            catch (Exception e)
            {
                Log("ERROR leyendo " + path + ": " + e.Message + " -> lo guardo como .roto y empiezo vacío");
                try { File.Copy(path, path + ".roto", true); } catch { }
            }
        }

        public string FilePath { get { return path; } }

        // La firma es "fecha de generación|tramos|cruces"; la fecha identifica los datos por sí sola.
        static string Gen(string sig) { if (string.IsNullOrEmpty(sig)) return ""; int i = sig.IndexOf('|'); return i < 0 ? sig : sig.Substring(0, i); }
        static bool SameSig(CircuitEntry c, string sig) { return string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(c.dataSig) || Gen(c.dataSig) == Gen(sig); }

        class RemapTable { public string from = ""; public string to = ""; public int[] map = new int[0]; }
        class RemapFile { public List<RemapTable> tables = new List<RemapTable>(); }

        /// <summary>v16: los circuitos creados con un mapa anterior se pasan a los ids de cruce del mapa nuevo
        /// (tabla circuit_remap.json incrustada en el servidor). Conserva rankings y carreras.</summary>
        int Migrate()
        {
            RemapFile rf = null;
            try
            {
                using (var st = typeof(CircuitStore).Assembly.GetManifestResourceStream("circuit_remap.json"))
                    if (st != null) rf = JsonSerializer.Deserialize<RemapFile>(st, Opts);
            }
            catch (Exception e) { Log("AVISO: tabla de traslado de circuitos ilegible: " + e.Message); }
            if (rf == null || rf.tables == null) return 0;
            int moved = 0, lost = 0;
            foreach (var t in rf.tables)
            {
                if (t == null || t.map == null || string.IsNullOrEmpty(t.from)) continue;
                foreach (var c in All.Where(x => !string.IsNullOrEmpty(x.dataSig) && Gen(x.dataSig) == t.from).ToList())
                {
                    var nn = new int[c.nodes.Length];
                    bool ok = true;
                    for (int i = 0; i < nn.Length && ok; i++)
                    {
                        int o = c.nodes[i];
                        ok = o >= 0 && o < t.map.Length && t.map[o] >= 0;
                        if (ok) nn[i] = t.map[o];
                    }
                    if (!ok) { lost++; continue; }
                    c.nodes = nn;
                    c.dataSig = t.to + "|";
                    c.key = NetProto.CircuitKey(c.nodes, c.closed);
                    var twin = All.FirstOrDefault(x => x != c && x.key == c.key && Gen(x.dataSig) == t.to);
                    if (twin != null)
                    {
                        // ya existía en el mapa nuevo: se juntan carreras y vueltas (la mejor de cada piloto)
                        twin.races += c.races;
                        foreach (var l in c.laps)
                        {
                            var o = twin.laps.FirstOrDefault(x => string.Equals(x.nick, l.nick, StringComparison.OrdinalIgnoreCase));
                            if (o == null) twin.laps.Add(l);
                            else if (l.time < o.time) { twin.laps.Remove(o); twin.laps.Add(l); }
                        }
                        twin.laps = twin.laps.OrderBy(x => x.time).Take(NetProto.TopLaps).ToList();
                        All.Remove(c);
                    }
                    moved++;
                }
            }
            if (moved + lost > 0) Log("Circuitos trasladados al mapa nuevo: " + moved + (lost > 0 ? " (sin equivalencia: " + lost + ")" : ""));
            return moved;
        }

        public CircuitEntry Find(string key, string sig) { return All.FirstOrDefault(c => c.key == key && SameSig(c, sig)); }

        public CircuitEntry FindName(string name, string sig)
        {
            return All.FirstOrDefault(c => SameSig(c, sig) && string.Equals(c.name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Los circuitos para un jugador (misma versión de datos): los más corridos primero.</summary>
        public List<CircuitEntry> For(string sig, int max)
        {
            return All.Where(c => SameSig(c, sig)).OrderByDescending(c => c.races).ThenBy(c => c.created).Take(max).ToList();
        }

        /// <summary>Apunta una vuelta. Devuelve el puesto en el ranking (1..10) si ha entrado o mejorado, 0 si no.</summary>
        public int AddLap(CircuitEntry c, string nick, string carId, float time)
        {
            var old = c.laps.FirstOrDefault(l => string.Equals(l.nick, nick, StringComparison.OrdinalIgnoreCase));
            if (old != null)
            {
                if (old.time <= time) return 0;
                c.laps.Remove(old);
            }
            var rec = new LapRecord { nick = nick, carId = carId, time = time, when = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            c.laps.Add(rec);
            c.laps.Sort((a, b) => a.time.CompareTo(b.time));
            if (c.laps.Count > NetProto.TopLaps) c.laps.RemoveRange(NetProto.TopLaps, c.laps.Count - NetProto.TopLaps);
            int pos = c.laps.IndexOf(rec);
            return pos < 0 ? 0 : pos + 1;
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new FileData { circuits = All }, Opts));
                File.Move(tmp, path, true);
            }
            catch (Exception e) { Log("ERROR guardando los circuitos en " + path + ": " + e.Message); }
        }
    }
}
