using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // Storage chests: the world's stash (the player chest, the showcase, and in Early Access the estate's chests that
    // crafting, repairs and building draw from). Their contents are world data (ChestManager -> WorldData.savedChests),
    // keyed by the chest's id, not by a scene object: one id can stand in several scenes (the demo's "chestShowcase"
    // is in three), and crafting takes from every chest whose sharing flags match, wherever it is.
    //
    // So there's one stash, the host's (the world's owner), whatever the loot mode:
    //   - The host keeps the truth (table below, pushed into its ChestManager, which saves it with its world).
    //     Every change goes out to everyone as the chest's whole contents (StashState); guests keep them as a mirror
    //     in their ChestManager (their world copy), so what they see, and what their crafting finds, is the host's.
    //   - Opening is an intent to the host: one player at a time has a stash open (lock, by id). The holder's moves
    //     go to the host as they happen, as changes (what came in, what went out), so they add up with what others
    //     take meanwhile, and a holder that drops out has already handed over what it took.
    //   - A guest's crafting / repair / building consumption from shared chests (Inventory.ConsumeItemTypeOf with
    //     ChestShare flags) is reported to the host as what it took out of which chest.
    //   - A chest the host's world has no contents for yet (a scene only a guest went to) takes the guest's roll.
    //
    // Which chests: flag-driven (IsStorage). Loot chests keep their opened look (fixedOpenedState) and roll once;
    // storage chests close again, are saved and have an id, or mirror / share their contents by id. In the demo that
    // is "playerChest" (VitromotusCrystal) and "chestShowcase" (TutorialArea, VitromotusCrystal, Town);
    // Town's "Chest_Materials" isn't saved (it refills on every visit), so it stays loot.
    internal class StashSync
    {
        public static StashSync Instance { get; private set; }
        public static void ResetInstance() => Instance = null;

        private const byte OpOpen = 0, OpClose = 1, OpEdit = 2, OpSync = 3;
        private const int MaxItems = 200;   // a chest holds 30; anything far beyond that on the wire is junk
        private const int MaxStashes = 256;

        private class Entry
        {
            public string Id;
            public ChestShare Share;
            public byte Holder;
            public string HolderScene = "";
            public string Json = "";
            public bool Pushed;   // in this copy's ChestManager since it last loaded
        }

        private readonly Session session;
        private readonly Dictionary<string, Entry> table = new Dictionary<string, Entry>();
        // Our open requests waiting for the host (id -> when, which chest).
        private readonly Dictionary<string, (float at, Chest chest)> asked = new Dictionary<string, (float, Chest)>();
        // The stash our player has open, and what was in it when we last told the host.
        public static Chest Held;
        private static string heldId;
        private List<InventoryItem> heldBase;
        private string heldSig;
        private ChestManager lastCm;
        private bool lastCmLoading = true;
        public int Grants, Denials, Edits, EditsApplied, Takes, TakesApplied, StatesSent, StatesApplied, Shortfalls;

        public StashSync(Session session)
        {
            Instance = this;
            this.session = session;
            Held = null;
            heldId = null;
            if (session.IsHost)
            {
                session.On(Msg.StashAsk, OnAsk);
                session.On(Msg.StashTake, OnTake);
                session.PlayerJoined += p => { foreach (var e in table.Values) SendState(e, p.Id); };
                session.PlayerLeft += p =>
                {
                    foreach (var e in table.Values.Where(x => x.Holder == p.Id).ToList()) { e.Holder = 0; Broadcast(e); }
                };
            }
            else session.On(Msg.StashState, OnState);
        }

        // ---------------- which chests ----------------

        public static bool IsStorage(Chest c) =>
            c != null && !string.IsNullOrEmpty(c.ID) && (c.shareContent != ChestShare.None || c.mirrorChest || (c.saveChest && !c.fixedOpenedState));

        // Our world is the session's (the host's own, or the guest's copy of it).
        public static bool On
        {
            get
            {
                var s = Session.Current;
                return Instance != null && s != null && s.Ready && (s.IsHost || GuestWorld.Active);
            }
        }

        public static bool Handles(Chest c) => On && IsStorage(c);

        public byte HolderOf(string id) => table.TryGetValue(id ?? "", out var e) ? e.Holder : (byte)0;

        // The contents as this copy's session knows them (host: the truth; guest: the host's last word).
        public List<InventoryItem> Known(string id) => table.TryGetValue(id ?? "", out var e) ? ItemCodec.Decode(e.Json) : null;

        private static ChestManager Cm => Singleton<ChestManager>.HasMain ? Singleton<ChestManager>.main : null;
        private static bool CmReady => Cm != null && !Cm.isLoading && Game.InGame;

        // What an open chest holds right now: while it's open the game keeps its items in the inventory (section Chest);
        // the chest's own list is only written back when it closes.
        public static List<InventoryItem> LiveChest() =>
            Singleton<Inventory>.HasMain ? Singleton<Inventory>.main.allItems.Where(i => i?.itemObject?.data != null && i.section == InventorySection.Chest).ToList()
                                         : new List<InventoryItem>();

        // Cheap fingerprint of a list (checked every frame while a chest is open).
        public static string Sig(IEnumerable<InventoryItem> items) =>
            string.Join(";", items.Select(i => i.itemObject.data.addressKey + "x" + i.itemObject.amount + "@" + i.slotIndex + "#" +
                                               System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(i.itemObject.randomValues)));

        private static string Key(ItemObject o) => StationItemCodec.Key(o);

        private static Dictionary<string, (ItemObject sample, int amount)> Totals(IEnumerable<InventoryItem> items)
        {
            var d = new Dictionary<string, (ItemObject, int)>();
            foreach (var i in items)
            {
                if (i?.itemObject?.data == null) continue;
                string k = Key(i.itemObject);
                d[k] = d.TryGetValue(k, out var t) ? (t.Item1, t.Item2 + i.itemObject.amount) : (i.itemObject, i.itemObject.amount);
            }
            return d;
        }

        private static bool SameTotals(IEnumerable<InventoryItem> a, IEnumerable<InventoryItem> b)
        {
            var x = Totals(a);
            var y = Totals(b);
            return x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var t) && t.amount == kv.Value.amount);
        }

        // What came in and what went out between two views of a chest (by item, amounts summed over the slots).
        private static void Diff(IEnumerable<InventoryItem> before, IEnumerable<InventoryItem> after, out List<InventoryItem> adds, out List<InventoryItem> removes)
        {
            var b = Totals(before);
            var a = Totals(after);
            adds = new List<InventoryItem>();
            removes = new List<InventoryItem>();
            foreach (var kv in a)
            {
                int d = kv.Value.amount - (b.TryGetValue(kv.Key, out var t) ? t.amount : 0);
                if (d > 0) adds.Add(new InventoryItem(new ItemObject(kv.Value.sample) { amount = d }, 0, InventorySection.Chest));
            }
            foreach (var kv in b)
            {
                int d = kv.Value.amount - (a.TryGetValue(kv.Key, out var t) ? t.amount : 0);
                if (d > 0) removes.Add(new InventoryItem(new ItemObject(kv.Value.sample) { amount = d }, 0, InventorySection.Chest));
            }
        }

        private static List<InventoryItem> Copy(IEnumerable<InventoryItem> items) => ItemCodec.Decode(ItemCodec.Encode(items));

        private static List<InventoryItem> DecodeBounded(string json)
        {
            var items = ItemCodec.Decode(json);
            if (items.Count > MaxItems) throw new System.FormatException($"{items.Count} items in one chest");
            foreach (var i in items) i.itemObject.amount = Mathf.Clamp(i.itemObject.amount, 0, 9999);
            return items;
        }

        private static bool SaneId(string id) => !string.IsNullOrEmpty(id) && id.Length <= 128 && id.All(ch => !char.IsControl(ch));

        // Takes items out of a list (amounts first from the matching stacks); returns how much couldn't be found.
        private static int Remove(List<InventoryItem> list, IEnumerable<InventoryItem> removes, System.Action<InventoryItem> removed = null)
        {
            int missing = 0;
            foreach (var r in removes)
            {
                string k = Key(r.itemObject);
                int left = r.itemObject.amount;
                foreach (var it in list.Where(x => x?.itemObject?.data != null && Key(x.itemObject) == k).ToList())
                {
                    if (left <= 0) break;
                    int take = Mathf.Min(left, it.itemObject.amount);
                    it.itemObject.amount -= take;
                    left -= take;
                    if (it.itemObject.amount <= 0) { list.Remove(it); removed?.Invoke(it); }
                }
                missing += Mathf.Max(0, left);
            }
            return missing;
        }

        // Puts items into a list: onto a stack of the same item first, then into the slot they came from (or a free one).
        private static void Add(List<InventoryItem> list, IEnumerable<InventoryItem> adds, IEnumerable<InventoryItem> layout)
        {
            int limit = Singleton<Inventory>.HasMain ? Singleton<Inventory>.main.stackLimit : 99;
            foreach (var a in adds)
            {
                string k = Key(a.itemObject);
                int left = a.itemObject.amount;
                if (a.itemObject.data.IsStackable)
                    foreach (var it in list.Where(x => Key(x.itemObject) == k && x.itemObject.amount < limit))
                    {
                        int put = Mathf.Min(left, limit - it.itemObject.amount);
                        it.itemObject.amount += put;
                        left -= put;
                        if (left <= 0) break;
                    }
                if (left <= 0) continue;
                int slot = layout?.FirstOrDefault(x => Key(x.itemObject) == k)?.slotIndex ?? -1;
                if (slot < 0 || list.Any(x => x.slotIndex == slot))
                {
                    slot = 0;
                    while (list.Any(x => x.slotIndex == slot)) slot++;
                }
                list.Add(new InventoryItem(new ItemObject(a.itemObject) { amount = left }, slot, InventorySection.Chest));
            }
        }

        // ---------------- this copy's ChestManager ----------------

        // Sets a stash's contents in our ChestManager (a saved collection with its sharing flags), unless our player
        // has it open (then the game's inventory screen owns it until it closes).
        private void Push(Entry e)
        {
            if (!(session.IsHost || GuestWorld.Active)) return;
            if (Held != null && heldId == e.Id && Held.isOpened) { e.Pushed = true; return; }
            var cm = Cm;
            if (!CmReady) { e.Pushed = false; return; }
            var items = ItemCodec.Decode(e.Json);
            var list = cm.GetChestItems(e.Id);
            if (list == null) cm.AddChest(e.Id, items, true, e.Share);
            else { list.Clear(); list.AddRange(items); }
            if (cm.allChests.TryGetValue(e.Id, out var col)) { col.saveCollection = true; col.shareContent = e.Share; }
            e.Pushed = true;
            RefreshTitles(e.Id);
        }

        // "Empty" titles follow the contents.
        private static void RefreshTitles(string id)
        {
            foreach (var c in Object.FindObjectsByType<Chest>(FindObjectsSortMode.None))
                if (c.ID == id && !c.isOpened)
                    try { c.UpdateVisual(); } catch { }
        }

        // ---------------- host: the stash's truth ----------------

        // The host's entry for a stash: known already, or from its ChestManager / its world's saved chests, or else
        // (nobody rolled it in this world yet) the guest's roll.
        private Entry Ensure(string id, ChestShare share, string guestJson)
        {
            if (table.TryGetValue(id, out var e)) return e;
            string json = null;
            var cm = Cm;
            if (Held != null && heldId == id && Held.isOpened) json = ItemCodec.Encode(LiveChest());
            else if (cm != null && !cm.isLoading && cm.allChests.TryGetValue(id, out var col))
            {
                json = ItemCodec.Encode(col.mainItems);
                share = col.shareContent;
            }
            else
            {
                var saved = GameData.worldData?.savedChests?.FirstOrDefault(x => x.CollectionId == id);
                if (saved != null)
                {
                    json = ItemCodec.EncodeSaved(saved.CollectionItems);
                    share = saved.CollectionSharing;
                }
            }
            bool adopted = false;
            if (json == null)
            {
                // (a guest can't fill the host's save with made-up chests: a world has a handful of storage chests)
                if (guestJson == null || table.Count >= MaxStashes) return null;
                json = guestJson;
                adopted = true;
            }
            e = table[id] = new Entry { Id = id, Share = share, Json = json };
            if (adopted) { Plugin.Log.LogInfo($"[stash] '{id}': the world had no contents yet, a guest's roll it is"); Push(e); }
            else e.Pushed = true;
            return e;
        }

        // Still has that stash open: our own player, or a guest still in the scene it opened it in.
        private bool StillHolding(Entry e)
        {
            if (e.Holder == 0) return false;
            if (e.Holder == session.LocalId) return Held != null && heldId == e.Id && Held.isOpened;
            var p = MPRunner.Instance?.Presence;
            return session.Players.ContainsKey(e.Holder) && p != null && p.Remotes.TryGetValue(e.Holder, out var rp) && rp.Latest != null
                   && rp.Latest.Scene == e.HolderScene && rp.SecondsSinceState < SceneAuthority.PresenceTimeout;
        }

        private static string SceneOf(byte id)
        {
            var p = MPRunner.Instance?.Presence;
            return p != null && p.Remotes.TryGetValue(id, out var rp) && rp.Latest != null ? rp.Latest.Scene ?? "" : "";
        }

        private void OnAsk(byte from, NetDataReader r)
        {
            byte op = r.GetByte();
            string id = r.GetString();
            var share = (ChestShare)r.GetInt();
            if (!SaneId(id) || from == session.LocalId) return;
            if (op == OpOpen || op == OpSync)
            {
                string local = r.GetString();
                DecodeBounded(local);   // (junk is the sender's fault, before we keep any of it)
                bool known = table.ContainsKey(id);
                var e = Ensure(id, share, local);
                if (e == null) return;
                if (op == OpSync)
                {
                    // A guest set this stash up: put it right if its copy isn't the world's.
                    if (!known) Broadcast(e);
                    else if (!SameTotals(ItemCodec.Decode(local), ItemCodec.Decode(e.Json))) SendState(e, from);
                    return;
                }
                if (e.Holder != from && StillHolding(e))
                {
                    Denials++;
                    SendState(e, from);   // tells the asker who has it
                    return;
                }
                e.Holder = from;
                e.HolderScene = SceneOf(from);
                Grants++;
                Broadcast(e);
                return;
            }
            if (op == OpEdit || op == OpClose)
            {
                var adds = DecodeBounded(r.GetString());
                var removes = DecodeBounded(r.GetString());
                var layout = DecodeBounded(r.GetString());
                var e = Ensure(id, share, null);
                if (e == null) return;
                // The moves happened on the guest (its bag already changed): they count whoever holds the lock now.
                var list = ItemCodec.Decode(e.Json);
                int missing = Remove(list, removes);
                if (missing > 0) { Shortfalls++; Plugin.Log.LogWarning($"[stash] '{id}': #{from} took {missing} more than was left"); }
                Add(list, adds, layout);
                // The holder's own arrangement when nothing else changed meanwhile (the usual case).
                if (e.Holder == from && SameTotals(list, layout)) list = layout;
                e.Json = ItemCodec.Encode(list);
                if (op == OpClose && e.Holder == from) e.Holder = 0;
                EditsApplied++;
                if (adds.Count + removes.Count > 0) SaveSync.Request($"stash: #{from}'s moves");
                Push(e);
                Broadcast(e);
            }
        }

        // A guest's crafting / repair took items from shared chests.
        private void OnTake(byte from, NetDataReader r)
        {
            int n = NetGuard.Count(r.GetInt(), r, 8);
            for (int i = 0; i < n; i++)
            {
                string id = r.GetString();
                var removes = DecodeBounded(r.GetString());
                if (!SaneId(id) || from == session.LocalId) continue;
                var e = Ensure(id, ChestShare.None, null);
                if (e == null) continue;
                int missing;
                if (Held != null && heldId == id && Held.isOpened)
                {
                    // Our player has it open: take them out of what's on screen.
                    var inv = Singleton<Inventory>.main;
                    missing = Remove(LiveChest(), removes, it => inv.allItems.Remove(it));
                    inv.SetInventoryChanged();
                    e.Json = ItemCodec.Encode(LiveChest());
                    heldSig = Sig(LiveChest());
                }
                else
                {
                    var list = ItemCodec.Decode(e.Json);
                    missing = Remove(list, removes);
                    e.Json = ItemCodec.Encode(list);
                    Push(e);
                }
                if (missing > 0) { Shortfalls++; Plugin.Log.LogWarning($"[stash] '{id}': #{from}'s crafting took {missing} more than was left"); }
                TakesApplied++;
                SaveSync.Request($"stash: #{from}'s crafting");
                Broadcast(e);
            }
        }

        private void Broadcast(Entry e, bool gone = false)
        {
            if (!session.IsHost) return;
            session.Send(Msg.StashState, w => Write(w, e, gone));
            StatesSent++;
        }

        private void SendState(Entry e, byte to)
        {
            session.SendTo(to, Msg.StashState, w => Write(w, e));
            StatesSent++;
        }

        private static void Write(NetDataWriter w, Entry e, bool gone = false)
        {
            w.Put(e.Id);
            w.Put((int)e.Share);
            w.Put(e.Holder);
            w.Put(e.Json);
            w.Put(gone);
        }

        // A stash's collection was removed (Chest.DropAllToWorld: its items went to the ground, the chest is gone).
        private bool removing;
        public void OnRemoved(string id)
        {
            if (removing || !On || id == null || !table.TryGetValue(id, out var e)) return;
            if (session.IsHost)
            {
                table.Remove(id);
                Broadcast(e, gone: true);
                return;
            }
            // Our player emptied it: what was in it is out of the world's stash (the host keeps the collection).
            string all = e.Json;
            session.Send(Msg.StashAsk, w => { w.Put(OpEdit); w.Put(id); w.Put((int)e.Share); w.Put(""); w.Put(all); w.Put(""); });
            table.Remove(id);
            SaveSync.Request("stash: emptied");
        }

        // ---------------- guest: the host's word ----------------

        private void OnState(byte from, NetDataReader r)
        {
            if (from != Protocol.HostId) return;
            string id = r.GetString();
            var share = (ChestShare)r.GetInt();
            byte holder = r.GetByte();
            string json = r.GetString();
            bool gone = r.GetBool();
            if (!SaneId(id)) return;
            DecodeBounded(json);
            if (gone)
            {
                table.Remove(id);
                var cm = Cm;
                if (GuestWorld.Active && cm != null && !cm.isLoading)
                {
                    removing = true;
                    try { cm.RemoveChest(id); }
                    finally { removing = false; }
                }
                return;
            }
            if (!table.TryGetValue(id, out var e)) table[id] = e = new Entry { Id = id };
            e.Share = share;
            e.Holder = holder;
            e.Json = json;
            StatesApplied++;
            Push(e);
            if (!asked.TryGetValue(id, out var a)) return;
            // The host answers an open with who holds it now (us, or whoever has it): a state with nobody holding it
            // is another change (the stash set up, someone closing it) on its way before our answer.
            if (holder == 0) return;
            asked.Remove(id);
            if (Time.realtimeSinceStartup - a.at > 6f || a.chest == null) return;   // too late: the player moved on
            if (holder == session.LocalId && !a.chest.isOpened)
            {
                Held = a.chest;
                heldId = id;
                a.chest.OpenChest();
            }
            else if (holder != session.LocalId) Plugin.Log.LogInfo($"[stash] '{id}' is in use by #{holder}");
        }

        // ---------------- our player ----------------

        // E on a storage chest. Returns whether the game's own OnInteract runs.
        public bool OnInteract(Chest c)
        {
            if (c.isOpened) return true;   // closing: OnClosed reports it
            string id = c.ID;
            if (session.IsHost)
            {
                var e = Ensure(id, c.shareContent, ItemCodec.Encode(LocalItems(id)));
                if (e.Holder != session.LocalId && StillHolding(e))
                {
                    Denials++;
                    Plugin.Log.LogInfo($"[stash] '{id}' is in use by #{e.Holder}");
                    return false;
                }
                e.Holder = session.LocalId;
                e.HolderScene = Game.SceneName;
                Push(e);
                Held = c;
                heldId = id;
                Grants++;
                Broadcast(e);
                return true;
            }
            if (asked.TryGetValue(id, out var a) && Time.realtimeSinceStartup - a.at < 6f) return false;   // still waiting
            asked[id] = (Time.realtimeSinceStartup, c);
            string local = ItemCodec.Encode(LocalItems(id));
            session.Send(Msg.StashAsk, w => { w.Put(OpOpen); w.Put(id); w.Put((int)c.shareContent); w.Put(local); });
            return false;
        }

        private static List<InventoryItem> LocalItems(string id) =>
            Cm != null && !Cm.isLoading ? Cm.GetChestItems(id) ?? new List<InventoryItem>() : new List<InventoryItem>();

        // The chest is open on our screen (the inventory now holds its items).
        public void OnOpened(Chest c)
        {
            if (Held != c) return;
            var live = LiveChest();
            heldBase = Copy(live);
            heldSig = Sig(live);
        }

        // The chest closed (the game wrote what's left back into its list).
        public void OnClosed(Chest c)
        {
            if (Held != c || !On) { if (Held == c) Drop(); return; }
            if (session.IsHost && table.TryGetValue(heldId, out var e)) e.Holder = 0;
            Report(OpClose, LocalItems(heldId));
            Drop();
        }

        private void Drop()
        {
            Held = null;
            heldId = null;
            heldBase = null;
            heldSig = null;
        }

        // Our player's moves: guest -> the host (as changes); host -> its truth, to everyone.
        private void Report(byte op, List<InventoryItem> now)
        {
            string id = heldId;
            if (id == null) return;
            if (session.IsHost)
            {
                if (!table.TryGetValue(id, out var e)) return;
                e.Json = ItemCodec.Encode(now);
                Broadcast(e);
                return;
            }
            Diff(heldBase ?? new List<InventoryItem>(), now, out var adds, out var removes);
            heldBase = Copy(now);
            string a = ItemCodec.Encode(adds), rm = ItemCodec.Encode(removes), layout = ItemCodec.Encode(now);
            var share = Held != null ? Held.shareContent : ChestShare.None;
            session.Send(Msg.StashAsk, w => { w.Put(op); w.Put(id); w.Put((int)share); w.Put(a); w.Put(rm); w.Put(layout); });
            Edits++;
            if (adds.Count + removes.Count > 0) SaveSync.Request("stash: our moves");
        }

        // A storage chest's contents were just set up here.
        public void OnCreated(Chest c)
        {
            if (!On || !IsStorage(c)) return;
            string id = c.ID;
            if (session.IsHost)
            {
                bool known = table.ContainsKey(id);
                var e = Ensure(id, c.shareContent, null);
                if (e == null) return;
                if (!known) Broadcast(e);
                else Push(e);   // the world's word over a fresh roll
                return;
            }
            if (table.TryGetValue(id, out var mine)) { Push(mine); return; }   // the host's word already came
            string local = ItemCodec.Encode(LocalItems(id));
            session.Send(Msg.StashAsk, w => { w.Put(OpSync); w.Put(id); w.Put((int)c.shareContent); w.Put(local); });
        }

        // ---------------- shared-chest consumption (crafting, repairs, building) ----------------

        // The shared collections a consumption may take from, as they are before it.
        public Dictionary<string, List<InventoryItem>> BeforeConsume(ChestShare share)
        {
            var cm = Cm;
            if (share == ChestShare.None || !On || cm == null) return null;
            return cm.allChests.Where(kv => (kv.Value.shareContent & share) != 0).ToDictionary(kv => kv.Key, kv => Copy(kv.Value.mainItems));
        }

        public void AfterConsume(Dictionary<string, List<InventoryItem>> before)
        {
            var cm = Cm;
            if (before == null || cm == null || !On) return;
            var taken = new List<(string id, string json)>();
            foreach (var kv in before)
            {
                var now = cm.GetChestItems(kv.Key) ?? new List<InventoryItem>();
                Diff(kv.Value, now, out _, out var removes);
                if (removes.Count == 0) continue;
                if (session.IsHost)
                {
                    var e = Ensure(kv.Key, ChestShare.None, null);
                    if (e == null) continue;
                    e.Json = ItemCodec.Encode(now);
                    Broadcast(e);
                }
                else taken.Add((kv.Key, ItemCodec.Encode(removes)));
            }
            if (taken.Count == 0) return;
            Takes += taken.Count;
            SaveSync.Request("stash: our crafting");
            session.Send(Msg.StashTake, w =>
            {
                w.Put(taken.Count);
                foreach (var t in taken) { w.Put(t.id); w.Put(t.json); }
            });
        }

        // ---------------- per frame ----------------

        public void Update()
        {
            if (!session.Ready || Session.Current != session) return;
            // A ChestManager that (re)loaded has the world's saved chests from before: the stash as we know it again.
            var cm = Cm;
            if (cm != lastCm || (lastCmLoading && cm != null && !cm.isLoading))
                foreach (var e in table.Values) e.Pushed = false;
            lastCm = cm;
            lastCmLoading = cm == null || cm.isLoading;
            if (CmReady)
                foreach (var e in table.Values.Where(x => !x.Pushed).ToList()) Push(e);

            if (heldId == null) return;
            if (Held == null || !Held.isOpened || !On)
            {
                // Gone without closing (scene change, the session ended): what it took already went over.
                if (session.IsHost && table.TryGetValue(heldId, out var e) && e.Holder == session.LocalId) { e.Holder = 0; Broadcast(e); }
                else if (!session.IsHost && On) Report(OpClose, heldBase ?? new List<InventoryItem>());
                Drop();
                return;
            }
            var live = LiveChest();
            string sig = Sig(live);
            if (heldSig == null) { heldSig = sig; heldBase = Copy(live); return; }
            if (sig == heldSig) return;
            heldSig = sig;
            Report(OpEdit, live);
        }
    }

    [HarmonyPatch(typeof(ChestManager), nameof(ChestManager.RemoveChest))]
    internal static class StashRemovePatch
    {
        private static void Postfix(string chestID, bool __result)
        {
            if (__result) StashSync.Instance?.OnRemoved(chestID);
        }
    }

    // Crafting, repairs, seeds and building take what they need from the bag and the shared chests.
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.ConsumeItemTypeOf))]
    internal static class StashConsumeTypePatch
    {
        private static void Prefix(ChestShare sharingContent, out Dictionary<string, List<InventoryItem>> __state) =>
            __state = StashSync.Instance?.BeforeConsume(sharingContent);

        private static void Postfix(Dictionary<string, List<InventoryItem>> __state) => StashSync.Instance?.AfterConsume(__state);
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.ConsumeItem))]
    internal static class StashConsumeItemPatch
    {
        private static void Prefix(ChestShare sharingContent, out Dictionary<string, List<InventoryItem>> __state) =>
            __state = StashSync.Instance?.BeforeConsume(sharingContent);

        private static void Postfix(Dictionary<string, List<InventoryItem>> __state) => StashSync.Instance?.AfterConsume(__state);
    }
}
