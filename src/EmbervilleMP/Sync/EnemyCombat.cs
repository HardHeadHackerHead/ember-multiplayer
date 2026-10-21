using System.Collections.Generic;
using System.Linq;
using System.Text;
using EmbervilleMP.Net;
using LiteNetLib.Utils;
using Unity.Behavior;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync
{
    // "Am I in combat?" on every copy: the quick portal (CollectablePortal.Execute: no portal while any enemy group is
    // engaged with the player), teleporting to a teammate and held-back tutorial windows (TeamTeleport.InCombat).
    // The game's answer reads its own enemies' groups. On the scene's runner they're engaged with whoever they chase
    // (HostWorld aims them at the nearest player), so a guest's fight in another room blocks the runner's portal; on
    // everyone else's copy they're puppets with their graphs off, never engaged, so a guest could portal out mid-fight.
    // Here, with others in the scene, it means: an enemy is fighting (its group engaged with a player, or its graph's
    // "State" InCombat, or it attacks) and it's close to our character, or it's in our room and after us.
    //   runner -> all: EnemyCombat: the fighting enemies in rooms where other players are, and whom each chases; on
    //                  change and every FullInterval
    // A player taking over the scene (its runner left or dropped) engages the groups that were fighting the way a hit
    // does in the game (Enemy.ApplyDamage), so the fight goes on instead of the enemies idling until they see someone.
    internal class EnemyCombat
    {
        public static EnemyCombat Instance { get; private set; }
        public const float PollInterval = 0.25f, FullInterval = 2f, FreshFor = 4f, TakeoverWindow = 8f;
        public const float NearRadius = TeamTeleport.EnemyRadius;   // an enemy fighting this close: our fight
        public int ListsSent, ListsReceived, GroupsEngaged;

        private readonly Session session;
        private readonly Presence presence;
        private readonly Dictionary<int, byte> fighting = new Dictionary<int, byte>();   // from the runner: enemy -> whom it chases (0: nobody's)
        private float receivedAt = -100f, nextPoll, lastSent = -100f;
        private string lastSignature = "";
        private int scene = -1;
        private bool wasAuthority;

        public EnemyCombat(Session session, Presence presence)
        {
            Instance = this;
            this.session = session;
            this.presence = presence;
            session.On(Msg.EnemyCombat, OnList);
        }

        public bool Fresh => Time.realtimeSinceStartup - receivedAt < FreshFor;
        public bool RunnerSaysFighting(int id) => Fresh && fighting.ContainsKey(id);

        // Is an enemy of our scene fighting that player (the public API's InCombat for another player)? On the runner
        // from its own enemies, elsewhere from the runner's list.
        public bool Chases(byte player)
        {
            if (!EnemySync.IsAuthority) return Fresh && fighting.ContainsValue(player);
            foreach (var e in EnemiesManager.AllEnemies)
                if (e != null && e.isActiveAndEnabled && !e.IsDead && !EnemySync.RunsHere(e) && RunnerFighting(e) && PlayerIdOf(ChasedBy(e)) == player) return true;
            return false;
        }

        public void Update()
        {
            if (!session.Ready || Session.Current != session || !Game.InScene || EnemySync.Instance == null) return;   // (a dead runner keeps telling: Game.InScene)
            int handle = SceneManager.GetActiveScene().handle;
            if (handle != scene)
            {
                scene = handle;
                fighting.Clear();
                receivedAt = -100f;
                lastSignature = "";
            }
            bool authority = EnemySync.IsAuthority;
            if (authority && !wasAuthority) TakeOver();
            wasAuthority = authority;
            if (!authority) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + PollInterval;
            SendList(now);
        }

        // ---- runner ----

        private void SendList(float now)
        {
            var es = EnemySync.Instance;
            string sceneName = Game.SceneName;
            var areas = new HashSet<string>(presence.Remotes.Values.Where(r => r.Latest != null && r.Latest.Scene == sceneName).Select(r => r.Latest.Area));
            if (areas.Count == 0) { lastSignature = ""; return; }
            var list = new List<(int id, byte target)>();
            foreach (var e in EnemiesManager.AllEnemies)
            {
                if (e == null || !e.isActiveAndEnabled || e.IsDead || EnemySync.RunsHere(e)) continue;
                int id = es.IdOf(e);
                if (id == 0 || !areas.Contains(es.AreaOf(e)) || !RunnerFighting(e)) continue;
                list.Add((id, PlayerIdOf(ChasedBy(e))));
                if (list.Count == 255) break;
            }
            list.Sort((a, b) => a.id.CompareTo(b.id));
            var sig = new StringBuilder();
            foreach (var (id, t) in list) sig.Append(id).Append(':').Append(t).Append(';');
            string signature = sig.ToString();
            if (signature == lastSignature && now - lastSent < FullInterval) return;
            lastSignature = signature;
            lastSent = now;
            int sceneHash = NetIds.SceneHash(sceneName);
            session.Send(Msg.EnemyCombat, w =>
            {
                w.Put(sceneHash);
                w.Put((byte)list.Count);
                foreach (var (id, t) in list) { w.Put(id); w.Put(t); }
            });
            ListsSent++;
        }

        // The game's own notions of an enemy in a fight, on a copy that runs it.
        private static bool RunnerFighting(Enemy e)
        {
            if (e.Group != null && e.Group.IsEngagedWithPlayer) return true;
            try { return e.State != null && e.State.Value == EnemyState.InCombat; } catch { return false; }
        }

        internal static GameObject ChasedBy(Enemy e)
        {
            try { return e.behaviorGraphAgent != null && e.behaviorGraphAgent.GetVariable("Player", out BlackboardVariable<GameObject> v) ? v.Value : null; }
            catch (System.Exception) { return null; }
        }

        private byte PlayerIdOf(GameObject go)
        {
            if (go == null) return 0;
            if (Game.LocalPlayer != null && go == Game.LocalPlayer.gameObject) return session.LocalId;
            foreach (var r in presence.Remotes.Values) if (r.Puppet == go) return r.Info.Id;
            return 0;
        }

        // ---- everyone else ----

        private void OnList(byte from, NetDataReader r)
        {
            int sceneHash = r.GetInt();
            int n = NetGuard.Count(r.GetByte(), r, 5);
            var list = new List<(int, byte)>(n);
            for (int i = 0; i < n; i++) list.Add((r.GetInt(), r.GetByte()));
            var es = EnemySync.Instance;
            if (es == null || EnemySync.IsAuthority || from != es.Authority || !Game.InGame || sceneHash != NetIds.SceneHash(Game.SceneName)) return;
            fighting.Clear();
            foreach (var (id, t) in list) fighting[id] = t;
            receivedAt = Time.realtimeSinceStartup;
            ListsReceived++;
        }

        // ---- the question ----

        // In place of the game's "any group engaged" (QuickPortalCombatPatch): ours with others in the scene.
        public static bool AnyEngaged(IEnumerable<EnemyGroup> groups, System.Func<EnemyGroup, bool> engaged)
        {
            if (Session.Current == null || Instance == null || !SceneAuthority.OthersInMyScene()) return groups.Any(engaged);
            return Fighting(Game.LocalPlayer);
        }

        // skipInvulnerable: TeamTeleport's rule (an enemy that can't be hurt doesn't hold a teleport back).
        public static bool Fighting(PlayerMaster me, bool skipInvulnerable = false)
        {
            var es = EnemySync.Instance;
            var inst = Instance;
            if (me == null || es == null) return false;
            Vector2 at = me.transform.position;
            string myArea = Game.AreaName;
            bool puppets = EnemySync.GuestPuppets;
            byte myId = Session.Current != null ? Session.Current.LocalId : (byte)0;
            foreach (var e in EnemiesManager.AllEnemies)
            {
                if (e == null || !e.isActiveAndEnabled || e.IsDead || EnemySync.RunsHere(e) || skipInvulnerable && !e.CanTakeDamage) continue;
                bool near = ((Vector2)e.transform.position - at).sqrMagnitude <= NearRadius * NearRadius;
                string area = es.AreaOf(e);
                bool sameRoom = area.Length == 0 || area == myArea;
                if (!near && !sameRoom) continue;
                bool fights;
                byte chasing = 0;
                if (puppets && inst != null)
                {
                    int id = es.IdOf(e);
                    fights = e.IsAttacking || inst.Fresh && id != 0 && inst.fighting.TryGetValue(id, out chasing);
                    if (fights && (near || chasing == myId)) return true;
                }
                else
                {
                    fights = e.IsAttacking || RunnerFighting(e);
                    if (fights && (near || ChasedBy(e) == me.gameObject)) return true;
                }
            }
            return false;
        }

        // ---- taking over ----

        // Every group that was fighting gets the game's engage-after-a-hit, also one that looks engaged here already: a
        // puppet's group keeps whatever sight its graph saw before it became a puppet (IsInSightOfPlayer), which the
        // restarted graph clears at its first look (SetInSight), ending the engagement unless it's forced too.
        private void TakeOver()
        {
            if (fighting.Count == 0) return;
            if (Time.realtimeSinceStartup - receivedAt > TakeoverWindow) { fighting.Clear(); return; }
            var es = EnemySync.Instance;
            var done = new HashSet<EnemyGroup>();
            int skipped = 0;
            foreach (var kv in fighting)
            {
                var e = es.ById(kv.Key);
                var g = e != null && e.isActiveAndEnabled && !e.IsDead ? e.Group : null;
                if (g == null) { skipped++; continue; }
                if (!done.Add(g)) continue;
                try
                {
                    g.LastPlayerSeenPosition = PositionOf(kv.Value) ?? e.transform.position;
                    g.TimeToExpirePlayerLastSeen = 15f;
                    g.IsForcedToPosition = true;
                    g.ForceEngage = true;
                }
                catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemy] Engaging {e.name}: {ex.Message}"); }
            }
            int n = fighting.Count;
            fighting.Clear();
            GroupsEngaged += done.Count;
            Plugin.Log.LogInfo($"[enemy] Took over the fight: {done.Count} groups of {n} fighting enemies stay engaged ({skipped} gone or without a group)");
        }

        private Vector3? PositionOf(byte player)
        {
            if (player == 0) return null;
            if (player == session.LocalId) return Game.LocalPlayer != null ? Game.LocalPlayer.transform.position : (Vector3?)null;
            return presence.Remotes.TryGetValue(player, out var r) && r.Puppet != null ? r.Puppet.transform.position : (Vector3?)null;
        }
    }
}
