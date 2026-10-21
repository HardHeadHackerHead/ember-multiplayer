using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync
{
    // The Acolyte Trackers: a hook that pulls its victim in, and a camouflage (half see-through) while they stalk.
    //   - The hook on the runner: AcolyteTrackerProjectile.HandleHit only pulls a "Player"-tagged PlayerMaster, so a hook
    //     reaching another player's puppet went through it and the tracker acted as if it had missed. Now the runner
    //     counts it as a hit (HasHit; the hook stops and follows that player while they're pulled) and tells everyone
    //     (TrackerPull). The hooked player runs the game's own pull (AcolyteTracker.StartPull) on its own character,
    //     toward its copy of the tracker, like any enemy hit is resolved by its defender.
    //   - The hook elsewhere: replayed hooks (EnemyAttackStart) never pull by themselves (HandleHit is skipped); only the
    //     runner's TrackerPull does, for the player it names. The copies where someone else is hooked make their hook
    //     follow that player for the pull. A hook on the runner's own character goes out as a TrackerPull too.
    //   - Camouflage: SetCamouflage runs in the runner's graph. The runner sends which trackers of the scene are
    //     camouflaged (on change and every few seconds); the others set the same fade on their copies.
    internal class TrackerSync
    {
        public static TrackerSync Instance { get; private set; }
        public int PuppetHooks, LocalHooksSent, PullsTaken, PullsShown, ReplayHooksSkipped, CamoSent, CamoApplied;

        private readonly Session session;
        private readonly Presence presence;
        private readonly List<AcolyteTracker> trackers = new List<AcolyteTracker>();
        private int registryScene = -1;
        private int[] lastCamo;
        private bool camoDirty;
        private float nextPoll, nextFull;
        private bool pulling;   // our own StartPull call (not a native hook on the runner's character)
        // The pull on our character the runner asked for: if its tracker goes (dies, its room switches off) before the
        // pull ends, its FixedUpdate never lets us go (StartPull holds us in PlayerStatic until the pull's end).
        private AcolyteTracker ourPull;
        private float ourPullStart, ourPullUntil;
        public int PullsReleased;

        // A hook shown following a hooked player until the pull is over.
        private class Follow
        {
            public AcolyteTrackerProjectile Hook;
            public Transform Center;
            public float Until;
        }
        private readonly List<Follow> follows = new List<Follow>();
        // Stand-ins for a hook that's gone here when the runner's pull arrives (StartPull needs one to move).
        private readonly List<(GameObject go, AcolyteTracker by, float since)> standIns = new List<(GameObject, AcolyteTracker, float)>();

        public const float PollInterval = 0.25f, FullInterval = 3f;

        public TrackerSync(Session session, Presence presence)
        {
            Instance = this;
            this.session = session;
            this.presence = presence;
            session.On(Msg.TrackerPull, OnPull);
            session.On(Msg.TrackerCamo, OnCamo);
        }

        private void EnsureRegistry()
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.handle == registryScene) return;
            registryScene = scene.handle;
            trackers.Clear();
            follows.Clear();
            lastCamo = null;
            trackers.AddRange(Object.FindObjectsByType<AcolyteTracker>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => t != null && t.enemy != null && t.gameObject.scene == scene));
        }

        private void Known(AcolyteTracker t)
        {
            if (t != null && !trackers.Contains(t)) trackers.Add(t);
        }

        // ---------------- per frame ----------------

        public void Update()
        {
            if (!session.Ready || !Game.InScene) return;
            EnsureRegistry();
            float now = Time.realtimeSinceStartup;
            for (int i = follows.Count - 1; i >= 0; i--)
            {
                var f = follows[i];
                if (f.Hook == null || !f.Hook.isActiveAndEnabled) { follows.RemoveAt(i); continue; }
                if (f.Center == null || now >= f.Until)
                {
                    f.Hook.gameObject.SetActive(false);
                    follows.RemoveAt(i);
                    continue;
                }
                f.Hook.transform.position = f.Center.position;
            }
            for (int i = standIns.Count - 1; i >= 0; i--)
            {
                var s = standIns[i];
                if (s.by != null && s.by.isPulling && now - s.since < 10f) continue;
                if (s.go != null) Object.Destroy(s.go);
                standIns.RemoveAt(i);
            }
            WatchOurPull(now);
            if (EnemySync.IsAuthority && now >= nextPoll)
            {
                nextPoll = now + PollInterval;
                SendCamo(now);
            }
        }

        // ---------------- camouflage ----------------

        // Postfix on AcolyteTracker.SetCamouflage.
        public void OnCamouflage(AcolyteTracker t)
        {
            Known(t);
            if (EnemySync.IsAuthority) camoDirty = true;
        }

        private void SendCamo(float now)
        {
            string scene = Game.SceneName;
            if (trackers.Count == 0 || !presence.Remotes.Values.Any(r => r.Latest != null && r.Latest.Scene == scene)) { lastCamo = null; return; }
            var es = EnemySync.Instance;
            if (es == null) return;
            var camo = trackers.Where(t => t != null && t.isCamouflage && t.enemy != null && t.enemy.isActiveAndEnabled && !t.enemy.IsDead)
                .Select(t => es.IdOf(t.enemy)).Where(id => id != 0).Take(255).OrderBy(id => id).ToArray();
            if (!camoDirty && lastCamo != null && now < nextFull && camo.SequenceEqual(lastCamo)) return;
            camoDirty = false;
            lastCamo = camo;
            nextFull = now + FullInterval;
            int sceneHash = NetIds.SceneHash(scene);
            session.Send(Msg.TrackerCamo, w =>
            {
                w.Put(sceneHash);
                w.Put((byte)camo.Length);
                foreach (int id in camo) w.Put(id);
            });
            CamoSent++;
        }

        private void OnCamo(byte from, NetDataReader r)
        {
            int scene = r.GetInt();
            int n = NetGuard.Count(r.GetByte(), r, 4);
            var es = EnemySync.Instance;
            if (es == null || EnemySync.IsAuthority || from != es.Authority || !Game.InGame || scene != NetIds.SceneHash(Game.SceneName)) return;
            EnsureRegistry();
            var camo = new HashSet<AcolyteTracker>();
            for (int i = 0; i < n; i++)
            {
                var t = es.ById(r.GetInt())?.GetComponent<AcolyteTracker>();
                if (t == null) continue;
                Known(t);
                camo.Add(t);
            }
            foreach (var t in trackers)
            {
                if (t == null || t.enemy == null || t.enemy.IsDead) continue;
                bool want = camo.Contains(t);
                if (t.isCamouflage == want) continue;
                t.SetCamouflage(want);
                CamoApplied++;
            }
        }

        // ---------------- the hook ----------------

        // Runner: a hook reached another player's puppet (prefix on HandleHit). What HandleHit does for the local
        // player, except the pull itself, which is theirs to run.
        public void OnPuppetHooked(AcolyteTrackerProjectile hook, Enemy e, RemotePlayerTarget target)
        {
            int id = EnemySync.Instance?.IdOf(e) ?? 0;
            var tracker = e.GetComponent<AcolyteTracker>();
            if (id == 0 || tracker == null) return;
            Stop(hook);
            tracker.HasHit = true;
            FollowFor(hook, tracker, target.CenterOfMass, target.transform.position, 0.15f);   // (the puppet runs ~0.1 s behind)
            Send(id, target.PlayerId);
            PuppetHooks++;
            Plugin.Log.LogInfo($"[tracker] {e.name} hooked player #{target.PlayerId}");
        }

        // Runner: the game's own pull started on our character (postfix on StartPull).
        public void OnLocalPull(AcolyteTracker tracker, PlayerMaster victim)
        {
            if (pulling || !EnemySync.IsAuthority || victim == null || victim != Game.LocalPlayer) return;
            int id = EnemySync.Instance?.IdOf(tracker.enemy) ?? 0;
            if (id == 0) return;
            Send(id, session.LocalId);
            LocalHooksSent++;
        }

        private void Send(int enemyId, byte player)
        {
            int scene = NetIds.SceneHash(Game.SceneName);
            session.Send(Msg.TrackerPull, w => { w.Put(scene); w.Put(enemyId); w.Put(player); });
        }

        private void OnPull(byte from, NetDataReader r)
        {
            int scene = r.GetInt();
            int enemyId = r.GetInt();
            byte player = r.GetByte();
            var es = EnemySync.Instance;
            if (es == null || EnemySync.IsAuthority || !Senders.IsRunner(from) || !Game.InGame || scene != NetIds.SceneHash(Game.SceneName)) return;
            var e = es.ById(enemyId);
            var tracker = e != null ? e.GetComponent<AcolyteTracker>() : null;
            if (tracker == null || !e.isActiveAndEnabled || e.IsDead) return;
            var hook = HookOf(e);
            if (player == session.LocalId)
            {
                var me = Game.LocalPlayer;
                if (me == null || me.IsDead) return;
                if (hook != null) Stop(hook);
                else hook = StandIn(tracker);
                pulling = true;
                try { tracker.StartPull(me, hook); }
                finally { pulling = false; }
                ourPull = tracker;
                ourPullStart = Time.realtimeSinceStartup;
                ourPullUntil = ourPullStart + Mathf.Max(0f, tracker.pullDuration) + 1f;
                PullsTaken++;
                Plugin.Log.LogInfo($"[tracker] {e.name}'s hook pulls us from {Vector3.Distance(me.transform.position, e.transform.position):F1} away, for {tracker.pullDuration:F2} s (#{from} says)");
            }
            else if (hook != null && presence.Remotes.TryGetValue(player, out var rp) && rp.Puppet != null && rp.Shown)
            {
                Stop(hook);
                FollowFor(hook, tracker, rp.Target != null ? rp.Target.CenterOfMass : rp.Puppet.transform, rp.Puppet.transform.position, 0f);
                PullsShown++;
            }
        }

        // Ends a pull the tracker can't end any more (or that runs far over its time), and lets our character go.
        private void WatchOurPull(float now)
        {
            if (ReferenceEquals(ourPull, null)) return;   // (a destroyed tracker is "== null" but still set: gone)
            var t = ourPull;
            bool gone = t == null || !t.isActiveAndEnabled || t.enemy == null || t.enemy.IsDead;
            if (!gone && !t.isPulling) { ourPull = null; return; }   // ended as the game ends it
            if (!gone && now < ourPullUntil) return;
            ourPull = null;
            if (t != null)
            {
                t.isPulling = false;
                if (t.tracker != null && t.tracker.gameObject.activeSelf) t.tracker.gameObject.SetActive(false);
            }
            var me = Game.LocalPlayer;
            if (me != null && !me.IsDead && me.currentState == me.stateStatic) me.ChangeState(me.stateIdle);
            PullsReleased++;
            Plugin.Log.LogInfo($"[tracker] Let go of a pull whose tracker {(gone ? "is gone" : "never finished it")}");
        }

        // The hook of this tracker flying here (its replay), if any.
        private static AcolyteTrackerProjectile HookOf(Enemy e)
        {
            foreach (var h in Object.FindObjectsByType<AcolyteTrackerProjectile>(FindObjectsSortMode.None))
                if (h != null && h.enemy == e && h.isActiveAndEnabled) return h;
            return null;
        }

        private static void Stop(AcolyteTrackerProjectile hook)
        {
            if (hook.owner != null) hook.owner.enabled = false;
            if (hook.collider2D != null) hook.collider2D.enabled = false;
            hook.isRetreating = false;   // (a replay that missed here may be on its way back already)
        }

        // An inactive object to stand in for a hook: StartPull moves it along and switches it off at the end.
        private AcolyteTrackerProjectile StandIn(AcolyteTracker by)
        {
            var go = new GameObject("MP_TrackerHook");
            go.SetActive(false);
            var hook = go.AddComponent<AcolyteTrackerProjectile>();
            standIns.Add((go, by, Time.realtimeSinceStartup));
            return hook;
        }

        // The hook stays on the hooked player for as long as the game's pull from here would last (the player is
        // really pulled on its own copy; its puppet follows).
        private void FollowFor(AcolyteTrackerProjectile hook, AcolyteTracker tracker, Transform center, Vector3 victim, float extra)
        {
            float distance = Vector3.Distance(victim, tracker.transform.position) - 0.6f;
            float duration = tracker.speed > 0f ? Mathf.Clamp(distance / (tracker.speed * 2f) - 0.1f, 0f, 3f) : 0.5f;
            follows.RemoveAll(f => f.Hook == hook);
            follows.Add(new Follow { Hook = hook, Center = center, Until = Time.realtimeSinceStartup + duration + extra + 0.2f });
        }

        // Non-runner: a replayed hook touched something; only the runner says who is hooked.
        public void OnReplayHit(Collider2D other)
        {
            var me = Game.LocalPlayer;
            if (me != null && other != null && (other.attachedRigidbody != null ? other.attachedRigidbody.gameObject : other.gameObject) == me.gameObject)
                ReplayHooksSkipped++;
        }
    }

    [HarmonyPatch(typeof(AcolyteTrackerProjectile), nameof(AcolyteTrackerProjectile.HandleHit))]
    internal static class TrackerHookHitPatch
    {
        private static bool Prefix(AcolyteTrackerProjectile __instance, Collider2D other)
        {
            var e = __instance.enemy;
            if (e == null || Session.Current == null || EnemySync.Instance == null) return true;
            // Another copy's hook, replayed here: the runner decides whom it caught (TrackerPull).
            if (EnemySync.GuestPuppets && !EnemySync.Passthrough && !EnemySync.RunsHere(e))
            {
                TrackerSync.Instance?.OnReplayHit(other);
                return false;
            }
            if (!EnemySync.IsAuthority || other == null) return true;
            var target = other.GetComponent<RemotePlayerHurtbox>()?.Owner ??
                         (other.attachedRigidbody != null ? other.attachedRigidbody.GetComponent<RemotePlayerTarget>() : null);
            if (target == null) return true;
            if (!target.IsDead) TrackerSync.Instance?.OnPuppetHooked(__instance, e, target);
            return false;
        }
    }

    [HarmonyPatch(typeof(AcolyteTracker), nameof(AcolyteTracker.StartPull))]
    internal static class TrackerStartPullPatch
    {
        private static void Postfix(AcolyteTracker __instance, PlayerMaster playerMaster) => TrackerSync.Instance?.OnLocalPull(__instance, playerMaster);
    }

    [HarmonyPatch(typeof(AcolyteTracker), nameof(AcolyteTracker.SetCamouflage))]
    internal static class TrackerCamouflagePatch
    {
        private static void Postfix(AcolyteTracker __instance) => TrackerSync.Instance?.OnCamouflage(__instance);
    }
}
