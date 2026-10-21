using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using EmbervilleMP.Sync.Objects;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Conversations must stay open for the NPC lock check (run-pair always passes --mp-skip-dialogue).
        private static void KeepDialogues()
        {
            var f = typeof(Args).GetField("values", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            (f?.GetValue(null) as Dictionary<string, string>)?.Remove("skip-dialogue");
        }

        // Everything of that item we carry, belt included (GetTotalAmount skips the belt, where food lands).
        private static int Owned(ItemData d) =>
            Singleton<Inventory>.main.allItems.Where(i => i.itemObject.data.IsSame(d) && i.section != InventorySection.Stock && i.section != InventorySection.Buyback).Sum(i => i.itemObject.amount);

        private static int StockOf(StoreComponent s, string item) =>
            StoreSync.Stock(s).Where(i => i.itemObject.data.name == item).Sum(i => i.itemObject.amount);

        private static IEnumerator BothInScene(string scene)
        {
            var s = Session.Current;
            yield return WaitFor(() => s != null && s.Ready && s.Players.Count >= 2, 90f);
            yield return WaitFor(() => Game.InGame && Game.SceneName == scene && (s.IsHost || GuestWorld.Active) && !GuestWorld.Switching, 90f);
            yield return Barrier("ready", 120f);
        }

        // Town: Gumu's shop (one stock, the guest's purchase comes out of the host's stock and the other way round),
        // a repairable house (the guest repairs and pays, the host sees it repaired), and an NPC conversation lock.
        [Scenario("shops", Timeout = 220, Scene = "Town", Isolate = "shops")]
        private static IEnumerator ShopsTest()
        {
            HookPeerData();
            KeepDialogues();
            // Gumu's intro ends the demo (DemoLock): mark it done before the town starts.
            yield return WaitFor(() => GameData.HasWorldData, 90f);
            GameData.gameBools[BoolID.GumuIntroductionDone] = true;
            yield return WaitInGame();
            var s = Session.Current;
            yield return BothInScene("Town");
            GameData.gameBools[BoolID.GumuIntroductionDone] = true;
            GameData.gameBools[BoolID.RepairablesActive] = true;
            var inv = Singleton<Inventory>.main;
            var me = Game.LocalPlayer;
            inv.AddGold(1000);
            int goldStart = inv.TotalGold.CurrentValue;

            // ---- shop ----
            var store = Object.FindObjectsByType<StoreComponent>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(x => NetIds.ScenePath(x.transform)).FirstOrDefault();
            if (!Check("found a store", store != null)) { yield return Finish(""); yield break; }
            yield return WaitFor(() => StoreSync.Collection(store) != null && StoreSync.Instance.HasRunnerStock(store), 20f);
            Check(s.IsHost ? "host announced its stock" : "guest got the runner's stock", StoreSync.Instance.HasRunnerStock(store),
                $"announced {StoreSync.Instance.StocksAnnounced} applied {StoreSync.Instance.StocksApplied}");
            yield return new WaitForSecondsRealtime(1f);
            string stock0 = StoreSync.Describe(store);
            Plugin.Log.LogInfo($"[test] stock here: {stock0}");
            Chat.Send("#stock0=" + stock0);
            yield return WaitFor(() => peerData.ContainsKey("stock0"), 10f);
            Check("both see the same stock", peerData.TryGetValue("stock0", out var theirs) && theirs == stock0, $"mine {stock0} / theirs {theirs}");
            string item = StoreSync.Stock(store).OrderBy(i => i.slotIndex).Select(i => i.itemObject.data.name).FirstOrDefault();
            if (!Check("the store has something to sell", item != null)) { yield return Finish(""); yield break; }
            int before = StockOf(store, item);
            yield return Barrier("stocks");

            if (!s.IsHost)
            {
                store.OpenStore();
                yield return WaitFor(() => store.isOpened, 5f);
                Check("guest opened the store", store.isOpened);
                var stockItem = StoreSync.Stock(store).First(i => i.itemObject.data.name == item);
                var data = stockItem.itemObject.data;
                int have = Owned(data), gold = inv.TotalGold.CurrentValue;
                Singleton<StoreManager>.main.TradeItem(stockItem, InventorySection.Stock, false);
                yield return WaitFor(() => Owned(data) > have, 6f);
                Check("guest got the item it bought", Owned(data) == have + 1, $"{have} -> {Owned(data)}");
                Check("guest paid for it", inv.TotalGold.CurrentValue < gold, $"{gold} -> {inv.TotalGold.CurrentValue}");
                Check("guest's stock went down too", StockOf(store, item) == before - 1, $"{before} -> {StockOf(store, item)}");
                store.CloseStore();
            }
            yield return Barrier("bought");
            if (s.IsHost)
            {
                yield return WaitFor(() => StockOf(store, item) == before - 1, 6f);
                Check("the guest's purchase left the host's stock", StockOf(store, item) == before - 1, $"{item}: {before} -> {StockOf(store, item)}");
                Check("host's gold untouched", inv.TotalGold.CurrentValue == goldStart, $"{goldStart} -> {inv.TotalGold.CurrentValue}");
            }
            // The host (runner) buys one of what's left (the first item, on both copies alike).
            string item2 = StoreSync.Stock(store).OrderBy(i => i.slotIndex).Select(i => i.itemObject.data.name).FirstOrDefault();
            int before2 = item2 != null ? StockOf(store, item2) : 0;
            if (s.IsHost && item2 != null)
            {
                store.OpenStore();
                yield return WaitFor(() => store.isOpened, 5f);
                var stockItem = StoreSync.Stock(store).First(i => i.itemObject.data.name == item2);
                Singleton<StoreManager>.main.TradeItem(stockItem, InventorySection.Stock, false);
                yield return null;
                store.CloseStore();
                Check("host bought one", StockOf(store, item2) == before2 - 1, $"{StockOf(store, item2)}");
            }
            yield return Barrier("hostbought");
            if (!s.IsHost && item2 != null)
            {
                yield return WaitFor(() => StockOf(store, item2) == before2 - 1, 6f);
                Check("the host's purchase left the guest's stock", StockOf(store, item2) == before2 - 1, $"{item2}: {before2} -> {StockOf(store, item2)}");
            }

            // ---- repairable ----
            var rb = Object.FindObjectsByType<RepairableBuilding>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(x => x.name == "Repairable_Cartographer");
            if (Check("found the cartographer's ruin", rb != null))
            {
                Check("not repaired yet", !rb.isRepaired);
                if (!s.IsHost)
                {
                    var costs = rb.GetItemCosts;
                    var had = costs.ToDictionary(c => c, c => inv.GetTotalAmount(c.itemData, rb.sharedChest));
                    foreach (var c in costs) inv.AddItem(c.itemData.CreateItem(c.Amount));
                    // Walk into its trigger (shows the repair window), then press the window's button.
                    var trig = Object.FindObjectsByType<ChildTriggerHandler>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                        .FirstOrDefault(t => t.receiver == rb.transform);
                    var col = trig != null ? trig.GetComponent<Collider2D>() : null;
                    Vector2 at = col != null ? (Vector2)col.bounds.center : (Vector2)rb.transform.position;
                    yield return MoveTo(me, at, 0.5f);
                    yield return new WaitForSecondsRealtime(1f);
                    Check("guest has the resources", rb.CheckResources());
                    rb.Repair();
                    yield return WaitFor(() => rb.isRepaired, 3f);
                    Check("guest repaired it", rb.isRepaired);
                    yield return WaitFor(() => costs.All(c => inv.GetTotalAmount(c.itemData, rb.sharedChest) <= had[c]), 6f);
                    Check("guest paid the resources", costs.All(c => inv.GetTotalAmount(c.itemData, rb.sharedChest) <= had[c]),
                        string.Join(",", costs.Select(c => $"{c.itemData.name} {had[c]}->{inv.GetTotalAmount(c.itemData, rb.sharedChest)}")));
                    yield return MoveTo(me, at + Vector2.down * 6f, 0.5f);
                }
                yield return Barrier("repaired");
                if (s.IsHost)
                {
                    yield return WaitFor(() => rb.isRepaired && rb.artRepaired.activeSelf, 8f);
                    Check("the guest's repair shows on the host", rb.isRepaired && rb.artRepaired.activeSelf && !rb.artDestroyed.activeSelf);
                }
            }

            // ---- NPC: talking works on the guest (puppet NPC), and locks the NPC for the other player ----
            var npc = Object.FindObjectsByType<DialogueTrigger>(FindObjectsSortMode.None)
                .Where(d => d.GetComponent<Enemy>() != null).OrderBy(d => NetIds.ScenePath(d.transform)).FirstOrDefault();
            if (Check("found an NPC to talk to", npc != null))
            {
                Plugin.Log.LogInfo($"[test] npc {npc.name}, puppet here: {EnemySync.Instance.IsPuppet(npc.GetComponent<Enemy>())}");
                yield return Barrier("npc");
                if (!s.IsHost)
                {
                    npc.OnInteract(me.gameObject);
                    yield return null;
                    Check("guest talks to the NPC", npc.isOpened && Singleton<DialogueManager>.main.IsPlaying);
                }
                yield return Barrier("talking");
                if (s.IsHost)
                {
                    yield return WaitFor(() => NpcTalkSync.Instance.BusyWith(npc) != 0, 5f);
                    Check("host sees the NPC busy", NpcTalkSync.Instance.BusyWith(npc) != 0 && !npc.CanInteract());
                    npc.OnInteract(me.gameObject);
                    yield return null;
                    Check("host can't start a conversation meanwhile", !npc.isOpened);
                }
                yield return Barrier("blocked");
                if (!s.IsHost)
                {
                    Singleton<DialogueManager>.main.EndDialogue();
                    yield return WaitFor(() => !npc.isOpened, 5f);
                    Check("guest's conversation ended", !npc.isOpened);
                }
                yield return Barrier("ended");
                if (s.IsHost)
                {
                    yield return WaitFor(() => NpcTalkSync.Instance.BusyWith(npc) == 0, 5f);
                    Check("NPC free again on the host", NpcTalkSync.Instance.BusyWith(npc) == 0 && npc.CanInteract());
                }
            }
            var ss = StoreSync.Instance;
            Plugin.Log.LogInfo($"[store] purchases {ss.Purchases} granted {ss.PurchasesGranted} denied {ss.PurchasesDenied} stocks {ss.StocksAnnounced}/{ss.StocksApplied}");
            yield return Barrier("done");
            yield return Finish("shop, repairs and NPC lock in sync");
        }

        // Tutorial area: a crafting station (the guest queues, the host's timer finishes it, the guest collects it)
        // and a saved story object (the boss corpse) changed on one machine reaching the other.
        [Scenario("stations", Timeout = 200)]
        private static IEnumerator StationsTest()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            yield return BothInScene("TutorialArea");
            var inv = Singleton<Inventory>.main;
            var me = Game.LocalPlayer;

            // ---- saved story object ----
            var corpse = Object.FindObjectsByType<GameObjectStateLoader>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(g => NetIds.ScenePath(g.transform)).FirstOrDefault();
            if (Check("found a saved story object", corpse != null))
            {
                Vector3 home = corpse.transform.position, moved = home + new Vector3(1.5f, 0f, 0f);
                bool wasActive = corpse.gameObject.activeSelf;
                Plugin.Log.LogInfo($"[test] saved object {NetIds.ScenePath(corpse.transform)} active={wasActive}");
                yield return new WaitForSecondsRealtime(1.5f);   // the watcher starts a second after the scene
                if (!s.IsHost)
                {
                    // As a guest-side cutscene or dialogue event would: show it somewhere else.
                    corpse.transform.position = moved;
                    corpse.gameObject.SetActive(!wasActive);
                }
                yield return Barrier("guest-changed");
                if (s.IsHost)
                {
                    yield return WaitFor(() => corpse.gameObject.activeSelf != wasActive && (corpse.transform.position - moved).sqrMagnitude < 0.01f, 5f);
                    Check("the guest's change to a saved object reached the host", corpse.gameObject.activeSelf != wasActive && (corpse.transform.position - moved).sqrMagnitude < 0.01f,
                        $"active={corpse.gameObject.activeSelf} pos={corpse.transform.position}");
                    // And back, from the runner.
                    corpse.transform.position = home;
                    corpse.gameObject.SetActive(wasActive);
                }
                yield return Barrier("host-changed");
                if (!s.IsHost)
                {
                    yield return WaitFor(() => corpse.gameObject.activeSelf == wasActive && (corpse.transform.position - home).sqrMagnitude < 0.01f, 5f);
                    Check("the host's change to a saved object reached the guest", corpse.gameObject.activeSelf == wasActive && (corpse.transform.position - home).sqrMagnitude < 0.01f,
                        $"active={corpse.gameObject.activeSelf} pos={corpse.transform.position}");
                }
            }

            // ---- crafting station ----
            const string room = "004_Gather";
            var station = Object.FindObjectsByType<CraftingStation>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x.GetComponentInParent<GameArea>(true)?.name == room).OrderBy(x => NetIds.ScenePath(x.transform)).FirstOrDefault();
            if (!Check("found a crafting station", station != null)) { yield return Finish(""); yield break; }
            yield return TeleportTo(room, (Vector2)station.transform.position + (s.IsHost ? Vector2.down * 2f : Vector2.down * 2f + Vector2.right));
            // The room's stations only come out later in the story; switch it on as that would (both copies alike).
            for (var t = station.transform; t != null && t.GetComponent<GameArea>() == null; t = t.parent)
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            station.enabled = true;   // switched on by a scene event later in the story
            yield return WaitFor(() => station.isActiveAndEnabled, 5f);
            Check("station is out", station.isActiveAndEnabled, $"enabled={station.enabled} active={station.gameObject.activeInHierarchy}");
            yield return Barrier("at-station", 40f);
            var recipe = station.availableRecipes.First();
            var result = recipe.Result.itemData;
            int resultBefore = Owned(result);
            if (!s.IsHost)
            {
                foreach (var ing in recipe.Ingredients)
                {
                    int need = recipe.GetActualItemCost(ing, recipe.RequiredCategory);
                    int left = inv.AddItem(ing.itemData.CreateItem(need));
                    // Food lands on the belt, which recipes don't count: put it in the bag.
                    foreach (var b in inv.allItems.Where(x => x.section == InventorySection.Belt && x.itemObject.data.IsSame(ing.itemData)).ToList())
                    {
                        b.slotIndex = inv.GetNextFreeIndex(inv.allItems, InventorySection.Main);
                        b.section = InventorySection.Main;
                    }
                    Plugin.Log.LogInfo($"[test] ingredient {ing.itemData.name} need {need} (x{ing.Amount}) left {left} have {inv.GetTotalAmount(ing.itemData, ChestShare.CraftingStations)} items {inv.allItems.Count}");
                }
                var had = recipe.Ingredients.ToDictionary(i => i, i => inv.GetTotalAmount(i.itemData, ChestShare.CraftingStations));
                Check("guest has the ingredients", recipe.HasIngredients);
                Check("guest queued a recipe", station.TryToEnqueue(recipe));
                Check("guest paid the ingredients", recipe.Ingredients.All(i => inv.GetTotalAmount(i.itemData, ChestShare.CraftingStations) < had[i]));
            }
            yield return Barrier("queued");
            if (s.IsHost)
            {
                yield return WaitFor(() => station.Demand.Count == 1, 5f);
                Check("the guest's recipe is queued on the host", station.Demand.Count == 1, $"{station.Demand.Count}");
                if (station.Demand.Count > 0) station.Demand.Peek().timer = 1.5f;   // don't wait the real crafting time
                yield return WaitFor(() => station.CreatedItems.Count == 1, 8f);
                Plugin.Log.LogInfo($"[test] station active={station.isActiveAndEnabled} on={station.isOn.Value} timer={station.producing?.timer} queue={station.Demand.Count} made={station.CreatedItems.Count} timeScale={Time.timeScale} runs={ObjectSync.RunsScene}");
                Check("the host's timer finished it", station.CreatedItems.Count == 1 && station.Demand.Count == 0);
            }
            yield return Barrier("crafted");
            if (!s.IsHost)
            {
                yield return WaitFor(() => station.CreatedItems.Count == 1 && station.Demand.Count == 0, 6f);
                Check("the result shows on the guest", station.CreatedItems.Count == 1 && station.Demand.Count == 0, $"{station.CreatedItems.Count} made, {station.Demand.Count} queued");
                Check("guest got the crafting XP", CraftingSync.Instance.XpGiven == 1, $"{CraftingSync.Instance.XpGiven}");
                station.OnInteract(me.gameObject);   // E on a station with results collects them
                yield return WaitFor(() => Owned(result) > resultBefore, 6f);
                Check("guest collected the result", Owned(result) > resultBefore, $"{result.name} {resultBefore} -> {Owned(result)}");
                Check("station empty on the guest", station.CreatedItems.Count == 0);
            }
            yield return Barrier("collected");
            if (s.IsHost)
            {
                yield return WaitFor(() => station.CreatedItems.Count == 0, 5f);
                Check("station empty on the host", station.CreatedItems.Count == 0);
                Check("the host didn't get the guest's item", Owned(result) == resultBefore);
            }
            var cs = CraftingSync.Instance;
            Plugin.Log.LogInfo($"[craft] enqueue intents {cs.EnqueueIntents} remote {cs.RemoteEnqueued} collects {cs.CollectIntents} granted {cs.Granted} xp {cs.XpGiven}; saved objects sent {SavedObjectSync.Instance.ChangesSent} applied {SavedObjectSync.Instance.ChangesApplied}");
            yield return Barrier("done");
            yield return Finish("crafting station and saved objects in sync");
        }
    }
}
