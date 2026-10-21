using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync
{
    // Deaths that happened before a player got to the scene. The game keeps a killed enemy dead until its scene loads
    // again (Enemy.SetKilled, EnemyDeadSate: inactive; not saved), so a scene's deaths are its runner's: a player who
    // arrives (joins mid-game, comes back from another scene) loads every enemy alive and is told which are dead.
    //   runner -> one: EnemyDeadList: the scene's placed enemies dead in our fight, to each player arriving in the scene,
    //                  and once more a few seconds later (an arrival still setting its scene up)
    // The arrival puts them in the state the game leaves a killed enemy in (dead, inactive, out of the alive list, saved
    // dead for a rewind), without a second death: no animation, loot, XP, kill count or death event. It then knows them
    // as dead like any death it saw, so it keeps them dead when it runs the scene next, and tells the next arrivals.
    // Same rule as the game's: the deaths last while someone is in the scene; once everyone has left it and it loads
    // again, its enemies are back, as in single player.
    internal partial class EnemySync
    {
        public const float DeadListRepeat = 4f;
        public int DeadListsSent, DeadListsApplied, DeadMarked, DeadRekilled;

        private readonly Dictionary<byte, float> toldDead = new Dictionary<byte, float>();   // runner: who got the list (0: and the repeat)
        private readonly Dictionary<int, float> deadHere = new Dictionary<int, float>();     // arrival: dead in the runner's fight, since
        private float nextDeadSweep, nextArrivalCheck;

        // Every death on this copy since its scene loaded (also before a session: a host that fought alone first).
        private static readonly HashSet<Enemy> diedSinceLoad = new HashSet<Enemy>();
        private static int diedScene = -1;

        internal static void NoteKilled(Enemy e)
        {
            int handle = SceneManager.GetActiveScene().handle;
            if (handle != diedScene) { diedScene = handle; diedSinceLoad.Clear(); }
            if (e != null) diedSinceLoad.Add(e);
        }

        public bool KnownDead(int id) => deadHere.ContainsKey(id);

        private void ClearDeadLists()
        {
            toldDead.Clear();
            deadHere.Clear();
        }

        // Runner: everyone who arrived in our scene since the last look gets our dead list.
        private void TellArrivalsWhoIsDead()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextArrivalCheck) return;
            nextArrivalCheck = now + 0.25f;
            string scene = Game.SceneName;
            var here = new HashSet<byte>(presence.Remotes.Values
                .Where(r => r.Latest != null && r.Latest.Scene == scene && r.SecondsSinceState < SceneAuthority.PresenceTimeout)
                .Select(r => r.Info.Id));
            foreach (var id in toldDead.Keys.Where(k => !here.Contains(k)).ToList()) toldDead.Remove(id);   // left: told again on return
            foreach (byte p in here)
            {
                if (!toldDead.TryGetValue(p, out float at)) { SendDeadList(p); toldDead[p] = now; }
                else if (at > 0f && now - at > DeadListRepeat) { SendDeadList(p); toldDead[p] = 0f; }
            }
        }

        // Dead in our fight: placed enemies (spawns arrive as EnemySpawn or not at all) killed here, or whose death we
        // were told about, and still dead (not brought back by a rewind of ours). Not IsDead alone: dormant enemies (a
        // room event's, a boss before its intro) look dead too.
        private List<int> DeadIds()
        {
            var ids = new List<int>();
            bool sameLoad = diedScene == SceneManager.GetActiveScene().handle;
            foreach (var kv in byId)
            {
                var e = kv.Value;
                if (kv.Key <= 0 || e == null || !e.IsDead) continue;
                if (killed.Contains(kv.Key) || localDeathTime.ContainsKey(kv.Key) || sameLoad && diedSinceLoad.Contains(e)) ids.Add(kv.Key);
                if (ids.Count == 3000) break;
            }
            return ids;
        }

        private void SendDeadList(byte to)
        {
            var ids = DeadIds();
            if (ids.Count == 0) return;
            int scene = NetIds.SceneHash(Game.SceneName);
            session.Send(Msg.EnemyDeadList, w =>
            {
                w.Put(to);
                w.Put(scene);
                w.Put((ushort)ids.Count);
                foreach (int id in ids) w.Put(id);
            });
            DeadListsSent++;
            Plugin.Log.LogInfo($"[enemy] Told #{to} which {ids.Count} enemies of {Game.SceneName} are dead");
        }

        private void OnDeadList(byte from, NetDataReader r)
        {
            if (r.GetByte() != session.LocalId) return;
            int scene = r.GetInt();
            int n = NetGuard.Count(r.GetUShort(), r, 4);
            var ids = new int[n];
            for (int i = 0; i < n; i++) ids[i] = r.GetInt();
            if (!Game.InGame || scene != NetIds.SceneHash(Game.SceneName)) return;
            EnsureRegistry();   // (the list can come before our first update in the scene)
            if (iAmAuthority && authority != 0) return;   // we run it: our fight is the scene's
            if (from != SceneAuthority.Mine && from != authority) return;
            float now = Time.realtimeSinceStartup;
            int marked = 0;
            foreach (int id in ids)
            {
                var e = ById(id);
                if (e == null || RunsHere(e)) continue;
                deadHere[id] = now;
                killed.Add(id);
                localDeathTime[id] = now;   // (a state sent before its death, arriving late, doesn't revive it)
                if (MarkDead(e)) marked++;
            }
            DeadListsApplied++;
            DeadMarked += marked;
            Plugin.Log.LogInfo($"[enemy] #{from} says {n} enemies of {Game.SceneName} are dead: {marked} put down here");
        }

        // The state the game leaves a killed enemy in, without its death (SetKilled would count a kill, the death state
        // would play the animation): dead, inactive (OnDisable leaves its group; OnEnable hides it again), out of the
        // alive list (DropIfAllEnemiesKilled, EnemiesInRange), and saved dead (a lone death's rewind keeps it dead).
        private bool MarkDead(Enemy e)
        {
            var go = e.gameObject;
            if (e.IsDead)
            {
                // Dying on screen (its death runs), or gone. One whose room never woke here isn't set up yet: its Awake
                // would make it alive, so it goes inactive.
                if (go.activeInHierarchy || !go.activeSelf) return false;
                go.SetActive(false);
                return true;
            }
            e.isDead = true;
            EnemiesManager.AliveEnemies.Remove(e);
            puppets.Remove(e);
            if (go.activeSelf) go.SetActive(false);
            try { e.SaveState(); } catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemy] Saving {e.name} dead: {ex.Message}"); }
            return true;
        }

        // Arrival: an enemy the runner says is dead came alive on our copy (a room event, a wake): put it down again,
        // unless the runner's states say it's alive there now (it came back there too: ours follows, see Revive).
        private void KeepDeadDead()
        {
            if (deadHere.Count == 0 || iAmAuthority) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextDeadSweep) return;
            nextDeadSweep = now + 0.5f;
            foreach (var kv in deadHere.ToList())
            {
                var e = ById(kv.Key);
                if (e == null || e.IsDead || !e.gameObject.activeInHierarchy || RunsHere(e)) continue;
                if (targets.TryGetValue(kv.Key, out var s) && !s.Dead && s.Received > kv.Value + 1.5f)
                {
                    deadHere.Remove(kv.Key);
                    continue;
                }
                if (MarkDead(e)) DeadRekilled++;
            }
        }
    }
}
