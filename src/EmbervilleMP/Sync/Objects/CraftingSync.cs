using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // Crafting stations (CraftingStation, CraftingManager.Create): one queue per station, run by the scene's runner.
    //   - Enqueue: the player's own machine checks and consumes the ingredients (the game's TryToEnqueue); a guest
    //     then asks the runner to queue it (with its own crafting time: mastery speeds it up).
    //   - The runner's timer finishes recipes; the crafting XP goes to whoever queued it.
    //   - Collecting (E on a station with results, or double click in the window) is an intent: the runner hands the
    //     items to the player who collects; they land in that player's bag.
    //   - Others' timers run locally for the progress display but never finish a recipe themselves.
    internal class CraftingSync : ObjectHandler
    {
        public static CraftingSync Instance { get; private set; }
        public CraftingSync()
        {
            Instance = this;
            StationsTicker.Ensure();
            StationsTicker.Tick -= TickStatic;
            StationsTicker.Tick += TickStatic;
        }

        public override string Kind => "craft";
        public int EnqueueIntents, CollectIntents, Granted, XpGiven, RemoteEnqueued;

        private const byte OpEnqueue = 1, OpCollectAll = 2, OpCollectOne = 3;

        private class Info
        {
            public readonly List<byte> Owners = new List<byte>();   // who queued each entry of the demand queue
            public int FinishSeq;
            public byte FinishOwner;
            public string FinishRecipe = "";
            public int SeenFinishSeq = -1;
        }

        private class Grant { public CraftingStation Station; public byte Player; public int Nonce; public List<ItemObject> Items; }

        private readonly Dictionary<CraftingStation, Info> info = new Dictionary<CraftingStation, Info>();
        private readonly HashSet<int> collects = new HashSet<int>();
        private Grant grant;
        private float nextRefresh;
        internal static bool SuppressXp;

        private Info Of(CraftingStation s)
        {
            if (!info.TryGetValue(s, out var i)) info[s] = i = new Info();
            return i;
        }

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<CraftingStation>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w)
        {
            var s = (CraftingStation)obj;
            var inf = Of(s);
            var q = s.Demand.ToList();
            w.Put(q.Count);
            for (int i = 0; i < q.Count; i++)
            {
                w.Put(q[i].recipe != null ? q[i].recipe.RecipeID : "");
                w.Put(q[i].timer);
                w.Put(i < inf.Owners.Count ? inf.Owners[i] : Requests.Me);
            }
            w.Put(s.CreatedItems.Count);
            foreach (var it in s.CreatedItems) StationItemCodec.Write(w, it.itemObject, it.itemObject.amount);
            w.Put(inf.FinishSeq);
            w.Put(inf.FinishOwner);
            w.Put(inf.FinishRecipe);
            var g = grant != null && grant.Station == s ? grant : null;
            w.Put(g != null);
            if (g == null) return;
            w.Put(g.Player);
            w.Put(g.Nonce);
            w.Put(g.Items.Count);
            foreach (var it in g.Items) StationItemCodec.Write(w, it, it.amount);
        }

        public override void Apply(Component obj, NetDataReader r)
        {
            var s = (CraftingStation)obj;
            var inf = Of(s);
            int n = r.GetInt();
            var demand = new Queue<EnqueuedRecipe>();
            inf.Owners.Clear();
            for (int i = 0; i < n; i++)
            {
                string id = r.GetString();
                float timer = r.GetFloat();
                byte owner = r.GetByte();
                var recipe = s.availableRecipes.Find(x => x.RecipeID == id);
                if (recipe == null) continue;
                demand.Enqueue(new EnqueuedRecipe { recipe = recipe, timer = timer });
                inf.Owners.Add(owner);
            }
            int c = r.GetInt();
            var created = new List<InventoryItem>();
            for (int i = 0; i < c; i++)
            {
                var it = StationItemCodec.Read(r);
                if (it != null) created.Add(new InventoryItem(it));
            }
            int seq = r.GetInt();
            byte finOwner = r.GetByte();
            string finRecipe = r.GetString();
            Grant g = null;
            if (r.GetBool())
            {
                g = new Grant { Station = s, Player = r.GetByte(), Nonce = r.GetInt(), Items = new List<ItemObject>() };
                int k = r.GetInt();
                for (int i = 0; i < k; i++) { var it = StationItemCodec.Read(r); if (it != null) g.Items.Add(it); }
            }

            s.demand = demand;
            s.createdItems = created;
            s.UpdateVisuals();
            // A recipe we queued finished on the runner: our crafting XP (once; not for finishes from before we came).
            if (seq != inf.SeenFinishSeq)
            {
                if (inf.SeenFinishSeq >= 0 && seq > inf.SeenFinishSeq && finOwner == Requests.Me)
                {
                    var recipe = s.availableRecipes.Find(x => x.RecipeID == finRecipe);
                    if (recipe != null && GameData.HasPlayerData)
                    {
                        GameData.playerData.AddXP(recipe.RequiredCategory, recipe.GetEarnedXP);
                        XpGiven++;
                    }
                }
                inf.SeenFinishSeq = seq;
            }
            inf.FinishSeq = seq; inf.FinishOwner = finOwner; inf.FinishRecipe = finRecipe;
            if (g != null && g.Player == Requests.Me && collects.Remove(g.Nonce))
                foreach (var it in g.Items) { StationItemCodec.Give(it, s.transform.position); Granted++; }
            RefreshWindow(s);
        }

        private static void RefreshWindow(CraftingStation s)
        {
            if (!Singleton<CraftingManager>.HasMain) return;
            var cm = Singleton<CraftingManager>.main;
            if (cm.activeStation != s || !cm.panelCrafting.activeInHierarchy) return;
            try
            {
                cm.UpdateQueue();
                cm.UpdateRecipes(resetSelected: false);
                cm.UpdateIngredientsList(cm.selectedRecipe);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[craft] Window refresh: " + e.Message); }
        }

        // ---------------- intents (on the scene's runner) ----------------

        public override void OnIntent(Component obj, byte from, NetDataReader r)
        {
            var s = (CraftingStation)obj;
            var inf = Of(s);
            byte op = r.GetByte();
            if (op == OpEnqueue)
            {
                string id = r.GetString();
                float time = r.GetFloat();
                var recipe = s.availableRecipes.Find(x => x.RecipeID == id);
                // The guest already paid (its own TryToEnqueue); a full queue here means two players filled it at once.
                if (recipe == null || s.Demand.Count >= s.MaxStack)
                {
                    Plugin.Log.LogWarning($"[craft] #{from}'s {id} refused (queue {s.Demand.Count}/{s.MaxStack})");
                    ObjectSync.Instance.Changed(this, s);
                    return;
                }
                SyncOwners(s);
                s.demand.Enqueue(new EnqueuedRecipe { recipe = recipe, timer = time > 0f ? time : recipe.CraftingTime });
                inf.Owners.Add(from);
                s.UpdateVisuals();
                RemoteEnqueued++;
                RefreshWindow(s);
                ObjectSync.Instance.Changed(this, s);
                return;
            }
            int nonce = r.GetInt();
            var items = new List<ItemObject>();
            if (op == OpCollectAll)
            {
                items.AddRange(s.CreatedItems.Select(i => i.itemObject));
                s.createdItems.Clear();
            }
            else if (op == OpCollectOne)
            {
                string key = r.GetString();
                var it = s.CreatedItems.FirstOrDefault(i => StationItemCodec.Key(i.itemObject) == key);
                if (it != null) { items.Add(it.itemObject); s.createdItems.Remove(it); }
            }
            s.UpdateVisuals();
            s.OnCollect?.Invoke();
            RefreshWindow(s);
            grant = new Grant { Station = s, Player = from, Nonce = nonce, Items = items };
            try { ObjectSync.Instance.Changed(this, s); }
            finally { grant = null; }
        }

        // Owner list in step with the queue (entries loaded from a save are the runner's).
        private void SyncOwners(CraftingStation s)
        {
            var o = Of(s).Owners;
            while (o.Count < s.Demand.Count) o.Add(Requests.Me);
            while (o.Count > s.Demand.Count) o.RemoveAt(o.Count - 1);
        }

        // ---------------- hooks ----------------

        public void OnLocalEnqueue(CraftingStation s, CraftingRecipe recipe)
        {
            var inf = Of(s);
            if (ObjectSync.RunsScene)
            {
                // TryToEnqueue already added it: the owner of the newest entry is us.
                while (inf.Owners.Count < s.Demand.Count - 1) inf.Owners.Add(Requests.Me);
                inf.Owners.Add(Requests.Me);
                ObjectSync.Instance.Changed(this, s);
                return;
            }
            inf.Owners.Add(Requests.Me);
            float time = recipe.CraftingTime;
            ObjectSync.Instance.SendIntent(this, s, w => { w.Put(OpEnqueue); w.Put(recipe.RecipeID); w.Put(time); });
            EnqueueIntents++;
        }

        public void RequestCollect(CraftingStation s, InventoryItem one)
        {
            int nonce = Requests.NewNonce();
            collects.Add(nonce);
            string key = one != null ? StationItemCodec.Key(one.itemObject) : "";
            ObjectSync.Instance.SendIntent(this, s, w =>
            {
                w.Put(one != null ? OpCollectOne : OpCollectAll);
                w.Put(nonce);
                if (one != null) w.Put(key);
            });
            CollectIntents++;
        }

        // The runner finished a recipe: whose was it.
        public void OnFinishing(CraftingStation s, EnqueuedRecipe e)
        {
            var inf = Of(s);
            byte owner = inf.Owners.Count > 0 ? inf.Owners[0] : Requests.Me;
            if (inf.Owners.Count > 0) inf.Owners.RemoveAt(0);
            inf.FinishSeq++;
            inf.FinishOwner = owner;
            inf.FinishRecipe = e?.recipe != null ? e.recipe.RecipeID : "";
            SuppressXp = owner != Requests.Me;   // theirs: they get the XP when they see it finish
        }

        public void OnFinished(CraftingStation s)
        {
            SuppressXp = false;
            ObjectSync.Instance.Changed(this, s);
        }

        public void OnLocalCollect(CraftingStation s) => ObjectSync.Instance.Changed(this, s);

        private static void TickStatic() => Instance?.Tick();

        // The runner refreshes busy stations now and then, so arrivals (who get the last known state) see the
        // real timer, and everyone's display stays close.
        private void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextRefresh || !EnemySync.IsAuthority) return;
            nextRefresh = now + 5f;
            foreach (var s in Object.FindObjectsByType<CraftingStation>(FindObjectsSortMode.None))
                if (s.Demand.Count > 0) ObjectSync.Instance.Changed(this, s);
        }
    }

    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.TryToEnqueue))]
    internal static class CraftEnqueuePatch
    {
        private static void Postfix(CraftingStation __instance, CraftingRecipe recipe, bool __result)
        {
            if (__result && ObjectSync.Active && !ObjectSync.Applying) CraftingSync.Instance?.OnLocalEnqueue(__instance, recipe);
        }
    }

    // Not the runner: our timer never finishes a recipe (the runner's does, and tells us).
    [HarmonyPatch(typeof(CraftingStation), "Update")]
    internal static class CraftUpdatePatch
    {
        private static bool Prefix(CraftingStation __instance)
        {
            if (!ObjectSync.Active || ObjectSync.RunsScene) return true;
            var p = __instance.producing;
            return !(__instance.isOn.Value && p != null && p.timer - Time.deltaTime <= 0f);
        }
    }

    [HarmonyPatch(typeof(CraftingStation), "Finish")]
    internal static class CraftFinishPatch
    {
        private static void Prefix(CraftingStation __instance, EnqueuedRecipe enqRec)
        {
            if (ObjectSync.Active && ObjectSync.RunsScene) CraftingSync.Instance?.OnFinishing(__instance, enqRec);
        }

        private static void Postfix(CraftingStation __instance)
        {
            if (ObjectSync.Active && ObjectSync.RunsScene) CraftingSync.Instance?.OnFinished(__instance);
            CraftingSync.SuppressXp = false;
        }
    }

    [HarmonyPatch(typeof(CraftingStation), "GiveXP")]
    internal static class CraftXpPatch
    {
        private static bool Prefix() => !CraftingSync.SuppressXp;
    }

    [HarmonyPatch(typeof(CraftingStation), "ForceCollect")]
    internal static class CraftForceCollectPatch
    {
        private static bool Prefix(CraftingStation __instance)
        {
            if (!ObjectSync.Active || ObjectSync.RunsScene || CraftingSync.Instance == null) return true;
            CraftingSync.Instance.RequestCollect(__instance, null);
            return false;
        }

        private static void Postfix(CraftingStation __instance)
        {
            if (ObjectSync.Active && ObjectSync.RunsScene) CraftingSync.Instance?.OnLocalCollect(__instance);
        }
    }

    // The window's double click on one result.
    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.CollectItem))]
    internal static class CraftCollectOnePatch
    {
        private static bool Prefix(CraftingStation __instance, InventoryItem item, bool removeItem)
        {
            if (!removeItem || !ObjectSync.Active || ObjectSync.RunsScene || CraftingSync.Instance == null) return true;
            CraftingSync.Instance.RequestCollect(__instance, item);
            return false;
        }

        private static void Postfix(CraftingStation __instance, bool removeItem)
        {
            if (removeItem && ObjectSync.Active && ObjectSync.RunsScene) CraftingSync.Instance?.OnLocalCollect(__instance);
        }
    }
}
