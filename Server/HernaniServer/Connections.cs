// Conexiones de los jugadores: UDP (LiteNetLib, juego de PC) y WebSocket (versión web en el navegador).
// El resto del servidor trata a todos igual: mismas salas, mismos mensajes.
using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using HernaniRacing.Net;
using LiteNetLib;
using LiteNetLib.Utils;

namespace HernaniRacing.Server
{
    public interface IConn
    {
        void Send(NetDataWriter w, byte channel, bool reliable);
        int Rtt { get; }
        string Remote { get; }
        string Kind { get; }
        void Close();
    }

    public class UdpConn : IConn
    {
        public readonly NetPeer peer;
        readonly string remote;
        public UdpConn(NetPeer p, string remoteEndPoint) { peer = p; remote = remoteEndPoint; }
        public void Send(NetDataWriter w, byte channel, bool reliable)
        {
            peer.Send(w, channel, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }
        public int Rtt { get { return peer.RoundTripTime; } }
        public string Remote { get { return remote; } }
        public string Kind { get { return "UDP"; } }
        public void Close() { peer.Disconnect(); }
    }

    /// <summary>
    /// Jugador de la versión web. Recibe en un hilo del servidor web y deja los mensajes en una cola que el
    /// bucle principal procesa (todo lo de las salas sigue en un solo hilo). Los envíos van por una cola propia;
    /// si la conexión va lenta se descartan posiciones viejas (nunca los mensajes importantes).
    /// </summary>
    public class WsConn : IConn
    {
        const int MaxQueuedUnreliable = 192;
        readonly WebSocket ws;
        readonly string remote;
        readonly BlockingCollection<byte[]> outbox = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        int queued;
        volatile bool closed;
        public double lastRecv;
        public double rttSmoothed = -1;
        public bool accepted;

        public WsConn(WebSocket socket, string remoteAddress, double now) { ws = socket; remote = remoteAddress; lastRecv = now; }

        public string Remote { get { return remote; } }
        public string Kind { get { return "web"; } }
        public int Rtt { get { return rttSmoothed < 0 ? 0 : (int)Math.Round(rttSmoothed * 1000.0); } }
        public bool IsClosed { get { return closed; } }

        public void Send(NetDataWriter w, byte channel, bool reliable) { SendBytes(w.Data, w.Length, reliable); }

        public void SendBytes(byte[] data, int len, bool reliable)
        {
            if (closed) return;
            if (!reliable && Volatile.Read(ref queued) > MaxQueuedUnreliable) return;
            var copy = new byte[len];
            Buffer.BlockCopy(data, 0, copy, 0, len);
            Interlocked.Increment(ref queued);
            try { outbox.Add(copy); } catch (InvalidOperationException) { }
        }

        /// <summary>Bucle de envío (una tarea por conexión: WebSocket no admite dos envíos a la vez).</summary>
        public async Task SendLoop()
        {
            try
            {
                while (!closed)
                {
                    byte[] m;
                    try { m = outbox.Take(cts.Token); } catch (OperationCanceledException) { break; } catch (InvalidOperationException) { break; }
                    Interlocked.Decrement(ref queued);
                    await ws.SendAsync(new ArraySegment<byte>(m), WebSocketMessageType.Binary, true, cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception) { }
            closed = true;
        }

        /// <summary>Rechazo: manda el motivo y cierra.</summary>
        public void Reject(string why)
        {
            var w = new NetDataWriter();
            w.Put(NetProto.WsReject); w.Put(why ?? "");
            SendBytes(w.Data, w.Length, true);
            // da tiempo a que salga el mensaje antes de cerrar
            Task.Delay(400).ContinueWith(_ => Close());
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            try { outbox.CompleteAdding(); } catch (Exception) { }
            try { cts.CancelAfter(1500); } catch (Exception) { }
            try { ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "adios", CancellationToken.None); } catch (Exception) { }
        }

        public void Abort()
        {
            closed = true;
            try { cts.Cancel(); } catch (Exception) { }
            try { ws.Abort(); } catch (Exception) { }
        }
    }

    /// <summary>Lo que llega del servidor web al bucle principal.</summary>
    public struct WsEvent
    {
        public WsConn conn;
        public byte[] data;      // null = se ha cerrado
    }
}
