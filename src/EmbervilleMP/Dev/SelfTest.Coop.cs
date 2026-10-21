using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        private static DamageData Hit(IDamageable from, IDamageable to, int amount) => new DamageData
        {
            Aggressor = from, Target = to, Amount = amount,
            Effects = new List<DamageEffect>(), Processors = new List<DamageDataProcessor>(),
        };

        private static Enemy NearestAlive(string area, Vector3 to) =>
            EnemiesIn(area).Where(e => !e.IsDead).OrderBy(e => (e.transform.position - to).sqrMagnitude).FirstOrDefault();

        private static IEnumerator MoveTo(PlayerMaster me, Vector2 to, float seconds)
        {
            Vector2 from = me.rb.position;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < seconds)
            {
                var p = Vector2.Lerp(from, to, (Time.realtimeSinceStartup - t0) / seconds);
                me.rb.position = p;
                me.transform.position = p;
                yield return null;
            }
        }

        private static IEnumerator SessionReady()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            Check("session exists", s != null);
            if (s == null) yield break;
            yield return WaitFor(() => s.Ready && s.Players.Count >= 2, 60f);
            yield return Barrier("ingame", 60f);
        }

        // The guest fights alone in a room the host isn't in: the host keeps that room running.
        [Scenario("split", Timeout = 150)]
        private static IEnumerator SplitTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            var es = EnemySync.Instance;
            const string guestRoom = "004_Gather";
            if (s.IsHost) yield return TeleportTo(CombatArea, new Vector2(51.8f, 76.3f));
            else yield return TeleportTo(guestRoom, new Vector2(150f, 130f));
            yield return Barrier("placed", 40f);
            if (s.IsHost)
            {
                yield return WaitFor(() => Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(a => a.name == guestRoom && a.gameObject.activeSelf), 10f);
                var room = Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).First(a => a.name == guestRoom);
                Check("host keeps the guest's room running", room.gameObject.activeSelf);
                Check("host is still in its own room", Game.AreaName == CombatArea);
                yield return WaitFor(() => peerData.ContainsKey("splitkill"), 40f);
                var e = peerData.TryGetValue("splitkill", out var idText) ? es.ById(int.Parse(idText)) : null;
                yield return WaitFor(() => e != null && e.IsDead, 10f);
                Check("guest's kill in the other room happened on the host", e != null && e.IsDead);
            }
            else
            {
                yield return WaitFor(() => EnemySync.GuestPuppets && es.StatesApplied > 20, 20f);
                Check("enemies in the guest's room follow the host", EnemySync.GuestPuppets && es.StatesApplied > 20, $"{es.StatesApplied} applied");
                var me = Game.LocalPlayer;
                var target = NearestAlive(guestRoom, me.transform.position);
                if (Check("found an enemy in the guest's room", target != null))
                {
                    Chat.Send("#splitkill=" + es.IdOf(target));
                    CombatSystem.Resolve(Hit(me, target, 99999));
                    yield return WaitFor(() => target.IsDead, 5f);
                    Check("it died on the guest", target.IsDead);
                }
            }
            yield return DevHarness.Screenshot(s.IsHost ? "split-host" : "split-guest");
            yield return Barrier("done", 40f);
            yield return Finish("split rooms work");
        }

        // A death in co-op puts the player back without rewinding the enemies.
        [Scenario("death", Timeout = 170, Quick = true)]
        private static IEnumerator DeathTest()
        {
            DevHarness.FastDeath = true;   // what happens around the deaths is checked, not their animation
            yield return SessionReady();
            var s = Session.Current;
            var es = EnemySync.Instance;
            yield return TeleportTo(CombatArea, s.IsHost ? new Vector2(51.8f, 76.3f) : new Vector2(53f, 77f));
            yield return Barrier("arrived", 40f);
            var me = Game.LocalPlayer;
            if (s.IsHost)
            {
                // Kill an enemy, then die: it must stay dead (no rewind with a guest around).
                var victim = NearestAlive(CombatArea, me.transform.position);
                Chat.Send("#victim=" + es.IdOf(victim));
                CombatSystem.Resolve(Hit(me, victim, 99999));
                yield return WaitFor(() => victim.IsDead, 5f);
                Check("host killed an enemy", victim.IsDead);
                yield return Barrier("victim-dead");
                me.ApplyDamage(Hit(null, me, 99999));
                yield return WaitFor(() => me.IsDead, 5f);
                Check("host died", me.IsDead);
                yield return WaitFor(() => !me.IsDead && Game.InGame && Game.Manager.gameState == GameState.Playing, 30f);
                Check("host respawned", !me.IsDead && Game.InGame);
                yield return new WaitForSecondsRealtime(1f);
                Check("enemy the host killed stayed dead", victim.IsDead || !victim.gameObject.activeInHierarchy);
                yield return Barrier("host-respawned", 40f);
                yield return Barrier("guest-respawned", 60f);
                Check("enemy still dead after the guest's death", victim.IsDead || !victim.gameObject.activeInHierarchy);
            }
            else
            {
                yield return Barrier("victim-dead");
                var victim = peerData.TryGetValue("victim", out var v) ? es.ById(int.Parse(v)) : null;
                yield return WaitFor(() => victim != null && victim.IsDead, 5f);
                Check("guest saw the host's kill", victim != null && victim.IsDead);
                yield return Barrier("host-respawned", 60f);
                yield return new WaitForSecondsRealtime(1f);
                Check("enemy stays dead on the guest after the host's death", victim == null || victim.IsDead || !victim.gameObject.activeInHierarchy);
                me.ApplyDamage(Hit(null, me, 99999));
                yield return WaitFor(() => me.IsDead, 5f);
                Check("guest died", me.IsDead);
                yield return WaitFor(() => !me.IsDead && Game.InGame && Game.Manager.gameState == GameState.Playing, 30f);
                Check("guest respawned", !me.IsDead && Game.InGame);
                yield return new WaitForSecondsRealtime(1f);
                Check("enemy stays dead after the guest's own death", victim == null || victim.IsDead || !victim.gameObject.activeInHierarchy);
                Check("guest still follows the host's enemies", EnemySync.GuestPuppets);
                yield return Barrier("guest-respawned", 40f);
            }
            yield return DevHarness.Screenshot(s.IsHost ? "death-host" : "death-guest");
            yield return Barrier("done", 40f);
            yield return Finish("deaths don't rewind the shared fight");
        }

        // No multiplayer: does the boss intro by itself throw (to tell the game's own errors from ours)?
        [Scenario("bosssolo")]
        private static IEnumerator BossSoloTest()
        {
            int exceptions = 0;
            Application.logMessageReceived += (c, st, t) => { if (t == LogType.Exception) exceptions++; };
            yield return WaitInGame();
            const string bossRoom = "SpiderMatriarch_Area";
            yield return TeleportTo(bossRoom, new Vector2(410f, 262f));
            var me = Game.LocalPlayer;
            me.Invincible = true;
            var cine = Object.FindAnyObjectByType<MatriarchBossCinematic>();
            var trigger = cine != null ? cine.GetComponent<Collider2D>() : null;
            if (cine != null) yield return MoveTo(me, trigger != null ? (Vector2)trigger.bounds.center : (Vector2)cine.transform.position, 1f);
            int before = exceptions;
            yield return new WaitForSecondsRealtime(25f);
            Check("boss intro and fight run without exceptions (no multiplayer)", exceptions == before, $"{exceptions - before} exceptions");
            yield return Finish("solo boss");
        }

        // The Spider Matriarch: boss health stays in step, her spiderlings appear for the guest too.
        [Scenario("boss", Timeout = 240)]
        private static IEnumerator BossTest()
        {
            FastBossIntro.On();   // (her intro is woken through, not watched)
            yield return SessionReady();
            var s = Session.Current;
            var es = EnemySync.Instance;
            const string bossRoom = "SpiderMatriarch_Area";
            yield return TeleportTo(bossRoom, s.IsHost ? new Vector2(410f, 262f) : new Vector2(412f, 262f));
            yield return Barrier("arrived", 40f);
            var me = Game.LocalPlayer;
            // Walk into the intro trigger (the host first; the guest a little later).
            var cine = Object.FindAnyObjectByType<MatriarchBossCinematic>();
            if (!Check("boss intro trigger found", cine != null)) { yield return Finish(""); yield break; }
            if (!s.IsHost) yield return new WaitForSecondsRealtime(FastBossIntro.SecondPlayerDelay);
            var trigger = cine.GetComponent<Collider2D>();
            yield return MoveTo(me, trigger != null ? (Vector2)trigger.bounds.center : (Vector2)cine.transform.position, 1f);
            Enemy boss = null;
            yield return WaitFor(() => (boss = EnemiesIn(bossRoom).FirstOrDefault(e => e.name.Contains("Boss") && !e.IsDead)) != null, 25f);
            if (!Check("boss is here", boss != null)) { yield return Finish(""); yield break; }
            if (s.IsHost) DumpBlackboards(bossRoom);
            // Both chip at the boss for a while so it fights back and lays eggs.
            float t0 = Time.realtimeSinceStartup;
            int hits = 0;
            bool toldSeen = false;
            while (Time.realtimeSinceStartup - t0 < 70f && !boss.IsDead)
            {
                // Her stages change at 80% (eggs, she heals) and 40% health (spiderlings). Light hits, none below 32%: she
                // gets under 40% (her eggs heal her), and the last hits (two players, crits: 160 at most) never kill her.
                if (boss.isActiveAndEnabled && boss.HealthPercent > 0.32f)
                {
                    CombatSystem.Resolve(Hit(me, boss, 40));
                    hits++;
                }
                if (me.IsDead) yield return WaitFor(() => !me.IsDead, 20f);
                yield return new WaitForSecondsRealtime(0.75f);   // (the spawns we wait for come sooner; the 32% floor still holds)
                // Until both have seen what the checks below look at (laid eggs aren't checked: not every phase lays
                // them, and waiting for them kept the fight going its full 70 s): then both stop together.
                // (and her health at the 32% floor, where the hits stop: the health check compares a settled fight)
                bool seen = boss.HealthPercent <= 0.32f
                    && (s.IsHost ? es.SpawnsSent > 0 : es.SpawnsApplied > 0 && es.AttackStartsApplied > 0);
                if (seen && !toldSeen) { Chat.Send("#bossseen=1"); toldSeen = true; }
                if (seen && peerData.ContainsKey("bossseen")) break;
                if (me.IsDead || me.CurrentHealthPoints < me.MaxHealthPoints / 3) me.ResetToFullHealth();
            }
            yield return new WaitForSecondsRealtime(3f);
            Chat.Send("#bosshp=" + boss.CurrentHealth.ToString(CultureInfo.InvariantCulture));
            yield return WaitFor(() => peerData.ContainsKey("bosshp"), 10f);
            float other = peerData.TryGetValue("bosshp", out var bh) ? float.Parse(bh, CultureInfo.InvariantCulture) : -1;
            Check("boss health is the same on both", Mathf.Abs(other - boss.CurrentHealth) < boss.MaxHealth * 0.05f, $"mine {boss.CurrentHealth}, theirs {other}, max {boss.MaxHealth}, {hits} hits by me");
            var eggs = EggSync.Instance;
            if (s.IsHost)
            {
                Check("boss spawned enemies were announced", es.SpawnsSent > 0, $"{es.SpawnsSent} spawns");
                Plugin.Log.LogInfo($"[egg] {eggs.Spawned} eggs laid, {eggs.Broken} broken/hatched"); // not every phase lays eggs
            }
            else
            {
                Plugin.Log.LogInfo($"[egg] {eggs.SpawnApplied} eggs shown, {eggs.BrokenApplied} broken/hatched");
                Check("boss spawns appeared on the guest", es.SpawnsApplied > 0, $"{es.SpawnsApplied} applied, {es.LocalSpawnsDropped} local dropped");
                Check("boss attacks replayed on the guest", es.AttackStartsApplied > 0, $"{es.AttackStartsApplied}");
            }
            yield return DevHarness.Screenshot(s.IsHost ? "boss-host" : "boss-guest");
            Plugin.Log.LogInfo($"[enemy] sent={es.StatesSent} applied={es.StatesApplied} hits={es.HitsSent}/{es.HitsApplied} deaths={es.DeathsSent}/{es.DeathsApplied} attacks={es.AttacksForwarded}/{es.AttacksTaken} starts={es.AttackStartsSent}/{es.AttackStartsApplied} spawns={es.SpawnsSent}/{es.SpawnsApplied}");
            yield return Barrier("done", 40f);
            yield return Finish("boss fight in sync");
        }
    }
}
