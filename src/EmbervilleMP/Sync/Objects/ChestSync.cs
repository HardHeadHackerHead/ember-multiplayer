using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // Chests. The game keeps a chest's contents in ChestManager (rolled with unseeded randomness, chests without an id
    // get a random GUID per machine), and the inventory UI works on that list while the chest is open.
    //
    // Shared loot: the scene's runner owns every chest. Opening is an intent: the runner lets one player at a time
    // have a chest open (lock) and sends its contents with the grant; that player takes/puts items with the game's
    // own UI, and every change it makes goes to the runner as it happens (what's in the chest now), so a holder
    // that drops out never leaves the runner with contents it already took. Closing sends the final contents and
    // frees the chest. State = (opened before, who has it open, contents).
    //
    // Instanced loot: nothing is synced. A guest rolls its own contents (the host's saved ones are the host's), and
    // what it leaves in a chest it opened is kept by the host (LootRules), so the chest stays looted for that guest.
    //
    // Storage chests (the world's stash: StashSync.IsStorage) are none of this: StashSync, in both loot modes.
    internal class ChestSync : ObjectHandler
    {
        public static ChestSync Instance { get; private set; }
        public ChestSync() { Instance = this; }

        public override string Kind => "chest";

        // The chest our player has open (shared mode).
        public static Chest Held;
        // Who has each chest open (the runner decides; everyone mirrors it so a new runner knows).
        private readonly Dictionary<Chest, byte> holder = new Dictionary<Chest, byte>();
        // Our open requests waiting for the runner.
        private readonly Dictionary<Chest, float> asked = new Dictionary<Chest, float>();
        // States for chests whose contents aren't set up yet (Setup waits for ChestManager, inactive rooms wait to start).
        private readonly Dictionary<Chest, (bool opened, byte who, string json)> waiting = new Dictionary<Chest, (bool, byte, string)>();
        public int Grants, Denials, LiveEdits;
        // What our open chest held when we last told the runner (shared mode).
        private string heldSig;

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<Chest>(FindObjectsInactive.Include, FindObjectsSortMode.None).Where(c => !StashSync.IsStorage(c));

        public override void Init(Session session)
        {
            // Whoever had a chest open and left frees it (what it took already reached the runner).
            session.PlayerLeft += p =>
            {
                foreach (var c in holder.Where(kv => kv.Value == p.Id).Select(kv => kv.Key).ToList())
                {
                    holder[c] = 0;
                    if (c != null && EnemySync.IsAuthority && ObjectSync.Active) ObjectSync.Instance.Changed(this, c);
                }
            };
        }

        // Our player has a chest open (shared mode): each change to it goes to the runner now, not at closing.
        public override void Update()
        {
            var c = Held;
            if (c == null || !c.isOpened || !LootRules.Shared) { heldSig = null; return; }
            var live = StashSync.LiveChest();
            string sig = StashSync.Sig(live);
            if (heldSig == null) { heldSig = sig; return; }   // just opened: the runner's own contents
            if (sig == heldSig) return;
            heldSig = sig;
            string json = ItemCodec.Encode(live);
            LiveEdits++;
            ObjectSync.Instance.SendIntent(this, c, w => { w.Put((byte)2); w.Put(json); });
        }

        public override void Capture(Component obj, NetDataWriter w)
        {
            var c = (Chest)obj;
            w.Put(c.alreadyOpened);
            w.Put(HolderOf(c));
            w.Put(ItemCodec.Encode(Items(c)));
        }

        public override void Apply(Component obj, NetDataReader r)
        {
            var c = (Chest)obj;
            bool opened = r.GetBool();
            byte who = r.GetByte();
            string json = r.GetString();
            holder[c] = who;
            if (!LootRules.Shared) return;   // states from a shared-loot session don't apply to instanced loot
            if (!Ready(c)) { waiting[c] = (opened, who, json); return; }
            ApplyState(c, opened, who, json);
        }

        private void ApplyState(Chest c, bool opened, byte who, string json)
        {
            bool ours = Held == c && c.isOpened;
            if (!ours) SetItems(c, ItemCodec.Decode(json));
            if (opened) c.alreadyOpened = true;
            if (!ours) c.SetArtOpened(who != 0 || (c.alreadyOpened && c.fixedOpenedState) || c.isOpened);
            if (asked.TryGetValue(c, out float at))
            {
                asked.Remove(c);
                if (Time.realtimeSinceStartup - at > 6f) return;   // too late: the player has moved on
                if (who == Session.Current.LocalId && !c.isOpened)
                {
                    Held = c;
                    c.OpenChest();
                }
                else if (who != Session.Current.LocalId) Plugin.Log.LogInfo($"[loot] {c.name} is in use by #{who}");
            }
        }

        // Runs on the scene's runner: 0 = open it for me, 1 = I closed it, this is what's left, 2 = I still have it
        // open, this is what's in it now.
        public override void OnIntent(Component obj, byte from, NetDataReader r)
        {
            var c = (Chest)obj;
            byte op = r.GetByte();
            byte me = Session.Current.LocalId;
            if (op == 0)
            {
                byte h = HolderOf(c);
                if (h != 0 && h != from && InScene(h))
                {
                    Denials++;
                    if (from == me) { asked.Remove(c); Plugin.Log.LogInfo($"[loot] {c.name} is in use by #{h}"); }
                    else ObjectSync.Instance.Changed(this, c);   // tells the asker who has it
                    return;
                }
                holder[c] = from;
                Grants++;
                if (from == me)
                {
                    asked.Remove(c);
                    Held = c;
                    c.OpenChest();
                }
                else c.SetArtOpened(true);
                ObjectSync.Instance.Changed(this, c);
            }
            else if (op == 1)
            {
                string json = r.GetString();
                byte h = HolderOf(c);
                if (h != from && h != 0) return;
                holder[c] = 0;
                if (from != me) SetItems(c, ItemCodec.Decode(json));
                c.alreadyOpened = true;
                if (!c.isOpened) c.SetArtOpened(c.fixedOpenedState);
                ObjectSync.Instance.Changed(this, c);
            }
            else if (op == 2)
            {
                string json = r.GetString();
                if (HolderOf(c) != from || from == me) return;
                SetItems(c, ItemCodec.Decode(json));
                ObjectSync.Instance.Changed(this, c);
            }
        }

        // Our player pressed E on a closed chest (shared mode): ask the runner.
        public void AskOpen(Chest c)
        {
            asked[c] = Time.realtimeSinceStartup;
            ObjectSync.Instance.SendIntent(this, c, w => w.Put((byte)0));
        }

        // Our player closed the chest it had open (shared mode): what's left goes back to the runner.
        public void Closed(Chest c)
        {
            Held = null;
            heldSig = null;
            string json = ItemCodec.Encode(Items(c));
            ObjectSync.Instance.SendIntent(this, c, w => { w.Put((byte)1); w.Put(json); });
        }

        // The chest's contents were just set up here: apply a state that arrived before.
        public void OnCreated(Chest c)
        {
            if (!waiting.TryGetValue(c, out var s) || !Ready(c)) return;
            waiting.Remove(c);
            if (!LootRules.Shared) return;
            ObjectSync.Applying = true;
            try { ApplyState(c, s.opened, s.who, s.json); }
            finally { ObjectSync.Applying = false; }
        }

        public byte HolderOf(Chest c) => holder.TryGetValue(c, out byte h) ? h : (byte)0;

        private static bool InScene(byte id)
        {
            var s = Session.Current;
            if (s == null) return false;
            if (id == s.LocalId) return Game.InGame;
            var p = MPRunner.Instance?.Presence;
            return p != null && p.Remotes.TryGetValue(id, out var rp) && rp.Latest != null && rp.Latest.Scene == Game.SceneName
                   && rp.SecondsSinceState < SceneAuthority.PresenceTimeout;
        }

        private static ChestManager Manager => Singleton<ChestManager>.HasMain ? Singleton<ChestManager>.main : null;

        public static bool Ready(Chest c)
        {
            var cm = Manager;
            return cm != null && !cm.isLoading && !string.IsNullOrEmpty(c.ID) && cm.HasID(c.ID);
        }

        public static List<InventoryItem> Items(Chest c)
        {
            var cm = Manager;
            return cm != null && !string.IsNullOrEmpty(c.ID) ? cm.GetChestItems(c.ID) ?? new List<InventoryItem>() : new List<InventoryItem>();
        }

        // Replaces the contents in place (the inventory UI holds on to the list while the chest is open).
        public static void SetItems(Chest c, List<InventoryItem> items)
        {
            var cm = Manager;
            if (cm == null) return;
            if (string.IsNullOrEmpty(c.chestID)) c.chestID = System.Guid.NewGuid().ToString();
            var list = cm.GetChestItems(c.ID);
            if (list == null)
            {
                cm.AddChest(c.ID, items, c.saveChest, c.shareContent);
                return;
            }
            list.Clear();
            list.AddRange(items);
        }
    }

    // Our player pressing E on a chest.
    [HarmonyPatch(typeof(Chest), nameof(Chest.OnInteract))]
    internal static class ChestInteractPatch
    {
        private static bool Prefix(Chest __instance)
        {
            if (StashSync.Handles(__instance)) return StashSync.Instance.OnInteract(__instance);
            if (!LootRules.Shared || !ObjectSync.Active || ChestSync.Instance == null) return true;
            if (__instance.isOpened) return true;   // closing: ChestClosePatch reports it
            ChestSync.Instance.AskOpen(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(Chest), nameof(Chest.OpenChest))]
    internal static class ChestOpenPatch
    {
        private static void Postfix(Chest __instance)
        {
            if (StashSync.IsStorage(__instance)) { StashSync.Instance?.OnOpened(__instance); return; }
            if (LootRules.InstancedGuest && !ObjectSync.Applying)
                LootRules.Instance?.RecordChest(__instance, ItemCodec.Encode(ChestSync.Items(__instance)));
        }
    }

    // CloseChest runs after the inventory UI wrote what's left back into the chest's list.
    [HarmonyPatch(typeof(Chest), nameof(Chest.CloseChest))]
    internal static class ChestClosePatch
    {
        private static void Postfix(Chest __instance)
        {
            if (StashSync.IsStorage(__instance)) { StashSync.Instance?.OnClosed(__instance); return; }
            if (ObjectSync.Applying) return;
            if (LootRules.Shared && ObjectSync.Active && ChestSync.Held == __instance) ChestSync.Instance?.Closed(__instance);
            else if (LootRules.InstancedGuest) LootRules.Instance?.RecordChest(__instance, ItemCodec.Encode(ChestSync.Items(__instance)));
        }
    }

    // Contents being set up (every scene load).
    [HarmonyPatch(typeof(Chest), nameof(Chest.CreateChestItems))]
    internal static class ChestCreatePatch
    {
        private static bool Prefix(Chest __instance)
        {
            // The world's stash is the host's, whatever the loot mode (StashSync): never a roll of our own over it.
            if (StashSync.IsStorage(__instance)) return true;
            if (!LootRules.InstancedGuest || LootRules.Instance == null || !Singleton<ChestManager>.HasMain) return true;
            var cm = Singleton<ChestManager>.main;
            if (LootRules.Instance.TryGetRecord(__instance, out string json))
            {
                // We looted this chest before: it has what we left in it, nothing new.
                ChestSync.SetItems(__instance, ItemCodec.Decode(json));
                __instance.alreadyOpened = true;
                if (__instance.fixedOpenedState) __instance.SetArtOpened(true);
                return false;
            }
            // The host's saved contents are the host's loot: roll our own.
            if (!string.IsNullOrEmpty(__instance.ID)) cm.RemoveChest(__instance.ID);
            return true;
        }

        private static void Postfix(Chest __instance)
        {
            if (StashSync.IsStorage(__instance)) StashSync.Instance?.OnCreated(__instance);
            else ChestSync.Instance?.OnCreated(__instance);
        }
    }
}
