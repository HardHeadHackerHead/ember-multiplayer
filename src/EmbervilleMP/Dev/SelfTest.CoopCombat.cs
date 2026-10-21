using System.Collections;
using System.Globalization;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // No friendly fire: the host's sword, a hit on the puppet itself, a bomb's hit and an arrow on the guest's puppet
        // reach nobody (and the arrow flies on); an enemy's hit on the same puppet still reaches the guest.
        [Scenario("friendlyfire", Timeout = 180, Quick = true)]
        private static IEnumerator FriendlyFireTest()
        {
            yield return TeamPlaced();
            var s = Session.Current;
            var es = EnemySync.Instance;
            var me = Game.LocalPlayer;
            var other = teamOther;
            var hurtbox = other != null && other.Puppet != null ? other.Puppet.GetComponentInChildren<RemotePlayerHurtbox>() : null;
            if (s == null || !Check("the teammate's puppet can be hit", other != null && other.Target != null && hurtbox != null))
            {
                yield return Barrier("done");
                yield return Finish("");
                yield break;
            }

            // 1. the host's hits on the guest's puppet
            int taken0 = es.AttacksTaken, hp0 = 0;
            if (!s.IsHost)
            {
                me.ResetToFullHealth();
                me.Invincible = false;
                hp0 = me.CurrentHealthPoints;
            }
            yield return Barrier("ff-ready");
            if (s.IsHost)
            {
                int dropped0 = FriendlyFire.HitsDropped, passed0 = FriendlyFire.ProjectilesPassed;
                int fwd0 = es.AttacksForwarded, enemyFwd0 = es.EnemyAttacksForwarded;
                CombatSystem.Resolve(Hit(me, hurtbox, 10));        // our sword: its hitbox finds the puppet's hurtbox
                CombatSystem.Resolve(Hit(me, other.Target, 10));   // the puppet itself
                var bomb = Hit(me, hurtbox, 10);                   // an explosion or area we own (Owner = us)
                bomb.Attack = PlayerAttacks().FirstOrDefault(a => a.attackSpawner != null);
                CombatSystem.Resolve(bomb);
                Check("our hits on the teammate's puppet were dropped", FriendlyFire.HitsDropped - dropped0 == 3, $"{FriendlyFire.HitsDropped - dropped0} of 3");

                // an arrow: a copy of one of our projectiles, touching the puppet's hurtbox
                var arrow = PlayerAttacks().FirstOrDefault(a => a.projectile != null && a.projectile.TryGetComponent<Projectile>(out var p) && !p.persistent && p.disableOnHit);
                var go = arrow != null ? FxCopies.Get(arrow.projectile, keepColliders: true) : null;
                if (go != null && go.TryGetComponent<Projectile>(out var proj))
                {
                    proj.transform.position = hurtbox.transform.position;
                    proj.Direction = Vector3.right;
                    proj.damageData = Hit(me, null, 10);
                    proj.damageData.Attack = arrow;
                    go.SetActive(true);
                    proj.OnTriggerEnter2D(hurtbox.GetComponent<Collider2D>());
                    Check("our arrow flies on through the teammate", go.activeSelf && FriendlyFire.ProjectilesPassed > passed0,
                        $"{arrow.name}: active {go.activeSelf}, passed {FriendlyFire.ProjectilesPassed - passed0}");
                    go.SetActive(false);
                }
                else Plugin.Log.LogInfo("[test] no player projectile to shoot (skipped the arrow)");
                int enemyHits = es.EnemyAttacksForwarded - enemyFwd0;
                Check("none of it went to the guest", es.AttacksForwarded - fwd0 == enemyHits, $"{es.AttacksForwarded - fwd0} forwarded, {enemyHits} by enemies");
                Chat.Send("#ffenemy=" + enemyHits);
            }
            yield return Barrier("ff-done");
            if (!s.IsHost)
            {
                yield return WaitFor(() => peerData.ContainsKey("ffenemy"), 10f);
                yield return new WaitForSecondsRealtime(0.5f);
                int enemyHits = peerData.TryGetValue("ffenemy", out var fe) ? int.Parse(fe) : -1;
                int taken = es.AttacksTaken - taken0;
                Check("the host's sword, bomb and arrow never reached us", taken == enemyHits, $"{taken} attacks taken, {enemyHits} by enemies meanwhile");
                if (enemyHits == 0) Check("our health is untouched", me.CurrentHealthPoints == hp0, $"{hp0} -> {me.CurrentHealthPoints}");
                me.Invincible = true;
            }

            // 2. an enemy's hit on the same puppet still goes to the guest
            int taken1 = es.AttacksTaken;
            yield return Barrier("enemy-ready");
            if (s.IsHost)
            {
                var enemy = EnemiesManager.AllEnemies.Where(x => x != null && es.IdOf(x) != 0)
                    .OrderBy(x => (x.transform.position - me.transform.position).sqrMagnitude).FirstOrDefault();
                int enemyFwd0 = es.EnemyAttacksForwarded, dropped0 = FriendlyFire.HitsDropped;
                if (Check("found an enemy", enemy != null))
                {
                    CombatSystem.Resolve(Hit(enemy, hurtbox, 1));
                    Check("an enemy's hit on the puppet is forwarded", es.EnemyAttacksForwarded == enemyFwd0 + 1 && FriendlyFire.HitsDropped == dropped0,
                        $"{es.EnemyAttacksForwarded - enemyFwd0} forwarded, {FriendlyFire.HitsDropped - dropped0} dropped");
                }
            }
            else
            {
                yield return WaitFor(() => es.AttacksTaken > taken1, 10f);
                Check("and it reached us", es.AttacksTaken > taken1, $"{es.AttacksTaken - taken1} taken");
            }
            Plugin.Log.LogInfo($"[friendlyfire] dropped {FriendlyFire.HitsDropped}, passed {FriendlyFire.ProjectilesPassed}, tag skips {FriendlyFire.TagSkips}");
            yield return Barrier("done");
            yield return Finish("no friendly fire; enemies still hit teammates");
        }

        private static Attack AttackOf(Enemy e) => e.attacks?.Select(a => a.Attack).Where(a => a != null)
            .OrderBy(a => a.projectile != null ? 0 : 1).ThenBy(a => a.name).FirstOrDefault();

        // Replayed enemy attacks and deaths on a non-runner: 1. an attack on the guest aims where the runner's does (the
        // guest as the runner's puppet of it shows it); 2. an interrupted attack stops on the puppet too; 3. a kill pops
        // one number (the killing blow), with the runner's gore roll, and the corpse slides where the runner's does.
        [Scenario("enemyreplay", Timeout = 200)]
        private static IEnumerator EnemyReplayTest()
        {
            Enemy e = null;
            yield return EnemyFxSetup(x => AttackOf(x) != null, x => e = x);
            var s = Session.Current;
            var es = EnemySync.Instance;
            var me = Game.LocalPlayer;
            if (me != null) me.Invincible = true;
            var other = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault(r => r.Shown);
            var attack = e != null ? AttackOf(e) : null;
            if (s == null || e == null || !Check("the enemy has an attack, the teammate is shown", attack != null && other != null, attack?.name ?? "no attack"))
            {
                yield return Barrier("done");
                yield return Finish("");
                yield break;
            }
            if (s.IsHost) e.DisableBehaviorGraph();   // its own AI would start and end attacks meanwhile

            // 1. an attack on the guest's puppet: the guest's replay aims at the runner's view of it
            int aims0 = es.AimsApplied;
            yield return Barrier("aim-ready");
            if (s.IsHost)
            {
                if (e.isAttacking) e.CancelAttack();
                int sent0 = es.AimsSent;
                e.PerformAttack(attack, other.Puppet);
                Check("the runner says where its attack on the guest aims", es.AimsSent == sent0 + 1, $"{attack.name}: {es.AimsSent - sent0} sent");
            }
            else
            {
                yield return WaitFor(() => es.AimsApplied > aims0, 5f);
                Check("our replay aims at the runner's puppet of us", es.AimsApplied > aims0, $"{es.AimsApplied - aims0} applied, {es.AttackStartsApplied} replayed");
                Check("which is us a moment ago (the runner's view of us)", es.LastAimLag > 0.02f && es.LastAimLag < 1.5f, $"{es.LastAimLag:F3} s behind");
            }
            yield return Barrier("aimed");
            if (s.IsHost)
            {
                yield return new WaitForSecondsRealtime(1f);
                if (e.isAttacking) e.CancelAttack();
                yield return new WaitForSecondsRealtime(0.5f);
            }

            // 2. an attack interrupted on the runner (what a stagger, stun or hit-freeze does)
            yield return Barrier("cancel-ready");
            int cancels0 = es.AttackCancelsApplied;
            yield return Barrier("cancel-go");
            if (s.IsHost)
            {
                int sent0 = es.AttackCancelsSent;
                e.PerformAttack(attack, other.Puppet);
                e.CancelAttack();
                Check("the runner tells the others its attack was interrupted", es.AttackCancelsSent == sent0 + 1, $"{es.AttackCancelsSent - sent0} sent");
            }
            else
            {
                yield return WaitFor(() => es.AttackCancelsApplied > cancels0, 5f);
                Check("the puppet dropped its attack too", es.AttackCancelsApplied > cancels0 && !e.isAttacking && e.attackBuildVFX == null,
                    $"{es.AttackCancelsApplied - cancels0} applied, attacking {e.isAttacking}");
            }
            yield return Barrier("cancelled");

            // 3. the kill
            int shown = 0, muted = 0;
            System.Action<UIDamageEvent> count = ev =>
            {
                if (ev.DamageAmount <= 0f || !string.IsNullOrEmpty(ev.Message) || e == null) return;
                if (((Vector2)(ev.origin - e.CenterOfMass.position)).sqrMagnitude > 4f) return;
                if (EnemySync.MutingNumbers) muted++;
                else shown++;
            };
            if (!s.IsHost) Bus<UIDamageEvent>.Listeners += count;
            yield return Barrier("kill-ready");
            if (s.IsHost)
            {
                for (int i = 0; i < 5 && !e.IsDead; i++)
                {
                    CombatSystem.Resolve(Hit(me, e, 99999));   // (it may parry)
                    yield return new WaitForSecondsRealtime(0.2f);
                }
                Check("the host killed it", e.IsDead);
                Chat.Send("#gore=" + (e.DieState.IsGore ? 1 : 0));
                // the corpse slides (what the killing blow's knockback does)
                yield return new WaitForSecondsRealtime(0.3f);
                if (e.isActiveAndEnabled)
                {
                    var from = e.transform.position;
                    e.transform.position += Vector3.right;
                    yield return new WaitForSecondsRealtime(0.4f);   // (where it really is then: something may hold it)
                    var p = e.transform.position;
                    Plugin.Log.LogInfo($"[test] corpse moved {Vector2.Distance(from, p):F2}");
                    Chat.Send("#corpse=" + p.x.ToString(CultureInfo.InvariantCulture) + ":" + p.y.ToString(CultureInfo.InvariantCulture));
                }
                else Chat.Send("#corpse=none");
            }
            else
            {
                yield return WaitFor(() => e.IsDead && peerData.ContainsKey("gore") && peerData.ContainsKey("corpse"), 10f);
                yield return new WaitForSecondsRealtime(0.6f);
                Bus<UIDamageEvent>.Listeners -= count;
                Check("the enemy died here too", e.IsDead);
                Check("the kill showed one number (the killing blow, not the health left too)", shown == 1 && muted == 1, $"{shown} shown, {muted} muted");
                string gore = peerData.TryGetValue("gore", out var g) ? g : "?";
                Check("with the runner's gore roll", gore == (e.DieState.IsGore ? "1" : "0"), $"runner {gore}, here {e.DieState.IsGore}");
                string corpse = peerData.TryGetValue("corpse", out var c) ? c : "none";
                if (corpse != "none" && e.isActiveAndEnabled)
                {
                    var parts = corpse.Split(':');
                    var at = new Vector2(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture));
                    float d = Vector2.Distance(at, e.transform.position);
                    Check("the corpse slid where the runner's did", d < 0.3f, $"{d:F2} apart");
                }
                else Plugin.Log.LogInfo($"[test] no corpse to follow (runner {corpse}, here active {e.isActiveAndEnabled})");
            }
            Plugin.Log.LogInfo($"[enemyreplay] aims {es.AimsSent}/{es.AimsApplied} cancels {es.AttackCancelsSent}/{es.AttackCancelsApplied} starts {es.AttackStartsSent}/{es.AttackStartsApplied}");
            yield return Barrier("done");
            yield return Finish("replayed attacks aim and stop like the runner's; one number per kill");
        }
    }
}
