using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Cysharp.Threading.Tasks;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        public const string CombatArea = "003_Vitromotus";
        // "#key=value" lines from the other copy, for the running scenario (see SelfTest.OnControl).
        private static Dictionary<string, string> peerData => Bucket(scenarioIndex).data;

        private static void HookPeerData() { }   // control lines are always collected (SelfTest.Run)

        private static IEnumerator TeleportTo(string areaName, Vector2 pos)
        {
            var area = Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(a => a.name == areaName);
            if (!Check($"room {areaName} exists", area != null)) yield break;
            Game.Manager.TeleportPlayerFade(pos, Direction.Right, area).Forget();
            yield return WaitFor(() => Game.AreaName == areaName && Game.Manager.gameState == GameState.Playing, 20f);
            Check($"arrived in {areaName}", Game.AreaName == areaName);
        }

        private static IEnumerable<Enemy> EnemiesIn(string area) =>
            EnemiesManager.AllEnemies.Where(e => e != null && e.isActiveAndEnabled && EnemySync.Instance.AreaOf(e) == area);

        private static void DumpBlackboards(string area)
        {
            var sb = new StringBuilder();
            var seen = new HashSet<string>();
            foreach (var e in EnemiesIn(area))
            {
                var agent = e.behaviorGraphAgent;
                if (agent == null || agent.Graph == null || !seen.Add(agent.Graph.name)) continue;
                sb.AppendLine($"graph {agent.Graph.name} ({e.name})");
                foreach (var v in agent.BlackboardReference.Blackboard.Variables)
                    sb.AppendLine($"  {v.Name}: {v.Type?.Name} = {v.ObjectValue}");
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, "blackboards.txt"), sb.ToString());
        }

        // Stage 2: the host runs the enemies, the guest sees them, hits them, kills them and gets hit by them.
        [Scenario("combat", Timeout = 150, Quick = true)]
        private static IEnumerator CombatTest()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            if (!Check("session exists", s != null)) { yield return Finish(""); yield break; }
            yield return WaitFor(() => s.Ready && s.Players.Count >= 2, 60f);
            yield return Barrier("ingame", 60f);

            // Host waits at the room's entrance; the guest walks in among the mushrooms, so enemies pick the guest.
            Vector2 entrance = new Vector2(51.8f, 76.3f);
            yield return TeleportTo(CombatArea, s.IsHost ? entrance : new Vector2(58f, 86f));
            yield return Barrier("arrived", 40f);
            var es = EnemySync.Instance;

            if (s.IsHost)
            {
                DumpBlackboards(CombatArea);
                yield return new WaitForSecondsRealtime(2f);
                // Publish where our enemies are so the guest can compare.
                var list = string.Join(";", EnemiesIn(CombatArea).Where(e => !e.IsDead).Select(e =>
                    $"{es.IdOf(e)}:{e.transform.position.x.ToString("F2", CultureInfo.InvariantCulture)}:{e.transform.position.y.ToString("F2", CultureInfo.InvariantCulture)}"));
                Chat.Send("#enemies=" + list);
                yield return Barrier("compared");

                // The guest hits an enemy: report its health to the guest.
                yield return WaitFor(() => peerData.ContainsKey("hitid"), 30f);
                if (peerData.TryGetValue("hitid", out var hitText))
                {
                    var e = es.ById(int.Parse(hitText));
                    yield return WaitFor(() => e != null && e.CurrentHealth < e.MaxHealth, 5f);
                    Check("guest's hit lowered the host's enemy health", e != null && e.CurrentHealth < e.MaxHealth, e == null ? "no enemy" : $"{e.CurrentHealth}/{e.MaxHealth}");
                    Chat.Send("#hosthp=" + (e != null ? e.CurrentHealth.ToString(CultureInfo.InvariantCulture) : "-1"));
                    yield return WaitFor(() => e == null || e.IsDead, 10f);
                    Check("guest's finishing blow killed the host's enemy", e != null && e.IsDead);
                }
                yield return Barrier("killed");
                // The guest fires a projectile at an enemy: we show it, and our copy must not hurt anyone.
                yield return WaitFor(() => peerData.ContainsKey("shotat"), 20f);
                var shotAt = peerData.TryGetValue("shotat", out var sa) ? es.ById(int.Parse(sa)) : null;
                float hpBeforeShot = shotAt != null ? shotAt.CurrentHealth : -1;
                yield return WaitFor(() => PlayerFx.Instance.ProjectilesShown > 0, 5f);
                Check("guest's projectile shows on the host", PlayerFx.Instance.ProjectilesShown > 0, $"{PlayerFx.Instance.ProjectilesShown} shown");
                yield return new WaitForSecondsRealtime(1.5f);
                Check("the shown copy dealt no damage", shotAt == null || shotAt.CurrentHealth >= hpBeforeShot, $"{hpBeforeShot} -> {(shotAt != null ? shotAt.CurrentHealth : -1)}");
                // Enemies should now be chasing and hitting the guest (only an enemy's attack counts: not our own hits).
                yield return WaitFor(() => es.EnemyAttacksForwarded > 0, 30f);
                if (!Check("an enemy attack on the guest was forwarded", es.EnemyAttacksForwarded > 0, $"{es.EnemyAttacksForwarded} by enemies ({es.AttacksForwarded} in all), {MPRunner.Instance.World.Retargets} retargets"))
                {
                    var guest = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault(r => r.Puppet != null);
                    if (guest != null)
                    {
                        var box = guest.Puppet.GetComponentInChildren<RemotePlayerHurtbox>(true);
                        Plugin.Log.LogInfo($"[test] guest's puppet at {guest.Puppet.transform.position} shown {guest.Shown} area '{guest.Latest?.Area}' (ours '{Game.AreaName}'), hurtbox {(box != null && box.isActiveAndEnabled && box.GetComponent<Collider2D>().enabled ? "on" : "off")}");
                        DumpFight(guest.Puppet.transform.position);
                    }
                    foreach (var line in es.RecentStarts) Plugin.Log.LogInfo("[test] attack start " + line);
                }
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("enemies"), 20f);
                yield return new WaitForSecondsRealtime(0.3f);
                Check("guest hands its enemies to the host", EnemySync.GuestPuppets);
                int compared = 0, close = 0;
                float worst = 0;
                if (peerData.TryGetValue("enemies", out var listText))
                    foreach (var item in listText.Split(';').Where(x => x.Length > 0))
                    {
                        var parts = item.Split(':');
                        var e = es.ById(int.Parse(parts[0]));
                        if (e == null) continue;
                        compared++;
                        var hp = new Vector2(float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture));
                        float d = Vector2.Distance(hp, e.transform.position);
                        worst = Mathf.Max(worst, d);
                        if (d < 1.0f) close++;
                    }
                Check("guest's enemies match the host's", compared > 0 && close >= compared * 0.8f, $"{close}/{compared} within 1 unit, worst {worst:F2}");
                Check("enemy states are flowing", es.StatesApplied > 0, $"{es.StatesApplied} applied");
                yield return Barrier("compared");

                // Hit the nearest enemy for a little, then finish it off.
                var me = Game.LocalPlayer;
                var target = EnemiesIn(CombatArea).Where(e => !e.IsDead).OrderBy(e => (e.transform.position - me.transform.position).sqrMagnitude).FirstOrDefault();
                if (Check("found an enemy to hit", target != null))
                {
                    int id = es.IdOf(target);
                    float before = target.CurrentHealth;
                    int xpBefore = TotalXp();
                    // Enemies can block or parry; a few hits make sure some damage lands.
                    for (int n = 0; n < 4; n++)
                    {
                        CombatSystem.Resolve(new DamageData { Aggressor = me, Target = target, Amount = 6, Effects = new List<DamageEffect>(), Processors = new List<DamageDataProcessor>() });
                        yield return new WaitForSecondsRealtime(0.15f);
                    }
                    Chat.Send("#hitid=" + id);
                    yield return WaitFor(() => peerData.ContainsKey("hosthp"), 10f);
                    yield return new WaitForSecondsRealtime(0.5f);
                    float hostHp = peerData.TryGetValue("hosthp", out var h) ? float.Parse(h, CultureInfo.InvariantCulture) : -1;
                    Check("puppet health follows the host", Mathf.Abs(target.CurrentHealth - hostHp) < 0.01f && target.CurrentHealth < before, $"puppet {target.CurrentHealth}, host {hostHp}, before {before}");
                    CombatSystem.Resolve(new DamageData { Aggressor = me, Target = target, Amount = 99999, Effects = new List<DamageEffect>(), Processors = new List<DamageDataProcessor>() });
                    yield return WaitFor(() => target.IsDead, 5f);
                    Check("enemy died on the guest too", target.IsDead);
                    yield return new WaitForSecondsRealtime(0.5f);
                    Check("guest got XP for the kill", TotalXp() > xpBefore, $"{xpBefore} -> {TotalXp()}");
                }
                yield return Barrier("killed");
                // Fire a projectile at the nearest enemy (any projectile attack will do for the visuals).
                var proj = Resources.FindObjectsOfTypeAll<Attack>().FirstOrDefault(a => a.projectile != null);
                var aim = EnemiesIn(CombatArea).Where(e => !e.IsDead).OrderBy(e => (e.transform.position - me.transform.position).sqrMagnitude).FirstOrDefault();
                if (Check("found a projectile attack and a target", proj != null && aim != null))
                {
                    Chat.Send("#shotat=" + es.IdOf(aim));
                    yield return new WaitForSecondsRealtime(0.3f);
                    PlayerFx.Instance.OnLocalProjectile(proj, me.transform.position + Vector3.up * 0.6f, (aim.transform.position - me.transform.position).normalized);
                }
                // Walk up to the nearest living enemy so it notices us (detection range is ~5 units).
                var near = EnemiesIn(CombatArea).Where(e => !e.IsDead).OrderBy(e => (e.transform.position - me.transform.position).sqrMagnitude).FirstOrDefault();
                if (near != null)
                {
                    Vector2 from = me.rb.position, to = (Vector2)near.transform.position + Vector2.left * 1.2f;
                    float t0 = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - t0 < 1f)
                    {
                        var p = Vector2.Lerp(from, to, (Time.realtimeSinceStartup - t0) / 1f);
                        me.rb.position = p;
                        me.transform.position = p;
                        yield return null;
                    }
                    Plugin.Log.LogInfo($"[test] Guest moved next to {near.name} at {near.transform.position}");
                }
                int hpBefore = me.CurrentHealthPoints;
                yield return WaitFor(() => es.AttacksTaken > 0, 30f);
                yield return new WaitForSecondsRealtime(0.3f);
                if (!Check("an enemy hit the guest", es.AttacksTaken > 0, $"{es.AttacksTaken} attacks taken, hp {hpBefore} -> {me.CurrentHealthPoints}"))
                {
                    Plugin.Log.LogInfo($"[test] guest at {me.transform.position} in '{Game.AreaName}', {es.AttackStartsApplied} attack starts replayed here");
                    DumpFight(me.transform.position);
                }
                // Some hits (a bomber's self-destruct) don't start a regular attack; give a regular one time to come.
                yield return WaitFor(() => es.AttackStartsApplied > 0, 15f);
                Check("enemy attacks replay on the guest (animations, projectiles)", es.AttackStartsApplied > 0, $"{es.AttackStartsApplied} replayed");
            }

            yield return DevHarness.Screenshot(s.IsHost ? "combat-host" : "combat-guest");
            Plugin.Log.LogInfo($"[enemy] sent={es.StatesSent} applied={es.StatesApplied} hits={es.HitsSent}/{es.HitsApplied} deaths={es.DeathsSent}/{es.DeathsApplied} attacks={es.AttacksForwarded}/{es.AttacksTaken} starts={es.AttackStartsSent}/{es.AttackStartsApplied}");
            yield return Barrier("done");
            yield return Finish("co-op combat works");
        }

        // What the enemies around a spot are doing (a combat check failed): who they chase, their graph's state, their
        // group's sight and last-seen spot.
        private static void DumpFight(Vector3 at)
        {
            var es = EnemySync.Instance;
            var sb = new StringBuilder();
            foreach (var e in EnemiesManager.AllEnemies.Where(x => x != null && x.isActiveAndEnabled && !x.IsDead)
                .OrderBy(x => (x.transform.position - at).sqrMagnitude).Take(6))
            {
                string state;
                try { state = e.State != null ? e.State.Value.ToString() : "-"; } catch { state = "?"; }
                var chased = EnemyCombat.ChasedBy(e);
                var g = e.Group;
                sb.Append($"\n  {e.name}#{es.IdOf(e)} at {e.transform.position} ({Vector2.Distance(e.transform.position, at):F1} away) area '{es.AreaOf(e)}'")
                  .Append($" state {e.currentState?.GetType().Name}/{state} attacking {e.isAttacking} puppet {es.IsPuppet(e)}")
                  .Append($" chases {(chased == null ? "nobody" : chased == Game.LocalPlayer?.gameObject ? "us" : chased.name)}")
                  .Append($" graph {(e.behaviorGraphAgent != null && e.behaviorGraphAgent.enabled ? "on" : "off")}")
                  .Append(g == null ? " no group" : $" group {g.name} engaged {g.IsEngagedWithPlayer} sight {g.IsInSightOfPlayer.Count} forced {g.IsForcedToPosition} last seen {g.LastPlayerSeenPosition?.ToString() ?? "-"}");
            }
            Plugin.Log.LogInfo($"[test] enemies around {at}:{sb}");
        }

        private static int TotalXp()
        {
            var pd = GameData.playerData;
            if (pd == null) return 0;
            var all = pd.playerXPDictionary != null && pd.playerXPDictionary.Count > 0 ? pd.playerXPDictionary.Values : (IEnumerable<PlayerExperience>)pd.playerXP;
            return all?.Sum(x => x.TotalXP) ?? 0;
        }
    }
}
