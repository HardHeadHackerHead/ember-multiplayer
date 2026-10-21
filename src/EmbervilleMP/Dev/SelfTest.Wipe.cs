using System.Collections;
using System.Linq;
using System.Reflection;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using EmbervilleMP.Sync.Objects;
using UnityEngine;
using UnityEngine.Events;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Our character dies now (a blow big enough for the gory death: the longest one, also with FastDeath).
        private static IEnumerator DieNow(string who)
        {
            var me = Game.LocalPlayer;
            me.Invincible = false;
            me.ApplyDamage(Hit(null, me, 99999));
            yield return WaitFor(() => me.IsDead, 5f);
            Check($"{who} died", me.IsDead);
        }

        // Back up after a death (respawn, fade, death screen), invincible again for the rest of the scenario.
        private static IEnumerator BackUp(string who)
        {
            var me = Game.LocalPlayer;
            yield return WaitFor(() => !me.IsDead && Game.InGame && Game.Manager.gameState == GameState.Playing, 40f);
            Check($"{who} is back up", !me.IsDead && Game.InGame);
            // (the respawn makes us invincible and takes that back 0.4 s after the game restarts: ours again after that)
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 1.5f) { me.Invincible = true; yield return null; }
            me = Game.LocalPlayer;
            me.Invincible = true;
        }

        private static bool RemoteDown(Session s) =>
            MPRunner.Instance.Presence.Remotes.Values.Any(r => r.Info.Id != s.LocalId && r.Latest != null && r.Latest.Dead);

        // A room event and enemy waves with deaths (built as in roomevents: the demo's scenes have none). The host runs
        // them. 1: the host dies while the guest is up: nothing fails (the host's copy runs on); then the guest dies
        // too: both fail on both copies, and the party wipe rewinds the scene on both. 2: the guest dies first: nothing
        // fails; the host's death, with the guest still down, fails both at once.
        [Scenario("roomwipe", Timeout = 240)]
        private static IEnumerator RoomWipeTest()
        {
            // (what a death fails and rewinds is checked, not its animation; a player stays down until its "up"
            // notice, a second after it's back in the game at the soonest, so the overlaps below still hold)
            DevHarness.FastDeath = true;
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) { yield return FinishMortal(""); yield break; }
            var me = Game.LocalPlayer;
            me.Invincible = true;
            const string room = "001_Barrack";
            var spot = new Vector2(-104f, 70f);
            yield return TeleportTo(room, spot + new Vector2(s.IsHost ? -1.5f : 1.5f, 0f));
            // (the teleport's fade back in may still be running: our first death would come in the middle of it)
            yield return WaitFor(() => Game.LocalPlayer != null && !Game.LocalPlayer.Static, 5f);
            yield return Barrier("placed", 60f);

            // An event whose one stage never ends by itself (breakables to destroy, none needed, none there): only a
            // death can fail it. It can be retried.
            var evGo = new GameObject("MP_WipeRoomEvent");
            evGo.SetActive(false);
            var stageGo = new GameObject("Stage1");
            stageGo.transform.SetParent(evGo.transform, false);
            stageGo.AddComponent<DestroyBreakablesStage>();
            var ev = evGo.AddComponent<DungeonRoomEvent>();
            ev.canRetry = true;
            int evFailed = 0;
            ev.onEventFailed.AddListener(() => evFailed++);
            evGo.SetActive(true);

            var src = Object.FindObjectsByType<Enemy>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(e => e.name.StartsWith("Mob_Glowbat") && !NetIds.IsRuntimeSpawned(e.transform)).OrderBy(e => NetIds.ScenePath(e.transform)).FirstOrDefault();
            if (!Check("found an enemy to copy", src != null)) { yield return FinishMortal(""); yield break; }
            var wvGo = new GameObject("MP_WipeWaves");
            wvGo.SetActive(false);
            wvGo.transform.position = spot;
            var wave = new GameObject("Wave1");
            wave.SetActive(false);   // (a template, as the game's are: its enemy never wakes up, nor joins the scene's list)
            wave.transform.SetParent(wvGo.transform, false);
            var mob = Object.Instantiate(src.gameObject, wave.transform);
            mob.name = "MP_WipeMob";
            mob.transform.position = spot + Vector2.up * 4f;
            var waves = wvGo.AddComponent<EnemyWaveHandler>();
            waves.timeBetweenWaves = 0.5f;
            waves.onWaveEventStart = new UnityEvent();
            waves.onWaveEventCompleted = new UnityEvent();
            waves.onWaveEventFailed = new UnityEvent();
            int wavesFailed = 0;
            waves.onWaveEventFailed.AddListener(() => wavesFailed++);
            wvGo.SetActive(true);
            typeof(ObjectSync).GetField("liveScene", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(ObjectSync.Instance, -1);
            yield return Barrier("built");

            System.Func<bool> running = () => ev.state == DungeonRoomEvent.EventState.inStage && waves.state != EnemyWaveHandler.waveState.ready && waves.state != EnemyWaveHandler.waveState.completed;
            for (int round = 1; round <= 2; round++)
            {
                if (s.IsHost)
                {
                    Check($"round {round}: the event is ready", ev.state == DungeonRoomEvent.EventState.ready && waves.state == EnemyWaveHandler.waveState.ready, $"{ev.state}, {waves.state}");
                    ev.StartEvent();
                    waves.StartWaveEvent();
                }
                yield return WaitFor(running, 10f);
                Check($"round {round}: the event and the waves run here", running(), $"{ev.state}, {waves.state}");
                yield return Barrier("running" + round, 30f);
                int wipes = s.IsHost ? PartyWipe.WipesDeclared : PartyWipe.WipesHeard, rewound = PartyWipe.ScenesRewound;
                bool hostFirst = round == 1;
                if (s.IsHost == hostFirst)
                {
                    // The first to fall: the others still fight, nothing fails.
                    int held = PartyWipe.HeldFails;
                    yield return DieNow(s.IsHost ? "host" : "guest");
                    yield return new WaitForSecondsRealtime(0.5f);   // (a wrong fail is at once on the runner, an RTT away here)
                    Check($"round {round}: our death alone fails nothing here", running() && evFailed == round - 1 && wavesFailed == round - 1, $"{ev.state}, {waves.state}, {evFailed} / {wavesFailed} failed");
                    if (s.IsHost) Check("the host's death hooks were held", PartyWipe.HeldFails >= held + 2, $"{PartyWipe.HeldFails - held} held");
                    Chat.Send("#down" + round + "=1");   // (peerData only keeps "#key=value" lines)
                }
                else
                {
                    // The last one up: until we fall, the event goes on; then it fails for everyone.
                    yield return WaitFor(() => peerData.ContainsKey("down" + round), 20f);
                    Check($"round {round}: the other player is down", RemoteDown(s));
                    Check($"round {round}: their death failed nothing here", running() && evFailed == round - 1 && wavesFailed == round - 1, $"{ev.state}, {waves.state}, {evFailed} / {wavesFailed} failed");
                    yield return DieNow(s.IsHost ? "host" : "guest");
                }
                yield return WaitFor(() => evFailed == round && wavesFailed == round, 8f);
                Check($"round {round}: with everyone down the event failed here, once", evFailed == round && ev.state == DungeonRoomEvent.EventState.ready, $"{evFailed} failed, {ev.state}");
                Check($"round {round}: and the waves", wavesFailed == round && waves.state == EnemyWaveHandler.waveState.ready, $"{wavesFailed} failed, {waves.state}");
                // (the fails come from our own death; the wipe is declared at the next 4 Hz check)
                yield return WaitFor(() => s.IsHost ? PartyWipe.WipesDeclared > wipes : PartyWipe.WipesHeard > wipes, 5f);
                if (s.IsHost) Check($"round {round}: the host called the party wipe", PartyWipe.WipesDeclared == wipes + 1, $"{PartyWipe.WipesDeclared - wipes}");
                else Check($"round {round}: the guest heard of it", PartyWipe.WipesHeard == wipes + 1, $"{PartyWipe.WipesHeard - wipes}");
                yield return BackUp(s.IsHost ? "host" : "guest");
                Check($"round {round}: the scene rewound here (as in single player)", PartyWipe.ScenesRewound > rewound, $"{PartyWipe.ScenesRewound - rewound}");
                yield return Barrier("up" + round, 60f);
            }
            Plugin.Log.LogInfo($"[wipe] held {PartyWipe.HeldFails}, room fails {PartyWipe.RoomFails}, wipes {PartyWipe.WipesDeclared}/{PartyWipe.WipesHeard}, rewinds {PartyWipe.Rewinds}/{PartyWipe.ScenesRewound}");
            yield return Barrier("done");
            Object.Destroy(evGo);
            Object.Destroy(wvGo);
            yield return FinishMortal("a death fails a room event only with everyone down");
        }

        // Her placed eggs, in id order: out (active) and whole (idle), as both copies must agree on after a rewind.
        private static string EggSignature() =>
            string.Concat(EggSync.Instance.Placed.OrderBy(p => p.id).Select(p =>
                p.egg == null ? "-" : (p.egg.gameObject.activeSelf ? "1" : "0") + (p.egg.state == SpiderEgg.EggState.Idle ? "i" : "x")));

        // The Matriarch with deaths. 1: the guest dies alone in her fight: it respawns outside the door its intro shut
        // and is put back into the arena; nothing rewinds. 2: both die (a party wipe): her fight rewinds on both copies
        // as in single player (intro ready again, door open, she's as the game saved her), the guest forgets her old
        // stage, and the eggs agree.
        [Scenario("bosswipe", Timeout = 280)]
        private static IEnumerator BossWipeTest()
        {
            FastBossIntro.On();   // (her intro is woken through, not watched)
            DevHarness.FastDeath = true;   // (as in roomwipe)
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) { yield return FinishMortal(""); yield break; }
            var me = Game.LocalPlayer;
            me.Invincible = true;
            yield return ToBossArena(s, wake: true);
            var cine = BossArena.Matriarch;
            var m = BossSync.Instance.Matriarch;
            if (!Check("the Matriarch and her intro are here", cine != null && m != null)) { yield return FinishMortal(""); yield break; }
            yield return WaitFor(() => BossArena.FightOn(cine), 10f);
            Check("her fight is on here", BossArena.FightOn(cine) && BossArena.LocalInArena(cine), BossArena.Describe(cine));
            int id = EnemySync.Instance.IdOf(m.enemy);
            int wipes = PartyWipe.WipesDeclared, heard = PartyWipe.WipesHeard;   // (chained runs: this scenario's)
            yield return Barrier("fight", 40f);

            // ---- 1. the guest falls alone ----
            if (s.IsHost)
            {
                // (her graph may only just have started: a stage written before it runs is lost; write it until it holds)
                BossStage now = BossStage.Stage1;
                float t0 = Time.realtimeSinceStartup;
                do
                {
                    m.enemy.currentHealth = m.enemy.initHealth * 0.6f;
                    BossSync.SetStage(m, BossStage.Stage2);
                    yield return new WaitForSecondsRealtime(0.3f);
                }
                while (!(m.enemy.behaviorGraphAgent.enabled && BossSync.TryGetStage(m, out now) && now != BossStage.Stage1) && Time.realtimeSinceStartup - t0 < 6f);
                Check("host's Matriarch is past stage 1", now != BossStage.Stage1, $"{now}, graph {m.enemy.behaviorGraphAgent.enabled}");
            }
            int rewound = PartyWipe.ScenesRewound, resets = BossArena.Resets;
            yield return Barrier("hurt");
            if (!s.IsHost)
            {
                int pulls = BossArena.PullIns;
                yield return DieNow("guest");
                yield return BackUp("guest");
                me = Game.LocalPlayer;
                float off = Vector2.Distance(me.rb.position, cine.toTeleportPlayer.position);
                Check("the guest was put back into her arena", BossArena.PullIns == pulls + 1 && off < 1.5f, $"{BossArena.PullIns - pulls} pulled in, {off:F1} from the arena's start");
                Check("her fight still goes on for the guest", BossArena.FightOn(cine) && BossArena.LocalInArena(cine));
            }
            yield return Barrier("guest-back", 60f);
            if (s.IsHost)
            {
                Check("her fight wasn't rewound", PartyWipe.ScenesRewound == rewound && BossArena.FightOn(cine), $"{PartyWipe.ScenesRewound - rewound} rewinds, health {m.enemy.HealthPercent:F2}, {BossArena.Describe(cine)}");
                Check("no party wipe for one death", PartyWipe.WipesDeclared == wipes, $"{PartyWipe.WipesDeclared - wipes}");
                // (her graph moves her stage on its own too: the guest must know the one she has now)
                BossSync.TryGetStage(m, out var hostStage);
                Chat.Send("#stage=" + (int)hostStage);
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("stage"), 20f);
                var hostStage = peerData.TryGetValue("stage", out var hs) ? (BossStage)int.Parse(hs) : BossStage.Stage2;
                yield return WaitFor(() => BossSync.Instance.KnownStage(id, out var k) && k == hostStage, BossSync.FullInterval + 3f);
                BossSync.Instance.KnownStage(id, out var k2);
                Check("the guest knows her stage (the host's)", k2 == hostStage, $"{k2} here, {hostStage} on the host");
                Plugin.Log.LogInfo($"[boss] her stage: {k2} here, {hostStage} on the host (set to Stage2 before the guest's death)");
            }
            yield return Barrier("part1");

            // ---- 2. a party wipe ----
            rewound = PartyWipe.ScenesRewound;
            if (s.IsHost)
            {
                yield return DieNow("host");
                Chat.Send("#hostdown=1");
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("hostdown"), 20f);
                yield return DieNow("guest");
            }
            yield return BackUp(s.IsHost ? "host" : "guest");
            Check("the scene rewound here", PartyWipe.ScenesRewound > rewound, $"{PartyWipe.ScenesRewound - rewound}");
            // (as the rewind left it: a player whose save point lies on her trigger walks into her intro again)
            Check("the rewind made her intro ready again and opened the door", BossArena.Resets > resets && BossArena.IntroReadyAfterReset,
                $"{BossArena.Resets - resets} resets, ready {BossArena.IntroReadyAfterReset}; now intro {cine.gameObject.activeSelf}, door {cine.door.activeSelf}");
            if (s.IsHost)
            {
                Check("the host called the party wipe", PartyWipe.WipesDeclared == wipes + 1, $"{PartyWipe.WipesDeclared - wipes}");
                // (as the rewind left her, back to the state the game saved for her when the room was entered: asleep, or
                // that health. Not as she is now: a player whose save point lies on her trigger replays her intro at once)
                Check("the rewind put her back as the game saved her", BossArena.BossSavedDead != null &&
                    (!BossArena.BossOutAfterReset || Mathf.Abs(BossArena.BossHealthAfterReset - BossArena.BossSavedHealth) < 1f),
                    $"out {BossArena.BossOutAfterReset}, health {BossArena.BossHealthAfterReset:F0} (saved {BossArena.BossSavedHealth:F0}, dead {BossArena.BossSavedDead}); now active {m.enemy.isActiveAndEnabled}, health {m.enemy.currentHealth:F0}");
                yield return new WaitForSecondsRealtime(EggSync.PollInterval * 4);
                Chat.Send("#eggs=" + EggSignature());
            }
            else
            {
                Check("the guest heard of the wipe", PartyWipe.WipesHeard > heard, $"{PartyWipe.WipesHeard - heard}");
                Check("the guest forgot her old stage", !BossSync.Instance.KnownStage(id, out var stale) || stale == BossStage.Stage1, stale.ToString());
                yield return WaitFor(() => peerData.ContainsKey("eggs"), 20f);
                string host = peerData.TryGetValue("eggs", out var e) ? e : "?";
                yield return WaitFor(() => EggSignature() == host, EggSync.FullInterval + 3f);
                Check("her eggs agree with the host's", EggSignature() == host, $"{EggSignature()} here, {host} on the host");
            }
            Plugin.Log.LogInfo($"[wipe] wipes {PartyWipe.WipesDeclared}/{PartyWipe.WipesHeard}, rewinds {PartyWipe.Rewinds}/{PartyWipe.ScenesRewound}, pull-ins {BossArena.PullIns}");
            yield return Barrier("done", 40f);
            yield return FinishMortal("her fight lets a fallen player back in, and a party wipe rewinds it");
        }

        // Her roar ("Spawn": everyone in the arena held still for a second) and screen shake come from her graph, on the
        // runner: the guest in the arena gets them too; a runner who isn't in the arena isn't held.
        [Scenario("bossroar", Timeout = 220)]
        private static IEnumerator BossRoarTest()
        {
            FastBossIntro.On();   // (her intro is woken through, not watched)
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) { yield return FinishMortal(""); yield break; }
            var me = Game.LocalPlayer;
            me.Invincible = true;
            yield return ToBossArena(s, wake: true);
            var cine = BossArena.Matriarch;
            if (!Check("her intro is here", cine != null)) { yield return FinishMortal(""); yield break; }
            yield return WaitFor(() => BossArena.LocalInArena(cine), 10f);
            Check("we're in her fight", BossArena.LocalInArena(cine));
            // (counts taken before each barrier: her event may arrive in the same frame as the barrier's signal)
            int holds = BossArena.Holds, shakes = BossArena.Shakes;
            yield return Barrier("fight", 40f);

            // ---- 1. her roar, both in the arena ----
            if (s.IsHost)
            {
                int sent = BossArena.EventsSent;
                cine.HandleEvent("Spawn");
                Check("the host is held by her roar (the game's own)", me.Static);
                Check("the host relayed it", BossArena.EventsSent == sent + 1, $"{BossArena.EventsSent - sent}");
            }
            else
            {
                yield return WaitFor(() => BossArena.Holds > holds, 8f);
                Check("the guest is held by her roar", BossArena.Holds == holds + 1 && me.Static, $"{BossArena.Holds - holds} holds, static {me.Static}");
                Check("and shaken", BossArena.Shakes > shakes, $"{BossArena.Shakes - shakes}");
                yield return WaitFor(() => !me.Static, 3f);
                Check("the guest is free again a moment later", !me.Static);
            }
            shakes = BossArena.Shakes;
            yield return Barrier("roar");

            // ---- 2. her screen shake ----
            if (s.IsHost) cine.HandleEvent("ScreenShake");
            else
            {
                yield return WaitFor(() => BossArena.Shakes > shakes, 8f);
                Check("her screen shake reached the guest", BossArena.Shakes == shakes + 1, $"{BossArena.Shakes - shakes}");
                Check("a shake holds nobody", !me.Static);
            }
            yield return Barrier("shake");

            // ---- 3. the runner in another room of the scene ----
            if (s.IsHost)
            {
                yield return TeleportTo(CombatArea, new Vector2(51.8f, 76.3f));
                me = Game.LocalPlayer;
                me.Invincible = true;
            }
            holds = BossArena.Holds;
            yield return Barrier("away", 40f);
            if (s.IsHost)
            {
                int paused = BossArena.PausedOnly;
                Check("the host runs the scene from another room", EnemySync.IsAuthority && !BossArena.LocalInArena(cine));
                yield return WaitFor(() => !me.Static, 5f);   // (whatever else held us: the teleport's fade)
                bool held = me.Static;
                string owners = string.Join(",", me.staticOwners.Select(o => o?.ToString()));
                cine.HandleEvent("Spawn");
                yield return new WaitForSecondsRealtime(0.3f);
                Check("her roar doesn't hold the host out of the arena", (!me.Static || held) && BossArena.PausedOnly == paused + 1,
                    $"static {me.Static} (before {held}: {owners}), {BossArena.PausedOnly - paused} spider pauses");
            }
            else
            {
                yield return WaitFor(() => BossArena.Holds > holds, 8f);
                Check("the guest in the arena is still held by it", BossArena.Holds == holds + 1, $"{BossArena.Holds - holds}");
            }
            Plugin.Log.LogInfo($"[boss] events sent {BossArena.EventsSent}, heard {BossArena.EventsHeard}, holds {BossArena.Holds}, shakes {BossArena.Shakes}, paused only {BossArena.PausedOnly}");
            yield return Barrier("done", 40f);
            yield return FinishMortal("her roar and shakes reach everyone in her arena");
        }
    }
}
