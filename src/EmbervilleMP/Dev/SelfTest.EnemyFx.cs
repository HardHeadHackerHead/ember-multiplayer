using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;
using UnityEngine.Events;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Host and guest side by side at the mushroom cavern's entrance; the host picks an enemy (sturdy enough for the
        // whole scenario) and the guest waits until its copy is a puppet of the host's.
        private static IEnumerator EnemyFxSetup(System.Func<Enemy, bool> prefer, System.Action<Enemy> picked, System.Action beforeHits = null)
        {
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) yield break;
            yield return TeleportTo(CombatArea, s.IsHost ? new Vector2(51.8f, 76.3f) : new Vector2(53.4f, 76.3f));
            yield return Barrier("placed", 40f);
            var es = EnemySync.Instance;
            Enemy e = null;
            if (s.IsHost)
            {
                yield return new WaitForSecondsRealtime(1f);
                var me = Game.LocalPlayer;
                var alive = EnemiesIn(CombatArea).Where(x => !x.IsDead && es.IdOf(x) != 0).OrderBy(x => (x.transform.position - me.transform.position).sqrMagnitude).ToList();
                e = alive.FirstOrDefault(prefer) ?? alive.FirstOrDefault();
                if (e != null)
                {
                    e.initHealth = 1000f;   // many hits to come: it mustn't die of them
                    e.currentHealth = 1000f;
                }
                Chat.Send("#target=" + (e != null ? es.IdOf(e) : 0));
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("target"), 20f);
                int id = peerData.TryGetValue("target", out var t) ? int.Parse(t) : 0;
                yield return WaitFor(() => (e = es.ById(id)) != null && es.IsPuppet(e), 10f);
                Check("guest's copy of the enemy is a puppet of the host's", e != null && es.IsPuppet(e), $"#{id}");
            }
            Check("picked an enemy", e != null);
            picked(e);
            beforeHits?.Invoke();   // the guest may hit as soon as this barrier lets it through
            yield return Barrier("target");
        }

        // 1. The runner's effects show on the puppets (status loop, popup), invulnerability and tint too, and the
        //    runner's hit event replays. 2. An effect the guest's character puts on a puppet reaches the runner's real
        //    enemy and ticks there, credited to the guest.
        [Scenario("enemyfx", Timeout = 220)]
        private static IEnumerator EnemyFxTest()
        {
            Enemy e = null;
            // The popups seen so far, read before the last barrier of the setup: the host burns the enemy as soon as it's
            // through, and its popup can arrive here before we are.
            int popups0 = 0;
            yield return EnemyFxSetup(x => true, x => e = x, () => popups0 = EnemyFx.Instance?.PopupsShown ?? 0);
            var s = Session.Current;
            var fx = EnemyFx.Instance;
            var es = EnemySync.Instance;
            var me = Game.LocalPlayer;
            if (s == null || e == null || fx == null) { yield return Barrier("done"); yield return Finish(""); yield break; }
            int id = es.IdOf(e);
            System.Func<EffectType, bool> mirrored = t => fx.MirroredOf(id).Any(x => x.Type == t);
            System.Func<EffectType, bool> shows = t => e.appliedEffects != null && e.appliedEffects.Contains(t);
            if (!s.IsHost) LogEnemyEffectWiring(e);

            // 1. a burn on the runner's enemy
            if (s.IsHost)
            {
                bool ok = e.EffectsHandler.HandleEffect(new DamageEffect { Effect = EffectType.Burn, DamagePerSec = 1, Duration = 20f });
                Check("host's enemy burns", ok && e.EffectsHandler.HasEffect(EffectType.Burn));
            }
            yield return Barrier("burn-on");
            if (!s.IsHost)
            {
                yield return WaitFor(() => mirrored(EffectType.Burn) && shows(EffectType.Burn) && fx.PopupsShown > popups0, 5f);
                Check("the runner's burn reached the guest", mirrored(EffectType.Burn), string.Join(", ", fx.MirroredOf(id).Select(x => x.Type)));
                Check("the puppet burns the game's way (Enemy.SetEffect)", shows(EffectType.Burn), $"applied [{string.Join(", ", e.appliedEffects ?? new List<EffectType>())}], FX {FxState(e)}");
                Check("the 'Burn' popup showed over the puppet", fx.PopupsShown > popups0, $"{fx.PopupsShown - popups0} popups");
            }
            yield return DevHarness.Screenshot(s.IsHost ? "enemyfx-burn-host" : "enemyfx-burn-guest");
            yield return Barrier("burn-seen");
            if (s.IsHost) e.EffectsHandler.RemoveEffect(EffectType.Burn);
            yield return Barrier("burn-off");
            if (!s.IsHost)
            {
                yield return WaitFor(() => !mirrored(EffectType.Burn) && !shows(EffectType.Burn), 5f);
                Check("the burn ended on the puppet too", !mirrored(EffectType.Burn) && !shows(EffectType.Burn), $"applied [{string.Join(", ", e.appliedEffects ?? new List<EffectType>())}]");
            }
            yield return Barrier("burn-cleared");

            // 2. the guest's character poisons the puppet the way a passive does (Bleeding Edge: our OnAppliedDamage)
            int intents0 = fx.IntentsSent, applied0 = fx.IntentsApplied;
            if (!s.IsHost)
            {
                if (me.IsDead || me.CurrentHealthPoints < me.MaxHealthPoints / 2) me.ResetToFullHealth();
                UnityAction<DamageData> passive = d =>
                {
                    var target = d.Target is DamageableCollider dc ? dc.Damageable : d.Target;
                    target?.EffectsHandler?.HandleEffect(new DamageEffect { Effect = EffectType.Poison, DamagePerSec = 2, Duration = 8f });
                };
                me.OnAppliedDamage.AddListener(passive);
                try { CombatSystem.Resolve(Hit(me, e, 1)); }
                finally { me.OnAppliedDamage.RemoveListener(passive); }
                Check("the guest's poison on the puppet went to the runner", fx.IntentsSent > intents0, $"{fx.IntentsSent - intents0} sent");
            }
            yield return Barrier("poison-sent");
            if (s.IsHost)
            {
                yield return WaitFor(() => e.EffectsHandler.HasEffect(EffectType.Poison), 5f);
                Check("the runner's enemy got the guest's poison", e.EffectsHandler.HasEffect(EffectType.Poison), $"{fx.IntentsApplied - applied0} applied");
                float hp0 = e.CurrentHealth;
                yield return WaitFor(() => e.CurrentHealth < hp0, 2.6f);
                Check("and it ticks there", e.CurrentHealth < hp0, $"{hp0} -> {e.CurrentHealth}");
                var guest = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault();
                Check("credited to the guest", guest != null && es.CurrentHitter(e) == guest.Info.Id, $"hitter #{es.CurrentHitter(e)}");
            }
            else
            {
                yield return WaitFor(() => mirrored(EffectType.Poison) && shows(EffectType.Poison), 6f);
                Check("the guest's poison shows on its puppet, from the runner's list", mirrored(EffectType.Poison) && shows(EffectType.Poison));
            }
            yield return Barrier("poison-done");
            if (s.IsHost) e.EffectsHandler.RemoveEffect(EffectType.Poison);

            // 3. invulnerable (SetEnemyInvulnerableAction): the puppet ignores hits like the game does
            if (s.IsHost) e.CanTakeDamage = false;
            yield return Barrier("invuln-on");
            if (!s.IsHost)
            {
                yield return WaitFor(() => !e.CanTakeDamage, 5f);
                Check("the puppet is invulnerable like the runner's", !e.CanTakeDamage);
                int hits0 = es.HitsSent;
                CombatSystem.Resolve(Hit(me, e, 5));
                Check("a hit on it isn't sent (no number, nothing to resolve)", es.HitsSent == hits0, $"{es.HitsSent - hits0} sent");
            }
            yield return Barrier("invuln-hit");
            if (s.IsHost) e.CanTakeDamage = true;
            yield return Barrier("invuln-off");
            if (!s.IsHost)
            {
                yield return WaitFor(() => e.CanTakeDamage, 5f);
                Check("and vulnerable again", e.CanTakeDamage);
            }

            // 4. a tint (TintEnemyAction); the puppet goes back to its own colours after (random for some mushrooms)
            var tint = new Color(1f, 0.25f, 0.25f);
            var ownColour = FirstSpriteColor(e);
            yield return Barrier("tint-ready");   // (the guest's colours read before the tint can arrive)
            if (s.IsHost) e.SetSpriteColor(tint);
            yield return Barrier("tint-on");
            if (!s.IsHost)
            {
                yield return WaitFor(() => ColorIs(e, tint), 5f);
                Check("the puppet is tinted like the runner's", ColorIs(e, tint), FirstSpriteColor(e).ToString());
            }
            yield return Barrier("tint-seen");
            if (s.IsHost) e.SetSpriteColor(Color.white);
            yield return Barrier("tint-off");
            if (!s.IsHost)
            {
                yield return WaitFor(() => ColorIs(e, ownColour), 5f);
                Check("and back to its colours", ColorIs(e, ownColour), $"{FirstSpriteColor(e)}, was {ownColour}");
            }

            // 5. the runner's hit: its number and the enemy's hit event (flash) on the puppet
            int events0 = fx.HitEventsReplayed;
            if (s.IsHost)
                for (int i = 0; i < 2; i++) { CombatSystem.Resolve(Hit(me, e, 10)); yield return new WaitForSecondsRealtime(0.3f); }
            yield return Barrier("host-hit");
            if (!s.IsHost)
            {
                yield return WaitFor(() => fx.HitEventsReplayed > events0, 5f);
                Check("the puppet replays the runner's hit event", fx.HitEventsReplayed > events0, $"{fx.HitEventsReplayed - events0}, {e.onHitEvent?.GetPersistentEventCount() ?? 0} listeners");
            }
            Plugin.Log.LogInfo($"[enemyfx] lists {fx.ListsSent}/{fx.ListsReceived} intents {fx.IntentsSent}/{fx.IntentsApplied} popups {fx.PopupsSent}/{fx.PopupsShown} hit events {fx.HitEventsReplayed}");
            yield return Barrier("done");
            yield return Finish("enemies' effects and looks are the runner's everywhere");
        }

        // A guest's hits are resolved against the runner's enemy (its runtime damage-received modifiers: the Acolyte
        // Protector's aura is -80%), the guest shows the runner's numbers, and the knockback points away from the guest.
        [Scenario("protector", Timeout = 200)]
        private static IEnumerator ProtectorTest()
        {
            // The runner's counts are read before each barrier the guest's hits follow: the guest hits as soon as our
            // signal reaches it, and its hits can arrive in the same poll as its own signal, before we resume.
            int dealt0 = 0;
            Enemy e = null;
            yield return EnemyFxSetup(x => KnockbackForce(x) != KnockBack.None, x => e = x, () => dealt0 = EnemyFx.Instance?.RawDealt.Count ?? 0);
            var s = Session.Current;
            var fx = EnemyFx.Instance;
            var es = EnemySync.Instance;
            var me = Game.LocalPlayer;
            if (s == null || e == null || fx == null) { yield return Barrier("done"); yield return Finish(""); yield break; }

            // 1. baseline: three plain hits
            if (!s.IsHost) yield return GuestHits(me, e, 3, 20);
            yield return Barrier("base-hits");
            List<int> baseline = null;
            if (s.IsHost)
            {
                yield return WaitFor(() => fx.RawDealt.Count >= dealt0 + 3, 5f);
                baseline = fx.RawDealt.Skip(dealt0).ToList();
                Check("the guest's hits were resolved on the runner", baseline.Count == 3, $"[{string.Join(", ", baseline)}]");
                // What the protector's aura does to itself (AcolyteProtector.StartAura: DamagePercentModifier(-80)).
                e.currentHealth = e.initHealth;
                e.GetCharacterStats().AddDamageReceiveModifier(auraForTest = new DamagePercentModifier(-80f));
                dealt0 = fx.RawDealt.Count;
            }
            int own0 = fx.OwnNumbers.Count;
            yield return Barrier("aura-on");

            // 2. the same hits under the aura
            if (!s.IsHost) yield return GuestHits(me, e, 3, 20);
            yield return Barrier("aura-hits");
            if (s.IsHost)
            {
                yield return WaitFor(() => fx.RawDealt.Count >= dealt0 + 3, 5f);
                var aura = fx.RawDealt.Skip(dealt0).ToList();
                int baseMax = baseline != null && baseline.Count > 0 ? baseline.Max() : 0;
                int auraMax = aura.Count > 0 ? aura.Max() : int.MaxValue;
                Check("under the aura the guest's hits are cut like the host's (-80%)", baseMax > 0 && auraMax <= Mathf.CeilToInt(baseMax * 0.35f) + 1,
                    $"plain [{string.Join(", ", baseline ?? new List<int>())}], aura [{string.Join(", ", aura)}]");
                e.GetCharacterStats().RemoveDamageReceiveModifier(auraForTest);
                Chat.Send("#auradealt=" + string.Join(",", aura));
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("auradealt") && fx.OwnNumbers.Count >= own0 + 3, 8f);
                var runner = (peerData.TryGetValue("auradealt", out var a) ? a : "").Split(',').Where(x => x.Length > 0).Select(int.Parse).Where(x => x > 0).OrderBy(x => x).ToList();
                var shown = fx.OwnNumbers.Skip(own0).Where(x => x > 0).OrderBy(x => x).ToList();
                Check("the guest showed the runner's numbers for its hits", runner.Count > 0 && runner.SequenceEqual(shown), $"runner [{string.Join(", ", runner)}], shown [{string.Join(", ", shown)}]");
            }
            yield return Barrier("aura-done");

            // 3. knockback: the guest stands on the far side of the enemy from the host and hits it
            string kbForce = "None";
            if (s.IsHost) Chat.Send("#kb=" + KnockbackForce(e) + "|" + F(me.transform.position.x) + "|" + F(me.transform.position.y));
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("kb"), 10f);
                var parts = (peerData.TryGetValue("kb", out var k) ? k : "None|0|0").Split('|');
                kbForce = parts[0];
                var host = new Vector2(float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture));
                Vector2 ep = e.transform.position;
                yield return MoveTo(me, ep + (ep - host).normalized * 1.4f, 0.5f);
            }
            yield return new WaitForSecondsRealtime(0.6f);   // our puppet on the host gets there
            int raw0 = fx.RawHitsApplied;
            yield return Barrier("kb-placed");
            if (!s.IsHost)
            {
                var force = (KnockBack)System.Enum.Parse(typeof(KnockBack), kbForce);
                var attack = Resources.FindObjectsOfTypeAll<Attack>().Where(x => x != null && x.knockBackForce == force && !x.willNotStagger &&
                    (x.effects == null || x.effects.Count == 0) && (x.customEffects == null || x.customEffects.Count == 0)).OrderBy(x => x.name).FirstOrDefault();
                if (Check("found an attack with that knockback", force != KnockBack.None && attack != null, $"{force}"))
                {
                    var hit = Hit(me, e, 1);
                    hit.Attack = attack;
                    CombatSystem.Resolve(hit);
                }
            }
            if (s.IsHost)
            {
                var guest = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault(r => r.Shown);
                Vector2 before = e.transform.position;
                float t0 = Time.realtimeSinceStartup;
                while (fx.RawHitsApplied == raw0 && Time.realtimeSinceStartup - t0 < 5f)
                {
                    before = e.transform.position;
                    yield return null;
                }
                bool hitArrived = fx.RawHitsApplied > raw0;
                yield return new WaitForSecondsRealtime(0.4f);
                Vector2 after = e.transform.position;
                Vector2 from = guest != null ? (Vector2)guest.Puppet.transform.position : before;
                float away = Vector2.Dot(after - before, (before - from).normalized);
                Check("the guest's hit pushed the enemy away from the guest", hitArrived && away > 0.15f,
                    $"moved {(after - before).magnitude:F2}, {away:F2} away from the guest (force {KnockbackForce(e)}), hit {(hitArrived ? "arrived" : "missing")}");
            }
            yield return Barrier("done");
            yield return Finish("guests' hits meet the runner's enemy");
        }

        private static DamageModifier auraForTest;

        private static IEnumerator GuestHits(PlayerMaster me, Enemy e, int n, int amount)
        {
            for (int i = 0; i < n; i++)
            {
                if (me.IsDead || me.CurrentHealthPoints < me.MaxHealthPoints / 2) me.ResetToFullHealth();
                CombatSystem.Resolve(Hit(me, e, amount));   // (no crit chance: the same every time)
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        // The strongest knockback this enemy reacts to (Enemy.HandleKnockback skips forces without a curve).
        private static KnockBack KnockbackForce(Enemy e)
        {
            if (e.knockbackCurveHigh.Use) return KnockBack.High;
            if (e.knockbackCurveVeryHigh.Use) return KnockBack.VeryHigh;
            if (e.knockbackCurveMedium.Use) return KnockBack.Medium;
            if (e.knockbackCurveLow.Use) return KnockBack.Low;
            return KnockBack.None;
        }

        // Deaths no player caused give nobody XP (the game's rule): a self-destruct (SetEnemyKilledAction: SetKilled),
        // a burn's tick. A player's kill still gives everyone theirs.
        [Scenario("selfdestruct", Timeout = 180)]
        private static IEnumerator SelfDestructTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) { yield return Finish(""); yield break; }
            yield return TeleportTo(CombatArea, s.IsHost ? new Vector2(51.8f, 76.3f) : new Vector2(53.4f, 76.3f));
            yield return Barrier("placed", 40f);
            var es = EnemySync.Instance;
            var me = Game.LocalPlayer;
            string ids = "0|0|0";
            if (s.IsHost)
            {
                yield return new WaitForSecondsRealtime(1f);
                var alive = EnemiesIn(CombatArea).Where(x => !x.IsDead && es.IdOf(x) != 0).OrderBy(x => (x.transform.position - me.transform.position).sqrMagnitude).ToList();
                // A LilBomber if one is here (TutorialArea's are in the trap rooms); the death path is the same for any.
                var k = alive.FirstOrDefault(x => x.enemyType == EnemyType.Mob_Kamikaze) ?? alive.FirstOrDefault();
                var rest = alive.Where(x => x != k).ToList();
                ids = string.Join("|", new[] { k, rest.ElementAtOrDefault(0), rest.ElementAtOrDefault(1) }.Select(x => x != null ? es.IdOf(x) : 0));
                Chat.Send("#ids=" + ids);
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("ids"), 20f);
                ids = peerData.TryGetValue("ids", out var t) ? t : ids;
            }
            var parts = ids.Split('|').Select(int.Parse).ToArray();
            Enemy bomber = es.ById(parts[0]), burnt = es.ById(parts[1]), slain = es.ById(parts[2]);
            if (!Check("found three enemies", bomber != null && burnt != null && slain != null, ids))
            {
                yield return Barrier("done");
                yield return Finish("");
                yield break;
            }
            // Each step's baselines are read before the barrier the other copy acts after: its kill can reach us before
            // we are through that barrier.
            int xp0 = TotalXp(), kills0 = KillCredit.XpKills(parts[0]);
            yield return Barrier("ids");

            // 1. it blows itself up
            if (s.IsHost)
            {
                bomber.SetKilled();   // what SetEnemyKilledAction does
                bomber.ChangeState(bomber.dieState);   // (its graph would play the blast; here: just lie down)
            }
            yield return WaitFor(() => bomber.IsDead, 5f);
            Check("the self-destruct ran on this copy", bomber.IsDead);
            yield return new WaitForSecondsRealtime(1f);
            Check("no XP for a self-destruct", TotalXp() == xp0 && KillCredit.XpKills(parts[0]) == kills0, $"XP {xp0} -> {TotalXp()}, kills {KillCredit.XpKills(parts[0]) - kills0}");
            xp0 = TotalXp();
            kills0 = KillCredit.XpKills(parts[1]);
            yield return Barrier("bomber-done");

            // 2. it burns to death
            if (s.IsHost) burnt.EffectsHandler.HandleEffect(new DamageEffect { Effect = EffectType.Burn, DamagePerSec = 99999, Duration = 5f, HideUI = true });
            yield return WaitFor(() => burnt.IsDead, 8f);
            Check("the burn killed it on this copy", burnt.IsDead);
            yield return new WaitForSecondsRealtime(1f);
            Check("no XP for a death by burning", TotalXp() == xp0 && KillCredit.XpKills(parts[1]) == kills0, $"XP {xp0} -> {TotalXp()}, kills {KillCredit.XpKills(parts[1]) - kills0}");
            xp0 = TotalXp();
            yield return Barrier("burnt-done");

            // 3. the guest kills one: XP for both, as before
            if (!s.IsHost)
                for (int attempt = 0; attempt < 3 && !slain.IsDead; attempt++)
                {
                    if (me.IsDead || me.CurrentHealthPoints < me.MaxHealthPoints / 2) me.ResetToFullHealth();
                    CombatSystem.Resolve(Hit(me, slain, 99999));
                    yield return WaitFor(() => slain.IsDead, 4f);
                }
            yield return WaitFor(() => slain.IsDead, 8f);
            Check("the guest's kill happened here", slain.IsDead);
            yield return new WaitForSecondsRealtime(1f);
            Check("XP for a player's kill", TotalXp() > xp0, $"XP {xp0} -> {TotalXp()}");
            yield return Barrier("done");
            yield return Finish("only players' kills give XP");
        }

        // ---- helpers ----

        private static bool ColorIs(Enemy e, Color c)
        {
            var x = FirstSpriteColor(e);
            return Mathf.Abs(x.r - c.r) < 0.02f && Mathf.Abs(x.g - c.g) < 0.02f && Mathf.Abs(x.b - c.b) < 0.02f;
        }

        private static Color FirstSpriteColor(Enemy e) =>
            e.fadableRenderers != null && e.fadableRenderers.Length > 0 && e.fadableRenderers[0] != null ? e.fadableRenderers[0].color : Color.white;

        private static string FxState(Enemy e)
        {
            var a = e.extraFXAnimator;
            if (a == null) return "no extra FX animator";
            if (!a.isActiveAndEnabled) return "extra FX animator off";
            return "state " + a.GetCurrentAnimatorStateInfo(0).fullPathHash;
        }

        // What the game wires to an enemy's effects (in the log, for when a game update changes it).
        private static void LogEnemyEffectWiring(Enemy e)
        {
            var h = e != null ? e.EffectsHandler : null;
            if (h == null) { Plugin.Log.LogInfo("[enemyfx] no EffectsHandler on the enemy"); return; }
            string Calls(UnityEventBase ev) => ev == null ? "" : string.Join("; ", Enumerable.Range(0, ev.GetPersistentEventCount())
                .Select(i => (ev.GetPersistentTarget(i)?.name ?? "null") + "." + ev.GetPersistentMethodName(i)));
            Plugin.Log.LogInfo($"[enemyfx] {e.name}: setEffectType [{Calls(h.setEffectType)}], receivers {h.eventsReceiver?.Length ?? 0}, onHitEvent [{Calls(e.onHitEvent)}]");
        }
    }
}
