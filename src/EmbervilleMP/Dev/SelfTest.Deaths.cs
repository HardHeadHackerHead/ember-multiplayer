using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        private static bool Announced(System.Func<string, bool> match) => Deaths.History.Any(match);
        private static string AnnouncedLine(System.Func<string, bool> match) => Deaths.History.LastOrDefault(match) ?? "(none) last: " + Deaths.Last;

        // Death announcements: the host's enemy kills the guest (the hit goes through the host like any enemy hit), the
        // host dies of an enemy's poison, the guest dies on a trap, then both bring the Spider Matriarch down. Everyone
        // reads who died, of what, who got back up and who beat the boss.
        [Scenario("deaths", Timeout = 240)]
        private static IEnumerator DeathsTest()
        {
            FastBossIntro.On();   // (her intro is woken through, not watched)
            DevHarness.FastDeath = true;   // the announcements are checked, not the death animation
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) { yield return Finish(""); yield break; }
            Deaths.History.Clear();   // chained runs: only this scenario's lines
            DeathBanner.LastTitle = DeathBanner.LastSubtitle = "";
            string hostName = s.IsHost ? s.LocalName : s.Players.TryGetValue(Protocol.HostId, out var hp) ? hp.Name : "Hosty";
            string guestName = s.IsHost ? s.Others.Select(p => p.Name).FirstOrDefault() ?? "Guesty" : s.LocalName;
            var me = Game.LocalPlayer;
            me.Invincible = true;   // nothing but the test's own blows until each stage wants us dead
            yield return TeleportTo(CombatArea, s.IsHost ? new Vector2(51.8f, 76.3f) : new Vector2(53f, 77f));
            yield return Barrier("arrived", 40f);
            me = Game.LocalPlayer;
            me.Invincible = true;

            // ---- 1. an enemy kills the guest ----
            if (s.IsHost)
            {
                RemotePlayer guest = null;
                yield return WaitFor(() => (guest = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault(r => r.Target != null && r.Shown)) != null, 15f);
                var killer = guest != null ? NearestAlive(CombatArea, guest.Puppet.transform.position) : null;
                // Any enemy of the room may get there first: accept any of their names.
                var names = EnemiesIn(CombatArea).Select(e => Deaths.EnemyName(e)).Distinct().ToList();
                yield return Barrier("guest-vulnerable", 20f);
                if (Check("an enemy and the guest's puppet to hit", guest != null && killer != null))
                {
                    Plugin.Log.LogInfo($"[test] {killer.name} ({Deaths.EnemyName(killer)}) attacks {guestName}; names in the room: {string.Join(", ", names)}");
                    System.Func<string, bool> guestKilled = l => l.StartsWith(guestName + " ") && l.Contains(" by ") && names.Any(n => l.Contains(n));
                    float t0 = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - t0 < 15f && !Announced(guestKilled))
                    {
                        // The guest may dodge or parry: a few blows.
                        if (!guest.Target.Dead && !killer.IsDead) CombatSystem.Resolve(Hit(killer, guest.Target, 99999));
                        yield return WaitFor(() => Announced(guestKilled), 1f);
                    }
                    Check("host announces the guest's death by the enemy", Announced(guestKilled), AnnouncedLine(guestKilled));
                    Check("an absurd killing blow shows no damage number", !Announced(l => guestKilled(l) && l.Contains("9999")), AnnouncedLine(guestKilled));
                    Check("host shows the guest's banner", DeathBanner.LastTitle.Contains(guestName) && DeathBanner.LastSubtitle.Contains(" by "), $"'{DeathBanner.LastTitle}' / '{DeathBanner.LastSubtitle}'");
                    yield return DevHarness.Screenshot("deaths-guest-killed-host", 0.4f);
                }
                yield return WaitFor(() => Announced(l => l.StartsWith(guestName + " is back on")), 40f);
                Check("host sees the guest get back up", Announced(l => l.StartsWith(guestName + " is back on")), Deaths.Last);
            }
            else
            {
                me.ResetToFullHealth();
                me.Invincible = false;
                yield return Barrier("guest-vulnerable", 20f);
                yield return WaitFor(() => me.IsDead, 20f);
                Check("guest was killed by the host's enemy", me.IsDead);
                Check("guest's own line names the enemy", Announced(l => l.StartsWith("You were") && l.Contains(" by ")), AnnouncedLine(l => l.StartsWith("You")));
                Check("guest's banner", DeathBanner.LastTitle == "You have fallen", DeathBanner.LastTitle);
                yield return DevHarness.Screenshot("deaths-self-guest", 0.4f);
                yield return WaitFor(() => !me.IsDead && Game.InGame && Game.Manager.gameState == GameState.Playing, 30f);
                Check("guest got back up", !me.IsDead);
                // Announced a moment after the character stands again (not in the respawn frame itself).
                yield return WaitFor(() => Announced(l => l.StartsWith("You are back on")), 5f);
                Check("guest's own 'back up' line", Announced(l => l.StartsWith("You are back on")), Deaths.Last);
                me = Game.LocalPlayer;
                me.Invincible = true;
            }
            yield return Barrier("stage1", 60f);

            // ---- 2. the host is poisoned to death by an enemy; meanwhile the guest steps on a trap ----
            const string trapRoom = "05_Trap_Room2";
            if (s.IsHost)
            {
                me = Game.LocalPlayer;
                var toxic = EnemiesIn(CombatArea).Where(e => !e.IsDead && e.enemyType == EnemyType.Mob_Toxic).OrderBy(e => NetIds.ScenePath(e.transform)).FirstOrDefault()
                    ?? NearestAlive(CombatArea, me.transform.position);
                string toxicName = toxic != null ? Deaths.EnemyName(toxic) : "?";
                me.ResetToFullHealth();
                me.Invincible = false;
                me.ApplyDamage(new DamageData
                {
                    Aggressor = toxic, Target = me, Amount = 1, Result = DamageResult.Success,
                    Effects = new List<DamageEffect> { new DamageEffect { Effect = EffectType.Poison, Chance = 100f, DamagePerSec = me.MaxHealthPoints * 2, Duration = 5f } },
                    CreatedEffects = new List<Effect>(), Processors = new List<DamageDataProcessor>(),
                });
                yield return WaitFor(() => me.IsDead, 8f);
                Check("host died of poison", me.IsDead);
                System.Func<string, bool> poisoned = l => l.StartsWith("You were poisoned by") && l.Contains(toxicName);
                Check("host's own line: poisoned by the enemy", Announced(poisoned), AnnouncedLine(l => l.StartsWith("You")));
                Check("host's banner says how", DeathBanner.LastTitle == "You have fallen" && DeathBanner.LastSubtitle.StartsWith("Poisoned by"), $"'{DeathBanner.LastTitle}' / '{DeathBanner.LastSubtitle}'");
                yield return DevHarness.Screenshot("deaths-poisoned-host", 0.4f);
                yield return WaitFor(() => !me.IsDead && Game.InGame && Game.Manager.gameState == GameState.Playing, 30f);
                Check("host got back up", !me.IsDead);
                me = Game.LocalPlayer;
                me.Invincible = true;
                System.Func<string, bool> trapped = l => l.StartsWith(guestName + " ") && l.Contains("trap");
                yield return WaitFor(() => Announced(trapped), 45f);
                Check("host hears the guest died on a trap", Announced(trapped), AnnouncedLine(l => l.StartsWith(guestName)));
                yield return DevHarness.Screenshot("deaths-chat-host", 0.4f);
            }
            else
            {
                System.Func<string, bool> hostPoisoned = l => l.StartsWith(hostName + " was poisoned by");
                yield return WaitFor(() => Announced(hostPoisoned), 20f);
                Check("guest hears the host was poisoned by the enemy", Announced(hostPoisoned), AnnouncedLine(l => l.StartsWith(hostName)));
                Check("guest shows the host's banner", DeathBanner.LastTitle == hostName + " has fallen!", DeathBanner.LastTitle);
                var groundTrap = Object.FindObjectsByType<GroundTrap>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Where(t => AreaOf(t) == trapRoom).OrderBy(t => NetIds.ScenePath(t.transform)).FirstOrDefault();
                if (Check("found a ground trap", groundTrap != null))
                {
                    yield return TeleportTo(trapRoom, (Vector2)groundTrap.transform.position + Vector2.down * 1.5f);
                    me = Game.LocalPlayer;
                    yield return MoveTo(me, groundTrap.transform.position, 0.3f);
                    me.currentHealth = 1;
                    me.Invincible = false;
                    float t0 = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - t0 < 8f && !me.IsDead)
                    {
                        me.rb.position = groundTrap.transform.position;
                        yield return null;
                    }
                    Check("guest died on the trap", me.IsDead);
                    Check("guest's own line names the trap", Announced(l => l.StartsWith("You ") && l.Contains("trap")), AnnouncedLine(l => l.StartsWith("You")));
                    yield return DevHarness.Screenshot("deaths-trap-guest", 0.4f);
                    yield return WaitFor(() => !me.IsDead && Game.InGame && Game.Manager.gameState == GameState.Playing, 30f);
                    me = Game.LocalPlayer;
                    me.Invincible = true;
                }
            }
            yield return Barrier("stage2", 60f);

            // ---- 3. the boss falls to both ----
            const string bossRoom = "SpiderMatriarch_Area";
            yield return TeleportTo(bossRoom, s.IsHost ? new Vector2(410f, 262f) : new Vector2(412f, 262f));
            yield return Barrier("boss-arrived", 40f);
            me = Game.LocalPlayer;
            me.Invincible = true;
            var cine = Object.FindAnyObjectByType<MatriarchBossCinematic>();
            if (cine != null)
            {
                if (!s.IsHost) yield return new WaitForSecondsRealtime(FastBossIntro.SecondPlayerDelay);
                var trigger = cine.GetComponent<Collider2D>();
                yield return MoveTo(me, trigger != null ? (Vector2)trigger.bounds.center : (Vector2)cine.transform.position, 1f);
            }
            Enemy boss = null;
            yield return WaitFor(() => (boss = EnemiesIn(bossRoom).FirstOrDefault(e => e.name.Contains("Boss") && !e.IsDead)) != null, 25f);
            if (Check("boss is here", boss != null))
            {
                string bossName = Deaths.EnemyName(boss);
                Plugin.Log.LogInfo($"[test] Boss {boss.name} reads as '{bossName}'");
                Check("boss has a readable name", bossName.Length > 0 && !bossName.Contains("_"), bossName);
                if (s.IsHost)
                {
                    float t0 = Time.realtimeSinceStartup;
                    while (!boss.IsDead && Time.realtimeSinceStartup - t0 < 25f)
                    {
                        if (boss.isActiveAndEnabled) CombatSystem.Resolve(Hit(me, boss, 999999));
                        yield return new WaitForSecondsRealtime(0.5f);
                    }
                    Check("host brought the boss down", boss.IsDead);
                }
                System.Func<string, bool> defeated = l => l.StartsWith(bossName + " was defeated by");
                yield return WaitFor(() => Announced(defeated), 30f);
                Check("boss defeat announced", Announced(defeated), AnnouncedLine(l => l.Contains("defeated")));
                Check("both players are named", Announced(l => defeated(l) && l.Contains(hostName) && l.Contains(guestName)), AnnouncedLine(defeated));
                Check("boss banner", DeathBanner.LastTitle == bossName + " defeated!", DeathBanner.LastTitle);
                yield return DevHarness.Screenshot(s.IsHost ? "deaths-boss-host" : "deaths-boss-guest", 0.4f);
            }
            Plugin.Log.LogInfo("[test] Announcements:\n  " + string.Join("\n  ", Deaths.History));
            yield return Barrier("done", 40f);
            yield return Finish("deaths are announced with their cause");
        }
    }
}
