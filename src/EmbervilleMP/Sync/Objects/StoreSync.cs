using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // Shops (StoreComponent + StoreManager): one stock per store for everyone.
    //   - The scene's runner owns the stock (its roll, or its saved store) and announces it; everyone else shows that.
    //   - A guest's purchase is an intent: the runner checks the item is still there, takes it out of the stock and
    //     broadcasts the new stock with "player P's request N got this item"; P then pays and gets it.
    //   - Gold, selling and buyback stay on each player's own machine (selling never touches the shared stock).
    //   - A guest opening a store before the runner's stock arrived asks for it and opens once it's here.
    internal class StoreSync : ObjectHandler
    {
        public static StoreSync Instance { get; private set; }
        public StoreSync()
        {
            Instance = this;
            StationsTicker.Ensure();
            StationsTicker.Tick -= TickStatic;
            StationsTicker.Tick += TickStatic;
        }

        public override string Kind => "store";
        public int Purchases, PurchasesGranted, PurchasesDenied, StocksApplied, StocksAnnounced;

        private const byte OpRequest = 0, OpBuy = 1;

        private class Grant { public StoreComponent Store; public byte Player; public int Nonce; public bool Ok; public ItemObject Item; public int Amount; }
        private class Pending { public StoreComponent Store; public bool WholeStack; }

        private int sceneHandle = -1;
        private readonly HashSet<StoreComponent> received = new HashSet<StoreComponent>();
        private readonly HashSet<StoreComponent> announced = new HashSet<StoreComponent>();
        private readonly HashSet<StoreComponent> resend = new HashSet<StoreComponent>();
        private readonly Dictionary<StoreComponent, byte[]> early = new Dictionary<StoreComponent, byte[]>();
        private readonly Dictionary<StoreComponent, float> setupAt = new Dictionary<StoreComponent, float>();
        private readonly Dictionary<int, Pending> buys = new Dictionary<int, Pending>();
        private Grant grant;
        private StoreComponent waitingOpen;
        private float waitingSince;
        internal static bool OpeningForReal;

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<StoreComponent>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public static StoreCollection Collection(StoreComponent s) =>
            s != null && !string.IsNullOrEmpty(s.ID) && Singleton<StoreManager>.HasMain && Singleton<StoreManager>.main.allStores.TryGetValue(s.ID, out var c) ? c : null;

        // While a store is open its stock lives in the inventory (section Stock); otherwise in its collection.
        public static List<InventoryItem> Stock(StoreComponent s)
        {
            var col = Collection(s);
            if (col == null) return new List<InventoryItem>();
            var src = s.isOpened && Singleton<Inventory>.HasMain ? Singleton<Inventory>.main.allItems : col.mainItems;
            return src.Where(i => i.section == InventorySection.Stock).OrderBy(i => i.slotIndex).ToList();
        }

        public static string Describe(StoreComponent s) =>
            string.Join(",", Stock(s).Select(i => i.itemObject.data.name + "x" + i.itemObject.amount));

        public bool HasRunnerStock(StoreComponent s) => received.Contains(s) || announced.Contains(s);

        public override void Capture(Component obj, NetDataWriter w)
        {
            var s = (StoreComponent)obj;
            var stock = Stock(s);
            w.Put(stock.Count);
            foreach (var i in stock) StationItemCodec.Write(w, i.itemObject, i.itemObject.amount);
            var g = grant != null && grant.Store == s ? grant : null;
            w.Put(g != null);
            if (g == null) return;
            w.Put(g.Player);
            w.Put(g.Nonce);
            w.Put(g.Ok);
            if (g.Ok) StationItemCodec.Write(w, g.Item, g.Amount);
        }

        public override void Apply(Component obj, NetDataReader r)
        {
            var s = (StoreComponent)obj;
            NewScene();
            // The store sets itself up a few frames after the scene loads (it waits for saved stores): keep it for then.
            if (Collection(s) == null) { early[s] = r.GetRemainingBytes(); return; }
            ApplyNow(s, r);
        }

        private void ApplyNow(StoreComponent s, NetDataReader r)
        {
            int n = r.GetInt();
            var items = new List<ItemObject>();
            bool complete = true;
            for (int i = 0; i < n; i++)
            {
                var it = StationItemCodec.Read(r);
                if (it != null) items.Add(it); else complete = false;
            }
            Grant g = null;
            if (r.GetBool())
            {
                g = new Grant { Store = s, Player = r.GetByte(), Nonce = r.GetInt(), Ok = r.GetBool() };
                if (g.Ok) { g.Item = StationItemCodec.Read(r); g.Amount = g.Item?.amount ?? 0; }
            }
            // A store that restocks on every visit (not saved): the runner's fresh roll on arrival stands, as in single
            // player; what reaches it before it announced that roll is from an earlier visit (the object table, or the
            // previous runner). Once it has a stock for this visit, later states are accepted (handoff races).
            bool stale = !s.saveStore && EnemySync.IsAuthority && !received.Contains(s) && !announced.Contains(s);
            if (!stale && complete)
            {
                string before = Describe(s) + "|" + string.Join(",", Stock(s).Select(i => StationItemCodec.Key(i.itemObject)));
                SetStock(s, items);
                string after = Describe(s) + "|" + string.Join(",", Stock(s).Select(i => StationItemCodec.Key(i.itemObject)));
                received.Add(s);
                StocksApplied++;
                // Two machines briefly both running the scene (handoff): the runner re-announces what it settled on.
                if (EnemySync.IsAuthority && before != after) resend.Add(s);
            }
            if (g != null && g.Player == Requests.Me && buys.TryGetValue(g.Nonce, out var p))
            {
                buys.Remove(g.Nonce);
                if (g.Ok && g.Item != null) CompletePurchase(p, g.Item);
                else
                {
                    PurchasesDenied++;
                    Plugin.Log.LogInfo("[store] Purchase refused by the scene's runner (sold out)");
                }
            }
        }

        private static void SetStock(StoreComponent s, List<ItemObject> items)
        {
            var col = Collection(s);
            var inv = Singleton<Inventory>.main;
            bool open = s.isOpened && inv != null;
            if (open) inv.allItems.RemoveAll(i => i.section == InventorySection.Stock);
            col.mainItems.Clear();
            for (int i = 0; i < items.Count; i++) col.mainItems.Add(new InventoryItem(items[i], i, InventorySection.Stock));
            if (open)
            {
                inv.allItems.AddRange(col.mainItems);
                inv.SetInventoryChanged();
            }
        }

        // The runner gave us the item: pay and take it, as StoreManager.TradeItem does.
        private void CompletePurchase(Pending p, ItemObject item)
        {
            var col = Collection(p.Store);
            var sm = Singleton<StoreManager>.main;
            var inv = Singleton<Inventory>.main;
            item.randomValues.intAttributes.TryGetValue(ItemRandomValues.Attr.BasePrice, out int basePrice);
            float factor = (col != null ? col.sellingFactor : 1f) * sm.globalPriceFactor;
            int price = Mathf.RoundToInt(basePrice * factor);
            if (p.WholeStack) price *= item.amount;
            StationItemCodec.Give(item, p.Store.transform.position);
            inv.AddGold(-price);
            // (no extra "purchased" toast: taking the item and paying already show theirs)
            PurchasesGranted++;
            Plugin.Log.LogInfo($"[store] Bought {item.data.name} x{item.amount} for {price}");
        }

        // ---------------- intents (on the scene's runner) ----------------

        public override void OnIntent(Component obj, byte from, NetDataReader r)
        {
            var s = (StoreComponent)obj;
            byte op = r.GetByte();
            if (Collection(s) == null) return;
            if (op == OpRequest) { ObjectSync.Instance.Changed(this, s); announced.Add(s); StocksAnnounced++; return; }
            if (op != OpBuy) return;
            int nonce = r.GetInt();
            int amount = r.GetInt();
            string key = r.GetString();
            string data = r.GetString();
            var stock = Stock(s);
            var it = stock.FirstOrDefault(i => StationItemCodec.Key(i.itemObject) == key) ?? stock.FirstOrDefault(i => i.itemObject.data.addressKey == data);
            var g = new Grant { Store = s, Player = from, Nonce = nonce };
            if (it != null && amount > 0)
            {
                int take = Mathf.Min(amount, it.itemObject.amount);
                g.Ok = true;
                g.Item = new ItemObject(it.itemObject) { amount = take };
                g.Amount = take;
                if (it.itemObject.amount > take) it.itemObject.amount -= take;
                else
                {
                    Collection(s).mainItems.Remove(it);
                    if (s.isOpened) Singleton<Inventory>.main.allItems.Remove(it);
                }
                if (s.isOpened) Singleton<Inventory>.main.SetInventoryChanged();
            }
            Plugin.Log.LogInfo($"[store] #{from} buys {data} x{amount}: {(g.Ok ? "ok" : "sold out")}");
            grant = g;
            try { ObjectSync.Instance.Changed(this, s); }
            finally { grant = null; }
        }

        // ---------------- hooks ----------------

        // Not the runner: buying from the stock asks the runner instead.
        public bool InterceptBuy(InventoryItem item, bool wholeStack)
        {
            var s = Object.FindObjectsByType<StoreComponent>(FindObjectsSortMode.None).FirstOrDefault(x => x.isOpened);
            if (s == null || item?.itemObject == null) return false;
            var col = Collection(s);
            item.itemObject.randomValues.intAttributes.TryGetValue(ItemRandomValues.Attr.BasePrice, out int basePrice);
            int price = Mathf.RoundToInt(basePrice * (col != null ? col.sellingFactor : 1f) * Singleton<StoreManager>.main.globalPriceFactor);
            if (wholeStack) price *= item.itemObject.amount;
            if (!Singleton<Inventory>.main.HasEnoughGold(price)) return false;   // the game says "not enough gold"
            int nonce = Requests.NewNonce();
            buys[nonce] = new Pending { Store = s, WholeStack = wholeStack };
            int amount = wholeStack ? item.itemObject.amount : 1;
            string key = StationItemCodec.Key(item.itemObject), data = item.itemObject.data.addressKey;
            ObjectSync.Instance.SendIntent(this, s, w => { w.Put(OpBuy); w.Put(nonce); w.Put(amount); w.Put(key); w.Put(data); });
            Purchases++;
            return true;
        }

        // Not the runner and its stock isn't here yet: ask for it, open when it arrives.
        public bool HoldOpen(StoreComponent s)
        {
            if (OpeningForReal || HasRunnerStock(s)) return false;
            waitingOpen = s;
            waitingSince = Time.realtimeSinceStartup;
            ObjectSync.Instance.SendIntent(this, s, w => w.Put(OpRequest));
            return true;
        }

        public void OnSetup(StoreComponent s)
        {
            NewScene();
            setupAt[s] = Time.realtimeSinceStartup;
            if (early.TryGetValue(s, out var bytes))
            {
                early.Remove(s);
                ObjectSync.Applying = true;
                try { ApplyNow(s, new NetDataReader(bytes)); }
                finally { ObjectSync.Applying = false; }
            }
        }

        private void NewScene()
        {
            int h = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (h == sceneHandle) return;
            sceneHandle = h;
            received.Clear(); announced.Clear(); resend.Clear(); early.Clear(); setupAt.Clear();
            waitingOpen = null;
        }

        private static void TickStatic() => Instance?.Tick();

        private void Tick()
        {
            NewScene();
            float now = Time.realtimeSinceStartup;
            var os = ObjectSync.Instance;
            if (EnemySync.IsAuthority)
            {
                // Our stock is the scene's: announce each store once it's set up (unless someone else's arrived first).
                foreach (var kv in setupAt.ToList())
                {
                    var s = kv.Key;
                    if (s == null || received.Contains(s) || announced.Contains(s) || now - kv.Value < 1f || Collection(s) == null) continue;
                    announced.Add(s);
                    os.Changed(this, s);
                    StocksAnnounced++;
                }
                foreach (var s in resend.ToList()) if (s != null) os.Changed(this, s);
                resend.Clear();
            }
            if (waitingOpen != null && (HasRunnerStock(waitingOpen) || now - waitingSince > 4f))
            {
                var s = waitingOpen;
                waitingOpen = null;
                if (!HasRunnerStock(s)) Plugin.Log.LogWarning("[store] No stock from the scene's runner; opening with ours");
                OpeningForReal = true;
                try { s.OpenStore(); }
                finally { OpeningForReal = false; }
            }
        }

        // Our own (runner's) purchase changed the stock.
        public void OnLocalTrade(InventorySection from)
        {
            if (from != InventorySection.Stock && from != InventorySection.Buyback) return;
            var s = Object.FindObjectsByType<StoreComponent>(FindObjectsSortMode.None).FirstOrDefault(x => x.isOpened);
            if (s != null && from == InventorySection.Stock) ObjectSync.Instance.Changed(this, s);
        }
    }

    [HarmonyPatch(typeof(StoreComponent), "Setup")]
    internal static class StoreSetupPatch
    {
        private static void Postfix(StoreComponent __instance)
        {
            if (ObjectSync.Active) StoreSync.Instance?.OnSetup(__instance);
        }
    }

    [HarmonyPatch(typeof(StoreComponent), nameof(StoreComponent.OpenStore))]
    internal static class StoreOpenPatch
    {
        private static bool Prefix(StoreComponent __instance)
        {
            if (__instance.isOpened || !ObjectSync.Active || ObjectSync.RunsScene || StoreSync.Instance == null) return true;
            return !StoreSync.Instance.HoldOpen(__instance);
        }
    }

    [HarmonyPatch(typeof(StoreManager), nameof(StoreManager.TradeItem))]
    internal static class StoreTradePatch
    {
        private static bool Prefix(InventoryItem item, InventorySection fromSection, bool wholeStack)
        {
            if (fromSection != InventorySection.Stock || !ObjectSync.Active || ObjectSync.RunsScene || StoreSync.Instance == null) return true;
            return !StoreSync.Instance.InterceptBuy(item, wholeStack);
        }

        private static void Postfix(InventorySection fromSection)
        {
            if (ObjectSync.Active && EnemySync.IsAuthority) StoreSync.Instance?.OnLocalTrade(fromSection);
        }
    }
}
