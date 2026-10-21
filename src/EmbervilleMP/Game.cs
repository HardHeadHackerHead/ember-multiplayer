using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP
{
    // Small helpers over the game's own singletons.
    internal static class Game
    {
        // Scene names the runtime code relies on, in one place (a new game build may rename them: docs/EA-PORT.md).
        public const string MainMenuScene = "MainMenu";
        public const string SplashScene = "SplashScreen";

        private static string fallbackStartScene;

        // The scene the game itself falls back to when there's no saved scene: the default of SceneLoader.CallScene's
        // fallback argument, read from the running build ("TutorialArea" in the demo).
        public static string FallbackStartScene
        {
            get
            {
                if (fallbackStartScene != null) return fallbackStartScene;
                string scene = null;
                try
                {
                    var p = typeof(SceneLoader).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(m => m.Name == nameof(SceneLoader.CallScene))
                        .SelectMany(m => m.GetParameters())
                        .LastOrDefault(x => x.ParameterType == typeof(string) && x.HasDefaultValue && x.DefaultValue is string s && s.Length > 0);
                    scene = p?.DefaultValue as string;
                }
                catch { }
                return fallbackStartScene = scene ?? "TutorialArea";
            }
        }

        public static GameManager Manager => Singleton<GameManager>.HasMain ? Singleton<GameManager>.main : null;

        public static PlayerMaster LocalPlayer
        {
            get
            {
                var gm = Manager;
                return gm != null && gm.player != null ? gm.player : null;
            }
        }

        public static bool InGame
        {
            get
            {
                var gm = Manager;
                return gm != null && gm.player != null && gm.gameStarted && gm.finishedSetup;
            }
        }

        // In the game, or lying dead in it in co-op: gameStarted is false from GameManager.ResetScene until the respawn
        // is over (fade, death screen, respawn), yet our copy is still in that scene. What runs the scene for the others
        // goes by this (presence, scene authority, enemies, world objects), so a dead runner doesn't hand the scene to
        // the next player for the length of its death screen. Everything about our own play keeps using InGame.
        public static bool InScene => InGame || Sync.PartyWipe.LocalDownInScene;

        public static string SceneName => SceneManager.GetActiveScene().name;

        public static string AreaName
        {
            get
            {
                var gm = Manager;
                return gm != null && gm.CurrentArea != null ? gm.CurrentArea.name : "";
            }
        }

        // root.Find(path); if a game update moved things, the deepest-named object anywhere under root (same name as
        // the path's last part). Only ever differs from Find when Find fails.
        public static Transform FindUi(Transform root, string path)
        {
            if (root == null) return null;
            var t = root.Find(path);
            if (t != null) return t;
            string name = path.Substring(path.LastIndexOf('/') + 1);
            t = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(c => c.name == name);
            if (t != null) Plugin.Log.LogWarning($"[menu] '{path}' not found under {root.name}; using {name} found elsewhere (game update?)");
            return t;
        }
    }
}
