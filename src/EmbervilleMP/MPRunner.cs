using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP
{
    // The plugin's one MonoBehaviour: drives the session, the remote players, the UI and the self-tests.
    internal class MPRunner : MonoBehaviour
    {
        public static MPRunner Instance { get; private set; }

        public Presence Presence { get; private set; }
        public EnemySync Enemies { get; private set; }
        public HostWorld World { get; private set; }
        public WorldState WorldStateSync { get; private set; }
        public Sync.Objects.ObjectSync Objects { get; private set; }
        public LootRules Loot { get; private set; }
        public EstateRules Estate { get; private set; }
        public MPWindow Window { get; private set; }
        public ChatBox ChatBox { get; private set; }
        public MainMenuMP Menu { get; private set; }
        public PauseMenuMP PauseMenu { get; private set; }
        public PartyHud PartyHud { get; private set; }

        private float startTime;
        private bool quitting;
        private bool wasInGame;

        private void Awake()
        {
            Instance = this;
            startTime = Time.realtimeSinceStartup;
            Window = new MPWindow();
            ChatBox = new ChatBox();
            Menu = new MainMenuMP();
            PauseMenu = new PauseMenuMP();
            PartyHud = new PartyHud();
            DevHarness.ApplyRuntimeSettings();
            Patches.Speed.Apply();
            Watchdog.Start();
            SceneManager.activeSceneChanged += (from, to) =>
            {
                Plugin.Log.LogInfo($"[scene] {from.name} -> {to.name} at {Time.realtimeSinceStartup:F1}s");
                DevHarness.ApplyWindowSize();
                string previous = lastScene;
                lastScene = to.name;
                if (to.name == Game.MainMenuScene && !string.IsNullOrEmpty(previous) && previous != Game.MainMenuScene && previous != Game.SplashScene)
                    EndSessionOnMenu();
            };
        }

        private string lastScene;

        // ---- the game's Main Menu button ----

        // The game's own Main Menu button (pause menu; a language change) while in a session: leave it the way
        // MULTIPLAYER > Leave / Stop hosting does, then the main menu. A guest playing in the host's world goes there
        // through GuestWorld (character saved, the host's world never written, the throw-away world dropped); the
        // host, or a guest still in its own world, then goes the game's own way (it saves that world: theirs).
        public void LeaveToMainMenu(System.Action gameWay)
        {
            bool away = GuestWorld.Active;
            var s = Session.Current;
            Plugin.Log.LogInfo($"[net] Main Menu: {(s == null ? (Reconnecting ? "giving up reconnecting" : "no session") : s.IsHost ? "ending the session for everyone" : "leaving the session")} first");
            GuestWorld.ReturnToMenu = true;
            try
            {
                if (Reconnecting) CancelReconnect();
                else s?.Stop();
                if (GuestWorld.Active) GuestWorld.ReturnHome();   // (no session left to end it: still go)
            }
            finally { GuestWorld.ReturnToMenu = false; }
            if (!away) gameWay?.Invoke();
        }

        // Safety net: the main menu loaded from a game (a way we didn't catch) with a session still up. A session is
        // played in one world, and the next world picked on this menu must not join it (its flags, quests and lore
        // would merge both ways and be saved). Leave as MULTIPLAYER > Leave / Stop hosting would.
        private void EndSessionOnMenu()
        {
            if (Reconnecting)
            {
                Plugin.Log.LogWarning("[net] On the main menu while reconnecting: giving up");
                CancelReconnect();
                return;
            }
            var s = Session.Current;
            if (s == null && !GuestWorld.Active) return;
            Plugin.Log.LogWarning("[net] Back on the main menu with the session still up: " + (s != null && s.IsHost ? "ending it" : "leaving it"));
            s?.Stop();
            GuestWorld.ReturnHome();   // (a guest: nothing to load, only what playing in the host's world left set)
        }

        private Steamworks.CSteamID pendingLobby = Steamworks.CSteamID.Nil;

        private void Start()
        {
            // Steam (when available) from the start, so invites and "Join Game" work any time.
            var lobby = Steam.SteamLobby.Start();
            if (lobby != null)
            {
                lobby.TestNonce = Args.Get("lobby-test");
                lobby.JoinedAsGuest += OnLobbyJoined;
                if (Steam.SteamLobby.LaunchedFromInvite(out var invited)) pendingLobby = invited;
            }
            if (Args.Has("host-steam")) HostSteam(Steam.LobbyKind.Public);
            else if (Args.Host) StartHost(Args.HostPort, MPWindow.DefaultName(), ArgTransport);
            else if (Args.Join == "lobby") StartCoroutine(JoinFirstPublicLobby());
            else if (Args.Join == "lan") StartCoroutine(JoinFirstLanGame());
            else if (Args.Join != null)
            {
                string[] parts = Args.Join.Split(':');
                int port = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : Plugin.Settings.Port.Value;
                StartJoin(parts[0], port, MPWindow.DefaultName(), ArgTransport);
            }
            if (Args.Get("join-later") != null) StartCoroutine(JoinWhenInGame(Args.Get("join-later")));
            if (Args.AutoStart) StartCoroutine(DevHarness.AutoStart());
            if (Args.Test != null) ApiTestMod.Register();   // (the API scenarios' channels, stores, kinds: every copy, before any session)
            if (Args.Test != null) StartCoroutine(SelfTest.Run(Args.Test));
        }

        // Hosts over Steam: a P2P listen socket plus a lobby friends can find. (--mp-transport=steamip: tests on one PC,
        // the lobby then also carries the host's address.)
        public bool HostSteam(Steam.LobbyKind kind)
        {
            var lobby = Steam.SteamLobby.Instance;
            if (lobby == null) { Plugin.Log.LogError("[steam] Steam isn't available: " + Steam.SteamBootstrap.Error); return false; }
            bool overIp = ArgTransport == TransportKind.SteamIP;
            if (!StartHost(Args.HostPort, MPWindow.DefaultName(), overIp ? TransportKind.SteamIP : TransportKind.SteamP2P)) return false;
            var session = Session.Current;
            lobby.Create(kind, session.MaxPlayers, id =>
            {
                if (overIp) lobby.PublishTestAddress("127.0.0.1", Args.HostPort);
            });
            session.PlayerJoined += _ => lobby.RefreshPlayerCount();
            session.PlayerLeft += _ => lobby.RefreshPlayerCount();
            session.Ended += lobby.Leave;
            return true;
        }

        private void OnLobbyJoined(Steam.LobbyInfo info)
        {
            if (Session.Current != null) return;
            bool menuReady = Game.SceneName == Game.MainMenuScene && GameData.HasPlayerData;
            if (!Game.InGame && !menuReady) { pendingLobbyInfo = info; return; } // wait for a character (menu) or a game
            ConnectToLobbyHost(info);
        }

        private Steam.LobbyInfo pendingLobbyInfo;

        private void ConnectToLobbyHost(Steam.LobbyInfo info)
        {
            bool ok = info.Ip != null
                ? StartJoin(info.Ip, info.Port, MPWindow.DefaultName(), TransportKind.SteamIP)
                : StartJoin(info.Host.m_SteamID.ToString(), 0, MPWindow.DefaultName(), TransportKind.SteamP2P);
            if (ok) Session.Current.Ended += LeaveLobbyUnlessReconnecting;
        }

        private void LeaveLobbyUnlessReconnecting()
        {
            if (!Reconnecting) Steam.SteamLobby.Instance?.Leave();
        }

        // ---- reconnect: a guest whose connection drops keeps playing and rejoins the same host ----

        private (string target, int port, string name, TransportKind kind)? lastJoin;
        public bool Reconnecting { get; private set; }
        public string ReconnectTarget => lastJoin?.target;
        private Coroutine reconnect;
        private Session backedUpFor;
        private byte lostId;

        private void OnSessionEnded(Session s)
        {
            int seconds = Args.Int("reconnect", 60);
            if (s.IsHost || !s.Dropped || lastJoin == null || seconds <= 0 || Reconnecting) return;
            Reconnecting = true;
            lostId = s.LocalId;
            reconnect = StartCoroutine(Reconnect(seconds));
        }

        private System.Collections.IEnumerator Reconnect(float seconds)
        {
            var j = lastJoin.Value;
            Plugin.Log.LogWarning($"[net] Lost the host; trying to reconnect for {seconds:F0} s");
            float until = Time.realtimeSinceStartup + seconds;
            yield return new WaitForSecondsRealtime(1f);
            while (Time.realtimeSinceStartup < until)
            {
                Session attempt = null;
                if (Session.Current == null && StartJoin(j.target, j.port, j.name, j.kind, retry: true))
                {
                    attempt = Session.Current;
                    attempt.RejoinAs = lostId;
                }
                // wait for this attempt to be welcomed or to fail
                float give = Time.realtimeSinceStartup + 12f;
                while (attempt != null && Session.Current == attempt && !attempt.Ready && Time.realtimeSinceStartup < give) yield return null;
                if (attempt != null && Session.Current == attempt && attempt.Ready)
                {
                    Plugin.Log.LogInfo("[net] Reconnected");
                    Reconnecting = false;
                    reconnect = null;
                    yield break;
                }
                if (attempt != null && Session.Current == attempt) attempt.Stop();
                yield return new WaitForSecondsRealtime(3f);
            }
            Plugin.Log.LogWarning("[net] Couldn't reconnect; leaving the session");
            CancelReconnect();
        }

        // Gives up on the host: back home (own world or main menu), as after leaving.
        public void CancelReconnect()
        {
            if (!Reconnecting) return;
            Reconnecting = false;
            if (reconnect != null) StopCoroutine(reconnect);
            reconnect = null;
            Session.Current?.Stop();
            Steam.SteamLobby.Instance?.Leave();
            GuestWorld.ReturnHome();
        }

        // --mp-join=lobby: find the public lobby of this test run and join it.
        private System.Collections.IEnumerator JoinFirstPublicLobby()
        {
            var lobby = Steam.SteamLobby.Instance;
            if (lobby == null) yield break;
            float until = Time.realtimeSinceStartup + 60f;
            while (Time.realtimeSinceStartup < until)
            {
                lobby.Search();
                while (lobby.Searching) yield return null;
                if (lobby.Found.Count > 0) break;
                yield return new WaitForSecondsRealtime(2f);
            }
            if (lobby.Found.Count == 0) { Plugin.Log.LogWarning("[steam] No public game found"); yield break; }
            Plugin.Log.LogInfo($"[steam] Found {lobby.Found[0].HostName}'s lobby");
            lobby.Join(lobby.Found[0].Id);
        }

        // --mp-join-later=ip:port: play our own world first, then join (tests the in-game join).
        private System.Collections.IEnumerator JoinWhenInGame(string target)
        {
            while (!Game.InGame) yield return null;
            yield return new WaitForSecondsRealtime(2f);
            string[] parts = target.Split(':');
            int port = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : Plugin.Settings.Port.Value;
            StartJoin(parts[0], port, MPWindow.DefaultName(), ArgTransport);
        }

        // --mp-join=lan: search the LAN and join the first game found.
        private System.Collections.IEnumerator JoinFirstLanGame()
        {
            var lan = new LanDiscovery();
            float until = Time.realtimeSinceStartup + 60f;
            while (lan.Found.Count == 0 && Time.realtimeSinceStartup < until)
            {
                lan.Probe(Args.Int("lan-port", Plugin.Settings.Port.Value));
                float wait = Time.realtimeSinceStartup + 1f;
                while (Time.realtimeSinceStartup < wait) { lan.Poll(); yield return null; }
            }
            lan.Stop();
            if (lan.Found.Count == 0) { Plugin.Log.LogWarning("[net] No LAN game found"); yield break; }
            var f = lan.Found[0];
            Plugin.Log.LogInfo($"[net] Found {f.name}'s game at {f.endPoint}");
            StartJoin(f.endPoint.Address.ToString(), f.endPoint.Port, MPWindow.DefaultName());
        }

        // --mp-transport=lan (default) | steam | steamip
        public static TransportKind ArgTransport =>
            Args.Get("transport") == "steam" ? TransportKind.SteamP2P : Args.Get("transport") == "steamip" ? TransportKind.SteamIP : TransportKind.Lan;

        public bool StartHost(int port, string name, TransportKind kind = TransportKind.Lan)
        {
            if (Reconnecting) CancelReconnect();
            lastJoin = null;
            ITransport t = kind == TransportKind.SteamP2P ? SteamTransport.P2P()
                : kind == TransportKind.SteamIP ? (ITransport)SteamTransport.OverIp(null, port)
                : new LiteNetTransport(null, port, true);
            var s = Session.Host(LagTransport.Maybe(t), name);
            if (s == null) return false;
            if (kind != TransportKind.SteamP2P)
            {
                Plugin.Settings.Port.Value = port;
                Plugin.Settings.Save();
            }
            Attach(s);
            return true;
        }

        // target: "ip[:port]" for LAN/IP, a SteamID64 for Steam.
        public bool StartJoin(string target, int port, string name, TransportKind kind = TransportKind.Lan, bool retry = false)
        {
            if (!retry)
            {
                if (Reconnecting) { Reconnecting = false; if (reconnect != null) StopCoroutine(reconnect); reconnect = null; }
                lastJoin = (target, port, name, kind);
            }
            ITransport t;
            if (kind == TransportKind.SteamP2P)
            {
                if (!ulong.TryParse(target, out ulong sid)) { Plugin.Log.LogError("[net] Not a Steam id: " + target); return false; }
                t = SteamTransport.P2P(new Steamworks.CSteamID(sid));
            }
            else if (kind == TransportKind.SteamIP) t = SteamTransport.OverIp(target, port);
            else t = new LiteNetTransport(target, port, false);
            var s = Session.Join(LagTransport.Maybe(t), name, kind == TransportKind.SteamP2P ? "Steam friend" : $"{target}:{port}");
            if (s == null) return false;
            if (kind == TransportKind.Lan)
            {
                Plugin.Settings.LastAddress.Value = target;
                Plugin.Settings.Save();
            }
            Attach(s);
            return true;
        }

        // Tests (a fresh world between chained scenarios): everything that rides on the session is taken down before
        // the world is swapped and built again on the same connection once the new scene is up (Reattach).
        public void Detach()
        {
            var s = Session.Current;
            if (s == null) return;
            Presence?.Clear();
            Steam.Voice.Instance?.Stop();
            s.ResetSubscribers();
            Chat.Hook(s);   // the other copies' signals keep coming while we're between worlds
            Presence = null;
            Enemies = null;
            World = null;
            WorldStateSync = null;
            Objects = null;
            Loot = null;
            Estate = null;   // (it sets the estate flags: in the new world only once WorldState is there to send them)
            Sync.Objects.StashSync.ResetInstance();
            KeyItemSync.ResetInstance();
            SaveSync.ResetInstance();
            GuestWorld.ResetInstance();
            detached = true;
        }

        public void Reattach()
        {
            var s = Session.Current;
            detached = false;
            if (s != null) Attach(s);
        }

        private bool detached;

        // Everything that rides on a session.
        private void Attach(Session s)
        {
            s.Ended += () => OnSessionEnded(s);   // first: the other Ended handlers check Reconnecting
            GuestWorld.ResetInstance();
            Presence = new Presence(s);
            Enemies = new EnemySync(s, Presence);
            new EnemyFx(s, Presence);
            new EnemyCombat(s, Presence);
            new EggSync(s, Presence);
            new BossSync(s, Presence);
            new TrackerSync(s, Presence);
            new PlayerFx(s, Presence);
            new FishingSync(s, Presence);
            new StatusFx(s, Presence);
            new TeamSounds(s, Presence);
            new TeamFx(s, Presence);
            World = new HostWorld(s, Presence);
            new GuestWorld(s);
            WorldStateSync = new WorldState(s);
            Objects = new Sync.Objects.ObjectSync(s);
            new Sync.Objects.StashSync(s);
            new KeyItemSync(s);
            new SaveSync(s);
            new Steam.Voice(s);
            Loot = new LootRules(s);
            new Pings(s);
            new TeamTeleport(s);
            Estate = new EstateRules(s);
            new EstateSync(s);
            new PartyStatus(s);
            new PartyWipe(s);
            Chat.Hook(s);
            Deaths.Hook(s);
            WorldMoments.Hook(s);
            new Api.ApiNet(s);   // last: other mods' handlers see every sync in place
        }

        private void Update()
        {
            Watchdog.Beat();
            Steam.SteamBootstrap.RunCallbacks();
            if (Game.InGame && Session.Current == null && Steam.SteamLobby.Instance != null)
            {
                if (pendingLobbyInfo != null)
                {
                    var info = pendingLobbyInfo;
                    pendingLobbyInfo = null;
                    ConnectToLobbyHost(info);
                }
                else if (pendingLobby.IsValid())
                {
                    Steam.SteamLobby.Instance.Join(pendingLobby);   // started from an invite
                    pendingLobby = Steamworks.CSteamID.Nil;
                }
            }
            DevHarness.ApplyTimeScale();
            DevHarness.ApplyLoadingFps();
            DevHarness.SkipDialogue();
            if (!wasInGame && Game.InGame)
                Plugin.Log.LogInfo($"[scene] in game ({Game.SceneName}/{Game.AreaName}) at {Time.realtimeSinceStartup:F1}s");
            if (Game.InGame) GuestWorld.OnSceneReady();
            if (Game.SceneName == Game.MainMenuScene && GuestWorld.Switching && !(Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading))
                GuestWorld.OnSceneReady();
            wasInGame = Game.InGame;
            Window.Update();
            ChatBox.Update();
            Deaths.Update();
            Menu.Update();
            PauseMenu.Update();
            var s = Session.Current;
            // The host's world is about to be changed by others too: a copy of it (and the host's character) first.
            if (s != null && s.IsHost && backedUpFor != s && Game.InGame && GameData.HasWorldData)
            {
                backedUpFor = s;
                GuestWorld.BackupHostSaves();
            }
            if (s != null)
            {
                s.Poll();
                Presence?.Update();
                StatusFx.Instance?.Update();
                TeamSounds.Instance?.Update();
                TeamFx.Instance?.Update();
                Enemies?.Update();
                EnemyFx.Instance?.Update();
                EnemyCombat.Instance?.Update();
                EggSync.Instance?.Update();
                BossSync.Instance?.Update();
                TrackerSync.Instance?.Update();
                FishingSync.Instance?.Update();
                GuestWorld.Instance?.Update();
                WorldStateSync?.Update();
                Loot?.Update();      // before Objects: it forgets node states of freshly loaded scenes
                Objects?.Update();
                Sync.Objects.StashSync.Instance?.Update();
                KeyItemSync.Instance?.Update();
                SaveSync.Instance?.Update();
                ClassLessons.Update();
                Estate?.Update();
                EstateSync.Instance?.Update();
                World?.Update();
                Steam.Voice.Instance?.Update();
                Pings.Instance?.Update();
                TeamTeleport.Instance?.Update();
                PartyStatus.Instance?.Update();
                PartyWipe.Instance?.Update();
            }
            else if (Presence != null && !detached)
            {
                Presence.Clear();
                Presence = null;
                Enemies = null;
                World = null;
            }
            Api.Multiplayer.Update();   // other mods' events, and the API's own work while in a session
            PartyHud.Update();
            Tutorials.Update();
            if (Args.QuitAfter > 0f && !quitting && Time.realtimeSinceStartup - startTime > Args.QuitAfter)
            {
                quitting = true;
                SelfTest.Report(false, "time limit (--mp-quit) reached");
                DevHarness.Quit("--mp-quit time limit");
            }
        }

        // Co-op never stops the clock: the host's pause or death screen would freeze every enemy for everyone.
        private void LateUpdate()
        {
            if (Time.timeScale == 0f && Presence != null && Session.Current != null && Session.Current.Ready &&
                Presence.Remotes.Values.Any(r => r.Latest != null && r.Latest.Scene == Game.SceneName))
                Time.timeScale = Args.TimeScale;   // (1 but in tests run faster: --mp-timescale)
            DevHarness.AutoContinueDeathScreen();
            PartyHud.LateUpdate();
        }

        private static GUIStyle noticeStyle;

        private void OnGUI()
        {
            if (Reconnecting)
            {
                if (noticeStyle == null)
                    noticeStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.UpperCenter, normal = { textColor = new Color(1f, 0.85f, 0.4f) } };
                GUI.Label(new Rect(0, 12, Screen.width, 30), "Connection to the host lost: reconnecting... (pause menu: Multiplayer to stop)", noticeStyle);
            }
            Presence?.OnGUI();
            StatusFx.Instance?.OnGUI();
            if (Session.Current != null) PingMarkers.OnGUI();
            ChatBox.OnGUI();
            DeathBanner.OnGUI();
            Window.OnGUI();
        }

        private void OnApplicationQuit()
        {
            Session.Current?.Stop();
        }
    }
}
