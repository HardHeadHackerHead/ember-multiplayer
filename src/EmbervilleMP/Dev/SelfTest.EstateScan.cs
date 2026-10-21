using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Phase D survey: where the estate lives and what the estate flags unlock (one copy, any scene).
        [Scenario("estatescan")]
        private static IEnumerator EstateScan()
        {
            yield return WaitInGame();
            yield return new WaitForSecondsRealtime(3f);
            var sb = new StringBuilder();
            sb.AppendLine($"scene={Game.SceneName} area={Game.AreaName} player={Game.LocalPlayer.transform.position}");
            void Flags(string when)
            {
                foreach (var b in new[] { BoolID.PlaceablesActive, BoolID.RepairablesActive, BoolID.PortalRepairActive, BoolID.GumuIntroductionDone, BoolID.FinishedTutorial })
                    sb.AppendLine($"{when} bool {b} = {GameData.gameBools.IsTrue(b)}");
            }
            void Census(string when)
            {
                sb.AppendLine($"--- {when}");
                void Count<T>() where T : Object
                {
                    var all = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                    sb.AppendLine($"{typeof(T).Name}: {all.Length}" + (all.Length > 0 ? "  e.g. " + string.Join(", ", all.Take(4).Select(x => x.name)) : ""));
                }
                Count<SoilTile>(); Count<FarmingManager>(); Count<FarmingController>(); Count<PlaceableController>();
                Count<PlacementManager>(); Count<TilesetDrawerManager>(); Count<PlacementObjectComponent>(); Count<PlaceablePrefab>();
                Count<RepairableBuilding>(); Count<DestructedPortal>(); Count<CraftingStation>(); Count<CutsceneGumu>();
                sb.AppendLine($"HasMain FarmingManager={Singleton<FarmingManager>.HasMain} PlaceableController={Singleton<PlaceableController>.HasMain} TilesetDrawerManager={Singleton<TilesetDrawerManager>.HasMain}");
                // Assets loaded (databases): what could be placed or planted.
                foreach (var db in Resources.FindObjectsOfTypeAll<PlaceablesDataBase>())
                    sb.AppendLine($"placeables db '{db.name}': " + string.Join(", ", db.placeables.Where(p => p != null).Select(p => $"{p.name}[{p.type},{p.GetType().Name},{p.saveID}]")));
                foreach (var fd in Resources.FindObjectsOfTypeAll<FarmingDatabase>())
                    sb.AppendLine($"farming db '{fd.name}': crops=" + string.Join(", ", fd.crops.Select(c => $"{c.cropType}->{c.resultItemData?.name}x{c.resultAmount} seed={c.seedItemData?.name}")));
                var rx = new Regex("Soil|Farm|Placeable|Estate|Plot|Tileset|Tilemap|Garden|Crop|Build", RegexOptions.IgnoreCase);
                foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (rx.IsMatch(t.name) && (t.parent == null || !rx.IsMatch(t.parent.name)))
                        sb.AppendLine($"obj '{FullPath(t)}' active={t.gameObject.activeInHierarchy} comps=[{string.Join(",", t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name))}] children={t.childCount}");
            }
            Flags("before");
            Census("before flags");
            GameData.gameBools[BoolID.PlaceablesActive] = true;
            GameData.gameBools[BoolID.RepairablesActive] = true;
            GameData.gameBools[BoolID.PortalRepairActive] = true;
            yield return new WaitForSecondsRealtime(3f);
            Flags("after");
            Census("after flags (same scene)");
            var types = new Dictionary<string, int>();
            foreach (var c in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string n = c.GetType().FullName;
                types[n] = types.TryGetValue(n, out int k) ? k + 1 : 1;
            }
            sb.AppendLine("--- component types: " + string.Join(", ", types.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}")));
            foreach (var t in Object.FindObjectsByType<Teleporter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"teleporter '{FullPath(t.transform)}' id={t.teleporterID} scene={t.sceneToLoad} dest={t.destinationID} active={t.gameObject.activeInHierarchy}");
            // What the build's Resources folder has (assets nobody references from a scene are only reachable there).
            if (Args.Has("scan-resources"))
            {
                void Res<T>() where T : Object
                {
                    var all = Resources.LoadAll<T>("");
                    sb.AppendLine($"resources {typeof(T).Name}: {all.Length}" + (all.Length > 0 ? "  e.g. " + string.Join(", ", all.Take(12).Select(x => x.name)) : ""));
                }
                Res<FarmingDatabase>(); Res<Crop>(); Res<Fertilizer>(); Res<PlaceablesDataBase>(); Res<PlaceableData>(); Res<TilesetItem>();
                foreach (var go in Resources.LoadAll<GameObject>(""))
                {
                    var hits = go.GetComponentsInChildren<MonoBehaviour>(true).Where(c => c is SoilTile || c is FarmingManager || c is PlaceableController || c is TilesetDrawerManager || c is PlacementObjectComponent || c is PlacementManager).Select(c => c.GetType().Name).Distinct().ToList();
                    if (hits.Count > 0) sb.AppendLine($"resources prefab '{go.name}': {string.Join(",", hits)}");
                }
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, $"estatescan-{Game.SceneName}.txt"), sb.ToString());
            Plugin.Log.LogInfo("[dev] estate scan written");
            yield return Finish("estate scanned");
        }
    
        // How the demo ends in Town: what calls DemoLock, what shows the "thank you" screen (one copy, fresh world).
        [Scenario("gumuscan")]
        private static IEnumerator GumuScan()
        {
            if (Args.Has("intro-done"))
            {
                yield return WaitFor(() => GameData.HasWorldData, 90f);
                GameData.gameBools[BoolID.GumuIntroductionDone] = true;
            }
            yield return WaitInGame();
            var sb = new StringBuilder();
            void Line(string s) { sb.AppendLine(s); Plugin.Log.LogInfo("[dev] gumu: " + s); }
            foreach (var tt in Object.FindObjectsByType<TimedTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Line($"timed '{FullPath(tt.transform)}' when={tt.triggerWhen} waitFade={tt.waitStartFadeIn} delay={tt.delaySeconds} active={tt.gameObject.activeInHierarchy}");
            var thanksRoot = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(t => t.name == "=== ThanksForPlaying");
            if (thanksRoot != null) foreach (var t in thanksRoot.GetComponentsInChildren<Transform>(true).Take(30))
                Line($"  thanks child '{FullPath(t)}' comps=[{string.Join(",", t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name))}]");
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                for (var t = mb.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                    foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                        if (typeof(UnityEngine.Events.UnityEventBase).IsAssignableFrom(f.FieldType) && f.GetValue(mb) is UnityEngine.Events.UnityEventBase ev)
                            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                            {
                                string m = ev.GetPersistentMethodName(i);
                                var target = ev.GetPersistentTarget(i);
                                string tn = target != null ? target.name : "null";
                                if (m.Contains("Demo") || tn.Contains("Thank") || m.Contains("Thank") || (target is CutsceneGumu))
                                    Line($"event {FullPath(mb.transform)} active={mb.gameObject.activeInHierarchy} {mb.GetType().Name}.{f.Name}[{i}] -> {tn}({target?.GetType().Name}).{m} mode={ev.GetPersistentListenerState(i)}");
                            }
            var thanks = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None).Where(t => t.name.Contains("Thank")).ToList();
            foreach (var t in thanks)
                Line($"thanks obj '{FullPath(t)}' active={t.gameObject.activeInHierarchy} comps=[{string.Join(",", t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name))}]");
            var gumu = Object.FindObjectsByType<CutsceneGumu>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            Line($"gumu cutscene: {(gumu != null ? FullPath(gumu.transform) : "none")} introDone={GameData.gameBools.IsTrue(BoolID.GumuIntroductionDone)}");
            var info = HarmonyLib.Harmony.GetPatchInfo(HarmonyLib.AccessTools.Method(typeof(CutsceneGumu), nameof(CutsceneGumu.DemoLock)));
            Line($"DemoLock patched: {(info != null ? info.Prefixes.Count : 0)} prefix(es)");
            thanks.AddRange(Object.FindObjectsByType<TimedTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None).Select(x => x.transform));
            float t0 = Time.realtimeSinceStartup;
            var shown = new HashSet<Transform>();
            int locks = -1;
            string lastSt = "";
            while (Time.realtimeSinceStartup - t0 < 100f)
            {
                foreach (var t in thanks)
                    if (t != null && t.gameObject.activeInHierarchy && shown.Add(t)) Line($"{Time.realtimeSinceStartup - t0:F1}s thanks shown: {FullPath(t)}");
                int n = Sync.EstateRules.DemoLocksKept + Sync.EstateRules.DemoLocksSkipped;
                string st = string.Join(",", Game.LocalPlayer.staticOwners.Select(o => o.ToString())) + $" graph={(gumu != null && gumu.agent != null && gumu.agent.Graph != null ? gumu.agent.Graph.IsRunning.ToString() : "-")} gumu={(gumu != null)}";
                if (st != lastSt) { lastSt = st; Line($"{Time.realtimeSinceStartup - t0:F1}s static={st} pos={Game.LocalPlayer.transform.position}"); }
                if (n != locks) { locks = n; Line($"{Time.realtimeSinceStartup - t0:F1}s demo locks={n} static={string.Join(",", Game.LocalPlayer.staticOwners.Select(o => o.ToString()))} state={Game.Manager.gameState} introDone={GameData.gameBools.IsTrue(BoolID.GumuIntroductionDone)}"); }
                yield return new WaitForSecondsRealtime(0.25f);
            }
            Line($"end: static={string.Join(",", Game.LocalPlayer.staticOwners.Select(o => o.ToString()))} state={Game.Manager.gameState} timeScale={Time.timeScale} introDone={GameData.gameBools.IsTrue(BoolID.GumuIntroductionDone)}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, "gumuscan.txt"), sb.ToString());
            yield return Finish("gumu scanned");
        }
    }
}
