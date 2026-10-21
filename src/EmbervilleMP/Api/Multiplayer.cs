using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Api
{
    // The multiplayer mod's public API for other mods (docs/API.md). Guarantees, for a given ApiVersion:
    //   - nothing here changes meaning or signature;
    //   - every call into a mod (events, channel handlers, object kinds, spawn factories) is try/caught and logged
    //     under that mod's id: a mod's bug never breaks the session;
    //   - main thread only (Unity's); a call from another thread is logged and ignored;
    //   - everything is safe outside a session: no-ops, empty lists, fallbacks.
    public static partial class Multiplayer
    {
        public const int ApiVersion = 1;

        // ---------------- the session ----------------

        // The multiplayer mod is loaded and running.
        public static bool Available => MPRunner.Instance != null;
        public static bool InSession => Session.Current != null && Session.Current.Ready;
        public static bool IsHost => InSession && Session.Current.IsHost;
        public static bool IsGuest => InSession && !Session.Current.IsHost;
        // The session's world (the host's, on every copy); "" outside a session.
        public static string WorldName => InSession && GameData.worldData != null ? GameData.worldData.Name ?? "" : "";
        // For mods declared [MultiplayerMod(Requirement.Incompatible)]: turn yourself off while this is true.
        public static bool ShouldBeOff => InSession;
        // A text field of ours has the keyboard (chat line, F8 window): don't treat key presses as hotkeys.
        public static bool TextFieldOpen => MPRunner.Instance != null && (MPRunner.Instance.ChatBox.Typing || MPRunner.Instance.Window.Open);

        // ---------------- players ----------------

        private static Session cacheFor;
        private static readonly Dictionary<byte, MpPlayer> cache = new Dictionary<byte, MpPlayer>();

        private static Dictionary<byte, MpPlayer> Cache()
        {
            var s = Session.Current;
            if (s != cacheFor) { cache.Clear(); cacheFor = s; }
            return cache;
        }

        // Everyone in the session, us included, by id (empty outside a session).
        public static IReadOnlyList<MpPlayer> Players
        {
            get
            {
                var s = Session.Current;
                if (s == null || !s.Ready) return new List<MpPlayer>();
                return s.Players.Keys.OrderBy(id => id).Select(GetPlayer).Where(p => p != null).ToList();
            }
        }

        public static MpPlayer LocalPlayer => InSession ? GetPlayer(Session.Current.LocalId) : null;

        public static MpPlayer GetPlayer(byte id)
        {
            var s = Session.Current;
            if (s == null || !s.Ready || !s.Players.TryGetValue(id, out var info)) return null;
            var c = Cache();
            if (!c.TryGetValue(id, out var p) || p.Name != info.Name) c[id] = p = new MpPlayer(id, info.Name, id == s.LocalId);
            return p;
        }

        // Whose character (or puppet) this is, or a part of it; null if nobody's.
        public static MpPlayer PlayerOf(GameObject go)
        {
            if (go == null || !InSession) return null;
            var me = Game.LocalPlayer;
            if (me != null && (go == me.gameObject || go.transform.IsChildOf(me.transform))) return LocalPlayer;
            var presence = MPRunner.Instance?.Presence;
            if (presence != null)
                foreach (var rp in presence.Remotes.Values)
                    if (rp.Puppet != null && (go == rp.Puppet || go.transform.IsChildOf(rp.Puppet.transform))) return GetPlayer(rp.Info.Id);
            return null;
        }

        // Players in our scene, us included (as each last reported it).
        public static IReadOnlyList<MpPlayer> PlayersInMyScene()
        {
            if (!InSession || !Game.InScene) return new List<MpPlayer>();
            string scene = Game.SceneName;
            return Players.Where(p => p.IsLocal || p.Scene == scene).ToList();
        }

        // Players in our scene within radius of pos, us included, nearest first.
        public static IReadOnlyList<MpPlayer> PlayersNear(Vector2 pos, float radius) =>
            PlayersInMyScene().Where(p => (p.Position - pos).sqrMagnitude <= radius * radius).OrderBy(p => (p.Position - pos).sqrMagnitude).ToList();

        // ---------------- who decides ----------------

        // This copy runs its scene (enemies, rooms, decisions): true outside a session too.
        public static bool RunsThisScene => !InSession || EnemySync.IsAuthority;

        // Who runs our scene (us outside a session: null then).
        public static MpPlayer SceneRunner
        {
            get
            {
                if (!InSession) return null;
                byte id = SceneAuthority.Mine;
                return id != 0 ? GetPlayer(id) : LocalPlayer;
            }
        }

        // Another player's puppet here (or a part of one).
        public static bool IsPuppet(GameObject go)
        {
            var p = PlayerOf(go);
            return p != null && !p.IsLocal;
        }

        // An enemy shown from another copy's simulation (we don't run it: hitting it is sent to its runner).
        public static bool IsPuppetEnemy(Enemy e) => e != null && InSession && EnemySync.Instance != null && EnemySync.Instance.IsPuppet(e);

        // ---------------- events (main thread; each handler on its own, errors logged under its mod) ----------------

        public static event Action SessionStarted;
        public static event Action SessionEnded;
        // Another player joined (a guest that just joined hears of the players already there through SessionStarted
        // and Players, not this).
        public static event Action<MpPlayer> PlayerJoined;
        public static event Action<MpPlayer> PlayerLeft;
        public static event Action<MpPlayer> PlayerDied;
        public static event Action<MpPlayer> PlayerRespawned;
        // A player (us included) arrived in a scene (not raised for "between scenes").
        public static event Action<MpPlayer> PlayerChangedScene;
        // While in a session: this copy started / stopped running the scene it's in.
        public static event Action BecameSceneRunner;
        public static event Action StoppedRunningScene;
        // A guest is about to swap worlds (into the host's world when joining, home when leaving): what's in the scene
        // goes away.
        public static event Action WorldSwitching;

        private static void Raise(Action e, string name) { if (e != null) ApiGuard.Raise(e, name, d => ((Action)d)()); }
        private static void Raise(Action<MpPlayer> e, string name, MpPlayer p) { if (e != null && p != null) ApiGuard.Raise(e, name, d => ((Action<MpPlayer>)d)(p)); }

        private static bool welcomed, wasRunner, wasSwitching, deathsHooked;
        private static readonly Dictionary<byte, string> sceneOf = new Dictionary<byte, string>();

        internal static void OnAttached(Session s, bool again)
        {
            if (!deathsHooked)
            {
                deathsHooked = true;
                Deaths.Noticed += (who, ev) =>
                {
                    if (ev == Deaths.Event.Died) Raise(PlayerDied, "PlayerDied", GetPlayer(who));
                    else if (ev == Deaths.Event.Up) Raise(PlayerRespawned, "PlayerRespawned", GetPlayer(who));
                };
            }
            s.PlayerJoined += p => { if (welcomed) Raise(PlayerJoined, "PlayerJoined", GetPlayer(p.Id)); };
            s.PlayerLeft += p =>
            {
                var c = Cache();
                if (!c.TryGetValue(p.Id, out var mp)) mp = new MpPlayer(p.Id, p.Name, false);
                c.Remove(p.Id);
                sceneOf.Remove(p.Id);
                Raise(PlayerLeft, "PlayerLeft", mp);
            };
            if (again) return;
            welcomed = false;
            wasRunner = false;
            sceneOf.Clear();
            if (s.Ready) Started();
            else s.Welcomed += Started;
        }

        private static void Started()
        {
            if (welcomed) return;
            welcomed = true;
            wasRunner = RunsThisScene;
            Plugin.Log.LogInfo("[api] Session started");
            Raise(SessionStarted, "SessionStarted");
        }

        internal static void OnEnded(Session s)
        {
            if (!welcomed) return;
            welcomed = false;
            sceneOf.Clear();
            Raise(SessionEnded, "SessionEnded");
        }

        // Every frame (MPRunner): what the session's state changes into events.
        internal static void Update()
        {
            bool switching = GuestWorld.Switching;
            if (switching && !wasSwitching) Raise(WorldSwitching, "WorldSwitching");
            wasSwitching = switching;
            ApiNet.Instance?.Update();
            if (!InSession || !welcomed) return;
            foreach (var p in Players)
            {
                string scene = p.Scene;
                if (scene.Length == 0) continue;
                if (sceneOf.TryGetValue(p.Id, out var was) && was == scene) continue;
                sceneOf[p.Id] = scene;
                Raise(PlayerChangedScene, "PlayerChangedScene", p);
            }
            if (Game.InScene && !Loading)
            {
                bool runs = RunsThisScene;
                if (runs != wasRunner)
                {
                    wasRunner = runs;
                    if (runs) Raise(BecameSceneRunner, "BecameSceneRunner"); else Raise(StoppedRunningScene, "StoppedRunningScene");
                }
            }
        }

        private static bool Loading => GuestWorld.Switching || Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading;

        // ---------------- channels ----------------

        // A channel of your mod (id: unique, e.g. "mymod.ping"). Register it on every copy (in Awake), before
        // sessions start; the same id again returns the same channel.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static MpChannel RegisterChannel(string id, ChannelOptions options = null) =>
            ApiChannels.Register(id, ApiGuard.ModIdOf(Assembly.GetCallingAssembly()), options);

        // ---------------- shared data ----------------

        public static MpStore WorldStore(string modId) => ApiStores.Get(true, modId ?? "");
        public static MpStore SessionStore(string modId) => ApiStores.Get(false, modId ?? "");

        // ---------------- world objects ----------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RegisterObjectKind(MpObjectHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ApiObjects.Register(handler, ApiGuard.ModIdOf(Assembly.GetCallingAssembly()));
        }

        // The object changed here: everyone gets its state. Sent when we run the scene, or when byLocalPlayer (our
        // player did it); otherwise the runner's change is the one that counts.
        public static void ObjectChanged(MpObjectHandler handler, GameObject obj, bool byLocalPlayer = false)
        {
            if (ApiGuard.OnMainThread("Multiplayer.ObjectChanged")) ApiObjects.Changed(handler, obj, byLocalPlayer);
        }

        // Asks the scene's runner to do something to the object (its handler's OnIntent runs there; at once if that's us).
        public static void SendIntent(MpObjectHandler handler, GameObject obj, Action<NetDataWriter> write)
        {
            if (ApiGuard.OnMainThread("Multiplayer.SendIntent")) ApiObjects.Intent(handler, obj, write);
        }

        // ---------------- spawned objects ----------------

        // create: makes the object from the init data Spawn was given (its position is set after). state: a handler
        // for its state (also registered as an object kind). saved: kept with the host's world and back next session.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RegisterSpawnable(string prefabKey, Func<NetDataReader, GameObject> create, MpObjectHandler state = null, bool saved = false) =>
            ApiSpawns.Register(prefabKey, ApiGuard.ModIdOf(Assembly.GetCallingAssembly()), create, state, saved);

        // Spawns an object in our scene on every copy, with one net id. On the scene's runner it's made at once and
        // returned; elsewhere the runner is asked and null is returned (it arrives a moment later). Players arriving
        // later get it. Nothing outside a session (null).
        public static GameObject Spawn(string prefabKey, Vector2 position, Action<NetDataWriter> init = null) => ApiSpawns.Spawn(prefabKey, position, init);

        // Removes a spawned object everywhere (the runner decides: elsewhere it's asked).
        public static void Despawn(GameObject spawned) => ApiSpawns.Despawn(spawned);

        // A spawned object's net id (the same on every copy), 0 for anything else.
        public static int NetIdOf(GameObject go) => ApiSpawns.NetIdOf(go);

        // ---------------- compatibility ----------------

        // That player's mods declared with [MultiplayerMod] (empty if unknown or outside a session).
        public static IReadOnlyList<MpMod> ModsOf(MpPlayer player)
        {
            if (player == null || !InSession) return new List<MpMod>();
            if (player.IsLocal) return ApiMods.Local();
            return ApiNet.Instance?.ModsOf(player.Id) ?? new List<MpMod>();
        }

        // ---------------- helpers ----------------

        private static readonly MpChannel notify = ApiChannels.Register("mp.notify", ApiGuard.OurId, new ChannelOptions { MaxSize = 1024, Rate = 5f, Burst = 10f });
        private static readonly MpChannel announce = ApiChannels.Register("mp.announce", ApiGuard.OurId, new ChannelOptions { MaxSize = 1024, Rate = 2f, Burst = 6f });
        private static readonly Color AnnounceColour = new Color(0.72f, 0.86f, 1f);

        static Multiplayer()
        {
            notify.Received += (from, r) => Chat.Notice(r.GetString());
            announce.Received += (from, r) => Chat.AddSystem(r.GetString(), AnnounceColour);
        }

        // A line in our own chat only.
        public static void ChatMessage(string text)
        {
            if (!string.IsNullOrEmpty(text) && ApiGuard.OnMainThread("Multiplayer.ChatMessage")) Chat.Notice(text);
        }

        // A line in everyone's chat (ours too).
        public static void Announce(string text)
        {
            if (string.IsNullOrEmpty(text) || !ApiGuard.OnMainThread("Multiplayer.Announce")) return;
            Chat.AddSystem(text, AnnounceColour);
            announce.SendToAll(w => w.Put(text));
        }

        // A line in one player's chat.
        public static void Notify(MpPlayer player, string text)
        {
            if (player == null || string.IsNullOrEmpty(text)) return;
            notify.SendTo(player, w => w.Put(text));   // (ourselves: at once)
        }

        // In a fight: an enemy fighting near that player or after it in its room. Us: as the quick portal and teleport
        // decide it; another player: from what our copy knows of enemies chasing it (its scene must be ours).
        public static bool InCombat(MpPlayer player)
        {
            if (player == null) return false;
            if (player.IsLocal || !InSession)
            {
                var me = Game.LocalPlayer;
                return me != null && (TeamTeleport.Instance != null ? TeamTeleport.Instance.InCombat(me) : EnemyCombat.Fighting(me));
            }
            return EnemyCombat.Instance != null && EnemyCombat.Instance.Chases(player.Id);
        }

        // Teleports our character next to that player (the pause menu's "teleport to teammate": the host's rule, out of
        // combat, a cooldown). False and why not, or true: on its way.
        public static bool TeleportTo(MpPlayer player) => TeleportTo(player, out _);

        public static bool TeleportTo(MpPlayer player, out string why)
        {
            why = "Not in a multiplayer game.";
            if (player == null || TeamTeleport.Instance == null || !InSession) return false;
            return TeamTeleport.Instance.Request(player.Id, out why);
        }
    }
}
