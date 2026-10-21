using System.Collections;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // ---------------- estate sync (on the test estate: see EstateFixture) ----------------

        private static readonly Vector3 SoilPos = new Vector3(1000f, 1000f, 0f), BoxPos = new Vector3(1004f, 1000f, 0f), FencePos = new Vector3(1008f, 1000f, 0f);

        // Two stackable materials, the same on every copy: the test crop's seed and harvest.
        private static (ItemData seed, ItemData result) TestItems()
        {
            var mats = Resources.FindObjectsOfTypeAll<ItemData>().Where(i => i.IsStackable && (i.itemCategory & ItemCategory.Material) != 0)
                .OrderBy(i => i.name, System.StringComparer.Ordinal).ToList();
            return (mats.FirstOrDefault(), mats.Skip(1).FirstOrDefault() ?? mats.FirstOrDefault());
        }

        private static int DropsNear(Vector3 pos) =>
            Object.FindObjectsByType<ItemDropObject>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Count(d => Vector2.Distance(d.transform.position, pos) < 4f);

        private static string SoilText(SoilTile t) =>
            t == null ? "none" : $"{t.State} watered={t.isWatered} crop={(t.ActiveCrop != null ? t.ActiveCrop.cropType.ToString() : "-")} step={t.lastCropStep} progress={t.growingProgress:F2} ready={t.canHarvest}";

        // What a player's farming input does (FarmingManager's own actions; planting without its seed HUD).
        private static IEnumerator Farm(SoilTile soil, FarmAction action)
        {
            var fm = EstateFixture.Farm;
            yield return WaitFor(() => !fm.isBusy, 5f);
            switch (action)
            {
                case FarmAction.Plow: fm.OnPlow(soil); break;
                case FarmAction.Water: fm.OnWater(soil); break;
                case FarmAction.Harvest: fm.OnHarvest(soil); break;
                case FarmAction.Plant:
                    var crop = EstateFixture.Crop;
                    crop.ConsumeSeed();
                    fm.BaseAction(FarmAction.Plant, fm.cooldownPlant, () => soil.Plant(crop, Singleton<TimeManager>.main.CurrentGameDateTime));
                    break;
            }
            yield return null;
            yield return WaitFor(() => !fm.isBusy, 5f);
        }

        // Host and guest in one scene with the estate unlocked (--mp-estate): the guest farms, both see the crop grow,
        // one harvest between the two, the host builds and the guest removes (and the same for tiles), the host's
        // save has it all.
        [Scenario("estate", Timeout = 220, Switches = "estate keep-playing", Isolate = "estate")]
        private static IEnumerator EstateTest()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            yield return BothInScene(Args.AutoStartScene ?? "TutorialArea");
            yield return WaitFor(() => EstateRules.Flags.All(f => GameData.gameBools.IsTrue(f)), 15f);
            Check("estate flags set in the world", EstateRules.Flags.All(f => GameData.gameBools.IsTrue(f)),
                string.Join(",", EstateRules.Flags.Select(f => $"{f}={GameData.gameBools.IsTrue(f)}")));
            if (!s.IsHost) Check("guest has the host's estate rules", EstateRules.Unlocked);

            var (seed, result) = TestItems();
            if (!Check("found test items", seed != null && result != null)) { yield return Finish(""); yield break; }
            yield return EstateFixture.Build(seed, result);
            Check("test estate built", EstateFixture.Built);
            // A game hour is ~64 s here: 5 game minutes (~5 s, still several clock ticks) instead of the fixture's
            // quarter hour (~16 s). (estate-late keeps the quarter hour: its crop must still be growing when the
            // guest arrives.)
            if (EstateFixture.Crop != null) EstateFixture.Crop.growingTime = new Vector2(0.08f, 0.08f);
            yield return Barrier("built");
            var es = EstateSync.Instance;
            var inv = Singleton<Inventory>.main;

            // ---- the guest makes a farm plot and farms it ----
            if (!s.IsHost)
            {
                EstateFixture.Place(EstateFixture.SoilData, SoilPos);
                var mine = EstateFixture.SoilAt(SoilPos);
                Check("guest placed a farm plot", mine != null && EstateFixture.HasTile(TilemapLayer.Farm, SoilPos));
                Chat.Send("#soilid=" + (mine != null ? mine.saveID : ""));
            }
            yield return Barrier("plot");
            SoilTile soil = null;
            yield return WaitFor(() => (soil = EstateFixture.SoilAt(SoilPos)) != null, 10f);
            if (s.IsHost)
            {
                Check("host sees the guest's farm plot", soil != null && EstateFixture.HasTile(TilemapLayer.Farm, SoilPos) && EstateFixture.HasSaved(EstateFixture.SoilID, SoilPos));
                yield return WaitFor(() => peerData.ContainsKey("soilid"), 10f);
                peerData.TryGetValue("soilid", out var sid);
                Check("the plot has the guest's save id", soil != null && !string.IsNullOrEmpty(sid) && sid == soil.saveID, $"{soil?.saveID} / {sid}");
            }
            if (soil == null) { yield return Finish(""); yield break; }
            if (!s.IsHost)
            {
                yield return Farm(soil, FarmAction.Plow);
                Check("guest plowed", soil.State == SoilState.Plowed, SoilText(soil));
                inv.AddItem(seed.CreateItem(2));
                yield return Farm(soil, FarmAction.Plant);
                Check("guest planted", soil.State == SoilState.Planted && soil.ActiveCrop != null, SoilText(soil));
                yield return Farm(soil, FarmAction.Water);
                Check("guest watered", soil.isWatered, SoilText(soil));
            }
            yield return Barrier("farmed");
            if (s.IsHost)
            {
                yield return WaitFor(() => soil.State == SoilState.Planted && soil.isWatered && soil.ActiveCrop == EstateFixture.Crop, 8f);
                Check("host sees the guest's plowing, planting and watering", soil.State == SoilState.Planted && soil.isWatered && soil.ActiveCrop == EstateFixture.Crop, SoilText(soil));
            }

            // ---- it grows on the shared clock ----
            float t0 = Time.realtimeSinceStartup;
            yield return WaitFor(() => soil.canHarvest, 40f);
            Check("the crop grew ready here", soil.canHarvest, $"{SoilText(soil)} after {Time.realtimeSinceStartup - t0:F0}s");
            yield return Barrier("grown", 40f);

            // ---- both harvest at once: one crop ----
            int dropsBefore = DropsNear(soil.transform.position);
            yield return Farm(soil, FarmAction.Harvest);
            yield return new WaitForSecondsRealtime(4f);
            yield return WaitFor(() => !EstateFixture.Farm.isBusy, 5f);
            int got = DropsNear(soil.transform.position) - dropsBefore;
            Chat.Send("#harvested=" + got);
            yield return WaitFor(() => peerData.ContainsKey("harvested"), 10f);
            int theirs = peerData.TryGetValue("harvested", out var h) ? int.Parse(h) : -1;
            Check("exactly one harvest between the two players", got >= 0 && theirs >= 0 && got + theirs == EstateFixture.Crop.resultAmount, $"mine {got}, theirs {theirs}");
            yield return WaitFor(() => soil.State == SoilState.Plowed && !soil.canHarvest, 5f);
            Check("the harvested plot is plowed again here", soil.State == SoilState.Plowed && !soil.canHarvest, SoilText(soil));
            Plugin.Log.LogInfo($"[test] claims sent {es.ClaimsSent} granted {es.ClaimsGranted} refused {es.ClaimsRefused}");
            yield return Barrier("harvested");

            // ---- the host builds (and pays), the guest removes it (and gets the refund) ----
            int gold0 = inv.TotalGold.CurrentValue;
            if (s.IsHost)
            {
                inv.AddGold(100);
                gold0 = inv.TotalGold.CurrentValue;
                EstateFixture.Place(EstateFixture.BoxData, BoxPos);
                Check("host built a box", EstateFixture.HasObject(EstateFixture.BoxID, BoxPos));
                Check("host paid for it", inv.TotalGold.CurrentValue == gold0 - EstateFixture.BoxCost, $"{gold0} -> {inv.TotalGold.CurrentValue}");
                gold0 = inv.TotalGold.CurrentValue;
            }
            yield return Barrier("built-box");
            if (!s.IsHost)
            {
                yield return WaitFor(() => EstateFixture.HasObject(EstateFixture.BoxID, BoxPos), 8f);
                Check("guest sees the host's box", EstateFixture.HasObject(EstateFixture.BoxID, BoxPos) && EstateFixture.HasSaved(EstateFixture.BoxID, BoxPos));
                Check("the box cost the guest nothing", inv.TotalGold.CurrentValue == gold0, $"{gold0} -> {inv.TotalGold.CurrentValue}");
                EstateFixture.Remove(BoxPos);
                yield return null;
                Check("guest removed the box", !EstateFixture.HasObject(EstateFixture.BoxID, BoxPos) && !EstateFixture.HasSaved(EstateFixture.BoxID, BoxPos));
                Check("guest got the refund", inv.TotalGold.CurrentValue == gold0 + EstateFixture.BoxCost, $"{gold0} -> {inv.TotalGold.CurrentValue}");
            }
            yield return Barrier("removed-box");
            if (s.IsHost)
            {
                yield return WaitFor(() => !EstateFixture.HasObject(EstateFixture.BoxID, BoxPos), 8f);
                yield return null;
                Check("the guest's removal reached the host", !EstateFixture.HasObject(EstateFixture.BoxID, BoxPos) && !EstateFixture.HasSaved(EstateFixture.BoxID, BoxPos));
                Check("no refund for the host", inv.TotalGold.CurrentValue == gold0, $"{gold0} -> {inv.TotalGold.CurrentValue}");
            }

            // ---- tiles: the host lays a fence, the guest takes it away ----
            if (s.IsHost)
            {
                EstateFixture.Place(EstateFixture.FenceData, FencePos);
                Check("host laid a fence", EstateFixture.HasTile(TilemapLayer.Fences, FencePos));
            }
            yield return Barrier("fenced");
            if (!s.IsHost)
            {
                yield return WaitFor(() => EstateFixture.HasTile(TilemapLayer.Fences, FencePos), 8f);
                Check("guest sees the host's fence", EstateFixture.HasTile(TilemapLayer.Fences, FencePos));
                EstateFixture.Remove(FencePos);
                Check("guest removed the fence", !EstateFixture.HasTile(TilemapLayer.Fences, FencePos));
            }
            yield return Barrier("unfenced");
            if (s.IsHost)
            {
                yield return WaitFor(() => !EstateFixture.HasTile(TilemapLayer.Fences, FencePos), 8f);
                Check("the fence is gone at the host's too", !EstateFixture.HasTile(TilemapLayer.Fences, FencePos));
            }

            // ---- the host's save keeps the guest's work; the guest writes no world ----
            if (s.IsHost)
            {
                GameData.SaveData();
                string path = SaveLoad.GetPath(GameData.GetWorldPrefix + "_estateFriendly.json");
                var saved = System.IO.File.Exists(path) ? JsonUtility.FromJson<PlayerEstateData>(System.IO.File.ReadAllText(path)) : null;
                Check("host saved its estate", saved != null, path);
                if (saved != null)
                {
                    var plot = saved.savedPlaceables.FirstOrDefault(p => p.placeableID == EstateFixture.SoilID);
                    Check("saved: the guest's farm plot, with its id", plot != null && plot.instanceSaveID == soil.saveID, plot?.instanceSaveID);
                    var st = saved.savedSoilStates.FirstOrDefault(x => x.saveID == soil.saveID);
                    Check("saved: the plot's soil (plowed after the harvest)", st != null && st.state == SoilState.Plowed, st != null ? st.state.ToString() : "none");
                    Check("saved: no box", !saved.savedPlaceables.Any(p => p.placeableID == EstateFixture.BoxID));
                    var farm = saved.tilemapLayers.FirstOrDefault(l => l.layer == TilemapLayer.Farm);
                    var fences = saved.tilemapLayers.FirstOrDefault(l => l.layer == TilemapLayer.Fences);
                    Check("saved: the plot's tile, no fence",
                        farm != null && farm.savedTiles.Any(t => t.pos.x == 1000 && t.pos.y == 1000) && (fences == null || fences.savedTiles.Count == 0));
                }
            }
            else
            {
                GameData.SaveData();
                Check("guest wrote no estate file", !System.IO.File.Exists(SaveLoad.GetPath(GuestWorld.GuestPrefix + ".eve")) && !System.IO.File.Exists(SaveLoad.GetPath(GuestWorld.GuestPrefix + "_estateFriendly.json")));
            }
            Plugin.Log.LogInfo($"[test] estate ops sent {es.OpsSent} ({es.ChangesSent} changes), applied live {es.ChangesLive}, as data {es.ChangesData}, soils sent {es.SoilsSent}");
            yield return Barrier("done");
            yield return Finish("estate synced");
        }

        // A guest joining later (--mp-join-later) gets the estate as the host has it, crops included, and changes after.
        [Scenario("estate-late", Timeout = 240, Start = "own-world", Switches = "estate keep-playing", Isolate = "estate")]
        private static IEnumerator EstateLateTest()
        {
            HookPeerData();
            yield return WaitInGame();
            var (seed, result) = TestItems();
            if (!Check("found test items", seed != null && result != null)) { yield return Finish(""); yield break; }
            if (Args.Host)
            {
                var s = Session.Current;
                yield return WaitFor(() => GameData.gameBools.IsTrue(BoolID.PlaceablesActive), 10f);
                yield return EstateFixture.Build(seed, result);
                EstateFixture.Place(EstateFixture.SoilData, SoilPos);
                var soil = EstateFixture.SoilAt(SoilPos);
                yield return Farm(soil, FarmAction.Plow);
                Singleton<Inventory>.main.AddItem(seed.CreateItem(2));
                yield return Farm(soil, FarmAction.Plant);
                yield return Farm(soil, FarmAction.Water);
                EstateFixture.Place(EstateFixture.FenceData, FencePos);
                Singleton<Inventory>.main.AddGold(100);
                EstateFixture.Place(EstateFixture.BoxData, BoxPos);
                Check("host's estate ready", soil.State == SoilState.Planted && soil.isWatered && EstateFixture.HasTile(TilemapLayer.Fences, FencePos) && EstateFixture.HasObject(EstateFixture.BoxID, BoxPos), SoilText(soil));
                yield return WaitFor(() => s.Players.Count >= 2, 120f);
                Chat.Send("#hostsoil=" + soil.saveID);
                yield return Barrier("arrived", 120f);
                // a change after the guest arrived
                EstateFixture.Remove(BoxPos);
                yield return null;   // (Destroy happens at the end of the frame)
                Check("host removed the box", !EstateFixture.HasObject(EstateFixture.BoxID, BoxPos));
                yield return Barrier("checked", 60f);
            }
            else
            {
                yield return WaitFor(() => Session.Current != null && Session.Current.Ready && GuestWorld.Active && Game.InGame && !GuestWorld.Switching, 120f);
                yield return new WaitForSecondsRealtime(2f);
                Check("guest joined the host's world", GuestWorld.Active);
                yield return EstateFixture.Build(seed, result);
                yield return WaitFor(() => peerData.ContainsKey("hostsoil"), 10f);
                var soil = EstateFixture.SoilAt(SoilPos);
                peerData.TryGetValue("hostsoil", out var id);
                Check("late guest has the host's farm plot", soil != null && soil.saveID == id, $"{soil?.saveID} / {id}");
                Check("late guest has the host's crop, watered", soil != null && soil.State == SoilState.Planted && soil.isWatered && soil.ActiveCrop == EstateFixture.Crop, SoilText(soil));
                Check("late guest has the host's tiles and box", EstateFixture.HasTile(TilemapLayer.Fences, FencePos) && EstateFixture.HasTile(TilemapLayer.Farm, SoilPos) && EstateFixture.HasObject(EstateFixture.BoxID, BoxPos));
                yield return Barrier("arrived", 60f);
                yield return WaitFor(() => !EstateFixture.HasObject(EstateFixture.BoxID, BoxPos), 8f);
                Check("late guest sees the host's later removal", !EstateFixture.HasObject(EstateFixture.BoxID, BoxPos));
                yield return WaitFor(() => soil != null && soil.canHarvest, 40f);
                Check("the crop grows ready at the late guest's too", soil != null && soil.canHarvest, SoilText(soil));
                yield return Barrier("checked", 60f);
            }
            yield return Finish("late guest got the estate");
        }

        // Town, fresh worlds, the host keeps playing after the demo (--mp-keep-playing): Gumu's intro ends without the
        // "thank you" screen and nobody stays frozen; the estate flags reach the guest (--mp-estate).
        [Scenario("demoend", Timeout = 220, Scene = "Town", Switches = "estate keep-playing", Isolate = "demoend")]
        private static IEnumerator DemoEndTest()
        {
            yield return WaitInGame();
            var s = Session.Current;
            yield return BothInScene("Town");
            if (!s.IsHost) Check("guest has the host's rules", EstateRules.KeepPlaying && EstateRules.Unlocked);
            var gumu = Object.FindObjectsByType<CutsceneGumu>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            Check("Gumu's intro is here", gumu != null);
            yield return WaitFor(() => GameData.gameBools.IsTrue(BoolID.GumuIntroductionDone) && (gumu == null || gumu.agent == null || gumu.agent.Graph == null || !gumu.agent.Graph.IsRunning), 60f);
            Check("Gumu's intro is over", GameData.gameBools.IsTrue(BoolID.GumuIntroductionDone));
            yield return new WaitForSecondsRealtime(6f);   // the demo's end comes a moment after
            var me = Game.LocalPlayer;
            var screens = Object.FindObjectsByType<TimedTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None).Where(EstateRules.IsDemoEnd)
                .SelectMany(t => Enumerable.Range(0, t.OnTriggerEvent.GetPersistentEventCount()).Select(i => t.OnTriggerEvent.GetPersistentTarget(i)).OfType<GameObject>()).ToList();
            Check("found the demo's end screen", screens.Count > 0);
            Check("no 'thank you for playing' screen", screens.All(g => !g.activeInHierarchy), string.Join(",", screens.Select(g => $"{g.name}={g.activeInHierarchy}")));
            Check("player not frozen", me.staticOwners.Count == 0 && !me.Static, string.Join(",", me.staticOwners.Select(o => o.ToString())));
            Check("the demo's end was skipped", EstateRules.EndTriggersSkipped + EstateRules.EndScreensClosed + EstateRules.LocksLifted + EstateRules.DemoLocksSkipped > 0,
                $"triggers {EstateRules.EndTriggersSkipped} screens {EstateRules.EndScreensClosed} lifted {EstateRules.LocksLifted} locks {EstateRules.DemoLocksSkipped}");
            Check("no demo lock let through", EstateRules.DemoLocksKept == 0);
            yield return WaitFor(() => EstateRules.Flags.All(f => GameData.gameBools.IsTrue(f)), 10f);
            Check("estate flags in the world", EstateRules.Flags.All(f => GameData.gameBools.IsTrue(f)));
            yield return Barrier("done");
            yield return Finish("kept playing after the demo");
        }
    }
}
