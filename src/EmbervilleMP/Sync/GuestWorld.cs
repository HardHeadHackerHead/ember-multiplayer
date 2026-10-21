using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // "Host's world, guests' characters". A guest keeps its own character (.evp) and plays in the host's world:
    // the host sends its world, map and estate as they are now; the guest loads them under a throw-away world
    // name and never writes them to disk (SaveGuard). Leaving the session takes the guest back to its own world (to
    // the main menu if it came from there, or left through the game's Main Menu button: see MPRunner.LeaveToMainMenu).
    internal class GuestWorld
    {
        public const string GuestPrefix = "mp_guest_world";

        public static GuestWorld Instance { get; private set; }
        // True while this copy plays in someone else's world: world files must not be written.
        public static bool Active { get; private set; }
        // Between leaving one world and arriving in the other: the game's save-on-scene-change must not run
        // (we save explicitly on the way, and the reset managers can't save anyway).
        public static bool Switching { get; private set; }
        private static int switchFromScene;

        private static void BeginSwitch()
        {
            Tutorials.OnWorldSwitch();
            Switching = true;
            switchFromScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        }
        public static int ReturnsHome, Rejoins;

        public static void BackupHostSaves()
        {
            GameData.SaveData();
            Backups.Make("before hosting", GameData.playerFilePrefix, GameData.GetWorldPrefix);
        }
        public int SnapshotsSent, SnapshotsLoaded;
        public string HostScene { get; private set; }

        private readonly Session session;
        // the guest's own world, to go back to (null: joined from the menu); kept across a reconnect
        private static string ownWorldPrefix;

        public static void ResetInstance() => Instance = null;

        public GuestWorld(Session session)
        {
            Instance = this;
            this.session = session;
            session.On(Msg.WorldRequest, OnRequest);
            session.On(Msg.WorldSnapshot, OnSnapshot);
            session.Welcomed += () =>
            {
                if (Args.Has("own-world")) return; // tests of stage 1/2 keep their own worlds
                Plugin.Log.LogInfo("[world] Asking the host for its world");
                session.Send(Msg.WorldRequest, null);
            };
            session.Ended += () => { if (!MPRunner.Instance.Reconnecting) ReturnHome(); };
        }

        // ---- host ----

        private readonly System.Collections.Generic.List<byte> requests = new System.Collections.Generic.List<byte>();

        // Answered once the host is in a playable scene (not on its menu, not mid-load).
        private void OnRequest(byte from, NetDataReader r)
        {
            if (session.IsHost && !requests.Contains(from)) requests.Add(from);
        }

        private void AnswerRequests()
        {
            bool loading = Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading;
            if (requests.Count == 0 || !Game.InGame || loading) return;
            foreach (byte id in requests.ToArray()) SendWorld(id);
            requests.Clear();
        }

        private void SendWorld(byte from)
        {
            if (!session.Players.ContainsKey(from)) return;
            var snapshot = Capture();
            var p = Game.LocalPlayer;
            session.SendTo(from, Msg.WorldSnapshot, w =>
            {
                w.Put(Game.SceneName);
                w.Put(Game.AreaName);
                w.Put(p != null ? p.transform.position.x : 0f);
                w.Put(p != null ? p.transform.position.y : 0f);
                w.PutBytesWithLength(snapshot);
            });
            SnapshotsSent++;
            Plugin.Log.LogInfo($"[world] Sent the world to #{from} ({snapshot.Length / 1024} KB)");
        }

        // The world as the host has it now: the game's own save listeners first refresh it from the live scene.
        public static byte[] Capture()
        {
            GameData.CallSaveListeners();
            GameData.worldData.bools = GameData.gameBools.ToGameBoolList();
            // (an unbuilt live map has nothing to save: then the saved copy is the world's map)
            var map = (Singleton<WorldMap>.HasMain ? Singleton<WorldMap>.main.WorldMapState.GetSavedData() : null) ?? GameData.worldMapData;
            var w = new NetDataWriter();
            w.Put(JsonUtility.ToJson(GameData.worldData), 0);
            w.Put(map != null ? JsonUtility.ToJson(map) : "", 0);
            w.Put(JsonUtility.ToJson(GameData.playerEstateData ?? new PlayerEstateData()), 0);
            return Zip(w.CopyData());
        }

        // ---- guest ----

        private byte[] pending;   // a snapshot waiting for the game to be idle (in game, or on the main menu)

        private void OnSnapshot(byte from, NetDataReader r)
        {
            if (session.IsHost) return;
            pending = r.GetRemainingBytes();
        }

        public void Update()
        {
            if (session.IsHost) { AnswerRequests(); return; }
            if (pending == null) return;
            bool loading = Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading;
            bool idleMenu = Game.SceneName == Game.MainMenuScene && !loading && GameData.HasPlayerData;
            if (!(Game.InGame && !loading) && !idleMenu) return;
            var bytes = pending;
            pending = null;
            Apply(new NetDataReader(bytes));
        }

        private void Apply(NetDataReader r)
        {
            string scene = r.GetString();
            if (scene == Game.MainMenuScene || scene == Game.SplashScene || string.IsNullOrEmpty(scene))
            {
                Plugin.Log.LogWarning($"[world] Host isn't in a playable scene ({scene}); asking again");
                session.Send(Msg.WorldRequest, null);
                return;
            }
            string area = r.GetString();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
            var data = new NetDataReader(Unzip(r.GetBytesWithLength()));
            string worldJson = data.GetString(0), mapJson = data.GetString(0), estateJson = data.GetString(0);

            // Rejoining after a lost connection: the host's world as it is now, but we stay where we are.
            if (Active && Game.InGame && Game.LocalPlayer != null)
            {
                scene = Game.SceneName;
                area = Game.AreaName;
                pos = Game.LocalPlayer.transform.position;
                Rejoins++;
                // The reload skips the scene-change save: what the character has now (gold, items from rewards...)
                // must reach its data first, or the new scene loads the older one.
                SaveCharacter();
                WorldState.CarryQuests();   // what our quests did while cut off survives the reload
                KeyItemSync.Carry();        // and the key items we got or used meanwhile
            }
            else
            {
                pos += new Vector3(1.2f, 0f, 0f);
                WorldState.DropCarried();
            }
            // Our own world first, as it is now (then nothing of it is touched while we're away).
            if (!Active)
            {
                Backups.Make("before joining", GameData.playerFilePrefix);
                if (GameData.HasWorldData && Game.InGame)
                {
                    GameData.SaveData();
                    ownWorldPrefix = GameData.GetWorldPrefix;
                    Tutorials.Seed(GameData.worldData.tutorialsDone);   // seen here: seen by this character
                }
                else ownWorldPrefix = null;
            }
            Active = true;
            GameData.SetWorldPrefix(GuestPrefix);
            GameData.worldData = JsonUtility.FromJson<WorldData>(worldJson);
            GameData.worldMapData = string.IsNullOrEmpty(mapJson) ? null : JsonUtility.FromJson<WorldMapState.SavedData>(mapJson);
            GameData.playerEstateData = JsonUtility.FromJson<PlayerEstateData>(estateJson) ?? new PlayerEstateData();
            GameData.gameBools = GameData.worldData.bools.ToHookedDictionary();
            MapSync.LoadWorldMap(GameData.worldMapData);
            SnapshotsLoaded++;
            HostScene = scene;
            Plugin.Log.LogInfo($"[world] Loaded the host's world; going to {scene}/{area}");
            Travel(scene, area, pos);
        }

        // Loads a scene of the current world at a spot (the game's own "continue from save" path).
        public static void Travel(string scene, string area, Vector3 pos, bool newWorld = true)
        {
            var wd = GameData.worldData;
            wd.lastActiveScene = scene;
            wd.lastGameArea = area;
            wd.lastSpawnPosition = pos;
            wd.startFromSavedData = true;
            GameData.teleporterID = "";
            try { MainInput.Actions.Player.Disable(); } catch { }
            if (!newWorld)
            {
                // Same world, another scene: the game's normal scene change (it saves on the way out).
                Singleton<SceneLoader>.main.CallScene(scene, () => { try { MainInput.Actions.Enable(); } catch { } }).Forget();
                return;
            }
            BeginSwitch();
            Singleton<SceneLoader>.main.CallScene(scene, ResetWorldManagers).Forget();
        }

        // The new world's scene is up.
        public static void OnSceneReady()
        {
            if (returnHomeWhenLoaded && !(Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading))
            {
                returnHomeWhenLoaded = false;
                if (Session.Current == null)   // (not if we joined again meanwhile)
                {
                    Switching = false;
                    ReturnHome();
                    return;
                }
                toMenuWhenLoaded = false;
            }
            if (Switching && UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != switchFromScene) Switching = false;
        }

        // Tests: swap in a brand-new world (same seed and difficulty as the current one) without leaving the session.
        // hostPrefix: the host's save name for it (null on a guest: it keeps playing under the throw-away name).
        public static void PrepareFreshWorld(string hostPrefix)
        {
            var old = GameData.worldData;
            var world = new WorldData
            {
                Name = old?.Name ?? "Test world",
                createdTime = System.DateTime.Now.ToFileTimeUtc(),
                MapSeed = old != null ? old.MapSeed : 12345,
                difficultyID = old != null ? old.difficultyID : (short)1,
            };
            if (hostPrefix != null)
            {
                string saves = Path.GetDirectoryName(SaveLoad.GetPath("x"));
                foreach (var f in Directory.GetFiles(saves, hostPrefix + "*")) File.Delete(f);
                GameData.SetWorldPrefix(hostPrefix);
            }
            GameData.worldData = world;
            GameData.worldMapData = null;
            GameData.playerEstateData = new PlayerEstateData();
            GameData.gameBools = world.bools.ToHookedDictionary();
            MapSync.LoadWorldMap(null);
        }

        // Loads a scene of the world just prepared, as the game's "continue" does for a new world.
        public static void LoadFreshWorld(string scene)
        {
            var wd = GameData.worldData;
            wd.lastActiveScene = scene;
            wd.startFromSavedData = true;
            GameData.teleporterID = "";
            try { MainInput.Actions.Player.Disable(); } catch { }
            BeginSwitch();
            Singleton<SceneLoader>.main.CallScene(scene, ResetWorldManagers).Forget();
        }

        // What the main menu resets between worlds (state that outlives scenes).
        private static void ResetWorldManagers()
        {
            QuestManager.model = null;
            if (Singleton<TimeManager>.HasMain) UnityEngine.Object.Destroy(Singleton<TimeManager>.main);
            PoolManager.ClearAllPools();
            try { MainInput.Actions.Enable(); } catch { }
            Time.timeScale = 1f;   // (as the game's own scene calls do: we may leave from its pause menu)
        }

        // Session over: back to our own world (or the main menu if we came from there, or asked for it).
        private static bool returnHomeWhenLoaded, toMenuWhenLoaded;

        // Set while the player leaves through the game's Main Menu button (MPRunner.LeaveToMainMenu): home is the menu.
        public static bool ReturnToMenu;

        public static void ReturnHome()
        {
            if (!Active) return;
            bool toMenu = ReturnToMenu || toMenuWhenLoaded;
            // Mid-load (a rejoin's world reload): two scene loads at once break the loader; go once it's done.
            if (Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading)
            {
                returnHomeWhenLoaded = true;
                toMenuWhenLoaded = toMenu;
                return;
            }
            returnHomeWhenLoaded = false;
            toMenuWhenLoaded = false;
            Active = false;
            WorldState.DropCarried();
            if (Game.SceneName == Game.MainMenuScene)
            {
                // Already there, the game's own way (MPRunner's safety net): its scene-change save wrote the character
                // on the way out and SaveGuard kept the host's world off the disk. Nothing to load; the character data
                // in memory may be the menu's by now, so nothing is saved here either.
                Plugin.Log.LogInfo("[world] Session over on the main menu");
                MapSync.LoadWorldMap(null);
                ReturnsHome++;
                return;
            }
            if (ownWorldPrefix == null || toMenu)
            {
                Plugin.Log.LogInfo(toMenu ? "[world] Session over: to the main menu, as asked" : "[world] Session over: back to the main menu");
                SaveCharacter();
                MapSync.LoadWorldMap(null);
                BeginSwitch();
                ReturnsHome++;
                Singleton<SceneLoader>.main.CallScene(Game.MainMenuScene, ResetWorldManagers).Forget();
                return;
            }
            Plugin.Log.LogInfo("[world] Session over: back to our own world");
            SaveCharacter();
            GameData.SetWorldPrefix(ownWorldPrefix);
            GameData.LoadData();
            MapSync.LoadWorldMap(GameData.worldMapData);
            var wd = GameData.worldData;
            wd.startFromSavedData = true;
            string scene = string.IsNullOrEmpty(wd.lastActiveScene) ? Game.FallbackStartScene : wd.lastActiveScene;
            BeginSwitch();
            ReturnsHome++;
            Singleton<SceneLoader>.main.CallScene(scene, ResetWorldManagers).Forget();
        }

        // The character as it is now, to disk (the live inventory and gold only reach the character data through the
        // game's save listeners, which a world switch skips).
        internal static void SaveCharacter()
        {
            if (Game.InGame) GameData.CallSaveListeners();
            GameData.SavePlayer();
        }

        private static byte[] Zip(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        private static byte[] Unzip(byte[] zipped)
        {
            using (var input = new MemoryStream(zipped))
            using (var gz = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gz.CopyTo(output);
                return output.ToArray();
            }
        }
    }

    // While playing in the host's world, nothing of that world reaches the disk (the guest's own worlds are
    // never touched either: the world prefix is a throw-away one). The character file still saves normally.
    [HarmonyPatch(typeof(GameData), nameof(GameData.SaveData))]
    internal static class SaveDuringSwitchPatch
    {
        private static bool Prefix()
        {
            if (!GuestWorld.Switching) return true;
            Plugin.Log.LogInfo("[world] Skipping the scene-change save while switching worlds");
            return false;
        }
    }

    [HarmonyPatch(typeof(SaveLoad), nameof(SaveLoad.Save))]
    internal static class SaveGuardPatch
    {
        private static bool Prefix(string fileName) => !GuestWorld.Active || !fileName.StartsWith(GuestWorld.GuestPrefix);
    }

    [HarmonyPatch(typeof(SaveLoad), nameof(SaveLoad.SaveAsync))]
    internal static class SaveGuardAsyncPatch
    {
        private static bool Prefix(string fileName, ref Cysharp.Threading.Tasks.UniTask __result)
        {
            if (!GuestWorld.Active || !fileName.StartsWith(GuestWorld.GuestPrefix)) return true;
            __result = Cysharp.Threading.Tasks.UniTask.CompletedTask;
            return false;
        }
    }

    [HarmonyPatch(typeof(SaveLoad), nameof(SaveLoad.Delete))]
    internal static class DeleteGuardPatch
    {
        private static bool Prefix(string fileName, ref bool __result)
        {
            if (!GuestWorld.Active || !fileName.StartsWith(GuestWorld.GuestPrefix)) return true;
            __result = false;
            return false;
        }
    }
}
