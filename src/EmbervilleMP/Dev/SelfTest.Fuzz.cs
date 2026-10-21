using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // A guest throws junk at the host through the raw send path: every message type (and unknown ones) empty,
        // cut short, random, with absurd counts, oversized, spoofing its sender id, out of order, host-only ones,
        // a second hello. Then a flood of states. The host must stay responsive, log errors only rate-limited, keep
        // the guest (bounded junk is dropped, not punished) and still follow it normally afterwards. Last, the guest
        // keeps sending junk without end: the host kicks it, with a reason the guest gets to read.
        //
        // Message types whose handlers change the world when a packet happens to parse (flags, quests, loot, kills,
        // chat, voice) only get packets too short to parse, plus oversized ones (dropped before any handler): random
        // junk could otherwise really set a story flag in the test world. WorldRequest isn't sent at all (any
        // payload is a valid request: the host would send its world again and the guest would reload it).
        [Scenario("fuzz", Timeout = 200, Isolate = "fuzz")]
        private static IEnumerator FuzzTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            if (s == null || s.Players.Count < 2) { yield return Finish(""); yield break; }
            yield return Barrier("fuzz-ready");
            if (s.IsHost) yield return FuzzHost(s);
            else yield return FuzzGuest(s);
        }

        private class FrameWatch
        {
            public float Worst;
            public double Sum;
            public int Frames;
            public float Mean => Frames > 0 ? (float)(Sum / Frames) : 0f;
            public override string ToString() => $"worst frame {Worst * 1000f:F0} ms, mean {Mean * 1000f:F1} ms over {Frames} frames";
        }

        // Waits (measuring every frame) until done() or the timeout.
        private static IEnumerator Watch(Func<bool> done, float timeout, FrameWatch w)
        {
            float t0 = Time.realtimeSinceStartup;
            yield return null;
            while (!done() && Time.realtimeSinceStartup - t0 < timeout)
            {
                float dt = Time.unscaledDeltaTime;
                w.Worst = Mathf.Max(w.Worst, dt);
                w.Sum += dt;
                w.Frames++;
                yield return null;
            }
        }

        private static IEnumerator FuzzHost(Session s)
        {
            var guest = s.Players.Values.First(p => p.Id != s.LocalId);
            var presence = MPRunner.Instance.Presence;
            int bad0 = PeerGuard.TotalBad, over0 = PeerGuard.TotalOverRate, err0 = NetGuard.ErrorsLogged, kicks0 = PeerGuard.Kicks;

            // 0. this machine's own hitches, with nothing thrown at it (several game copies share the PC in test runs)
            var quiet = new FrameWatch();
            float t0 = Time.realtimeSinceStartup;
            yield return Watch(() => Time.realtimeSinceStartup - t0 > 3f, 5f, quiet);
            float sane = Mathf.Max(0.5f, quiet.Worst * 2f + 0.25f);
            Plugin.Log.LogInfo($"[fuzz] quiet: {quiet}; a frame up to {sane * 1000f:F0} ms counts as responsive");

            // 1. junk
            float junk0 = Time.realtimeSinceStartup;
            var junk = new FrameWatch();
            yield return Watch(() => peerData.ContainsKey("junkdone"), 90f, junk);
            yield return Barrier("junk");
            int bad = PeerGuard.TotalBad - bad0;
            Check("host dropped the junk", bad > 100, $"{bad} bad packets dropped, guest sent {(peerData.TryGetValue("junkdone", out var sent) ? sent : "?")}");
            Check("host stayed responsive through the junk", junk.Worst < sane, junk.ToString());
            Check("guest still in the session after bounded junk", s.Players.ContainsKey(guest.Id) && guest.Guard != null && !guest.Guard.Kicked,
                $"score {guest.Guard?.Score:F0}/{NetGuard.KickScore:F0}");
            Check("spoofed host messages changed nothing (still host #1, both players here)", s.LocalId == Protocol.HostId && s.Players.Count == 2 && s.Ready);
            int logged = NetGuard.ErrorsLogged - err0;
            // (at most ErrorsPerWindow full reports per message type per window: the cap grows with the message types)
            int windows = Mathf.CeilToInt((Time.realtimeSinceStartup - junk0) / NetGuard.ErrorWindow) + 1;
            int cap = NetGuard.ErrorsPerWindow * System.Enum.GetValues(typeof(Msg)).Length * windows;
            Check("handler errors logged rate-limited", logged <= cap, $"{logged} logged (at most {cap}), {NetGuard.ErrorsSuppressed} suppressed in all");

            // 2. flood
            var flood = new FrameWatch();
            yield return Watch(() => peerData.ContainsKey("flooddone"), 60f, flood);
            yield return Barrier("flood");
            int over = PeerGuard.TotalOverRate - over0;
            Check("host dropped the flood over the rate limits", over > 1000, $"{over} over the limits, guest sent {(peerData.TryGetValue("flooddone", out var fl) ? fl : "?")}");
            Check("host stayed responsive through the flood", flood.Worst < sane, flood.ToString());
            Check("guest kept after the flood", s.Players.ContainsKey(guest.Id) && !guest.Guard.Kicked, $"score {guest.Guard.Score:F0}");

            // 3. normal sync still works: the guest walks, we follow it
            presence.Remotes.TryGetValue(guest.Id, out var rp);
            Vector2 before = rp?.Latest?.Position ?? Vector2.zero;
            yield return WaitFor(() => peerData.ContainsKey("fuzzfinal"), 30f);
            yield return new WaitForSecondsRealtime(0.5f);
            presence.Remotes.TryGetValue(guest.Id, out rp);
            if (Check("guest's puppet is shown after the fuzz", rp != null && rp.Shown && rp.SecondsSinceState < 1f,
                    rp == null ? "no remote" : $"shown {rp.Shown}, last state {rp.SecondsSinceState:F1} s ago") && peerData.TryGetValue("fuzzfinal", out var fin))
            {
                var parts = fin.Split(':');
                var real = new Vector2(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture));
                Check("host saw the guest move after the fuzz", Vector2.Distance(rp.Latest.Position, before) > 0.5f, $"moved {Vector2.Distance(rp.Latest.Position, before):F2}");
                float off = Vector2.Distance(real, rp.Puppet.transform.position);
                Check("guest's puppet is where the guest really is", off < 0.1f, $"off by {off:F3}");
            }
            yield return Barrier("synced");

            // 4. junk without end: kicked, with a reason
            var abuse = new FrameWatch();
            yield return Watch(() => !s.Players.ContainsKey(guest.Id), 30f, abuse);
            Check("host kicked the guest that kept sending junk", PeerGuard.Kicks > kicks0 && !s.Players.ContainsKey(guest.Id), PeerGuard.LastKickReason);
            Check("kick reason is readable", PeerGuard.LastKickReason.Contains("invalid network traffic"), PeerGuard.LastKickReason);
            Check("host stayed responsive until the kick", abuse.Worst < sane, abuse.ToString());
            Check("host session still up", Session.Current == s && s.Ready);
            Plugin.Log.LogInfo($"[fuzz] host: {PeerGuard.TotalBad - bad0} bad, {PeerGuard.TotalOverRate - over0} over rate, {NetGuard.ErrorsLogged - err0} errors logged, {NetGuard.ErrorsSuppressed} suppressed");
            yield return Finish("host survived the fuzz");
        }

        // Full random payloads only for types whose handlers can't change the host's world when junk happens to parse
        // (the host ignores them from a guest, or they only touch that guest's own puppet). Every other type, including
        // the ones added later, only gets payloads too short to parse: at most this many bytes (default 1).
        private static readonly HashSet<Msg> fuzzAnyPayload = new HashSet<Msg>
        {
            Msg.State, Msg.Look, Msg.PlayerProjectile, Msg.EnemyStates, Msg.EnemyAttack, Msg.EnemyAttackStart, Msg.EnemySpawn,
            Msg.EnemyDamaged, Msg.EggSpawn, Msg.EggBroken,
        };
        private static readonly Dictionary<Msg, int> fuzzShortOnly = new Dictionary<Msg, int>
        {
            { Msg.WorldBool, 4 }, { Msg.WorldTinetia, 3 }, { Msg.QuestSnapshot, 3 }, { Msg.EnemyHit, 3 }, { Msg.EnemyDied, 12 },
            { Msg.EnemyWake, 3 }, { Msg.EggHit, 7 }, { Msg.EnemyHandoff, 4 }, { Msg.Voice, 0 },
        };
        // Any payload is a valid request (answered with the whole world / map): not fuzzed.
        private static readonly HashSet<Msg> fuzzSkip = new HashSet<Msg> { Msg.WorldRequest, Msg.MapRequest };

        private static IEnumerator FuzzGuest(Session s)
        {
            var rnd = new System.Random(1234);
            int sent = 0, failed = 0;
            var me = Game.LocalPlayer;

            void Raw(byte type, byte[] payload, DeliveryMethod method, bool runt = false)
            {
                var w = new NetDataWriter();
                w.Put(type);
                if (!runt)
                {
                    w.Put((byte)rnd.Next(256));   // a sender id of its choice (the host overwrites it)
                    if (payload != null) w.Put(payload);
                }
                try { s.SendRaw(w, method); sent++; }
                catch (Exception e) { failed++; if (failed < 5) Plugin.Log.LogInfo($"[fuzz] send failed: {e.Message}"); }
            }
            byte[] Bytes(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
            DeliveryMethod Way(int n) => n > 1000 ? DeliveryMethod.ReliableOrdered : rnd.Next(2) == 0 ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered;

            // 1. junk, in small batches (about 500 packets a second)
            var packets = new List<Action>();
            foreach (Msg m in Enum.GetValues(typeof(Msg)))
            {
                if (fuzzSkip.Contains(m)) continue;
                byte t = (byte)m;
                var rule = NetGuard.Rules[t];
                if (rule.MaxBytes <= 64 * 1024) packets.Add(() => Raw(t, Bytes(rule.MaxBytes + 100), DeliveryMethod.ReliableOrdered));   // oversized
                packets.Add(() => Raw(t, null, DeliveryMethod.ReliableOrdered, runt: true));                                              // no header
                packets.Add(() => Raw(t, new byte[0], DeliveryMethod.ReliableOrdered));                                                   // empty
                if (!rule.HostSends && !fuzzAnyPayload.Contains(m))   // (host-only types are dropped unread: anything goes)
                {
                    int maxShort = fuzzShortOnly.TryGetValue(m, out int ms) ? ms : 1;
                    for (int i = 0; i < 6; i++) packets.Add(() => Raw(t, Bytes(rnd.Next(maxShort + 1)), DeliveryMethod.ReliableOrdered));
                    continue;
                }
                for (int i = 0; i < 3; i++) packets.Add(() => Raw(t, Bytes(1 + rnd.Next(6)), Way(8)));                     // cut short
                for (int i = 0; i < 6; i++) { int n = 16 + rnd.Next(600); packets.Add(() => Raw(t, Bytes(n), Way(n))); }     // random
                packets.Add(() => Raw(t, Enumerable.Repeat((byte)0xFF, 64).ToArray(), DeliveryMethod.ReliableOrdered));    // -1 / 65535 everywhere
                packets.Add(() => Raw(t, Enumerable.Range(0, 64).Select(i => i % 4 == 3 ? (byte)0x7F : (byte)0xFF).ToArray(), DeliveryMethod.ReliableOrdered)); // int.MaxValue counts
            }
            foreach (int unknown in Enumerable.Range(0, 256).Where(b => !NetGuard.Known((Msg)b)).Where((b, i) => i % 9 == 0))
                for (int i = 0; i < 3; i++) { byte t = (byte)unknown; packets.Add(() => Raw(t, Bytes(rnd.Next(40)), Way(40))); }
            // a second hello, as if we weren't welcomed yet (and claiming the host's id)
            packets.Add(() =>
            {
                var w = new NetDataWriter();
                w.Put(Protocol.Version); w.Put("Intruder"); w.Put(Plugin.Version); w.Put(Protocol.HostId);
                Raw((byte)Msg.Hello, w.CopyData(), DeliveryMethod.ReliableOrdered);
            });
            // well-formed states with bad values, and old ones (out of order)
            var floats = new List<int>();
            void BadState(Action<PlayerState> spoil)
            {
                var st = PlayerState.Capture(me, floats);
                spoil(st);
                var w = new NetDataWriter();
                st.Write(w);
                Raw((byte)Msg.State, w.CopyData(), DeliveryMethod.Unreliable);
            }
            packets.Add(() => BadState(st => st.Time = float.NaN));
            packets.Add(() => BadState(st => st.Time = 1e30f));
            packets.Add(() => BadState(st => st.Time = -5f));
            packets.Add(() => BadState(st => st.Position = new Vector2(float.NaN, 0f)));
            packets.Add(() => BadState(st => st.Position = new Vector2(1e9f, -1e9f)));
            packets.Add(() => BadState(st => { st.Floats = new[] { float.PositiveInfinity }; }));
            for (int i = 1; i <= 10; i++) { float back = i; packets.Add(() => BadState(st => st.Time -= back)); }
            // shuffle: out of order by type too
            packets = packets.OrderBy(_ => rnd.Next()).ToList();
            yield return new WaitForSecondsRealtime(3.5f);   // the host measures its quiet frames first
            for (int i = 0; i < packets.Count; i++)
            {
                packets[i]();
                if (i % 25 == 24) yield return new WaitForSecondsRealtime(0.05f);
            }
            Plugin.Log.LogInfo($"[fuzz] sent {sent} junk packets ({failed} refused by the transport)");
            yield return new WaitForSecondsRealtime(2f);   // let the reliable ones land before the signal
            Chat.Send("#junkdone=" + sent);
            yield return Barrier("junk");
            Check("guest still connected after its junk", Session.Current == s && s.Ready);
            yield return new WaitForSecondsRealtime(2f);

            // 2. flood: real states, ~3000 a second for 3 s
            int floodSent = 0;
            float t0 = Time.realtimeSinceStartup;
            float due = 0f;
            while (Time.realtimeSinceStartup - t0 < 3f)
            {
                due += Time.unscaledDeltaTime * 3000f;
                for (; due >= 1f; due -= 1f)
                {
                    var st = PlayerState.Capture(me, floats);
                    var w = new NetDataWriter();
                    w.Put((byte)Msg.State); w.Put(s.LocalId);
                    st.Write(w);
                    s.SendRaw(w, DeliveryMethod.Unreliable);
                    floodSent++;
                }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(1.5f);
            Chat.Send("#flooddone=" + floodSent);
            yield return Barrier("flood");
            Check("guest still connected after its flood", Session.Current == s && s.Ready);

            // 3. walk: the host must follow
            Vector2 start = me.rb.position;
            yield return MoveTo(me, start + Vector2.left * 2f, 1.5f);
            yield return new WaitForSecondsRealtime(0.7f);
            var pos = me.transform.position;
            Chat.Send("#fuzzfinal=" + pos.x.ToString("F3", CultureInfo.InvariantCulture) + ":" + pos.y.ToString("F3", CultureInfo.InvariantCulture));
            var host = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault();
            Check("guest still sees the host", host != null && host.Shown && host.SecondsSinceState < 1.5f);
            yield return Barrier("synced");

            // 4. junk without end, until the host has had enough
            float a0 = Time.realtimeSinceStartup;
            int abuse = 0;
            while (Session.Current == s && !s.Status.StartsWith("Refused") && Time.realtimeSinceStartup - a0 < 25f)
            {
                for (int i = 0; i < 100; i++) { Raw(200, Bytes(1), DeliveryMethod.Unreliable); abuse++; }
                yield return null;
            }
            yield return WaitFor(() => s.Status.StartsWith("Refused") || Session.Current != s, 5f);
            Plugin.Log.LogInfo($"[fuzz] sent {abuse} junk packets until: {s.Status}");
            Check("guest was kicked for its junk, with the reason", s.Status.StartsWith("Refused") && s.Status.Contains("invalid network traffic"), s.Status);
            yield return Finish("guest fuzzed the host");
        }
    }
}
