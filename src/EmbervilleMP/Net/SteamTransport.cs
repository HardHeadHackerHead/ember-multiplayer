using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using EmbervilleMP.Steam;
using LiteNetLib;
using Steamworks;

namespace EmbervilleMP.Net
{
    // Steam Networking Sockets. P2P mode connects to a Steam user through Valve's relay network (no port
    // forwarding, works behind NAT); IP mode runs the same code over plain UDP, which is how the one-PC tests
    // exercise it (one Steam account can't open a P2P connection to itself).
    internal class SteamTransport : ITransport
    {
        private class Peer : IPeer
        {
            public readonly HSteamNetConnection Conn;
            public readonly string Who;
            public Peer(HSteamNetConnection c, string who) { Conn = c; Who = who; }

            public void Send(byte[] data, int offset, int length, DeliveryMethod method)
            {
                int flags = method == DeliveryMethod.ReliableOrdered
                    ? Constants.k_nSteamNetworkingSend_ReliableNoNagle
                    : Constants.k_nSteamNetworkingSend_UnreliableNoNagle;
                var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
                try { SteamNetworkingSockets.SendMessageToConnection(Conn, handle.AddrOfPinnedObject() + offset, (uint)length, flags, out _); }
                finally { handle.Free(); }
            }

            public int Ping
            {
                get
                {
                    var status = new SteamNetConnectionRealTimeStatus_t();
                    var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
                    return SteamNetworkingSockets.GetConnectionRealTimeStatus(Conn, ref status, 0, ref lanes) == EResult.k_EResultOK ? status.m_nPing : 0;
                }
            }

            public string Address => Who;
            public void Disconnect() => SteamNetworkingSockets.CloseConnection(Conn, 0, "bye", true);
        }

        public string Name => p2p ? "Steam" : "Steam (IP)";
        public event Action<IPeer> Connected;
        public event Action<IPeer, string> Disconnected;
        public event Action<IPeer, byte[], DeliveryMethod> Received;

        private readonly bool p2p;
        private readonly CSteamID remoteUser;   // P2P guest: the host
        private readonly string address;        // IP mode
        private readonly ushort port;
        private HSteamListenSocket listen = HSteamListenSocket.Invalid;
        private HSteamNetPollGroup group = HSteamNetPollGroup.Invalid;
        private readonly Dictionary<HSteamNetConnection, Peer> peers = new Dictionary<HSteamNetConnection, Peer>();
        private readonly IntPtr[] inbox = new IntPtr[64];
        private Callback<SteamNetConnectionStatusChangedCallback_t> statusCallback;
        private bool isHost;

        private SteamTransport(bool p2p, CSteamID remote, string address, int port)
        {
            this.p2p = p2p;
            remoteUser = remote;
            this.address = address;
            this.port = (ushort)port;
        }

        public static SteamTransport P2P(CSteamID host = default) => new SteamTransport(true, host, null, 0);
        public static SteamTransport OverIp(string address, int port) => new SteamTransport(false, default, address, port);

        private bool Begin()
        {
            if (!SteamBootstrap.Init()) { Plugin.Log.LogError("[net] Steam isn't available: " + SteamBootstrap.Error); return false; }
            statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
            group = SteamNetworkingSockets.CreatePollGroup();
            if (p2p) SteamNetworkingUtils.InitRelayNetworkAccess();
            return true;
        }

        public bool Listen()
        {
            if (!Begin()) return false;
            isHost = true;
            if (p2p) listen = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
            else
            {
                var addr = new SteamNetworkingIPAddr();
                addr.Clear();
                addr.m_port = port;
                listen = SteamNetworkingSockets.CreateListenSocketIP(ref addr, 0, null);
            }
            if (listen == HSteamListenSocket.Invalid) { Plugin.Log.LogError("[net] Steam listen socket failed"); return false; }
            return true;
        }

        public bool Connect()
        {
            if (!Begin()) return false;
            HSteamNetConnection conn;
            if (p2p)
            {
                var id = new SteamNetworkingIdentity();
                id.SetSteamID(remoteUser);
                conn = SteamNetworkingSockets.ConnectP2P(ref id, 0, 0, null);
            }
            else
            {
                var addr = new SteamNetworkingIPAddr();
                if (!addr.ParseString($"{address}:{port}")) { Plugin.Log.LogError($"[net] Bad address {address}:{port}"); return false; }
                conn = SteamNetworkingSockets.ConnectByIPAddress(ref addr, 0, null);
            }
            if (conn == HSteamNetConnection.Invalid) return false;
            SteamNetworkingSockets.SetConnectionPollGroup(conn, group);
            peers[conn] = new Peer(conn, p2p ? remoteUser.ToString() : $"{address}:{port}");
            return true;
        }

        private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t e)
        {
            var conn = e.m_hConn;
            var info = e.m_info;
            switch (info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    // Host: a guest is knocking. Version and room are checked in the Hello.
                    if (isHost && info.m_hListenSocket == listen)
                    {
                        if (SteamNetworkingSockets.AcceptConnection(conn) != EResult.k_EResultOK)
                        {
                            SteamNetworkingSockets.CloseConnection(conn, 0, "accept failed", false);
                            return;
                        }
                        SteamNetworkingSockets.SetConnectionPollGroup(conn, group);
                        peers[conn] = new Peer(conn, info.m_identityRemote.GetSteamID().ToString());
                    }
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    if (peers.TryGetValue(conn, out var p)) Connected?.Invoke(p);
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    if (peers.TryGetValue(conn, out var gone))
                    {
                        peers.Remove(conn);
                        string debug = string.IsNullOrEmpty(info.m_szEndDebug) ? info.m_eState.ToString() : info.m_szEndDebug;
                        bool closed = info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer;
                        Disconnected?.Invoke(gone, closed ? "closed (" + debug + ")" : debug);
                    }
                    SteamNetworkingSockets.CloseConnection(conn, 0, null, false);
                    break;
            }
        }

        public void Poll()
        {
            if (group == HSteamNetPollGroup.Invalid) return;
            SteamBootstrap.RunCallbacks();
            int n;
            do
            {
                n = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(group, inbox, inbox.Length);
                for (int i = 0; i < n; i++)
                {
                    var msg = SteamNetworkingMessage_t.FromIntPtr(inbox[i]);
                    var data = new byte[msg.m_cbSize];
                    Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);
                    var method = (msg.m_nFlags & Constants.k_nSteamNetworkingSend_Reliable) != 0 ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
                    var conn = msg.m_conn;
                    SteamNetworkingMessage_t.Release(inbox[i]);
                    if (peers.TryGetValue(conn, out var p)) Received?.Invoke(p, data, method);
                }
            } while (n == inbox.Length);
        }

        // Steam always tells the other side when a connection closes, so a test drop is a plain close.
        public void Drop() => Stop();

        public void Stop()
        {
            foreach (var conn in peers.Keys) SteamNetworkingSockets.CloseConnection(conn, 0, "session ended", true);
            peers.Clear();
            if (listen != HSteamListenSocket.Invalid) SteamNetworkingSockets.CloseListenSocket(listen);
            if (group != HSteamNetPollGroup.Invalid) SteamNetworkingSockets.DestroyPollGroup(group);
            listen = HSteamListenSocket.Invalid;
            group = HSteamNetPollGroup.Invalid;
            statusCallback?.Dispose();
            statusCallback = null;
        }
    }
}
