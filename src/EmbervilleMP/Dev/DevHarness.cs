using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using Cysharp.Threading.Tasks;

namespace EmbervilleMP
{
    // Test/dev plumbing: per-instance save folders, skipping the menus, speed-ups, screenshots, dumps.
    internal static partial class DevHarness
    {
        public const string PlayerFile = "player_slot1";
        public const string WorldFile = "world_slot1";

        public static string OutDir
        {
            get
            {
                string dir = Args.Out ?? Path.Combine(BepInEx.Paths.BepInExRootPath, "mp-out");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static void ApplyRuntimeSettings()
        {
            Application.runInBackground = true;
            if (Args.Fps != 0)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = Args.Fps;
            }
            if (Args.Mute)
            {
                try { AkUnitySoundEngine.Suspend(false); }
                catch (Exception e) { Plugin.Log.LogWarning("Mute failed: " + e.Message); }
            }
        }

        // --mp-window=640x360: the game applies its saved resolution (often fullscreen) on load; tests want small windows.
        public static void ApplyWindowSize()
        {
            string spec = Args.Get("window");
            if (spec == null) return;
            string[] wh = spec.Split('x');
            if (wh.Length == 2 && int.TryParse(wh[0], out int w) && int.TryParse(wh[1], out int h))
                Screen.SetResolution(w, h, FullScreenMode.Windowed);
        }

        // --mp-skip-dialogue: closes conversations as soon as they open (tests start in Town without the tutorial,
        // so the town's intro conversation would otherwise hold the character still).
        public static bool HoldDialogue;   // a test reading a conversation on purpose

        public static void SkipDialogue()
        {
            if (!Args.Has("skip-dialogue") || HoldDialogue || !Singleton<DialogueManager>.HasMain) return;
            var dm = Singleton<DialogueManager>.main;
            if (dm.isPlaying && dm.dialogueWindow != null && dm.dialogueWindow.activeSelf)
            {
                Plugin.Log.LogInfo("[dev] Skipping dialogue");
                dm.EndDialogue();
            }
        }

        // Tests: deaths take a second or two instead of ~8 (see FastDeath.cs). A scenario that doesn't look at the
        // death animation or the death screen themselves sets it at its start; --mp-fast-death turns it on for all.
        public static bool FastDeath;

        // Before each scenario (chains): nothing a previous one asked for (or left set when it failed) carries over.
        public static void ResetForScenario()
        {
            FastDeath = false;
            HoldDialogue = false;
        }

        private static FaderManager fader;

        // Tests: press Continue on the death screen.
        public static void AutoContinueDeathScreen()
        {
            if (Args.Test == null) return;
            if (fader == null) fader = UnityEngine.Object.FindAnyObjectByType<FaderManager>();
            var f = fader;
            if (f != null && f.deadObject != null && f.deadObject.activeInHierarchy && f.deadGroup != null && f.deadGroup.interactable && f.continueButton != null)
            {
                Plugin.Log.LogInfo("[dev] Death screen: Continue");
                f.continueButton.onClick.Invoke();
            }
        }

        public static void ApplyTimeScale()
        {
            // The game sets 0 (pause) and short slow-motion values itself; only 1 (normal speed) gets sped up.
            if (Args.TimeScale != 1f && Mathf.Approximately(Time.timeScale, 1f))
                Time.timeScale = Args.TimeScale;
        }

        // Skips the main menu: makes a character and a world in this profile if there are none, then loads them.
        public static IEnumerator AutoStart()
        {
            while (SceneManager.GetActiveScene().name != "MainMenu" || !Singleton<SceneLoader>.HasMain)
                yield return null;
            // Leaving before localization has finished initializing deadlocks the next scene: its UI asks for
            // strings synchronously while the init is still running. A human in the menu never gets there that fast.
            var init = UnityEngine.Localization.Settings.LocalizationSettings.InitializationOperation;
            Plugin.Log.LogInfo($"[dev] Main menu at {Time.realtimeSinceStartup:F1}s, waiting for localization");
            while (!init.IsDone || OptionsPanel.Options == null)
                yield return null;
            Plugin.Log.LogInfo($"[dev] Localization ready at {Time.realtimeSinceStartup:F1}s");
            yield return new WaitForSecondsRealtime(0.5f);

            string name = Args.Name ?? Args.Profile ?? "Tester";
            if (!File.Exists(SaveLoad.GetPath(PlayerFile + ".evp")))
            {
                GameData.NewData();
                GameData.SetPlayerPrefix(PlayerFile);
                GameData.playerData.playerName = name;
                GameData.playerData.createdTime = DateTime.Today.ToFileTimeUtc();
                // hair, facial hair, extras, skin, hair colour, facial hair colour, clothes colour, body type
                var rnd = new System.Random(name.GetHashCode());
                GameData.playerData.playerSetup = new System.Collections.Generic.List<int>
                {
                    rnd.Next(0, 6), rnd.Next(0, 3), 0, rnd.Next(0, 9), rnd.Next(0, 9), 0, rnd.Next(0, 9), rnd.Next(0, 2)
                };
                GameData.SavePlayer();
                Plugin.Log.LogInfo($"[dev] Created character '{name}' setup={string.Join(",", GameData.playerData.playerSetup)}");
            }
            // A guest joining someone's world only needs its character: it waits on the menu for the host's world.
            if (Args.Join != null && !Args.Has("own-world") && !Args.Has("prepare"))
            {
                CharacterForJoining();
                Plugin.Log.LogInfo($"[dev] Character loaded; waiting on the menu for the host's world at {Time.realtimeSinceStartup:F1}s");
                yield break;
            }
            EnsureOwnWorld(name);
            if (Args.Has("prepare"))
            {
                // Saves exist now; the test drives the menus itself.
                GameData.NewData();
                Plugin.Log.LogInfo("[dev] Character and world ready; staying on the menu");
                yield break;
            }
            LoadOwnWorld(Args.AutoStartScene);
        }

        // On the main menu, before joining: our character loaded, no world (the host's comes).
        public static void CharacterForJoining()
        {
            GameData.NewData();
            GameData.SetPlayerPrefix(PlayerFile);
            GameData.LoadPlayer();
            GameData.worldData = null;
        }

        // This profile's own world file (made if there's none).
        private static void EnsureOwnWorld(string name)
        {
            if (File.Exists(SaveLoad.GetPath(WorldFile + ".evw"))) return;
            var panel = UnityEngine.Object.FindObjectsByType<WorldCreationPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            short difficulty = panel != null && panel.difficultySettings != null && panel.difficultySettings.Length > 1
                ? panel.difficultySettings[1].DifficultyID : (short)1;
            var world = new WorldData
            {
                Name = name + "'s world",
                createdTime = DateTime.Now.ToFileTimeUtc(),
                MapSeed = Args.Seed != 0 ? Args.Seed : 12345,
                difficultyID = difficulty,
            };
            GameData.SetWorldPrefix(WorldFile);
            GameData.worldData = world;
            GameData.SaveWorld();
            Plugin.Log.LogInfo($"[dev] Created world '{world.Name}' difficulty={difficulty}");
        }

        // From the main menu into our own character and world (made if needed), as "continue" does.
        public static void LoadOwnWorld(string scene)
        {
            EnsureOwnWorld(Args.Name ?? Args.Profile ?? "Tester");
            GameData.NewData();
            GameData.SetPlayerPrefix(PlayerFile);
            GameData.LoadPlayer();
            GameData.SetWorldPrefix(WorldFile);
            GameData.LoadData();
            if (string.IsNullOrEmpty(scene)) scene = GameData.worldData.lastActiveScene;
            if (string.IsNullOrEmpty(scene)) scene = "TutorialArea";
            GameData.worldData.startFromSavedData = true;
            MainInput.Actions.UI.Disable();
            QuickLoadBegins();
            Plugin.Log.LogInfo($"[dev] Autostart -> {scene} at {Time.realtimeSinceStartup:F1}s");
            Singleton<SceneLoader>.main.CallScene(scene, null).Forget();
        }

        // Chained tests: every copy switches to a brand-new world (same seed and difficulty) and reloads the scene, at
        // the same time and without leaving the session; the syncs on the session are built again from scratch.
        private static int freshWorlds;

        // Where this copy first got in game (the chain's start), for fresh worlds to put the player back.
        private static string firstSpawnArea;
        private static Vector3 firstSpawnPos;

        public static void NoteFirstSpawn()
        {
            if (firstSpawnArea != null || !Game.InGame || Game.LocalPlayer == null) return;
            firstSpawnArea = Game.AreaName;
            firstSpawnPos = Game.LocalPlayer.transform.position;
            Plugin.Log.LogMessage("[MP] TIME in-game " + Time.realtimeSinceStartup.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s");
        }

        public static IEnumerator FreshWorld()
        {
            while (Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading) yield return null;
            yield return SettleBeforeFreshWorld();
            QuickTransition = QuickOn;
            freshWorlds++;
            var s = Session.Current;
            bool host = s == null || s.IsHost;
            string scene = Args.AutoStartScene ?? "TutorialArea";
            Plugin.Log.LogInfo($"[dev] Fresh world #{freshWorlds} -> {scene} at {Time.realtimeSinceStartup:F1}s");   // (the times: where a fresh world spends them)
            MPRunner.Instance.Detach();
            GuestWorld.PrepareFreshWorld(host ? WorldFile + "_chain" + freshWorlds : null);
            GuestWorld.LoadFreshWorld(scene);
            float until = Time.realtimeSinceStartup + 120f;
            yield return null;
            while (Time.realtimeSinceStartup < until && (GuestWorld.Switching || !Game.InGame ||
                   (Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading)))
                yield return null;
            Plugin.Log.LogInfo($"[dev] Fresh world in game at {Time.realtimeSinceStartup:F1}s");
            MPRunner.Instance.Reattach();   // the syncs start on the new scene and read their tables for the new world
            ResetAfterFreshWorld();
            var me = Game.LocalPlayer;
            yield return BackToChainStart();
            if (me != null && !me.IsDead) me.ResetToFullHealth();
            // As a new game's character: mortal (a scenario that made it invincible may have ended early).
            if (me != null) me.Invincible = false;
            QuickTransition = false;
            Plugin.Log.LogInfo($"[dev] Fresh world ready at {Time.realtimeSinceStartup:F1}s");
        }

        public static bool Headless => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        // settle: seconds to let the picture settle first (effects, UI fades); headless runs skip it with the shot
        public static IEnumerator Screenshot(string name, float settle = 0f)
        {
            if (Headless) { Plugin.Log.LogInfo($"[dev] Screenshot {name} skipped (headless)"); yield break; }
            if (settle > 0f) yield return new WaitForSecondsRealtime(settle);
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            string path = Path.Combine(OutDir, name + ".png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
            Plugin.Log.LogInfo($"[dev] Screenshot {path}");
        }

        public static void DumpHierarchy(GameObject root, string file)
        {
            var sb = new StringBuilder();
            void Walk(Transform t, int depth)
            {
                sb.Append(' ', depth * 2).Append(t.gameObject.activeSelf ? "+ " : "- ").Append(t.name).Append("  [");
                sb.Append(string.Join(", ", t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c =>
                {
                    string s = c.GetType().Name;
                    if (c is SpriteRenderer sr) s += $"(sprite={(sr.sprite ? sr.sprite.name : "null")} mat={(sr.sharedMaterial ? sr.sharedMaterial.name : "null")} col={sr.color} en={sr.enabled})";
                    if (c is Behaviour b && !b.enabled) s += "(off)";
                    return s;
                })));
                sb.Append("] scale=").Append(t.localScale).AppendLine();
                foreach (Transform child in t) Walk(child, depth + 1);
            }
            Walk(root.transform, 0);
            string path = Path.Combine(OutDir, file);
            File.WriteAllText(path, sb.ToString());
            Plugin.Log.LogInfo($"[dev] Dumped {root.name} to {path}");
        }

        public static void Quit(string why)
        {
            Plugin.Log.LogInfo($"[dev] Quitting: {why}");
            Session.Current?.Stop();
            // The game can take 20s+ to shut down (audio, saving); a test copy has nothing worth waiting for.
            if (Args.Test != null)
            {
                new System.Threading.Thread(() =>
                {
                    System.Threading.Thread.Sleep(800);   // (the log and the goodbye are out by then)
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                }) { IsBackground = true }.Start();
            }
            Application.Quit();
        }
    }

    // --mp-profile keeps each instance's saves apart, so two copies on one PC don't overwrite each other.
    [HarmonyPatch(typeof(SaveLoad), nameof(SaveLoad.GetPath))]
    internal static class SaveLoadGetPathPatch
    {
        private static bool Prefix(string fileName, ref string __result)
        {
            if (Args.Profile == null) return true;
            __result = Path.Combine(Application.persistentDataPath, "Saves_" + Args.Profile, fileName);
            return false;
        }
    }
}
