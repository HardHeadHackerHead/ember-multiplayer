using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;

namespace EmbervilleMP.Net
{
    // What a message may look like when a guest sends it: its size, how often, and who may send it at all.
    // Limits are generous (several times normal play, including join bursts and boss fights): they exist to stop
    // a broken or hostile guest, never to shape honest traffic.
    internal struct MsgRule
    {
        public bool Known;
        public bool HostSends;     // only the host sends these: a guest's copy is dropped (never relayed, never handled)
        public bool ToHost;        // guest -> host only: handled by the host, not relayed to the other guests
        public int MaxBytes;       // whole packet, header included
        public float Rate, Burst;  // messages per second, and how many may come at once
        public float ByteRate, ByteBurst;
    }

    internal static class NetGuard
    {
        public static readonly MsgRule[] Rules = new MsgRule[256];

        public const int MaxHello = 16 * 1024;   // (the mods each side declares ride on it: Api/ApiMods.cs)
        public const int MaxUnreliable = 4 * 1024;   // an unreliable packet is one datagram; anything bigger is junk
        public const int MaxNameLength = 32;
        // All of one guest's accepted traffic together.
        public const float TotalRate = 1500f, TotalBurst = 6000f, TotalByteRate = 2f * 1024 * 1024, TotalByteBurst = 8f * 1024 * 1024;

        // Misbehaviour score: each bad packet adds points, the score drains over time, a guest reaching KickScore is
        // removed. A malformed / unknown / host-only / oversized packet is 1 point; a packet over a rate limit is
        // 0.02 (a flood is mostly harmless once dropped: it takes ~50,000 dropped packets beyond the drain to kick).
        public const float BadPacket = 1f, OverRate = 0.02f, Drain = 25f, KickScore = 1000f;

        private const int K = 1024, M = 1024 * 1024;

        static NetGuard()
        {
            void Set(Msg m, int max = 64 * K, float rate = 100, float burst = 500, float byteRate = 256 * K, float byteBurst = M,
                bool hostSends = false, bool toHost = false) =>
                Rules[(byte)m] = new MsgRule
                {
                    Known = true, HostSends = hostSends, ToHost = toHost, MaxBytes = max,
                    Rate = rate, Burst = burst, ByteRate = byteRate, ByteBurst = Math.Max(byteBurst, max),
                };
            foreach (Msg m in Enum.GetValues(typeof(Msg))) Set(m);   // defaults, then the specifics

            // session (the host's alone)
            Set(Msg.Hello, max: MaxHello, rate: 1, burst: 2, toHost: true);
            foreach (var m in new[] { Msg.Welcome, Msg.PlayerJoined, Msg.PlayerLeft, Msg.Discover, Msg.Refuse }) Set(m, hostSends: true);
            Set(Msg.WorldRequest, max: 64, rate: 0.2f, burst: 3, toHost: true);   // answered with the whole world: rare
            Set(Msg.WorldSnapshot, max: 256 * M, hostSends: true);

            // players: 20 Hz state, look on change, projectiles, voice (~50 packets/s while talking)
            Set(Msg.State, max: 2 * K, rate: 60, burst: 120, byteRate: 64 * K, byteBurst: 128 * K);
            Set(Msg.Look, max: 256 * K, rate: 10, burst: 20, byteRate: M, byteBurst: 2 * M);
            Set(Msg.Chat, max: 16 * K, rate: 20, burst: 100, byteRate: 64 * K, byteBurst: 256 * K);
            Set(Msg.PlayerProjectile, max: K, rate: 30, burst: 60);
            Set(Msg.Voice, max: 64 * K, rate: 120, burst: 240, byteRate: 256 * K, byteBurst: 512 * K);

            // combat (a guest runs the scenes the host isn't in: 15 Hz enemy batches, deaths, spawns...)
            Set(Msg.EnemyStates, max: 16 * K, rate: 300, burst: 600, byteRate: 512 * K, byteBurst: M);
            Set(Msg.EnemyHit, max: 16 * K, rate: 60, burst: 200);
            Set(Msg.EnemySpawn, max: 4 * K, rate: 30, burst: 100);
            Set(Msg.EnemyWake, max: 64, rate: 10, burst: 30);
            Set(Msg.EnemyHandoff, max: 256 * K, rate: 2, burst: 10);
            // the boss and the trackers (a guest runs the scenes the host isn't in): small snapshots on change + every few s
            Set(Msg.EggStates, max: 4 * K, rate: 20, burst: 60);
            Set(Msg.MatriarchState, max: 256, rate: 10, burst: 30);
            Set(Msg.TrackerPull, max: 64, rate: 20, burst: 60);
            Set(Msg.TrackerCamo, max: 2 * K, rate: 20, burst: 60);
            // enemy effects: lists on change, popups, our effects on puppets (an area over a crowd: enter and exit each)
            Set(Msg.EnemyEffects, max: 16 * K, rate: 30, burst: 100);
            Set(Msg.EnemyVisual, max: 4 * K, rate: 60, burst: 200);
            Set(Msg.EnemyEffectIntent, max: K, rate: 60, burst: 200);
            Set(Msg.EnemyFxHandoff, max: 64 * K, rate: 2, burst: 10);
            Set(Msg.EnemyHitRaw, max: 16 * K, rate: 60, burst: 200);
            // replayed attacks: an interruption, the aim at a player (at most one each per attack start)
            Set(Msg.EnemyAttackCancel, max: 64, rate: 60, burst: 200);
            Set(Msg.EnemyAttackAim, max: 64, rate: 60, burst: 200);
            // the fight beyond one runner: a dead list per arrival (twice), the fighting enemies on change (<= 4/s)
            Set(Msg.EnemyDeadList, max: 16 * K, rate: 10, burst: 40);
            Set(Msg.EnemyCombat, max: 2 * K, rate: 10, burst: 40);

            // world progress (a guest's whole lore/recipe lists arrive at once when it joins)
            Set(Msg.WorldBool, max: K, rate: 100, burst: 500);
            Set(Msg.WorldListAdd, max: 4 * K, rate: 200, burst: 3000);
            Set(Msg.WorldTinetia, max: 64, rate: 10, burst: 50);
            Set(Msg.DialogueVar, max: 4 * K, rate: 20, burst: 100);   // each one searches the scene's dialogue triggers
            Set(Msg.TimeSync, hostSends: true);
            Set(Msg.QuestSnapshot, max: 256 * K, rate: 10, burst: 20);

            // world objects
            Set(Msg.ObjState, max: 64 * K, rate: 200, burst: 1000);
            Set(Msg.ObjSnapshot, max: M, rate: 10, burst: 30, byteRate: 2 * M, byteBurst: 4 * M);
            Set(Msg.ObjIntent, max: 16 * K, rate: 50, burst: 200);
            Set(Msg.TrapClock, hostSends: true);

            // loot
            Set(Msg.LootRules, hostSends: true);
            Set(Msg.LootChestRecord, max: 64 * K, rate: 20, burst: 100, toHost: true);
            Set(Msg.LootSpawn, max: M, rate: 50, burst: 200, byteRate: M, byteBurst: 2 * M);
            Set(Msg.LootDropRequest, max: 16 * K, rate: 10, burst: 30);   // each item is a drop the runner spawns
            Set(Msg.LootPickup, max: K, rate: 30, burst: 100);

            // estate (the whole estate's changes go out at once when someone arrives)
            Set(Msg.EstateRules, hostSends: true);
            Set(Msg.EstateOps, max: M, rate: 50, burst: 200, byteRate: M, byteBurst: 2 * M);
            Set(Msg.EstateClaim, max: K, rate: 20, burst: 60);
            Set(Msg.EstateGrant, max: K, rate: 20, burst: 60);
            Set(Msg.CutscenePlay, max: 64 * K, rate: 10, burst: 30);

            // players' extras: effects on change, skills' spawned objects (several per skill) and their ~10 Hz updates,
            // party vitals (<= 5 Hz + every 2 s), fishing phases, pings, deaths
            Set(Msg.PlayerEffects, max: 16 * K, rate: 20, burst: 60);
            Set(Msg.SkillFxSpawn, max: K, rate: 60, burst: 200);
            Set(Msg.SkillFxUpdate, max: 256, rate: 100, burst: 300);
            Set(Msg.PartyStatus, max: K, rate: 20, burst: 60);
            Set(Msg.Fishing, max: 64, rate: 20, burst: 60);
            Set(Msg.Ping, max: K, rate: 5, burst: 20);
            Set(Msg.DeathNotice, max: 4 * K, rate: 10, burst: 30);
            Set(Msg.CoopRules, hostSends: true);

            // teammates' presence: sounds (a batch per frame with any, footsteps aren't sent), one-off effects,
            // floating text (damage over time ticks), animator speed (each attack with a multiplier: twice)
            Set(Msg.PlayerSounds, max: 512, rate: 40, burst: 120);
            Set(Msg.PlayerMoment, max: 256, rate: 10, burst: 30);
            Set(Msg.PlayerFloatText, max: 256, rate: 40, burst: 120);
            Set(Msg.PlayerAnimSpeed, max: 64, rate: 30, burst: 90);

            // world map: fog in chunks of up to 4000 cells (32 KB); the whole map only goes host -> guest, on request
            Set(Msg.MapOps, max: 128 * K, rate: 50, burst: 200, byteRate: 512 * K, byteBurst: M);
            Set(Msg.MapQuickPortal, max: 4 * K, rate: 20, burst: 100);
            Set(Msg.MapRequest, max: 64, rate: 0.5f, burst: 5, toHost: true);   // answered with the whole map

            // the world's stash and key items: guests ask, the host decides (a chest's whole contents: <= ~30 items,
            // each with its rolled values). Moving items in an open chest reports each change; a scene load sets up
            // a few storage chests at once.
            Set(Msg.StashAsk, max: 64 * K, rate: 20, burst: 100, toHost: true);
            Set(Msg.StashState, max: 64 * K, hostSends: true);
            Set(Msg.StashTake, max: 64 * K, rate: 20, burst: 60, toHost: true);
            Set(Msg.KeyItemsChange, max: 16 * K, rate: 10, burst: 30, toHost: true);
            Set(Msg.KeyItemsState, max: 64 * K, hostSends: true);
            // story and world scripts: a click in the cartographer's panel, a script setting the clock, rewards
            Set(Msg.CartographerBuy, max: 64, rate: 5, burst: 20, toHost: true);
            Set(Msg.CartographerResult, max: 64, hostSends: true);
            Set(Msg.TimeSet, max: 64, rate: 5, burst: 20, toHost: true);
            Set(Msg.CharReward, max: 8 * K, rate: 10, burst: 50);
            // world objects' one-off reactions: every hit on a training dummy (a fast combo, a multi-hit skill)
            Set(Msg.WorldMoment, max: 256, rate: 30, burst: 90);
            Set(Msg.SaveNow, max: 64, hostSends: true);   // the host's coordinated saves
            // party wipes (once per wipe) and the Matriarch's events (a few per fight phase); a guest runs the scenes
            // the host isn't in
            Set(Msg.SceneWipe, max: 64, rate: 2, burst: 10);
            Set(Msg.BossEvent, max: 64, rate: 10, burst: 30);
            // other mods (Api/): the host checks each channel's own limits on top (ApiChannels); a store dump is the
            // host's alone (a guest only sets values and asks); spawns are the scene runner's, checked by the host
            Set(Msg.ModMessage, max: 64 * K, rate: 300, burst: 900, byteRate: 512 * K, byteBurst: M, toHost: true);
            Set(Msg.ModState, max: 16 * K, rate: 50, burst: 200, toHost: true);
            Set(Msg.ModSpawn, max: 32 * K, rate: 50, burst: 200, toHost: true);
        }

        // A count read from the wire, before anything is allocated or looped over for it: each item takes at least
        // minBytes of what's left, so a count the packet can't hold is junk (FormatException: the sender's fault).
        public static int Count(int n, NetDataReader r, int minBytes = 1)
        {
            if (n < 0 || (long)n * Math.Max(1, minBytes) > r.AvailableBytes) throw new FormatException($"count {n} doesn't fit the packet");
            return n;
        }

        public static bool Known(Msg m) => Rules[(byte)m].Known;

        // A packet the payload readers rejected (cut short, or values that make no sense): the sender's fault.
        public static bool IsMalformed(Exception e) =>
            e is FormatException || e is InvalidOperationException && e.TargetSite?.DeclaringType == typeof(NetDataReader);

        // ---- handler errors: a few full reports per message type, then counts ----

        public static int ErrorsLogged, ErrorsSuppressed;
        public const float ErrorWindow = 10f;
        public const int ErrorsPerWindow = 3;
        private static readonly Dictionary<Msg, (float since, int logged, int suppressed)> errors = new Dictionary<Msg, (float, int, int)>();

        public static void HandlerError(Msg type, byte from, Exception e)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            errors.TryGetValue(type, out var w);
            if (now - w.since >= ErrorWindow)
            {
                if (w.suppressed > 0) Plugin.Log.LogError($"[net] ({w.suppressed} more {type} handler errors in the last {ErrorWindow:F0} s not shown)");
                w = (now, 0, 0);
            }
            if (w.logged < ErrorsPerWindow)
            {
                w.logged++;
                ErrorsLogged++;
                // Junk from a peer needs no stack trace; our own bugs do.
                string what = IsMalformed(e) ? "malformed packet: " + e.Message : e.ToString();
                Plugin.Log.LogError($"[net] Handler for {type} from #{from} failed: {what}");
            }
            else
            {
                w.suppressed++;
                ErrorsSuppressed++;
            }
            errors[type] = w;
        }

        // "ip:port" -> "ip" (LAN); a Steam id stays as it is. Same machine = same result.
        public static string Machine(string address)
        {
            if (string.IsNullOrEmpty(address)) return "";
            int colon = address.LastIndexOf(':');
            return colon > 0 && address.IndexOf(':') == colon && int.TryParse(address.Substring(colon + 1), out _) ? address.Substring(0, colon) : address;
        }

        public static string CleanName(string name)
        {
            name = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length > MaxNameLength) name = name.Substring(0, MaxNameLength).Trim();
            return name.Length == 0 ? "Player" : name;
        }
    }

    // Host side: one per guest. Admits or drops each packet, keeps the misbehaviour score, logs (rate-limited).
    internal class PeerGuard
    {
        private struct Bucket
        {
            public float Tokens, At;
            public bool Take(float cost, float rate, float burst, float now)
            {
                Tokens = At == 0f ? burst : Math.Min(burst, Tokens + (now - At) * rate);
                At = now;
                if (Tokens < cost) return false;
                Tokens -= cost;
                return true;
            }
        }

        public readonly PlayerInfo Player;
        public float Score { get; private set; }
        public int Dropped, BadPackets, OverRate;
        public string LastReason = "";
        public bool Kicked;
        public static int TotalDropped, TotalBad, TotalOverRate, Kicks;
        public static string LastKickReason = "";

        private readonly Bucket[] msgs = new Bucket[256], bytes = new Bucket[256];
        private Bucket allMsgs, allBytes;
        private float scoreAt;
        private readonly Dictionary<string, int> unreported = new Dictionary<string, int>();
        private float reportAt;

        public PeerGuard(PlayerInfo player) { Player = player; }

        // False: drop it (not handled, not relayed).
        public bool Admit(Msg type, int length, DeliveryMethod method, float now)
        {
            var rule = NetGuard.Rules[(byte)type];
            if (!rule.Known) return Bad($"unknown message type {(byte)type}", now);
            if (rule.HostSends) return Bad($"{type} (only the host sends that)", now);
            if (length > rule.MaxBytes) return Bad($"{type} too big ({length} bytes)", now);
            if (length > NetGuard.MaxUnreliable && method != DeliveryMethod.ReliableOrdered) return Bad($"unreliable {type} too big ({length} bytes)", now);
            if (!msgs[(byte)type].Take(1f, rule.Rate, rule.Burst, now) || !bytes[(byte)type].Take(length, rule.ByteRate, rule.ByteBurst, now))
                return Over($"{type} over its rate limit", now);
            if (!allMsgs.Take(1f, NetGuard.TotalRate, NetGuard.TotalBurst, now) || !allBytes.Take(length, NetGuard.TotalByteRate, NetGuard.TotalByteBurst, now))
                return Over("over the total rate limit", now);
            return true;
        }

        public bool Bad(string why, float now)
        {
            BadPackets++;
            TotalBad++;
            Add(why, NetGuard.BadPacket, now);
            return false;
        }

        private bool Over(string why, float now)
        {
            OverRate++;
            TotalOverRate++;
            Add(why, NetGuard.OverRate, now);
            return false;
        }

        private void Add(string why, float points, float now)
        {
            Dropped++;
            TotalDropped++;
            Score = Math.Max(0f, Score - (now - scoreAt) * NetGuard.Drain) + points;
            scoreAt = now;
            LastReason = why;
            unreported.TryGetValue(why, out int n);
            unreported[why] = n + 1;
            if (now >= reportAt) Report(now);
        }

        public bool Abusive => !Kicked && Score >= NetGuard.KickScore;

        // At most one line per guest every 5 s.
        public void Report(float now)
        {
            if (unreported.Count == 0) return;
            reportAt = now + 5f;
            string list = string.Join(", ", unreported.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Value}x {kv.Key}"));
            Plugin.Log.LogWarning($"[net] Dropped bad traffic from {Player}: {list} (score {Score:F0}/{NetGuard.KickScore:F0})");
            unreported.Clear();
        }

        public void Tick(float now)
        {
            if (now >= reportAt) Report(now);
        }
    }
}
