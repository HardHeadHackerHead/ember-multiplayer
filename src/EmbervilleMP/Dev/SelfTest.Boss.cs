using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using Unity.Behavior;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        private const string BossRoom = "SpiderMatriarch_Area";

        // Both in the Matriarch's arena; with wake, the host walks into her intro first, the guest a little later
        // (each copy plays the intro and wakes its own boss, as in the boss scenario).
        private static IEnumerator ToBossArena(Session s, bool wake)
        {
            yield return TeleportTo(BossRoom, s.IsHost ? new Vector2(410f, 262f) : new Vector2(412f, 262f));
            yield return Barrier("arrived", 40f);
            if (wake) yield return WakeBoss(s);
        }

        private static IEnumerator WakeBoss(Session s)
        {
            var me = Game.LocalPlayer;
            var cine = Object.FindAnyObjectByType<MatriarchBossCinematic>();
            if (!Check("boss intro trigger found", cine != null)) yield break;
            if (!s.IsHost) yield return new WaitForSecondsRealtime(FastBossIntro.SecondPlayerDelay);
            var trigger = cine.GetComponent<Collider2D>();
            Vector2 from = me.rb.position, to = trigger != null ? (Vector2)trigger.bounds.center : (Vector2)cine.transform.position;
            yield return MoveTo(me, to, 1f);
            // Our own intro (the trigger switches itself off as it starts). A player already standing on the trigger when
            // it came on (the arrival point can lie on it) enters nothing: walk out and in again, then re-arm it.
            for (int attempt = 1; attempt <= 2 && cine.gameObject.activeSelf; attempt++)
            {
                yield return WaitFor(() => !cine.gameObject.activeSelf, 2f);
                if (!cine.gameObject.activeSelf) break;
                Plugin.Log.LogInfo($"[test] Her intro didn't start here (attempt {attempt}): walking out and in again");
                yield return MoveTo(me, from + (from - to).normalized * 2f, 0.5f);
                yield return new WaitForFixedUpdate();
                if (attempt == 2) { cine.gameObject.SetActive(false); yield return new WaitForFixedUpdate(); cine.gameObject.SetActive(true); }
                yield return MoveTo(me, to, 1f);
            }
            // A trigger that never fires here (seen on a host's first scene) still gets us in: the other player's
            // intro starts the fight, and we join it (BossArena.Join).
            int joins = BossArena.Joins;
            yield return WaitFor(() => !cine.gameObject.activeSelf, 20f);
            Check("our intro started, or we joined the fight", !cine.gameObject.activeSelf, $"{BossArena.Joins - joins} joined, {BossArena.Describe(cine)}");
            yield return WaitFor(() => Boss() != null, 25f);
            Check("boss is here", Boss() != null);
            yield return WaitFor(() => me.currentState != me.stateStatic, 10f);   // the intro holds us still
        }

        // These scenarios make the players invincible: the next scenario in a chain gets them as they were.
        private static IEnumerator FinishMortal(string summary)
        {
            if (Game.LocalPlayer != null) Game.LocalPlayer.Invincible = false;
            yield return Finish(summary);
        }

        private static Enemy Boss() => EnemiesIn(BossRoom).FirstOrDefault(e => e.name.Contains("Boss") && !e.IsDead);

        private static IEnumerable<SpiderEgg> LiveEggs() =>
            EggSync.Instance.Placed.Select(p => p.egg).Where(e => e != null && e.isActiveAndEnabled && e.state == SpiderEgg.EggState.Idle);

        // The eggs placed in her arena: a guest's hit breaks one on both, a hatch on the runner shows on the guest, and
        // an egg the guest got wrong is put right by the runner's egg states.
        [Scenario("bosseggs", Timeout = 220)]
        private static IEnumerator BossEggsTest()
        {
            FastBossIntro.On();   // (her intro is woken through, not watched)
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) { yield return FinishMortal(""); yield break; }
            var es = EnemySync.Instance;
            var eggs = EggSync.Instance;
            var me = Game.LocalPlayer;
            me.Invincible = true;
            yield return ToBossArena(s, wake: false);
            Check("the arena's eggs have network ids", eggs.PlacedCount > 0, $"{eggs.PlacedCount} placed");
            Plugin.Log.LogInfo($"[egg] placed: {string.Join(", ", eggs.Placed.Select(p => $"{p.egg.name} {p.egg.state} active={p.egg.gameObject.activeInHierarchy}"))}");
            // The boss's graph only finds eggs that are out (active): wake her if none is yet.
            // (control lines only reach the other copies: the host keeps its own answer)
            string outFlag = null;
            if (s.IsHost) Chat.Send("#eggsout=" + (outFlag = LiveEggs().Any() ? "1" : "0"));
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("eggsout"), 15f);
                peerData.TryGetValue("eggsout", out outFlag);
            }
            if (outFlag == "0")
            {
                Plugin.Log.LogInfo("[egg] no egg out before the fight: waking the boss");
                yield return WakeBoss(s);
            }
            yield return Barrier("eggs-out", 60f);
            if (!s.IsHost)
            {
                yield return WaitFor(() => EnemySync.GuestPuppets && eggs.StatesApplied > 0 && LiveEggs().Any(), 15f);
                Check("guest follows the host's eggs", EnemySync.GuestPuppets && eggs.StatesApplied > 0, $"{eggs.StatesApplied} egg states");
            }
            Check("an egg is out", LiveEggs().Any(), string.Join(", ", eggs.Placed.Select(p => $"{p.egg.state}/{p.egg.gameObject.activeInHierarchy}")));
            yield return Barrier("eggs-ready");

            // 1. The guest breaks the egg nearest to it: its hit goes to the host, the break comes back.
            SpiderEgg egg1 = null;
            if (!s.IsHost)
            {
                egg1 = LiveEggs().OrderBy(e => (e.transform.position - me.transform.position).sqrMagnitude).FirstOrDefault();
                if (Check("guest picked an egg", egg1 != null))
                {
                    Chat.Send("#egg1=" + eggs.IdOf(egg1));
                    yield return new WaitForSecondsRealtime(0.3f);
                    CombatSystem.Resolve(Hit(me, egg1, 999));
                    Check("guest's hit went to the host", eggs.Hits > 0, $"{eggs.Hits} hits sent");
                    yield return WaitFor(() => egg1.state != SpiderEgg.EggState.Idle, 8f);
                    Check("the egg broke on the guest (the host's word)", egg1.state == SpiderEgg.EggState.Destroyed && eggs.BrokenApplied > 0, $"{egg1.state}, {eggs.BrokenApplied} applied");
                }
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("egg1"), 15f);
                egg1 = peerData.TryGetValue("egg1", out var e1) ? eggs.ById(int.Parse(e1)) : null;
                yield return WaitFor(() => egg1 != null && egg1.state != SpiderEgg.EggState.Idle, 8f);
                Check("the guest's hit broke the egg on the host", egg1 != null && egg1.state == SpiderEgg.EggState.Destroyed && eggs.HitsApplied > 0,
                    $"{egg1?.state}, {eggs.HitsApplied} hits applied");
            }
            yield return Barrier("egg1");

            // 2. The host hatches another (what her graph does: Destroy(spiders per egg)); the guest sees it hatch and
            // gets the spiderlings.
            int spawnsBefore = s.IsHost ? es.SpawnsSent : es.SpawnsApplied;
            yield return Barrier("egg2-ready");
            if (s.IsHost)
            {
                var egg2 = LiveEggs().OrderByDescending(e => (e.transform.position - me.transform.position).sqrMagnitude).FirstOrDefault();
                if (Check("host picked an egg to hatch", egg2 != null))
                {
                    Chat.Send("#egg2=" + eggs.IdOf(egg2));
                    yield return new WaitForSecondsRealtime(0.3f);
                    egg2.Destroy(3);
                    yield return WaitFor(() => es.SpawnsSent >= spawnsBefore + 3, 8f);
                    Check("the egg hatched spiderlings on the host", es.SpawnsSent >= spawnsBefore + 3, $"{es.SpawnsSent - spawnsBefore} spawns, egg {egg2.state}");
                }
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("egg2"), 15f);
                var egg2 = peerData.TryGetValue("egg2", out var e2) ? eggs.ById(int.Parse(e2)) : null;
                yield return WaitFor(() => egg2 != null && egg2.state != SpiderEgg.EggState.Idle, 8f);
                Check("the host's hatch played on the guest's egg", egg2 != null && egg2.state != SpiderEgg.EggState.Idle, $"{egg2?.state}");
                yield return WaitFor(() => es.SpawnsApplied > spawnsBefore, 10f);
                Check("its spiderlings appeared on the guest", es.SpawnsApplied > spawnsBefore, $"{es.SpawnsApplied - spawnsBefore} spawns, {es.LocalSpawnsDropped} local dropped");
            }
            yield return Barrier("egg2");

            // 3. An egg the guest has wrong (a message it missed, a late arrival) is put right by the host's egg states.
            if (!s.IsHost)
            {
                var boss = Boss();
                var egg3 = LiveEggs().OrderByDescending(e => boss != null ? (e.transform.position - boss.transform.position).sqrMagnitude : 0f).FirstOrDefault();
                if (Check("guest picked an egg to get wrong", egg3 != null))
                {
                    int fixes = eggs.StateFixes;
                    egg3.state = SpiderEgg.EggState.Destroyed;
                    egg3.health = 0f;
                    yield return WaitFor(() => egg3.state == SpiderEgg.EggState.Idle, EggSync.FullInterval + 4f);
                    Check("the host's egg states put it right", egg3.state == SpiderEgg.EggState.Idle && eggs.StateFixes > fixes, $"{egg3.state}, {eggs.StateFixes - fixes} fixes");
                }
            }
            else Check("host sent its eggs' states", eggs.StatesSent > 0, $"{eggs.StatesSent}");
            Plugin.Log.LogInfo($"[egg] hits={eggs.Hits}/{eggs.HitsApplied} broken={eggs.Broken}/{eggs.BrokenApplied} states={eggs.StatesSent}/{eggs.StatesApplied} fixes={eggs.StateFixes}");
            yield return DevHarness.Screenshot(s.IsHost ? "bosseggs-host" : "bosseggs-guest");
            yield return Barrier("done", 40f);
            yield return FinishMortal("the arena's eggs are shared");
        }

        private static GameObject PlayerVar(Enemy e)
        {
            try
            {
                return e.behaviorGraphAgent != null && e.behaviorGraphAgent.GetVariable("Player", out BlackboardVariable<GameObject> v) ? v.Value : null;
            }
            catch (System.Exception) { return null; }
        }

        // The web: the boss's web hits the guest's puppet on the host and lands on the guest's own character; the host's
        // spiders then rush the webbed guest (her HandleEffectApplied, for a player that isn't the host's), and calm down
        // when the guest breaks free.
        [Scenario("bossweb", Timeout = 220)]
        private static IEnumerator BossWebTest()
        {
            FastBossIntro.On();   // (her intro is woken through, not watched)
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) { yield return FinishMortal(""); yield break; }
            var me = Game.LocalPlayer;
            me.Invincible = true;
            yield return ToBossArena(s, wake: true);
            var bs = BossSync.Instance;
            var m = bs.Matriarch;
            Check("the Matriarch is in the scene", m != null);
            yield return new WaitForSecondsRealtime(2f);
            yield return Barrier("awake", 40f);
            if (m == null) { yield return FinishMortal(""); yield break; }

            RemotePlayer guest = null;
            byte gid = 0;
            if (s.IsHost)
            {
                yield return WaitFor(() => (guest = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault(r => r.Shown && r.Target != null)) != null, 15f);
                Check("guest's puppet is here", guest != null);
                gid = guest != null ? guest.Info.Id : (byte)0;
            }

            // 1. Her web (the attack with the Trapped effect), from her, on the guest's puppet: a forwarded hit.
            var web = Resources.FindObjectsOfTypeAll<Attack>().FirstOrDefault(a => a != null && a.effects != null && a.effects.Any(x => x.Effect == EffectType.Trapped));
            if (!s.IsHost) me.Invincible = false;   // the web has to land
            yield return Barrier("web-ready");
            if (s.IsHost)
            {
                Check("her web attack found", web != null, web != null ? web.name : "no attack with Trapped");
                if (web != null && guest != null)
                    CombatSystem.Resolve(new DamageData
                    {
                        Aggressor = m.enemy, Target = guest.Target, Attack = web, Amount = 1, BaseEffectsChance = 100f,
                        Effects = new List<DamageEffect>(), Processors = new List<DamageDataProcessor>(),
                    });
                Chat.Send("#web=" + (web != null ? web.name : ""));
            }
            else
            {
                yield return WaitFor(() =>
                {
                    if (me.CurrentHealthPoints < me.MaxHealthPoints / 2) Heal(me);
                    return me.EffectsHandler.HasEffect(EffectType.Trapped) || (peerData.TryGetValue("web", out var w) && w == "");
                }, 8f);
                bool viaHit = me.EffectsHandler.HasEffect(EffectType.Trapped);
                if (peerData.TryGetValue("web", out var wn) && wn != "")
                    Check("her web on the guest's puppet webbed the guest (forwarded hit)", viaHit, $"state {me.currentState?.GetType().Name}");
                if (!viaHit)
                {
                    Plugin.Log.LogInfo("[boss] webbing the guest directly");
                    me.EffectsHandler.HandleEffect(new DamageEffect { Effect = EffectType.Trapped, HideUI = true });
                }
                me.Invincible = true;
                Heal(me);
                yield return null;
                Check("guest is webbed", me.EffectsHandler.HasEffect(EffectType.Trapped) && me.currentState == me.stateTrapped, $"state {me.currentState?.GetType().Name}");
            }
            yield return Barrier("webbed");

            // 2. The host's spiders rush the webbed guest.
            List<Enemy> Spiders() => EnemiesManager.AllEnemies.Where(e => e != null && e.isActiveAndEnabled && !e.IsDead && e.EnemyTag == m.tagToBuff &&
                e.behaviorGraphAgent != null && e.behaviorGraphAgent.enabled && e != m.enemy).ToList();
            if (s.IsHost && guest != null)
            {
                yield return WaitFor(() => bs.IsRemoteTrapped(gid), 6f);
                Check("host knows the guest is webbed", bs.IsRemoteTrapped(gid), $"{bs.RemoteTraps} webs seen");
                var spiders = Spiders().Where(e => Vector3.Distance(e.transform.position, guest.Puppet.transform.position) < BossSync.RushRange).ToList();
                Check("spiders around", spiders.Count > 0);
                yield return WaitFor(() => spiders.All(e => !e.isActiveAndEnabled || e.IsDead || PlayerVar(e) == guest.Puppet), HostWorld.RetargetInterval * 3);
                Check("the spiders chase the webbed guest", spiders.Count > 0 && spiders.All(e => !e.isActiveAndEnabled || e.IsDead || PlayerVar(e) == guest.Puppet),
                    string.Join(", ", spiders.Select(e => $"{e.name}->{(PlayerVar(e) != null ? PlayerVar(e).name : "none")}")));
                Check("they rush (speed buff)", spiders.Any(e => e.EffectsHandler.HasEffect(EffectType.MoveSpeedBuff)));
                Check("their group heads for the guest", spiders.Any(e => e.Group != null && e.Group.LastPlayerSeenPosition.HasValue &&
                    Vector3.Distance(e.Group.LastPlayerSeenPosition.Value, guest.Puppet.transform.position) < 3f));
                Check("she goes for the webbed guest too", PlayerVar(m.enemy) == guest.Puppet, PlayerVar(m.enemy)?.name ?? "none");
                Plugin.Log.LogInfo($"[boss] IsPlayerWebbed={ReadBool(m.enemy, "IsPlayerWebbed")}");
            }
            else if (!s.IsHost)
            {
                yield return new WaitForSecondsRealtime(1f);
                Check("guest's own spider puppets didn't rush on their own", !Spiders().Concat(EnemiesIn(BossRoom)).Any(e => e.EffectsHandler.HasEffect(EffectType.MoveSpeedBuff)));
            }
            yield return Barrier("rush-checked");

            // 3. Free again: the rush is over.
            if (!s.IsHost)
            {
                me.EffectsHandler.RemoveEffect(EffectType.Trapped);
                yield return WaitFor(() => !me.EffectsHandler.HasEffect(EffectType.Trapped), 3f);
                Check("guest broke free", !me.EffectsHandler.HasEffect(EffectType.Trapped));
            }
            yield return Barrier("freed");
            if (s.IsHost && guest != null)
            {
                yield return WaitFor(() => !bs.IsRemoteTrapped(gid), 6f);
                Check("host saw the guest break free", !bs.IsRemoteTrapped(gid), $"{bs.RemoteFrees} frees");
                yield return WaitFor(() => !Spiders().Any(e => e.EffectsHandler.HasEffect(EffectType.MoveSpeedBuff)), 3f);
                Check("the spiders' speed buff is gone", !Spiders().Any(e => e.EffectsHandler.HasEffect(EffectType.MoveSpeedBuff)));
                Check("nobody is forced on the guest any more", !Spiders().Any(e => HostWorld.ForcedTarget?.Invoke(e) != null));
            }
            me.ResetToFullHealth();
            yield return DevHarness.Screenshot(s.IsHost ? "bossweb-host" : "bossweb-guest");
            yield return Barrier("done", 40f);
            yield return FinishMortal("a webbed guest is rushed like a webbed host");
        }

        // Full health without ResetToFullHealth, which also puts the character back in its saved state (out of the web).
        private static void Heal(PlayerMaster me) => me.currentHealth = me.MaxHealthPoints;

        private static string ReadBool(Enemy e, string name)
        {
            try { return e.behaviorGraphAgent.GetVariable(name, out BlackboardVariable<bool> v) ? v.Value.ToString() : "-"; }
            catch (System.Exception) { return "?"; }
        }

        // Her stage on a change of runner: the host puts her in a later stage, leaves the scene mid-fight, and the guest,
        // now running the fight, carries on from that stage (not from the Stage1 her OnEnable set on its copy).
        [Scenario("bossstage", Timeout = 260)]
        private static IEnumerator BossStageTest()
        {
            FastBossIntro.On();   // (her intro is woken through, not watched)
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) { yield return FinishMortal(""); yield break; }
            var es = EnemySync.Instance;
            var bs = BossSync.Instance;
            var me = Game.LocalPlayer;
            me.Invincible = true;
            yield return ToBossArena(s, wake: true);
            var m = bs.Matriarch;
            Check("the Matriarch is in the scene", m != null);
            yield return new WaitForSecondsRealtime(2f);
            yield return Barrier("awake", 40f);
            if (m == null) { yield return FinishMortal(""); yield break; }
            int id = es.IdOf(m.enemy);
            Check("she has a network id", id != 0);

            if (s.IsHost)
            {
                // Under 40% health: her third stage.
                m.enemy.currentHealth = m.enemy.initHealth * 0.35f;
                BossSync.SetStage(m, BossStage.Stage3);
                yield return new WaitForSecondsRealtime(1f);
                bool got = BossSync.TryGetStage(m, out var stage);
                Check("host's Matriarch is past stage 1", got && stage != BossStage.Stage1, stage.ToString());
                Chat.Send("#stage=" + (int)stage);
            }
            yield return Barrier("staged");
            BossStage hostStage = peerData.TryGetValue("stage", out var st) ? (BossStage)int.Parse(st) : BossStage.Stage3;
            if (!s.IsHost)
            {
                // (her graph may move on from the stage the host reported: whatever the host sends last is her stage)
                yield return WaitFor(() => bs.KnownStage(id, out var k) && k != BossStage.Stage1, BossSync.FullInterval + 3f);
                Check("guest knows her stage from the host (past stage 1)", bs.KnownStage(id, out var known) && known != BossStage.Stage1, $"{known} (host reported {hostStage})");
                BossSync.TryGetStage(m, out var mine);
                Plugin.Log.LogInfo($"[boss] guest's own copy (a puppet) is at {mine}");
            }
            yield return Barrier("known");

            // The host leaves the scene mid-fight.
            const string hub = "VitromotusCrystal";
            GameData.gameBools[BoolID.CutsceneCrystal] = true;
            if (s.IsHost)
            {
                GuestWorld.Travel(hub, "", Vector3.zero, newWorld: false);
                yield return WaitFor(() => Game.InGame && Game.SceneName == hub && Game.Manager.gameState == GameState.Playing, 90f);
                Check("host left for the hub", Game.SceneName == hub);
            }
            else
            {
                yield return WaitFor(() => EnemySync.IsAuthority, 60f);
                Check("guest runs the fight now", EnemySync.IsAuthority, $"runner #{es.Authority}");
                BossStage now = BossStage.Stage1;
                bs.KnownStage(id, out var expect);
                yield return WaitFor(() => m.enemy.behaviorGraphAgent.enabled && BossSync.TryGetStage(m, out now) && now == expect, 10f);
                Check("her graph runs on the guest", m.enemy.isActiveAndEnabled && m.enemy.behaviorGraphAgent.enabled);
                Check("she carries on from the host's stage", expect != BossStage.Stage1 && now == expect && bs.StagesInherited > 0, $"{now} (host's last {expect}), {bs.StagesInherited} inherited");
                Plugin.Log.LogInfo($"[boss] her health on the guest: {m.enemy.HealthPercent:F2}");
            }
            yield return Barrier("done", 120f);
            yield return FinishMortal("her stage survives a change of runner");
        }

        // The Acolyte Tracker: its camouflage shows on the guest; its hook, fired at the guest's puppet on the host, pulls
        // the guest on its own copy; and a hook replayed on the guest pulls nobody by itself.
        [Scenario("tracker", Timeout = 200)]
        private static IEnumerator TrackerTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) { yield return FinishMortal(""); yield break; }
            var es = EnemySync.Instance;
            var ts = TrackerSync.Instance;
            var me = Game.LocalPlayer;
            me.Invincible = true;

            // The acolytes' room (the second one if the first has no tracker up).
            string room = "07_Acolytes";
            yield return TeleportTo(room, s.IsHost ? new Vector2(203.0f, 236.9f) : new Vector2(201.4f, 244.4f));
            yield return Barrier("room1", 40f);
            // (control lines only reach the other copies: the host keeps its own choice)
            string r2 = null;
            if (s.IsHost)
            {
                yield return new WaitForSecondsRealtime(1f);
                r2 = Trackers(room).Any() ? room : "07_Combat";
                Chat.Send("#room=" + r2);
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("room"), 15f);
                peerData.TryGetValue("room", out r2);
            }
            if (r2 != null && r2 != room)
            {
                room = r2;
                yield return TeleportTo(room, s.IsHost ? new Vector2(299.2f, 233.8f) : new Vector2(305.4f, 241.2f));
            }
            yield return Barrier("room", 40f);

            // The host holds every enemy of the room still (no other hook, nobody wanders off) and picks the tracker
            // nearest to the guest.
            var stopped = new List<Enemy>();
            Enemy t = null;
            if (s.IsHost)
            {
                foreach (var e in EnemiesIn(room).Where(e => !e.IsDead).ToList()) { e.StopBehavior(); stopped.Add(e); }
                RemotePlayer guest = null;
                yield return WaitFor(() => (guest = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault(r => r.Shown && r.Target != null)) != null, 15f);
                Check("guest's puppet is here", guest != null);
                if (guest != null)
                    t = Trackers(room).OrderBy(e => (e.transform.position - guest.Puppet.transform.position).sqrMagnitude).FirstOrDefault();
                if (Check("a tracker is here", t != null)) Chat.Send("#tracker=" + es.IdOf(t));
            }
            if (!s.IsHost)
            {
                yield return WaitFor(() => peerData.ContainsKey("tracker"), 15f);
                t = peerData.TryGetValue("tracker", out var tid) ? es.ById(int.Parse(tid)) : null;
                Check("guest has the tracker", t != null && t.isActiveAndEnabled);
                yield return new WaitForSecondsRealtime(1f);   // the host's copy has stopped; ours follows it
                // The room's trackers may have hooked us on the way in (before the host held them): that pull ends first.
                yield return WaitFor(() => me.currentState != me.stateStatic, 6f);
                Check("guest isn't held by an earlier hook", me.currentState != me.stateStatic, $"{ts.PullsTaken} pulls so far, {ts.PullsReleased} released");
                if (t != null)
                {
                    // 6 units from it, on our side. (The game's pull lasts (distance - 0.6) / (2 x speed) - 0.1 s: from 4
                    // units it moved us ~0.25, and a puppet a little off made that less.)
                    Vector3 dir = (me.transform.position - t.transform.position);
                    dir = dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.right;
                    yield return MoveTo(me, t.transform.position + dir * 6f, 0.5f);
                }
            }
            yield return Barrier("placed");
            if (t == null) { yield return FinishMortal(""); yield break; }
            var tracker = t.GetComponent<AcolyteTracker>();

            // 1. Camouflage.
            if (s.IsHost) { tracker.SetCamouflage(true); Check("host's tracker camouflaged", tracker.isCamouflage); }
            yield return Barrier("camo-on");
            if (!s.IsHost)
            {
                yield return WaitFor(() => tracker.isCamouflage, TrackerSync.FullInterval + 2f);
                Check("the tracker is camouflaged on the guest too", tracker.isCamouflage && FadeIs(t, 0.5f), Alphas(t));
            }
            yield return Barrier("camo-seen");
            if (s.IsHost) tracker.SetCamouflage(false);
            yield return Barrier("camo-off");
            if (!s.IsHost)
            {
                yield return WaitFor(() => !tracker.isCamouflage, TrackerSync.FullInterval + 2f);
                Check("and visible again with the host's", !tracker.isCamouflage && FadeIs(t, 1f), Alphas(t));
            }
            yield return Barrier("camo-done");

            // 2. The host's tracker hooks the guest's puppet: the guest is pulled on its own copy.
            Vector3 before = me.transform.position;
            int hooks0 = ts.PuppetHooks, pulls0 = ts.PullsTaken;
            yield return Barrier("hook-ready");
            if (s.IsHost)
            {
                var guest = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault(r => r.Shown && r.Target != null);
                var hook = guest != null ? FireHook(t, guest.Target.CenterOfMass.position) : null;
                Check("host fired the tracker's hook at the guest's puppet", hook != null);
                yield return WaitFor(() => ts.PuppetHooks > hooks0, 4f);
                Check("the hook caught the guest's puppet", ts.PuppetHooks > hooks0 && tracker.HasHit, $"{ts.PuppetHooks - hooks0} hooks, HasHit {tracker.HasHit}");
                yield return new WaitForSecondsRealtime(2f);
                Check("the host's own character wasn't pulled", (me.transform.position - before).magnitude < 0.1f);
            }
            else
            {
                bool sawStatic = false;
                yield return WaitFor(() => ts.PullsTaken > pulls0, 6f);
                Check("the host's hook pulls the guest", ts.PullsTaken > pulls0);
                yield return WaitFor(() => { sawStatic |= me.currentState == me.stateStatic; return !tracker.isPulling && me.currentState != me.stateStatic; }, 5f);
                yield return new WaitForSecondsRealtime(0.3f);
                Vector3 moved = me.transform.position - before;
                Check("on its own copy, toward the tracker", ts.PullsTaken > pulls0 && moved.magnitude > 0.3f && Vector3.Dot(moved, t.transform.position - before) > 0f,
                    $"moved {moved.magnitude:F2}, held still during the pull: {sawStatic}");
                Check("and free again after", me.currentState != me.stateStatic, me.currentState?.GetType().Name);
            }
            yield return Barrier("hooked");

            // 3. A hook replayed on the guest (as from EnemyAttackStart) reaches it: nothing pulls without the host's word.
            if (!s.IsHost)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                before = me.transform.position;
                int pulls = ts.PullsTaken, skipped = ts.ReplayHooksSkipped;
                var hook = FireHook(t, me.CenterOfMass.position);
                Check("guest fired a replayed hook at itself", hook != null);
                yield return WaitFor(() => ts.ReplayHooksSkipped > skipped, 3f);
                yield return new WaitForSecondsRealtime(1f);
                Check("the replayed hook reached the guest", ts.ReplayHooksSkipped > skipped);
                Check("and didn't pull it", ts.PullsTaken == pulls && (me.transform.position - before).magnitude < 0.2f && me.currentState != me.stateStatic,
                    $"moved {(me.transform.position - before).magnitude:F2}, {me.currentState?.GetType().Name}");
                if (hook != null && hook.gameObject.activeSelf) hook.gameObject.SetActive(false);
            }
            yield return Barrier("replayed");

            foreach (var e in stopped)
                if (e != null && e.isActiveAndEnabled && !e.IsDead) { e.RestartGraph(); e.RestartBehavior(); }
            Plugin.Log.LogInfo($"[tracker] hooks={ts.PuppetHooks} local={ts.LocalHooksSent} pulls={ts.PullsTaken} shown={ts.PullsShown} skipped={ts.ReplayHooksSkipped} camo={ts.CamoSent}/{ts.CamoApplied}");
            yield return DevHarness.Screenshot(s.IsHost ? "tracker-host" : "tracker-guest");
            yield return Barrier("done", 40f);
            yield return FinishMortal("the trackers' hooks and camouflage are shared");
        }

        private static IEnumerable<Enemy> Trackers(string room) =>
            EnemiesIn(room).Where(e => !e.IsDead && e.GetComponent<AcolyteTracker>() != null);

        private static bool FadeIs(Enemy e, float fade)
        {
            var r = e.fadableRenderers;
            if (r == null || e.renderersAlphas == null || e.renderersAlphas.Length < r.Length) return true;
            for (int i = 0; i < r.Length; i++)
                if (r[i] != null && Mathf.Abs(r[i].color.a - fade * e.renderersAlphas[i]) > 0.02f) return false;
            return true;
        }

        private static string Alphas(Enemy e) =>
            e.fadableRenderers == null ? "no renderers" : string.Join(" ", e.fadableRenderers.Select(r => r != null ? r.color.a.ToString("F2", CultureInfo.InvariantCulture) : "-"));

        // The tracker's hook, as its attack spawns it (Enemy.PerformAttackHit), flying at a point.
        private static Projectile FireHook(Enemy t, Vector3 at)
        {
            var attack = Resources.FindObjectsOfTypeAll<Attack>().FirstOrDefault(a => a != null && a.projectile != null &&
                a.projectile.GetComponentInChildren<AcolyteTrackerProjectile>(true) != null);
            if (attack == null) { Plugin.Log.LogInfo("[tracker] no hook attack found"); return null; }
            var go = PoolManager.Get(attack.projectile);
            if (go == null)
            {
                var holder = new GameObject("MP_TestHolder");
                holder.SetActive(false);
                go = Object.Instantiate(attack.projectile, holder.transform);
                go.SetActive(false);
                go.transform.SetParent(null, true);
                Object.Destroy(holder);
            }
            var p = go.GetComponent<Projectile>();
            if (p == null) return null;
            Vector3 from = t.CenterOfMass.position;
            Vector3 dir = at - from;
            dir.z = 0f;
            p.transform.position = from + dir.normalized * 0.8f;
            p.Direction = dir.normalized;
            p.Owner = t;
            p.Range = Mathf.Max(p.Range, dir.magnitude + 2f);   // (the pooled hook keeps it: reach the target from where it stands)
            go.SetActive(true);
            p.damageData = new DamageData
            {
                Aggressor = t, Attack = attack, Amount = 1,
                Effects = new List<DamageEffect>(), Processors = new List<DamageDataProcessor>(),
            };
            Plugin.Log.LogInfo($"[tracker] fired {attack.name} from {t.name} at {at}");
            return p;
        }
    }
}
