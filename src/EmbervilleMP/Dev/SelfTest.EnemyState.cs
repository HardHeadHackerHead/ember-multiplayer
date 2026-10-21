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
        private const string CrystalHub = "VitromotusCrystal";

        private static IEnumerator TravelAndWait(string scene, string area, Vector3 pos)
        {
            GameData.gameBools[BoolID.CutsceneCrystal] = true;   // the hub's first-visit cutscene would move us on
            GuestWorld.Travel(scene, area, pos, newWorld: false);
            yield return WaitFor(() => Game.InGame && Game.SceneName == scene && (area.Length == 0 || Game.AreaName == area)
                && Game.Manager.gameState == GameState.Playing && !GuestWorld.Switching, 90f);
            Check($"arrived in {scene}{(area.Length > 0 ? "/" + area : "")}", Game.SceneName == scene, $"{Game.SceneName}/{Game.AreaName}");
        }

        private static bool Gone(Enemy e) => e != null && e.IsDead && !e.gameObject.activeInHierarchy;

        // Deaths outlive the players who saw them, while someone stays in the scene:
        //   1. The guest leaves for the hub; the host kills two enemies.
        //   2. The guest comes back (its scene loads with every enemy alive): the host's dead list puts them down, with
        //      no second death (no XP), and the living one is the host's puppet.
        //   3. The host leaves: the guest runs the scene, the two stay dead.
        //   4. The host comes back (a fresh copy of the scene): the guest's handoff says they're dead.
        [Scenario("deadstay", Timeout = 420)]
        private static IEnumerator DeadStayTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) { yield return FinishMortal(""); yield break; }
            var es = EnemySync.Instance;
            var me = Game.LocalPlayer;
            me.Invincible = true;
            yield return TeleportTo(CombatArea, s.IsHost ? new Vector2(51.8f, 76.3f) : new Vector2(53f, 77f));
            yield return Barrier("placed", 40f);
            // (control lines only reach the other copies: the host keeps its own list)
            string idList = "";
            if (s.IsHost)
            {
                yield return new WaitForSecondsRealtime(1f);
                var alive = EnemiesIn(CombatArea).Where(e => !e.IsDead && es.IdOf(e) > 0)
                    .OrderBy(e => (e.transform.position - me.transform.position).sqrMagnitude).Take(3).ToList();
                idList = string.Join("|", alive.Select(e => es.IdOf(e)));
                Chat.Send("#ids=" + idList);
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("ids"), 20f);
                idList = peerData.TryGetValue("ids", out var t) ? t : "";
            }
            var ids = idList.Split('|').Where(x => x.Length > 0).Select(int.Parse).ToArray();
            if (!Check("three enemies picked", ids.Length == 3, string.Join("|", ids))) { yield return Barrier("done", 60f); yield return FinishMortal(""); yield break; }
            yield return Barrier("ids");

            // 1. away, and the host kills two
            if (!s.IsHost) yield return TravelAndWait(CrystalHub, "", Vector3.zero);
            yield return Barrier("away", 120f);
            if (s.IsHost)
            {
                foreach (int id in ids.Take(2))
                {
                    var v = es.ById(id);
                    if (v != null && !v.IsDead) CombatSystem.Resolve(Hit(me, v, 99999));
                    yield return WaitFor(() => v == null || v.IsDead, 5f);
                }
                Check("host killed two", ids.Take(2).All(id => es.ById(id) != null && es.ById(id).IsDead));
                var other = es.ById(ids[2]);
                Check("the third is alive", other != null && !other.IsDead);
            }
            yield return Barrier("killed");

            // 2. back: they're dead here too, no death ran
            if (!s.IsHost)
            {
                int xp0 = TotalXp();
                int lists0 = es.DeadListsApplied;
                yield return TravelAndWait("TutorialArea", CombatArea, new Vector3(52.5f, 76.6f, 0f));
                yield return WaitFor(() => es.DeadListsApplied > lists0, 15f);
                Check("the host told us who's dead", es.DeadListsApplied > lists0, $"{es.DeadListsApplied - lists0} lists, {es.DeadMarked} marked");
                var v1 = es.ById(ids[0]);
                var v2 = es.ById(ids[1]);
                var other = es.ById(ids[2]);
                yield return WaitFor(() => Gone(v1) && Gone(v2), 5f);
                Check("the host's kills are dead here", Gone(v1) && Gone(v2), $"{v1?.IsDead}/{v1?.gameObject.activeInHierarchy}, {v2?.IsDead}/{v2?.gameObject.activeInHierarchy}");
                Check("out of the game's alive list", v1 != null && v2 != null && !EnemiesManager.AliveEnemies.Contains(v1) && !EnemiesManager.AliveEnemies.Contains(v2));
                yield return WaitFor(() => other != null && es.IsPuppet(other), 10f);
                Check("the living one is the host's puppet", other != null && es.IsPuppet(other) && !other.IsDead);
                Check("no second death (no XP)", TotalXp() == xp0, $"{xp0} -> {TotalXp()}");
                yield return new WaitForSecondsRealtime(EnemySync.DeadListRepeat + 1.5f);   // the repeat, and the sweep
                Check("still dead after the repeat", Gone(v1) && Gone(v2));
            }
            yield return Barrier("seen-dead", 60f);

            // 3. the host leaves: the guest runs the scene
            if (s.IsHost) yield return TravelAndWait(CrystalHub, "", Vector3.zero);
            else
            {
                yield return WaitFor(() => EnemySync.IsAuthority, 90f);
                Check("guest runs the scene now", EnemySync.IsAuthority, $"runner #{es.Authority}");
                yield return new WaitForSecondsRealtime(2f);
                var other = es.ById(ids[2]);
                Check("the host's kills stay dead on the new runner", Gone(es.ById(ids[0])) && Gone(es.ById(ids[1])));
                Check("the living one runs here now", other != null && !other.IsDead && other.isActiveAndEnabled && other.behaviorGraphAgent != null && other.behaviorGraphAgent.enabled);
            }
            yield return Barrier("guest-runs", 120f);

            // 4. the host comes back
            if (s.IsHost)
            {
                int handoffs0 = es.HandoffsApplied;
                yield return TravelAndWait("TutorialArea", CombatArea, new Vector3(52.5f, 76.6f, 0f));
                yield return WaitFor(() => es.HandoffsApplied > handoffs0, 10f);
                Check("host got the fight handed over", es.HandoffsApplied > handoffs0);
                var v1 = es.ById(ids[0]);
                var v2 = es.ById(ids[1]);
                Check("its own kills are still dead in the fresh scene", (v1 == null || v1.IsDead || !v1.gameObject.activeInHierarchy) && (v2 == null || v2.IsDead || !v2.gameObject.activeInHierarchy),
                    $"{v1?.IsDead}/{v1?.gameObject.activeInHierarchy}, {v2?.IsDead}/{v2?.gameObject.activeInHierarchy}");
            }
            else
            {
                yield return WaitFor(() => es.Authority == Protocol.HostId && EnemySync.GuestPuppets, 120f);
                Check("guest follows the host again", es.Authority == Protocol.HostId && EnemySync.GuestPuppets, $"runner #{es.Authority}");
            }
            Plugin.Log.LogInfo($"[enemy] dead lists {es.DeadListsSent}/{es.DeadListsApplied}, marked {es.DeadMarked}, re-killed {es.DeadRekilled}, handoffs {es.HandoffsSent}/{es.HandoffsApplied}");
            yield return Barrier("done", 120f);
            yield return FinishMortal("deaths last while someone is in the scene");
        }

        // The runner leaves mid-fight; the guest taking over carries the fight on. The host waits in a house (no enemies)
        // while the guest is with the acolytes (the host keeps their room running), so the two notions of "in combat"
        // differ:
        //   1. The host's protector starts its aura (the guest's puppet shows its look), the host's enemy burns and has a
        //      shield, its group is engaged; a hit on a protected enemy shows the absorb on the guest.
        //   2. In combat: yes for the guest next to the fight, no for the host in its house (the game would say yes).
        //   3. The host leaves: on the guest, the burn (with its time left), the shield and the aura carry on, the group
        //      stays engaged; the aura it carried on ends by itself.
        [Scenario("fxtakeover", Timeout = 300)]
        private static IEnumerator FxTakeoverTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) { yield return FinishMortal(""); yield break; }
            var es = EnemySync.Instance;
            var fx = EnemyFx.Instance;
            var me = Game.LocalPlayer;
            me.Invincible = true;
            const string house = "02_House";
            // The room: both copies pick it from the scene (the same on both): the first of the acolyte rooms with a
            // placed protector that isn't dormant; the guest stands next to that protector.
            var placed = Object.FindObjectsByType<AcolyteProtector>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => x.enemy != null && es.IdOf(x.enemy) > 0 && x.enemy.gameObject.activeSelf)
                .OrderBy(x => NetIds.ScenePath(x.enemy.transform)).ToList();
            Plugin.Log.LogInfo("[test] protectors: " + string.Join(", ", placed.Select(x => $"{x.enemy.name} in {es.AreaOf(x.enemy)} at {(Vector2)x.enemy.transform.position}")));
            var first = placed.Where(x => es.AreaOf(x.enemy).StartsWith("07_")).Concat(placed).FirstOrDefault();
            string room = first != null ? es.AreaOf(first.enemy) : "07_Acolytes";
            Vector2 near = first != null ? (Vector2)first.enemy.transform.position + Vector2.left * 1.5f : new Vector2(201.4f, 244.4f);
            Check("a placed protector in the scene", first != null);
            if (s.IsHost)
            {
                var spot = SpawnPointIn(house);
                if (Check($"found a spot in {house}", spot != null)) yield return TeleportTo(house, spot.Value);
            }
            else yield return TeleportTo(room, near);
            yield return Barrier("placed", 40f);
            System.Func<string, IEnumerable<Enemy>> protectorsIn = a => EnemiesIn(a).Where(e => !e.IsDead && EnemyFx.ProtectorOf(e) != null);
            yield return Barrier("room", 40f);

            Enemy e = null, member = null;
            AcolyteProtector p = null;
            if (s.IsHost)
            {
                yield return WaitFor(() => protectorsIn(room).Any(), 10f);
                yield return new WaitForSecondsRealtime(1.5f);   // its enemies set up (OnEnable waits a few frames)
                foreach (var x in EnemiesIn(room).Where(x => !x.IsDead).ToList()) x.StopBehavior();   // nobody moves, nobody hooks
                e = protectorsIn(room).OrderBy(x => NetIds.ScenePath(x.transform)).FirstOrDefault();
                Check("a protector is here", e != null, room);
                if (e == null)
                {
                    var guest = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault(r => r.Puppet != null);
                    e = EnemiesIn(room).Where(x => !x.IsDead && es.IdOf(x) != 0).OrderBy(x => guest != null ? (x.transform.position - guest.Puppet.transform.position).sqrMagnitude : 0f).FirstOrDefault();
                }
                p = EnemyFx.ProtectorOf(e);
                if (e != null)
                {
                    e.initHealth = 5000f;
                    e.currentHealth = 5000f;
                }
                Chat.Send("#fxtarget=" + (e != null ? es.IdOf(e) : 0));
            }
            else yield return WaitFor(() => peerData.ContainsKey("fxtarget"), 20f);
            int id = s.IsHost ? es.IdOf(e) : peerData.TryGetValue("fxtarget", out var ft) ? int.Parse(ft) : 0;
            if (!s.IsHost)
            {
                yield return WaitFor(() => (e = es.ById(id)) != null && es.IsPuppet(e), 10f);
                Check("guest's copy is a puppet of the host's", e != null && es.IsPuppet(e), $"#{id}");
                p = EnemyFx.ProtectorOf(e);
                // An earlier hook (before the host held its trackers) lets go first; then next to the enemy.
                yield return WaitFor(() => me.currentState != me.stateStatic, 6f);
                if (e != null) yield return MoveTo(me, (Vector2)e.transform.position + Vector2.left * 1.5f, 0.5f);
            }
            if (e == null) { yield return Barrier("done", 60f); yield return FinishMortal(""); yield break; }
            yield return Barrier("picked");

            // 1. on the runner: aura, burn, shield, engaged
            int auraShown0 = fx.AuraShown, absorbs0 = fx.AbsorbsShown;
            if (s.IsHost)
            {
                if (p != null && !EnemyFx.AuraOn(p))
                {
                    e.Group?.skillCooldown.Remove(p.groupSkill);   // (its graph may have used it on the way)
                    bool started = false;
                    try { started = p.StartAura(); } catch (System.Exception ex) { Plugin.Log.LogWarning("[test] StartAura: " + ex.Message); }
                    Check("host's protector starts its aura", started && EnemyFx.AuraOn(p));
                }
                Check("burn on the host's enemy", e.EffectsHandler.HandleEffect(new DamageEffect { Effect = EffectType.Burn, DamagePerSec = 1, Duration = 40f, HideUI = true }));
                Check("shield on the host's enemy", e.EffectsHandler.HandleEffect(new DamageEffect { Effect = EffectType.Shield, DamagePerSec = 3, HideUI = true }));
                var g = e.Group;
                if (Check("it has a group", g != null))
                {
                    // What a player's hit does to an unengaged group (Enemy.ApplyDamage).
                    g.LastPlayerSeenPosition = e.transform.position;
                    g.TimeToExpirePlayerLastSeen = 15f;
                    g.IsForcedToPosition = true;
                    g.ForceEngage = true;
                    Check("its group is engaged", g.IsEngagedWithPlayer);
                }
            }
            yield return Barrier("fx-on");
            System.Func<EffectType, bool> mirrored = x => fx.MirroredOf(id).Any(m => m.Type == x);
            if (!s.IsHost)
            {
                yield return WaitFor(() => mirrored(EffectType.Burn) && mirrored(EffectType.Shield) && (p == null || fx.AuraShown > auraShown0), 6f);
                Check("the puppet shows the runner's burn and shield", mirrored(EffectType.Burn) && mirrored(EffectType.Shield), string.Join(", ", fx.MirroredOf(id).Select(m => m.Type)));
                if (p != null) Check("the puppet shows the protector's aura (its own start event)", fx.AuraShown > auraShown0);
                yield return WaitFor(() => EnemyCombat.Instance.RunnerSaysFighting(id), 5f);
                Check("the runner says it's fighting", EnemyCombat.Instance.RunnerSaysFighting(id));
            }
            yield return Barrier("fx-seen");

            // the absorb: the host hits an enemy its aura protects
            if (s.IsHost && p != null)
            {
                member = p.enemiesProtected.FirstOrDefault(x => x != null && x != e && x.isActiveAndEnabled && !x.IsDead);
                if (member != null) CombatSystem.Resolve(Hit(me, member, 10));
                else Plugin.Log.LogInfo("[test] the protector protects nobody here: absorb not checked");
                Chat.Send("#absorbed=" + (member != null ? 1 : 0));
            }
            if (!s.IsHost) yield return WaitFor(() => peerData.ContainsKey("absorbed"), 20f);
            if (!s.IsHost && peerData.TryGetValue("absorbed", out var ab) && ab == "1")
            {
                yield return WaitFor(() => fx.AbsorbsShown > absorbs0, 5f);
                Check("the absorb's VFX show on the guest", fx.AbsorbsShown > absorbs0, $"{fx.AbsorbsShown - absorbs0}");
            }
            yield return Barrier("absorb");

            // 2. who's in combat
            bool game = EnemiesManager.AllGroups.Any(g => g.IsEngagedWithPlayer);
            bool ours = EnemyCombat.AnyEngaged(EnemiesManager.AllGroups, g => g.IsEngagedWithPlayer);
            if (s.IsHost)
            {
                Check("the game's check would block the host's quick portal", game);
                Check("the host in its house isn't in combat", !ours && !TeamTeleport.Instance.InCombat(me));
            }
            else Check("the guest next to the fight is in combat (no quick portal)", ours, $"the game's check: {game}");
            yield return Barrier("combat");

            // 3. the host leaves mid-fight
            if (s.IsHost) yield return TravelAndWait(CrystalHub, "", Vector3.zero);
            else
            {
                yield return WaitFor(() => EnemySync.IsAuthority, 90f);
                Check("guest runs the fight now", EnemySync.IsAuthority, $"runner #{es.Authority}");
                yield return new WaitForSecondsRealtime(0.5f);
                var burn = EnemyFx.Effects(e).Where(x => x.Type == EffectType.Burn).ToList();
                Check("the burn carries on, with its time left", burn.Count > 0 && burn[0].Remaining > 3f && burn[0].Remaining < 40f,
                    burn.Count > 0 ? $"{burn[0].Remaining:F1} s left" : "none");
                Check("the shield carries on", e.EffectsHandler.HasEffect(EffectType.Shield));
                Check("the effects were taken over", fx.TakeoverEffects > 0, $"{fx.TakeoverEffects} on {fx.TakeoverEnemies}");
                Check("its group stays engaged", e.Group != null && e.Group.IsEngagedWithPlayer && EnemyCombat.Instance.GroupsEngaged > 0, $"{EnemyCombat.Instance.GroupsEngaged} groups engaged on takeover; group {(e.Group != null ? $"in sight {e.Group.IsInSightOfPlayer.Count}, forced {e.Group.ForceEngage}" : "none")}");
                if (p != null)
                {
                    // (carried on by us; or, after a drop, started again by its graph while the runner was only quiet)
                    Check("the aura carries on (no hit-freeze)", EnemyFx.AuraOn(p) && !e.CanHitFreeze, $"{fx.ProtectorsAdopted} carried on");
                    if (fx.ProtectorsAdopted > 0)
                    {
                        System.Func<bool> ended = () => !EnemyFx.AuraAdopted(p) && (fx.AdoptedEnded > 0 || !EnemyFx.AuraOn(p));
                        yield return WaitFor(ended, EnemyFx.AdoptedSeconds + 1.5f);
                        Check("the aura we carried on ends by itself", ended());
                    }
                }
            }
            Plugin.Log.LogInfo($"[enemyfx] aura shown {fx.AuraShown}/{fx.AuraHidden}, absorbs {fx.AbsorbsSent}/{fx.AbsorbsShown}, taken over {fx.TakeoverEffects}, adopted {fx.ProtectorsAdopted} (ended {fx.AdoptedEnded}), combat lists {EnemyCombat.Instance.ListsSent}/{EnemyCombat.Instance.ListsReceived}");
            yield return Barrier("done", 120f);
            yield return FinishMortal("the fight carries on when its runner leaves");
        }
    }
}
