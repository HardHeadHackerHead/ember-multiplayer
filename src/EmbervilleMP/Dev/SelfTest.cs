using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    // Self-test scenarios (--mp-test=name). Each ends with one "[MP] RESULT PASS|FAIL <why>" line and quits.
    // Two-instance scenarios run the same scenario on host and guest; the role comes from the session.
    // Marks a self-test scenario: [Scenario("name", Timeout = 200)] private static IEnumerator MyTest() in any
    // SelfTest.*.cs file. What it needs is said here; scripts/test-all.ps1 reads these attributes from the source
    // (the one list of scenarios: keep each attribute on one line, values as plain literals).
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    internal class ScenarioAttribute : Attribute
    {
        public readonly string[] Names;
        public ScenarioAttribute(params string[] names) { Names = names; }
        // Seconds it may take (the first of a run also boots the game); 0: not in test-all (surveys, tools, shots).
        public int Timeout;
        // Copies of the game: the host and Players - 1 guests.
        public int Players = 2;
        // How it starts: "" (a fresh world, everyone in the session), "boot" (right after the game started: first of
        // its run), "own-world" (the guests play their own world and join during the scenario), "menu" (the guests
        // join from the main menu during it), "menus" (every copy on the main menu: a run of its own).
        public string Start = "";
        // The scene it starts in ("": TutorialArea).
        public string Scene = "";
        // Its own --mp-* switches while it runs, space separated ("loot=shared estate"): applied at runtime (the host's
        // rules reach the guests before it starts), so they don't need a boot of their own.
        public string Switches = "";
        // Part of test-all -Quick (the everyday check).
        public bool Quick;
        // false: it goes on in the last scenario's world (no reload) when that one is in its scene: for scenarios that
        // only need the players, not a world nobody has touched (DevHarness.CarryOn).
        public bool Fresh = true;
        // What it tests beyond what its file names (test-all -Changed): paths under srcmbervilleMP, wildcards, commas
        // ("Patches/EnemyPatches.cs,Sync/Enemy*").
        public string Covers = "";
        // A run of its own (scenarios with the same name share it): for a scenario whose start inside a chain isn't
        // proven, or that leaves the session in no state for the next one (fuzz).
        public string Isolate = "";
    }

    internal static partial class SelfTest
    {
        private static bool reported;
        private static int failedChecks;

        // --mp-test=a,b,c runs the scenarios one after another in the same copies of the game (no restart): before
        // each one after the first, every copy switches to a brand-new world at the same time (DevHarness.FreshWorld).
        // Control lines ("#signal", "#key=value") are tagged with the scenario's index, so one scenario's signals
        // never satisfy the next one's barriers, even when a copy runs ahead.
        private static string current = "";
        public static string Current => current;   // the scenario running now (chains: changes per scenario)
        private static int scenarioIndex;
        private static bool chained;

        private class ScenarioBucket
        {
            public readonly Dictionary<string, HashSet<byte>> signals = new Dictionary<string, HashSet<byte>>();
            public readonly Dictionary<string, string> data = new Dictionary<string, string>();
        }
        private static readonly Dictionary<int, ScenarioBucket> buckets = new Dictionary<int, ScenarioBucket>();

        private static ScenarioBucket Bucket(int index)
        {
            if (!buckets.TryGetValue(index, out var b)) buckets[index] = b = new ScenarioBucket();
            return b;
        }

        // "#3|name" or "#3|key=value" from another copy.
        private static void OnControl(byte from, string text)
        {
            if (!text.StartsWith("#")) return;
            int bar = text.IndexOf('|');
            if (bar < 2 || !int.TryParse(text.Substring(1, bar - 1), out int index)) return;
            string body = text.Substring(bar + 1);
            var b = Bucket(index);
            string key = "#" + body;
            if (!b.signals.TryGetValue(key, out var set)) b.signals[key] = set = new HashSet<byte>();
            set.Add(from);
            int eq = body.IndexOf('=');
            if (eq > 0) b.data[body.Substring(0, eq)] = body.Substring(eq + 1);
            if (!peerAt.TryGetValue(from, out int at) || index > at) peerAt[from] = index;
            // A peer gave up on this scenario: so do we (the chain stays in step, nobody waits out the rest).
            if (index == scenarioIndex && body.StartsWith("abort=") && !aborted)
            {
                aborted = true;
                abortWhy = $"#{from} gave up: {body.Substring(6)}";
                Plugin.Log.LogMessage($"[MP] ABORT {current}: {abortWhy}");
            }
        }

        // The scenario each other copy last signalled from (out of step: it shows at once).
        private static readonly Dictionary<byte, int> peerAt = new Dictionary<byte, int>();

        // Fail fast: once a scenario can't go on (a peer never reached a barrier, a copy is out of step), it stops on
        // every copy and the chain moves on (Drive checks this every frame).
        private static bool aborted;
        private static string abortWhy = "";

        private static void Abort(string why)
        {
            if (aborted) return;
            aborted = true;
            abortWhy = why;
            Plugin.Log.LogMessage($"[MP] ABORT {current}: {why}");
            if (Session.Current != null && Session.Current.Ready) Chat.Send("#abort=" + why);
        }

        public static void Report(bool pass, string why)
        {
            if (reported || Args.Test == null) return;
            reported = true;
            Plugin.Log.LogMessage($"[MP] RESULT {(pass ? "PASS" : "FAIL")} {current}: {why}");
        }

        private static string Secs(float s) => s.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        public static bool Check(string what, bool ok, string detail = "")
        {
            if (!ok) failedChecks++;
            string tag = chained ? $"[{current}] " : "";
            // (@: seconds since the game started, to see where a scenario spends its time)
            Plugin.Log.LogMessage($"[MP] CHECK {(ok ? "ok  " : "FAIL")} {tag}{what}{(detail.Length > 0 ? " (" + detail + ")" : "")} @" + Secs(Time.realtimeSinceStartup));
            return ok;
        }

        private static IEnumerator Finish(string summary)
        {
            Report(failedChecks == 0, failedChecks == 0 ? summary : $"{failedChecks} check(s) failed");
            if (chained) yield break;   // the chain moves on to the next scenario
            yield return new WaitForSecondsRealtime(0.5f);
            DevHarness.Quit("test finished");
        }

        public static IEnumerator Run(string list)
        {
            Chat.Received += OnControl;
            Chat.ControlFilter = text => "#" + scenarioIndex + "|" + text.Substring(1);
            var names = list.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();
            chained = names.Length > 1;
            current = names.Length > 0 ? names[0] : list;
            Check($"every game hook in place ({Plugin.PatchClasses} patch classes)", Plugin.FailedPatches.Count == 0, string.Join(", ", Plugin.FailedPatches));
            for (int i = 0; i < names.Length; i++)
            {
                scenarioIndex = i;
                current = names[i];
                Args.Scenario = current;   // this scenario's own switches (--mp-scenario-args) from now on
                reported = false;
                aborted = false;
                abortWhy = "";
                // (a faster peer may have given up on it before we got here)
                var early = Bucket(i).signals.Keys.FirstOrDefault(k => k.StartsWith("#abort="));
                if (early != null) { aborted = true; abortWhy = "a peer gave up: " + early.Substring(7); }
                if (i > 0) failedChecks = 0;
                DevHarness.ResetForScenario();
                InjectQuestRewards = false;   // (questrewards turns it off at its end, unless it timed out)
                var info = Info(current);
                float t0 = Time.realtimeSinceStartup;
                if (chained)
                {
                    string own = Args.DescribeScenario();
                    Plugin.Log.LogMessage($"[MP] SCENARIO {i + 1}/{names.Length} {current}{(own.Length > 0 ? " (" + own + ")" : "")}");
                    if (i > 0)
                    {
                        bool carry = false;
                        if (info != null && !info.Fresh) { yield return AgreeToCarryOn(); carry = carryAgreed; }
                        if (carry) yield return DevHarness.CarryOn();
                        else yield return DevHarness.FreshWorld();
                        yield return Barrier("fresh-world", 120f, sync: true);
                        if (!lastBarrierOk) Abort("the fresh world didn't come together");
                        yield return SyncRules();
                        if (!lastBarrierOk) Abort("the rules didn't reach everyone");
                        // (the first scenario of a run gets its start from the boot itself: see test-all.ps1)
                        if (aborted) { }
                        else if (info?.Start == "own-world") yield return GuestsLeave(toMenu: false);
                        else if (info?.Start == "menu") yield return GuestsLeave(toMenu: true);
                    }
                }
                float setup = Time.realtimeSinceStartup - t0;
                var body = Body(current);
                if (body == null)
                {
                    Report(false, "unknown scenario " + current);
                    if (!chained) DevHarness.Quit("bad scenario");
                    continue;
                }
                NoteSession();
                // A scenario that hangs or throws fails on its own; a chain goes on with the next one.
                yield return Drive(body, Args.Float("scenario-timeout", 0f));
                if (!reported) Report(failedChecks == 0, failedChecks == 0 ? "ended" : $"{failedChecks} check(s) failed");
                float took = Time.realtimeSinceStartup - t0 - setup;
                Plugin.Log.LogMessage($"[MP] TIME {current} {Secs(took)} s (fresh world {Secs(setup)} s, at {Secs(Time.realtimeSinceStartup)} s)");
                if (!chained || i == names.Length - 1) continue;
                // Everyone back in the session and at the end of this scenario before anyone starts the next one (InStep).
                yield return InStep();
                if (!inStep)
                {
                    for (int j = i + 1; j < names.Length; j++)
                        Plugin.Log.LogMessage($"[MP] RESULT FAIL {names[j]}: not run, the copies couldn't get back in step (after {current})");
                    break;
                }
            }
            // (the last one may have ended the session: then no barrier, as for a scenario run alone)
            if (chained && EveryoneHere()) yield return Barrier("chain-end", 60f, sync: true);
            yield return new WaitForSecondsRealtime(0.5f);
            DevHarness.Quit(chained ? "test chain finished" : "test finished");
        }

        private static bool EveryoneHere()
        {
            var s = Session.Current;
            return s != null && s.Ready && s.Players.Count >= Args.Int("players", 2);
        }

        // Runs a scenario's coroutine (and the ones it yields, nested) frame by frame as Unity would, so it can be cut
        // off: past `limit` seconds (0: never) or on an exception it fails, and a chain goes on.
        private static IEnumerator Drive(IEnumerator body, float limit)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(body);
            float noSession = 0f;
            float t0 = Time.realtimeSinceStartup;
            while (stack.Count > 0)
            {
                // No session for long (and no reconnect under way): nobody to play this with any more. (Leaving and
                // joining again takes a few seconds; the scenarios that end the session finish soon after.)
                noSession = chained && (Session.Current == null || !Session.Current.Ready) && !MPRunner.Instance.Reconnecting ? noSession + Time.unscaledDeltaTime : 0f;
                if (noSession > 30f) Abort("no session for 30 s");
                if (limit > 0f && Time.realtimeSinceStartup - t0 > limit)
                {
                    Check($"scenario finished within {limit:F0} s", false);
                    Report(false, $"timed out after {limit:F0} s");
                    yield break;
                }
                if (aborted)
                {
                    Report(false, "aborted: " + abortWhy);
                    yield break;
                }
                var top = stack.Peek();
                bool more;
                try { more = top.MoveNext(); }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[MP] {current} threw: {e}");
                    Check("scenario ran without an exception", false, e.GetType().Name + ": " + e.Message);
                    Report(false, "exception " + e.GetType().Name + ": " + e.Message);
                    yield break;
                }
                if (!more) { stack.Pop(); continue; }
                // nested coroutines, and CustomYieldInstructions (WaitForSecondsRealtime, WaitUntil): IEnumerators too
                if (top.Current is IEnumerator nested) { stack.Push(nested); continue; }
                yield return top.Current;
            }
        }

        private static IEnumerable<(System.Reflection.MethodInfo method, ScenarioAttribute info)> Scenarios() =>
            typeof(SelfTest).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                .SelectMany(m => m.GetCustomAttributes(typeof(ScenarioAttribute), false).Cast<ScenarioAttribute>().Select(a => (m, a)))
                .Concat(Api.ApiTesting.ModScenarios());   // other mods' [MpScenario]s (Party Tricks...)

        private static ScenarioAttribute Info(string scenario) => Scenarios().Where(x => x.info.Names.Contains(scenario)).Select(x => x.info).FirstOrDefault();

        private static IEnumerator Body(string scenario)
        {
            var m = Scenarios().Where(x => x.info.Names.Contains(scenario)).Select(x => x.method).FirstOrDefault();
            return m == null ? null : (IEnumerator)m.Invoke(null, null);
        }

        // Waits until the local player is in a playable scene.
        public static IEnumerator WaitInGame(float timeout = 180f)
        {
            float t = Time.realtimeSinceStartup;
            while (!Game.InGame)
            {
                if (Time.realtimeSinceStartup - t > timeout)
                {
                    Report(false, "never got in game");
                    DevHarness.Quit("timeout");
                    yield break;
                }
                yield return null;
            }
            // A guest that booted into its own world (--mp-join-later) is in game before it has joined: "in game" is
            // in the host's world then, unless the scenario starts from the guest's own world.
            if (!Args.Host && Args.Get("join-later") != null && Info(current)?.Start != "own-world")
            {
                t = Time.realtimeSinceStartup;
                while (!(Session.Current != null && Session.Current.Ready && GuestWorld.Active && Game.InGame && !GuestWorld.Switching && !Loading()))
                {
                    if (Time.realtimeSinceStartup - t > timeout) { Check("joined the host's world", false); break; }
                    yield return null;
                }
            }
            DevHarness.NoteFirstSpawn();
            // Test saves carry damage over from earlier runs; start every scenario healthy.
            var me = Game.LocalPlayer;
            if (me != null && !me.IsDead) me.ResetToFullHealth();
        }

        private static IEnumerator WaitFor(Func<bool> cond, float timeout)
        {
            float t = Time.realtimeSinceStartup;
            while (!cond() && Time.realtimeSinceStartup - t < timeout) yield return null;
        }

        // Both sides signal "#name" and wait for the other's, so neither quits while the other still checks.
        // Waits until every other player (--mp-players, default 2 in all) sent the same signal. Peers get there within
        // a second or two: a longer timeout only where a step takes long (scene loads, travel, fights).
        // Then "#name!" says we have everyone's (ConfirmBeforeLeaving waits for it: a message sent just before leaving can be
        // lost with the connection).
        // A peer that never comes (or a copy that is in another scenario) ends the scenario for everyone (Abort).
        private static bool lastBarrierOk;

        private static IEnumerator Barrier(string name, float timeout = 15f, bool sync = false)
        {
            Chat.Send("#" + name);
            int others = Args.Int("players", 2) - 1;
            var bucket = Bucket(scenarioIndex);
            System.Func<string, int> got = key => bucket.signals.TryGetValue(key, out var set) ? set.Count : 0;
            float t0 = Time.realtimeSinceStartup, noSession = 0f;
            while (got("#" + name) < others && (sync || !aborted) && Time.realtimeSinceStartup - t0 < timeout)
            {
                // a copy already in a later scenario, or no session for a while: nobody is coming
                if (!sync && peerAt.Values.Any(i => i > scenarioIndex)) break;
                noSession = Session.Current != null && Session.Current.Ready ? 0f : noSession + Time.unscaledDeltaTime;
                if (noSession > 5f) break;
                yield return null;
            }
            bool ok = got("#" + name) >= others;
            lastBarrierOk = ok;
            string where = $"we are at scenario {scenarioIndex}, peers at {(peerAt.Count > 0 ? string.Join(",", peerAt.Select(kv => $"#{kv.Key}:{kv.Value}")) : "-")}";
            Check($"{(others == 1 ? "peer" : "everyone")} reached '{name}'", ok, $"{got("#" + name)}/{others}; {where}");
            if (!ok && !sync)
            {
                Abort($"'{name}' not reached ({where})");
                yield break;
            }
            Chat.Send("#" + name + "!");
            lastBarrier = name;
        }

        // Before leaving the session right after a barrier: until the others say they have our signal ("#name!"), at
        // most 3 s. (Not part of every barrier: the copy through first would wait longest, and the other one could act
        // before it looks at the state the barrier was for.)
        private static string lastBarrier = "";

        private static IEnumerator ConfirmBeforeLeaving()
        {
            int others = Args.Int("players", 2) - 1;
            var bucket = Bucket(scenarioIndex);
            string key = "#" + lastBarrier + "!";
            float t1 = Time.realtimeSinceStartup;
            while ((bucket.signals.TryGetValue(key, out var set) ? set.Count : 0) < others && Time.realtimeSinceStartup - t1 < 3f) yield return null;
        }

        [Scenario("dump")]
        private static IEnumerator Dump()
        {
            yield return WaitInGame();
            yield return new WaitForSecondsRealtime(2f);
            var player = Game.LocalPlayer;
            DevHarness.DumpHierarchy(player.gameObject, "player-hierarchy.txt");
            var anim = player.animator;
            Plugin.Log.LogInfo($"[dev] animator={anim.name} layers={anim.layerCount} params={string.Join(",", Array.ConvertAll(anim.parameters, p => p.name + ":" + p.type))}");
            Plugin.Log.LogInfo($"[dev] scene={Game.SceneName} area={Game.AreaName} pos={player.transform.position}");
            yield return DevHarness.Screenshot("dump");
            Report(true, "dumped");
            DevHarness.Quit("done");
        }

        // Stage 1: host and guest in Town see each other move and animate, with their own looks.
        [Scenario("presence", Timeout = 120, Quick = true)]
        private static IEnumerator PresenceTest()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            if (!Check("session exists", s != null)) { yield return Finish(""); yield break; }
            var presence = MPRunner.Instance.Presence;

            RemotePlayer other = null;
            yield return WaitFor(() => (other = presence.Remotes.Values.FirstOrDefault(r => r.Shown && r.Look != null)) != null, 60f);
            if (!Check("other player is shown with a look", other != null, $"remotes={presence.Remotes.Count} states={presence.StatesReceived} looks={presence.LooksReceived}"))
            {
                yield return Finish("");
                yield break;
            }
            yield return Barrier("seen");

            // Walk our own character 3 units sideways (host right, guest left) while doing a gather animation.
            var me = Game.LocalPlayer;
            Vector2 start = me.rb.position;
            // host 2 right, guest 1 left: they end up 3 apart, both on screen
            float myMove = s.IsHost ? 2f : 1f, theirMove = s.IsHost ? 1f : 2f;
            Vector2 target = start + (s.IsHost ? Vector2.right : Vector2.left) * myMove;
            Vector2 otherStart = other.Latest.Position;
            var seenHashes = new HashSet<int>();
            int matchedFrames = 0, frames = 0;
            me.animator.SetTrigger("Wood");
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 3f)
            {
                float k = Mathf.Clamp01((Time.realtimeSinceStartup - t0) / 1.5f);
                Vector2 p = Vector2.Lerp(start, target, k);
                me.rb.position = p;
                me.transform.position = p;
                frames++;
                var pa = other.PuppetAnimator;
                // Compare with the state the puppet is showing (it plays ~0.1s behind on purpose, to smooth jitter).
                var shown = other.Applied;
                if (pa != null && shown != null && shown.LayerStates.Length > 0)
                {
                    bool all = true;
                    for (int l = 0; l < shown.LayerStates.Length && l < pa.layerCount; l++)
                    {
                        int remote = shown.LayerStates[l];
                        var info = pa.GetCurrentAnimatorStateInfo(l);
                        if (!(info.fullPathHash == remote || (pa.IsInTransition(l) && pa.GetNextAnimatorStateInfo(l).fullPathHash == remote))) all = false;
                        seenHashes.Add(remote * 31 + l);
                    }
                    if (all) matchedFrames++;
                }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.5f);

            // Physics can stop a character short of the target (walls), so compare with where the other one really is.
            Chat.Send("#final=" + me.transform.position.x.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ":" + me.transform.position.y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            yield return WaitFor(() => peerData.ContainsKey("final"), 10f);
            float moved = Vector2.Distance(other.Latest.Position, otherStart);
            Check($"other player moved (target {theirMove} units)", moved > 0.5f, $"moved {moved:F2}");
            if (peerData.TryGetValue("final", out var fin))
            {
                var parts = fin.Split(':');
                var real = new Vector2(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                float off = Vector2.Distance(real, other.Puppet.transform.position);
                Check("puppet is where the other player really is", off < 0.1f, $"off by {off:F3}");
            }
            float err = Vector2.Distance(other.Puppet.transform.position, other.Latest.Position);
            Check("puppet sits where the other player is", err < 0.05f, $"error {err:F3}");
            Check("puppet animation follows the other player's", frames > 0 && matchedFrames > frames * 0.6f, $"{matchedFrames}/{frames} frames matched, {seenHashes.Count} states seen");
            Check("other player's animation changed (gather)", seenHashes.Count > other.Latest.LayerStates.Length, $"{seenHashes.Count} layer-states over {other.Latest.LayerStates.Length} layers");
            int bad = other.LookMismatches(out int total);
            Check("puppet look matches", bad >= 0 && bad <= total / 20, $"{bad}/{total} mismatched");
            Check("own player unaffected (still the tagged Player)", GameObject.FindGameObjectsWithTag("Player").Length == 1);

            yield return DevHarness.Screenshot(s.IsHost ? "presence-host" : "presence-guest");
            Plugin.Log.LogInfo($"[sync] sent states={presence.StatesSent} looks={presence.LooksSent}; got states={presence.StatesReceived} looks={presence.LooksReceived}");
            yield return Barrier("done");
            yield return Finish("host and guest see each other");
        }
    }
}
