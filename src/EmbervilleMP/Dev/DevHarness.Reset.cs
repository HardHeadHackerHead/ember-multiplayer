using System.Collections;
using System.Linq;
using Cysharp.Threading.Tasks;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    // Chained tests: what one scenario leaves behind must not reach the next. The scene reload (FreshWorld) takes the
    // world, its enemies and rooms with it, and the syncs are rebuilt on the session; this is the rest: the player
    // object (it outlives the reload), the game's UI and transitions in flight, and the mod's state that is static.
    internal static partial class DevHarness
    {
        // Tests: set while the harness itself moves the game (the boot's first load, a fresh world, the way back to the
        // chain's start): its fades take ~0.1 s instead of up to 1 s (FastDeathFadePatch). A scene a scenario loads is
        // the game's own and keeps its fades. --mp-slow-loads turns it off.
        public static bool QuickTransition;
        private static bool bootLoad;   // (QuickTransition until our own world's first load is in game)

        public static void QuickLoadBegins() { if (QuickOn) { QuickTransition = true; bootLoad = true; } }
        private static bool QuickOn => Args.Test != null && !Args.Has("slow-loads");

        // Tests: frames aren't capped while a scene loads or the harness moves the game (the --mp-fps cap is for
        // playing): the loader integrates a slice per frame, and the fades step at most 3% a frame.
        public static void ApplyLoadingFps()
        {
            if (Args.Test == null || Args.Fps <= 0) return;
            bool loading = QuickTransition || (Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading) || GuestWorld.Switching;
            if (bootLoad && Game.InGame) { bootLoad = false; QuickTransition = false; }
            int want = loading ? 300 : Args.Fps;
            if (Application.targetFrameRate != want) Application.targetFrameRate = want;
        }

        // Before the reload: whatever is still going ends its own way first (a teleport's callback or a respawn landing
        // in the new world breaks it), and nothing is left open (a chest open on this copy holds the stash for everyone).
        private static IEnumerator SettleBeforeFreshWorld()
        {
            var me = Game.LocalPlayer;
            if (me == null) yield break;
            float until = Time.realtimeSinceStartup + 20f;
            while (me != null && me.IsDead && Time.realtimeSinceStartup < until) yield return null;   // the respawn comes
            until = Time.realtimeSinceStartup + 6f;
            while (me != null && Game.Manager != null && me.staticOwners.Contains(Game.Manager) && Time.realtimeSinceStartup < until) yield return null;
            if (me == null) yield break;
            foreach (var c in Object.FindObjectsByType<Chest>(FindObjectsSortMode.None).Where(c => c != null && c.isOpened))
            {
                try { c.OnInteract(me.gameObject); Plugin.Log.LogInfo($"[dev] Closed chest {c.name} left open"); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"[dev] closing {c.name}: {e.Message}"); }
            }
            try
            {
                if (Singleton<DialogueManager>.HasMain && Singleton<DialogueManager>.main.isPlaying) Singleton<DialogueManager>.main.EndDialogue();
                if (Singleton<TutorialManager>.HasMain && Singleton<TutorialManager>.main.panelPopup != null && Singleton<TutorialManager>.main.panelPopup.activeInHierarchy)
                    Singleton<TutorialManager>.main.ClosePopupWindow();
                if (UIPanels.HasOpenedPanel) Object.FindAnyObjectByType<UIManager>()?.panelsManager?.CloseAll();
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[dev] closing the UI: " + e.Message); }
            yield return null;
        }

        // After the reload and the syncs' rebuild: the player as a new game's, the mod's static state as a new session's.
        private static void ResetAfterFreshWorld()
        {
            var me = Game.LocalPlayer;
            if (me != null)
            {
                // locks a cutscene, the demo's end or a cut-off transition left (the player object outlived them)
                foreach (var owner in me.staticOwners.ToList()) me.RemoveStaticStack(owner);
                try { me.EffectsHandler?.StopAllEffects(); } catch (System.Exception e) { Plugin.Log.LogWarning("[dev] effects: " + e.Message); }
                me.Invincible = false;   // (a scenario that made it invincible may have ended early)
                if (!me.IsDead) me.ResetToFullHealth();
            }
            Time.timeScale = Args.TimeScale;
            SkillHandler.CanUseSkill = true;
            NetStats.Trim = true;        // (netstats turns it off for a while)
            Chat.Lines.Clear();          // scenarios read the chat ("/tp", ping notices)
            Deaths.ForgetWorld();        // its bosses can fall again, announcements start over
            DeathBanner.LastTitle = DeathBanner.LastSubtitle = "";
            EnemyAim.Forget();
            HoldDialogue = false;
        }

        // The player object outlives the reload and keeps the last scenario's spot: back to where this copy started
        // the chain, as a new game would.
        private static IEnumerator BackToChainStart()
        {
            var me = Game.LocalPlayer;
            if (me == null || firstSpawnArea == null || Game.AreaName == firstSpawnArea) yield break;
            var area = Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(x => x.name == firstSpawnArea);
            if (area == null) yield break;
            Game.Manager.TeleportPlayerFade(firstSpawnPos, Direction.Down, area).Forget();
            float t = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < t && !(Game.AreaName == firstSpawnArea && Game.Manager.gameState == GameState.Playing)) yield return null;
            Plugin.Log.LogInfo($"[dev] Back at the chain's start: {Game.AreaName}");
        }

        // A scenario that says it doesn't need a fresh world ([Scenario(Fresh = false)]) goes on in the last one's: no
        // reload, the syncs keep running; only the player and the harness's state start over, back at the chain's start.
        // (In another scene than its own, or without everyone: a fresh world after all.)
        public static bool CanCarryOn(string scene) =>
            Game.InGame && Game.SceneName == scene && !GuestWorld.Switching && Session.Current != null && Session.Current.Ready;

        public static IEnumerator CarryOn()
        {
            yield return SettleBeforeFreshWorld();
            QuickTransition = QuickOn;
            ResetAfterFreshWorld();
            yield return BackToChainStart();
            var me = Game.LocalPlayer;
            if (me != null && !me.IsDead) me.ResetToFullHealth();
            QuickTransition = false;
            Plugin.Log.LogInfo($"[dev] Carrying on in the same world at {Time.realtimeSinceStartup:F1}s");
        }
    }
}
