using System;
using LiteNetLib.Utils;

namespace EmbervilleMP.Api
{
    public enum WhoMaySend : byte
    {
        Anyone = 0,
        HostOnly = 1,          // a guest's message on it is dropped by the host
        SceneRunnerOnly = 2,   // only the player running the sender's scene (the host checks)
    }

    // A channel's limits. The host enforces them for every guest (and drops what breaks them); MaxSize is also checked
    // when sending.
    public sealed class ChannelOptions
    {
        public int MaxSize = 1024;       // payload bytes per message
        public float Rate = 10f;         // messages per second, per sender
        public float Burst = 20f;        // how many may come at once
        public WhoMaySend WhoMaySend = WhoMaySend.Anyone;
    }

    // A mod's own messages between the players (Multiplayer.RegisterChannel). Main thread only. Sending to yourself
    // (SendTo(LocalPlayer), SendToHost on the host) delivers at once, here; SendToAll / SendToScene never include you.
    public sealed class MpChannel
    {
        public string Id { get; }
        public string Mod { get; }
        public ChannelOptions Options { get; }
        internal readonly int Key;

        // (from, payload): from is the verified sender (the host stamps it; it can't be spoofed).
        public event Action<MpPlayer, NetDataReader> Received;

        internal MpChannel(string id, string mod, ChannelOptions options, int key)
        {
            Id = id;
            Mod = mod;
            Options = options;
            Key = key;
        }

        public bool SendToAll(Action<NetDataWriter> write, bool reliable = true) => ApiChannels.Send(this, ApiChannels.ToAll, 0, write, reliable);
        public bool SendTo(MpPlayer player, Action<NetDataWriter> write, bool reliable = true) =>
            player != null && ApiChannels.Send(this, ApiChannels.ToOne, player.Id, write, reliable);
        public bool SendToHost(Action<NetDataWriter> write, bool reliable = true) => ApiChannels.Send(this, ApiChannels.ToHost, 0, write, reliable);
        // Everyone else in our scene (as each player last reported it).
        public bool SendToScene(Action<NetDataWriter> write, bool reliable = true) => ApiChannels.Send(this, ApiChannels.ToScene, 0, write, reliable);

        internal void Deliver(MpPlayer from, byte[] payload)
        {
            var handlers = Received;
            if (handlers == null) return;
            ApiGuard.Raise(handlers, $"channel {Id}", d => ((Action<MpPlayer, NetDataReader>)d)(from, new NetDataReader(payload)));
        }

        public override string ToString() => Id;
    }
}
