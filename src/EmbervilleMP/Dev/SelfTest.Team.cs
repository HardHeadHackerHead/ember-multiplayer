using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cysharp.Threading.Tasks;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    // Teammates' presence: their sounds, night light, teleport / respawn flashes, floating text, death marks and
    // attack speeds, as seen on the other screen. The guest acts, the host checks its puppet (teamlight: both).
    // Tests run headless with Unity audio off: what's checked is that the sound EVENTS are posted (TeamSounds counts
    // what it posts on a teammate's emitter, and on which), not that anything is heard.
    internal static partial class SelfTest
    {
        private static RemotePlayer teamOther;

        // Side by side at the mushroom cavern's entrance (no enemy reaches it in the time these take), each seeing the
        // other's puppet.
        private static IEnumerator TeamPlaced()
        {
            teamOther = null;
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) yield break;
            Game.LocalPlayer.Invincible = true;
            yield return TeleportTo(CombatArea, s.IsHost ? new Vector2(51.8f, 76.3f) : new Vector2(53.4f, 76.3f));
            Game.LocalPlayer.Invincible = true;
            yield return Barrier("placed", 40f);
            yield return WaitFor(() => (teamOther = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault(r => r.Shown)) != null, 20f);
            Check("teammate's puppet is shown", teamOther != null);
        }

        private static IEnumerable<Attack> PlayerAttacks() =>
            Resources.FindObjectsOfTypeAll<Attack>().Where(a => a != null && a.attackFor == Attack.Target.Player).OrderBy(a => a.name);

        // A plain attack first (no spawned objects: less going on), then the rest.
        private static int Plainness(Attack a) => (a.attackSpawner != null ? 1 : 0) + (a.projectile != null ? 1 : 0);

        private static uint SfxId(PlayerMaster me, string id)
        {
            var s = me.sfxComponent != null && me.sfxComponent.sfxList != null ? me.sfxComponent.sfxList.Find(x => x != null && x.sfxID == id) : null;
            return s != null && s.sfxEvent != null && s.sfxEvent.IsValid() ? s.sfxEvent.Id : 0u;
        }

        private static uint StepId(PlayerMaster me)
        {
            var st = me.GetComponent<StepsComponent>();
            return st != null && st.stepEvent != null && st.stepEvent.IsValid() ? st.stepEvent.Id : 0u;
        }

        private static List<uint> Ids(string text) =>
            string.IsNullOrEmpty(text) ? new List<uint>() : text.Split(',').Select(x => uint.TryParse(x, out uint v) ? v : 0u).Where(v => v != 0).ToList();

        // Every item of `want` is in `have`, as many times.
        private static bool HasAll(List<uint> have, List<uint> want) =>
            want.GroupBy(x => x).All(g => have.Count(x => x == g.Key) >= g.Count());

        // ---- teamsounds: the guest attacks, dodges, gets hurt and walks; the host's copy of the guest makes the sounds ----
        [Scenario("teamsounds", Timeout = 200)]
        private static IEnumerator TeamSoundsTest()
        {
            yield return TeamPlaced();
            var s = Session.Current;
            var snd = TeamSounds.Instance;
            if (s == null || !Check("sound sync is running", snd != null)) { yield return Finish(""); yield break; }
            var me = Game.LocalPlayer;
            var guest = teamOther;
            byte gid = guest != null ? guest.Info.Id : (byte)0;
            yield return new WaitForSecondsRealtime(1f);   // the teleport's own sounds are done with
            snd.SentLog.Clear();
            snd.PlayedLog.Clear();
            snd.StepsBy.Clear();
            int hitTypes0 = snd.HitTypesApplied, own0 = snd.OwnFootsteps;
            yield return Barrier("cleared");

            if (!s.IsHost)
            {
                // 1. an attack with a sound (PlayerAttack.EnterState posts it)
                var attack = PlayerAttacks().Where(a => a.sfx != null && a.sfx.IsValid() && !string.IsNullOrEmpty(a.animation)).OrderBy(Plainness).FirstOrDefault();
                if (Check("an attack with a sound in this build", attack != null)) yield return UseAttack(me, attack, Vector2.right, "teamsounds-attack-guest");
                // 2. a dodge (PlayerDodge.EnterState posts its sound)
                var dodge = me.equippedDodge != null && me.equippedDodge.sfx != null && me.equippedDodge.sfx.IsValid() ? me.equippedDodge
                    : PlayerAttacks().FirstOrDefault(a => a.dodgeSpeed > 0f && a.dodgeDistance > 0f && a.sfx != null && a.sfx.IsValid());
                if (Check("a dodge with a sound", dodge != null, me.equippedDodge != null ? me.equippedDodge.name : "nothing equipped"))
                {
                    yield return WaitIdle(me);
                    me.currentDodge = dodge;
                    Try("dodge", () => me.ChangeState(me.stateDodge));
                    yield return WaitFor(() => me.currentState != me.stateDodge, 2f);
                    if (me.currentState == me.stateDodge) Try("idle", () => me.ChangeState(me.stateIdle));
                }
                yield return new WaitForSecondsRealtime(0.4f);
                // 3. a hit ("Hurt", with the hit's type as a Wwise switch)
                yield return WaitIdle(me);
                me.Invincible = false;
                me.ResetToFullHealth();
                var hit = Hit(null, me, 3);
                hit.HitType = DamageHitType.Cut;
                me.ApplyDamage(hit);
                me.Invincible = true;
                yield return new WaitForSecondsRealtime(0.8f);
                // 4. a walk, the way cutscenes walk the player (footsteps aren't sent: the host's copy makes them)
                yield return WaitIdle(me);
                Vector2 start = me.rb.position;
                // (1.2 s each way: some 6 footsteps on the host, 2 are enough; 2.5 s each way made 13)
                me.MoveToPosition(start + Vector2.right * 2f, 1.2f).Forget();
                yield return new WaitForSecondsRealtime(1.5f);
                me.MoveToPosition(start, 1.2f).Forget();
                yield return new WaitForSecondsRealtime(1.5f);

                uint hurt = SfxId(me, "Hurt"), step = StepId(me);
                var sent = snd.SentLog.ToList();
                Check("guest sent its attack's sound", attack == null || sent.Contains(attack.sfx.Id), $"{attack?.name}: {attack?.sfx.Name}");
                Check("guest sent its dodge's sound", dodge == null || sent.Contains(dodge.sfx.Id), $"{dodge?.name}: {dodge?.sfx.Name}");
                Check("guest sent its 'Hurt' sound", hurt != 0 && sent.Contains(hurt), $"Hurt id {hurt}");
                Check("guest's footsteps weren't sent", step == 0 || !sent.Contains(step),
                    $"step id {step}, the guest's own DoFootstep events: {snd.OwnFootsteps - own0} (0 is fine headless)");
                Chat.Send("#sfx=" + string.Join(",", sent));
                Chat.Send("#hurt=" + hurt);
            }
            yield return Barrier("acted", 60f);

            if (s.IsHost)
            {
                yield return WaitFor(() => peerData.ContainsKey("sfx") && peerData.ContainsKey("hurt"), 10f);
                var sent = Ids(peerData.TryGetValue("sfx", out var t) ? t : "");
                uint hurt = peerData.TryGetValue("hurt", out var h) && uint.TryParse(h, out uint hv) ? hv : 0u;
                List<uint> Played() => snd.PlayedLog.TryGetValue(gid, out var l) ? l : new List<uint>();
                yield return WaitFor(() => sent.Count > 0 && HasAll(Played(), sent), 5f);
                string detail = $"sent [{string.Join(",", sent)}], played [{string.Join(",", Played())}]";
                Check("guest's sounds reached the host", sent.Count >= 3, detail);
                Check("host posted every one of them for the guest", sent.Count > 0 && HasAll(Played(), sent), detail);
                Check("and nothing else for the guest (only what its character posted)", Played().All(sent.Contains), detail);
                Check("on an emitter at the guest's puppet", snd.LastPlayedToPuppet >= 0f && snd.LastPlayedToPuppet < 0.05f, $"{snd.LastPlayedToPuppet:F3} from the puppet");
                Check("'Hurt' played with its hit type switch", hurt != 0 && Played().Contains(hurt) && snd.HitTypesApplied > hitTypes0, $"{snd.HitTypesApplied - hitTypes0} switches");
                var clips = guest != null && guest.PuppetAnimator != null && guest.PuppetAnimator.runtimeAnimatorController != null
                    ? guest.PuppetAnimator.runtimeAnimatorController.animationClips.Where(c => c != null && TeamSounds.StepTimes(c).Length > 0).Select(c => c.name).Distinct().ToList()
                    : new List<string>();
                Check("the puppet's clips have footstep events", clips.Count > 0, string.Join(", ", clips));
                int steps = snd.StepsBy.TryGetValue(gid, out int st) ? st : 0;
                Check("guest's walk made footsteps on the host, from its puppet's animation", steps >= 2, $"{steps} steps");
                Check("no sound errors", snd.Errors == 0, snd.Errors.ToString());
                Plugin.Log.LogInfo($"[sounds] batches sent/received {snd.BatchesSent}/{snd.BatchesReceived}, played {snd.Played}, dropped {snd.Dropped}");
            }
            yield return Barrier("done");
            yield return Finish("teammates' sounds play on their puppets");
        }

        // ---- teamlight: at night a teammate carries the same light as we do ----
        [Scenario("teamlight", Timeout = 150)]
        private static IEnumerator TeamLightTest()
        {
            yield return TeamPlaced();
            var s = Session.Current;
            var other = teamOther;
            if (s == null || other == null || !Singleton<TimeManager>.HasMain) { Check("time manager", Singleton<TimeManager>.HasMain); yield return Finish(""); yield break; }
            var me = Game.LocalPlayer;
            var tm = Singleton<TimeManager>.main;
            bool forced = tm.IsForcedToCurrentState;
            tm.IsForcedToCurrentState = false;   // this room may hold the time still: let the clock (and the light) run
            var mine = PuppetBuilder.NightLightOf(me.transform);
            var theirs = other.NightLight;
            Check("our character has its night light", mine != null);
            Check("the teammate's puppet has one too", theirs != null && other.Puppet != null && theirs.transform.parent == other.Puppet.transform);
            Check("without the game's tint script (its colour comes from ours)", theirs != null && theirs.GetComponent<WorldLight2D>() == null);
            if (mine == null || theirs == null) { tm.IsForcedToCurrentState = forced; yield return Finish(""); yield break; }

            bool Same(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.02f && Mathf.Abs(a.g - b.g) < 0.02f && Mathf.Abs(a.b - b.b) < 0.02f && Mathf.Abs(a.a - b.a) < 0.02f;
            string Show(Color c) => $"({c.r:F2},{c.g:F2},{c.b:F2})";

            tm.dayTime = 12f;   // both: noon (the host's clock reaches the guest anyway)
            yield return new WaitForSecondsRealtime(1f);
            Color myNoon = mine.color, theirNoon = theirs.color;
            Check("at noon the teammate's light is ours", Same(myNoon, theirNoon) && Mathf.Abs(mine.intensity - theirs.intensity) < 0.01f,
                $"ours {Show(myNoon)} x{mine.intensity:F2}, theirs {Show(theirNoon)} x{theirs.intensity:F2}");
            yield return Barrier("noon");

            tm.dayTime = 23f;
            yield return new WaitForSecondsRealtime(1f);
            Color myNight = mine.color, theirNight = theirs.color;
            bool on = theirs.enabled && theirs.gameObject.activeInHierarchy;
            Check("at night the teammate's light is ours", Same(myNight, theirNight) && Mathf.Abs(mine.intensity - theirs.intensity) < 0.01f,
                $"ours {Show(myNight)} x{mine.intensity:F2}, theirs {Show(theirNight)} x{theirs.intensity:F2}");
            Check("and it's on", on == (mine.enabled && mine.gameObject.activeInHierarchy) && on);
            Check("our own light changes with the time (the game's tint)", !Same(myNoon, myNight), $"{Show(myNoon)} -> {Show(myNight)}");
            Check("so the teammate's changed the same way", !Same(theirNoon, theirNight), $"{Show(theirNoon)} -> {Show(theirNight)}");
            yield return DevHarness.Screenshot("teamlight-night-" + (s.IsHost ? "host" : "guest"));
            yield return Barrier("night");
            tm.dayTime = 12f;
            tm.IsForcedToCurrentState = forced;
            yield return Barrier("done");
            yield return Finish("teammates carry their night light");
        }

        // ---- animspeed: an attack with a speed multiplier plays at that speed on the puppet ----
        [Scenario("animspeed", Timeout = 150)]
        private static IEnumerator AnimSpeedTest()
        {
            yield return TeamPlaced();
            var s = Session.Current;
            var fx = TeamFx.Instance;
            if (s == null || !Check("presence effects are running", fx != null)) { yield return Finish(""); yield break; }
            var me = Game.LocalPlayer;
            var guest = teamOther;
            int sent0 = fx.SpeedsSent, applied0 = fx.SpeedsApplied;
            yield return Barrier("ready");
            if (!s.IsHost)
            {
                var attack = PlayerAttacks().Where(a => Mathf.Abs(a.animationSpeedMultiplier - 1f) > 0.05f && !string.IsNullOrEmpty(a.animation)).OrderBy(Plainness).FirstOrDefault();
                float seen = 1f;
                bool done = false;
                if (attack != null)
                {
                    Plugin.Log.LogInfo($"[test] {attack.name}: speed x{attack.animationSpeedMultiplier}");
                    MPRunner.Instance.StartCoroutine(Run(UseAttack(me, attack, Vector2.right, "animspeed-guest"), () => done = true));
                }
                else
                {
                    // No attack with a multiplier in this build: the same thing by hand (what PlayerAttack does).
                    Plugin.Log.LogInfo("[test] no attack with a speed multiplier; setting the animator's speed directly");
                    MPRunner.Instance.StartCoroutine(Run(SpeedFor(me, 1.37f, 1.5f), () => done = true));
                }
                yield return WaitFor(() =>
                {
                    if (Mathf.Abs(me.animator.speed - 1f) > Mathf.Abs(seen - 1f)) seen = me.animator.speed;
                    return done;
                }, 15f);
                Check("guest's animator ran at another speed", Mathf.Abs(seen - 1f) > 0.04f, $"x{seen:F3}");
                Check("and back to normal", Mathf.Abs(me.animator.speed - 1f) < 0.001f, $"x{me.animator.speed:F3}");
                Check("guest sent its speed on each change", fx.SpeedsSent - sent0 >= 2, $"{fx.SpeedsSent - sent0} sent");
                Chat.Send("#speed=" + seen.ToString(CultureInfo.InvariantCulture));
                yield return Barrier("attack-done");
            }
            else
            {
                var seen = new List<float>();
                yield return WaitFor(() =>
                {
                    var a = guest != null && guest.Shown ? guest.PuppetAnimator : null;
                    if (a != null && !seen.Any(x => Mathf.Abs(x - a.speed) < 0.001f)) seen.Add(a.speed);
                    return Bucket(scenarioIndex).signals.ContainsKey("#attack-done");
                }, 30f);
                yield return Barrier("attack-done");
                float want = peerData.TryGetValue("speed", out var w) && float.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out float wv) ? wv : -1f;
                string list = string.Join(", ", seen.Select(x => x.ToString("F3")));
                Check("the guest's puppet played at the guest's speed", want > 0f && seen.Any(x => Mathf.Abs(x - want) < 0.02f), $"guest x{want:F3}, puppet [{list}]");
                yield return WaitFor(() => guest.PuppetAnimator != null && Mathf.Abs(guest.PuppetAnimator.speed - 1f) < 0.001f, 3f);
                Check("then at normal speed again", guest.PuppetAnimator != null && Mathf.Abs(guest.PuppetAnimator.speed - 1f) < 0.001f, $"x{guest.PuppetAnimator?.speed:F3}");
                Check("speeds arrived", fx.SpeedsApplied - applied0 >= 2, $"{fx.SpeedsApplied - applied0} applied");
            }
            yield return Barrier("done");
            yield return Finish("attack speeds show on teammates");
        }

        private static IEnumerator Run(IEnumerator body, System.Action done)
        {
            yield return body;
            done();
        }

        private static IEnumerator SpeedFor(PlayerMaster me, float speed, float seconds)
        {
            me.animator.speed = speed;
            yield return new WaitForSecondsRealtime(seconds);
            me.animator.speed = 1f;
        }

        // ---- teamfx: floating text over the guest, its teleport flash, its death mark and respawn flash ----
        [Scenario("teamfx", Timeout = 260)]
        private static IEnumerator TeamFxTest()
        {
            DevHarness.FastDeath = true;   // the death mark and respawn flash still play, in their order
            yield return TeamPlaced();
            var s = Session.Current;
            var fx = TeamFx.Instance;
            var snd = TeamSounds.Instance;
            if (s == null || !Check("presence effects are running", fx != null && snd != null)) { yield return Finish(""); yield break; }
            var me = Game.LocalPlayer;
            var guest = teamOther;
            byte gid = guest != null ? guest.Info.Id : (byte)0;
            string tag = s.IsHost ? "host" : "guest";

            // Every damage text the host's screen raises over the guest's puppet, whatever raised it.
            int atPuppet = 0;
            System.Action<UIDamageEvent> count = e =>
            {
                if (guest != null && guest.Puppet != null && ((Vector2)(e.origin - guest.Puppet.transform.position)).sqrMagnitude < 0.01f) atPuppet++;
            };
            if (s.IsHost) Bus<UIDamageEvent>.Listeners += count;
            int texts0 = fx.TextsShown, bars0 = fx.BarsShown, fx0 = fx.FxShown, marks0 = fx.MarksShown, played0 = OneShotFx.Played, logged0 = fx.TextLog.Count;
            int sent0 = fx.TextsSent, barsSent0 = fx.BarsSent;
            snd.PlayedLog.Clear();
            fx.ShownLog.Clear();
            fx.MomentLog.Clear();
            yield return Barrier("ready");

            // 1. floating text: a hit, a block, a heal
            if (!s.IsHost)
            {
                me.Invincible = false;
                me.ResetToFullHealth();
                me.ApplyDamage(Hit(null, me, 7));
                yield return new WaitForSecondsRealtime(0.5f);
                var block = Hit(null, me, 0);
                block.Result = DamageResult.Block;
                me.ApplyDamage(block);
                yield return new WaitForSecondsRealtime(0.5f);
                me.RestoreHealth(5);
                me.Invincible = true;
                Check("guest sent what its screen showed over it", fx.TextsSent - sent0 >= 3, $"{fx.TextsSent - sent0} texts, {fx.BarsSent - barsSent0} bar updates");
                Chat.Send("#bars=" + (fx.BarsSent - barsSent0));
            }
            yield return Barrier("texts");
            if (s.IsHost)
            {
                bool Has(System.Func<(byte from, float amount, string message, DamageResult result, Vector3 at), bool> f) =>
                    fx.TextLog.Skip(logged0).Any(x => x.from == gid && f(x));
                yield return WaitFor(() => Has(x => x.result == DamageResult.Success && Mathf.Abs(x.amount - 7f) < 0.01f) &&
                    Has(x => x.result == DamageResult.Block && x.message == "Blocked") && Has(x => x.result == DamageResult.Heal && Mathf.Abs(x.amount - 5f) < 0.01f), 5f);
                string all = string.Join("; ", fx.TextLog.Skip(logged0).Select(x => $"{x.result} {x.amount:0.#} '{x.message}'"));
                Check("the guest's damage (7) shows over its puppet", Has(x => x.result == DamageResult.Success && Mathf.Abs(x.amount - 7f) < 0.01f), all);
                Check("its 'Blocked'", Has(x => x.result == DamageResult.Block && x.message == "Blocked"), all);
                Check("its heal (+5)", Has(x => x.result == DamageResult.Heal && Mathf.Abs(x.amount - 5f) < 0.01f), all);
                Check("over the puppet, not over us", fx.TextLog.Skip(logged0).All(x => Vector2.Distance(x.at, me.transform.position) > 0.8f), all);
                Check("each shown once (nothing else shows numbers over a puppet)", atPuppet == fx.TextsShown - texts0, $"{atPuppet} raised at the puppet, {fx.TextsShown - texts0} ours");
                int barsSent = peerData.TryGetValue("bars", out var b) && int.TryParse(b, out int bv) ? bv : -1;
                if (barsSent > 0)
                {
                    Check("its health bar shows over the puppet", fx.BarsShown > bars0 && fx.LastBarInUi != 0,
                        $"{fx.BarsShown - bars0} shown, in the game's UI: {(fx.LastBarInUi < 0 ? "no UI" : fx.LastBarInUi == 1 ? "yes" : "no")}");
                }
                else Plugin.Log.LogInfo($"[test] the guest showed no floating bar ({barsSent}): its option is off");
                Bus<UIDamageEvent>.Listeners -= count;
            }
            yield return Barrier("texts-seen");

            // 2. the teleport flash (what arriving through a portal plays: GameManager.PortalSpawnAnim)
            if (!s.IsHost)
            {
                Game.Manager.PortalSpawnAnim();
                // It holds the character still (AddStaticStack) until its flash has played, then lets go with
                // ChangeState(idle): a death before that would be cut short by it.
                yield return new WaitForSecondsRealtime(0.5f);
                yield return WaitFor(() => me.staticOwners.Count == 0 && me.currentState != me.stateStatic &&
                    Game.Manager.gameState == GameState.Playing && Game.Manager.gameStarted, 10f);
                Check("guest's teleport-in finished", me.staticOwners.Count == 0 && me.currentState != me.stateStatic, $"state {me.currentState?.GetType().Name}, {me.staticOwners.Count} holding it");
                Chat.Send("#tpsfx=" + SfxId(me, "Teleport"));
            }
            yield return Barrier("teleported");
            if (s.IsHost)
            {
                yield return WaitFor(() => fx.ShownLog.Any(x => x.from == gid && x.what == "Teleport"), 3f);
                var shown = fx.ShownLog.FirstOrDefault(x => x.from == gid && x.what == "Teleport");
                Check("the guest's teleport flash plays here", shown.what == "Teleport", string.Join(", ", fx.MomentLog));
                Vector2 puppetAt = guest != null && guest.Puppet != null ? (Vector2)guest.Puppet.transform.position : guest?.Latest?.Position ?? Vector2.zero;
                Check("where the guest is", shown.what == "Teleport" && Vector2.Distance(shown.at, puppetAt) < 1f, $"{shown.at} vs puppet {puppetAt}");
                Check("its effects animator played it", OneShotFx.Played > played0, $"{OneShotFx.Played - played0}");
                uint tp = peerData.TryGetValue("tpsfx", out var tps) && uint.TryParse(tps, out uint tv) ? tv : 0u;
                // The demo's character has no "Teleport" sound (the game logs it can't find it): nothing to hear then.
                if (tp != 0) Check("with its sound", snd.PlayedLog.TryGetValue(gid, out var l) && l.Contains(tp), $"Teleport id {tp}");
                else Plugin.Log.LogInfo("[test] the character has no 'Teleport' sound in this build");
            }
            yield return DevHarness.Screenshot("teamfx-teleport-" + tag);
            yield return Barrier("teleport-seen");

            // 3. a death: the scorch mark where the guest fell, then the respawn flash (played while it's hidden)
            Vector2 diedAt = Vector2.zero;
            if (!s.IsHost)
            {
                yield return WaitIdle(me);
                int moments0 = fx.MomentsSent;
                me.Invincible = false;
                me.ApplyDamage(Hit(null, me, 99999));
                yield return WaitFor(() => me.IsDead, 5f);
                Check("guest died", me.IsDead);
                diedAt = me.transform.position;
                Chat.Send("#diedat=" + diedAt.x.ToString(CultureInfo.InvariantCulture) + "," + diedAt.y.ToString(CultureInfo.InvariantCulture));
                yield return WaitFor(() => !me.IsDead && Game.InGame && Game.Manager.gameState == GameState.Playing, 45f);
                me = Game.LocalPlayer;
                Check("guest got back up", !me.IsDead);
                Check("guest sent its death mark and its respawn flash", fx.MomentsSent - moments0 >= 2, $"{fx.MomentsSent - moments0} sent");
                me.Invincible = true;
                Chat.Send("#uparea=" + Game.AreaName);
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("diedat"), 15f);
                if (peerData.TryGetValue("diedat", out var d))
                {
                    var p = d.Split(',');
                    diedAt = new Vector2(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture));
                }
                yield return WaitFor(() => fx.ShownLog.Any(x => x.from == gid && x.what.EndsWith("Mark")), 10f);
                var mark = fx.ShownLog.FirstOrDefault(x => x.from == gid && x.what != null && x.what.EndsWith("Mark"));
                Check("the guest's death mark is left here", mark.what != null, string.Join(", ", fx.MomentLog));
                Check("where it fell", mark.what != null && Vector2.Distance(mark.at, diedAt) < 1.5f, $"{mark.at} vs {diedAt}");
                yield return WaitFor(() => peerData.ContainsKey("uparea"), 50f);
                yield return WaitFor(() => fx.MomentLog.Contains($"{gid}:ExtraFx:Spawn"), 3f);
                Check("the guest's respawn flash reached the host", fx.MomentLog.Contains($"{gid}:ExtraFx:Spawn"), string.Join(", ", fx.MomentLog));
                if (peerData.TryGetValue("uparea", out var area) && area == Game.AreaName)
                    Check("and played here (same room)", fx.ShownLog.Any(x => x.from == gid && x.what == "Spawn"), string.Join(", ", fx.ShownLog.Select(x => x.what)));
                else Plugin.Log.LogInfo($"[test] the guest got up in {area}, not here: its flash isn't for us");
            }
            yield return Barrier("respawned", 60f);
            Plugin.Log.LogInfo($"[teamfx] moments {fx.MomentsSent}/{fx.MomentsReceived}, fx {fx.FxShown}, marks {fx.MarksShown}, texts {fx.TextsSent}/{fx.TextsShown}, bars {fx.BarsSent}/{fx.BarsShown}");
            yield return Barrier("done");
            yield return Finish("teammates' floating text, teleport, death and respawn effects show");
        }
    }
}
