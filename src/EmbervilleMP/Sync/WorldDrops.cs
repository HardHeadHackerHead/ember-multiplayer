using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync
{
    // Items lying in the world (ItemDropHandler.DropToWorld: enemy loot, breakables, gathering, chests dropped,
    // items a player throws away) with shared loot. Instanced loot leaves all of this alone (drops are local), except
    // items a player throws away from its inventory (PlayerDrop): those are shared in both modes, so a teammate can
    // pick them up (the dropper's inventory already lost them; the runner gives each to one player).
    //
    //   - The scene's runner makes the drops: its own (enemy deaths run there) and the ones other players cause
    //     (they send LootDropRequest with the rolled items instead of dropping them). It gives each one a network id
    //     and sends LootSpawn (item, from, to): every copy shows the same item land in the same place.
    //   - Picking one up asks the runner (LootPickup 0); it decides who gets it (LootPickup 1: taker, or 0 = gone);
    //     the taker adds it to its own inventory, everyone else sees it vanish.
    //   - Arriving in a scene asks for the drops lying there (LootPickup 2).
    internal class WorldDrops
    {
        public static WorldDrops Instance { get; private set; }
        public int Spawned, Shown, Requests, RequestsServed, Granted, Vanished;

        private readonly Session session;
        private readonly Dictionary<int, ItemDropObject> drops = new Dictionary<int, ItemDropObject>();
        private readonly Dictionary<ItemDropObject, int> idOf = new Dictionary<ItemDropObject, int>();
        private readonly Dictionary<int, float> pendingTake = new Dictionary<int, float>();
        // breakables already dropped (by us or on request), so one broken pot drops once even if several copies break it
        private readonly Dictionary<int, float> sourceDropped = new Dictionary<int, float>();
        private int sceneHandle = -1, nextId;
        private float askAgainAt = -1f;

        // set by the patches below
        internal static bool Spawning;          // we're showing a drop the runner announced
        internal static bool AnnounceNext;      // the drop being made here gets an id and is announced
        internal static ItemDropper Dropper;    // the dropper whose Drop/DropItem is running
        internal static bool SuppressEvent;     // this dropper event was already dropped elsewhere
        internal static List<(ItemObject item, int amount, Vector3 pos, float radius, float rad)> Batch;
        internal static int BatchSource;
        // An item our player throws away (inventory Drop, unequipped to the bin, what didn't fit when picking up a
        // network drop): goes through the runner whatever the loot mode.
        internal static bool PlayerDrop;

        private const byte AskTake = 0, Taken = 1, AskDrops = 2;

        public WorldDrops(Session session)
        {
            Instance = this;
            this.session = session;
            session.On(Msg.LootSpawn, OnSpawn);
            session.On(Msg.LootDropRequest, OnRequest);
            session.On(Msg.LootPickup, OnPickup);
        }

        private static bool Runner => EnemySync.IsAuthority;

        public IEnumerable<KeyValuePair<int, ItemDropObject>> Live => drops.Where(kv => kv.Value != null && kv.Value.gameObject.activeSelf);
        public bool IsNetworked(ItemDropObject d, out int id) => idOf.TryGetValue(d, out id);

        public void Update()
        {
            if (!session.Ready || !Game.InGame) return;
            int handle = SceneManager.GetActiveScene().handle;
            if (handle != sceneHandle)
            {
                // New scene: the old drops went with it.
                sceneHandle = handle;
                drops.Clear();
                idOf.Clear();
                pendingTake.Clear();
                sourceDropped.Clear();
                AskForDrops();   // (instanced loot too: items teammates threw away may lie there)
                askAgainAt = Time.realtimeSinceStartup + 3f;
            }
            if (askAgainAt > 0f && Time.realtimeSinceStartup > askAgainAt)
            {
                askAgainAt = -1f;
                AskForDrops();
            }
            // No answer from the runner: put the drop back.
            if (pendingTake.Count > 0)
                foreach (var kv in pendingTake.Where(kv => Time.realtimeSinceStartup - kv.Value > 4f).ToList())
                {
                    pendingTake.Remove(kv.Key);
                    if (drops.TryGetValue(kv.Key, out var d) && d != null) { RestoreArt(d); d.isReady = true; }
                }
        }

        private void AskForDrops()
        {
            if (Runner) return;
            string scene = Game.SceneName;
            session.Send(Msg.LootPickup, w => { w.Put(AskDrops); w.Put(scene); w.Put(0); w.Put((byte)0); });
        }

        // ---------------- making drops ----------------

        // ItemDropHandler.DropToWorld is about to run here. False: don't drop it here.
        public bool BeforeDrop(Vector3 position, ItemObject item, int amount, float radius, float rad)
        {
            if (Spawning || !(LootRules.Shared || PlayerDrop) || !session.Ready || !Game.InGame) return true;
            if (Runner)
            {
                if (SuppressEvent) return false;
                AnnounceNext = true;
                return true;
            }
            // Enemy deaths run on the runner too: its loot is the loot.
            if (Dropper != null && Dropper.GetComponentInParent<Enemy>(true) != null) return false;
            // Anything else we caused (our gathering, a pot we broke, an item we threw away): the runner drops it.
            var copy = new ItemObject(item) { amount = amount };
            if (Batch != null) Batch.Add((copy, amount, position, radius, rad));
            else SendRequest(0, new List<(ItemObject, int, Vector3, float, float)> { (copy, amount, position, radius, rad) });
            return false;
        }

        internal void SendRequest(int source, List<(ItemObject item, int amount, Vector3 pos, float radius, float rad)> items)
        {
            if (items.Count == 0) return;
            string scene = Game.SceneName;
            session.Send(Msg.LootDropRequest, w =>
            {
                w.Put(scene);
                w.Put(source);
                w.Put(items.Count);
                foreach (var x in items)
                {
                    w.Put(ItemCodec.EncodeOne(x.item));
                    w.Put(x.amount);
                    w.Put(x.pos.x); w.Put(x.pos.y); w.Put(x.pos.z);
                    w.Put(x.radius);
                    w.Put(x.rad);
                }
            });
            Requests++;
        }

        private void OnRequest(byte from, NetDataReader r)
        {
            string scene = r.GetString();
            int source = r.GetInt();
            int n = r.GetInt();
            if (scene != Game.SceneName || !Game.InGame || !Runner) return;
            // Instanced loot: only thrown-away items come here (source 0); breakables and gathering drop locally.
            if (!LootRules.Shared && source != 0) return;
            if (source != 0 && !FirstDrop(source)) return;
            var handler = Singleton<ItemDropHandler>.HasMain ? Singleton<ItemDropHandler>.main : null;
            bool was = PlayerDrop;
            PlayerDrop = true;   // announced like our own drops, in either mode
            try
            {
                for (int i = 0; i < n; i++)
                {
                    string json = r.GetString();
                    int amount = r.GetInt();
                    var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    float radius = r.GetFloat(), rad = r.GetFloat();
                    var item = ItemCodec.DecodeOne(json);
                    if (item == null || handler == null) continue;
                    handler.DropToWorld(pos, item, amount, radius, rad);
                }
            }
            finally { PlayerDrop = was; }
            RequestsServed++;
        }

        // Breakables: true the first time this source drops (within a few seconds).
        internal bool FirstDrop(int source)
        {
            float now = Time.realtimeSinceStartup;
            if (sourceDropped.TryGetValue(source, out float t) && now - t < 5f) return false;
            sourceDropped[source] = now;
            return true;
        }

        // ItemDropHandler.MoveToPosition: a drop is starting its flight here.
        public void OnMove(ItemDropObject drop, Vector2 from, Vector2 to)
        {
            if (Spawning) return;
            Unregister(drop);   // pooled objects come back for new drops
            if (!AnnounceNext) return;
            AnnounceNext = false;
            int id = (session.LocalId << 24) | (++nextId & 0xFFFFFF);
            Register(id, drop);
            string scene = Game.SceneName, json = ItemCodec.EncodeOne(drop.itemObj);
            session.Send(Msg.LootSpawn, w =>
            {
                w.Put(scene);
                w.Put(1);
                PutDrop(w, id, json, from, to);
            });
            Spawned++;
        }

        private static void PutDrop(NetDataWriter w, int id, string json, Vector2 from, Vector2 to)
        {
            w.Put(id);
            w.Put(json);
            w.Put(from.x); w.Put(from.y);
            w.Put(to.x); w.Put(to.y);
        }

        private void OnSpawn(byte from, NetDataReader r)
        {
            if (!Senders.IsRunner(from)) return;   // drops are made by the scene's runner only
            string scene = r.GetString();
            int n = r.GetInt();
            bool here = scene == Game.SceneName && Game.InGame;
            var handler = Singleton<ItemDropHandler>.HasMain ? Singleton<ItemDropHandler>.main : null;
            for (int i = 0; i < n; i++)
            {
                int id = r.GetInt();
                string json = r.GetString();
                var a = new Vector2(r.GetFloat(), r.GetFloat());
                var b = new Vector2(r.GetFloat(), r.GetFloat());
                if (!here || handler == null || drops.ContainsKey(id)) continue;
                var item = ItemCodec.DecodeOne(json);
                if (item == null) continue;
                Spawning = true;
                try
                {
                    var drop = PoolManager.Get(handler.itemDropObjectPrefab)?.GetComponent<ItemDropObject>();
                    if (drop == null) continue;
                    Unregister(drop);
                    RestoreArt(drop);
                    drop.Setup(item);
                    Register(id, drop);
                    handler.MoveToPosition(drop, a, b).Forget();
                    Shown++;
                }
                finally { Spawning = false; }
            }
        }

        // ---------------- picking up ----------------

        // ItemDropObject.OnInteract by our player. False: the runner decides first. Network drops are all drops with
        // shared loot, and thrown-away items with instanced loot (any other drop is ours alone).
        public bool BeforePickup(ItemDropObject d)
        {
            if (!d.gameObject.activeSelf) return false;   // just taken by someone else (the interaction list can lag a frame)
            if (!idOf.TryGetValue(d, out int id)) return true;
            string scene = Game.SceneName;
            if (Runner)
            {
                Unregister(d);
                session.Send(Msg.LootPickup, w => { w.Put(Taken); w.Put(scene); w.Put(id); w.Put(session.LocalId); });
                Granted++;
                PlayerDrop = true;   // what doesn't fit drops again for everyone (DropPickupPatch puts the flag back)
                return true;
            }
            if (pendingTake.ContainsKey(id)) return false;
            pendingTake[id] = Time.realtimeSinceStartup;
            d.isReady = false;
            if (d.art != null) d.art.SetActive(false);
            if (d.titlePivot != null) d.titlePivot.SetActive(false);
            session.Send(Msg.LootPickup, w => { w.Put(AskTake); w.Put(scene); w.Put(id); w.Put((byte)0); });
            return false;
        }

        private void OnPickup(byte from, NetDataReader r)
        {
            byte op = r.GetByte();
            string scene = r.GetString();
            int id = r.GetInt();
            byte taker = r.GetByte();
            if (scene != Game.SceneName || !Game.InGame) return;
            if (op == AskDrops)
            {
                if (!Runner) return;
                var live = Live.ToList();
                if (live.Count == 0) return;
                session.Send(Msg.LootSpawn, w =>
                {
                    w.Put(scene);
                    w.Put(live.Count);
                    foreach (var kv in live) PutDrop(w, kv.Key, ItemCodec.EncodeOne(kv.Value.itemObj), kv.Value.transform.position, kv.Value.transform.position);
                });
                return;
            }
            if (op == AskTake)
            {
                if (!Runner) return;
                byte got = 0;
                if (drops.TryGetValue(id, out var d) && d != null && d.gameObject.activeSelf)
                {
                    got = from;
                    Unregister(d);
                    Vanish(d);
                    Granted++;
                }
                session.Send(Msg.LootPickup, w => { w.Put(Taken); w.Put(scene); w.Put(id); w.Put(got); });
                return;
            }
            // Taken: the runner's decision (nobody else's).
            if (!Senders.IsRunner(from)) return;
            bool asked = pendingTake.Remove(id);
            if (!drops.TryGetValue(id, out var drop) || drop == null) return;
            Unregister(drop);
            if (taker == session.LocalId && asked && Game.LocalPlayer != null)
            {
                RestoreArt(drop);
                bool was = PlayerDrop;
                PlayerDrop = true;   // what doesn't fit drops again for everyone
                try { drop.OnInteract(Game.LocalPlayer.gameObject); }   // the game's own pickup (no longer networked: runs as is)
                finally { PlayerDrop = was; }
            }
            else Vanish(drop);
        }

        // Gone for us: someone else has it.
        private void Vanish(ItemDropObject d)
        {
            RestoreArt(d);
            d.activeVfx.RemoveAll(e => { if (e != null) e.SetActive(false); return true; });
            if (d.vfxCollectPrefab != null)
            {
                var vfx = PoolManager.Get(d.vfxCollectPrefab);
                if (vfx != null)
                {
                    vfx.transform.position = d.transform.position;
                    vfx.SetActive(true);
                    Utils.TimeoutDeltaTime(1f, () => vfx.SetActive(false));
                }
            }
            d.gameObject.SetActive(false);
            Vanished++;
        }

        private static void RestoreArt(ItemDropObject d)
        {
            if (d.art != null && !d.art.activeSelf) d.art.SetActive(true);
        }

        private void Register(int id, ItemDropObject d) { drops[id] = d; idOf[d] = id; }

        private void Unregister(ItemDropObject d)
        {
            if (d != null && idOf.TryGetValue(d, out int id)) { idOf.Remove(d); drops.Remove(id); }
        }
    }

    // ---------------- patches ----------------

    [HarmonyPatch(typeof(ItemDropHandler), nameof(ItemDropHandler.DropToWorld))]
    internal static class DropToWorldPatch
    {
        private static bool Prefix(Vector3 position, ItemObject itemObj, int stackAmount, float radius, float rad) =>
            WorldDrops.Instance == null || WorldDrops.Instance.BeforeDrop(position, itemObj, stackAmount, radius, rad);

        private static void Postfix() => WorldDrops.AnnounceNext = false;
    }

    [HarmonyPatch(typeof(ItemDropHandler), nameof(ItemDropHandler.MoveToPosition))]
    internal static class DropMovePatch
    {
        private static void Prefix(ItemDropObject drop, Vector2 from, Vector2 to) => WorldDrops.Instance?.OnMove(drop, from, to);
    }

    [HarmonyPatch(typeof(ItemDropObject), nameof(ItemDropObject.OnInteract))]
    internal static class DropPickupPatch
    {
        private static bool Prefix(ItemDropObject __instance, out bool __state)
        {
            __state = WorldDrops.PlayerDrop;
            return WorldDrops.Instance == null || WorldDrops.Instance.BeforePickup(__instance);
        }

        private static System.Exception Finalizer(System.Exception __exception, bool __state)
        {
            WorldDrops.PlayerDrop = __state;
            return __exception;
        }
    }

    // Items our player throws away: the inventory's Drop (main, belt, an open chest) and gear unequipped to the bin.
    [HarmonyPatch]
    internal static class PlayerDropPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Inventory), nameof(Inventory.DisposeItem));
            yield return AccessTools.Method(typeof(Inventory), nameof(Inventory.TryToUnequip));
        }

        private static void Prefix(out bool __state)
        {
            __state = WorldDrops.PlayerDrop;
            WorldDrops.PlayerDrop = true;
        }

        private static System.Exception Finalizer(System.Exception __exception, bool __state)
        {
            WorldDrops.PlayerDrop = __state;
            return __exception;
        }
    }

    // Which dropper is dropping (enemy loot vs. the rest), and one request per drop event when someone else runs the scene.
    [HarmonyPatch]
    internal static class DropperPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ItemDropper), nameof(ItemDropper.Drop));
            yield return AccessTools.Method(typeof(ItemDropper), nameof(ItemDropper.TryDrop));
            foreach (var m in typeof(ItemDropper).GetMethods().Where(m => m.Name == nameof(ItemDropper.DropItem) && m.DeclaringType == typeof(ItemDropper)))
                yield return m;
        }

        private static void Prefix(ItemDropper __instance, out bool __state)
        {
            __state = WorldDrops.Dropper == null;   // outermost call (Drop calls DropItem)
            if (!__state) return;
            WorldDrops.Dropper = __instance;
            var wd = WorldDrops.Instance;
            if (wd == null || !LootRules.Shared) return;
            // Breakables (pots, bushes) may break on several copies: the first drop counts.
            int source = !NetIds.IsRuntimeSpawned(__instance.transform) && __instance.GetComponentInParent<DamageableObject>(true) != null
                ? NetIds.Hash(NetIds.ScenePath(__instance.transform)) : 0;
            if (EnemySync.IsAuthority) WorldDrops.SuppressEvent = source != 0 && !wd.FirstDrop(source);
            else
            {
                WorldDrops.Batch = new List<(ItemObject, int, Vector3, float, float)>();
                WorldDrops.BatchSource = source;
            }
        }

        private static System.Exception Finalizer(System.Exception __exception, bool __state)
        {
            if (!__state) return __exception;
            var batch = WorldDrops.Batch;
            WorldDrops.Batch = null;
            WorldDrops.Dropper = null;
            WorldDrops.SuppressEvent = false;
            if (batch != null && batch.Count > 0) WorldDrops.Instance?.SendRequest(WorldDrops.BatchSource, batch);
            return __exception;
        }
    }
}
