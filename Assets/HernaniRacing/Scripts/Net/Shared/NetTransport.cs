// Transporte de red del cliente: UDP (LiteNetLib, PC y móvil) o WebSocket (versión web: el navegador no
// permite UDP). Los mensajes son los mismos bytes en los dos casos; el servidor acepta ambos a la vez y
// los jugadores de web y de PC juegan juntos en las mismas salas.
using System;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace HernaniRacing.Net
{
    public enum CloseKind { Local, Rejected, NoResponse, Lost, Unreachable, RemoteClose, Other }

    public interface INetTransport
    {
        /// <summary>true = WebSocket (versión web).</summary>
        bool IsWeb { get; }
        /// <summary>Empieza a conectar. Devuelve un error legible o null.</summary>
        string Connect(string host, int port, NetDataWriter connectData);
        void Send(NetDataWriter w, byte channel, bool reliable);
        /// <summary>Procesa lo recibido (llama a Received / Closed).</summary>
        void Poll();
        void Disconnect();
        void Shutdown();
        /// <summary>Ping en ms (o -1 si no lo sabe el transporte).</summary>
        int Rtt { get; }
        int MaxUnreliable { get; }
        Action<NetDataReader> Received { get; set; }
        /// <summary>Se ha cerrado la conexión: tipo, texto del servidor (si lo hay) y detalle técnico.</summary>
        Action<CloseKind, string, string> Closed { get; set; }
    }

    /// <summary>UDP con LiteNetLib (lo de siempre en PC).</summary>
    public class UdpTransport : INetTransport, INetEventListener
    {
        readonly NetManager net;
        NetPeer server;
        public bool IsWeb { get { return false; } }
        public Action<NetDataReader> Received { get; set; }
        public Action<CloseKind, string, string> Closed { get; set; }

        public UdpTransport()
        {
            net = new NetManager(this)
            {
                ChannelsCount = (byte)NetProto.Channels,
                DisconnectTimeout = 10000,
                UpdateTime = 10,
                AutoRecycle = true,
                UnsyncedEvents = false,
                IPv6Enabled = false
            };
        }

        public string Connect(string host, int port, NetDataWriter connectData)
        {
            if (!net.IsRunning) net.Start();
            IPAddress ip;
            try
            {
                if (!IPAddress.TryParse(host.Trim(), out ip))
                {
                    ip = null;
                    foreach (var a in Dns.GetHostAddresses(host.Trim()))
                        if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                    if (ip == null) throw new Exception("sin IPv4");
                }
            }
            catch (Exception)
            {
                return "No encuentro el servidor \"" + host + "\". Revisa la dirección.";
            }
            server = net.Connect(new IPEndPoint(ip, port), connectData);
            return null;
        }

        public void Send(NetDataWriter w, byte channel, bool reliable)
        {
            if (server != null) server.Send(w, channel, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }

        public void Poll() { if (net.IsRunning) net.PollEvents(); }

        public void Disconnect()
        {
            if (server != null) server.Disconnect();
            server = null;
        }

        public void Shutdown()
        {
            Disconnect();
            if (net.IsRunning) net.Stop();
        }

        public int Rtt { get { return server != null ? server.RoundTripTime : -1; } }
        public int MaxUnreliable { get { return server != null ? server.GetMaxSinglePacketSize(DeliveryMethod.Unreliable) : 500; } }

        public void OnPeerConnected(NetPeer peer) { }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (peer != server) return;
            server = null;
            string text = null;
            if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
            {
                try { text = info.AdditionalData.GetString(200); } catch (Exception) { text = null; }
            }
            CloseKind k;
            if (text != null) k = CloseKind.Rejected;
            else switch (info.Reason)
            {
                case DisconnectReason.ConnectionFailed:
                case DisconnectReason.Timeout: k = CloseKind.NoResponse; break;
                case DisconnectReason.HostUnreachable:
                case DisconnectReason.NetworkUnreachable: k = CloseKind.Unreachable; break;
                case DisconnectReason.RemoteConnectionClose: k = CloseKind.RemoteClose; break;
                case DisconnectReason.DisconnectPeerCalled: k = CloseKind.Local; break;
                default: k = CloseKind.Other; break;
            }
            Closed?.Invoke(k, text, info.Reason.ToString());
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader r, byte channel, DeliveryMethod method)
        {
            if (peer == server) Received?.Invoke(r);
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        public void OnConnectionRequest(ConnectionRequest request) { request.Reject(); }
    }
}
