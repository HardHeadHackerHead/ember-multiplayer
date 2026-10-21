using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync
{
    // Host only: keeps the rooms guests are in running, and points enemies at the nearest player.
    internal class HostWorld
    {
        public const float RetargetInterval = 0.5f;
        // A new target must be this much nearer than the one an enemy chases: each swap writes its graph's "Player",
        // which wakes the graph and can start over what it was doing with its target (an approach, a wind-up).
        public const float SwitchMargin = 1.5f;
        public int Retargets;
        // Who an enemy must chase whoever is nearest (the Matriarch's spiders rushing a webbed player: BossSync), or null.
        public static System.Func<Enemy, GameObject> ForcedTarget;

        private readonly Session session;
        private readonly Presence presence;
        private readonly Dictionary<string, GameArea> areas = new Dictionary<string, GameArea>();
        private readonly HashSet<GameArea> keptOn = new HashSet<GameArea>();
        private readonly Dictionary<Enemy, GameObject> currentTarget = new Dictionary<Enemy, GameObject>();
        private int areasScene = -1;
        private float nextRetarget;

        public HostWorld(Session session, Presence presence)
        {
            this.session = session;
            this.presence = presence;
            RemotePlayer.PuppetDestroying += OnPuppetDestroying;
            session.Ended += () =>
            {
                foreach (var a in keptOn) if (a != null && a != Game.Manager?.CurrentArea) a.gameObject.SetActive(false);
                keptOn.Clear();
            };
        }

        public void Update()
        {
            if (!Game.InScene) return;   // (also while we lie dead: the others are still being chased)
            if (!EnemySync.IsAuthority)
            {
                // Not running this scene (any more): let go of the rooms we were keeping up for others.
                if (keptOn.Count > 0) ReleaseRooms();
                return;
            }
            int scene = SceneManager.GetActiveScene().handle;
            if (scene != areasScene)
            {
                areasScene = scene;
                areas.Clear();
                keptOn.Clear();
                currentTarget.Clear();
                foreach (var a in Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    areas[a.name] = a;
            }
            KeepGuestAreas();
            if (Time.realtimeSinceStartup >= nextRetarget)
            {
                nextRetarget = Time.realtimeSinceStartup + RetargetInterval;
                Retarget();
            }
        }

        // Re-aim at the next update instead of up to RetargetInterval later.
        public void RetargetSoon() => nextRetarget = 0f;

        private void ReleaseRooms()
        {
            var current = Game.Manager.CurrentArea;
            foreach (var a in keptOn)
                if (a != null && a != current && a.gameObject.activeSelf) a.gameObject.SetActive(false);
            keptOn.Clear();
            currentTarget.Clear();
        }

        private IEnumerable<RemotePlayer> GuestsHere()
        {
            string scene = Game.SceneName;
            return presence.Remotes.Values.Where(r => r.Latest != null && r.Latest.Scene == scene);
        }

        // A room only runs while it's active; the game deactivates every room but the host's.
        private void KeepGuestAreas()
        {
            var wanted = new HashSet<GameArea>();
            foreach (var r in GuestsHere())
                if (areas.TryGetValue(r.Latest.Area, out var a) && a != null) wanted.Add(a);
            var current = Game.Manager.CurrentArea;
            foreach (var a in wanted)
            {
                if (a == current) continue;
                if (!a.gameObject.activeSelf)
                {
                    a.gameObject.SetActive(true);
                    Plugin.Log.LogInfo($"[world] Keeping room '{a.name}' running for a guest");
                }
                keptOn.Add(a);
            }
            foreach (var a in keptOn.ToList())
            {
                if (wanted.Contains(a)) continue;
                keptOn.Remove(a);
                if (a != null && a != current && a.gameObject.activeSelf)
                {
                    a.gameObject.SetActive(false);
                    Plugin.Log.LogInfo($"[world] Room '{a.name}' has no players left");
                }
            }
        }

        // Each enemy chases the nearest living player in its room (host or guest puppet).
        private void Retarget()
        {
            var es = EnemySync.Instance;
            if (es == null) return;
            var me = Game.LocalPlayer;
            string myArea = Game.AreaName;
            var guests = GuestsHere().Where(r => r.Puppet != null && r.Target != null && !r.Latest.Dead).ToList();
            if (guests.Count == 0)
            {
                // Nobody else around: make sure every enemy is back on the host.
                foreach (var kv in currentTarget.ToList())
                    if (Alive(kv.Key) && kv.Value != me.gameObject) SetTarget(kv.Key, me.gameObject);
                currentTarget.Clear();
                return;
            }
            foreach (var e in EnemiesManager.AllEnemies)
            {
                if (!Alive(e)) continue;
                string area = es.AreaOf(e);
                Vector3 p = e.transform.position;
                GameObject best = ForcedTarget?.Invoke(e);
                float bestD = best != null ? -1f : float.MaxValue;   // forced: nobody is nearer
                if (best == null && area == myArea && !me.IsDead)
                {
                    best = me.gameObject;
                    bestD = (me.transform.position - p).sqrMagnitude;
                }
                foreach (var g in guests)
                {
                    if (g.Latest.Area != area) continue;
                    float d = (g.Puppet.transform.position - p).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = g.Puppet; }
                }
                if (best == null) best = me.gameObject;
                if (currentTarget.TryGetValue(e, out var cur) && cur == best) continue;
                // Not forced, and the one it chases is still in its room and up: only a clearly nearer player wins it.
                if (cur != null && bestD >= 0f && Chaseable(cur, area, myArea, me, guests) &&
                    Mathf.Sqrt(bestD) > Vector2.Distance(cur.transform.position, p) - SwitchMargin) continue;
                SetTarget(e, best);
                currentTarget[e] = best;
                Plugin.Log.LogInfo($"[world] {e.name} (#{es.IdOf(e)}) now chases {(best == me.gameObject ? "the host" : best.name)} at {Mathf.Sqrt(Mathf.Max(0f, bestD)):F1}");
            }
        }

        private static bool Chaseable(GameObject target, string area, string myArea, PlayerMaster me, List<RemotePlayer> guests)
        {
            if (target == null) return false;
            if (target == me.gameObject) return area == myArea && !me.IsDead;
            return guests.Any(g => g.Puppet == target && g.Latest.Area == area);
        }

        // A puppet going away (its player left, a fresh world): every enemy chasing it chases us instead. Their graph
        // starts over too, and so do the graphs of the enemies around it: a running action may hold the puppet's
        // transform (MoveDirectionAction keeps the "Player" it started with, to face it) and would throw every frame
        // once it's destroyed.
        private void OnPuppetDestroying(GameObject puppet)
        {
            var me = Game.LocalPlayer;
            if (me == null || MPRunner.Instance?.World != this) return;   // (earlier sessions' instances stay subscribed)
            foreach (var kv in currentTarget.ToList())
                if (kv.Value == puppet) currentTarget[kv.Key] = me.gameObject;
            foreach (var e in EnemiesManager.AllEnemies.ToList())
            {
                if (!Alive(e) || (PlayerOf(e) != puppet && (e.transform.position - puppet.transform.position).sqrMagnitude > 30f * 30f)) continue;
                try { e.behaviorGraphAgent.Restart(); }
                catch (System.Exception ex) { Plugin.Log.LogWarning($"[world] Restart {e.name}: {ex.Message}"); }
                SetTarget(e, me.gameObject);
            }
        }

        private static GameObject PlayerOf(Enemy e)
        {
            try { return e.behaviorGraphAgent.GetVariable("Player", out Unity.Behavior.BlackboardVariable<GameObject> v) ? v.Value : null; }
            catch (System.Exception) { return null; }
        }

        // Writing a blackboard variable wakes the graph up, which breaks dead enemies (no group): leave them alone.
        private static bool Alive(Enemy e) => e != null && e.isActiveAndEnabled && !e.IsDead && e.behaviorGraphAgent != null && e.behaviorGraphAgent.enabled;

        private void SetTarget(Enemy e, GameObject go)
        {
            if (!Alive(e)) return;
            try
            {
                e.behaviorGraphAgent.SetVariableValue("Player", go);
                Retargets++;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[world] Retarget {e.name}: {ex.Message}"); }
        }
    }
}
