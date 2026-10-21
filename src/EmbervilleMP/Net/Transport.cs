using System;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace EmbervilleMP.Net
{
    // A connection to one other machine (host side: a guest; guest side: the host).
    internal interface IPeer
    {
        void Send(byte[] data, int offset, int length, DeliveryMethod method);
        int Ping { get; }
        string Address { get; }
        void Disconnect();
    }

    // How bytes get between machines. Session doesn't care which one it runs on.
    internal interface ITransport
    {
        string Name { get; }
        bool Listen();      // host
        bool Connect();     // guest (target given when the transport was made)
        void Poll();
        void Stop();
        void Drop();        // tests: go silent without telling the other side (as if the network died)
        event Action<IPeer> Connected;
        event Action<IPeer, string> Disconnected;
        event Action<IPeer, byte[], DeliveryMethod> Received;
    }

    internal enum TransportKind { Lan, SteamP2P, SteamIP }

    // ---------------- LiteNetLib: LAN / direct IP ----------------

    internal class LiteNetTransport : ITransport, INetEventListener
    {
        private class Peer : IPeer
        {
            public readonly NetPeer Net;
            public int Latency;
            public Peer(NetPeer p) { Net = p; }
            public void Send(byte[] data, int offset, int length, DeliveryMethod method) =>
                Net.Send(data, offset, length, method == DeliveryMethod.ReliableOrdered ? Channels.Reliable : Channels.State, method);
            public int Ping => Latency;
            public string Address => Net.ToString();
            public void Disconnect() => Net.Disconnect();
        }

        public string Name => "LAN/IP";
        public event Action<IPeer> Connected;
        public event Action<IPeer, string> Disconnected;
        public event Action<IPeer, byte[], DeliveryMethod> Received;
        public Func<NetDataWriter> DiscoveryReply;   // host: answers LAN searches

        private const string Key = Protocol.Key;      // the mod version is checked in the Hello, with a readable refusal
        private readonly NetManager net;
        private readonly string address;
        private readonly int port;

        public LiteNetTransport(string address, int port, bool host)
        {
            this.address = address;
            this.port = port;
            net = new NetManager(this)
            {
                ChannelsCount = Channels.Count,
                DisconnectTimeout = 8000,
                UnconnectedMessagesEnabled = host,
                BroadcastReceiveEnabled = host,
                IPv6Enabled = false,
                UpdateTime = 10,
            };
        }

        public bool Listen()
        {
            if (net.Start(port)) return true;
            Plugin.Log.LogError($"[net] Could not listen on UDP port {port} (already in use?)");
            return false;
        }

        public bool Connect()
        {
            net.Start();
            return net.Connect(address, port, Key) != null;
        }

        public void Poll() => net.PollEvents();
        public void Stop() => net.Stop(true);
        public void Drop() => net.Stop(false);

        private static Peer Wrap(NetPeer p) => (Peer)(p.Tag ?? (p.Tag = new Peer(p)));

        public void OnConnectionRequest(ConnectionRequest request)
        {
            if (!net.BroadcastReceiveEnabled) { request.Reject(); return; } // guests don't accept connections
            request.AcceptIfKey(Key);
        }

        public void OnPeerConnected(NetPeer peer) => Connected?.Invoke(Wrap(peer));

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            // "closed": the other side left on purpose (no reconnect); anything else is a lost connection.
            string why = info.Reason == DisconnectReason.ConnectionRejected ? "refused"
                : info.Reason == DisconnectReason.RemoteConnectionClose || info.Reason == DisconnectReason.DisconnectPeerCalled ? "closed"
                : info.Reason.ToString();
            Disconnected?.Invoke(Wrap(peer), why);
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
            Plugin.Log.LogWarning($"[net] Socket error {socketError} ({endPoint})");

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) => Wrap(peer).Latency = latency;

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            byte[] data = reader.GetRemainingBytes();
            reader.Recycle();
            Received?.Invoke(Wrap(peer), data, method);
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type)
        {
            if (DiscoveryReply != null && reader.AvailableBytes > 0 && reader.GetByte() == (byte)Msg.Discover)
                net.SendUnconnectedMessage(DiscoveryReply(), remote);
            reader.Recycle();
        }
    }
}
