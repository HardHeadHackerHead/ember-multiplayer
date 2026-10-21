using System;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using LiteNetLib;
using LiteNetLib.Utils;

namespace EmbervilleMP.Api
{
    // Mods' channels over one message id (Msg.ModMessage): [target][arg][channel key][payload].
    //   target: ToAll, ToOne (arg: player id), ToHost, ToScene (arg: the sender's scene hash)
    //   key: NetIds.Hash of the channel id (4 bytes; two ids with the same hash are refused at registration)
    // Guests send everything to the host, which checks the channel's limits for that guest, then delivers and relays
    // with the sender's id in the packet header (the guest can't put another id there: the host overwrites it).
    // A channel the receiver doesn't know is counted, logged once and dropped; the host still relays it (it may be a
    // ClientOnly mod's), with default limits.
    internal static class ApiChannels
    {
        public const byte ToAll = 0, ToOne = 1, ToHost = 2, ToScene = 3;
        public const int HeaderBytes = 9;
        private static readonly ChannelOptions UnknownLimits = new ChannelOptions { MaxSize = 4096, Rate = 20f, Burst = 40f };

        private static readonly Dictionary<int, MpChannel> byKey = new Dictionary<int, MpChannel>();
        private static readonly Dictionary<string, MpChannel> byId = new Dictionary<string, MpChannel>();

        // Counters (tests, F8): what the host dropped, what nobody here knew.
        public static int Delivered, Relayed, RateDrops, SizeDrops, SenderDrops, UnknownDrops;

        public static MpChannel Register(string id, string mod, ChannelOptions options)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("a channel needs an id");
            if (id.StartsWith("mp.") && mod != ApiGuard.OurId) throw new ArgumentException($"channel ids starting with \"mp.\" are the multiplayer mod's: prefix {id} with your mod's");
            options = options ?? new ChannelOptions();
            options.MaxSize = Math.Max(0, Math.Min(options.MaxSize, NetGuard.Rules[(byte)Msg.ModMessage].MaxBytes - 64));
            if (byId.TryGetValue(id, out var existing))
            {
                if (existing.Mod != mod) throw new InvalidOperationException($"channel {id} already belongs to {existing.Mod}");
                return existing;   // (registered again, e.g. a mod reloading: the same channel)
            }
            int key = NetIds.Hash(id);
            if (byKey.TryGetValue(key, out var clash)) throw new InvalidOperationException($"channel id {id} has the same hash as {clash.Id}: pick another id");
            var ch = new MpChannel(id, mod, options, key);
            byKey[key] = ch;
            byId[id] = ch;
            return ch;
        }

        public static MpChannel Find(string id) => byId.TryGetValue(id, out var c) ? c : null;

        // ---------------- sending ----------------

        public static bool Send(MpChannel ch, byte target, byte player, Action<NetDataWriter> write, bool reliable)
        {
            if (!ApiGuard.OnMainThread("MpChannel.Send")) return false;
            var s = Session.Current;
            if (s == null || !s.Ready) return false;
            var w = new NetDataWriter();
            if (write != null && !ApiGuard.Run(ch.Mod, $"writing a message on {ch.Id}", () => { write(w); return true; }, false)) return false;
            byte[] payload = w.CopyData();
            if (payload.Length > ch.Options.MaxSize)
            {
                if (ApiGuard.Once("size:" + ch.Id)) Plugin.Log.LogError($"[api] {ch.Mod}: a message on {ch.Id} is {payload.Length} bytes, over its MaxSize {ch.Options.MaxSize}: not sent");
                return false;
            }
            var method = reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
            if (target == ToOne && player == s.LocalId || target == ToHost && s.IsHost)
            {
                Deliver(ch, s.LocalId, payload);
                return true;
            }
            int arg = target == ToOne ? player : target == ToScene ? NetIds.SceneHash(Game.SceneName) : 0;
            if (s.IsHost)
            {
                Route(s, s.LocalId, target, arg, ch.Key, payload, method, deliverHere: false);
                return true;
            }
            s.Send(Msg.ModMessage, x => Frame(x, target, arg, ch.Key, payload), method);
            return true;
        }

        internal static void Frame(NetDataWriter w, byte target, int arg, int key, byte[] payload)
        {
            w.Put(target);
            w.Put(arg);
            w.Put(key);
            w.Put(payload);
        }

        // ---------------- receiving ----------------

        private struct Bucket
        {
            public float Tokens, At;
            public bool Take(float rate, float burst, float now)
            {
                Tokens = At == 0f ? burst : Math.Min(burst, Tokens + (now - At) * rate);
                At = now;
                if (Tokens < 1f) return false;
                Tokens -= 1f;
                return true;
            }
        }
        private static readonly Dictionary<(byte, int), Bucket> buckets = new Dictionary<(byte, int), Bucket>();
        private static int unknownKeysLogged;   // (junk makes up keys: at most 10 lines)
        private static float reportAt;

        public static void OnMessage(Session s, byte from, NetDataReader r, DeliveryMethod method)
        {
            byte target = r.GetByte();
            int arg = r.GetInt();
            int key = r.GetInt();
            byte[] payload = r.GetRemainingBytes();
            byKey.TryGetValue(key, out var ch);
            if (!s.IsHost)
            {
                // (everything comes from the host, which checked it; `from` is its stamp)
                if (ch == null) { Unknown(key, from); return; }
                Deliver(ch, from, payload);
                return;
            }
            // Host: a guest's message. Its channel's rules for that guest, then where it goes.
            float now = UnityEngine.Time.realtimeSinceStartup;
            var limits = ch?.Options ?? UnknownLimits;
            if (ch != null && !MaySend(s, ch, from))
            {
                SenderDrops++;
                Bad(s, from, $"{ch.Id} (not theirs to send)", now);
                return;
            }
            if (payload.Length > limits.MaxSize)
            {
                SizeDrops++;
                Bad(s, from, $"{ch?.Id ?? "a mod message"} too big ({payload.Length} bytes)", now);
                return;
            }
            buckets.TryGetValue((from, key), out var b);
            bool admitted = b.Take(Math.Max(0.01f, limits.Rate), Math.Max(1f, limits.Burst), now);
            buckets[(from, key)] = b;
            if (!admitted)
            {
                RateDrops++;
                if (now >= reportAt)
                {
                    reportAt = now + 5f;
                    Plugin.Log.LogWarning($"[api] Dropping #{from}'s messages on {ch?.Id ?? "channel " + key} over its rate limit ({limits.Rate}/s, burst {limits.Burst}); {RateDrops} so far");
                }
                return;
            }
            if (ch == null) Unknown(key, from);
            if (target == ToScene)
            {
                // (the sender's scene as we know it; its own word only while we don't)
                var p = Multiplayer.GetPlayer(from);
                string scene = p?.Scene;
                if (!string.IsNullOrEmpty(scene)) arg = NetIds.SceneHash(scene);
            }
            Route(s, from, target, arg, key, payload, method, deliverHere: ch != null);
        }

        // Host: delivers here if it's for us, relays to the guests it's for (never back to the sender).
        private static void Route(Session s, byte from, byte target, int arg, int key, byte[] payload, DeliveryMethod method, bool deliverHere)
        {
            byKey.TryGetValue(key, out var ch);
            var to = new List<byte>();
            bool here = false;
            switch (target)
            {
                case ToAll:
                    here = from != s.LocalId;
                    to.AddRange(s.Players.Keys.Where(id => id != s.LocalId && id != from));
                    break;
                case ToOne:
                    if (arg == s.LocalId) here = from != s.LocalId;
                    else if (arg != from && s.Players.ContainsKey((byte)arg)) to.Add((byte)arg);
                    break;
                case ToHost:
                    here = from != s.LocalId;
                    break;
                case ToScene:
                    here = from != s.LocalId && Game.InScene && NetIds.SceneHash(Game.SceneName) == arg;
                    foreach (var id in s.Players.Keys)
                    {
                        if (id == s.LocalId || id == from) continue;
                        var p = Multiplayer.GetPlayer(id);
                        if (p != null && p.Scene.Length > 0 && NetIds.SceneHash(p.Scene) == arg) to.Add(id);
                    }
                    break;
                default:
                    return;
            }
            foreach (var id in to)
            {
                s.RelayTo(id, from, Msg.ModMessage, x => Frame(x, target, arg, key, payload), method);
                if (from != s.LocalId) Relayed++;
            }
            if (here && deliverHere && ch != null) Deliver(ch, from, payload);
        }

        private static bool MaySend(Session s, MpChannel ch, byte from)
        {
            switch (ch.Options.WhoMaySend)
            {
                case WhoMaySend.HostOnly: return from == s.LocalId;
                case WhoMaySend.SceneRunnerOnly:
                    var p = Multiplayer.GetPlayer(from);
                    string scene = p?.Scene;
                    if (string.IsNullOrEmpty(scene)) return true;   // (can't tell: let it through, as Senders does)
                    byte runner = SceneAuthority.For(scene);
                    return runner == 0 || runner == from;
                default: return true;
            }
        }

        private static void Bad(Session s, byte from, string why, float now)
        {
            if (s.Players.TryGetValue(from, out var info) && info.Guard != null) info.Guard.Bad(why, now);
            if (ApiGuard.Once("bad:" + from + ":" + why.Split(' ')[0])) Plugin.Log.LogWarning($"[api] Dropped #{from}'s message: {why} (more like it: counted, not logged)");
        }

        private static void Unknown(int key, byte from)
        {
            UnknownDrops++;
            if (++unknownKeysLogged <= 10 && ApiGuard.Once("unknown:" + key))
                Plugin.Log.LogWarning($"[api] A message from #{from} on a channel this copy doesn't know (key {key}): dropped (a mod one side doesn't have; logged once)");
        }

        private static void Deliver(MpChannel ch, byte from, byte[] payload)
        {
            var p = Multiplayer.GetPlayer(from);
            if (p == null) return;
            Delivered++;
            ch.Deliver(p, payload);
        }


        public static void OnSessionEnded() => buckets.Clear();
    }
}
