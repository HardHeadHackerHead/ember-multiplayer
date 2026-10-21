using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Key items are the world's, not the character's: the game keeps them in WorldData.worldKeyItems (Inventory.
    // SaveInventory / LoadInventory), never in the character file. In co-op the world is the host's, so they are the
    // host's too, held by everyone in the session:
    //   - A guest arrives with the world's key items (they come with the host's world) and goes home without them.
    //   - A key item a guest gets (a chest, a drop, a reward of its own) or uses (an item lock consumes it) changes
    //     the host's world: the guest reports what it got and used; the host adds / consumes them in its own
    //     inventory (saved with its world) and sends everyone the world's list, which replaces theirs.
    //   - Changes on the host go to everyone the same way.
    // So a one-off key a guest picks up stays in the world when it leaves (nobody is locked out), and a key used
    // by anyone is gone for everyone (item locks open for all through ItemRequesterSync).
    //
    // Changes are found by comparing each frame (any way the game adds or takes an item counts). A guest's list
    // from the host only replaces ours once it includes our last report (ack), so nothing we just got flickers away.
    // Quest rewards run on every copy that completes the quest (QuestRewards): the key items they hand out reach
    // the world through the host's own completion, not once per player (BeginRewards / EndRewards).
    internal class KeyItemSync
    {
        public static KeyItemSync Instance { get; private set; }
        // Tests: a fresh world on the same session (nothing of the last world carries over).
        public static void ResetInstance()
        {
            Instance = null;
            agreed = null;
            carriedAdds.Clear();
            carriedRemoves.Clear();
        }
        private const int MaxItems = 200;

        private readonly Session session;
        // What we last saw (key -> amount); null = take a fresh look (a load replaced the inventory).
        private Dictionary<string, int> seen;
        private Inventory seenInv;
        // guest
        private int seq;               // our last report
        private string hostJson;       // the world's list as the host last sent it
        private int hostAck = -1;      // which of our reports it includes
        private bool hello, started;
        // The world's key items as we last agreed with the host, and what we got / used since while cut off
        // (Carry, before a rejoin reloads the host's world): reported once we're back. Outlive the session.
        private static Dictionary<string, int> agreed;
        private static List<InventoryItem> carriedAdds = new List<InventoryItem>(), carriedRemoves = new List<InventoryItem>();
        // host
        private readonly Dictionary<byte, int> acks = new Dictionary<byte, int>();
        private readonly List<(byte from, int seq, List<InventoryItem> adds, List<InventoryItem> removes)> queue = new List<(byte, int, List<InventoryItem>, List<InventoryItem>)>();
        private int rewards;
        public int ReportsSent, ReportsApplied, StatesSent, StatesApplied;

        public KeyItemSync(Session session)
        {
            Instance = this;
            this.session = session;
            if (session.IsHost)
            {
                session.On(Msg.KeyItemsChange, OnChange);
                session.PlayerLeft += p => acks.Remove(p.Id);
            }
            else
            {
                session.On(Msg.KeyItemsState, OnState);
                // A new join (not a rejoin while still in the host's world): nothing carried over.
                if (!GuestWorld.Active) { agreed = null; carriedAdds.Clear(); carriedRemoves.Clear(); }
            }
        }

        // Guest, connection lost and back: the host's world is about to be loaded again (GuestWorld), which replaces
        // our key items with the world's. What we got or used while cut off is kept to report (if this session's
        // first look didn't already find it).
        public static void Carry()
        {
            var inv = Inv;
            if (agreed == null || inv == null || inv.isLoading) return;
            Diff(agreed, Totals(Held(inv)), inv, out var adds, out var removes);
            carriedAdds.AddRange(adds);
            carriedRemoves.AddRange(removes);
            agreed = null;
            if (adds.Count + removes.Count > 0) Plugin.Log.LogInfo($"[keys] Kept for the host: +{Describe(adds)} -{Describe(removes)}");
        }

        public static bool IsKey(InventoryItem i) =>
            i?.itemObject?.data != null && i.itemObject.data.itemBehaviour == ItemBehaviour.KeyItem
            && i.section != InventorySection.Chest && i.section != InventorySection.Stock && i.section != InventorySection.Buyback;

        public static List<InventoryItem> Held(Inventory inv) => inv.allItems.Where(IsKey).ToList();

        private static Dictionary<string, int> Totals(IEnumerable<InventoryItem> items)
        {
            var d = new Dictionary<string, int>();
            foreach (var i in items)
            {
                string k = i.itemObject.data.addressKey ?? "";
                d[k] = (d.TryGetValue(k, out int n) ? n : 0) + i.itemObject.amount;
            }
            return d;
        }

        private static bool Same(Dictionary<string, int> a, Dictionary<string, int> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out int n) && n == kv.Value);

        // Our inventory is there and is the session's world's.
        private bool Ready(Inventory inv) =>
            inv != null && !inv.isLoading && Game.InGame && !GuestWorld.Switching && (session.IsHost || GuestWorld.Active);

        private static Inventory Inv => Singleton<Inventory>.HasMain ? Singleton<Inventory>.main : null;

        private static List<InventoryItem> DecodeBounded(string json)
        {
            var items = ItemCodec.Decode(json);
            if (items.Count > MaxItems) throw new System.FormatException($"{items.Count} key items");
            foreach (var i in items) i.itemObject.amount = Mathf.Clamp(i.itemObject.amount, 0, 999);
            items.RemoveAll(i => i.itemObject.amount <= 0 || i.itemObject.data.itemBehaviour != ItemBehaviour.KeyItem);
            return items;
        }

        // ---------------- per frame ----------------

        public void Update()
        {
            if (!session.Ready || Session.Current != session) return;
            var inv = Inv;
            if (!Ready(inv) || inv != seenInv) { seen = null; seenInv = inv; return; }
            if (session.IsHost && queue.Count > 0)
            {
                ApplyQueue(inv);   // (sends the world's list, our own latest changes included)
                seen = Totals(Held(inv));
                return;
            }
            var now = Totals(Held(inv));
            if (seen == null)
            {
                // Freshly loaded: what the world gave us, not something we did.
                seen = now;
                if (session.IsHost) return;
                if (!started)
                {
                    // Back after a lost connection: what we got or used since we last agreed with the host.
                    started = true;
                    if (agreed != null)
                    {
                        Diff(agreed, now, inv, out var a, out var r);
                        carriedAdds.AddRange(a);
                        carriedRemoves.AddRange(r);
                    }
                }
                if (!hello) { hello = true; Report(new List<InventoryItem>(), new List<InventoryItem>()); }   // the host answers with the world's list
                if (carriedAdds.Count + carriedRemoves.Count > 0)
                {
                    Report(carriedAdds, carriedRemoves);
                    carriedAdds = new List<InventoryItem>();
                    carriedRemoves = new List<InventoryItem>();
                }
                TryApplyHost(inv);
                agreed = seen;
                return;
            }
            if (!Same(now, seen))
            {
                var before = seen;
                seen = now;
                if (session.IsHost) { Broadcast(inv); return; }
                Diff(before, now, inv, out var adds, out var removes);
                if (rewards > 0) adds.Clear();   // the host's own completion hands those to the world
                if (adds.Count + removes.Count > 0) Report(adds, removes);
            }
            if (!session.IsHost) agreed = seen;
        }

        private static void Diff(Dictionary<string, int> before, Dictionary<string, int> now, Inventory inv, out List<InventoryItem> adds, out List<InventoryItem> removes)
        {
            adds = new List<InventoryItem>();
            removes = new List<InventoryItem>();
            foreach (var kv in now)
            {
                int d = kv.Value - (before.TryGetValue(kv.Key, out int b) ? b : 0);
                var sample = inv.allItems.FirstOrDefault(i => IsKey(i) && i.itemObject.data.addressKey == kv.Key);
                if (d > 0 && sample != null) adds.Add(new InventoryItem(new ItemObject(sample.itemObject) { amount = d }, 0, InventorySection.KeyItem));
            }
            foreach (var kv in before)
            {
                int d = kv.Value - (now.TryGetValue(kv.Key, out int n) ? n : 0);
                var data = d > 0 ? ItemCodec.Find(kv.Key) : null;
                if (data != null) removes.Add(new InventoryItem(new ItemObject(data, d), 0, InventorySection.KeyItem));
            }
        }

        // ---------------- guest ----------------

        private void Report(List<InventoryItem> adds, List<InventoryItem> removes)
        {
            int n = ++seq;
            string a = ItemCodec.Encode(adds), r = ItemCodec.Encode(removes);
            session.Send(Msg.KeyItemsChange, w => { w.Put(n); w.Put(a); w.Put(r); });
            ReportsSent++;
            if (adds.Count + removes.Count > 0)
            {
                Plugin.Log.LogInfo($"[keys] Reported to the host: +{Describe(adds)} -{Describe(removes)}");
                SaveSync.Request("key items: ours");
            }
        }

        private void OnState(byte from, NetDataReader r)
        {
            if (from != Protocol.HostId) return;
            int ack = r.GetInt();
            string json = r.GetString();
            DecodeBounded(json);
            hostJson = json;
            hostAck = ack;
            var inv = Inv;
            if (Ready(inv) && seen != null) TryApplyHost(inv);
        }

        // The world's key items replace ours, once they include everything we reported.
        private void TryApplyHost(Inventory inv)
        {
            if (hostJson == null || hostAck < seq) return;
            var world = DecodeBounded(hostJson);
            if (Same(Totals(world), Totals(Held(inv)))) { seen = Totals(Held(inv)); return; }
            inv.allItems.RemoveAll(IsKey);
            foreach (var i in world)
            {
                i.section = InventorySection.KeyItem;
                inv.allItems.Add(i);
            }
            inv.SetInventoryChanged();
            seen = Totals(Held(inv));
            StatesApplied++;
            Plugin.Log.LogInfo($"[keys] The world's key items: {Describe(world)}");
        }

        // Quest rewards (QuestRewards.Grant) on a guest: the key items they give come from the host's completion.
        public void BeginRewards()
        {
            if (session.IsHost) return;
            Update();   // what happened before counts as ours
            rewards++;
        }

        public void EndRewards()
        {
            if (session.IsHost || rewards == 0) return;
            Update();
            rewards--;
        }

        // ---------------- host ----------------

        private void OnChange(byte from, NetDataReader r)
        {
            int n = r.GetInt();
            var adds = DecodeBounded(r.GetString());
            var removes = DecodeBounded(r.GetString());
            if (from == session.LocalId) return;
            if (queue.Count >= 64) { Plugin.Log.LogWarning($"[keys] #{from}'s report dropped (queue full)"); return; }
            queue.Add((from, n, adds, removes));
            var inv = Inv;
            if (Ready(inv)) { ApplyQueue(inv); seen = Totals(Held(inv)); }   // (before the next frame takes it for our own change)
        }

        // Reports apply to our own inventory (saved with our world); then everyone gets the world's list.
        private void ApplyQueue(Inventory inv)
        {
            foreach (var q in queue)
            {
                foreach (var a in q.adds) inv.AddItem(new ItemObject(a.itemObject));
                foreach (var x in q.removes)
                {
                    int left = inv.ConsumeItemTypeOf(x.itemObject.data, x.itemObject.amount);
                    if (left > 0) Plugin.Log.LogInfo($"[keys] #{q.from} used {left} {x.itemObject.data.addressKey} the world didn't have");
                }
                if (!acks.TryGetValue(q.from, out int had) || q.seq > had) acks[q.from] = q.seq;
                ReportsApplied++;
                if (q.adds.Count + q.removes.Count > 0)
                {
                    Plugin.Log.LogInfo($"[keys] #{q.from}: +{Describe(q.adds)} -{Describe(q.removes)}");
                    SaveSync.Request($"key items: #{q.from}'s");
                }
            }
            queue.Clear();
            Broadcast(inv);
        }

        private void Broadcast(Inventory inv)
        {
            string json = ItemCodec.Encode(Held(inv));
            foreach (var p in session.Others)
            {
                int ack = acks.TryGetValue(p.Id, out int a) ? a : 0;
                session.SendTo(p.Id, Msg.KeyItemsState, w => { w.Put(ack); w.Put(json); });
                StatesSent++;
            }
        }

        private static string Describe(IEnumerable<InventoryItem> items) =>
            "[" + string.Join(",", items.Select(i => i.itemObject.data.addressKey + "x" + i.itemObject.amount)) + "]";
    }
}
