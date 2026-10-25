// Servidor web integrado (Kestrel): sirve la versión web del juego (el build WebGL de Unity) y acepta las
// conexiones WebSocket de los jugadores del navegador en /ws. Así basta con abrir http://IP-del-servidor:8080
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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
                // v16: https opcional en el MISMO puerto; lo que llegue por http se redirige a https
                bool tls = !string.IsNullOrWhiteSpace(cfg.tlsCert) && !string.IsNullOrWhiteSpace(cfg.tlsKey);
                if (tls)
                {
                    certs = new CertCache(cfg.tlsCert.Trim(), cfg.tlsKey.Trim(), Log);
                    if (certs.Get() == null)
                    {
                        Log("AVISO: no puedo leer el certificado (" + cfg.tlsCert + " / " + cfg.tlsKey + "): sigo sólo con http");
                        tls = false; certs = null;
                    }
                }
                https = tls;
                b.WebHost.ConfigureKestrel(o =>
                {
                    o.Limits.MaxRequestBodySize = 64 * 1024; o.AddServerHeader = false;
                    o.ListenAnyIP(cfg.webPort, lo =>
                    {
                        if (!tls) return;
                        lo.Protocols = HttpProtocols.Http1;   // WebSocket clásico (Upgrade) en todos los navegadores
                        lo.Use(next => conn => PlainHttpRedirect(conn, next));
                        lo.UseHttps(new HttpsConnectionAdapterOptions { ServerCertificateSelector = (c, name) => certs.Get() });
                    });
                });
                app = b.Build();
                app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
                app.Run(Handle);
                app.StartAsync().GetAwaiter().GetResult();
                bool hasGame = root != null && File.Exists(Path.Combine(root, "index.html"));
                Log($"Web: {(https ? "https" : "http")}://0.0.0.0:{cfg.webPort}/  (WebSocket en {NetProto.WebSocketPath}{(https ? ", http → https" : "")}) · " +
                    (hasGame ? "juego web en " + root : "AÚN NO hay juego web en " + (root ?? "(sin webRoot)") + " → usa install-web.sh"));
                return true;
            }
            catch (Exception e)
            {
                Log("ERROR: no puedo abrir el puerto web TCP " + cfg.webPort + ": " + e.Message);
                return false;
            }
        }

        CertCache certs;
        bool https;

        /// <summary>
        /// Primer byte de la conexión: 0x16 = saludo TLS (https) → sigue; cualquier otra cosa es http normal →
        /// respuesta 301 a la misma dirección con https (mismo puerto) y se cierra.
        /// </summary>
        async Task PlainHttpRedirect(ConnectionContext conn, ConnectionDelegate next)
        {
            var input = conn.Transport.Input;
            ReadResult r;
            try { r = await input.ReadAsync(); } catch (Exception) { return; }
            var buf = r.Buffer;
            if (buf.Length == 0) { input.AdvanceTo(buf.Start); return; }
            if (buf.FirstSpan[0] == 0x16) { input.AdvanceTo(buf.Start); await next(conn); return; }
            // http: leer la cabecera (máx. 8 KB / 5 s) para sacar la ruta y el Host
            using var cts = new CancellationTokenSource(5000);
            string head = "";
            try
            {
                while (true)
                {
                    head = Encoding.ASCII.GetString(buf.Length > 8192 ? buf.Slice(0, 8192).ToArray() : buf.ToArray());
                    if (head.Contains("\r\n\r\n") || buf.Length >= 8192 || r.IsCompleted) break;
                    input.AdvanceTo(buf.Start, buf.End);
                    r = await input.ReadAsync(cts.Token);
                    buf = r.Buffer;
                }
            }
            catch (Exception) { return; }
            string path = "/", host = null;
            var lines = head.Split("\r\n");
            if (lines.Length > 0)
            {
                var parts = lines[0].Split(' ');
                if (parts.Length >= 2 && parts[1].StartsWith("/")) path = parts[1];
            }
            foreach (var l in lines)
                if (l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) { host = l.Substring(5).Trim(); break; }
            if (string.IsNullOrEmpty(host) || host.IndexOfAny(new[] { ' ', '/', '\\', '\r', '\n' }) >= 0)
                host = (conn.LocalEndPoint as IPEndPoint)?.Address + ":" + cfg.webPort;
            // por IP (http://194.x.x.x:8080) el certificado no valdría: se manda al nombre del certificado
            {
                string hn = host, hp = "";
                int ci = host.LastIndexOf(':');
                if (ci > 0 && host.IndexOf(']') < ci) { hn = host.Substring(0, ci); hp = host.Substring(ci); }
                string cn = certs?.Name;
                if (!string.IsNullOrEmpty(cn) && !cn.StartsWith("*") &&
                    (IPAddress.TryParse(hn.Trim('[', ']'), out _) || hn.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
                    host = cn + (hp.Length > 0 ? hp : ":" + cfg.webPort);
            }
            string loc = "https://" + host + path;
            string body = "<a href=\"" + WebUtility.HtmlEncode(loc) + "\">" + WebUtility.HtmlEncode(loc) + "</a>";
            string resp = "HTTP/1.1 301 Moved Permanently\r\nLocation: " + loc + "\r\nContent-Type: text/html; charset=utf-8\r\n" +
                          "Content-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;
            try
            {
                input.AdvanceTo(buf.End);
                await conn.Transport.Output.WriteAsync(Encoding.UTF8.GetBytes(resp));
                await conn.Transport.Output.FlushAsync();
            }
            catch (Exception) { }
        }

        /// <summary>Certificado PEM; se vuelve a leer solo cuando cambia el fichero (renovación de Let's Encrypt).</summary>
        sealed class CertCache
        {
            readonly string certPath, keyPath;
            readonly Action<string> log;
            readonly object sync = new object();
            X509Certificate2 cert;
            DateTime stamp, nextCheck;
            public string Name;   // nombre DNS principal del certificado
            public CertCache(string c, string k, Action<string> l) { certPath = c; keyPath = k; log = l; }
            public X509Certificate2 Get()
            {
                lock (sync)
                {
                    if (cert != null && DateTime.UtcNow < nextCheck) return cert;
                    nextCheck = DateTime.UtcNow.AddSeconds(60);
                    try
                    {
                        var t = File.GetLastWriteTimeUtc(certPath);
                        var tk = File.GetLastWriteTimeUtc(keyPath);
                        if (tk > t) t = tk;
                        if (cert != null && t == stamp) return cert;
                        using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                        // (exportar y volver a importar: en Windows la clave PEM es "efímera" y TLS no la acepta)
                        var c = new X509Certificate2(pem.Export(X509ContentType.Pkcs12));
                        cert = c; stamp = t;
                        Name = c.GetNameInfo(X509NameType.DnsName, false);
                        log($"Certificado https: {c.GetNameInfo(X509NameType.DnsName, false)} (caduca {c.NotAfter:yyyy-MM-dd})");
                    }
                    catch (Exception e) { log("ERROR leyendo el certificado https: " + e.Message); }
                    return cert;
                }
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
                bool own = SameOrigin(ctx);
                // registro de diagnóstico: ayuda a ver proxys que cambian Host/Origin o no dejan pasar WebSocket
                Log($"Web: petición /ws de {ctx.Connection.RemoteIpAddress} · Host={ctx.Request.Host.Value} · Origin={ctx.Request.Headers["Origin"]} · " +
                    $"WebSocket={(ctx.WebSockets.IsWebSocketRequest ? "sí" : "no")} · misma página={(own ? "sí" : "no")}");
                if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("Solo WebSocket"); return; }
                using (var ws = await ctx.WebSockets.AcceptWebSocketAsync())
                    await RunSocket(ws, ctx.Connection.RemoteIpAddress?.ToString() ?? "?", own);
                return;
            }
            if (ctx.Request.Method != "GET" && ctx.Request.Method != "HEAD") { ctx.Response.StatusCode = 405; return; }
            await ServeFile(ctx);
        }

        /// <summary>v16: ¿la conexión viene de la página del juego servida por este mismo servidor? (Origin == Host)</summary>
        static bool SameOrigin(HttpContext ctx)
        {
            string origin = ctx.Request.Headers["Origin"].ToString();
            string host = ctx.Request.Host.Value ?? "";
            if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(host)) return false;
            int i = origin.IndexOf("://", StringComparison.Ordinal);
            if (i < 0) return false;
            string auth = origin.Substring(i + 3).TrimEnd('/');
            string scheme = origin.Substring(0, i).ToLowerInvariant();
            // puerto por defecto: el navegador no lo pone en Origin
            string Norm(string a, string sch)
            {
                a = a.ToLowerInvariant();
                if (sch == "https" && a.EndsWith(":443")) a = a.Substring(0, a.Length - 4);
                if (sch == "http" && a.EndsWith(":80")) a = a.Substring(0, a.Length - 3);
                return a;
            }
            return Norm(auth, scheme) == Norm(host, ctx.Request.IsHttps ? "https" : "http");
        }

        async Task RunSocket(WebSocket ws, string remote, bool ownPage)
        {
            var conn = new WsConn(ws, remote, now()) { ownPage = ownPage };
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
