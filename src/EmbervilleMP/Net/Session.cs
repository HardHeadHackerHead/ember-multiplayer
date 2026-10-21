using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace EmbervilleMP.Net
{
    internal class PlayerInfo
    {
        public byte Id;
        public string Name;
        public IPeer Peer;     // host side: the guest's connection
        public PeerGuard Guard; // host side: what this guest may send (see NetGuard)
        public int Ping;
        public List<Api.MpMod> Mods; // host side: the mods it declared in its hello (Api/ApiMods.cs)
        public override string ToString() => $"{Name}#{Id}";
    }

    // One multiplayer session: star topology, the host relays what guests send to everyone else.
    // Every packet is [Msg][sender player id][payload]. Call Poll() every frame on the main thread.
    // The bytes travel over an ITransport (LAN/IP via LiteNetLib, or Steam).
    internal class Session
    {
        public static Session Current { get; private set; }

        public readonly bool IsHost;
        public byte LocalId { get; private set; }
        public string LocalName { get; }
        public bool Ready => IsHost || LocalId != 0;
        public string Status { get; private set; } = "";
        // Guest: the connection to the host was lost (not closed by the host, not refused): worth reconnecting.
        public bool Dropped { get; private set; }
        // Guest reconnecting: the id we had, so the host replaces our lost connection at once (and we keep the id).
        public byte RejoinAs;
        public string TransportName => transport.Name;
        public readonly Dictionary<byte, PlayerInfo> Players = new Dictionary<byte, PlayerInfo>();
        public IEnumerable<PlayerInfo> Others => Players.Values.Where(p => p.Id != LocalId);

        public event Action<PlayerInfo> PlayerJoined;
        public event Action<PlayerInfo> PlayerLeft;
        public event Action Ended;
        public event Action Welcomed;   // guest: the host accepted us
        // How the packet being handled now came (handlers that pass it on keep it: Api/ApiChannels.cs).
        public DeliveryMethod Receiving { get; private set; } = DeliveryMethod.ReliableOrdered;

        private readonly ITransport transport;
        private IPeer hostPeer;
        private readonly NetDataWriter writer = new NetDataWriter();
        private readonly Dictionary<Msg, Action<byte, NetDataReader>> handlers = new Dictionary<Msg, Action<byte, NetDataReader>>();
        // Messages kept per player and replayed to anyone who joins later (latest of each kind).
        private readonly Dictionary<(byte, Msg), byte[]> sticky = new Dictionary<(byte, Msg), byte[]>();
        private static readonly HashSet<Msg> stickyKinds = new HashSet<Msg> { Msg.Look };
        // Which messages a guest may send, how big, how often, and which ones stay with the host: NetGuard.Rules.
        private readonly List<(IPeer peer, float at)> pendingKicks = new List<(IPeer, float)>();
        private bool ended;

        public int MaxPlayers = Protocol.MaxPlayers;

        private Session(bool host, string name, ITransport transport)
        {
            IsHost = host;
            LocalName = name;
            this.transport = transport;
            transport.Connected += OnConnected;
            transport.Disconnected += OnDisconnected;
            transport.Received += OnReceived;
        }

        public static Session Host(ITransport transport, string name)
        {
            Current?.Stop();
            var s = new Session(true, name, transport);
            if (transport is LiteNetTransport lite) lite.DiscoveryReply = s.DiscoveryReply;
            if (!transport.Listen()) return null;
            s.LocalId = Protocol.HostId;
            s.Players[s.LocalId] = new PlayerInfo { Id = s.LocalId, Name = name };
            s.Status = $"Hosting ({transport.Name})";
            Plugin.Log.LogInfo($"[net] Hosting over {transport.Name} as {name}");
            Current = s;
            return s;
        }

        public static Session Join(ITransport transport, string name, string target)
        {
            Current?.Stop();
            var s = new Session(false, name, transport);
            s.Status = $"Connecting to {target}...";
            Plugin.Log.LogInfo($"[net] Joining {target} over {transport.Name} as {name}");
            Current = s;
            if (!transport.Connect())
            {
                s.Status = "Could not connect";
                s.Stop();
                return null;
            }
            return s;
        }

        public static Session HostLan(int port, string name) => Host(new LiteNetTransport(null, port, true), name);
        public static Session JoinLan(string address, int port, string name) => Join(new LiteNetTransport(address, port, false), name, $"{address}:{port}");

        public void On(Msg type, Action<byte, NetDataReader> handler) => handlers[type] = handler;

        // Tests (a fresh world between chained scenarios): forget everything that rode on this session, so the
        // syncs can be built again on the same connection.
        public void ResetSubscribers()
        {
            PlayerJoined = null;
            PlayerLeft = null;
            Ended = null;
            Welcomed = null;
            handlers.Clear();
        }

        public void Poll()
        {
            if (ended) return;
            transport.Poll();
            float now = UnityEngine.Time.realtimeSinceStartup;
            for (int i = pendingKicks.Count - 1; i >= 0; i--)
                if (now >= pendingKicks[i].at)
                {
                    var peer = pendingKicks[i].peer;
                    pendingKicks.RemoveAt(i);
                    peer.Disconnect();
                    OnDisconnected(peer, "kicked");   // gone now (not every transport reports a connection we closed)
                }
            foreach (var p in Players.Values)
            {
                if (p.Peer != null) p.Ping = p.Peer.Ping;
                p.Guard?.Tick(now);
            }
            NetStats.Tick();
            if (!IsHost && hostPeer != null && Players.TryGetValue(Protocol.HostId, out var h)) h.Ping = hostPeer.Ping;
        }

        public void Stop()
        {
            if (ended) return;
            ended = true;
            transport.Stop();
            Plugin.Log.LogInfo("[net] Session ended");
            if (Current == this) Current = null;
            Ended?.Invoke();
        }

        // Tests: the network dies under us (the other side only notices by the timeout).
        public void SimulateDrop()
        {
            if (ended) return;
            Plugin.Log.LogInfo("[net] Simulating a lost connection");
            transport.Drop();
            if (IsHost) Stop();
            else OnDisconnected(hostPeer, "lost (simulated)");
        }

        // ---------------- sending ----------------

        // Sends one of the local player's messages to everyone (via the host when we're a guest).
        public void Send(Msg type, Action<NetDataWriter> body, DeliveryMethod method = DeliveryMethod.ReliableOrdered)
        {
            if (!Ready) return;
            writer.Reset();
            writer.Put((byte)type);
            writer.Put(LocalId);
            body?.Invoke(writer);
            if (stickyKinds.Contains(type)) sticky[(LocalId, type)] = writer.CopyData();
            SendRaw(writer, method);
        }

        // Sends an already framed packet ([Msg][LocalId][...]) the same way Send does.
        public void SendRaw(NetDataWriter w, DeliveryMethod method)
        {
            if (!Ready) return;
            if (IsHost)
            {
                foreach (var p in Players.Values)
                    if (p.Peer != null) Out(p.Peer, w.Data, w.Length, method); // welcomed guests only
            }
            else if (hostPeer != null) Out(hostPeer, w.Data, w.Length, method);
        }

        // Every packet leaves through here (counted per message type).
        private static void Out(IPeer peer, byte[] data, int length, DeliveryMethod method)
        {
            NetStats.Out(data, 0, length);
            peer.Send(data, 0, length, method);
        }

        // Host only: sends to a single guest.
        public void SendTo(byte playerId, Msg type, Action<NetDataWriter> body, DeliveryMethod method = DeliveryMethod.ReliableOrdered)
        {
            if (!IsHost || !Players.TryGetValue(playerId, out var p) || p.Peer == null) return;
            var w = Frame(type, body);
            Out(p.Peer, w.Data, w.Length, method);
        }

        // Host only: a packet to one guest in another player's name (`from`: a guest's message the host checked and
        // passes on to some players only; the header is ours to write, so it can't be spoofed).
        public void RelayTo(byte playerId, byte from, Msg type, Action<NetDataWriter> body, DeliveryMethod method)
        {
            if (!IsHost || !Players.TryGetValue(playerId, out var p) || p.Peer == null) return;
            var w = new NetDataWriter();
            w.Put((byte)type);
            w.Put(from);
            body?.Invoke(w);
            Out(p.Peer, w.Data, w.Length, method);
        }

        private NetDataWriter Frame(Msg type, Action<NetDataWriter> body)
        {
            var w = new NetDataWriter();
            w.Put((byte)type);
            w.Put(LocalId);
            body?.Invoke(w);
            return w;
        }

        private void Broadcast(Msg type, Action<NetDataWriter> body)
        {
            var w = Frame(type, body);
            foreach (var p in Players.Values)
                if (p.Peer != null) Out(p.Peer, w.Data, w.Length, DeliveryMethod.ReliableOrdered);
        }

        private void SendToPeer(IPeer peer, Msg type, Action<NetDataWriter> body)
        {
            var w = Frame(type, body);
            Out(peer, w.Data, w.Length, DeliveryMethod.ReliableOrdered);
        }

        // Host: removes a guest, telling it why first.
        public void Kick(byte playerId, string reason)
        {
            if (!IsHost || !Players.TryGetValue(playerId, out var p) || p.Peer == null) return;
            if (p.Guard != null) p.Guard.Kicked = true;
            Refuse(p.Peer, reason);
        }

        private bool Kicking(IPeer peer) => pendingKicks.Exists(k => k.peer == peer);

        private void Refuse(IPeer peer, string reason)
        {
            if (Kicking(peer)) return;
            Plugin.Log.LogInfo($"[net] Refusing {peer.Address}: {reason}");
            SendToPeer(peer, Msg.Refuse, w => w.Put(reason));
            pendingKicks.Add((peer, UnityEngine.Time.realtimeSinceStartup + 0.5f));
        }

        // ---------------- transport events ----------------

        private void OnConnected(IPeer peer)
        {
            if (IsHost)
            {
                Plugin.Log.LogInfo($"[net] Peer connected {peer.Address}");
                return; // the player is registered when its Hello arrives
            }
            hostPeer = peer;
            Status = "Connected, waiting for welcome...";
            SendToPeer(peer, Msg.Hello, w =>
            {
                w.Put(Protocol.Version);
                w.Put(LocalName);
                w.Put(Plugin.Version);
                w.Put(RejoinAs);
                Api.ApiMods.Write(w, Api.ApiMods.Local());   // (the host refuses us if an Everyone mod differs)
            });
        }

        private void OnDisconnected(IPeer peer, string why)
        {
            if (IsHost)
            {
                var p = Players.Values.FirstOrDefault(x => x.Peer == peer);
                if (p == null) return;
                RemovePlayer(p.Id);
                Broadcast(Msg.PlayerLeft, w => w.Put(p.Id));
                return;
            }
            if (ended) return;
            Dropped = Ready && !Status.StartsWith("Refused") && !why.StartsWith("closed") && why != "refused";
            if (!Status.StartsWith("Refused")) Status = "Disconnected (" + why + ")";
            Plugin.Log.LogInfo($"[net] Disconnected from host: {why}");
            foreach (var id in Players.Keys.ToList()) RemovePlayer(id);
            Stop();
        }

        private void OnReceived(IPeer peer, byte[] data, DeliveryMethod method)
        {
            if (data == null || data.Length < 1) return;
            var type = (Msg)data[0];
            NetStats.In(type, data.Length);

            if (IsHost)
            {
                if (Kicking(peer)) return;   // on its way out: nothing more from it
                var sender = Players.Values.FirstOrDefault(x => x.Peer == peer);
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (type == Msg.Hello)
                {
                    if (sender != null) Reject(sender, "a second hello", now);
                    else if (data.Length <= NetGuard.MaxHello)
                        try { HandleHello(peer, new NetDataReader(data, 2, data.Length)); }
                        catch (Exception e)
                        {
                            Plugin.Log.LogWarning($"[net] Bad hello from {peer.Address}: {e.Message}");
                            Refuse(peer, "bad handshake");
                        }
                    return;
                }
                if (sender == null) return;   // not welcomed (yet)
                if (data.Length < 2) { Reject(sender, "a packet without header", now); return; }
                if (sender.Guard != null && !sender.Guard.Admit(type, data.Length, method, now))
                {
                    NetStats.Drop(type);
                    KickIfAbusive(sender);
                    return;
                }
                data[1] = sender.Id; // a guest can only speak for itself
                if (!NetGuard.Rules[(byte)type].ToHost)
                    foreach (var other in Players.Values)
                        if (other.Peer != null && other.Peer != peer)
                            try { Out(other.Peer, data, data.Length, method); }
                            catch (Exception e) { NetGuard.HandlerError(type, sender.Id, e); }   // (a transport refusing it)
                // Kept for later arrivals only if it made sense to us (a broken look must not reach every newcomer).
                if (Dispatch(type, sender.Id, new NetDataReader(data, 2, data.Length), sender, method) && stickyKinds.Contains(type))
                    sticky[(sender.Id, type)] = data;
                return;
            }

            // Guest: everything comes from the host. Only the host itself may send the host's messages (the host
            // never relays a guest's copy; this is the second lock).
            if (data.Length < 2 || !NetGuard.Known(type) || NetGuard.Rules[(byte)type].HostSends && data[1] != Protocol.HostId)
            {
                NetStats.Drop(type);
                return;
            }
            try
            {
                switch (type)
                {
                    case Msg.Welcome: HandleWelcome(new NetDataReader(data, 2, data.Length)); return;
                    case Msg.Refuse:
                        Status = "Refused: " + new NetDataReader(data, 2, data.Length).GetString();
                        Plugin.Log.LogWarning("[net] " + Status);
                        return;
                    case Msg.PlayerJoined:
                    {
                        var r = new NetDataReader(data, 2, data.Length);
                        AddPlayer(r.GetByte(), r.GetString(), null);
                        return;
                    }
                    case Msg.PlayerLeft:
                        RemovePlayer(new NetDataReader(data, 2, data.Length).GetByte());
                        return;
                }
            }
            catch (Exception e) { NetGuard.HandlerError(type, data[1], e); return; }
            Dispatch(type, data[1], new NetDataReader(data, 2, data.Length), null, method);
        }

        // False: the handler choked on it.
        private bool Dispatch(Msg type, byte from, NetDataReader r, PlayerInfo sender = null, DeliveryMethod method = DeliveryMethod.ReliableOrdered)
        {
            if (from == LocalId || !handlers.TryGetValue(type, out var h)) return true;
            Receiving = method;
            try { h(from, r); return true; }
            catch (Exception e)
            {
                NetGuard.HandlerError(type, from, e);
                if (sender?.Guard != null && NetGuard.IsMalformed(e))
                {
                    sender.Guard.Bad($"malformed {type}", UnityEngine.Time.realtimeSinceStartup);
                    KickIfAbusive(sender);
                }
                return false;
            }
        }

        private void Reject(PlayerInfo sender, string why, float now)
        {
            sender.Guard?.Bad(why, now);
            KickIfAbusive(sender);
        }

        // Host: a guest that keeps sending junk goes (bounded junk is just dropped; see NetGuard.KickScore).
        private void KickIfAbusive(PlayerInfo p)
        {
            if (p.Guard == null || !p.Guard.Abusive) return;
            p.Guard.Report(UnityEngine.Time.realtimeSinceStartup);
            string reason = $"too much invalid network traffic ({p.Guard.LastReason})";
            PeerGuard.Kicks++;
            PeerGuard.LastKickReason = reason;
            Plugin.Log.LogWarning($"[net] Kicking {p}: {reason}");
            Kick(p.Id, reason);
        }

        private void HandleHello(IPeer peer, NetDataReader r)
        {
            int version = r.GetInt();
            string name = NetGuard.CleanName(r.GetString());
            string modVersion = r.AvailableBytes > 0 ? r.GetString() : "?";
            byte rejoinAs = r.AvailableBytes > 0 ? r.GetByte() : (byte)0;
            if (version != Protocol.Version)
            {
                Refuse(peer, $"different mod version (host has {Plugin.Version}, you have {NetGuard.CleanName(modVersion)})");
                return;
            }
            var mods = r.AvailableBytes > 0 ? Api.ApiMods.Read(r) : new List<Api.MpMod>();
            string modProblem = Api.ApiMods.Refusal(Api.ApiMods.Local(), mods);
            if (modProblem != null)
            {
                Refuse(peer, modProblem);
                return;
            }
            // A guest coming back after a lost connection: its old connection hasn't timed out here yet. Only from the
            // same machine (IP, or Steam account), so nobody else can push a player out by claiming its id and name.
            if (rejoinAs != 0 && rejoinAs != LocalId && Players.TryGetValue(rejoinAs, out var old) && old.Name == name && old.Peer != peer
                && old.Peer != null && NetGuard.Machine(old.Peer.Address) == NetGuard.Machine(peer.Address))
            {
                Plugin.Log.LogInfo($"[net] {old} is back; dropping its old connection");
                var oldPeer = old.Peer;
                RemovePlayer(old.Id);
                Broadcast(Msg.PlayerLeft, w => w.Put(old.Id));
                oldPeer?.Disconnect();
            }
            if (Players.Count >= MaxPlayers)
            {
                Refuse(peer, "the session is full");
                return;
            }
            byte id = 2;
            if (rejoinAs > Protocol.HostId && !Players.ContainsKey(rejoinAs)) id = rejoinAs;
            while (Players.ContainsKey(id)) id++;
            // tell the newcomer about everyone, then everyone about the newcomer
            SendToPeer(peer, Msg.Welcome, w =>
            {
                w.Put(id);
                w.Put(Players.Count);
                foreach (var p in Players.Values) { w.Put(p.Id); w.Put(p.Name); }
            });
            Broadcast(Msg.PlayerJoined, w => { w.Put(id); w.Put(name); });
            AddPlayer(id, name, peer, mods);
            foreach (var kv in sticky)
                Out(peer, kv.Value, kv.Value.Length, DeliveryMethod.ReliableOrdered);
        }

        private void HandleWelcome(NetDataReader r)
        {
            LocalId = r.GetByte();
            int n = r.GetInt();
            Players[LocalId] = new PlayerInfo { Id = LocalId, Name = LocalName };
            for (int i = 0; i < n; i++) AddPlayer(r.GetByte(), r.GetString(), null);
            Status = $"Connected as #{LocalId} ({transport.Name})";
            Plugin.Log.LogInfo($"[net] Welcomed as #{LocalId}; {n} player(s) here");
            Welcomed?.Invoke();
        }

        private void AddPlayer(byte id, string name, IPeer peer, List<Api.MpMod> mods = null)
        {
            if (id == LocalId) return;
            var p = new PlayerInfo { Id = id, Name = name, Peer = peer, Mods = mods };
            if (peer != null) p.Guard = new PeerGuard(p);
            Players[id] = p;
            Plugin.Log.LogInfo($"[net] {p} joined");
            PlayerJoined?.Invoke(p);
        }

        private void RemovePlayer(byte id)
        {
            if (!Players.TryGetValue(id, out var p) || id == LocalId) return;
            Players.Remove(id);
            foreach (var key in sticky.Keys.Where(k => k.Item1 == id).ToList()) sticky.Remove(key);
            Plugin.Log.LogInfo($"[net] {p} left");
            PlayerLeft?.Invoke(p);
        }

        private NetDataWriter DiscoveryReply()
        {
            var w = new NetDataWriter();
            w.Put((byte)Msg.Discover);
            w.Put(Protocol.Version);
            w.Put(LocalName);
            w.Put(Players.Count);
            w.Put(Plugin.Settings.Port.Value);
            return w;
        }
    }

    // Finds hosts on the LAN by broadcasting a probe to the default port.
    internal class LanDiscovery : INetEventListener
    {
        public readonly List<(string name, IPEndPoint endPoint, int players)> Found = new List<(string, IPEndPoint, int)>();
        private readonly NetManager net;

        public LanDiscovery()
        {
            net = new NetManager(this) { UnconnectedMessagesEnabled = true, IPv6Enabled = false };
            net.Start();
        }

        public void Probe(int port)
        {
            Found.Clear();
            var w = new NetDataWriter();
            w.Put((byte)Msg.Discover);
            net.SendBroadcast(w, port);
        }

        public void Poll() => net.PollEvents();
        public void Stop() => net.Stop();

        public void OnNetworkReceiveUnconnected(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type)
        {
            try
            {
                if (reader.GetByte() != (byte)Msg.Discover) return;
                int version = reader.GetInt();
                string name = reader.GetString();
                int players = reader.GetInt();
                int port = reader.GetInt();
                var ep = new IPEndPoint(remote.Address, port);
                if (version == Protocol.Version && !Found.Any(f => f.endPoint.Equals(ep)))
                    Found.Add((name, ep, players));
            }
            catch { }
            finally { reader.Recycle(); }
        }

        public void OnPeerConnected(NetPeer peer) { }
        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) { }
        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod) => reader.Recycle();
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        public void OnConnectionRequest(ConnectionRequest request) => request.Reject();
    }
}
