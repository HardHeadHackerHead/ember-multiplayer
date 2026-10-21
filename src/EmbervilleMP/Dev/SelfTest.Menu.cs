using System.Collections;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        private static bool Click(string name)
        {
            var go = GameObject.Find(name);
            var b = go != null ? go.GetComponent<UnityEngine.UI.Button>() : null;
            if (b == null || !b.interactable) return false;
            b.onClick.Invoke();
            return true;
        }

        private static IEnumerator ClickWhenThere(string name, float timeout = 20f)
        {
            bool ok = false;
            yield return WaitFor(() => ok = Click(name), timeout);
            Check($"clicked {name}", ok);
            yield return new WaitForSecondsRealtime(1.2f);
        }

        // The real menus, both sides: host via Multiplayer > Host > LAN + the game's character/world screens;
        // guest via Multiplayer > Join + character screen, then the host from the games list.
        [Scenario("menuflow", Timeout = 240, Start = "menus")]
        private static IEnumerator MenuFlowTest()
        {
            HookPeerData();
            bool host = Args.Name == "Hosty";
            yield return WaitFor(() => Game.SceneName == "MainMenu" && System.IO.File.Exists(SaveLoad.GetPath(DevHarness.WorldFile + ".evw")), 60f);
            yield return new WaitForSecondsRealtime(2f);
            var mm = Object.FindAnyObjectByType<MainMenuManager>();
            // The welcome panel comes first; the menu buttons appear once it's closed.
            foreach (var c in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                if (c.name == "WelcomePanel")
                    foreach (var b in c.GetComponentsInChildren<UnityEngine.UI.Button>()) b.onClick.Invoke();
            yield return new WaitForSecondsRealtime(2f);
            yield return ClickWhenThere("Button_Multiplayer");
            if (host)
            {
                yield return ClickWhenThere("MP_Host a game");
                yield return ClickWhenThere("MP_LAN / direct IP");
                yield return WaitFor(() => mm.state == MainMenuState.CharacterSelection && !mm.isBusy, 20f);
                mm.LoadPlayer(DevHarness.PlayerFile);
                yield return WaitFor(() => mm.state == MainMenuState.WorldSelection && !mm.isBusy, 20f);
                mm.LoadWorldSave(DevHarness.WorldFile);
                yield return WaitFor(() => Game.InGame && Session.Current != null, 90f);
                Check("hosting started once the world loaded", Session.Current != null && Session.Current.IsHost);
                yield return WaitFor(() => Session.Current.Players.Count >= 2, 120f);
                Check("a guest joined through the menu", Session.Current.Players.Count >= 2);
            }
            else
            {
                yield return ClickWhenThere("MP_Join a game");
                yield return WaitFor(() => mm.state == MainMenuState.CharacterSelection && !mm.isBusy, 20f);
                mm.LoadPlayer(DevHarness.PlayerFile);
                yield return ClickWhenThere("MP_Hosty's game (LAN)", 90f);
                yield return DevHarness.Screenshot("menuflow-joining");
                yield return WaitFor(() => GuestWorld.Active && Game.InGame && !GuestWorld.Switching, 120f);
                Check("joined into the host's world from the menu", GuestWorld.Active && Game.InGame, $"{Game.SceneName}");
            }
            yield return Barrier("done", 60f);
            yield return Finish("menu flow works");
        }
    }
}
