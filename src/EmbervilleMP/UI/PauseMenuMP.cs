using System;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Steam;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleMP
{
    // MULTIPLAYER in the in-game pause menu: host (Steam friends / invite only / public / LAN), invite friends,
    // see who's there (host: kick), leave or stop hosting. Built from the pause menu's own buttons.
    // The game's own Main Menu button asks first while in a session (a page here): going to the menu leaves it.
    internal class PauseMenuMP
    {
        private enum Page { Main, Invite, Kick, MainMenu }

        // In a session, or trying to get back into one: the game's Main Menu button must leave it first.
        public static bool InSession => Session.Current != null || (MPRunner.Instance != null && MPRunner.Instance.Reconnecting);

        private UIManager ui;
        private int builtForScene = -1;
        private Transform pauseButtons, holder;
        private GameObject template;
        private TextMeshProUGUI title;
        private bool open;
        private Page page;
        private int hash;

        public void Update()
        {
            if (!Game.InGame) { builtForScene = -1; return; }
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (builtForScene != scene)
            {
                ui = UnityEngine.Object.FindAnyObjectByType<UIManager>();
                if (ui == null || ui.panelPauseMenu == null) return;
                try { Build(); }
                catch (Exception e) { Plugin.Log.LogError("[menu] Couldn't add multiplayer to the pause menu: " + e); }
                builtForScene = scene;
            }
            if (ui == null || holder == null) return;
            // The pause menu closed (Resume / Esc) while ours was showing: next time it opens on the game's buttons.
            if (open && !ui.panelPauseMenu.activeInHierarchy) Close();
            if (open)
            {
                int h = StateHash();
                if (h != hash) { hash = h; Rebuild(); }
            }
        }

        private void Build()
        {
            var options = Game.FindUi(ui.panelPauseMenu.transform, "Content/ButtonHolder/Button_Options");
            pauseButtons = options?.parent;
            if (options == null) throw new Exception("pause buttons not found");
            var mp = Clone(options.gameObject, pauseButtons);
            mp.name = "Button_Multiplayer";
            mp.transform.SetSiblingIndex(options.GetSiblingIndex() + 1);
            SetLabel(mp, "MULTIPLAYER");
            Rewire(mp, Open);

            holder = Clone(pauseButtons.gameObject, pauseButtons.parent, active: false).transform;
            holder.name = "MP_ButtonHolder";
            template = holder.Find("Button_Options").gameObject;
            foreach (Transform c in holder) if (c.gameObject != template) UnityEngine.Object.Destroy(c.gameObject);
            template.SetActive(false);
            // A title line: one of the buttons' labels, on its own.
            var label = Clone(template.GetComponentInChildren<TextMeshProUGUI>(true).gameObject, holder);
            label.name = "MP_Title";
            foreach (var c in label.GetComponentsInChildren<Image>(true)) UnityEngine.Object.Destroy(c.gameObject == label ? (UnityEngine.Object)c : c.gameObject);
            title = label.GetComponent<TextMeshProUGUI>();
            title.color = new Color(0.36f, 0.92f, 0.82f);
            label.transform.SetAsFirstSibling();
        }

        private void Open()
        {
            open = true;
            page = Page.Main;
            pauseButtons.gameObject.SetActive(false);
            holder.gameObject.SetActive(true);
            hash = 0;
        }

        // The game's Main Menu button, in a session: our page asks to confirm (false: the pause menu isn't up).
        public bool AskMainMenu(UIManager from)
        {
            if (from == null || from != ui || holder == null || !ui.panelPauseMenu.activeInHierarchy) return false;
            Open();
            page = Page.MainMenu;
            return true;
        }

        private void Close()
        {
            open = false;
            if (holder != null) holder.gameObject.SetActive(false);
            if (pauseButtons != null) pauseButtons.gameObject.SetActive(true);
            var resume = pauseButtons?.Find("Button_Resume");
            if (resume != null && ui.panelPauseMenu.activeInHierarchy)
                UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(resume.gameObject);
        }

        private int StateHash()
        {
            var s = Session.Current;
            unchecked
            {
                int h = (int)page * 7919 + (s == null ? 1 : s.Players.Count * 31 + (s.Ready ? 2 : 3) + s.Status.GetHashCode())
                    + (MPRunner.Instance.Reconnecting ? 5 : 0) + (int)Steam.Voice.Mode * 101 + (int)Plugin.Settings.LootMode.Value * 307
                    + (Plugin.Settings.AllowTeleport.Value ? 401 : 0) + (Sync.TeamTeleport.Allowed ? 809 : 0)
                    + Sync.EstateRules.SettingsHash * 613;
                if (page == Page.Invite && SteamBootstrap.Ready) h += SteamLobby.OnlineFriends().Count * 13;
                return h;
            }
        }

        private void Rebuild()
        {
            foreach (Transform c in holder)
                if (c.gameObject != template && c != title.transform) UnityEngine.Object.Destroy(c.gameObject);
            var s = Session.Current;
            var lobby = SteamLobby.Instance;
            switch (page)
            {
                case Page.Main when MPRunner.Instance.Reconnecting && (s == null || !s.Ready):
                    title.text = "Connection lost: reconnecting...";
                    Add("Stop and leave", () => MPRunner.Instance.CancelReconnect());
                    break;
                case Page.Main when s == null:
                    title.text = "Multiplayer";
                    if (lobby != null)
                    {
                        Add("Host: Steam friends", () => MPRunner.Instance.HostSteam(LobbyKind.FriendsOnly));
                        Add("Host: invite only", () => MPRunner.Instance.HostSteam(LobbyKind.InviteOnly));
                        Add("Host: public", () => MPRunner.Instance.HostSteam(LobbyKind.Public));
                    }
                    Add("Host: LAN / IP", () => MPRunner.Instance.StartHost(Plugin.Settings.Port.Value, MPWindow.DefaultName()));
                    Add(Sync.LootRules.SettingLabel, Sync.LootRules.CycleSetting);
                    Add(Sync.TeamTeleport.SettingLabel, Sync.TeamTeleport.CycleSetting);
                    Add(Sync.EstateRules.EstateLabel, Sync.EstateRules.CycleEstate);
                    Add(Sync.EstateRules.DemoEndLabel, Sync.EstateRules.CycleDemoEnd);
                    break;
                case Page.Main:
                    title.text = s.IsHost ? $"Hosting · {s.Players.Count} player(s)" : $"In {HostName(s)}'s game";
                    foreach (var p in s.Players.Values.OrderBy(p => p.Id))
                    {
                        byte id = p.Id;
                        // Teammates' lines teleport you to them (when the host allows it).
                        if (id != s.LocalId && Sync.TeamTeleport.Allowed && Sync.TeamTeleport.Instance != null)
                            Add($"Teleport to {p.Name}  ({p.Ping} ms)", () => TeleportTo(id));
                        else Add($"{p.Name}{(id == s.LocalId ? " (you)" : $"  {p.Ping} ms")}", null);
                    }
                    if (lobby != null && lobby.InLobby) Add("Invite friends", () => { page = Page.Invite; hash = 0; });
                    Add("Voice: " + Steam.Voice.ModeLabel, Steam.Voice.CycleMode);
                    if (s.IsHost) Add("Loot: " + (Sync.LootRules.Mode == Sync.LootMode.Shared ? "shared" : "each player their own") + " (set before hosting)", null);
                    if (s.IsHost) Add(Sync.TeamTeleport.SettingLabel, Sync.TeamTeleport.CycleSetting);
                    if (s.IsHost)
                    {
                        Add(Sync.EstateRules.EstateLabel, Sync.EstateRules.CycleEstate);
                        Add(Sync.EstateRules.DemoEndLabel, Sync.EstateRules.CycleDemoEnd);
                    }
                    else Add(Sync.EstateRules.GuestLabel, null);
                    if (s.IsHost && s.Players.Count > 1) Add("Remove a player", () => { page = Page.Kick; hash = 0; });
                    Add(s.IsHost ? "Stop hosting" : "Leave game", () => s.Stop());
                    break;
                case Page.Invite:
                    title.text = "Invite friends";
                    Add("Steam overlay", () => lobby?.InviteOverlay());
                    if (SteamBootstrap.Ready)
                        foreach (var f in SteamLobby.OnlineFriends().Take(8))
                        {
                            var id = f.id;
                            Add($"{f.name}{(f.inGame ? " (in Emberville)" : "")}", () => lobby?.Invite(id));
                        }
                    break;
                case Page.MainMenu:
                    title.text = s == null && !MPRunner.Instance.Reconnecting ? "Main menu"
                        : s != null && s.IsHost ? (s.Players.Count > 1 ? "Stop hosting? Everyone goes home" : "Stop hosting?")
                        : "Leave the game?";
                    var from = ui;
                    Add(s != null && s.IsHost ? "Stop hosting and go to the main menu" : "Leave and go to the main menu", () =>
                    {
                        Close();
                        MPRunner.Instance.LeaveToMainMenu(() => from.CallScene(Game.MainMenuScene));
                    });
                    break;
                case Page.Kick:
                    title.text = "Remove a player";
                    if (s != null)
                        foreach (var p in s.Others)
                        {
                            byte id = p.Id;
                            Add(p.Name, () => { s.Kick(id, "removed by the host"); page = Page.Main; hash = 0; });
                        }
                    break;
            }
            Add("Back", () => { if (page == Page.Main || page == Page.MainMenu) Close(); else { page = Page.Main; hash = 0; } });
            var first = holder.GetComponentsInChildren<Button>().FirstOrDefault(b => b.interactable && b.gameObject.activeInHierarchy);
            if (first != null) UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(first.gameObject);
        }

        // Teleporting needs the game running: close the menu, the teleport starts once it's unpaused.
        private void TeleportTo(byte id)
        {
            var tp = Sync.TeamTeleport.Instance;
            if (tp == null) return;
            bool ok = tp.Request(id, out string message);
            Sync.Chat.Notice(message);
            if (!ok) return;
            Close();
            ui.Resume();
        }

        private static string HostName(Session s) => s.Players.TryGetValue(Protocol.HostId, out var h) ? h.Name : "the host";

        private void Add(string label, Action onClick)
        {
            var b = Clone(template, holder);
            b.name = "MP_" + label;
            SetLabel(b, label);
            var button = b.GetComponent<Button>();
            if (onClick == null) { button.interactable = false; return; }   // a line of text (player list)
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => { onClick(); hash = 0; });
        }

        private static GameObject Clone(GameObject original, Transform parent, bool active = true)
        {
            var temp = new GameObject("MP_CloneHolder");
            temp.SetActive(false);
            var copy = UnityEngine.Object.Instantiate(original, temp.transform);
            foreach (var c in copy.GetComponentsInChildren<UnityEngine.Localization.Components.LocalizeStringEvent>(true))
                UnityEngine.Object.DestroyImmediate(c);
            copy.SetActive(active);
            copy.transform.SetParent(parent, false);
            UnityEngine.Object.Destroy(temp);
            return copy;
        }

        private static void SetLabel(GameObject button, string text)
        {
            var tmp = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null) tmp.text = text;
        }

        private static void Rewire(GameObject button, Action onClick)
        {
            var b = button.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(() => onClick());
        }
    }

    // The pause menu's Main Menu button (UIManager.CallMainMenu) loads the menu straight away, session or not; in one,
    // it asks first (PauseMenuMP's page), then leaves the session properly (MPRunner.LeaveToMainMenu).
    [HarmonyPatch(typeof(UIManager), nameof(UIManager.CallMainMenu))]
    internal static class MainMenuButtonPatch
    {
        private static bool Prefix(UIManager __instance)
        {
            if (!PauseMenuMP.InSession) return true;
            if (__instance.onCooldown) return false;   // (as the game does)
            if (MPRunner.Instance.PauseMenu.AskMainMenu(__instance)) return false;
            MPRunner.Instance.LeaveToMainMenu(() => __instance.CallScene(Game.MainMenuScene));
            return false;
        }
    }

    // Other ways the game's UI loads the menu (UIManagerProxy.CallScene("MainMenu") on a button): leave first, no question.
    [HarmonyPatch(typeof(UIManager), nameof(UIManager.CallScene))]
    internal static class UiCallMenuScenePatch
    {
        private static bool Prefix(UIManager __instance, string sceneName)
        {
            if (sceneName != Game.MainMenuScene || !PauseMenuMP.InSession) return true;
            // (the game's way, once out of the session: back here with no session, so it runs)
            MPRunner.Instance.LeaveToMainMenu(() => __instance.CallScene(sceneName));
            return false;
        }
    }

    // Changing the language goes to the main menu too (after the game's own confirmation): leave the session first.
    [HarmonyPatch(typeof(OptionsPanel), nameof(OptionsPanel.CallMainMenu))]
    internal static class OptionsMainMenuPatch
    {
        private static bool Prefix(OptionsPanel __instance)
        {
            if (!PauseMenuMP.InSession) return true;
            MPRunner.Instance.LeaveToMainMenu(() => __instance.CallScene(Game.MainMenuScene));
            return false;
        }
    }
}
