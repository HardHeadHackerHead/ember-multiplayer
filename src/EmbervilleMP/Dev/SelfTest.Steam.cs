using System.Collections;
using EmbervilleMP.Steam;
using Steamworks;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // The main menu as it looks (for the multiplayer banner/button): screenshot + UI dump.
        [Scenario("menu")]
        private static IEnumerator MenuShot()
        {
            while (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "MainMenu") yield return null;
            yield return new WaitForSecondsRealtime(4f);
            yield return DevHarness.Screenshot("menu-welcome");
            foreach (var c in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                if (c.name == "WelcomePanel")
                    foreach (var b in c.GetComponentsInChildren<UnityEngine.UI.Button>())
                    {
                        Plugin.Log.LogInfo($"[dev] Pressing {b.name} on the welcome panel");
                        b.onClick.Invoke();
                    }
            yield return new WaitForSecondsRealtime(3f);
            yield return DevHarness.Screenshot("menu");
            // Our screens
            System.Func<string, bool> click = name =>
            {
                var go = GameObject.Find(name);
                var b = go != null ? go.GetComponent<UnityEngine.UI.Button>() : null;
                if (b == null) { Plugin.Log.LogWarning($"[dev] No button {name}"); return false; }
                b.onClick.Invoke();
                return true;
            };
            Check("MULTIPLAYER button on the main menu", click("Button_Multiplayer"));
            yield return new WaitForSecondsRealtime(2f);
            yield return DevHarness.Screenshot("menu-mp");
            Check("Host page", click("MP_Host a game"));
            yield return new WaitForSecondsRealtime(1.5f);
            yield return DevHarness.Screenshot("menu-host");
            var mm = Object.FindAnyObjectByType<MainMenuManager>();
            if (mm != null)
            {
                var canvas = mm.GetComponentInParent<Canvas>(true);
                DevHarness.DumpHierarchy((canvas != null ? canvas.rootCanvas.gameObject : mm.gameObject), "menu-hierarchy.txt");
            }
            foreach (var c in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                if (c.isRootCanvas) Plugin.Log.LogInfo($"[dev] canvas {c.name} mode={c.renderMode} sort={c.sortingOrder}");
            yield return Finish("menu captured");
        }

        // Is Steam usable from inside the game? Init, relay network, a friends-only lobby.
        [Scenario("steamcheck")]
        private static IEnumerator SteamCheck()
        {
            yield return new WaitForSecondsRealtime(1f);
            if (!Check("Steam API starts inside the game", SteamBootstrap.Init(), SteamBootstrap.Error)) { yield return Finish(""); yield break; }
            Check("running as this game's Steam app", SteamUtils.GetAppID().m_AppId == SteamBootstrap.AppId, $"{SteamUtils.GetAppID()} (expected {SteamBootstrap.AppId})");
            Check("logged in to Steam", SteamUser.BLoggedOn());

            SteamNetworkingUtils.InitRelayNetworkAccess();
            float t0 = Time.realtimeSinceStartup;
            ESteamNetworkingAvailability relay = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Unknown;
            while (Time.realtimeSinceStartup - t0 < 20f)
            {
                SteamBootstrap.RunCallbacks();
                relay = SteamNetworkingUtils.GetRelayNetworkStatus(out _);
                if (relay == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current) break;
                yield return null;
            }
            Check("Steam relay network reachable (play over the internet without port forwarding)", relay == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current, relay.ToString());

            CSteamID lobby = CSteamID.Nil;
            bool done = false;
            var created = CallResult<LobbyCreated_t>.Create((r, failed) =>
            {
                done = true;
                if (!failed && r.m_eResult == EResult.k_EResultOK) lobby = new CSteamID(r.m_ulSteamIDLobby);
                Plugin.Log.LogInfo($"[steam] LobbyCreated {r.m_eResult} failed={failed}");
            });
            created.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 4));
            t0 = Time.realtimeSinceStartup;
            while (!done && Time.realtimeSinceStartup - t0 < 15f)
            {
                SteamBootstrap.RunCallbacks();
                yield return null;
            }
            Check("a friends-only Steam lobby can be created", lobby.IsValid() && lobby.IsLobby(), lobby.ToString());
            if (lobby.IsValid())
            {
                SteamMatchmaking.SetLobbyData(lobby, "emberville_mp", Net.Protocol.Version.ToString());
                Check("lobby data can be set", SteamMatchmaking.GetLobbyData(lobby, "emberville_mp") == Net.Protocol.Version.ToString());
                SteamMatchmaking.LeaveLobby(lobby);
            }
            SteamBootstrap.Shutdown();
            yield return Finish("Steam works inside Emberville");
        }
    }
}
