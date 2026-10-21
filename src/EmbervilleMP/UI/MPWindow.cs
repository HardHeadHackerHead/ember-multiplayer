using System.Linq;
using EmbervilleMP.Net;
using UnityEngine;

namespace EmbervilleMP
{
    internal static class MPStyle
    {
        private static GUIStyle nameTag, hud;

        public static GUIStyle NameTag => nameTag ?? (nameTag = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter, fontSize = Mathf.Max(14, Screen.height / 55), fontStyle = FontStyle.Bold,
        });

        public static GUIStyle Hud => hud ?? (hud = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(12, Screen.height / 70) });

        public static void Shadowed(Rect r, string text, GUIStyle style)
        {
            var c = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, style);
            GUI.color = c;
            GUI.Label(r, text, style);
        }
    }

    // F8: host / join / leave, LAN search, who's connected.
    internal class MPWindow
    {
        public bool Open;
        private Rect rect = new Rect(40, 40, 420, 420);
        private string address, portText, nameText;
        private LanDiscovery discovery;
        private float discoveryUntil;
        private bool showFriends;
        private Vector2 friendsScroll;

        public MPWindow()
        {
            address = Plugin.Settings.LastAddress.Value;
            portText = Plugin.Settings.Port.Value.ToString();
            nameText = Plugin.Settings.PlayerName.Value;
        }

        public static string DefaultName()
        {
            if (!string.IsNullOrEmpty(Args.Name)) return Args.Name;
            if (!string.IsNullOrEmpty(Plugin.Settings.PlayerName.Value)) return Plugin.Settings.PlayerName.Value;
            if (GameData.playerData != null && !string.IsNullOrEmpty(GameData.playerData.playerName)) return GameData.playerData.playerName;
            return System.Environment.UserName;
        }

        public void Update()
        {
            if (Input.GetKeyDown(Plugin.Settings.MenuKey.Value)) Open = !Open;
            InputBlock.Set("window", Open);
            if (discovery != null)
            {
                discovery.Poll();
                if (Time.realtimeSinceStartup > discoveryUntil + 10f) { discovery.Stop(); discovery = null; }
            }
        }

        public void OnGUI()
        {
            DrawHud();
            if (!Open) return;
            rect = GUILayout.Window(0x4D50, rect, Draw, "EMP: Ember Multiplayer");
        }

        private void DrawHud()
        {
            if (Args.Has("clean")) return;   // --mp-clean: screenshots without the status line
            var s = Session.Current;
            string text = s == null
                ? $"[{Plugin.Settings.MenuKey.Value}] Multiplayer"
                : $"[{Plugin.Settings.MenuKey.Value}] {(s.IsHost ? "Hosting" : "Guest")} · {s.Players.Count} player(s) · {s.Status}";
            MPStyle.Shadowed(new Rect(10, Screen.height - 28, 900, 24), text, MPStyle.Hud);
        }

        private void Draw(int id)
        {
            var s = Session.Current;
            GUILayout.Label("Name");
            nameText = GUILayout.TextField(nameText ?? "", 24);
            var lobby = Steam.SteamLobby.Instance;
            if (s == null)
            {
                if (lobby != null)
                {
                    GUILayout.Space(6);
                    GUILayout.Label("Steam");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Host: friends")) MPRunner.Instance.HostSteam(Steam.LobbyKind.FriendsOnly);
                    if (GUILayout.Button("Invite only")) MPRunner.Instance.HostSteam(Steam.LobbyKind.InviteOnly);
                    if (GUILayout.Button("Public")) MPRunner.Instance.HostSteam(Steam.LobbyKind.Public);
                    GUILayout.EndHorizontal();
                    if (GUILayout.Button(lobby.Searching ? "Searching public games..." : "Find public games")) lobby.Search();
                    foreach (var g in lobby.Found)
                        if (GUILayout.Button($"Join {g.HostName}'s game ({g.Players}/{g.MaxPlayers})")) lobby.Join(g.Id);
                    GUILayout.Label("Friends can also join from the Steam friends list (Join Game) or an invite.");
                }
                GUILayout.Space(6);
                GUILayout.Label("LAN / direct IP");
                GUILayout.BeginHorizontal();
                GUILayout.Label("Port", GUILayout.Width(40));
                portText = GUILayout.TextField(portText, 6, GUILayout.Width(70));
                if (GUILayout.Button("Host")) MPRunner.Instance.StartHost(Port(), Name());
                GUILayout.EndHorizontal();
                GUILayout.Space(6);
                GUILayout.Label("Join address");
                GUILayout.BeginHorizontal();
                address = GUILayout.TextField(address, 64);
                if (GUILayout.Button("Join", GUILayout.Width(70))) MPRunner.Instance.StartJoin(address, Port(), Name());
                GUILayout.EndHorizontal();
                if (GUILayout.Button(discovery != null && Time.realtimeSinceStartup < discoveryUntil ? "Searching LAN..." : "Find games on LAN"))
                {
                    discovery = discovery ?? new LanDiscovery();
                    discovery.Probe(Port());
                    discoveryUntil = Time.realtimeSinceStartup + 2f;
                }
                if (discovery != null)
                    foreach (var f in discovery.Found)
                        if (GUILayout.Button($"Join {f.name}'s game ({f.players} player(s)) at {f.endPoint}"))
                            MPRunner.Instance.StartJoin(f.endPoint.Address.ToString(), f.endPoint.Port, Name());
            }
            else
            {
                GUILayout.Label(s.Status);
                GUILayout.Label($"Loot: {Sync.LootRules.Mode}{(s.IsHost ? "" : " (host's setting)")}");
                GUILayout.Label("Players:");
                foreach (var p in s.Players.Values.OrderBy(p => p.Id))
                    GUILayout.Label($"  {p.Name}{(p.Id == s.LocalId ? " (you)" : "")}{(p.Id == Protocol.HostId ? " [host]" : "")}  {(p.Id != s.LocalId ? p.Ping + " ms" : "")}");
                NetStats.DrawGui();
                if (lobby != null && lobby.InLobby)
                {
                    if (GUILayout.Button("Invite friends (Steam overlay)")) lobby.InviteOverlay();
                    showFriends = GUILayout.Toggle(showFriends, "Invite from my friends list");
                    if (showFriends)
                    {
                        friendsScroll = GUILayout.BeginScrollView(friendsScroll, GUILayout.Height(120));
                        foreach (var f in Steam.SteamLobby.OnlineFriends())
                        {
                            GUILayout.BeginHorizontal();
                            GUILayout.Label(f.name + (f.inGame ? " (in Emberville)" : ""));
                            if (GUILayout.Button("Invite", GUILayout.Width(60))) lobby.Invite(f.id);
                            GUILayout.EndHorizontal();
                        }
                        GUILayout.EndScrollView();
                    }
                }
                if (GUILayout.Button(s.IsHost ? "Stop hosting" : "Leave")) s.Stop();
            }
            Api.ApiMods.DrawGui();   // declared mods, and the ones without a declaration
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close")) Open = false;
            GUI.DragWindow();
        }

        private int Port() => int.TryParse(portText, out int p) && p > 0 && p < 65536 ? p : Plugin.Settings.Port.Value;

        private string Name()
        {
            if (!string.IsNullOrWhiteSpace(nameText)) { Plugin.Settings.PlayerName.Value = nameText.Trim(); Plugin.Settings.Save(); }
            return DefaultName();
        }
    }
}
