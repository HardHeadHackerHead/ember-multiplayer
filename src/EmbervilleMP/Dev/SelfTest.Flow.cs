using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // The game's own Main Menu button in a session (--mp-join-later: the guest plays its own world first).
        // Guest: the pause menu's Main Menu asks first, then leaves the session properly: to the main menu (not its own
        // world), the character saved, its own world and the host's never written, nothing shared any more.
        // Host: the guest is gone, the host keeps hosting and playing. (The session ends: no barrier after that.)
        [Scenario("mainmenu-leave", Timeout = 240, Start = "own-world")]
        private static IEnumerator MainMenuLeaveTest()
        {
            yield return WaitInGame();
            if (Args.Host)
            {
                yield return WaitFor(() => Session.Current != null && Session.Current.Players.Count >= 2, 150f);
                yield return Barrier("in-host-world", 150f);
                yield return WaitFor(() => Session.Current == null || Session.Current.Players.Count == 1, 90f);
                var hs = Session.Current;
                Check("the guest left through the game's Main Menu button", hs != null && hs.Players.Count == 1, $"{hs?.Players.Count} player(s)");
                Check("the host keeps hosting and playing", hs != null && hs.IsHost && Game.InGame && Game.SceneName != Game.MainMenuScene, Game.SceneName);
                yield return Finish("the host carried on");
                yield break;
            }

            string ownPrefix = GameData.GetWorldPrefix;
            yield return WaitFor(() => GuestWorld.Active && Game.InGame && !GuestWorld.Switching && GuestWorld.Instance != null && GuestWorld.Instance.SnapshotsLoaded > 0, 150f);
            if (!Check("playing in the host's world", GuestWorld.Active && Game.InGame, $"{Game.SceneName}, own world {ownPrefix}"))
            {
                yield return Finish("");
                yield break;
            }
            // Our own world as it was saved on the way to the host's.
            string ownFile = SaveLoad.GetPath(ownPrefix + ".evw");
            byte[] ownBefore = File.Exists(ownFile) ? File.ReadAllBytes(ownFile) : null;
            string charFile = SaveLoad.GetPath(GameData.playerFilePrefix + ".evp");
            var charBefore = File.GetLastWriteTimeUtc(charFile);
            yield return Barrier("in-host-world", 150f);
            yield return new WaitForSecondsRealtime(1f);

            var ui = Object.FindAnyObjectByType<UIManager>();
            if (!Check("UIManager found", ui != null)) { yield return Finish(""); yield break; }
            ui.Pause();
            yield return new WaitForSecondsRealtime(2f);   // (the game's pause cooldown)
            var button = GameMainMenuButton(ui);
            if (!Check("the game's Main Menu button in the pause menu", button != null)) { yield return Finish(""); yield break; }
            button.onClick.Invoke();
            yield return new WaitForSecondsRealtime(1f);
            Check("it asks first: still in the session and in game", Session.Current != null && GuestWorld.Active && Game.SceneName != Game.MainMenuScene, Game.SceneName);
            int returns = GuestWorld.ReturnsHome;
            Check("confirmation in the pause menu", Click("MP_Leave and go to the main menu"));
            yield return WaitFor(() => Game.SceneName == Game.MainMenuScene && !GuestWorld.Switching &&
                !(Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading), 60f);
            yield return new WaitForSecondsRealtime(1f);
            Check("on the main menu", Game.SceneName == Game.MainMenuScene, Game.SceneName);
            Check("left the session", Session.Current == null && !MPRunner.Instance.Reconnecting);
            Check("no longer in the host's world", !GuestWorld.Active && GuestWorld.ReturnsHome > returns);
            Check("nothing is shared any more", !WorldState.InSharedWorld);
            byte[] ownAfter = File.Exists(ownFile) ? File.ReadAllBytes(ownFile) : null;
            Check("own world untouched", ownBefore != null && ownAfter != null && ownBefore.SequenceEqual(ownAfter), ownFile);
            string saves = Path.GetDirectoryName(SaveLoad.GetPath("x"));
            var guestFiles = Directory.GetFiles(saves).Select(Path.GetFileName).Where(f => f.StartsWith(GuestWorld.GuestPrefix)).ToList();
            Check("no file of the host's world written", guestFiles.Count == 0, string.Join(",", guestFiles));
            Check("character saved on the way", File.GetLastWriteTimeUtc(charFile) > charBefore);
            yield return Finish("left through the game's Main Menu button");
        }

        // The pause menu's Main Menu button: by name, else the button wired to UIManager.CallMainMenu.
        private static UnityEngine.UI.Button GameMainMenuButton(UIManager ui)
        {
            var buttons = ui.panelPauseMenu.GetComponentsInChildren<UnityEngine.UI.Button>(true);
            var b = buttons.FirstOrDefault(x => x.name == "Button_MainMenu");
            if (b != null) return b;
            foreach (var x in buttons)
                for (int i = 0; i < x.onClick.GetPersistentEventCount(); i++)
                    if (x.onClick.GetPersistentMethodName(i) == nameof(UIManager.CallMainMenu)) return x;
            return null;
        }

        // Typing in the chat: none of the game's keys work (movement, Esc, I/J/C/P/K/M/Tab...), and closing it gives
        // each one back the state the game wants it in, whatever the game did meanwhile (dialogue, cutscene, death).
        [Scenario("chatkeys", Timeout = 120)]
        private static IEnumerator ChatKeysTest()
        {
            yield return WaitInGame();
            var input = MainInput.Actions;
            yield return WaitFor(() => Game.Manager.gameState == GameState.Playing && !UIPanels.HasOpenedPanel && input.Player.enabled && input.Menus.enabled, 30f);
            var chat = MPRunner.Instance.ChatBox;
            Check("playing: movement and the menu keys on", input.Player.enabled && input.Menus.enabled, $"state {Game.Manager.gameState}, panels {UIPanels.HasOpenedPanel}");

            chat.Open();
            yield return null;
            yield return null;
            Check("typing: movement, attacks and Esc off", !input.Player.enabled && !input.Player.Pause.enabled);
            Check("typing: the menu keys (inventory, quests, map...) off", !input.Menus.enabled && !input.Menus.ToggleInventory.enabled && !input.Menus.OpenWorldMap.enabled);
            var on = input.asset.actionMaps.Where(m => m.enabled).Select(m => m.name).ToList();
            Check("typing: every map of the game off", on.Count == 0, string.Join(",", on));

            // A dialogue starts while typing (the game turns movement off): closing the chat leaves it off.
            input.Player.Disable();
            chat.Cancel();
            yield return null;
            Check("chat closed during a dialogue: movement stays off", !input.Player.enabled);
            Check("chat closed: the menu keys back", input.Menus.enabled);
            input.Player.Enable();   // the dialogue ends
            yield return null;

            // A cutscene had movement off when the chat opened, and gives it back while we type: on once the chat closes.
            input.Player.Disable();
            chat.Open();
            yield return null;
            int recorded = InputBlock.Recorded;
            input.Player.Enable();
            yield return null;
            Check("typing: the game's 'movement on' waits for the chat", !input.Player.enabled && InputBlock.Recorded > recorded);
            chat.Cancel();
            yield return null;
            Check("chat closed: movement back, as the game wanted", input.Player.enabled);

            // One action the game had off (placement turns Esc off) stays off.
            input.Player.Pause.Disable();
            chat.Open();
            yield return null;
            chat.Cancel();
            yield return null;
            Check("an action the game had off stays off, the rest back", !input.Player.Pause.enabled && input.Player.Move.enabled);
            input.Player.Pause.Enable();

            // The death fade turns the menu keys off while we type: they stay off.
            chat.Open();
            yield return null;
            input.Menus.Disable();
            chat.Cancel();
            yield return null;
            Check("chat closed during the death fade: the menu keys stay off", !input.Menus.enabled && input.Player.enabled);
            input.Menus.Enable();
            yield return null;
            Check("all back to normal", input.Player.enabled && input.Menus.enabled && !InputBlock.Blocking);

            yield return Barrier("done", 60f);
            yield return Finish("the chat holds the game's keys and gives them back as the game wants");
        }

        private static IEnumerator CloseTutorialWindow(TutorialManager tm)
        {
            if (tm == null || tm.panelPopup == null || !tm.panelPopup.activeSelf) yield break;
            Plugin.Log.LogInfo("[test] Closing a tutorial window the game opened");
            yield return new WaitForSecondsRealtime(1.5f);   // (its close button shows after a moment)
            tm.ClosePopupWindow();
            yield return WaitFor(() => !tm.panelPopup.activeSelf, 5f);
        }

        // Tutorials: the host's are its world's (as in the game); a guest in the host's world keeps its own (a record
        // with its character) and never writes the host's list. A window that stops time, opened in a fight with others
        // around, waits until we're out of combat.
        [Scenario("tutorials", Timeout = 220)]
        private static IEnumerator TutorialsTest()
        {
            yield return WaitInGame();
            var s = Session.Current;
            if (!Game.InGame || s == null) { Check("in game, in the session", false); yield return Finish(""); yield break; }
            if (!s.IsHost) yield return WaitFor(() => GuestWorld.Active && !GuestWorld.Switching && Game.InGame, 60f);
            yield return WaitFor(() => Singleton<TutorialManager>.HasMain, 20f);
            var tm = Singleton<TutorialManager>.HasMain ? Singleton<TutorialManager>.main : null;
            bool ok = Check("the scene's TutorialManager", tm != null && tm.dataBase != null);
            if (ok) yield return CloseTutorialWindow(tm);   // (one the game opened by itself: quest window, first hit...)
            var ids = ok ? tm.dataBase.tutorials.Where(t => t != null && !string.IsNullOrEmpty(t.id)).Select(t => t.id).Distinct().ToList() : new List<string>();
            yield return Barrier("ready", 60f);

            if (s.IsHost)
            {
                if (ok)
                {
                    tm.SetAllDone();   // the host has seen every tutorial: its world says so (the game's OnSave)
                    GameData.CallSaveListeners();
                    Check("host: its world keeps its tutorials", ids.Count > 0 && ids.All(GameData.worldData.tutorialsDone.Contains), $"{GameData.worldData.tutorialsDone.Count}/{ids.Count}");
                }
                yield return Barrier("host-seen-all");
                yield return Barrier("guest-checked", 180f);
                if (ok) Check("host: its tutorials untouched by the guest", ids.All(tm.tutorialsDone.Contains));
                yield return Finish("the host's tutorials are its world's");
                yield break;
            }

            yield return Barrier("host-seen-all");
            var unseen = ok ? ids.Where(id => !tm.tutorialsDone.Contains(id)).ToList() : new List<string>();
            if (Check("guest: tutorials it hasn't seen yet", unseen.Count >= 2, $"{unseen.Count} of {ids.Count}"))
            {
                string a = unseen[0], b = unseen[1];
                tm.SetTutorialDone(tm.dataBase.tutorials.First(t => t != null && t.id == a));   // the guest sees one
                // The host's world as a snapshot brings it: every tutorial seen.
                GameData.worldData.tutorialsDone = new List<string>(ids);
                // A scene change: the game saves the list on the way out (OnSave) and loads it on the way in (OnLoad).
                var old = tm;
                GuestWorld.Travel(Game.SceneName, Game.AreaName, Game.LocalPlayer.transform.position, newWorld: false);
                yield return new WaitForSecondsRealtime(1f);
                yield return WaitFor(() => Game.InGame && !(Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading) &&
                    Singleton<TutorialManager>.HasMain && Singleton<TutorialManager>.main != old, 90f);
                yield return new WaitForSecondsRealtime(1f);
                tm = Singleton<TutorialManager>.HasMain ? Singleton<TutorialManager>.main : null;
                if (Check("guest: back after the scene change", tm != null && tm != old && Game.InGame))
                {
                    Check("guest: the host's world list not overwritten by the guest's", ids.All(GameData.worldData.tutorialsDone.Contains), $"{GameData.worldData.tutorialsDone.Count}/{ids.Count}");
                    Check("guest: its own tutorials (the one it saw, not the host's)", tm.tutorialsDone.Contains(a) && !tm.tutorialsDone.Contains(b), $"{tm.tutorialsDone.Count} seen");
                    var lines = File.Exists(Tutorials.FilePath) ? File.ReadAllLines(Tutorials.FilePath) : new string[0];
                    Check("guest: its record saved with the character", lines.Contains(a) && !lines.Contains(b), Tutorials.FilePath);
                }
            }

            // A window that stops time, opened in a fight with the host in the scene: it waits until we're out of it.
            tm = Singleton<TutorialManager>.HasMain ? Singleton<TutorialManager>.main : null;
            var window = tm?.dataBase?.tutorials.OfType<TutorialDataWindow>().Where(t => t.stopTime)
                .OrderBy(t => t.OnCloseEvents?.events?.Count ?? 0).FirstOrDefault();
            if (tm != null) yield return CloseTutorialWindow(tm);
            yield return WaitFor(() => SceneAuthority.OthersInMyScene() && Game.Manager.gameState == GameState.Playing && !UIPanels.HasOpenedPanel, 30f);
            if (Check("a tutorial window that stops time", window != null) & Check("the host is in the scene", SceneAuthority.OthersInMyScene()))
            {
                var me = Game.LocalPlayer;
                me.currentHealth -= 1;   // just hurt: in a fight (TeamTeleport's rule; real damage opens the first-hit tutorial)
                yield return null;
                yield return null;
                int deferred = Tutorials.Deferred;
                tm.ShowModularTutorial(window);
                yield return new WaitForSecondsRealtime(0.5f);
                Check("in a fight with others around: the window waits", !tm.panelPopup.activeSelf && Tutorials.WaitingCount == 1 && Tutorials.Deferred == deferred + 1,
                    $"open {tm.panelPopup.activeSelf}, waiting {Tutorials.WaitingCount}");
                Check("the menus stay usable meanwhile", !UIPanels.isLocked);
                me.ResetToFullHealth();
                yield return WaitFor(() => tm.panelPopup.activeSelf, TeamTeleport.HurtSeconds + 10f);
                Check("out of the fight: the window opens", tm.panelPopup.activeSelf && Tutorials.WaitingCount == 0);
                yield return new WaitForSecondsRealtime(1.5f);   // (its close button shows after a moment)
                tm.ClosePopupWindow();
                yield return WaitFor(() => !tm.panelPopup.activeSelf, 5f);
                Check("closed again", !tm.panelPopup.activeSelf && !UIPanels.isLocked);
            }
            yield return Barrier("guest-checked", 60f);
            yield return Finish("the guest's tutorials are its own");
        }
    }
}
