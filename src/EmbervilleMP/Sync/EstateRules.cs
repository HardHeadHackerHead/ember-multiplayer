using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync
{
    // Host settings about the estate and the demo's end (config [Coop] EstateUnlocked / KeepPlayingAfterDemo, or
    // --mp-estate / --mp-keep-playing for tests):
    //   - Estate unlocked: the host's world gets the story flags the demo never sets (building/placing, repairs,
    //     portal repairs). They reach guests like any story flag (WorldState) and stay in the host's save.
    //   - Keep playing after the demo: Gumu's intro in Town ends the demo. The first time, its cutscene leaves the
    //     player frozen (the cutscene agent's lock is never lifted) and shows the "thank you for playing" screen; on
    //     every later visit a trigger calls CutsceneGumu.DemoLock and shows that screen. With this on, every copy
    //     skips that trigger and the lock, closes the screen and lifts the cutscene's lock once it's over; the rest of
    //     the cutscene plays as usual.
    // The host sends both to every guest (EstateRules message) when it's welcomed and when they change.
    internal class EstateRules
    {
        public static EstateRules Instance { get; private set; }
        // What applies in this session (the host's choice; on a guest, what the host said).
        public static bool Unlocked { get; private set; }
        public static bool KeepPlaying { get; private set; }
        public static int DemoLocksSkipped, DemoLocksKept, EndTriggersSkipped, EndScreensClosed, LocksLifted;

        // Every flag the demo keeps off and the estate needs.
        public static readonly BoolID[] Flags = { BoolID.PlaceablesActive, BoolID.RepairablesActive, BoolID.PortalRepairActive };

        private readonly Session session;
        private static Session lastSession;
        private float nextCheck;

        public EstateRules(Session session)
        {
            Instance = this;
            this.session = session;
            if (session.IsHost)
            {
                Unlocked = Args.Has("estate") || Plugin.Settings.EstateUnlocked.Value;
                KeepPlaying = Args.Has("keep-playing") || Plugin.Settings.KeepPlayingAfterDemo.Value;
                Plugin.Log.LogInfo($"[estate] Estate unlocked: {Unlocked}, keep playing after the demo: {KeepPlaying}");
                session.PlayerJoined += p => SendRules(p.Id);
                // Rebuilt on the same session (chained tests: a fresh world): tell the guests again.
                if (lastSession == session) foreach (var p in session.Others) SendRules(p.Id);
            }
            else
            {
                // A new session: nothing until the host says; rebuilt on the same one, the host's word stands (as LootRules).
                if (lastSession != session) Unlocked = KeepPlaying = false;
                session.On(Msg.EstateRules, OnRules);
            }
            lastSession = session;
            session.Ended += () => { if (Instance == this) Unlocked = KeepPlaying = false; };
        }

        public static bool Active => Instance != null && Session.Current != null && Session.Current.Ready;

        public void Update()
        {
            UpdateDemoEnd();
            UpdateFlags();
        }

        private void UpdateFlags()
        {
            if (!session.IsHost || !Unlocked || GuestWorld.Active || !Game.InGame || !GameData.HasWorldData) return;
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 1f;
            // Through the game's own flag table: WorldState sends each change to the guests, the save keeps them.
            foreach (var id in Flags)
                if (!GameData.gameBools.IsTrue(id))
                {
                    GameData.gameBools[id] = true;
                    Plugin.Log.LogInfo($"[estate] Set {id} in the host's world");
                }
        }

        // ---------------- the demo's end ----------------

        private int scannedScene = -1;
        private readonly List<GameObject> endScreens = new List<GameObject>();
        private CutsceneGumu gumu;

        private void UpdateDemoEnd()
        {
            if (!KeepPlaying || !Game.InGame) return;
            int scene = SceneManager.GetActiveScene().handle;
            if (scene != scannedScene)
            {
                scannedScene = scene;
                endScreens.Clear();
                gumu = Object.FindObjectsByType<CutsceneGumu>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
                // The screens the demo-end trigger shows (no names: whatever that trigger switches on).
                foreach (var t in Object.FindObjectsByType<TimedTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (IsDemoEnd(t))
                        for (int i = 0; i < t.OnTriggerEvent.GetPersistentEventCount(); i++)
                            if (t.OnTriggerEvent.GetPersistentTarget(i) is GameObject go && t.OnTriggerEvent.GetPersistentMethodName(i) == "SetActive")
                                endScreens.Add(go);
            }
            foreach (var go in endScreens)
                if (go != null && go.activeSelf)
                {
                    go.SetActive(false);
                    EndScreensClosed++;
                    Plugin.Log.LogInfo($"[estate] Closed the demo's end screen ({go.name})");
                }
            // Gumu's cutscene is over: lift its lock on the player (the demo never does).
            var p = Game.LocalPlayer;
            if (gumu == null || p == null || p.staticOwners.Count == 0 || !GameData.gameBools.IsTrue(BoolID.GumuIntroductionDone)) return;
            var agent = gumu.agent;
            if (agent != null && agent.Graph != null && agent.Graph.IsRunning) return;
            foreach (var owner in p.staticOwners.ToList())
                if ((agent != null && owner is GameObject go && go == agent.gameObject) || (owner is System.Type ty && ty == typeof(CutsceneGumu)))
                {
                    p.RemoveStaticStack(owner);
                    LocksLifted++;
                    Plugin.Log.LogInfo($"[estate] Lifted the demo's end lock on the player ({owner})");
                }
        }

        public static bool IsDemoEnd(TimedTrigger t)
        {
            var ev = t != null ? t.OnTriggerEvent : null;
            if (ev == null) return false;
            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                if (ev.GetPersistentTarget(i) is CutsceneGumu && ev.GetPersistentMethodName(i) == nameof(CutsceneGumu.DemoLock)) return true;
            return false;
        }

        // Host: the rules to every guest again (tests: each chained scenario may run with other ones).
        public void ResendRules()
        {
            if (!session.IsHost) return;
            Unlocked = Args.Has("estate") || Plugin.Settings.EstateUnlocked.Value;
            KeepPlaying = Args.Has("keep-playing") || Plugin.Settings.KeepPlayingAfterDemo.Value;
            foreach (var p in session.Others) SendRules(p.Id);
        }

        private void SendRules(byte to) =>
            session.SendTo(to, Msg.EstateRules, w => { w.Put(Unlocked); w.Put(KeepPlaying); });

        private void OnRules(byte from, NetDataReader r)
        {
            if (from != Protocol.HostId) return;
            Unlocked = r.GetBool();
            KeepPlaying = r.GetBool();
            Plugin.Log.LogInfo($"[estate] Host's rules: estate unlocked {Unlocked}, keep playing after the demo {KeepPlaying}");
        }

        // The host changed a setting mid-session (pause menu).
        private void Changed()
        {
            if (!session.IsHost) return;
            foreach (var p in session.Others) SendRules(p.Id);
        }

        // ---------------- menus ----------------

        private static bool Hosting => Active && Session.Current.IsHost;

        public static string EstateLabel =>
            "Estate: " + ((Hosting ? Unlocked : Plugin.Settings.EstateUnlocked.Value) ? "unlocked (building, repairs)" : "locked (as in the demo)");

        public static string DemoEndLabel =>
            "Demo end (Gumu): " + ((Hosting ? KeepPlaying : Plugin.Settings.KeepPlayingAfterDemo.Value) ? "keep playing" : "stop there");

        // On a guest: what the host chose.
        public static string GuestLabel =>
            $"Estate: {(Unlocked ? "unlocked" : "locked")} · demo end: {(KeepPlaying ? "keep playing" : "stop")} (host's settings)";

        public static int SettingsHash =>
            (Plugin.Settings.EstateUnlocked.Value ? 1 : 0) + (Plugin.Settings.KeepPlayingAfterDemo.Value ? 2 : 0) + (Unlocked ? 4 : 0) + (KeepPlaying ? 8 : 0);

        public static void CycleEstate()
        {
            var e = Plugin.Settings.EstateUnlocked;
            e.Value = !e.Value;
            Plugin.Settings.Save();
            // While hosting: unlocking applies now. Locking again only stops further unlocks: the flags already set
            // are the world's progress now (taking them away could strand what was built).
            if (Hosting && e.Value != Unlocked)
            {
                Unlocked = e.Value;
                Instance.Changed();
            }
        }

        public static void CycleDemoEnd()
        {
            var k = Plugin.Settings.KeepPlayingAfterDemo;
            k.Value = !k.Value;
            Plugin.Settings.Save();
            if (Hosting && k.Value != KeepPlaying)
            {
                KeepPlaying = k.Value;
                Instance.Changed();
            }
        }
    }

    // Gumu's intro freezes the player at the demo's end. Skipped in co-op when the host chose to keep playing.
    [HarmonyPatch(typeof(CutsceneGumu), nameof(CutsceneGumu.DemoLock))]
    internal static class DemoLockPatch
    {
        private static bool Prefix()
        {
            if (!EstateRules.Active || !EstateRules.KeepPlaying)
            {
                EstateRules.DemoLocksKept++;
                Plugin.Log.LogInfo("[estate] Demo lock (the demo ends here)");
                return true;
            }
            EstateRules.DemoLocksSkipped++;
            Plugin.Log.LogInfo("[estate] Demo lock skipped (host: keep playing)");
            return false;
        }
    }

    // The trigger that ends the demo (DemoLock + the "thank you" screen) doesn't fire when the host keeps playing.
    [HarmonyPatch(typeof(TimedTrigger), nameof(TimedTrigger.Trigger))]
    internal static class DemoEndTriggerPatch
    {
        private static bool Prefix(TimedTrigger __instance)
        {
            if (!EstateRules.Active || !EstateRules.KeepPlaying || !EstateRules.IsDemoEnd(__instance)) return true;
            EstateRules.EndTriggersSkipped++;
            Plugin.Log.LogInfo("[estate] Demo end trigger skipped (host: keep playing)");
            return false;
        }
    }
}
