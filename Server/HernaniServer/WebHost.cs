// Servidor web integrado (Kestrel): sirve la versión web del juego (el build WebGL de Unity) y acepta las
// conexiones WebSocket de los jugadores del navegador en /ws. Así basta con abrir http://IP-del-servidor:8080
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using HernaniRacing.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace HernaniRacing.Server
{
    public class WebHost
    {
        readonly ServerConfig cfg;
        readonly Func<double> now;
        public readonly ConcurrentQueue<WsEvent> events = new ConcurrentQueue<WsEvent>();
        WebApplication app;
        string root;
        public Action<string> Log = Console.WriteLine;
        int live;
        public int LiveConnections { get { return Volatile.Read(ref live); } }

        public WebHost(ServerConfig c, Func<double> clock) { cfg = c; now = clock; }

        public bool Start()
        {
            root = string.IsNullOrWhiteSpace(cfg.webRoot) ? null : Path.GetFullPath(cfg.webRoot);
            try
            {
                var b = WebApplication.CreateSlimBuilder();
                b.Logging.ClearProviders();
                b.WebHost.UseUrls("http://0.0.0.0:" + cfg.webPort);
                b.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = 64 * 1024; o.AddServerHeader = false; });
                app = b.Build();
                app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
                app.Run(Handle);
                app.StartAsync().GetAwaiter().GetResult();
                bool hasGame = root != null && File.Exists(Path.Combine(root, "index.html"));
                Log($"Web: http://0.0.0.0:{cfg.webPort}/  (WebSocket en {NetProto.WebSocketPath}) · " +
                    (hasGame ? "juego web en " + root : "AÚN NO hay juego web en " + (root ?? "(sin webRoot)") + " → usa install-web.sh"));
                return true;
            }
            catch (Exception e)
            {
                Log("ERROR: no puedo abrir el puerto web TCP " + cfg.webPort + ": " + e.Message);
                return false;
            }
        }

        public void Stop()
        {
            try { app?.StopAsync().GetAwaiter().GetResult(); } catch (Exception) { }
        }

        async Task Handle(HttpContext ctx)
        {
            if (ctx.Request.Path.Equals(NetProto.WebSocketPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("Solo WebSocket"); return; }
                using (var ws = await ctx.WebSockets.AcceptWebSocketAsync())
                    await RunSocket(ws, ctx.Connection.RemoteIpAddress?.ToString() ?? "?");
                return;
            }
            if (ctx.Request.Method != "GET" && ctx.Request.Method != "HEAD") { ctx.Response.StatusCode = 405; return; }
            await ServeFile(ctx);
        }

        async Task RunSocket(WebSocket ws, string remote)
        {
            var conn = new WsConn(ws, remote, now());
            Interlocked.Increment(ref live);
            var sender = Task.Run(conn.SendLoop);
            var buf = new byte[8192];
            var ms = new MemoryStream();
            // el primer mensaje (datos de conexión) tiene que llegar en 10 s
            var firstCts = new CancellationTokenSource(10000);
            bool first = true;
            try
            {
                while (ws.State == WebSocketState.Open && !conn.IsClosed)
                {
                    var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), first ? firstCts.Token : CancellationToken.None);
                    first = false;
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, res.Count);
                    if (ms.Length > 16384) break;                      // ningún mensaje del juego es tan grande
                    if (!res.EndOfMessage) continue;
                    if (res.MessageType == WebSocketMessageType.Binary && ms.Length > 0)
                        events.Enqueue(new WsEvent { conn = conn, data = ms.ToArray() });
                    ms.SetLength(0);
                }
            }
            catch (Exception) { }
            finally
            {
                events.Enqueue(new WsEvent { conn = conn, data = null });
                conn.Abort();
                try { await sender; } catch (Exception) { }
                Interlocked.Decrement(ref live);
            }
        }

        // ------------------------------------------------------------------ ficheros del juego web
        static string ContentType(string ext)
        {
            switch (ext)
            {
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".js": return "application/javascript";
                case ".wasm": return "application/wasm";
                case ".css": return "text/css";
                case ".json": return "application/json";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".ico": return "image/x-icon";
                case ".svg": return "image/svg+xml";
                case ".txt": return "text/plain; charset=utf-8";
                default: return "application/octet-stream";
            }
        }

        async Task ServeFile(HttpContext ctx)
        {
            string rel = Uri.UnescapeDataString(ctx.Request.Path.Value ?? "/").Replace('\\', '/').TrimStart('/');
            if (rel.Length == 0 || rel.EndsWith("/")) rel += "index.html";
            string full = root == null ? null : Path.GetFullPath(Path.Combine(root, rel));
            if (full == null || !full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
            {
                if (rel == "index.html") { await NoGamePage(ctx); return; }
                ctx.Response.StatusCode = 404;
                return;
            }
            string name = Path.GetFileName(full).ToLowerInvariant();
            string enc = null;
            if (name.EndsWith(".gz")) { enc = "gzip"; name = name.Substring(0, name.Length - 3); }
            else if (name.EndsWith(".br")) { enc = "br"; name = name.Substring(0, name.Length - 3); }
            var res = ctx.Response;
            res.ContentType = ContentType(Path.GetExtension(name));
            if (enc != null) res.Headers["Content-Encoding"] = enc;
            res.Headers["Cache-Control"] = "no-cache";
            var fi = new FileInfo(full);
            res.ContentLength = fi.Length;
            if (ctx.Request.Method == "HEAD") return;
            await res.SendFileAsync(full);
        }

        static async Task NoGamePage(HttpContext ctx)
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync("<!doctype html><meta charset=utf-8><title>Hernani Racing</title>" +
                "<body style='font-family:sans-serif;background:#123;color:#eee;padding:40px'>" +
                "<h1>Hernani Racing</h1><p>El servidor funciona, pero todavía no se ha instalado la versión web del juego.</p>" +
                "<p>En Unity: <b>File → Build Settings → WebGL → Build</b>, sube la carpeta al servidor y ejecuta " +
                "<code>sudo ./install-web.sh carpeta-del-build</code>.</p></body>");
        }
    }
}
