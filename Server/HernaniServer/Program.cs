using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using HernaniRacing.Net;

namespace HernaniRacing.Server
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string cfgPath = "server.json";
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--config" || args[i] == "-c") && i + 1 < args.Length) cfgPath = args[++i];
                else if (args[i] == "--help" || args[i] == "-h")
                {
                    Console.WriteLine("hernani-server [--config server.json]");
                    Console.WriteLine("  Servidor de Hernani Racing. Abre el puerto UDP indicado en server.json (por defecto " + NetProto.DefaultPort + ")");
                    Console.WriteLine("  y el puerto web TCP (por defecto " + NetProto.DefaultWebPort + ") para la versión web: página del juego + WebSocket.");
                    return 0;
                }
            }
            var cfg = LoadConfig(cfgPath);
            var core = new ServerCore(cfg);
            Console.WriteLine("Hernani Racing - servidor multijugador (protocolo v" + NetProto.Version + ")");
            if (!core.Start()) return 1;

            bool running = true;
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; running = false; };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { running = false; };
            DateTime nextStatus = DateTime.Now.AddMinutes(10);
            while (running)
            {
                core.Tick();
                Thread.Sleep(2);
                if (DateTime.Now >= nextStatus)
                {
                    nextStatus = DateTime.Now.AddMinutes(10);
                    if (core.PlayerCount > 0) Console.WriteLine("[estado] " + core.Status());
                }
            }
            core.Stop();
            Console.WriteLine("Servidor detenido.");
            return 0;
        }

        static ServerConfig LoadConfig(string path)
        {
            var opts = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, WriteIndented = true };
            if (!File.Exists(path))
            {
                var def = new ServerConfig();
                try
                {
                    File.WriteAllText(path, JsonSerializer.Serialize(def, opts));
                    Console.WriteLine("Creado " + Path.GetFullPath(path) + " con la configuración por defecto (¡pon una contraseña!).");
                }
                catch (Exception e) { Console.WriteLine("No puedo escribir " + path + ": " + e.Message); }
                return def;
            }
            try
            {
                var c = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), opts) ?? new ServerConfig();
                Console.WriteLine("Configuración: " + Path.GetFullPath(path));
                return c;
            }
            catch (Exception e)
            {
                Console.WriteLine("ERROR en " + path + ": " + e.Message + " -> uso la configuración por defecto");
                return new ServerConfig();
            }
        }
    }
}
