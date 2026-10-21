using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using Unity.Behavior;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync
{
    // What a death fails or rewinds when others play the same scene.
    //   - Room events, enemy waves and graph events (FailEventOnPlayerDeath) fail on the local player's death in the
    //     game. In co-op they fail only once every player in their room is down: the runner's copy keeps simulating
    //     while its own player is dead and the others fight on, and it watches the others' deaths. Graph events aren't
    //     synced (procedural dungeons only, not in the demo), so every copy applies the same rule to its own graph.
    //   - A party wipe: everyone in the scene down at once. One copy decides (the scene's runner, counting the dead
    //     too: SceneWipe) and every copy rewinds the scene as the game does after a death (enemies, the boss and her
    //     eggs, breakables, cutscene props) as its own player gets back up (Patches/DeathPatches.cs). Short of a wipe,
    //     only the dead player is put back (and into a boss fight if their copy had it going: BossArena).
    // "Down" lasts from the death to getting back up (Deaths' Died / Up notices, and the player's state: a dead player
    // keeps reporting its scene through its death screen, Game.InScene; a copy without that still says "between
    // scenes" then, which is neither up nor gone from the scene).
    internal class PartyWipe
    {
        public static PartyWipe Instance { get; private set; }
        public static int HeldFails, RoomFails, WipesDeclared, WipesHeard, Rewinds, ScenesRewound;

        // Set while our character's Die runs: the events' death hooks fire inside it.
        public static bool LocalDying;
        // EnemiesResetAllPatch: the rewind of a party wipe resets the enemies as the game would.
        public static bool Rewinding;

        private readonly Session session;
        private readonly List<DungeonRoomEventStage> stages = new List<DungeonRoomEventStage>();
        private readonly List<EnemyWaveHandler> waves = new List<EnemyWaveHandler>();
        private readonly List<FailEventOnPlayerDeath> graphs = new List<FailEventOnPlayerDeath>();
        // other players down by their Died notice: where, and their state's clock then (a later state alive = back up)
        private readonly Dictionary<byte, (string scene, string area, float since)> down = new Dictionary<byte, (string, string, float)>();
        private readonly Dictionary<byte, bool> seenUp = new Dictionary<byte, bool>();   // for the log: last up / down we saw
        private bool seenLocalDown;
        private bool localDown, declared, respawning;
        private int sceneHandle = -1;   // (Unity's scene handles can be negative: never a sentinel)
        private bool pending;   // a party wipe of this scene to rewind as we get back up
        private float respawnedAt = -100f, pendingAt, nextPoll;

        public const float PollInterval = 0.25f, LateWipe = 3f, StalePending = 30f;

        public PartyWipe(Session session)
        {
            Instance = this;
            this.session = session;
            session.On(Msg.SceneWipe, OnWipe);
            session.PlayerLeft += p => { down.Remove(p.Id); seenUp.Remove(p.Id); };
            BossArena.Hook(session);
        }

        // ---------------- who's still up ----------------

        public static string AreaOf(Component c) => c != null ? c.GetComponentInParent<GameArea>(true)?.name ?? "" : "";

        // Deaths.OnNotice: another player died / got back up.
        public void OnNotice(byte from, Deaths.Event ev)
        {
            if (ev == Deaths.Event.Up)
            {
                if (down.Remove(from)) Plugin.Log.LogInfo($"[wipe] #{from} is back up (their notice)");
                return;
            }
            if (ev != Deaths.Event.Died) return;
            RemotePlayer rp = null;
            var st = MPRunner.Instance?.Presence != null && MPRunner.Instance.Presence.Remotes.TryGetValue(from, out rp) ? rp.Latest : null;
            if (st != null && st.Scene.Length > 0)
            {
                down[from] = (st.Scene, st.Area, st.Time);
                Plugin.Log.LogInfo($"[wipe] #{from} is down in {st.Scene}/{st.Area} (their death notice; state dead={st.Dead})");
            }
            else Plugin.Log.LogInfo($"[wipe] #{from} died, but we don't know where (no state from them): not counted");
        }

        // The players of our scene (us too) in that room ("": anywhere in the scene), up or down.
        private List<(byte id, bool up)> Members(string area)
        {
            var list = new List<(byte, bool)>();
            var me = Game.LocalPlayer;
            string scene = Game.SceneName;
            if (me != null && (Game.InGame || localDown) && (area.Length == 0 || Game.AreaName == area))
                list.Add((session.LocalId, !me.IsDead && !localDown));
            var presence = MPRunner.Instance?.Presence;
            if (presence == null) return list;
            foreach (var r in presence.Remotes.Values)
            {
                byte id = r.Info.Id;
                var st = r.Latest;
                bool fresh = st != null && st.Scene == scene && r.SecondsSinceState < SceneAuthority.PresenceTimeout;
                if (down.TryGetValue(id, out var d))
                {
                    // Up again by a state sent after the death (not an older one overtaking the notice): their "up"
                    // notice is on its way.
                    if (fresh && !st.Dead && st.Time > d.since + 0.5f)
                    {
                        down.Remove(id);
                        Plugin.Log.LogInfo($"[wipe] #{id} is back up (their state: alive in {st.Scene}/{st.Area})");
                    }
                    else
                    {
                        if (d.scene == scene && (area.Length == 0 || d.area == area)) list.Add((id, false));
                        continue;
                    }
                }
                if (fresh && (area.Length == 0 || st.Area == area)) list.Add((id, !st.Dead));
            }
            return list;
        }

        public static bool AllDown(string area)
        {
            var m = Instance?.Members(area);
            return m != null && m.Count > 0 && m.All(x => !x.up);
        }

        // Others play (or lie dead in) our scene.
        public static bool Coop => Session.Current != null && Session.Current.Ready && Instance != null && Instance.Members("").Count > 1;

        // A death hook of the game is about to fail c. false: someone in its room is still up, it goes on. (A
        // follower's synced events wait for the runner anyway: RoomEventSync.)
        public static bool DeathFails(Component c, bool synced = true)
        {
            if (!LocalDying || !Coop || (synced && Objects.RoomEventPatches.Follower)) return true;
            string area = AreaOf(c);
            if (AllDown(area)) return true;
            if (HeldFails++ < 20) Plugin.Log.LogInfo($"[wipe] We're down but others fight on in {(area.Length > 0 ? area : Game.SceneName)}: {c.GetType().Name} {c.name} goes on");
            return false;
        }

        // ---------------- registry (whatever a death can fail) ----------------

        private void EnsureScene()
        {
            int h = SceneManager.GetActiveScene().handle;
            if (h == sceneHandle) return;
            sceneHandle = h;
            stages.Clear();
            waves.Clear();
            graphs.Clear();
            declared = false;
            pending = false;
            respawning = false;
        }

        public void Watch(DungeonRoomEventStage s) { EnsureScene(); if (!stages.Contains(s)) stages.Add(s); }
        public void Watch(EnemyWaveHandler h) { EnsureScene(); if (!waves.Contains(h)) waves.Add(h); }
        public void Watch(FailEventOnPlayerDeath g) { EnsureScene(); if (!graphs.Contains(g)) graphs.Add(g); }

        private static bool Running(DungeonRoomEventStage s) => s != null && s.isActiveAndEnabled && s.state == DungeonRoomEventStage.StageState.active;
        public static bool Running(EnemyWaveHandler h) =>
            h != null && (h.state == EnemyWaveHandler.waveState.wave || h.state == EnemyWaveHandler.waveState.cooldown);
        private static bool Running(FailEventOnPlayerDeath g) => g != null && g.state != null && g.state.Value == EventState.Running;

        // ---------------- per frame (also on the death screen) ----------------

        public static void OnLocalDie() { if (Instance != null) Instance.localDown = true; }

        // Game.InScene: our player died in this scene (in a session) and isn't back in the game yet.
        public static bool LocalDownInScene
        {
            get
            {
                var gm = Game.Manager;
                return Instance != null && Instance.localDown && Session.Current != null && gm != null && gm.player != null && gm.finishedSetup &&
                       !(Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading);
            }
        }

        public void Update()
        {
            var me = Game.LocalPlayer;
            if (!session.Ready || me == null) return;
            EnsureScene();
            BossArena.Update();   // (a fight someone else started, for a player whose own intro hasn't played)
            if (localDown && !me.IsDead && Game.InGame) localDown = false;
            // (a wipe is taken by our respawn, or by the game's own reset; one still here long after is stale)
            if (pending && !localDown && !respawning && !me.IsDead && Time.realtimeSinceStartup - pendingAt > StalePending)
            {
                pending = false;
                Plugin.Log.LogWarning("[wipe] A party wipe was never rewound here (no respawn came): dropped");
            }
            float now = Time.realtimeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + PollInterval;
            var members = Members("");
            LogChanges(members);
            if (members.Count < 2) { declared = false; return; }
            bool runner = EnemySync.IsAuthority;   // (what RoomEventSync goes by)
            stages.RemoveAll(s => s == null);
            waves.RemoveAll(h => h == null);
            graphs.RemoveAll(g => g == null);
            if (runner)
            {
                foreach (var s in stages.Where(Running).ToList())
                    if (AllDown(AreaOf(s))) { Fail(s); s.FailStage(); }
                foreach (var h in waves.Where(Running).ToList())
                    if (AllDown(AreaOf(h))) { Fail(h); h.FailedWaveEvent(); }
            }
            foreach (var g in graphs.Where(Running).ToList())
                if (AllDown(AreaOf(g))) { Fail(g); g.state.Value = EventState.Failed; }
            WatchWipe(members);
        }

        // Tests and bug reports: every change of who's up or down in our scene, as this copy sees it.
        private void LogChanges(List<(byte id, bool up)> members)
        {
            bool meDown = localDown || (Game.LocalPlayer != null && Game.LocalPlayer.IsDead);
            if (meDown != seenLocalDown)
            {
                seenLocalDown = meDown;
                Plugin.Log.LogInfo($"[wipe] We're {(meDown ? "down" : "up")} (dead {Game.LocalPlayer?.IsDead}, in game {Game.InGame})");
            }
            var presence = MPRunner.Instance?.Presence;
            if (presence == null) return;
            foreach (var r in presence.Remotes.Values)
            {
                byte id = r.Info.Id;
                int i = members.FindIndex(m => m.id == id);
                bool? now = i < 0 ? (bool?)null : members[i].up;
                bool? was = seenUp.TryGetValue(id, out var w) ? w : (bool?)null;
                if (now == was) continue;
                if (now == null) seenUp.Remove(id); else seenUp[id] = now.Value;
                var st = r.Latest;
                string state = st == null ? "no state" : $"state {st.Scene}/{st.Area} dead={st.Dead} {r.SecondsSinceState:F1}s old";
                Plugin.Log.LogInfo($"[wipe] #{id}: {(now == null ? "not in our scene" : now.Value ? "up" : "down")} ({state}, death notice {(down.ContainsKey(id) ? "yes" : "no")})");
            }
        }

        private static void Fail(Component c)
        {
            RoomFails++;
            Plugin.Log.LogInfo($"[wipe] Everyone in {(AreaOf(c).Length > 0 ? AreaOf(c) : Game.SceneName)} is down: {c.GetType().Name} {c.name} fails");
        }

        // ---------------- party wipe ----------------

        // Who decides: the scene's runner as SceneAuthority picks it, the dead counted (a runner on its death screen
        // still decides).
        private static byte Decider(List<(byte id, bool up)> members) =>
            members.Any(m => m.id == Protocol.HostId) ? Protocol.HostId : members.Min(m => m.id);

        private void WatchWipe(List<(byte id, bool up)> members)
        {
            if (members.Any(m => m.up)) { declared = false; return; }
            if (declared || Decider(members) != session.LocalId) return;
            declared = true;
            WipesDeclared++;
            Plugin.Log.LogInfo($"[wipe] Everyone in {Game.SceneName} is down: the scene rewinds for all {members.Count}");
            int scene = NetIds.SceneHash(Game.SceneName);
            session.Send(Msg.SceneWipe, w => w.Put(scene));
            Heard();
        }

        private void OnWipe(byte from, NetDataReader r)
        {
            int scene = r.GetInt();
            if (from == session.LocalId || Game.LocalPlayer == null || scene != NetIds.SceneHash(Game.SceneName)) return;
            EnsureScene();
            if (!Members("").Any(m => m.id == from)) return;   // only someone of this scene
            WipesHeard++;
            Heard();
        }

        private void Heard()
        {
            var me = Game.LocalPlayer;
            if (me != null && (me.IsDead || localDown || respawning))
            {
                Plugin.Log.LogInfo("[wipe] Party wipe: the scene rewinds as we get back up");
                pending = true;
                pendingAt = Time.realtimeSinceStartup;
                return;
            }
            // Back up a moment ago (the decider's view of us was that old): this copy was part of it.
            if (Time.realtimeSinceStartup - respawnedAt < LateWipe)
            {
                Plugin.Log.LogInfo("[wipe] The party wipe came just after our respawn: rewinding now");
                Rewind(Game.Manager);
                return;
            }
            Plugin.Log.LogInfo("[wipe] A party wipe was declared, but we're up: nothing to rewind");
        }

        // CoopResetScenePatch, our own respawn.
        public static void RespawnStarted() { if (Instance != null) Instance.respawning = true; }

        public static void RespawnDone()
        {
            if (Instance == null) return;
            Instance.respawning = false;
            Instance.respawnedAt = Time.realtimeSinceStartup;
        }

        public static bool TakePending()
        {
            var me = Instance;
            if (me == null || !me.pending) return false;
            me.EnsureScene();   // (a scene change since drops it)
            bool here = me.pending;
            me.pending = false;
            return here;
        }

        // What the game's own ResetScene does after putting the player back: every enemy and resettable thing of the
        // scene back to its state when its room was entered.
        public static void Rewind(GameManager gm)
        {
            if (gm == null) return;
            Rewinds++;
            Plugin.Log.LogInfo("[wipe] Party wipe: the scene rewinds as in single player");
            Rewinding = true;
            try
            {
                foreach (var r in gm.toRemove) gm.resettables.Remove(r);
                gm.toRemove.Clear();
                try { EnemiesManager.ResetAll(); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"[wipe] Rewinding the enemies: {e.Message}"); }
                foreach (var r in gm.resettables.ToList())
                    try { r.ResetToLastState(); }
                    catch (System.Exception e) { Plugin.Log.LogWarning($"[wipe] Rewinding {r}: {e.Message}"); }
            }
            finally { Rewinding = false; }
        }
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.Die))]
    internal static class PlayerDieWipePatch
    {
        private static void Prefix(PlayerMaster __instance)
        {
            if (__instance != Game.LocalPlayer) return;
            PartyWipe.LocalDying = true;
            PartyWipe.OnLocalDie();
        }

        private static void Finalizer() => PartyWipe.LocalDying = false;
    }

    // Whenever our copy rewinds its scene (a party wipe, or the game's own reset when we're alone in it): her fight
    // starts over, what we knew of it is stale.
    [HarmonyPatch(typeof(EnemiesManager), nameof(EnemiesManager.ResetAll))]
    internal static class RewoundPatch
    {
        // The game's list keeps enemies that were destroyed (a failed wave destroys its spawns): resetting one threw,
        // and the rest of the rewind with it.
        private static void Prefix()
        {
            int gone = EnemiesManager.AllEnemies.RemoveWhere(e => e == null);
            if (gone > 0) Plugin.Log.LogInfo($"[wipe] {gone} destroyed enemies dropped from the scene's list before the rewind");
        }

        private static void Postfix()
        {
            // (whether it ran: EnemiesResetAllPatch's condition)
            if (Session.Current == null || (Patches.CoopResetScenePatch.OthersInScene() && !PartyWipe.Rewinding)) return;
            PartyWipe.ScenesRewound++;
            BossSync.Instance?.OnRewound();
            EggSync.Instance?.OnRewound();
        }
    }

    // Room event stages: the runner's call (RoomEventStageOutcomePatch); our death fails one only if nobody in its
    // room is still up. The runner watches the rest.
    [HarmonyPatch(typeof(DungeonRoomEventStage), nameof(DungeonRoomEventStage.StartStage))]
    internal static class StageWatchPatch
    {
        private static void Postfix(DungeonRoomEventStage __instance)
        {
            if (__instance.failOnPlayerDeath) PartyWipe.Instance?.Watch(__instance);
        }
    }

    [HarmonyPatch(typeof(DungeonRoomEventStage), nameof(DungeonRoomEventStage.FailStage))]
    internal static class StageDeathFailPatch
    {
        private static bool Prefix(DungeonRoomEventStage __instance) => PartyWipe.DeathFails(__instance);
    }

    // Enemy waves: the same. (In the game any death fails them, even ready or completed ones; in co-op only a
    // running wave fails.)
    [HarmonyPatch(typeof(EnemyWaveHandler), nameof(EnemyWaveHandler.SpawnNextWave))]
    internal static class WaveWatchPatch
    {
        private static void Postfix(EnemyWaveHandler __instance) => PartyWipe.Instance?.Watch(__instance);
    }

    // A wave started again after it failed still lists the enemies its failure destroyed, and SpawnNextWave threw on
    // them (the game's own retry has the same flaw; in co-op failing and retrying without a rewind is the usual way).
    [HarmonyPatch(typeof(EnemyWaveHandler), nameof(EnemyWaveHandler.SpawnNextWave))]
    internal static class WaveRetryPatch
    {
        private static void Prefix(EnemyWaveHandler __instance) => __instance.currentEnemies?.RemoveAll(e => e == null);
    }

    [HarmonyPatch(typeof(EnemyWaveHandler), nameof(EnemyWaveHandler.OnPlayerDeath))]
    internal static class WaveDeathFailPatch
    {
        private static bool Prefix(EnemyWaveHandler __instance)
        {
            if (!PartyWipe.LocalDying || !PartyWipe.Coop || Objects.RoomEventPatches.Follower) return true;
            return PartyWipe.Running(__instance) && PartyWipe.DeathFails(__instance);
        }
    }

    // Graph events (procedural dungeons): every copy runs its own graph, and fails it by the same rule.
    [HarmonyPatch(typeof(FailEventOnPlayerDeath), nameof(FailEventOnPlayerDeath.Start))]
    internal static class GraphEventWatchPatch
    {
        private static void Postfix(FailEventOnPlayerDeath __instance) => PartyWipe.Instance?.Watch(__instance);
    }

    [HarmonyPatch(typeof(FailEventOnPlayerDeath), nameof(FailEventOnPlayerDeath.UpdateState))]
    internal static class GraphEventDeathFailPatch
    {
        private static bool Prefix(FailEventOnPlayerDeath __instance) => PartyWipe.DeathFails(__instance, synced: false);
    }
}
