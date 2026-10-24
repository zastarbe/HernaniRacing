using System;
using System.Runtime.InteropServices;
using HernaniRacing.Net;
using LiteNetLib.Utils;

namespace HernaniRacing
{
    /// <summary>
    /// Transporte WebSocket para la versión web (WebGL). Usa el WebSocket del navegador a través de
    /// Plugins/WebGL/HernaniWebSocket.jslib. Habla con el mismo servidor que la versión de PC (ws://servidor:8080/ws).
    /// </summary>
    public class WebSocketTransport : INetTransport
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void HRWS_Connect(string url);
        [DllImport("__Internal")] static extern int HRWS_State();
        [DllImport("__Internal")] static extern int HRWS_CloseCode();
        [DllImport("__Internal")] static extern int HRWS_Send(byte[] data, int len);
        [DllImport("__Internal")] static extern int HRWS_Recv(byte[] buf, int max);
        [DllImport("__Internal")] static extern void HRWS_Close();
#else
        // fuera del navegador no hay WebSocket de JavaScript (este transporte sólo se usa en WebGL)
        static void HRWS_Connect(string url) { }
        static int HRWS_State() { return 3; }
        static int HRWS_CloseCode() { return 1006; }
        static int HRWS_Send(byte[] data, int len) { return 0; }
        static int HRWS_Recv(byte[] buf, int max) { return -1; }
        static void HRWS_Close() { }
#endif
        byte[] buf = new byte[4096];
        readonly NetDataReader reader = new NetDataReader();
        readonly NetDataWriter hello = new NetDataWriter();
        bool active, helloSent, closedReported;

        public bool IsWeb { get { return true; } }
        public Action<NetDataReader> Received { get; set; }
        public Action<CloseKind, string, string> Closed { get; set; }
        public int Rtt { get { return -1; } }            // el ping lo calcula NetClientCore con el reloj
        public int MaxUnreliable { get { return 1200; } }

        /// <summary>host puede ser una URL completa (ws://... o wss://...) o un nombre/IP (se usa ws://host:port/ws).</summary>
        public static string MakeUrl(string host, int port)
        {
            host = (host ?? "").Trim();
            if (host.StartsWith("ws://") || host.StartsWith("wss://")) return host;
            if (port <= 0) port = NetProto.DefaultWebPort;
            return "ws://" + host + ":" + port + NetProto.WebSocketPath;
        }

        public string Connect(string host, int port, NetDataWriter connectData)
        {
            Disconnect();
            hello.Reset();
            hello.Put(NetProto.WsHello);
            hello.Put(connectData.Data, 0, connectData.Length);
            helloSent = false; closedReported = false; active = true;
            HRWS_Connect(MakeUrl(host, port));
            return null;
        }

        public void Send(NetDataWriter w, byte channel, bool reliable)
        {
            if (!active || !helloSent) return;
            SendRaw(w.Data, w.Length);
        }

        void SendRaw(byte[] data, int len)
        {
            // el plugin lee desde el principio del array: copia si hace falta (NetDataWriter puede tener más capacidad)
            HRWS_Send(data, len);
        }

        public void Poll()
        {
            if (!active) return;
            int st = HRWS_State();
            if (st == 2 && !helloSent) { helloSent = true; SendRaw(hello.Data, hello.Length); }
            // mensajes recibidos
            for (int guard = 0; guard < 4000; guard++)
            {
                int n = HRWS_Recv(buf, buf.Length);
                if (n == -1) break;
                if (n < -1) { buf = new byte[Math.Max(buf.Length * 2, -n - 2 + 64)]; continue; }
                if (n == 0) continue;
                if (buf[0] == NetProto.WsReject)
                {
                    string why = null;
                    try { reader.SetSource(buf, 1, n); why = reader.GetString(300); } catch (Exception) { why = null; }
                    Finish(CloseKind.Rejected, why ?? "El servidor ha rechazado la conexión.", "rechazo");
                    return;
                }
                reader.SetSource(buf, 0, n);
                if (Received != null) Received(reader);
                if (!active) return;
            }
            if (st == 3) Finish(helloSent ? CloseKind.Lost : CloseKind.NoResponse, null, "código " + HRWS_CloseCode());
        }

        void Finish(CloseKind k, string text, string detail)
        {
            if (closedReported) return;
            closedReported = true;
            active = false;
            HRWS_Close();
            if (Closed != null) Closed(k, text, detail);
        }

        public void Disconnect()
        {
            if (active) HRWS_Close();
            active = false; helloSent = false;
        }

        public void Shutdown() { Disconnect(); }
    }
}
