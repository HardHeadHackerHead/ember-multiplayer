using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbervilleMP.Api;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using EmbervilleMP.Sync.Objects;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP
{
    // The public API for other mods (Api/), through a test-only mod inside our own DLL (Dev/ApiTestMod.cs: channels,
    // stores, object kinds, spawnables, fake declared mods), so these don't need Party Tricks:
    //   api-session   : (guest joins during it) the session's facts, players, who decides, the events: joined,
    //                   started, world switching, changed scene, died / respawned, left, ended
    //   api-channels  : (3 players) to all / one / the host / the scene; the sender can't be spoofed (host-only
    //                   channel, a forged header); a channel's rate and size limits; an unknown channel
    //   api-store     : (guest joins during it) values set before the join reach the late joiner; a guest's write
    //                   goes through the host; the host's file keeps the world store (not the session one), another
    //                   world's file is ignored; leaving forgets both
    //   api-objects   : a mod's object kind: changes, intents to the runner, the runner handoff (the host leaves the
    //                   scene: the guest runs it, its change waits for the host's return); api-objects-late: a late joiner
    //                   gets the states
    //   api-spawn     : (guest joins during it) spawned objects: the late joiner gets them with the same ids and state,
    //                   a guest's spawn and despawn go through the runner; the saved one is in the host's file and back
    //                   after a reload
    //   api-modcheck  : (a run of its own) the guest leaves and joins again hiding an Everyone mod (refused, with the
    //                   message), with an Everyone mod the host lacks, with another version (refused), then hiding a
    //                   ClientOnly and a HostOnly mod (joins)
    //   api-isolation : handlers that throw (a channel's, a store's Changed, an object kind's Apply, a spawn factory)
    //                   are logged under their mod and the session goes on
    internal static partial class SelfTest
    {
        // ---- the harness for other mods' scenarios (Multiplayer.Testing) ----

        internal static IEnumerator ApiBarrier(string name, float timeout) => Barrier(name, timeout);
        internal static IEnumerator ApiWaitFor(Func<bool> condition, float timeout) => WaitFor(condition, timeout);
        // (also for a guest that joins during the scenario: it waits for the session, not only the one it had at the start)
        internal static IEnumerator ApiReady()
        {
            yield return WaitInGame();
            yield return WaitFor(() => Session.Current != null && Session.Current.Ready && Session.Current.Players.Count >= Args.Int("players", 2) && Game.InGame
                && (Session.Current.IsHost || GuestWorld.Active) && !GuestWorld.Switching && !Loading(), 180f);
            yield return Barrier("ready", 120f);
        }
        internal static string ApiHeard(string key) => peerData.TryGetValue(key, out var v) ? v : null;
        internal static IEnumerator ApiFinish(string summary) => Finish(summary);

        // A guest that joined during the scenario (Start = "own-world"): in the session, in the host's world, settled.
        private static IEnumerator InHostWorld(float timeout = 150f)
        {
            yield return WaitFor(() => Session.Current != null && Session.Current.Ready && GuestWorld.Active && Game.InGame && !GuestWorld.Switching && !Loading(), timeout);
        }

        // A guest leaves (back to its own world), as a player would; the chain brings it back (EnsureSession).
        private static IEnumerator LeaveToOwnWorld()
        {
            int returns = GuestWorld.ReturnsHome;
            yield return ConfirmBeforeLeaving();
            Session.Current?.Stop();
            yield return WaitFor(() => GuestWorld.ReturnsHome > returns && !GuestWorld.Switching && !Loading() && Game.InGame, 60f);
        }

        private static string Names(IEnumerable<MpPlayer> ps) => string.Join(",", ps.Select(p => p.ToString()));

        // ---------------- api-session ----------------

        [Scenario("api-session", Timeout = 240, Start = "own-world", Covers = "Api/*")]
        private static IEnumerator ApiSessionTest()
        {
            DevHarness.FastDeath = true;   // (the deaths' events are checked, not their animation)
            var joined = new List<MpPlayer>(); var left = new List<MpPlayer>(); var arrived = new List<MpPlayer>();
            var died = new List<MpPlayer>(); var up = new List<MpPlayer>();
            int started = 0, ended = 0, switching = 0;
            Action<MpPlayer> onJoined = p => joined.Add(p), onLeft = p => left.Add(p), onArrived = p => arrived.Add(p), onDied = p => died.Add(p), onUp = p => up.Add(p);
            Action onStarted = () => started++, onEnded = () => ended++, onSwitching = () => switching++;
            Multiplayer.PlayerJoined += onJoined; Multiplayer.PlayerLeft += onLeft; Multiplayer.PlayerChangedScene += onArrived;
            Multiplayer.PlayerDied += onDied; Multiplayer.PlayerRespawned += onUp;
            Multiplayer.SessionStarted += onStarted; Multiplayer.SessionEnded += onEnded; Multiplayer.WorldSwitching += onSwitching;
            try
            {
                yield return WaitInGame();
                Check("the API is there, version 1", Multiplayer.Available && Multiplayer.ApiVersion == 1);
                if (Args.Host)
                {
                    Check("host: in a session, as the host", Multiplayer.InSession && Multiplayer.IsHost && !Multiplayer.IsGuest);
                    yield return WaitFor(() => joined.Count > 0, 150f);
                    var guest = joined.FirstOrDefault();
                    if (!Check("PlayerJoined: the guest", guest != null && !guest.IsLocal && !guest.IsHost, Names(joined))) { yield return Finish(""); yield break; }
                    yield return WaitFor(() => arrived.Contains(guest) && guest.Scene == Game.SceneName, 120f);
                    Check("PlayerChangedScene: the guest arrived in our scene", arrived.Contains(guest), $"{Names(arrived)}; guest in '{guest.Scene}'");
                    yield return Barrier("in", 120f);
                    yield return WaitFor(() => guest.Character != null, 20f);
                    var me = Multiplayer.LocalPlayer;
                    Check("Players: both of us, by id", Multiplayer.Players.Count == 2 && Multiplayer.Players[0] == me && Multiplayer.Players[1] == guest, Names(Multiplayer.Players));
                    Check("LocalPlayer is us, the host", me != null && me.IsLocal && me.IsHost && me.Id == Protocol.HostId);
                    Check("GetPlayer gives the same object every time", Multiplayer.GetPlayer(guest.Id) == guest);
                    Check("the guest's Character is its puppet", guest.Character != null && Multiplayer.IsPuppet(guest.Character) && Multiplayer.PlayerOf(guest.Character) == guest);
                    Check("our Character is ours, not a puppet", me.Character == Game.LocalPlayer.gameObject && !Multiplayer.IsPuppet(me.Character) && Multiplayer.PlayerOf(me.Character) == me);
                    Check("PlayerOf a part of the puppet", Multiplayer.PlayerOf(guest.Character.transform.GetChild(0).gameObject) == guest);
                    Check("we run the scene", Multiplayer.RunsThisScene && Multiplayer.SceneRunner == me);
                    Check("PlayersInMyScene: both", Multiplayer.PlayersInMyScene().Count == 2);
                    Check("PlayersNear the guest: it's there", Multiplayer.PlayersNear(guest.Position, 0.5f).Contains(guest) && !Multiplayer.PlayersNear(guest.Position + Vector2.right * 50f, 1f).Contains(guest));
                    Check("the guest isn't in combat", !Multiplayer.InCombat(guest) && !Multiplayer.InCombat(me));
                    Check("ModsOf(guest): its declared mods", Multiplayer.ModsOf(guest).Any(m => m.Guid == "mptest.everyone"), string.Join(",", Multiplayer.ModsOf(guest)));
                    Check("WorldName is our world's", Multiplayer.WorldName.Length > 0 && Multiplayer.WorldName == GameData.worldData.Name, Multiplayer.WorldName);
                    Chat.Send("#world=" + Multiplayer.WorldName);
                    yield return WaitFor(() => peerData.ContainsKey("pos"), 10f);
                    var pos = peerData.TryGetValue("pos", out var ps) ? P(ps) : Vector2.zero;
                    Check("the guest's Position is where it is", (guest.Position - pos).magnitude < 0.5f, $"{guest.Position} vs {pos}");
                    yield return Barrier("checked");
                    yield return WaitFor(() => died.Contains(guest), 30f);
                    Check("PlayerDied: the guest", died.Contains(guest), Names(died));
                    yield return WaitFor(() => up.Contains(guest), 60f);
                    Check("PlayerRespawned: the guest", up.Contains(guest), Names(up));
                    yield return Barrier("deaths", 90f);
                    yield return WaitFor(() => left.Contains(guest), 40f);
                    Check("PlayerLeft: the guest (the same object)", left.Contains(guest), Names(left));
                    Check("... and it's gone from Players", Multiplayer.GetPlayer(guest.Id) == null && Multiplayer.Players.Count == 1);
                    Check("no SessionEnded on the host", ended == 0 && Multiplayer.InSession);
                }
                else
                {
                    yield return InHostWorld();
                    var me = Multiplayer.LocalPlayer;
                    Check("SessionStarted once", started == 1, $"{started}");
                    Check("WorldSwitching: into the host's world", switching >= 1, $"{switching}");
                    Check("guest: in a session, as a guest", Multiplayer.InSession && Multiplayer.IsGuest && !Multiplayer.IsHost);
                    Check("PlayerJoined isn't raised for the players already there", joined.Count == 0, Names(joined));
                    var host = Multiplayer.GetPlayer(Protocol.HostId);
                    Check("the host is a player", host != null && host.IsHost && !host.IsLocal && me != null && me.IsLocal && !me.IsHost);
                    yield return WaitFor(() => arrived.Contains(me), 20f);
                    Check("PlayerChangedScene: us, in the host's scene", arrived.Contains(me) && me.Scene == Game.SceneName, Names(arrived));
                    yield return Barrier("in", 120f);
                    yield return WaitFor(() => host.Character != null && peerData.ContainsKey("world"), 20f);
                    Check("the host's Character is its puppet", host.Character != null && Multiplayer.IsPuppet(host.Character) && Multiplayer.PlayerOf(host.Character) == host);
                    Check("the host runs the scene, not us", !Multiplayer.RunsThisScene && Multiplayer.SceneRunner == host);
                    Check("PlayersInMyScene: both", Multiplayer.PlayersInMyScene().Count == 2);
                    Check("ModsOf(host): its declared mods", Multiplayer.ModsOf(host).Any(m => m.Guid == "mptest.everyone"), string.Join(",", Multiplayer.ModsOf(host)));
                    Check("WorldName is the host's", peerData.TryGetValue("world", out var w) && w == Multiplayer.WorldName, Multiplayer.WorldName);
                    Chat.Send("#pos=" + V(Game.LocalPlayer.transform.position));
                    yield return Barrier("checked");
                    var pm = Game.LocalPlayer;
                    pm.ApplyDamage(Hit(null, pm, 99999));
                    yield return WaitFor(() => died.Contains(me), 10f);
                    Check("PlayerDied: us", died.Contains(me), Names(died));
                    yield return WaitFor(() => up.Contains(me), 60f);
                    Check("PlayerRespawned: us", up.Contains(me), Names(up));
                    yield return Barrier("deaths", 90f);
                    yield return LeaveToOwnWorld();
                    Check("SessionEnded once", ended == 1, $"{ended}");
                    Check("out of the session: nothing", !Multiplayer.InSession && Multiplayer.Players.Count == 0 && Multiplayer.LocalPlayer == null
                        && Multiplayer.RunsThisScene && Multiplayer.SceneRunner == null && Multiplayer.WorldName == "");
                    Check("out of the session: calls are harmless", Multiplayer.Spawn("mptest.orb", Vector2.zero) == null && !ApiTestMod.Ch.SendToAll(x => x.Put("x")));
                }
            }
            finally
            {
                Multiplayer.PlayerJoined -= onJoined; Multiplayer.PlayerLeft -= onLeft; Multiplayer.PlayerChangedScene -= onArrived;
                Multiplayer.PlayerDied -= onDied; Multiplayer.PlayerRespawned -= onUp;
                Multiplayer.SessionStarted -= onStarted; Multiplayer.SessionEnded -= onEnded; Multiplayer.WorldSwitching -= onSwitching;
            }
            yield return Finish("the session through the API");
        }

        // ---------------- api-channels ----------------

        private static bool GotText(string text, byte from) => ApiTestMod.Got.Any(g => g.text == text && g.from == from);
        private static bool GotText(string text) => ApiTestMod.Got.Any(g => g.text == text);

        // A ModMessage put together by hand (what a broken or hostile guest could send).
        private static void RawModMessage(byte claimedSender, byte target, int arg, int key, byte[] payload)
        {
            var w = new NetDataWriter();
            w.Put((byte)Msg.ModMessage);
            w.Put(claimedSender);
            ApiChannels.Frame(w, target, arg, key, payload);
            Session.Current.SendRaw(w, DeliveryMethod.ReliableOrdered);
        }

        private static byte[] Text(string s) { var w = new NetDataWriter(); w.Put(s); return w.CopyData(); }

        [Scenario("api-channels", Timeout = 300, Players = 3, Covers = "Api/*")]
        private static IEnumerator ApiChannelsTest()
        {
            // (all three in the session and the host's world before the first signal: one sent before the second guest
            // is welcomed never reaches it)
            yield return ApiReady();
            var s = Session.Current;
            byte me = s.LocalId;
            const byte A = 2, B = 3;
            var pa = Multiplayer.GetPlayer(A); var pb = Multiplayer.GetPlayer(B);
            if (!Check("three players", pa != null && pb != null && Multiplayer.Players.Count == 3, Names(Multiplayer.Players))) { yield return Finish(""); yield break; }
            var ch = ApiTestMod.Ch;
            ApiTestMod.Got.Clear();
            yield return Barrier("clear");

            // 1. Where messages go.
            if (s.IsHost)
            {
                ch.SendToAll(w => w.Put("all-from-host"));
                ch.SendToHost(w => w.Put("host-to-itself"));
                Check("SendToHost on the host: delivered here at once", GotText("host-to-itself", me));
            }
            else if (me == A)
            {
                ch.SendTo(pb, w => w.Put("a-to-b"));
                ch.SendToHost(w => w.Put("a-to-host"));
                ch.SendTo(Multiplayer.LocalPlayer, w => w.Put("a-to-itself"));
                Check("SendTo ourselves: delivered here at once", GotText("a-to-itself", A));
            }
            else ch.SendToAll(w => w.Put("all-from-b"));
            if (s.IsHost) yield return WaitFor(() => GotText("all-from-b", B) && GotText("a-to-host", A), 10f);
            else if (me == A) yield return WaitFor(() => GotText("all-from-host", Protocol.HostId) && GotText("all-from-b", B), 10f);
            else yield return WaitFor(() => GotText("all-from-host", Protocol.HostId) && GotText("a-to-b", A), 10f);
            yield return new WaitForSecondsRealtime(1f);   // (what must NOT arrive has had the time to)
            string got = string.Join(",", ApiTestMod.Got.Select(g => $"{g.text}<{g.from}"));
            if (s.IsHost)
            {
                Check("host got B's to-all and A's to-host, with their ids", GotText("all-from-b", B) && GotText("a-to-host", A), got);
                Check("host didn't get A's message to B", !GotText("a-to-b"), got);
            }
            else if (me == A)
            {
                Check("A got the host's and B's to-all, with their ids", GotText("all-from-host", Protocol.HostId) && GotText("all-from-b", B), got);
                Check("A got nothing it sent elsewhere", !GotText("a-to-b") && !GotText("a-to-host"), got);
            }
            else
            {
                Check("B got the host's to-all and A's to-B", GotText("all-from-host", Protocol.HostId) && GotText("a-to-b", A), got);
                Check("B didn't get A's to-host nor its own to-all", !GotText("a-to-host") && !GotText("all-from-b"), got);
            }
            // (counted before the barrier: past it, A's host-only message can reach us before we're through it ourselves)
            int senderDrops = ApiChannels.SenderDrops;
            yield return Barrier("routed");

            // 2. Who sent it: the host's word. A guest on a host-only channel, and a header claiming B.
            if (me == A)
            {
                Check("a guest may send on a host-only channel (the host drops it)", ApiTestMod.HostOnly.SendToAll(w => w.Put("spoof")));
                RawModMessage(B, ApiChannels.ToAll, 0, ch.Key, Text("forged"));
            }
            if (s.IsHost) ApiTestMod.HostOnly.SendToAll(w => w.Put("legit"));
            if (me == B) yield return WaitFor(() => GotText("forged") && GotText("hostonly:legit"), 10f);
            else if (s.IsHost) yield return WaitFor(() => GotText("forged"), 10f);
            else yield return WaitFor(() => GotText("hostonly:legit"), 10f);
            yield return new WaitForSecondsRealtime(1f);
            got = string.Join(",", ApiTestMod.Got.Select(g => $"{g.text}<{g.from}"));
            Check("nobody got the guest's host-only message", !GotText("hostonly:spoof"), got);
            if (s.IsHost) Check("the host dropped it as not the guest's to send", ApiChannels.SenderDrops > senderDrops, $"{ApiChannels.SenderDrops - senderDrops}");
            if (me != A) Check("the forged message is A's (the header said B)", GotText("forged", A) && !GotText("forged", B), got);
            if (me != Protocol.HostId) Check("the host's host-only message arrived", GotText("hostonly:legit", Protocol.HostId), got);
            yield return Barrier("spoofed");

            // 3. A channel's limits: 2/s, a burst of 3, 64 bytes.
            ApiTestMod.GotLimited = 0;
            int rateDrops = ApiChannels.RateDrops, sizeDrops = ApiChannels.SizeDrops;
            yield return Barrier("limits");
            if (me == A)
            {
                for (int i = 0; i < 20; i++) ApiTestMod.Limited.SendToAll(w => w.Put(i));
                Check("a message over the channel's MaxSize isn't sent", !ApiTestMod.Limited.SendToAll(w => w.Put(new string('x', 80))));
                RawModMessage(A, ApiChannels.ToAll, 0, ApiTestMod.Limited.Key, new byte[200]);   // (past our own check)
            }
            yield return new WaitForSecondsRealtime(2.5f);   // (everything sent has arrived; refills add ~5)
            if (me != A) Check("only the channel's burst got through (and a refill)", ApiTestMod.GotLimited >= 3 && ApiTestMod.GotLimited <= 6, $"{ApiTestMod.GotLimited} of 20");
            if (s.IsHost)
            {
                Check("the host dropped the rest over the rate", ApiChannels.RateDrops - rateDrops >= 14, $"{ApiChannels.RateDrops - rateDrops}");
                Check("... and the oversized one", ApiChannels.SizeDrops > sizeDrops);
            }
            yield return Barrier("limited");

            // 4. A channel nobody here registered (a ClientOnly mod's): counted and dropped, relayed by the host.
            int unknown = ApiChannels.UnknownDrops;
            yield return Barrier("unknown-start");
            if (me == A) for (int i = 0; i < 2; i++) RawModMessage(A, ApiChannels.ToAll, 0, NetIds.Hash("mptest.nobody"), Text("?"));
            if (me != A) yield return WaitFor(() => ApiChannels.UnknownDrops >= unknown + 2, 10f);
            if (me != A) Check("an unknown channel's messages are counted and dropped", ApiChannels.UnknownDrops >= unknown + 2, $"{ApiChannels.UnknownDrops - unknown}");
            if (me == A) ch.SendToAll(w => w.Put("still-fine"));
            if (me != A) yield return WaitFor(() => GotText("still-fine", A), 10f);
            if (me != A) Check("the channels still work after it", GotText("still-fine", A));
            yield return Barrier("unknown");

            // 5. To the scene: the host goes to the hub; A's goes to B only, the host's to nobody. Unreliable ones too.
            const string hub = "VitromotusCrystal";
            if (s.IsHost)
            {
                GameData.gameBools[BoolID.CutsceneCrystal] = true;
                GuestWorld.Travel(hub, "", Vector3.zero, newWorld: false);
                yield return WaitFor(() => Game.InGame && Game.SceneName == hub, 90f);
                Check("host went to the hub", Game.SceneName == hub);
            }
            else yield return WaitFor(() => Multiplayer.GetPlayer(Protocol.HostId)?.Scene == hub, 90f);
            yield return Barrier("split", 120f);
            ApiTestMod.Got.Clear();
            yield return Barrier("split-clear");
            if (me == A) ch.SendToScene(w => w.Put("scene-a"));
            if (s.IsHost) ch.SendToScene(w => w.Put("scene-host"));
            if (me == B) for (int i = 0; i < 5; i++) ch.SendToAll(w => w.Put("unreliable-b"), reliable: false);
            yield return new WaitForSecondsRealtime(2f);
            got = string.Join(",", ApiTestMod.Got.Select(g => $"{g.text}<{g.from}"));
            if (me == B) Check("B (A's scene) got A's to-scene", GotText("scene-a", A), got);
            if (s.IsHost) Check("the host (in the hub) didn't get A's to-scene", !GotText("scene-a"), got);
            if (me != Protocol.HostId) Check("nobody got the host's to-scene (alone in the hub)", !GotText("scene-host"), got);
            if (me != B) Check("unreliable messages arrive", GotText("unreliable-b", B), got);
            yield return Barrier("scened");
            if (s.IsHost)
            {
                GuestWorld.Travel("TutorialArea", CombatArea, new Vector3(52.5f, 76.6f, 0f), newWorld: false);
                yield return WaitFor(() => Game.InGame && Game.SceneName == "TutorialArea", 90f);
            }
            Plugin.Log.LogInfo($"[api] channels: delivered {ApiChannels.Delivered}, relayed {ApiChannels.Relayed}, dropped rate {ApiChannels.RateDrops} size {ApiChannels.SizeDrops} sender {ApiChannels.SenderDrops} unknown {ApiChannels.UnknownDrops}");
            yield return Barrier("done", 120f);
            yield return Finish("mods' channels");
        }

        // ---------------- api-store ----------------

        [Scenario("api-store", Timeout = 240, Start = "own-world", Covers = "Api/*")]
        private static IEnumerator ApiStoreTest()
        {
            var ws = Multiplayer.WorldStore("mptest");
            var ss = Multiplayer.SessionStore("mptest");
            var changes = new List<string>();
            Action<string> onWorld = k => changes.Add("w:" + k), onSession = k => changes.Add("s:" + k);
            ws.Changed += onWorld;
            ss.Changed += onSession;
            try
            {
                yield return WaitInGame();
                if (Args.Host)
                {
                    ws.Set("w.text", "before"); ws.Set("w.n", 5); ws.Set("w.f", 1.5f); ws.Set("w.b", true);
                    ss.Set("s.flag", true);
                    Check("the host's own values apply at once", ws.Get("w.text", "") == "before" && ws.Get("w.n", 0) == 5 && ws.Get("w.f", 0f) == 1.5f && ws.Get("w.b", false) && ss.Get("s.flag", false));
                    Check("Changed: raised for each", changes.Contains("w:w.text") && changes.Contains("w:w.n") && changes.Contains("s:s.flag"), string.Join(",", changes));
                    Check("Get<T>: another type gives the fallback, an int reads as float and string", ws.Get("w.text", -1) == -1 && ws.Get("w.n", 0f) == 5f && ws.Get("w.n", "") == "5");
                    Check("Has / Keys", ws.Has("w.text") && !ws.Has("nope") && ws.Keys.Contains("w.b"));
                    yield return WaitFor(() => peerData.ContainsKey("loaded"), 180f);
                    yield return Barrier("joined", 60f);
                    yield return WaitFor(() => ws.Get("w.guest", 0f) == 2.5f, 15f);
                    Check("the guest's value reached the host", ws.Get("w.guest", 0f) == 2.5f && changes.Contains("w:w.guest"));
                    ws.Set("w.n", 6);
                    ss.Set("s.flag", false);
                    ws.Remove("w.f");
                    Check("Remove", !ws.Has("w.f"));
                    yield return Barrier("updated", 30f);

                    // The world store is in the host's file (not the session store), and comes back from it.
                    GameData.SaveData();
                    string file = ApiSideFile.CurrentPath;
                    var lines = File.Exists(file) ? File.ReadAllLines(file) : new string[0];
                    Check("the host saved <world>_mp_mods.txt, naming its world", lines.FirstOrDefault() == SideFiles.WorldHeader, file);
                    string B64(string x) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(x));
                    Check("... with the world store's values", lines.Any(l => l.StartsWith("store\t" + B64("mptest") + "\t" + B64("w.guest") + "\t")));
                    Check("... and not the session store's", !lines.Any(l => l.Contains("\t" + B64("s.flag") + "\t")));
                    ApiStores.WorldKey = null;
                    ApiSideFile.Load();
                    Check("loaded again: the same values", ws.Get("w.text", "") == "before" && ws.Get("w.n", 0) == 6 && ws.Get("w.guest", 0f) == 2.5f && ws.Get("w.b", false) && !ws.Has("w.f"),
                        string.Join(",", ws.Keys.Select(k => k + "=" + ws.Get(k, ""))));
                    File.WriteAllLines(file, new[] { "world\tAnother world|1" }.Concat(lines.Skip(1)));
                    int ignored = SideFiles.Ignored;
                    ApiStores.WorldKey = null;
                    ApiSideFile.Load();
                    Check("another world's file is ignored", SideFiles.Ignored == ignored + 1 && !ws.Has("w.text"));
                    File.WriteAllLines(file, lines);
                    ApiStores.WorldKey = null;
                    ApiSideFile.Load();
                    Check("our own file read again", ws.Get("w.text", "") == "before");
                    yield return Barrier("persisted", 30f);
                    yield return WaitFor(() => Session.Current.Players.Count == 1, 40f);
                }
                else
                {
                    yield return InHostWorld();
                    yield return WaitFor(() => ws.Has("w.text"), 20f);
                    Check("the late joiner has the world store", ws.Get("w.text", "") == "before" && ws.Get("w.n", 0) == 5 && ws.Get("w.f", 0f) == 1.5f && ws.Get("w.b", false));
                    Check("... and the session store", ss.Get("s.flag", false));
                    Check("... with Changed for what arrived", changes.Contains("w:w.text") && changes.Contains("s:s.flag"), string.Join(",", changes));
                    Chat.Send("#loaded=1");
                    yield return Barrier("joined", 60f);
                    ws.Set("w.guest", 2.5f);
                    Check("a guest's Set goes through the host (not applied here yet)", !ws.Has("w.guest"));
                    yield return WaitFor(() => ws.Get("w.guest", 0f) == 2.5f, 15f);
                    Check("... and comes back from it", ws.Get("w.guest", 0f) == 2.5f && changes.Contains("w:w.guest"));
                    yield return WaitFor(() => ws.Get("w.n", 0) == 6 && !ss.Get("s.flag", true) && !ws.Has("w.f"), 15f);
                    Check("the host's changes and removal arrive", ws.Get("w.n", 0) == 6 && !ss.Get("s.flag", true) && !ws.Has("w.f"));
                    yield return Barrier("updated", 30f);
                    yield return Barrier("persisted", 60f);
                    changes.Clear();
                    yield return LeaveToOwnWorld();
                    Check("out of the session: the host's values are gone", !ws.Has("w.text") && !ss.Has("s.flag") && ws.Get("w.n", -1) == -1);
                    Check("... with Changed for each", changes.Contains("w:w.text") && changes.Contains("s:s.flag"), string.Join(",", changes));
                    ws.Set("w.solo", 1);
                    Check("Set outside a session does nothing", !ws.Has("w.solo"));
                }
            }
            finally
            {
                ws.Changed -= onWorld;
                ss.Changed -= onSession;
            }
            yield return Finish("mods' shared data");
        }

        // ---------------- api-objects ----------------

        private static void UseTestBoxes(bool on)
        {
            ApiTestMod.BoxesWanted = on;
            ApiTestMod.Boxes.Values.Clear();
            ObjectSync.Instance?.Rescan();
        }

        [Scenario("api-objects", Timeout = 300, Covers = "Api/*,Sync/Objects/ObjectSync.cs")]
        [Scenario("api-objects-late", Timeout = 220, Start = "own-world", Covers = "Api/*,Sync/Objects/ObjectSync.cs")]
        private static IEnumerator ApiObjectsTest()
        {
            var boxes = ApiTestMod.Boxes;
            UseTestBoxes(true);
            if (current == "api-objects-late") { yield return ApiObjectsLate(); yield break; }
            int became = 0, stopped = 0;
            Action onBecame = () => became++, onStopped = () => stopped++;
            Multiplayer.BecameSceneRunner += onBecame;
            Multiplayer.StoppedRunningScene += onStopped;
            try
            {
                yield return CoopReady();
                var s = Session.Current;
                yield return WaitFor(() => boxes.Box("Box1") != null && ObjectSync.Instance.Find(boxes.Kind, ObjectSync.Instance.IdOf(boxes.Box("Box1").transform)) != null, 10f);
                Check("the test boxes are in the scene, known to the framework", boxes.Box("Box1") != null);
                yield return Barrier("boxes");

                // A change by the runner; a guest's own change (only with byLocalPlayer: the runner's are the ones that count).
                if (s.IsHost)
                {
                    boxes.Values["Box1"] = 7;
                    Multiplayer.ObjectChanged(boxes, boxes.Box("Box1"));
                    yield return new WaitForSecondsRealtime(2f);   // (the guest's change without byLocalPlayer must not arrive)
                    Check("a guest's change it didn't make isn't sent", boxes.Get("Box2") == 0, $"{boxes.Get("Box2")}");
                    yield return WaitFor(() => boxes.Get("Box2") == 4, 10f);
                    Check("a guest's own change (byLocalPlayer) reached the host", boxes.Get("Box2") == 4);
                }
                else
                {
                    yield return WaitFor(() => boxes.Get("Box1") == 7, 10f);
                    Check("the runner's change reached the guest", boxes.Get("Box1") == 7);
                    boxes.Values["Box2"] = 99;
                    Multiplayer.ObjectChanged(boxes, boxes.Box("Box2"));
                    yield return new WaitForSecondsRealtime(2.5f);
                    boxes.Values["Box2"] = 4;
                    Multiplayer.ObjectChanged(boxes, boxes.Box("Box2"), byLocalPlayer: true);
                }
                yield return Barrier("changed");

                // Intents: decided on the runner (at once if that's us).
                if (s.IsHost)
                {
                    Multiplayer.SendIntent(boxes, boxes.Box("Box1"), w => w.Put(1));
                    Check("an intent on the runner runs at once", boxes.Get("Box1") == 8 && boxes.LastIntentFrom == Protocol.HostId);
                    yield return WaitFor(() => boxes.Get("Box2") == 7, 10f);
                    Check("the guest's intent ran here, from the guest", boxes.Get("Box2") == 7 && boxes.LastIntentFrom == 2, $"{boxes.Get("Box2")} from #{boxes.LastIntentFrom}");
                }
                else
                {
                    Multiplayer.SendIntent(boxes, boxes.Box("Box2"), w => w.Put(3));
                    yield return WaitFor(() => boxes.Get("Box2") == 7 && boxes.Get("Box1") == 8, 10f);
                    Check("the runner's decisions came back", boxes.Get("Box2") == 7 && boxes.Get("Box1") == 8, $"{boxes.Get("Box1")},{boxes.Get("Box2")}");
                }
                yield return Barrier("intent");

                // The runner leaves: the guest runs the scene and changes Box1; the host gets it when it comes back.
                const string hub = "VitromotusCrystal";
                if (s.IsHost)
                {
                    GameData.gameBools[BoolID.CutsceneCrystal] = true;
                    GuestWorld.Travel(hub, "", Vector3.zero, newWorld: false);
                    yield return WaitFor(() => Game.InGame && Game.SceneName == hub, 90f);
                    yield return Barrier("away", 120f);
                    yield return Barrier("guest-changed", 60f);
                    boxes.Values.Clear();   // (what we knew before going: only what the framework kept must count)
                    GuestWorld.Travel("TutorialArea", CombatArea, new Vector3(52.5f, 76.6f, 0f), newWorld: false);
                    yield return WaitFor(() => Game.InGame && Game.SceneName == "TutorialArea" && boxes.Get("Box1") == 9, 90f);
                    Check("back: the box has the change made while we were away", boxes.Get("Box1") == 9 && boxes.Get("Box2") == 7, $"{boxes.Get("Box1")},{boxes.Get("Box2")}");
                    yield return WaitFor(() => Multiplayer.RunsThisScene && EnemySync.IsAuthority, 20f);
                    Check("we run the scene again", Multiplayer.RunsThisScene);
                }
                else
                {
                    yield return Barrier("away", 120f);
                    yield return WaitFor(() => Multiplayer.RunsThisScene && became > 0, 20f);
                    Check("BecameSceneRunner: the guest runs the scene now", Multiplayer.RunsThisScene && became == 1 && Multiplayer.SceneRunner == Multiplayer.LocalPlayer, $"became {became}");
                    boxes.Values["Box1"] = 9;
                    Multiplayer.ObjectChanged(boxes, boxes.Box("Box1"));
                    yield return Barrier("guest-changed", 60f);
                    yield return WaitFor(() => !Multiplayer.RunsThisScene && stopped > 0, 120f);
                    Check("StoppedRunningScene: the host is back", !Multiplayer.RunsThisScene && stopped == 1 && Multiplayer.SceneRunner?.IsHost == true, $"stopped {stopped}");
                }
                yield return Barrier("back", 120f);
            }
            finally
            {
                Multiplayer.BecameSceneRunner -= onBecame;
                Multiplayer.StoppedRunningScene -= onStopped;
                ApiTestMod.BoxesWanted = false;
            }
            yield return Finish("mods' world objects");
        }

        private static IEnumerator ApiObjectsLate()
        {
            var boxes = ApiTestMod.Boxes;
            yield return WaitInGame();
            if (Args.Host)
            {
                yield return WaitFor(() => boxes.Box("Box1") != null, 10f);
                boxes.Values["Box1"] = 11;
                Multiplayer.ObjectChanged(boxes, boxes.Box("Box1"));   // (alone: we run the scene)
                yield return WaitFor(() => peerData.ContainsKey("late"), 180f);
                Check("the guest has the state set before it came", peerData.TryGetValue("late", out var v) && v == "11", v);
            }
            else
            {
                yield return InHostWorld();
                yield return WaitFor(() => boxes.Get("Box1") == 11, 20f);
                Check("late joiner: the box is in the host's state", boxes.Get("Box1") == 11, $"{boxes.Get("Box1")}");
                Chat.Send("#late=" + boxes.Get("Box1"));
            }
            yield return Barrier("done", 60f);
            ApiTestMod.BoxesWanted = false;
            yield return Finish("a late joiner gets mods' object states");
        }

        // ---------------- api-spawn ----------------

        private static GameObject Orb(string name) => UnityEngine.Object.FindObjectsByType<ApiTestMod.OrbTag>(FindObjectsSortMode.None).Select(t => t.gameObject).FirstOrDefault(g => g.name == "MPTestOrb_" + name);

        [Scenario("api-spawn", Timeout = 260, Start = "own-world", Covers = "Api/*")]
        private static IEnumerator ApiSpawnTest()
        {
            yield return WaitInGame();
            if (Args.Host)
            {
                Vector2 at = Game.LocalPlayer.transform.position;
                var a = Multiplayer.Spawn("mptest.orb", at + Vector2.right, w => w.Put("A"));
                var relic = Multiplayer.Spawn("mptest.relic", at + Vector2.right * 2f, w => w.Put("R"));
                int idA = Multiplayer.NetIdOf(a), idR = Multiplayer.NetIdOf(relic);
                Check("the runner's Spawn makes the object at once, with a net id", a != null && relic != null && idA != 0 && idR != 0 && (idA & 0x80000000) != 0, $"{idA},{idR}");
                Check("... where it was asked", a != null && ((Vector2)a.transform.position - (at + Vector2.right)).magnitude < 0.01f);
                Check("NetIdOf anything else: 0", Multiplayer.NetIdOf(Game.LocalPlayer.gameObject) == 0);
                a.GetComponent<ApiTestMod.OrbTag>().Value = 5;
                Multiplayer.ObjectChanged(ApiTestMod.Orbs, a);
                // (told once the guest is in the session: a control line sent before reaches nobody)
                yield return WaitFor(() => peerData.ContainsKey("arrived"), 180f);
                Chat.Send($"#ids={idA},{idR}");
                yield return WaitFor(() => peerData.ContainsKey("loaded"), 60f);
                yield return Barrier("joined", 60f);
                yield return WaitFor(() => Orb("B") != null, 15f);
                var b = Orb("B");
                Check("the guest's Spawn was made here (we run the scene)", b != null && Multiplayer.NetIdOf(b) != 0 && ((Multiplayer.NetIdOf(b) >> 24) & 0x7F) == Protocol.HostId);
                Chat.Send("#idB=" + Multiplayer.NetIdOf(b));
                yield return Barrier("b", 30f);
                yield return WaitFor(() => a == null, 10f);
                Check("the guest's Despawn removed it here", a == null && ApiSpawns.Find(idA) == null);
                yield return Barrier("despawned", 30f);

                // The saved one is in the host's file, and comes back from it.
                GameData.SaveData();
                var lines = File.Exists(ApiSideFile.CurrentPath) ? File.ReadAllLines(ApiSideFile.CurrentPath) : new string[0];
                Check("the saved spawn is in the host's file, the others aren't", lines.Count(l => l.StartsWith("spawn\t")) == 1 && lines.Any(l => l.StartsWith("spawn\t") && l.Contains("\t" + idR + "\t")),
                    string.Join(" | ", lines.Where(l => l.StartsWith("spawn"))));
                Multiplayer.Testing.SaveAndReloadWorld();
                yield return WaitFor(() => ApiSpawns.Find(idR) != null, 10f);
                Check("after a reload the saved spawn is back, same id", ApiSpawns.Find(idR) != null && ApiSpawns.Find(idR).name == "MPTestOrb_R");
                yield return Barrier("saved", 30f);
            }
            else
            {
                yield return InHostWorld();
                Chat.Send("#arrived=1");
                yield return WaitFor(() => peerData.ContainsKey("ids"), 30f);
                var ids = peerData.TryGetValue("ids", out var v) ? v.Split(',').Select(int.Parse).ToArray() : new[] { 0, 0 };
                yield return WaitFor(() => ApiSpawns.Find(ids[0]) != null && ApiSpawns.Find(ids[1]) != null, 20f);
                var a = ApiSpawns.Find(ids[0]);
                Check("the late joiner has the spawned objects, same ids", a != null && a.name == "MPTestOrb_A" && ApiSpawns.Find(ids[1]) != null);
                yield return WaitFor(() => a != null && a.GetComponent<ApiTestMod.OrbTag>().Value == 5, 10f);
                Check("... with their state", a != null && a.GetComponent<ApiTestMod.OrbTag>().Value == 5);
                Chat.Send("#loaded=1");
                yield return Barrier("joined", 60f);
                var asked = Multiplayer.Spawn("mptest.orb", (Vector2)Game.LocalPlayer.transform.position + Vector2.up, w => w.Put("B"));
                Check("a guest's Spawn asks the runner (nothing here yet)", asked == null);
                yield return WaitFor(() => Orb("B") != null && peerData.ContainsKey("idB"), 15f);
                Check("the runner's copy came here, same id", Orb("B") != null && peerData.TryGetValue("idB", out var idB) && Multiplayer.NetIdOf(Orb("B")).ToString() == idB);
                yield return Barrier("b", 30f);
                Multiplayer.Despawn(a);
                yield return WaitFor(() => a == null, 10f);
                Check("Despawn through the runner: gone here too", a == null && ApiSpawns.Find(ids[0]) == null);
                yield return Barrier("despawned", 30f);
                yield return Barrier("saved", 60f);
            }
            yield return Finish("mods' spawned objects");
        }

        // ---------------- api-modcheck ----------------

        private static IEnumerator JoinExpecting(string ip, int port, bool refused, string message)
        {
            if (!MPRunner.Instance.StartJoin(ip, port, MPWindow.DefaultName(), MPRunner.ArgTransport)) { Check("the join started", false); yield break; }
            var attempt = Session.Current;
            if (lastLocalId > Protocol.HostId) attempt.RejoinAs = lastLocalId;
            yield return WaitFor(() => attempt.Ready || attempt.Status.StartsWith("Refused") || Session.Current != attempt, 30f);
            string status = attempt.Status;
            if (refused)
            {
                Check($"refused: \"{message}\"", status.StartsWith("Refused") && status.Contains(message), status);
                yield return WaitFor(() => Session.Current != attempt, 10f);
                Check("... and not in the session", Session.Current == null || !Session.Current.Ready);
            }
            else Check("joined", attempt.Ready, status);
        }

        [Scenario("api-modcheck", Timeout = 260, Isolate = "api-modcheck", Covers = "Api/*")]
        private static IEnumerator ApiModCheckTest()
        {
            yield return CoopReady();
            var s = Session.Current;
            if (s.IsHost)
            {
                var guest = Multiplayer.Players.FirstOrDefault(p => !p.IsLocal);
                Check("the guest's declared mods, all four", guest != null && new[] { "mptest.everyone", "mptest.hostonly", "mptest.clientonly", "mptest.incompatible" }.All(g => Multiplayer.ModsOf(guest).Any(m => m.Guid == g)),
                    guest == null ? "" : string.Join(",", Multiplayer.ModsOf(guest)));
                Check("an Incompatible mod is told to be off in a session", Multiplayer.ShouldBeOff);
                int refusals = ApiMods.Refusals;
                yield return Barrier("before");
                yield return WaitFor(() => peerData.ContainsKey("rejoined"), 200f);
                Check("three joins refused for their mods", ApiMods.Refusals == refusals + 3, $"{ApiMods.Refusals - refusals}");
                yield return WaitFor(() => Multiplayer.Players.Count == 2, 20f);
                guest = Multiplayer.Players.FirstOrDefault(p => !p.IsLocal);
                var mods = guest != null ? Multiplayer.ModsOf(guest) : new List<MpMod>();
                Check("the guest is back without its ClientOnly and HostOnly mods", guest != null && mods.Any(m => m.Guid == "mptest.everyone") && !mods.Any(m => m.Guid == "mptest.clientonly" || m.Guid == "mptest.hostonly"),
                    string.Join(",", mods));
            }
            else
            {
                yield return Barrier("before");
                if (!Check("the host's address is known", HostTarget(out var ip, out int port))) { yield return Finish(""); yield break; }
                NoteSession();
                yield return LeaveToOwnWorld();
                Check("ShouldBeOff only in a session", !Multiplayer.ShouldBeOff);
                var everyone = ApiMods.TestMods.First(m => m.Guid == "mptest.everyone");
                try
                {
                    ApiMods.TestHidden.Add("mptest.everyone");
                    yield return JoinExpecting(ip, port, true, "The host uses MP Test Everyone 1.0.0: install it to join.");
                    ApiMods.TestHidden.Clear();
                    var extra = new MpMod("mptest.extra", "MP Test Extra", "2.0.0", Requirement.Everyone);
                    ApiMods.TestMods.Add(extra);
                    yield return JoinExpecting(ip, port, true, "You use MP Test Extra 2.0.0 and the host doesn't");
                    ApiMods.TestMods.Remove(extra);
                    ApiMods.TestMods.Remove(everyone);
                    ApiMods.TestMods.Add(new MpMod("mptest.everyone", "MP Test Everyone", "1.0.1", Requirement.Everyone));
                    yield return JoinExpecting(ip, port, true, "The host uses MP Test Everyone 1.0.0, you have 1.0.1: use the same version to join.");
                    ApiMods.TestMods.RemoveAll(m => m.Guid == "mptest.everyone");
                    ApiMods.TestMods.Add(everyone);
                    ApiMods.TestHidden.Add("mptest.clientonly");
                    ApiMods.TestHidden.Add("mptest.hostonly");
                    yield return JoinExpecting(ip, port, false, "");
                }
                finally
                {
                    ApiMods.TestHidden.Clear();
                    ApiMods.TestMods.RemoveAll(m => m.Guid == "mptest.everyone" || m.Guid == "mptest.extra");
                    ApiMods.TestMods.Insert(0, everyone);
                }
                yield return InHostWorld();
                Check("back in the host's world", Multiplayer.IsGuest && GuestWorld.Active);
                Chat.Send("#rejoined=1");
            }
            yield return Barrier("after", 90f);
            yield return Finish("mods are checked at join");
        }

        // ---------------- api-isolation ----------------

        [Scenario("api-isolation", Timeout = 150, Covers = "Api/*")]
        private static IEnumerator ApiIsolationTest()
        {
            yield return CoopReady();
            var s = Session.Current;
            int ours = ApiGuard.ErrorsOf(ApiGuard.OurId), mod = ApiGuard.ErrorsOf(ApiTestMod.Mod);
            ApiTestMod.GotThrowing = 0;
            var ws = Multiplayer.WorldStore("mptest");
            int heard = 0;
            Action<string> bad = k => throw new InvalidOperationException("mptest: a Changed handler that throws");
            Action<string> good = k => { if (k == "iso.k") heard++; };
            ws.Changed += bad;
            ws.Changed += good;
            UseTestBoxes(true);
            try
            {
                yield return Barrier("hooked");
                // A channel handler that throws (the second one still hears every message).
                for (int i = 0; i < 3; i++) { if (s.IsHost) ApiTestMod.Throwing.SendToAll(w => w.Put(i)); else ApiTestMod.Throwing.SendToHost(w => w.Put(i)); }
                yield return WaitFor(() => ApiTestMod.GotThrowing >= 3, 10f);
                Check("the other handler heard all three", ApiTestMod.GotThrowing == 3, $"{ApiTestMod.GotThrowing}");
                Check("each throw was logged under its mod", ApiGuard.ErrorsOf(ApiGuard.OurId) >= ours + 3, ApiGuard.Describe());
                // A store's Changed handler.
                if (s.IsHost) ws.Set("iso.k", 1);
                yield return WaitFor(() => heard > 0, 10f);
                Check("a throwing Changed handler: the next one still runs", heard == 1);
                // An object kind's Apply, a spawn factory.
                if (s.IsHost)
                {
                    yield return WaitFor(() => GameObject.Find("MPTestBad") != null, 5f);
                    Multiplayer.ObjectChanged(ApiTestMod.Bad, GameObject.Find("MPTestBad"));
                    Check("a spawn whose factory throws gives nothing", Multiplayer.Spawn("mptest.badorb", Game.LocalPlayer.transform.position) == null);
                }
                else yield return WaitFor(() => ApiGuard.ErrorsOf(ApiTestMod.Mod) >= mod + 2, 10f);
                Check("the mod's Apply / factory errors are logged under it", ApiGuard.ErrorsOf(ApiTestMod.Mod) >= mod + (s.IsHost ? 1 : 2), ApiGuard.Describe());
                yield return Barrier("thrown");
                // The session goes on.
                ApiTestMod.Got.Clear();
                ApiTestMod.Ch.SendToAll(w => w.Put("after-" + s.LocalId));
                byte other = s.IsHost ? (byte)2 : Protocol.HostId;
                yield return WaitFor(() => GotText("after-" + other, other), 10f);
                Check("the session goes on: messages still arrive", GotText("after-" + other, other) && Multiplayer.InSession);
            }
            finally
            {
                ws.Changed -= bad;
                ws.Changed -= good;
                ApiTestMod.BoxesWanted = false;
            }
            yield return Barrier("done");
            yield return Finish("a mod's errors stay its own");
        }
    }
}
