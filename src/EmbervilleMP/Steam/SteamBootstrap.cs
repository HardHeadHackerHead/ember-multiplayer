using System;
using System.IO;
using System.Runtime.InteropServices;
using Steamworks;

namespace EmbervilleMP.Steam
{
    // The game has no Steam code of its own, so the mod brings Steamworks.NET + steam_api64.dll and starts the
    // Steam API itself, as the app Steam launched (its id comes from Steam; see AppId for games started directly).
    internal static class SteamBootstrap
    {
        // Last resort when the game wasn't started by Steam and its install can't tell (test copies): the demo.
        public const uint DemoAppId = 5355880;

        private static uint appId;
        private static string appIdSource = "";

        // The app this game is: SteamAppId (set when Steam launches the game), else steam_appid.txt in the game folder,
        // else the Steam library's appmanifest_<id>.acf whose installdir is this game folder, else the demo's id.
        public static uint AppId
        {
            get
            {
                if (appId == 0) appId = ResolveAppId(out appIdSource);
                return appId;
            }
        }

        private static uint ResolveAppId(out string source)
        {
            if (uint.TryParse(Environment.GetEnvironmentVariable("SteamAppId"), out uint id) && id != 0) { source = "SteamAppId"; return id; }
            try
            {
                string game = BepInEx.Paths.GameRootPath;
                string txt = Path.Combine(game, "steam_appid.txt");
                if (File.Exists(txt) && uint.TryParse(File.ReadAllText(txt).Trim(), out id) && id != 0) { source = "steam_appid.txt"; return id; }
                // <library>\steamapps\common\<installdir> -> <library>\steamapps\appmanifest_<id>.acf
                var common = Directory.GetParent(game);
                var steamapps = common?.Parent;
                if (common != null && steamapps != null && string.Equals(common.Name, "common", StringComparison.OrdinalIgnoreCase))
                {
                    string installdir = Path.GetFileName(game.TrimEnd('\\', '/'));
                    foreach (var acf in Directory.GetFiles(steamapps.FullName, "appmanifest_*.acf"))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]*)\"");
                        if (!m.Success || !string.Equals(m.Groups[1].Value, installdir, StringComparison.OrdinalIgnoreCase)) continue;
                        string n = Path.GetFileNameWithoutExtension(acf).Substring("appmanifest_".Length);
                        if (uint.TryParse(n, out id) && id != 0) { source = Path.GetFileName(acf); return id; }
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[steam] Couldn't work out the app id from the install: " + e.Message); }
            source = "fallback (demo)";
            return DemoAppId;
        }

        public static bool Ready { get; private set; }
        public static string Error { get; private set; } = "";

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        public static bool Init()
        {
            if (Ready) return true;
            try
            {
                // DllImport("steam_api64") only searches the game folder; load our copy first so it resolves.
                string dir = Path.GetDirectoryName(typeof(SteamBootstrap).Assembly.Location);
                string native = Path.Combine(dir, "steam_api64.dll");
                if (File.Exists(native) && LoadLibrary(native) == IntPtr.Zero)
                    Plugin.Log.LogWarning($"[steam] LoadLibrary({native}) failed: {Marshal.GetLastWin32Error()}");
                // Started from Steam these are already set; started directly (tests) they tell Steam which game this is.
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamAppId")))
                {
                    Environment.SetEnvironmentVariable("SteamAppId", AppId.ToString());
                    Environment.SetEnvironmentVariable("SteamGameId", AppId.ToString());
                    Plugin.Log.LogInfo($"[steam] Not started by Steam: app id {AppId} (from {appIdSource})");
                }
                if (!Packsize.Test()) { Error = "Steamworks.NET packsize mismatch"; return false; }
                if (!DllCheck.Test()) { Error = "steam_api64.dll version mismatch"; return false; }
                var result = SteamAPI.InitEx(out string message);
                if (result != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
                {
                    Error = $"{result}: {message}";
                    Plugin.Log.LogWarning("[steam] Init failed: " + Error);
                    return false;
                }
                Ready = true;
                Plugin.Log.LogInfo($"[steam] Ready as {SteamFriends.GetPersonaName()} ({SteamUser.GetSteamID()}), app {SteamUtils.GetAppID()}");
                return true;
            }
            catch (Exception e)
            {
                Error = e.GetType().Name + ": " + e.Message;
                Plugin.Log.LogWarning("[steam] Init failed: " + e);
                return false;
            }
        }

        public static void RunCallbacks()
        {
            if (Ready) SteamAPI.RunCallbacks();
        }

        public static void Shutdown()
        {
            if (!Ready) return;
            Ready = false;
            SteamAPI.Shutdown();
        }
    }
}
