using System;
using System.Collections.Generic;
using System.Linq;
using Steamworks;
using UnityEngine;

namespace EmbervilleMP.Steam
{
    internal enum LobbyKind { FriendsOnly, InviteOnly, Public }

    internal class LobbyInfo
    {
        public CSteamID Id;
        public string HostName;
        public CSteamID Host;
        public int Players, MaxPlayers;
        public string Mod;
        public string Ip;     // test sessions only (Steam over IP on one PC)
        public int Port;
    }

    // Steam lobbies: how friends find and join a game. The lobby only carries the meeting info; the game
    // traffic goes over Steam Networking Sockets to the lobby's host.
    internal class SteamLobby
    {
        public const string Tag = "emberville_mp";   // lobby key carrying the protocol version (used as the search filter)

        public static SteamLobby Instance { get; private set; }
        public CSteamID Current { get; private set; } = CSteamID.Nil;
        public bool InLobby => Current.IsValid();
        // Whether this copy of the game made the lobby (not "owner == me": in one-PC tests both copies are the same account).
        public bool IsOwner => InLobby && createdHere;
        private bool createdHere;
        public readonly List<LobbyInfo> Found = new List<LobbyInfo>();
        public bool Searching { get; private set; }
        public string TestNonce;      // tests: only see lobbies from this run

        // A lobby was entered as a guest: connect to its host.
        public event Action<LobbyInfo> JoinedAsGuest;
        public event Action<CSteamID> Created;

        private Callback<GameLobbyJoinRequested_t> joinRequested;
        private Callback<GameRichPresenceJoinRequested_t> richJoinRequested;
        private Callback<LobbyEnter_t> entered;
        private Callback<LobbyChatUpdate_t> chatUpdate;
        private readonly CallResult<LobbyCreated_t> createdResult = new CallResult<LobbyCreated_t>();
        private readonly CallResult<LobbyMatchList_t> listResult = new CallResult<LobbyMatchList_t>();
        private Action<CSteamID> pendingCreate;

        public static SteamLobby Start()
        {
            if (Instance != null) return Instance;
            if (!SteamBootstrap.Init()) return null;
            Instance = new SteamLobby();
            return Instance;
        }

        private SteamLobby()
        {
            joinRequested = Callback<GameLobbyJoinRequested_t>.Create(e =>
            {
                Plugin.Log.LogInfo("[steam] Invite accepted / Join Game from the friends list");
                Join(e.m_steamIDLobby);
            });
            richJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(e =>
            {
                if (TryParseConnect(e.m_rgchConnect, out var lobby)) Join(lobby);
            });
            entered = Callback<LobbyEnter_t>.Create(OnEntered);
            chatUpdate = Callback<LobbyChatUpdate_t>.Create(e => UpdatePlayerCount());
        }

        // "+connect_lobby <id>": what Steam puts on the command line (game started from an invite) and in rich presence.
        public static bool TryParseConnect(string text, out CSteamID lobby)
        {
            lobby = CSteamID.Nil;
            if (string.IsNullOrEmpty(text)) return false;
            var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int i = Array.IndexOf(parts, "+connect_lobby");
            if (i < 0 || i + 1 >= parts.Length || !ulong.TryParse(parts[i + 1], out ulong id)) return false;
            lobby = new CSteamID(id);
            return lobby.IsValid();
        }

        public static bool LaunchedFromInvite(out CSteamID lobby) =>
            TryParseConnect(string.Join(" ", Environment.GetCommandLineArgs()), out lobby);

        // ---- host ----

        public void Create(LobbyKind kind, int maxPlayers, Action<CSteamID> done = null)
        {
            Leave();
            pendingCreate = done;
            var type = kind == LobbyKind.Public ? ELobbyType.k_ELobbyTypePublic
                : kind == LobbyKind.InviteOnly ? ELobbyType.k_ELobbyTypePrivate : ELobbyType.k_ELobbyTypeFriendsOnly;
            createdResult.Set(SteamMatchmaking.CreateLobby(type, maxPlayers), OnCreated);
        }

        private void OnCreated(LobbyCreated_t r, bool failed)
        {
            if (failed || r.m_eResult != EResult.k_EResultOK)
            {
                Plugin.Log.LogWarning($"[steam] Lobby creation failed: {r.m_eResult}");
                pendingCreate?.Invoke(CSteamID.Nil);
                return;
            }
            Current = new CSteamID(r.m_ulSteamIDLobby);
            createdHere = true;
            var me = SteamUser.GetSteamID();
            SteamMatchmaking.SetLobbyData(Current, Tag, Net.Protocol.Version.ToString());
            SteamMatchmaking.SetLobbyData(Current, "mod", Plugin.Version);
            SteamMatchmaking.SetLobbyData(Current, "name", SteamFriends.GetPersonaName());
            SteamMatchmaking.SetLobbyData(Current, "host", me.m_SteamID.ToString());
            if (TestNonce != null) SteamMatchmaking.SetLobbyData(Current, "test", TestNonce);
            SetRichPresence();
            UpdatePlayerCount();
            Plugin.Log.LogInfo($"[steam] Lobby {Current} created");
            pendingCreate?.Invoke(Current);
            Created?.Invoke(Current);
        }

        // Tests on one PC: guests reach the host over IP instead of P2P.
        public void PublishTestAddress(string ip, int port)
        {
            if (!InLobby) return;
            SteamMatchmaking.SetLobbyData(Current, "ip", ip);
            SteamMatchmaking.SetLobbyData(Current, "port", port.ToString());
        }

        public void SetLobbyField(string key, string value)
        {
            if (IsOwner) SteamMatchmaking.SetLobbyData(Current, key, value);
        }

        private void UpdatePlayerCount()
        {
            if (!IsOwner) return;
            int players = Net.Session.Current?.Players.Count ?? 1;
            SteamMatchmaking.SetLobbyData(Current, "players", players.ToString());
        }

        public void RefreshPlayerCount() => UpdatePlayerCount();

        // Friends see "Join Game"; invites carry the lobby.
        private void SetRichPresence()
        {
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + Current.m_SteamID);
            SteamFriends.SetRichPresence("status", "Playing Emberville co-op");
        }

        public void InviteOverlay()
        {
            if (InLobby) SteamFriends.ActivateGameOverlayInviteDialog(Current);
        }

        public bool Invite(CSteamID friend) => InLobby && SteamMatchmaking.InviteUserToLobby(Current, friend);

        // Friends who are online (for the in-game invite list, which works without the Steam overlay).
        public static List<(CSteamID id, string name, bool inGame)> OnlineFriends()
        {
            var list = new List<(CSteamID, string, bool)>();
            int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < n; i++)
            {
                var f = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (SteamFriends.GetFriendPersonaState(f) == EPersonaState.k_EPersonaStateOffline) continue;
                bool inGame = SteamFriends.GetFriendGamePlayed(f, out var game) && game.m_gameID.AppID() == SteamUtils.GetAppID();
                list.Add((f, SteamFriends.GetFriendPersonaName(f), inGame));
            }
            return list.OrderByDescending(x => x.Item3).ThenBy(x => x.Item2).ToList();
        }

        // ---- guest ----

        public void Join(CSteamID lobby)
        {
            if (Current == lobby) return;
            Leave();
            Plugin.Log.LogInfo($"[steam] Joining lobby {lobby}");
            SteamMatchmaking.JoinLobby(lobby);
        }

        private void OnEntered(LobbyEnter_t e)
        {
            var lobby = new CSteamID(e.m_ulSteamIDLobby);
            if ((EChatRoomEnterResponse)e.m_EChatRoomEnterResponse != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Plugin.Log.LogWarning($"[steam] Couldn't enter lobby: {(EChatRoomEnterResponse)e.m_EChatRoomEnterResponse}");
                return;
            }
            if (createdHere && Current == lobby) return; // our own lobby (OnCreated handles it)
            Current = lobby;
            createdHere = false;
            var info = Read(lobby);
            if (info.Mod == null || SteamMatchmaking.GetLobbyData(lobby, Tag) != Net.Protocol.Version.ToString())
                Plugin.Log.LogWarning($"[steam] Lobby {lobby} runs mod {info.Mod ?? "?"} (we have {Plugin.Version})");
            SetRichPresence();
            Plugin.Log.LogInfo($"[steam] In {info.HostName}'s lobby");
            JoinedAsGuest?.Invoke(info);
        }

        public void Leave()
        {
            if (!InLobby) return;
            SteamMatchmaking.LeaveLobby(Current);
            Current = CSteamID.Nil;
            createdHere = false;
            SteamFriends.ClearRichPresence();
        }

        // ---- public games ----

        public void Search()
        {
            Searching = true;
            SteamMatchmaking.AddRequestLobbyListStringFilter(Tag, Net.Protocol.Version.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
            if (TestNonce != null) SteamMatchmaking.AddRequestLobbyListStringFilter("test", TestNonce, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            listResult.Set(SteamMatchmaking.RequestLobbyList(), (r, failed) =>
            {
                Searching = false;
                Found.Clear();
                if (failed) return;
                for (int i = 0; i < r.m_nLobbiesMatching; i++) Found.Add(Read(SteamMatchmaking.GetLobbyByIndex(i)));
                Plugin.Log.LogInfo($"[steam] {Found.Count} public game(s)");
            });
        }

        private static LobbyInfo Read(CSteamID lobby)
        {
            ulong.TryParse(SteamMatchmaking.GetLobbyData(lobby, "host"), out ulong host);
            int.TryParse(SteamMatchmaking.GetLobbyData(lobby, "players"), out int players);
            int.TryParse(SteamMatchmaking.GetLobbyData(lobby, "port"), out int port);
            string ip = SteamMatchmaking.GetLobbyData(lobby, "ip");
            return new LobbyInfo
            {
                Id = lobby,
                HostName = SteamMatchmaking.GetLobbyData(lobby, "name"),
                Host = host != 0 ? new CSteamID(host) : SteamMatchmaking.GetLobbyOwner(lobby),
                Players = players,
                MaxPlayers = SteamMatchmaking.GetLobbyMemberLimit(lobby),
                Mod = SteamMatchmaking.GetLobbyData(lobby, "mod"),
                Ip = string.IsNullOrEmpty(ip) ? null : ip,
                Port = port,
            };
        }
    }
}
