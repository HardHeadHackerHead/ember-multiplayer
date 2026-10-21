using System;
using System.Linq;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Saves in co-op. The game saves on a scene change and on quit only, and in co-op items change hands between
    // machines while everyone stays where they are: a guest's deposit into the world's stash is in the host's world and
    // gone from the guest's bag, both in memory. Whichever copy crashes before its next scene change loses its half,
    // and the items vanish or come back twice. So after an item changes hands, both sides save what they own, soon:
    //   - What counts: the stash (a guest's moves, its crafting from it), the world's key items (a guest's report), and
    //     on the host a guest's shop purchase, shared-loot chest or crafting order (Changed(kind)). Plus, on the host,
    //     every few minutes while others are in the session.
    //   - The host saves the game's own way (GameData.SaveData: character, world, map, estate, and the mod's side files
    //     through their SaveData postfixes). Whenever the host's world reaches the disk (these saves, the game's own on
    //     a scene change) it tells the guests (SaveNow); each saves its character (GuestWorld.SaveCharacter: the
    //     character file alone; the host's world never reaches a guest's disk, SaveGuard).
    //   - Debounced: a request saves 1.5 s later and what comes meanwhile rides along (a burst of moves is one save);
    //     saves are at least 5 s apart (a few JSON files, written on the main thread: milliseconds, logged).
    //   - Only at a safe moment, else it waits: in game, no scene load or world switch, the game Playing or Paused (not
    //     a cutscene, dialogue, death or teleport), no room cutscene step playing, nothing loading, no chest or shop
    //     open (an open chest's items sit in the inventory and its own list is stale until it closes: a save then would
    //     write the items twice).
    internal class SaveSync
    {
        public static SaveSync Instance { get; private set; }
        public static void ResetInstance() => Instance = null;

        public const float Delay = 1.5f, MinGap = 5f, Periodic = 300f;

        private readonly Session session;
        private float due = -1f, lastSave = -999f;
        private float periodicFrom;   // the last time the world (host) reached the disk, any way
        private string reason;
        public int Saves, Requests, NoticesSent, NoticesReceived, Waits;
        public float LastSaveAt = -1f;
        public double LastMs;
        public string LastReason = "";

        public SaveSync(Session session)
        {
            Instance = this;
            this.session = session;
            periodicFrom = Time.realtimeSinceStartup;
            if (!session.IsHost) session.On(Msg.SaveNow, OnSaveNow);
        }

        public bool Pending => due >= 0f;

        private bool OnSession => session.Ready && Session.Current == session;

        // This copy plays the session's world and owns something in it: the host its world, a guest its character.
        private bool Mine => OnSession && (session.IsHost ? !GuestWorld.Active : GuestWorld.Active);

        // Something changed hands: save soon (see above).
        public static void Request(string why) => Instance?.Soon(why);

        // Host: a guest did something to an object that hands out items (it's in the host's world from now on).
        public static void Changed(string kind, byte from)
        {
            var i = Instance;
            if (i == null || !i.session.IsHost || from == i.session.LocalId) return;
            if (kind == "chest" || kind == "store" || kind == "craft") i.Soon($"{kind} by #{from}");
        }

        private void Soon(string why)
        {
            if (!Mine) return;
            Requests++;
            if (due >= 0f) return;   // rides along with the one pending
            due = Mathf.Max(Time.realtimeSinceStartup + Delay, lastSave + MinGap);
            reason = why;
        }

        private void OnSaveNow(byte from, NetDataReader r)
        {
            if (from != Protocol.HostId) return;
            NoticesReceived++;
            Soon("the host saved");
        }

        // The host's world just reached the disk (any save): the guests save their characters too.
        public void HostSaved()
        {
            if (!session.IsHost || !OnSession || GuestWorld.Active || GuestWorld.Switching) return;
            periodicFrom = Time.realtimeSinceStartup;
            if (!session.Others.Any()) return;
            session.Send(Msg.SaveNow, null);
            NoticesSent++;
        }

        public void Update()
        {
            if (!Mine) { due = -1f; return; }
            float now = Time.realtimeSinceStartup;
            if (due < 0f && session.IsHost && session.Others.Any() && now - periodicFrom >= Periodic) Soon("every few minutes");
            if (due < 0f || now < due) return;
            string unsafeWhy = Unsafe();
            if (unsafeWhy != null)
            {
                if (Waits++ % 600 == 0) Plugin.Log.LogInfo($"[save] Waiting to save ({reason}): {unsafeWhy}");
                return;
            }
            Save(reason);
        }

        // Why now is no time to save (null: it is).
        public static string Unsafe()
        {
            if (!Game.InGame) return "not in game";
            if (Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading) return "loading a scene";
            if (GuestWorld.Switching) return "switching worlds";
            if (!GameData.HasPlayerData || !GameData.HasWorldData || GameData.playerData.isLoading) return "no data";
            var st = Game.Manager.gameState;
            if (st != GameState.Playing && st != GameState.Paused) return "game state " + st;
            if (Objects.CutsceneSync.Instance != null && Objects.CutsceneSync.Instance.HasActive) return "a cutscene is playing";
            var me = Game.LocalPlayer;
            if (me == null || me.IsDead) return "dead";
            if (!Singleton<Inventory>.HasMain || Singleton<Inventory>.main.isLoading) return "inventory loading";
            if (Singleton<Inventory>.main.allItems.Any(i => i != null && (i.section == InventorySection.Chest || i.section == InventorySection.Stock || i.section == InventorySection.Buyback)))
                return "a chest or shop is open";
            if (Singleton<ChestManager>.HasMain && Singleton<ChestManager>.main.isLoading) return "chests loading";
            if (QuestManager.model == null) return "quests loading";
            return null;
        }

        private void Save(string why)
        {
            due = -1f;
            lastSave = Time.realtimeSinceStartup;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (session.IsHost) GameData.SaveData();   // (HostSaved tells the guests, from the postfix)
                else GuestWorld.SaveCharacter();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[save] Saving ({why}) failed: {e.Message}");
                return;
            }
            LastMs = sw.Elapsed.TotalMilliseconds;
            Saves++;
            LastSaveAt = Time.realtimeSinceStartup;
            LastReason = why;
            Plugin.Log.LogInfo($"[save] Saved {(session.IsHost ? "the world" : "our character")} ({why}) in {LastMs:F0} ms");
        }
    }

    // Whenever the host's world reaches the disk (our saves, the game's own on a scene change), the guests save too.
    [HarmonyPatch(typeof(GameData), nameof(GameData.SaveData))]
    internal static class SaveNoticePatch
    {
        private static void Postfix()
        {
            if (GameData.HasWorldData) SaveSync.Instance?.HostSaved();
        }
    }
}
