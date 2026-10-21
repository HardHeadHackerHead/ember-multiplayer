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
        // The world's stash (StashSync) and key items (KeyItemSync): the host's, whatever the loot mode.

        private const string TestStashId = "mp_test_stash";

        private static IEnumerator CoopReady()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            yield return WaitFor(() => s != null && s.Ready && s.Players.Count >= 2, 90f);
            yield return WaitFor(() => Game.InGame && (s.IsHost || GuestWorld.Active) && !GuestWorld.Switching, 90f);
            yield return Barrier("ready", 120f);
        }

        // Every chest of the scene with the flags that decide what it is (the demo's storage chests live in other
        // scenes too: lootsurvey there lists them the same way).
        private static void LogChestFlags()
        {
            foreach (var c in Object.FindObjectsByType<Chest>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(x => NetIds.ScenePath(x.transform)))
                Plugin.Log.LogInfo($"[test] chest '{c.ID}' save={c.saveChest} mirror={c.mirrorChest} fixed={c.fixedOpenedState} share={c.shareContent} storage={StashSync.IsStorage(c)} active={c.gameObject.activeInHierarchy} {NetIds.ScenePath(c.transform)}");
        }

        // Two different stackable items from the scene's gathering nodes (the same on every copy).
        private static (ItemData a, ItemData b) TwoItems()
        {
            var items = Object.FindObjectsByType<GatheringObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(g => g.itemToDrop != null && g.itemToDrop.IsStackable).OrderBy(g => NetIds.ScenePath(g.transform))
                .Select(g => g.itemToDrop).GroupBy(d => d.addressKey).Select(g => g.First()).ToList();
            var a = items.FirstOrDefault() ?? TestItem();
            // (one kind of node here: another stackable material the scene has loaded)
            var b = items.Skip(1).FirstOrDefault() ?? Resources.FindObjectsOfTypeAll<ItemData>()
                .Where(d => d != null && d.IsStackable && d.itemCategory == a.itemCategory && d.itemBehaviour != ItemBehaviour.KeyItem
                            && !(d is CollectableCoin) && !string.IsNullOrEmpty(d.addressKey) && d.addressKey != a.addressKey)
                .OrderBy(d => d.addressKey).FirstOrDefault() ?? a;
            return (a, b);
        }

        // A storage chest built at runtime from a loot chest of the first room: same id, flags and first roll
        // (10 x seed) on every copy, shared with crafting stations (the estate's chests in Early Access are like it).
        private static Chest MakeStash(ItemData seed)
        {
            var template = PickChest();
            if (template == null) return null;
            var go = Object.Instantiate(template.gameObject, template.transform.parent);
            go.name = "MP_TestStash";
            go.transform.position = template.transform.position + Vector3.right * 1.5f;
            var c = go.GetComponent<Chest>();
            c.chestID = TestStashId;
            c.saveChest = true;
            c.fixedOpenedState = false;
            c.mirrorChest = false;
            c.shareContent = ChestShare.CraftingStations;
            c.mandatories = new List<ItemDataQuantified> { new ItemDataQuantified { itemData = seed, amount = 10 } };
            c.itemsToCreate = new Vector2Int(1, 1);
            c.setQuality = false;
            return c;
        }

        private static int CountIn(IEnumerable<InventoryItem> items, ItemData d) =>
            (items ?? Enumerable.Empty<InventoryItem>()).Where(i => i?.itemObject?.data != null && i.itemObject.data.IsSame(d)).Sum(i => i.itemObject.amount);

        // Moves one stack of an item between our bag and the open chest (the game's own inventory call); how many moved.
        private static int MoveStack(ItemData d, bool intoChest)
        {
            var inv = Singleton<Inventory>.main;
            var from = intoChest ? new[] { InventorySection.Main, InventorySection.Belt } : new[] { InventorySection.Chest };
            var st = inv.allItems.FirstOrDefault(i => from.Contains(i.section) && i.itemObject.data.IsSame(d));
            if (st == null) return 0;
            int before = CountIn(StashSync.LiveChest(), d);
            if (intoChest) inv.ChangeSection(st, st.section, InventorySection.Chest);
            else if (!inv.ChangeSection(st, InventorySection.Chest, InventorySection.Main)) inv.ChangeSection(st, InventorySection.Chest, InventorySection.Belt);
            inv.SetInventoryChanged();
            return intoChest ? CountIn(StashSync.LiveChest(), d) - before : before - CountIn(StashSync.LiveChest(), d);
        }

        // The stash as this copy's game has it (its ChestManager).
        private static List<InventoryItem> StashHere(Chest c) => ChestSync.Items(c);

        // A storage chest is one stash, the host's, in the default (instanced) loot mode too: the guest sees what the
        // host put in, its takes and deposits reach the host as they happen, and crafting from shared chests on
        // either side takes from the same stash. Fails without StashSync: an instanced guest rolls its own stash
        // (and the host never hears of what it moved).
        [Scenario("storagechest", Timeout = 220, Quick = true)]
        private static IEnumerator StorageChestTest()
        {
            yield return CoopReady();
            var s = Session.Current;
            var inv = Singleton<Inventory>.main;
            var me = Game.LocalPlayer;
            LogChestFlags();
            var (itemA, itemB) = TwoItems();
            Plugin.Log.LogInfo($"[test] stash items {itemA.addressKey}, {itemB.addressKey}");
            var c = MakeStash(itemA);
            if (!Check("made a storage chest", c != null && StashSync.IsStorage(c))) { yield return Finish(""); yield break; }
            yield return WaitFor(() => ChestSync.Ready(c) && CountIn(StashHere(c), itemA) >= 10, 15f);
            Check("the stash is set up here", CountIn(StashHere(c), itemA) >= 10, Keys(StashHere(c)));
            yield return Barrier("stash-made");

            // 1. The host puts something in (its own open / move / close).
            if (s.IsHost)
            {
                inv.AddItem(new ItemObject(itemB, 3));
                c.OnInteract(me.gameObject);
                yield return WaitFor(() => c.isOpened, 3f);
                Check("host opened the stash", c.isOpened);
                int put = MoveStack(itemB, true);
                yield return null;
                c.OnInteract(me.gameObject);
                yield return WaitFor(() => !c.isOpened, 3f);
                Check("host put items into the stash", put > 0 && CountIn(StashHere(c), itemB) == put, $"moved {put}, stash {Keys(StashHere(c))}");
                Chat.Send("#hoststash=" + Keys(StashHere(c)));
            }
            yield return Barrier("host-deposited");

            // 2. The guest opens it: the host's contents. It takes the host's deposit and puts some of its own in,
            //    and keeps it open until the host saw both.
            byte guestId = s.IsHost ? s.Players.Keys.First(id => id != s.LocalId) : s.LocalId;
            int baseA = 10;
            if (!s.IsHost)
            {
                int ownB = Owned(itemB);
                c.OnInteract(me.gameObject);
                yield return WaitFor(() => c.isOpened, 6f);
                Check("guest opened the stash", c.isOpened);
                string hostKeys = peerData.TryGetValue("hoststash", out var hk) ? hk : "?";
                Check("the guest sees the host's stash (not a roll of its own)", Keys(StashSync.LiveChest()) == hostKeys, $"host {hostKeys} guest {Keys(StashSync.LiveChest())}");
                int took = MoveStack(itemB, false);
                inv.AddItem(new ItemObject(itemA, 2));
                yield return null;
                int put = MoveStack(itemA, true);
                Chat.Send($"#guestmoved={took}:{put}");
                yield return WaitFor(() => peerData.ContainsKey("sawlive"), 10f);
                Check("the stash stayed open on the guest until the host saw its moves", c.isOpened && peerData.ContainsKey("sawlive"));
                c.OnInteract(me.gameObject);
                yield return WaitFor(() => !c.isOpened, 3f);
                Check("guest closed the stash", !c.isOpened);
                Check("the guest got the host's deposit", took > 0 && Owned(itemB) == ownB + took, $"took {took}, {ownB} -> {Owned(itemB)}");
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("guestmoved"), 20f);
                var parts = (peerData.TryGetValue("guestmoved", out var gm) ? gm : "0:0").Split(':');
                int took = int.Parse(parts[0]), put = int.Parse(parts[1]);
                baseA = 10 + put;
                yield return WaitFor(() => CountIn(StashHere(c), itemB) == 0 && CountIn(StashHere(c), itemA) == baseA, 8f);
                Check("the guest's moves reached the host while it had the stash open",
                    CountIn(StashHere(c), itemB) == 0 && CountIn(StashHere(c), itemA) == baseA && StashSync.Instance.HolderOf(TestStashId) == guestId,
                    $"took {took} put {put}: {Keys(StashHere(c))}, holder #{StashSync.Instance.HolderOf(TestStashId)}");
                Chat.Send("#sawlive=1");
                yield return WaitFor(() => StashSync.Instance.HolderOf(TestStashId) == 0, 10f);
                Check("the stash is free again after the guest closed it", StashSync.Instance.HolderOf(TestStashId) == 0);
                Check("nothing duplicated: the host's deposit is gone, the guest's is there",
                    CountIn(StashHere(c), itemB) == 0 && CountIn(StashHere(c), itemA) == baseA, Keys(StashHere(c)));
                // It's the world's: saved with the host's world.
                GameData.CallSaveListeners();
                var saved = GameData.worldData.savedChests.FirstOrDefault(x => x.CollectionId == TestStashId);
                int savedA = saved?.CollectionItems.Where(x => x.Key == itemA.addressKey).Sum(x => x.Amount) ?? -1;
                Check("the stash is saved with the host's world", savedA == baseA, $"{savedA}/{baseA}");
                Chat.Send("#basea=" + baseA);
            }
            yield return Barrier("guest-moved", 60f);
            if (!s.IsHost)
            {
                yield return WaitFor(() => peerData.ContainsKey("basea"), 10f);
                baseA = int.Parse(peerData.TryGetValue("basea", out var ba) ? ba : "0");
                yield return WaitFor(() => CountIn(StashHere(c), itemA) == baseA && CountIn(StashHere(c), itemB) == 0, 5f);
                Check("the guest's copy of the stash is the host's after closing", CountIn(StashHere(c), itemA) == baseA && CountIn(StashHere(c), itemB) == 0, Keys(StashHere(c)));
            }

            // 3. Crafting on the guest takes from the shared chest (the game's ConsumeItemTypeOf with ChestShare flags,
            //    as CraftingRecipe / RepairableBuilding / PlaceableData do): the host's stash loses it.
            if (!s.IsHost)
            {
                int bag = Owned(itemA);
                int left = inv.ConsumeItemTypeOf(itemA, bag + 4, ChestShare.CraftingStations);
                Check("guest's crafting took 4 from the stash here", left == 0 && CountIn(StashHere(c), itemA) == baseA - 4, $"left {left}, stash {Keys(StashHere(c))}");
                Chat.Send("#crafted=1");
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("crafted"), 20f);
                yield return WaitFor(() => CountIn(StashHere(c), itemA) == baseA - 4, 8f);
                Check("the guest's crafting took from the host's stash", CountIn(StashHere(c), itemA) == baseA - 4, $"{Keys(StashHere(c))} (wanted {baseA - 4})");
            }
            yield return Barrier("guest-crafted");

            // 4. The host's crafting: the guest's copy follows.
            if (s.IsHost)
            {
                int bag = Owned(itemA);
                int left = inv.ConsumeItemTypeOf(itemA, bag + 1, ChestShare.CraftingStations);
                Check("host's crafting took 1 from the stash", left == 0 && CountIn(StashHere(c), itemA) == baseA - 5, Keys(StashHere(c)));
            }
            else
            {
                yield return WaitFor(() => CountIn(StashHere(c), itemA) == baseA - 5, 8f);
                Check("the host's crafting shows in the guest's copy", CountIn(StashHere(c), itemA) == baseA - 5, $"{Keys(StashHere(c))} (wanted {baseA - 5})");
            }
            var st = StashSync.Instance;
            Plugin.Log.LogInfo($"[stash] grants {st.Grants} denials {st.Denials} edits {st.Edits}/{st.EditsApplied} takes {st.Takes}/{st.TakesApplied} states {st.StatesSent}/{st.StatesApplied} shortfalls {st.Shortfalls}");
            Check("no take was short (nothing taken twice)", st.Shortfalls == 0, st.Shortfalls.ToString());
            yield return Barrier("done");
            yield return Finish("one stash, the host's");
        }

        // A key item of the scene's (the tutorial's common key), or any loaded one.
        private static ItemData KeyData() =>
            ItemCodec.Find("key_common") is ItemData d && d.itemBehaviour == ItemBehaviour.KeyItem ? d
                : Resources.FindObjectsOfTypeAll<ItemData>().Where(x => x != null && x.itemBehaviour == ItemBehaviour.KeyItem && !string.IsNullOrEmpty(x.addressKey))
                    .OrderBy(x => x.addressKey).FirstOrDefault();

        // Key items are the world's: what a guest gets reaches the host's world (and stays when the guest leaves),
        // what anyone uses is gone for all. Ends with the guest leaving while it has a storage chest open: what it
        // took stays taken, and the chest is free again. Solo: the guest leaves the session.
        [Scenario("keyitems", Timeout = 220, Quick = true)]
        private static IEnumerator KeyItemsTest()
        {
            yield return CoopReady();
            var s = Session.Current;
            var inv = Singleton<Inventory>.main;
            var me = Game.LocalPlayer;
            var key = KeyData();
            if (!Check("found a key item", key != null)) { yield return Finish(""); yield break; }
            Plugin.Log.LogInfo($"[test] key item {key.addressKey}");
            int k0 = Owned(key);
            Chat.Send("#k0=" + k0);
            yield return WaitFor(() => peerData.ContainsKey("k0"), 10f);
            Check("everyone holds the world's key items", peerData.TryGetValue("k0", out var theirK0) && theirK0 == k0.ToString(), $"mine {k0}, theirs {theirK0}");
            yield return Barrier("keys-ready");

            // 1. The guest picks a key up: the host's world has it.
            if (!s.IsHost) inv.AddItem(new ItemObject(key, 1));
            else
            {
                yield return WaitFor(() => Owned(key) == k0 + 1, 10f);
                Check("the guest's key reached the host", Owned(key) == k0 + 1, $"{k0} -> {Owned(key)}");
                GameData.CallSaveListeners();
                int saved = GameData.worldData.worldKeyItems.Where(x => x.Key == key.addressKey).Sum(x => x.Amount);
                Check("and the host's world saves it", saved == k0 + 1, $"{saved}");
            }
            yield return Barrier("key1");
            yield return new WaitForSecondsRealtime(1.5f);
            Check("one key, not one per player", Owned(key) == k0 + 1, $"{Owned(key)}");

            // 2. The host gets one: the guest holds it too.
            yield return Barrier("key1-checked");
            if (s.IsHost) inv.AddItem(new ItemObject(key, 1));
            yield return WaitFor(() => Owned(key) == k0 + 2, 10f);
            Check("the host's key is the guest's too", Owned(key) == k0 + 2, $"{Owned(key)}");
            yield return Barrier("key2");

            // 3. The guest uses one (as an item lock does): gone from the world.
            if (!s.IsHost) inv.ConsumeItemTypeOf(key, 1);
            yield return WaitFor(() => Owned(key) == k0 + 1, 10f);
            Check("a key the guest used is gone for everyone", Owned(key) == k0 + 1, $"{Owned(key)}");
            yield return Barrier("key3");

            // 4. The guest opens a storage chest, takes from it and leaves without closing it.
            var (itemA, _) = TwoItems();
            var c = MakeStash(itemA);
            if (!Check("made a storage chest", c != null)) { yield return Finish(""); yield break; }
            yield return WaitFor(() => ChestSync.Ready(c) && CountIn(StashHere(c), itemA) >= 10, 15f);
            yield return Barrier("stash-made");
            if (!s.IsHost)
            {
                c.OnInteract(me.gameObject);
                yield return WaitFor(() => c.isOpened, 6f);
                Check("guest opened the stash", c.isOpened);
                int took = MoveStack(itemA, false);
                Check("guest took from it", took > 0, took.ToString());
                Chat.Send("#took=" + took);
                yield return WaitFor(() => peerData.ContainsKey("seentake"), 10f);
                Plugin.Log.LogInfo("[test] leaving with the stash open");
                int returns = GuestWorld.ReturnsHome;
                Session.Current.Stop();
                yield return WaitFor(() => GuestWorld.ReturnsHome > returns && !GuestWorld.Switching, 60f);
                yield return Finish("key items and the stash (guest)");
                yield break;
            }
            yield return WaitFor(() => peerData.ContainsKey("took"), 20f);
            int taken = int.Parse(peerData.TryGetValue("took", out var tk) ? tk : "0");
            byte guestId = s.Players.Keys.FirstOrDefault(id => id != s.LocalId);
            yield return WaitFor(() => CountIn(StashHere(c), itemA) == 10 - taken, 8f);
            Check("the guest's take reached the host while it had the stash open", taken > 0 && CountIn(StashHere(c), itemA) == 10 - taken && StashSync.Instance.HolderOf(TestStashId) == guestId,
                $"{Keys(StashHere(c))}, holder #{StashSync.Instance.HolderOf(TestStashId)}");
            Chat.Send("#seentake=1");
            yield return WaitFor(() => s.Players.Count == 1, 60f);
            Check("guest left", s.Players.Count == 1);
            yield return WaitFor(() => StashSync.Instance.HolderOf(TestStashId) == 0, 5f);
            Check("the stash is free again", StashSync.Instance.HolderOf(TestStashId) == 0);
            Check("what the guest took stays taken", CountIn(StashHere(c), itemA) == 10 - taken, Keys(StashHere(c)));
            Check("the guest's key stays in the host's world", Owned(key) == k0 + 1, $"{Owned(key)}");
            c.OnInteract(me.gameObject);
            yield return WaitFor(() => c.isOpened, 3f);
            Check("the host can open the stash", c.isOpened && CountIn(StashSync.LiveChest(), itemA) == 10 - taken, Keys(StashSync.LiveChest()));
            if (c.isOpened) c.OnInteract(me.gameObject);
            var ks = KeyItemSync.Instance;
            Plugin.Log.LogInfo($"[keys] reports applied {ks.ReportsApplied} states sent {ks.StatesSent}");
            yield return Finish("key items and the stash (host)");
        }
    }
}
