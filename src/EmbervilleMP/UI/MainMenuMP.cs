using System;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Steam;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleMP
{
    // The main menu's MULTIPLAYER button and screens. Built from the game's own UI (the SETTINGS button and the
    // exit-confirmation panel are cloned), so it looks, sounds and animates like the rest of the menu.
    //   Host: choose who can join -> the game's character + world selection -> hosting starts once in the world.
    //   Join: the game's character selection -> a list of games (friends, public, LAN, IP) -> the host's world.
    internal class MainMenuMP
    {
        public static LobbyKind? PendingHost;   // set: host this way once the chosen world is loaded
        public static bool PendingHostLan;
        public static bool PendingJoin;         // set: after the character is chosen, show the games list

        private enum Page { Root, Host, Join, Ip }

        private MainMenuManager mm;
        private int builtForScene = -1;
        private GameObject panel;
        private Transform holder;
        private GameObject buttonTemplate;
        private TextMeshProUGUI title;
        private TMP_InputField ipField;
        private Page page;
        private bool open;
        private string status = "";
        private LanDiscovery lan;
        private float nextSearch;
        private int listHash;

        public void Update()
        {
            // Hosting starts once the world chosen through the normal Play flow is up.
            if (PendingHost.HasValue || PendingHostLan)
            {
                if (Game.InGame && Session.Current == null)
                {
                    if (PendingHostLan) MPRunner.Instance.StartHost(Plugin.Settings.Port.Value, MPWindow.DefaultName());
                    else MPRunner.Instance.HostSteam(PendingHost.Value);
                    PendingHost = null;
                    PendingHostLan = false;
                }
            }
            if (Game.SceneName != Game.MainMenuScene) { builtForScene = -1; open = false; return; }
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (builtForScene != scene)
            {
                mm = UnityEngine.Object.FindAnyObjectByType<MainMenuManager>();
                if (mm == null || mm.state != MainMenuState.MainScreen) return;   // wait until the menu is set up
                // Localized UI made while localization is still starting deadlocks the game (see DevHarness.AutoStart).
                if (!UnityEngine.Localization.Settings.LocalizationSettings.InitializationOperation.IsDone || OptionsPanel.Options == null) return;
                try { Build(); builtForScene = scene; }
                catch (Exception e) { Plugin.Log.LogError("[menu] Couldn't add the multiplayer menu: " + e); builtForScene = scene; }
            }
            if (mm == null) return;

            // Joining: the character was chosen (picked or newly created); the game now wants a world: show games instead.
            if (PendingJoin && mm.state == MainMenuState.WorldSelection && !mm.isBusy)
            {
                mm.isBusy = true;
                mm.worldSelection.Panel.AnimExit(() => { mm.isBusy = false; Show(Page.Join); });
                mm.state = (MainMenuState)100;
            }
            if (open && page == Page.Join) RefreshJoinList();
            if (open && Input.GetKeyDown(KeyCode.Escape)) Back();
        }

        // ---------------- building ----------------

        private void Build()
        {
            var settings = Game.FindUi(mm.buttonsMain.transform, "Content/Holder/Button_Options");
            var buttons = settings?.parent;
            if (settings == null) throw new Exception("menu buttons not found");

            // MULTIPLAYER, between PLAY and SETTINGS.
            var mp = Clone(settings.gameObject, buttons);
            mp.name = "Button_Multiplayer";
            mp.transform.SetSiblingIndex(settings.GetSiblingIndex());
            SetLabel(mp, "MULTIPLAYER");
            Rewire(mp, () => OpenFromMain());

            // The panel: a copy of the exit confirmation (title + a column of menu buttons).
            var confirm = mm.panelConfirm;
            panel = Clone(confirm, confirm.transform.parent, active: false);
            panel.name = "Panel_Multiplayer";
            holder = Game.FindUi(panel.transform, "Content/Holder/Button_Confirm").parent;
            title = Game.FindUi(holder, "Title").GetComponent<TextMeshProUGUI>();
            Unlocalize(title.gameObject);
            buttonTemplate = holder.Find("Button_Confirm").gameObject;
            buttonTemplate.SetActive(false);
            var back = holder.Find("Button_Back");
            if (back != null) UnityEngine.Object.Destroy(back.gameObject);

            // A text box for addresses, copied from the character creator's name field.
            var nameField = mm.characterCustomizationPanel != null ? mm.characterCustomizationPanel.nameInputField : null;
            if (nameField != null)
            {
                ipField = Clone(nameField.gameObject, holder, active: false).GetComponent<TMP_InputField>();
                ipField.name = "MP_Address";
                ipField.onValueChanged = new TMP_InputField.OnChangeEvent();
                ipField.onEndEdit = new TMP_InputField.SubmitEvent();
                ipField.characterLimit = 64;
                ipField.contentType = TMP_InputField.ContentType.Standard;
                ipField.gameObject.SetActive(false);
            }

            AddBadge();
            AddVersion();
            Plugin.Log.LogInfo("[menu] Multiplayer menu added");
        }

        // "MULTIPLAYER" across the bottom corner of the Emberville logo.
        private void AddBadge()
        {
            var logo = mm.panelMain.transform.Find("Content/Logo Holder");
            var source = mm.panelMain.transform.parent.Find("Copyright")?.GetComponent<TextMeshProUGUI>();
            if (logo == null || source == null) return;
            var badge = Clone(source.gameObject, logo);
            badge.name = "MP_Badge";
            Unlocalize(badge);
            var t = badge.GetComponent<TextMeshProUGUI>();
            t.text = "MULTIPLAYER";
            t.fontSize = 34;
            t.fontStyle = FontStyles.Bold;
            t.enableWordWrapping = false;
            t.alignment = TextAlignmentOptions.Center;
            t.color = new Color(0.36f, 0.92f, 0.82f);
            t.outlineWidth = 0.25f;
            t.outlineColor = new Color32(30, 18, 26, 255);
            var rt = (RectTransform)badge.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(360, 50);
            rt.anchoredPosition = new Vector2(210, -95);
            rt.localRotation = Quaternion.Euler(0, 0, 7);
            rt.localScale = Vector3.one;
        }

        // "Multiplayer mod 0.x" under the game's version number.
        private void AddVersion()
        {
            var root = GameObject.Find("VersionNumber");
            var text = root != null ? root.GetComponentInChildren<TextMeshProUGUI>() : null;
            if (text == null) return;
            var copy = Clone(text.gameObject, text.transform.parent);
            copy.name = "MP_Version";
            Unlocalize(copy);
            foreach (var mb in copy.GetComponents<MonoBehaviour>())
                if (!(mb is TextMeshProUGUI)) UnityEngine.Object.Destroy(mb);   // VersionNumber would rewrite it
            copy.GetComponent<TextMeshProUGUI>().text = "Multiplayer mod " + Plugin.Version;
            var rt = (RectTransform)copy.transform;
            rt.anchoredPosition += new Vector2(0, -rt.rect.height - 4);
        }

        // Copies a piece of the game's UI without letting it wake up first: localized text components on the copy
        // are removed before anything runs.
        private static GameObject Clone(GameObject original, Transform parent, bool active = true)
        {
            var holder = new GameObject("MP_CloneHolder");
            holder.SetActive(false);
            var copy = UnityEngine.Object.Instantiate(original, holder.transform);
            foreach (var c in copy.GetComponentsInChildren<UnityEngine.Localization.Components.LocalizeStringEvent>(true))
                UnityEngine.Object.DestroyImmediate(c);
            copy.SetActive(active);
            copy.transform.SetParent(parent, false);
            UnityEngine.Object.Destroy(holder);
            return copy;
        }

        private static void Unlocalize(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<UnityEngine.Localization.Components.LocalizeStringEvent>(true))
                UnityEngine.Object.Destroy(c);
        }

        private static void SetLabel(GameObject button, string text)
        {
            Unlocalize(button);
            var tmp = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null) tmp.text = text;
        }

        // A cloned button still carries the original's inspector-wired click: replace the whole event.
        private static void Rewire(GameObject button, Action onClick)
        {
            var b = button.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(() => onClick());
        }

        // ---------------- pages ----------------

        private async void OpenFromMain()
        {
            if (mm.isBusy) return;
            mm.isBusy = true;
            PendingHost = null; PendingHostLan = false; PendingJoin = false;
            await mm.panelMain.AnimExitAsync();
            mm.isBusy = false;
            mm.state = (MainMenuState)100;   // not one of the game's states: its Back/Esc handling stays out of ours
            Show(Page.Root);
        }

        private void Show(Page p)
        {
            page = p;
            open = true;
            Rebuild();
            if (!panel.activeSelf) panel.AnimEntry();
        }

        private void Rebuild()
        {
            foreach (Transform child in holder)
                if (child.gameObject != buttonTemplate && child != title.transform && child.name != "Space" && (ipField == null || child != ipField.transform))
                    UnityEngine.Object.Destroy(child.gameObject);
            if (ipField != null) ipField.gameObject.SetActive(page == Page.Ip);
            switch (page)
            {
                case Page.Root:
                    SetTitle("Multiplayer");
                    AddButton("Host a game", () => Show(Page.Host));
                    AddButton("Join a game", StartJoinFlow);
                    AddButton("Back", Back);
                    break;
                case Page.Host:
                    SetTitle("Who can join?");
                    if (SteamLobby.Instance != null)
                    {
                        AddButton("Steam friends", () => StartHostFlow(LobbyKind.FriendsOnly, false));
                        AddButton("Invite only", () => StartHostFlow(LobbyKind.InviteOnly, false));
                        AddButton("Anyone (public)", () => StartHostFlow(LobbyKind.Public, false));
                    }
                    AddButton("LAN / direct IP", () => StartHostFlow(LobbyKind.FriendsOnly, true));
                    AddButton(Sync.LootRules.SettingLabel, () => { Sync.LootRules.CycleSetting(); Rebuild(); });
                    AddButton(Sync.TeamTeleport.SettingLabel, () => { Sync.TeamTeleport.CycleSetting(); Rebuild(); });
                    AddButton(Sync.EstateRules.EstateLabel, () => { Sync.EstateRules.CycleEstate(); Rebuild(); });
                    AddButton(Sync.EstateRules.DemoEndLabel, () => { Sync.EstateRules.CycleDemoEnd(); Rebuild(); });
                    AddButton("Back", Back);
                    break;
                case Page.Join:
                    SetTitle(string.IsNullOrEmpty(status) ? "Join a game" : status);
                    foreach (var (label, action) in JoinEntries()) AddButton(label, action);
                    AddButton("Join by address", () => Show(Page.Ip));
                    AddButton("Back", Back);
                    break;
                case Page.Ip:
                    SetTitle("Host's address (IP or IP:port)");
                    if (ipField != null) { ipField.text = Plugin.Settings.LastAddress.Value; ipField.transform.SetSiblingIndex(holder.childCount - 1); }
                    AddButton("Join", JoinByAddress);
                    AddButton("Back", () => Show(Page.Join));
                    break;
            }
            var first = holder.GetComponentsInChildren<Button>().FirstOrDefault(b => b.gameObject.activeInHierarchy);
            if (first != null) UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(first.gameObject);
        }

        private void SetTitle(string text) => title.text = text;

        private void AddButton(string label, Action onClick)
        {
            var b = Clone(buttonTemplate, holder);
            b.name = "MP_" + label;
            SetLabel(b, label);
            Rewire(b, onClick);
        }

        private async void Back()
        {
            if (mm.isBusy) return;
            if (page == Page.Host || (page == Page.Join && !PendingJoin)) { Show(Page.Root); return; }
            if (page == Page.Ip) { Show(Page.Join); return; }
            // Root, or the games list after choosing a character: back to the main screen.
            mm.isBusy = true;
            open = false;
            PendingJoin = false;
            lan?.Stop();
            lan = null;
            await panel.AnimExitAsync();
            mm.panelMain.AnimEntry();
            mm.state = MainMenuState.MainScreen;
            mm.isBusy = false;
        }

        private async void StartHostFlow(LobbyKind kind, bool lanOnly)
        {
            PendingHost = lanOnly ? (LobbyKind?)null : kind;
            PendingHostLan = lanOnly;
            await CloseThenPlay();
        }

        private async void StartJoinFlow()
        {
            PendingJoin = true;
            status = "";
            await CloseThenPlay();
        }

        // The game's own Play: character selection (and for hosts, world selection).
        private async Cysharp.Threading.Tasks.UniTask CloseThenPlay()
        {
            open = false;
            mm.isBusy = true;
            await panel.AnimExitAsync();
            // What Play does after hiding the main panel (ours already did): show the character screen.
            await mm.characterSelection.Panel.AnimEntryAsync();
            mm.state = MainMenuState.CharacterSelection;
            mm.isBusy = false;
        }

        // ---------------- the games list ----------------

        private List<(string label, Action action)> JoinEntries()
        {
            var list = new List<(string, Action)>();
            var lobby = SteamLobby.Instance;
            if (lobby != null)
            {
                foreach (var f in FriendGames())
                {
                    var id = f.lobby;
                    list.Add(($"{f.name}'s game (friend)", () => JoinLobby(id, f.name)));
                }
                foreach (var g in lobby.Found)
                {
                    var id = g.Id;
                    if (list.Any(x => x.Item1.StartsWith(g.HostName + "'s game"))) continue;
                    list.Add(($"{g.HostName}'s game ({g.Players}/{g.MaxPlayers})", () => JoinLobby(id, g.HostName)));
                }
            }
            if (lan != null)
                foreach (var f in lan.Found)
                {
                    var ep = f.endPoint;
                    list.Add(($"{f.name}'s game (LAN)", () => JoinIp(ep.Address.ToString(), ep.Port, f.name)));
                }
            return list;
        }

        // Friends playing Emberville in a lobby (Steam tells us which).
        private static List<(string name, CSteamID lobby)> FriendGames()
        {
            var list = new List<(string, CSteamID)>();
            if (!SteamBootstrap.Ready) return list;
            int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < n; i++)
            {
                var f = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (SteamFriends.GetFriendGamePlayed(f, out var game) && game.m_gameID.AppID() == SteamUtils.GetAppID() && game.m_steamIDLobby.IsValid())
                    list.Add((SteamFriends.GetFriendPersonaName(f), game.m_steamIDLobby));
            }
            return list;
        }

        private void RefreshJoinList()
        {
            float now = Time.realtimeSinceStartup;
            if (now >= nextSearch)
            {
                nextSearch = now + 4f;
                SteamLobby.Instance?.Search();
                lan = lan ?? new LanDiscovery();
                lan.Probe(Plugin.Settings.Port.Value);
            }
            lan?.Poll();
            var s = Session.Current;
            if (s != null && !s.Ready) status = s.Status;
            else if (s == null && status.StartsWith("Connecting")) status = "Couldn't connect";
            int hash = string.Join("|", JoinEntries().Select(e => e.label)).GetHashCode() ^ status.GetHashCode();
            if (hash != listHash) { listHash = hash; Rebuild(); }
        }

        private void JoinLobby(CSteamID lobby, string name)
        {
            status = $"Connecting to {name}...";
            SteamLobby.Instance.Join(lobby);
            Rebuild();
        }

        private void JoinIp(string ip, int port, string name)
        {
            status = $"Connecting to {name}...";
            MPRunner.Instance.StartJoin(ip, port, MPWindow.DefaultName());
            Rebuild();
        }

        private void JoinByAddress()
        {
            string text = ipField != null ? ipField.text.Trim() : "";
            if (text.Length == 0) return;
            string[] parts = text.Split(':');
            int port = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : Plugin.Settings.Port.Value;
            page = Page.Join;
            JoinIp(parts[0], port, text);
        }
    }
}
