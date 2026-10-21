using System.Collections;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Screenshots for the README (run windowed with --mp-clean): two players together, chatting, fighting,
        // the multiplayer pause menu with both players, and the boss arena.
        [Scenario("showcase")]
        private static IEnumerator ShowcaseTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            var me = Game.LocalPlayer;
            string tag = s.IsHost ? "host" : "guest";
            // Fresh test characters get the game's tutorial windows: mark them all seen (test profiles only).
            System.Action noTutorials = () =>
            {
                if (!Singleton<TutorialManager>.HasMain) return;
                Singleton<TutorialManager>.main.SetAllDone();
                Singleton<TutorialManager>.main.ClosePopupWindow();
            };
            noTutorials();

            // 1. Together at the lit entrance of the mushroom cavern, saying hello.
            yield return TeleportTo(CombatArea, s.IsHost ? new Vector2(56f, 77f) : new Vector2(57.6f, 77.4f));
            yield return Barrier("arrived", 40f);
            yield return new WaitForSecondsRealtime(1f);
            noTutorials();
            yield return new WaitForSecondsRealtime(1.5f);
            if (!s.IsHost) Chat.Send("Ready when you are!");
            yield return new WaitForSecondsRealtime(1f);
            if (s.IsHost) Chat.Send("Let's clear this cave.");
            yield return new WaitForSecondsRealtime(1.5f);
            noTutorials();
            yield return DevHarness.Screenshot("together-" + tag);
            yield return Barrier("shot1");

            // 2. Into the mushroom cavern, next to its first enemies; both chip at them.
            yield return TeleportTo(CombatArea, s.IsHost ? new Vector2(62.5f, 77.2f) : new Vector2(64.2f, 78f));
            yield return Barrier("in-cave", 40f);
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 5f)
            {
                var e = NearestAlive(CombatArea, me.transform.position);
                if (e != null && (e.transform.position - me.transform.position).sqrMagnitude < 16f) CombatSystem.Resolve(Hit(me, e, 3));
                if (me.CurrentHealthPoints < me.MaxHealthPoints / 2) me.ResetToFullHealth();
                yield return new WaitForSecondsRealtime(0.7f);
            }
            noTutorials();
            yield return new WaitForSecondsRealtime(0.3f);
            yield return DevHarness.Screenshot("combat-" + tag);
            yield return Barrier("shot2");
            me.ResetToFullHealth();

            // 3. The multiplayer pause menu, with both players listed.
            if (s.IsHost)
            {
                var ui = Object.FindAnyObjectByType<UIManager>();
                ui.Pause();
                yield return new WaitForSecondsRealtime(1.5f);
                Click("Button_Multiplayer");
                yield return new WaitForSecondsRealtime(1.5f);
                yield return DevHarness.Screenshot("pause-players");
                Click("MP_Back");
                yield return new WaitForSecondsRealtime(0.5f);
                ui.Resume();
                yield return new WaitForSecondsRealtime(1f);
            }
            yield return Barrier("shot3");

            // 4. The Spider Matriarch's arena.
            yield return TeleportTo("SpiderMatriarch_Area", s.IsHost ? new Vector2(410f, 262f) : new Vector2(412f, 262f));
            yield return Barrier("boss-arrived", 40f);
            var cine = Object.FindAnyObjectByType<MatriarchBossCinematic>();
            if (cine != null)
            {
                if (!s.IsHost) yield return new WaitForSecondsRealtime(1f);
                var trigger = cine.GetComponent<Collider2D>();
                yield return MoveTo(me, trigger != null ? (Vector2)trigger.bounds.center : (Vector2)cine.transform.position, 1f);
                Enemy boss = null;
                yield return WaitFor(() => (boss = EnemiesIn("SpiderMatriarch_Area").FirstOrDefault(x => x.name.Contains("Boss") && !x.IsDead)) != null, 25f);
                yield return new WaitForSecondsRealtime(6f);
                for (int i = 0; i < 4 && boss != null; i++)
                {
                    if (boss.isActiveAndEnabled && boss.HealthPercent > 0.6f) CombatSystem.Resolve(Hit(me, boss, 25));
                    if (me.CurrentHealthPoints < me.MaxHealthPoints / 2) me.ResetToFullHealth();
                    yield return new WaitForSecondsRealtime(1f);
                }
                noTutorials();
                yield return new WaitForSecondsRealtime(0.3f);
                yield return DevHarness.Screenshot("boss-" + tag);
            }
            yield return Barrier("done", 60f);
            yield return Finish("showcase");
        }
    }
}
