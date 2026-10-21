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
        // Loot (chests, drops, gathering) in both loot modes. The mode is the host's: --mp-loot=shared on the host.

        private static IEnumerator LootReady()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            yield return WaitFor(() => s != null && s.Ready && s.Players.Count >= 2, 90f);
            yield return WaitFor(() => Game.InGame && (s.IsHost || GuestWorld.Active) && !GuestWorld.Switching, 90f);
            yield return Barrier("ready", 120f);
            bool wantShared = (Args.Get("loot") ?? "").StartsWith("s");
            Check($"loot mode is {(wantShared ? "shared" : "instanced")}", LootRules.Mode == (wantShared ? LootMode.Shared : LootMode.Instanced), LootRules.Mode.ToString());
        }

        // The same chest on every copy: the first one in the starting room (by scene path).
        private static Chest PickChest()
        {
            return Object.FindObjectsByType<Chest>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(c => c.gameObject.activeInHierarchy && !StashSync.IsStorage(c) && c.GetComponentInParent<GameArea>(true)?.name == "001_Barrack")
                .OrderBy(c => NetIds.ScenePath(c.transform)).FirstOrDefault();
        }

        // Everything our player carries (items outside the chest section, and gold).
        private static int Carried() =>
            Singleton<Inventory>.main.allItems.Where(i => i.section != InventorySection.Chest).Sum(i => i.itemObject.amount) + Singleton<Inventory>.main.TotalGold.CurrentValue;

        private static string Keys(IEnumerable<InventoryItem> items) =>
            string.Join(",", items.Select(i => i.itemObject.data.addressKey + "x" + i.itemObject.amount).OrderBy(x => x));

        // The guest takes everything from a chest: open, collect all, close (the game's own inventory calls).
        // holdUntilSeen: keep it open until the host saw the chest emptied (shared loot: takes count as they happen).
        private static IEnumerator LootChestAsGuest(Chest c, bool holdUntilSeen = false)
        {
            var me = Game.LocalPlayer;
            int before = Carried();
            c.OnInteract(me.gameObject);
            yield return WaitFor(() => c.isOpened, 6f);
            Check("guest opened the chest", c.isOpened);
            Chat.Send("#guestcontent=" + Keys(ChestSync.Items(c)));
            yield return new WaitForSecondsRealtime(0.5f);
            Singleton<Inventory>.main.CollectAllFromChest();
            yield return new WaitForSecondsRealtime(0.3f);
            if (holdUntilSeen)
            {
                Chat.Send("#taken=1");
                yield return WaitFor(() => peerData.ContainsKey("seenempty"), 10f);
                Check("the chest stayed open on the guest until the host saw it emptied", c.isOpened && peerData.ContainsKey("seenempty"));
            }
            c.OnInteract(me.gameObject);
            yield return WaitFor(() => !c.isOpened, 3f);
            Check("guest closed the chest", !c.isOpened);
            Check("the chest is empty for the guest", ChestSync.Items(c).Count == 0, ChestSync.Items(c).Count + " left");
            Check("the guest got the chest's items", Carried() > before, $"{before} -> {Carried()}");
        }

        // Instanced loot: the guest loots a chest; the host's chest keeps its own loot, and the guest's stays empty
        // (also when its contents are set up again, as after rejoining).
        [Scenario("lootchest", Timeout = 200, Quick = true)]
        private static IEnumerator LootChestTest()
        {
            yield return LootReady();
            var s = Session.Current;
            var c = PickChest();
            if (!Check("found a chest in the first room", c != null)) { yield return Finish(""); yield break; }
            yield return WaitFor(() => ChestSync.Ready(c), 15f);
            int hostCount = ChestSync.Items(c).Count;
            Plugin.Log.LogInfo($"[test] chest {c.name} id '{c.ID}' items {hostCount}: {Keys(ChestSync.Items(c))}");
            Check("the chest has loot here", hostCount > 0);
            yield return Barrier("picked");

            if (!s.IsHost)
            {
                yield return LootChestAsGuest(c);
                Check("the host keeps what the guest left", LootRules.Instance.TryGetRecord(c, out _));
                // Contents set up again (scene reload / rejoin): still looted, no new roll.
                Singleton<ChestManager>.main.RemoveChest(c.ID);
                c.CreateChestItems();
                Check("no refill when the chest is set up again", ChestSync.Items(c).Count == 0 && c.alreadyOpened, ChestSync.Items(c).Count + " items");
            }
            yield return Barrier("guest-looted", 60f);
            if (s.IsHost)
            {
                Check("the host's chest still has its loot", ChestSync.Items(c).Count == hostCount, $"{ChestSync.Items(c).Count}/{hostCount}");
                Check("the host's chest wasn't opened", !c.alreadyOpened && !c.isOpened);
                string guest = s.Players.Values.First(p => p.Id != s.LocalId).Name;
                Check("the host recorded the guest's chest", LootRules.Instance.RecordCount(guest) > 0, $"{guest}: {LootRules.Instance.RecordCount(guest)}");
                LootRules.Instance.SaveRecords();
                Check("chest records saved next to the host's world", System.IO.File.Exists(LootRules.RecordsPath), LootRules.RecordsPath);
            }
            yield return Barrier("done");
            yield return Finish("instanced chests per player");
        }

        // Shared loot: the host (runner) owns the chest. The guest opens it (one player at a time), sees the host's
        // contents, takes everything; the host sees it emptied.
        [Scenario("lootchest-shared", Timeout = 200, Switches = "loot=shared")]
        private static IEnumerator LootChestSharedTest()
        {
            yield return LootReady();
            var s = Session.Current;
            var c = PickChest();
            if (!Check("found a chest in the first room", c != null)) { yield return Finish(""); yield break; }
            yield return WaitFor(() => ChestSync.Ready(c), 15f);
            string hostKeys = Keys(ChestSync.Items(c));
            yield return Barrier("picked");

            if (!s.IsHost) yield return LootChestAsGuest(c, holdUntilSeen: true);
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("guestcontent"), 15f);
                Check("the guest saw the host's chest contents", peerData.TryGetValue("guestcontent", out var gk) && gk == hostKeys, $"host {hostKeys} guest {gk}");
                byte guestId = s.Players.Keys.First(id => id != s.LocalId);
                yield return WaitFor(() => ChestSync.Instance.HolderOf(c) == guestId || c.alreadyOpened, 5f);
                Check("the chest is held by the guest (or already closed)", ChestSync.Instance.HolderOf(c) == guestId || c.alreadyOpened);
                // What the guest takes reaches us while it still has the chest open (a holder that drops out now
                // must not leave us with items it already has).
                yield return WaitFor(() => peerData.ContainsKey("taken"), 15f);
                yield return WaitFor(() => ChestSync.Items(c).Count == 0, 5f);
                Check("the guest's takes reached the runner while it still had the chest open",
                    ChestSync.Items(c).Count == 0 && ChestSync.Instance.HolderOf(c) == guestId, $"{Keys(ChestSync.Items(c))}, holder #{ChestSync.Instance.HolderOf(c)}");
                Chat.Send("#seenempty=1");
            }
            yield return Barrier("guest-looted", 60f);
            if (s.IsHost)
            {
                yield return WaitFor(() => ChestSync.Items(c).Count == 0 && ChestSync.Instance.HolderOf(c) == 0, 5f);
                Check("the host's chest was emptied by the guest", ChestSync.Items(c).Count == 0, Keys(ChestSync.Items(c)));
                Check("the chest is free again", ChestSync.Instance.HolderOf(c) == 0);
                Check("the chest shows opened", c.alreadyOpened && c.artOpened.activeSelf);
                // The host can open it now (empty).
                c.OnInteract(Game.LocalPlayer.gameObject);
                yield return WaitFor(() => c.isOpened, 3f);
                Check("host opens the chest", c.isOpened);
                Chat.Send("#hostopen=1");
                yield return new WaitForSecondsRealtime(1f);
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("hostopen"), 20f);
                yield return WaitFor(() => ChestSync.Instance.HolderOf(c) == Protocol.HostId, 5f);
                // Now the host has it: our request is turned down.
                c.OnInteract(Game.LocalPlayer.gameObject);
                yield return new WaitForSecondsRealtime(1.5f);
                Check("guest can't open a chest the host has open", !c.isOpened);
            }
            yield return Barrier("host-open");
            if (s.IsHost && c.isOpened) c.OnInteract(Game.LocalPlayer.gameObject);
            yield return Barrier("done");
            var cs = ChestSync.Instance;
            Plugin.Log.LogInfo($"[loot] chest grants {cs.Grants} denials {cs.Denials}");
            yield return Finish("shared chest in sync");
        }

        // How many of an item our player carries (main, belt, key items).
        private static int AmountOf(ItemData data) =>
            Singleton<Inventory>.main.allItems.Where(i => i.section != InventorySection.Chest && i.itemObject.data.IsSame(data)).Sum(i => i.itemObject.amount);

        private static ItemData TestItem() =>
            Object.FindObjectsByType<GatheringObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(g => g.itemToDrop != null).OrderBy(g => NetIds.ScenePath(g.transform)).First().itemToDrop;

        private static int NewestDrop(HashSet<int> before) =>
            WorldDrops.Instance.Live.Select(kv => kv.Key).Where(id => !before.Contains(id)).DefaultIfEmpty(0).First();

        // Shared loot: drops are the runner's; a pickup goes to exactly one player and vanishes for everyone.
        [Scenario("lootpickup", Timeout = 200, Switches = "loot=shared")]
        private static IEnumerator LootPickupTest()
        {
            yield return LootReady();
            var s = Session.Current;
            var wd = WorldDrops.Instance;
            var data = TestItem();
            var me = Game.LocalPlayer;
            Plugin.Log.LogInfo($"[test] drop item {data.name} ({data.addressKey})");

            // 1. The host drops an item; the guest picks it up.
            var known = new HashSet<int>(wd.Live.Select(kv => kv.Key));
            if (s.IsHost)
            {
                data.CreateItem(1).DropToWorld(me.transform.position, 1, 0.8f);
                yield return WaitFor(() => NewestDrop(known) != 0, 3f);
                Chat.Send("#drop1=" + NewestDrop(known));
            }
            yield return WaitFor(() => peerData.ContainsKey("drop1") || (s.IsHost && NewestDrop(known) != 0), 10f);
            int id1 = s.IsHost ? NewestDrop(known) : int.Parse(peerData.TryGetValue("drop1", out var v1) ? v1 : "0");
            ItemDropObject d1 = null;
            yield return WaitFor(() => (d1 = wd.Live.FirstOrDefault(kv => kv.Key == id1).Value) != null && d1.isReady, 8f);
            Check("the drop shows on this copy", d1 != null, "id " + id1);
            yield return Barrier("drop1");
            int had = AmountOf(data);
            if (!s.IsHost && d1 != null)
            {
                d1.OnInteract(me.gameObject);
                yield return WaitFor(() => AmountOf(data) > had, 5f);
                Check("guest picked the host's drop up", AmountOf(data) == had + 1, $"{had} -> {AmountOf(data)}");
            }
            yield return Barrier("picked1");
            if (s.IsHost)
            {
                yield return WaitFor(() => d1 == null || !d1.gameObject.activeSelf, 5f);
                Check("the drop vanished for the host", d1 == null || !d1.gameObject.activeSelf);
                Check("the host didn't get it", AmountOf(data) == had);
            }

            // 2. The guest drops an item: the host spawns it for everyone.
            known = new HashSet<int>(wd.Live.Select(kv => kv.Key));
            yield return Barrier("checked1");
            if (!s.IsHost) data.CreateItem(1).DropToWorld(me.transform.position, 1, 0.8f);
            yield return WaitFor(() => NewestDrop(known) != 0, 8f);
            int id2 = NewestDrop(known);
            Check("the guest's drop shows (made by the runner)", id2 != 0 && (id2 >> 24) == Protocol.HostId, $"id {id2:X}");
            ItemDropObject d2 = null;
            yield return WaitFor(() => (d2 = wd.Live.FirstOrDefault(kv => kv.Key == id2).Value) != null && d2.isReady, 5f);
            yield return Barrier("drop2");

            // 3. Both grab it at once: exactly one gets it.
            had = AmountOf(data);
            if (d2 != null) d2.OnInteract(me.gameObject);
            yield return new WaitForSecondsRealtime(3f);
            int got = AmountOf(data) - had;
            Check("the drop is gone here", d2 == null || !d2.gameObject.activeSelf);
            Chat.Send("#got2=" + got);
            yield return WaitFor(() => peerData.ContainsKey("got2"), 10f);
            int theirs = int.Parse(peerData.TryGetValue("got2", out var g2) ? g2 : "-1");
            Check("exactly one player got the contested drop", got + theirs == 1 && got >= 0 && theirs >= 0, $"me {got}, other {theirs}");
            Plugin.Log.LogInfo($"[loot] drops spawned {wd.Spawned} shown {wd.Shown} requests {wd.Requests}/{wd.RequestsServed} granted {wd.Granted} vanished {wd.Vanished}");
            yield return Barrier("done");
            yield return Finish("shared drops picked up once");
        }

        // A gathering node hit by the guest: the host sees it damaged, then depleted. The drops follow the loot mode;
        // the job XP is the guest's.
        [Scenario("lootgather", Timeout = 200)]
        [Scenario("lootgather-shared", Timeout = 200, Switches = "loot=shared")]
        private static IEnumerator LootGatherTest()
        {
            yield return LootReady();
            var s = Session.Current;
            const string room = "004_Gather";
            var node = Object.FindObjectsByType<GatheringObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(g => g.gatherType == GatheringObject.GatherType.Mine && g.hitsToFinish == 3 && g.GetComponentInParent<GameArea>(true)?.name == room)
                .OrderBy(g => NetIds.ScenePath(g.transform)).FirstOrDefault();
            if (!Check("found a 3-hit rock", node != null)) { yield return Finish(""); yield break; }
            Vector2 at = node.transform.position;
            yield return TeleportTo(room, at + Vector2.down * (s.IsHost ? 3f : 1.5f));
            yield return WaitFor(() => node.isInitialized, 5f);
            Check("the rock starts whole", node.currentHealth == 3 && node.isActive, $"hp {node.currentHealth}");
            int xp = GameData.playerData.GetTotalXP(LifeSkillJob.Mining);
            int DropsNear() => Object.FindObjectsByType<ItemDropObject>(FindObjectsSortMode.None)
                .Count(d => d.gameObject.activeInHierarchy && d.itemObj?.data == node.itemToDrop && Vector2.Distance(d.transform.position, at) < 6f);
            int dropsBefore = DropsNear();
            yield return Barrier("at-rock", 60f);

            var me = Game.LocalPlayer;
            if (!s.IsHost)
            {
                if (node.player == null) node.player = me;
                node.OnHitCallback();
                Check("guest's hit counted here", node.currentHealth == 2);
            }
            yield return Barrier("hit1");
            if (s.IsHost)
            {
                yield return WaitFor(() => node.currentHealth == 2, 5f);
                Check("the host sees the rock damaged", node.currentHealth == 2 && node.isActive, $"hp {node.currentHealth}");
            }
            yield return Barrier("seen1");
            if (!s.IsHost)
            {
                yield return new WaitForSecondsRealtime(0.6f);
                node.OnHitCallback();
                yield return new WaitForSecondsRealtime(0.6f);
                node.OnHitCallback();
                Check("guest depleted the rock", node.currentHealth <= 0 && !node.isActive);
                Check("guest got the mining XP", GameData.playerData.GetTotalXP(LifeSkillJob.Mining) > xp, $"{xp} -> {GameData.playerData.GetTotalXP(LifeSkillJob.Mining)}");
            }
            yield return Barrier("depleted");
            yield return WaitFor(() => node.currentHealth <= 0, 5f);
            yield return new WaitForSecondsRealtime(1.5f);   // drops land
            int drops = DropsNear() - dropsBefore;
            if (s.IsHost)
            {
                Check("the host sees the rock depleted", node.currentHealth <= 0 && !node.isActive && node.artToHide.All(a => a == null || !a.activeSelf), $"hp {node.currentHealth} active {node.isActive}");
                Check("the host got no mining XP", GameData.playerData.GetTotalXP(LifeSkillJob.Mining) == xp);
                if (LootRules.Shared) Check("shared loot: the rock's drops lie here too", drops > 0, drops + " drops");
                else Check("instanced loot: no drops for the host", drops == 0, drops + " drops");
            }
            else Check("the guest sees its drops", drops > 0, drops + " drops");
            if (LootRules.Shared)
                Check("the drops are network drops", WorldDrops.Instance.Live.Count(kv => kv.Value.itemObj?.data == node.itemToDrop) >= drops);
            Chat.Send("#drops=" + drops);
            yield return WaitFor(() => peerData.ContainsKey("drops"), 10f);
            if (LootRules.Shared && s.IsHost) Check("both see the same drops", peerData["drops"] == drops.ToString(), $"host {drops} guest {peerData["drops"]}");
            var gs = GatheringSync.Instance;
            Plugin.Log.LogInfo($"[loot] gather remote hits {gs.RemoteHits} depleted {gs.RemoteDepleted}");
            yield return Barrier("done");
            yield return Finish("gathering node in sync");
        }

        // Lists the scene's loot sources: chests, gathering nodes, item droppers, breakables, placed drops.
        [Scenario("lootsurvey")]
        private static IEnumerator LootSurvey()
        {
            yield return WaitInGame();
            yield return new WaitForSecondsRealtime(3f);
            var sb = new System.Text.StringBuilder();
            string Area(Component c) => c.GetComponentInParent<GameArea>(true)?.name ?? "-";
            var cm = Singleton<ChestManager>.HasMain ? Singleton<ChestManager>.main : null;
            sb.AppendLine($"scene={Game.SceneName} chestManager={(cm != null ? cm.gameObject.scene.name : "none")} chests={cm?.allChests.Count}");
            if (cm != null)
                foreach (var kv in cm.allChests)
                    sb.AppendLine($"  managed chest '{kv.Key}' save={kv.Value.saveCollection} share={kv.Value.shareContent} items={kv.Value.mainItems.Count}");
            foreach (var c in Object.FindObjectsByType<Chest>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(x => NetIds.ScenePath(x.transform)))
                sb.AppendLine($"chest id='{c.ID}' save={c.saveChest} mirror={c.mirrorChest} fixed={c.fixedOpenedState} share={c.shareContent} storage={StashSync.IsStorage(c)} mand={c.mandatories.Count} table={(c.lootTable != null)} n={c.itemsToCreate} init={c.isInitialized} area={Area(c)} active={c.gameObject.activeInHierarchy} path={NetIds.ScenePath(c.transform)} pos={c.transform.position}");
            foreach (var g in Object.FindObjectsByType<GatheringObject>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(x => NetIds.ScenePath(x.transform)))
                sb.AppendLine($"gather {g.gatherType} hits={g.hitsToFinish} hp={g.currentHealth} tree={g.isTree} residualHits={g.hitsToRemoveTreeResidual} variants={g.variations?.Count} repeat={g.canRepeat} item={(g.itemToDrop != null ? g.itemToDrop.name : "-")} saveID='{g.saveID}' area={Area(g)} active={g.gameObject.activeInHierarchy} path={NetIds.ScenePath(g.transform)} pos={g.transform.position}");
            foreach (var d in Object.FindObjectsByType<ItemDropper>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(x => NetIds.ScenePath(x.transform)))
                sb.AppendLine($"dropper on={(d.GetComponentInParent<Enemy>(true) != null ? "enemy" : d.GetComponentInParent<GatheringObject>(true) != null ? "gather" : d.GetComponentInParent<DamageableObject>(true) != null ? "damageable" : "other")} table={(d.lootTable != null)} mand={d.mandatories.Count} p={d.dropProbability} area={Area(d)} path={NetIds.ScenePath(d.transform)}");
            foreach (var d in Object.FindObjectsByType<DamageableObject>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(x => NetIds.ScenePath(x.transform)))
                sb.AppendLine($"damageable hp={d.health} beh={d.damageBehaviour} area={Area(d)} path={NetIds.ScenePath(d.transform)}");
            foreach (var d in Object.FindObjectsByType<ItemDropObject>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(x => NetIds.ScenePath(x.transform)))
                sb.AppendLine($"itemdrop started={d.startedActive} data={(d.itemData != null ? d.itemData.name : "-")} active={d.gameObject.activeInHierarchy} area={Area(d)} path={NetIds.ScenePath(d.transform)}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, $"lootsurvey-{Game.SceneName}.txt"), sb.ToString());
            Report(true, "surveyed");
            DevHarness.Quit("done");
        }
    }
}
