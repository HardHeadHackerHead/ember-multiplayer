using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using EmbervilleMP.Sync;
using EmbervilleMP.Sync.Objects;

namespace EmbervilleMP.Api
{
    // TEST ONLY. A mod's own self-test scenario, run by the multiplayer mod's harness like its own (scripts/test-all.ps1
    // reads this attribute from the mod's source: keep it on one line, values as plain literals). On a public static
    // method returning IEnumerator in the mod's plugin assembly. The fields are the harness's (docs/DEVELOPMENT.md,
    // Testing): Timeout (seconds; 0: not in test-all), Players, Start ("", "own-world", "menu", "boot"), Scene,
    // Switches, Quick, Fresh, Covers, Isolate.
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class MpScenarioAttribute : Attribute
    {
        public readonly string[] Names;
        public MpScenarioAttribute(params string[] names) { Names = names; }
        public int Timeout;
        public int Players = 2;
        public string Start = "";
        public string Scene = "";
        public string Switches = "";
        public bool Quick;
        public bool Fresh = true;
        public string Covers = "";
        public string Isolate = "";
    }

    public static partial class Multiplayer
    {
        // TEST ONLY: helpers for mods' [MpScenario]s (the same the multiplayer mod's own scenarios use). Every copy runs
        // the same scenario; the role comes from the session (IsHost). Nothing here is meant for play.
        public static class Testing
        {
            // A self-test run (--mp-test=...).
            public static bool Active => Args.Test != null;
            // The scenario running now.
            public static string Current => Active ? SelfTest.Current : "";

            // "[MP] CHECK ok|FAIL what (detail)": a scenario fails if any check fails.
            public static bool Check(string what, bool ok, string detail = "") => SelfTest.Check(what, ok, detail);
            // Waits until every other copy reached the same barrier (it aborts the scenario everywhere if one never does).
            public static IEnumerator Barrier(string name, float timeout = 15f) => SelfTest.ApiBarrier(name, timeout);
            public static IEnumerator WaitFor(Func<bool> condition, float timeout) => SelfTest.ApiWaitFor(condition, timeout);
            // In game (a guest that boots in its own world: in the host's, unless the scenario starts there).
            public static IEnumerator WaitInGame() => SelfTest.WaitInGame();
            // In game, the session up with everyone, the guests in the host's world, and a barrier.
            public static IEnumerator Ready() => SelfTest.ApiReady();
            // Tells the other copies key=value (for this scenario), and what they told us (null: nothing yet).
            public static void Tell(string key, string value) => Chat.Send("#" + key + "=" + value);
            public static string Heard(string key) => SelfTest.ApiHeard(key);
            // Ends the scenario (its result line).
            public static IEnumerator Finish(string summary) => SelfTest.ApiFinish(summary);
            // A chat line (ours, a notice, an announcement) with this text, since the scenario started.
            public static bool ChatHas(string text) => Chat.Lines.Any(l => l.text != null && l.text.Contains(text));
            // Moves our character there (same room), as a teleport would.
            public static void MoveLocalPlayer(UnityEngine.Vector2 to)
            {
                var me = Game.LocalPlayer;
                if (me == null) return;
                me.rb.position = to;
                me.transform.position = new UnityEngine.Vector3(to.x, to.y, me.transform.position.z);
            }
            // Host: saves the world (the game's own save: the mods' side file and the object table go with it), then
            // reads both files back as a new session in this world would, and makes the scene's spawned objects again.
            public static void SaveAndReloadWorld()
            {
                if (!IsHost) return;
                GameData.SaveData();
                ApiStores.WorldKey = null;
                ApiSideFile.Load();
                ObjectSync.Instance?.LoadTable();
                ObjectSync.Instance?.Rescan();
                ApiSpawns.Rescan();
            }
        }
    }

    // The harness's side: other mods' [MpScenario] methods, as the harness's own scenarios.
    internal static class ApiTesting
    {
        private static List<(MethodInfo, ScenarioAttribute)> found;

        public static IEnumerable<(MethodInfo method, ScenarioAttribute info)> ModScenarios()
        {
            if (found != null) return found;
            found = new List<(MethodInfo, ScenarioAttribute)>();
            try
            {
                foreach (var asm in Chainloader.PluginInfos.Values.Where(i => i?.Instance != null).Select(i => i.Instance.GetType().Assembly).Distinct())
                {
                    if (asm == typeof(ApiTesting).Assembly) continue;
                    Type[] types;
                    try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    foreach (var t in types)
                        foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (m.ReturnType != typeof(IEnumerator) || m.GetParameters().Length != 0) continue;
                            foreach (MpScenarioAttribute a in m.GetCustomAttributes(typeof(MpScenarioAttribute), false))
                                found.Add((m, new ScenarioAttribute(a.Names)
                                {
                                    Timeout = a.Timeout, Players = a.Players, Start = a.Start ?? "", Scene = a.Scene ?? "", Switches = a.Switches ?? "",
                                    Quick = a.Quick, Fresh = a.Fresh, Covers = a.Covers ?? "", Isolate = a.Isolate ?? "",
                                }));
                        }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[api] Couldn't look for other mods' scenarios: " + e.Message); }
            if (found.Count > 0) Plugin.Log.LogInfo("[api] Other mods' scenarios: " + string.Join(", ", found.SelectMany(f => f.Item2.Names)));
            return found;
        }
    }
}
