using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using HarmonyLib;

namespace EmbervilleMP.Patches
{
    // A death normally rewinds the room: the player goes back to where they came in, and every enemy, boss
    // encounter, trap and door returns to how it was. With other players in the scene that would undo
    // their fight, so in co-op only the player who died is put back (same fade and death screen), and back into
    // the Matriarch's fight if it goes on. A party wipe (everyone in the scene down, the runner's call:
    // Sync/PartyWipe) rewinds the scene the game's way on every copy, as its own player gets back up.
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ResetScene))]
    internal static class CoopResetScenePatch
    {
        public static bool OthersInScene() => Sync.SceneAuthority.OthersInMyScene();

        private static bool Prefix(GameManager __instance)
        {
            // (someone else lying dead here counts, also if their state hasn't said so: PartyWipe's death notices)
            if (!OthersInScene() && !Sync.PartyWipe.Coop)
            {
                if (Sync.PartyWipe.TakePending()) Plugin.Log.LogInfo("[death] Party wipe: the game's own reset rewinds the scene (we're alone here)");
                return true;
            }
            Plugin.Log.LogInfo("[death] Co-op death: respawning without rewinding the room");
            var gm = __instance;
            Sync.PartyWipe.RespawnStarted();
            MainInput.Actions.Player.Disable();
            MainInput.Actions.Menus.Disable();
            gm.gameState = GameState.Gameover;
            gm.gameStarted = false;
            int serial = ++respawns;
            MPRunner.Instance?.StartCoroutine(KeepDeathScreen(serial));
            Bus<FadeEvent>.Invoke(new FadeEvent(FadeType.FadeOut, 1.7f, async delegate
            {
                respawned = serial;
                try
                {
                    var player = gm.player;
                    player.ResetToLastState();
                    player.ResetToFullHealth();
                    if (GameManager.playerHasDied) player.ShowPlayer(value: false, includeExtraVFX: false);
                    // (the runner's word may come any time while we're on the death screen)
                    bool wipe = Sync.PartyWipe.TakePending();
                    if (wipe) Plugin.Log.LogInfo("[death] Party wipe: our respawn rewinds the scene");
                    if (!wipe) Sync.BossArena.PullIn(player);
                    Bus<CamEvent>.Invoke(new CamEvent { CamEventType = CamEventType.SetPosition, Position = player.myTransform.position });
                    await UniTask.DelayFrame(2);
                    if (wipe) Sync.PartyWipe.Rewind(gm);
                }
                // (whatever goes wrong, the respawn goes on: without the fade in we'd never be back in the game)
                catch (System.Exception e) { Plugin.Log.LogError("[death] Co-op respawn: " + e); }
                finally { Sync.PartyWipe.RespawnDone(); }
                Bus<FadeEvent>.Invoke(new FadeEvent(FadeType.FadeIn, 0.8f, gm.RestartGameplay));
            }, showDeadScreen: true));
            return false;
        }

        private static int respawns, respawned;
        public static int DeathScreensRestored;

        // Fades run side by side and each one hides the death screen as it starts: a fade that began before the death
        // (a teleport's, through a door, the fade back in after it) hid ours while it waited for Continue, and the
        // respawn never came (a black screen without the button). While our death screen waits for its click, it stays.
        private static System.Collections.IEnumerator KeepDeathScreen(int serial)
        {
            var fader = UnityEngine.Object.FindAnyObjectByType<FaderManager>();
            float until = UnityEngine.Time.realtimeSinceStartup + 600f;
            while (fader != null && respawned != serial && UnityEngine.Time.realtimeSinceStartup < until)
            {
                if (fader.deadGroup != null && fader.deadGroup.interactable && fader.deadObject != null && !fader.deadObject.activeInHierarchy)
                {
                    fader.mainGroup.gameObject.SetActive(true);
                    fader.mainGroup.alpha = 1f;
                    fader.deadObject.SetActive(true);
                    fader.deadGroup.alpha = 1f;
                    if (DeathScreensRestored++ < 5) Plugin.Log.LogInfo("[death] Another fade hid our death screen: shown again");
                }
                yield return null;
            }
        }
    }

    // A dead character stays dead until its respawn puts it back (ResetToLastState). Anything else changing its state
    // meanwhile cancels the death halfway: no death screen, no respawn, back up where it fell with no health. Seen in
    // co-op, where the game runs on around a dead player: the game's RemoveStaticStack goes to idle whenever the stack
    // is empty, also for an owner it never had (a graph's SetPlayerStatic, a door's or a dialogue's end, a panel
    // closing). The player's entry points into "held / released / stunned" are guarded here (not the shared generic
    // StateMachine<T>.ChangeState: patching it for PlayerMaster and for Enemy would be one native method twice).
    internal static class DeadStaysDead
    {
        public static int Blocked, Escapes;
        public static bool Respawning;   // (DeadRespawnPatch: the respawn's own way back up)

        public static bool Keep(PlayerMaster p) =>
            !Respawning && p != null && Net.Session.Current != null && p == Game.LocalPlayer && p.IsDead;

        public static void Note(string what)
        {
            if (Blocked++ < 5) Plugin.Log.LogInfo($"[death] Keeping our dead character down: {what} asked by {Callers()}");
        }

        public static string Callers()
        {
            var frames = new System.Diagnostics.StackTrace(2, false).GetFrames() ?? new System.Diagnostics.StackFrame[0];
            return string.Join(" < ", frames.Take(8).Select(f => f.GetMethod()).Where(m => m != null).Select(m => m.DeclaringType?.Name + "." + m.Name));
        }
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.AddStaticStack))]
    internal static class DeadAddStaticPatch
    {
        // The owner is still noted (its RemoveStaticStack comes later), the state stays.
        private static bool Prefix(PlayerMaster __instance, object owner)
        {
            if (!DeadStaysDead.Keep(__instance)) return true;
            __instance.staticOwners.Add(owner);
            DeadStaysDead.Note("hold (" + owner + ")");
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.RemoveStaticStack))]
    internal static class DeadRemoveStaticPatch
    {
        private static bool Prefix(PlayerMaster __instance, object owner)
        {
            if (!DeadStaysDead.Keep(__instance)) return true;
            __instance.staticOwners.Remove(owner);
            DeadStaysDead.Note("release (" + owner + ")");
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.Static), MethodType.Setter)]
    internal static class DeadStaticSetterPatch
    {
        private static bool Prefix(PlayerMaster __instance, bool value)
        {
            if (!DeadStaysDead.Keep(__instance)) return true;
            DeadStaysDead.Note("Static = " + value);
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.Stun))]
    internal static class DeadStunPatch
    {
        private static bool Prefix(PlayerMaster __instance)
        {
            if (!DeadStaysDead.Keep(__instance)) return true;
            DeadStaysDead.Note("stun");
            return false;
        }
    }

    // Whatever still gets our dead character out of its death another way: say who, so it can be guarded too.
    [HarmonyPatch(typeof(PlayerDead), nameof(PlayerDead.ExitState))]
    internal static class DeadExitWatchPatch
    {
        private static void Prefix(PlayerDead __instance, State<PlayerMaster> nextState)
        {
            if (DeadStaysDead.Respawning || Net.Session.Current == null || __instance.owner != Game.LocalPlayer) return;
            if (DeadStaysDead.Escapes++ < 5)
                Plugin.Log.LogWarning($"[death] Our dead character left its death without a respawn (to {nextState?.GetType().Name}): {DeadStaysDead.Callers()}");
        }
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.ResetToLastState))]
    internal static class DeadRespawnPatch
    {
        private static void Prefix() => DeadStaysDead.Respawning = true;
        private static void Finalizer() => DeadStaysDead.Respawning = false;
    }
}
