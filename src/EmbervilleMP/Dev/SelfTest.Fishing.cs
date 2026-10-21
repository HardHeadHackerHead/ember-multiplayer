using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Where fishing exists: this scene's spots and manager, prefabs in memory, estate placeables, items, and
        // (deep) every Addressables prefab. One copy: .\scripts\run-test.ps1 fishsurvey -Scene <scene>
        [Scenario("fishsurvey")]
        private static IEnumerator FishSurvey()
        {
            yield return WaitInGame();
            yield return new WaitForSecondsRealtime(3f);
            var sb = new StringBuilder();
            int found = 0;
            yield return FishingSurvey(sb, true, n => found = n);
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, $"fishing-{Game.SceneName}.txt"), sb.ToString());
            Plugin.Log.LogInfo("[fish] survey:\n" + sb);
            Plugin.Log.LogMessage($"[MP] NOTE fishing in {Game.SceneName}: {found} fishing spot(s)/manager(s) found (scene, memory, placeables, Addressables)");
            yield return Finish($"fishing surveyed: {found} found");
        }

        // Counts FishingTrigger / FishingManager wherever the game could have them; reports the total through done.
        private static IEnumerator FishingSurvey(StringBuilder sb, bool deep, System.Action<int> done)
        {
            int found = 0;
            sb.AppendLine($"scene={Game.SceneName} area={Game.AreaName} FishingManager.HasMain={Singleton<FishingManager>.HasMain}");
            foreach (var t in Object.FindObjectsByType<FishingTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                found++;
                sb.AppendLine($"scene-spot '{FullPath(t.transform)}' active={t.gameObject.activeInHierarchy} area={AreaOf(t)} at={t.transform.position} dir={t.directionToLook} stand={(t.playerPosition != null ? t.playerPosition.position.ToString() : "-")}");
            }
            foreach (var m in Object.FindObjectsByType<FishingManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                found++;
                sb.AppendLine($"scene-manager '{FullPath(m.transform)}' active={m.gameObject.activeInHierarchy}");
            }
            // Loaded assets (prefabs and objects of other loaded scenes too).
            foreach (var t in Resources.FindObjectsOfTypeAll<FishingTrigger>().Where(x => !x.gameObject.scene.IsValid()))
            {
                found++;
                sb.AppendLine($"prefab-spot '{FullPath(t.transform)}'");
            }
            foreach (var m in Resources.FindObjectsOfTypeAll<FishingManager>().Where(x => !x.gameObject.scene.IsValid()))
            {
                found++;
                sb.AppendLine($"prefab-manager '{FullPath(m.transform)}'");
            }
            foreach (var p in Resources.FindObjectsOfTypeAll<PlaceableWorldObjectData>())
            {
                bool fishing = p.placeObject != null && (p.placeObject.GetComponentsInChildren<FishingTrigger>(true).Length > 0 || p.placeObject.GetComponentsInChildren<FishingManager>(true).Length > 0);
                if (fishing) found++;
                if (fishing || p.name.IndexOf("fish", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    sb.AppendLine($"placeable '{p.name}' fishing={fishing}");
            }
            sb.AppendLine($"placeables in memory: {Resources.FindObjectsOfTypeAll<PlaceableWorldObjectData>().Length}");
            foreach (var item in Resources.FindObjectsOfTypeAll<ItemData>().Where(i => i.name.IndexOf("fish", System.StringComparison.OrdinalIgnoreCase) >= 0))
                sb.AppendLine($"item '{item.name}'");
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
                sb.AppendLine($"build-scene {SceneUtility.GetScenePathByBuildIndex(i)}");

            // Addressables: keys that mention fishing, scenes, and (deep) every prefab opened and searched.
            var prefabs = new List<IResourceLocation>();
            var seen = new HashSet<string>();
            int keys = 0;
            foreach (var locator in Addressables.ResourceLocators)
            {
                foreach (var key in locator.Keys)
                {
                    keys++;
                    if (!locator.Locate(key, typeof(object), out IList<IResourceLocation> locs)) continue;
                    foreach (var l in locs)
                    {
                        if (!seen.Add(l.ResourceType?.Name + "|" + l.InternalId)) continue;
                        if ((l.PrimaryKey ?? "").IndexOf("fish", System.StringComparison.OrdinalIgnoreCase) >= 0 || (l.InternalId ?? "").IndexOf("fish", System.StringComparison.OrdinalIgnoreCase) >= 0)
                            sb.AppendLine($"addressable '{l.PrimaryKey}' type={l.ResourceType?.Name} id={l.InternalId}");
                        if (l.ResourceType == typeof(SceneInstance)) sb.AppendLine($"addressable-scene '{l.PrimaryKey}' id={l.InternalId}");
                        else if (l.ResourceType == typeof(GameObject)) prefabs.Add(l);
                    }
                }
            }
            sb.AppendLine($"addressables: {keys} keys, {seen.Count} locations, {prefabs.Count} prefabs");
            if (deep)
            {
                int opened = 0, failed = 0;
                foreach (var l in prefabs)
                {
                    GameObject go = null;
                    var h = default(UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject>);
                    try
                    {
                        h = Addressables.LoadAssetAsync<GameObject>(l);
                        go = h.WaitForCompletion();
                    }
                    catch (System.Exception) { failed++; }
                    if (go != null)
                    {
                        opened++;
                        int spots = go.GetComponentsInChildren<FishingTrigger>(true).Length, managers = go.GetComponentsInChildren<FishingManager>(true).Length;
                        if (spots + managers > 0)
                        {
                            found += spots + managers;
                            sb.AppendLine($"addressable-prefab '{l.PrimaryKey}' spots={spots} managers={managers}");
                        }
                    }
                    if (h.IsValid()) Addressables.Release(h);
                    if (opened % 10 == 0) yield return null;   // keep the frame (and the watchdog) going
                }
                sb.AppendLine($"addressable prefabs opened {opened}, failed {failed}");
            }
            sb.AppendLine($"TOTAL fishing objects: {found}");
            done(found);
        }

        // A FishingManager for a scene that has none: a copy of one in memory if any, else a bare one.
        private static GameObject MakeFishingManager()
        {
            var proto = Resources.FindObjectsOfTypeAll<FishingManager>().FirstOrDefault(m => !m.gameObject.scene.IsValid());
            if (proto != null) return Object.Instantiate(proto.gameObject);
            var go = new GameObject("MP_TestFishingManager");
            go.SetActive(false);
            var fm = go.AddComponent<FishingManager>();
            fm.sfxCatch = new AK.Wwise.Event();   // no sound set: Post does nothing (a null one would throw mid-catch)
            go.SetActive(true);
            return go;
        }

        // A fishing spot as a level would place it: a trigger, a direction to face, where to stand.
        private static FishingTrigger MakeFishingSpot(Vector2 stand, Direction dir)
        {
            var go = new GameObject("MP_TestFishingSpot");
            go.SetActive(false);
            go.transform.position = stand;
            var standAt = new GameObject("PlayerPosition").transform;
            standAt.SetParent(go.transform, false);
            var col = go.AddComponent<CircleCollider2D>();
            col.isTrigger = true;
            col.radius = 0.6f;
            var t = go.AddComponent<FishingTrigger>();
            t.directionToLook = dir;
            t.forcePlayerPosition = true;
            t.playerPosition = standAt;
            go.SetActive(true);
            return t;
        }

        private static void LayerHashes(Animator a, int layer, ISet<int> into)
        {
            into.Add(a.GetCurrentAnimatorStateInfo(layer).fullPathHash);
            if (a.IsInTransition(layer)) into.Add(a.GetNextAnimatorStateInfo(layer).fullPathHash);
        }

        // The guest fishes through the game's own code (FishingTrigger.OnInteract -> FishingManager); the host's
        // puppet of the guest shows it (poses, catch effect where the game puts it) and only the guest gets the catch.
        // The demo has no fishing spots, so unless this scene has one, both copies build one (and a FishingManager).
        [Scenario("fishing", Timeout = 200)]
        private static IEnumerator FishingTest()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            yield return WaitFor(() => s.Ready && s.Players.Count >= 2, 90f);
            yield return WaitFor(() => Game.InGame && (s.IsHost || GuestWorld.Active) && !GuestWorld.Switching, 90f);
            yield return Barrier("ready", 120f);
            var fs = FishingSync.Instance;
            if (!Check("fishing sync running", fs != null)) { yield return Finish(""); yield break; }

            var survey = new StringBuilder();
            int inScene = 0;
            yield return FishingSurvey(survey, false, n => inScene = n);
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, $"fishing-{Game.SceneName}.txt"), survey.ToString());
            var real = Object.FindObjectsByType<FishingTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => t.GetComponentInParent<GameArea>(true) != null && Singleton<FishingManager>.HasMain)
                .OrderBy(t => NetIds.ScenePath(t.transform)).FirstOrDefault();
            Plugin.Log.LogMessage($"[MP] NOTE fishing objects found here: {inScene}; using {(real != null ? "the scene's spot " + NetIds.ScenePath(real.transform) : "a spot built for the test")}");

            string room = real != null ? AreaOf(real) : "001_Barrack";
            Vector2 stand = real != null ? (Vector2)(real.playerPosition != null ? real.playerPosition.position : real.transform.position) : new Vector2(-104f, 70f);
            yield return TeleportTo(room, s.IsHost ? stand + new Vector2(-3f, 0f) : stand);
            yield return Barrier("placed", 60f);

            GameObject builtManager = null;
            var spot = real;
            if (spot == null)
            {
                if (!Singleton<FishingManager>.HasMain) builtManager = MakeFishingManager();
                spot = MakeFishingSpot(stand, Direction.Down);
            }
            yield return null;
            yield return null;   // Start: the manager finds the player, the spot checks for the manager
            Check("a FishingManager is running", Singleton<FishingManager>.HasMain && Singleton<FishingManager>.main.player == Game.LocalPlayer);
            Check("the fishing spot is active", spot != null && spot.gameObject.activeInHierarchy);
            yield return Barrier("spot");

            var me = Game.LocalPlayer;
            byte guestId = s.IsHost ? s.Players.Keys.FirstOrDefault(id => id != s.LocalId) : s.LocalId;
            int xpBefore = GameData.playerData.GetTotalXP(LifeSkillJob.Fishing);
            // Whether game time keeps running: a popup may set timeScale 0 for a moment (the mod sets it back in
            // LateUpdate while others are here; a coroutine can see the 0 in between), so judge by frames that
            // really stood still (deltaTime 0) and by how far game time got against real time.
            float game0 = Time.time, real0 = Time.realtimeSinceStartup;
            int stillFrames = 0, longestStill = 0;
            void SampleClock()
            {
                stillFrames = Time.deltaTime > 0f ? 0 : stillFrames + 1;
                longestStill = Mathf.Max(longestStill, stillFrames);
            }
            var fm = Singleton<FishingManager>.main;

            if (!s.IsHost)
            {
                // What our own animators show before (idle) and while fishing, how long each state lasts.
                var idle = new HashSet<int>[me.animator.layerCount];
                for (int l = 0; l < idle.Length; l++) { idle[l] = new HashSet<int>(); LayerHashes(me.animator, l, idle[l]); }
                var fxIdle = new HashSet<int>();
                if (me.extraFxAnimator != null) LayerHashes(me.extraFxAnimator, 0, fxIdle);
                var held = new Dictionary<(int layer, int hash), float>();
                var fxCatch = new HashSet<int>();
                var phases = new List<FishPhase>();

                spot.OnInteract(me.gameObject);
                Check("the guest is fishing (the game's FishingManager took it)", fm.isInteracting);
                float t0 = Time.realtimeSinceStartup, catchAt = -1f;
                bool forced = false;
                while (Time.realtimeSinceStartup - t0 < 15f)
                {
                    SampleClock();
                    var ph = fs.LocalPhase;
                    if (phases.Count == 0 || phases[phases.Count - 1] != ph) phases.Add(ph);
                    if (ph == FishPhase.None && phases.Count > 1) break;
                    if (ph == FishPhase.Cast || ph == FishPhase.Reel)
                        for (int l = 0; l < me.animator.layerCount; l++)
                        {
                            var info = me.animator.GetCurrentAnimatorStateInfo(l);
                            var key = (l, info.fullPathHash);
                            held[key] = (held.TryGetValue(key, out var d) ? d : 0f) + Time.unscaledDeltaTime;
                        }
                    if (ph == FishPhase.Catch)
                    {
                        if (catchAt < 0f) catchAt = Time.realtimeSinceStartup;
                        if (me.extraFxAnimator != null) LayerHashes(me.extraFxAnimator, 0, fxCatch);
                        // The game ends fishing on the animation's AttackEnd event; if this build's clip lacks it,
                        // end it the way that event would (and say so).
                        if (!forced && Time.realtimeSinceStartup - catchAt > 6f)
                        {
                            forced = true;
                            Plugin.Log.LogMessage("[MP] NOTE the game's fishing didn't end by itself 6 s after the catch (no AttackEnd event?): ending it via OnAttackEnd");
                            me.OnAttackEnd?.InvokeAndClear();
                        }
                    }
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.3f);
                Check("fishing went cast, reel, catch, done", string.Join(",", phases) == "Cast,Reel,Catch,None", string.Join(",", phases));
                Check("fishing ended", !fm.isInteracting && fs.LocalPhase == FishPhase.None);
                var poses = held.Where(kv => kv.Value >= 0.3f && !idle[kv.Key.layer].Contains(kv.Key.hash)).Select(kv => kv.Key).ToList();
                Check("the game's fishing poses played here", poses.Count > 0, $"{poses.Count} layer states");
                fxCatch.ExceptWith(fxIdle);
                Check("the catch effect played here", fxCatch.Count > 0, $"{fxCatch.Count} effect states");
                int xpAfter = GameData.playerData.GetTotalXP(LifeSkillJob.Fishing);
                Check("the catch (Fishing XP) went to the guest", xpAfter == xpBefore + 1, $"{xpBefore} -> {xpAfter}");
                Chat.Send("#fishposes=" + string.Join(",", poses.Select(p => p.layer + ":" + p.hash)));
                Chat.Send("#fishfx=" + string.Join(",", fxCatch));
                Chat.Send("#fishforced=" + forced);
            }
            else
            {
                var presence = MPRunner.Instance.Presence;
                RemotePlayer rp = null;
                yield return WaitFor(() => presence.Remotes.TryGetValue(guestId, out rp) && rp.Shown, 10f);
                if (Check("the guest's puppet is shown", rp != null && rp.Shown))
                {
                    var seen = new HashSet<(int, int)>();
                    var fxSeen = new HashSet<int>();
                    bool offsetInCatch = false, catchSeen = false;
                    float t0 = Time.realtimeSinceStartup;
                    Vector3 want = Direction.Down.ToVector();
                    string offsetDetail = "";
                    while (Time.realtimeSinceStartup - t0 < 25f && !(peerData.ContainsKey("fishfx") && fs.PhaseOf(guestId) == FishPhase.None && catchSeen))
                    {
                        SampleClock();
                        var pa = rp.PuppetAnimator;
                        if (pa != null)
                            for (int l = 0; l < pa.layerCount; l++)
                            {
                                seen.Add((l, pa.GetCurrentAnimatorStateInfo(l).fullPathHash));
                                if (pa.IsInTransition(l)) seen.Add((l, pa.GetNextAnimatorStateInfo(l).fullPathHash));
                            }
                        var pfx = rp.Puppet != null ? rp.Puppet.transform.Find("Player_ExtraFxAnimations")?.GetComponent<Animator>() : null;
                        if (pfx != null && pfx.isActiveAndEnabled)
                        {
                            fxSeen.Add(pfx.GetCurrentAnimatorStateInfo(0).fullPathHash);
                            if (pfx.IsInTransition(0)) fxSeen.Add(pfx.GetNextAnimatorStateInfo(0).fullPathHash);
                        }
                        if (fs.PhaseOf(guestId) == FishPhase.Catch)
                        {
                            catchSeen = true;
                            var off = fs.PuppetFxOffset(guestId);
                            offsetDetail = off?.ToString() ?? "no puppet effects";
                            if (off.HasValue && (off.Value - want).sqrMagnitude < 0.01f) offsetInCatch = true;
                        }
                        yield return null;
                    }
                    yield return new WaitForSecondsRealtime(0.3f);
                    var hist = fs.HistoryOf(guestId);
                    Check("the host saw the guest cast, reel, catch, finish", string.Join(",", hist) == "Cast,Reel,Catch,None", string.Join(",", hist));
                    var poses = peerData.TryGetValue("fishposes", out var pz) && pz.Length > 0
                        ? pz.Split(',').Select(x => x.Split(':')).Select(x => (int.Parse(x[0], CultureInfo.InvariantCulture), int.Parse(x[1], CultureInfo.InvariantCulture))).ToList()
                        : new List<(int, int)>();
                    int shown = poses.Count(p => seen.Contains(p));
                    Check("the puppet showed the guest's fishing poses", poses.Count > 0 && shown == poses.Count, $"{shown}/{poses.Count}");
                    var fxWanted = peerData.TryGetValue("fishfx", out var fz) && fz.Length > 0 ? fz.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList() : new List<int>();
                    Check("the puppet played the guest's catch effect", fxWanted.Any(fxSeen.Contains), $"{fxWanted.Count(fxSeen.Contains)}/{fxWanted.Count}");
                    Check("the puppet's catch effect sits towards the water", offsetInCatch, offsetDetail);
                    var after = fs.PuppetFxOffset(guestId);
                    Check("and goes back after", after.HasValue && after.Value.sqrMagnitude < 0.0001f, after?.ToString() ?? "-");
                    Check("the puppet has no fishing indicator", rp.Puppet != null && rp.Puppet.transform.Find("Fishing") == null);
                    Check("the host didn't fish", fs.LocalPhase == FishPhase.None && !fm.isInteracting);
                    int xpAfter = GameData.playerData.GetTotalXP(LifeSkillJob.Fishing);
                    Check("the guest's catch gave the host nothing", xpAfter == xpBefore, $"{xpBefore} -> {xpAfter}");
                    if (peerData.TryGetValue("fishforced", out var forced) && forced == "True")
                        Plugin.Log.LogMessage("[MP] NOTE the guest's fishing had to be ended by the test (see the guest's log)");
                }
            }
            float ratio = (Time.time - game0) / Mathf.Max(0.001f, (Time.realtimeSinceStartup - real0) * Args.TimeScale);
            Check("time kept running (fishing never paused co-op)", longestStill <= 1 && ratio > 0.6f, $"longest still run {longestStill} frames, game/real time {ratio:F2}");
            Plugin.Log.LogInfo($"[fish] phases sent {fs.PhasesSent} received {fs.PhasesReceived} puppet offsets applied {fs.OffsetsApplied}");
            yield return Barrier("done", 40f);
            if (real == null && spot != null) Object.Destroy(spot.gameObject);
            if (builtManager != null) Object.Destroy(builtManager);
            yield return Finish(real != null ? "fishing in sync (the scene's spot)" : "fishing in sync (a spot built for the test: the demo has none)");
        }
    }
}
