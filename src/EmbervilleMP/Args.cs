using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;

namespace EmbervilleMP
{
    // Command line switches, used by the test scripts (and handy for quick LAN sessions):
    //   --mp-host[=port]        host a session
    //   --mp-join=ip[:port]     join a session
    //   --mp-name=Name          player name shown to others
    //   --mp-profile=Name       use Saves_<Name> instead of Saves (several instances on one PC)
    //   --mp-autostart[=Scene]  skip the menus: create a character/world if needed and load it
    //   --mp-test=scenario      run a self-test scenario (see SelfTest.cs); prints "[MP] RESULT ..."
    //   --mp-out=dir            where self-tests write screenshots and reports
    //   --mp-timescale=4        speed the game up
    //   --mp-quit=seconds       quit after this long (safety net for tests)
    //   --mp-mute               silence Wwise
    //   --mp-fps=N              target frame rate (default: uncapped for tests)
    //   --mp-loot=shared|instanced  host: loot mode for this run (over the config)
    //   --mp-estate             host: estate unlocked for this run (over the config)
    //   --mp-keep-playing       host: skip the demo's end lock (Gumu) for this run
    //   --mp-netstats           log bytes/s per message type every 30 s;  --mp-notrim: bandwidth trims off
    //   --mp-scenario-args=a:loot=shared/estate;b:reconnect=8   chained tests: switches of one scenario, while it runs
    //   --mp-fast-death         tests: every death short (DevHarness.FastDeath); scenarios can also ask for it
    internal static class Args
    {
        private static readonly Dictionary<string, string> values = new Dictionary<string, string>();
        // scenario -> its own switches, over the command line's while that scenario runs (one boot, many setups)
        private static readonly Dictionary<string, Dictionary<string, string>> scenarioValues = new Dictionary<string, Dictionary<string, string>>();

        // The scenario running now (SelfTest moves it along a chain); until then the first of --mp-test, so switches
        // read while the session starts (loot mode, estate rules) are already the first scenario's.
        public static string Scenario;

        public static bool Host => Has("host");
        public static int HostPort => Int("host", Plugin.Settings.Port.Value);
        public static string Join => Get("join");
        public static string Name => Get("name");
        public static string Profile => Get("profile");
        public static bool AutoStart => Has("autostart");
        public static string AutoStartScene => Get("autostart");
        public static string Test => Get("test");
        public static string Out => Get("out");
        public static float TimeScale => Float("timescale", 1f);
        public static float QuitAfter => Float("quit", 0f);
        public static bool Mute => Has("mute");
        public static int Fps => Int("fps", Test != null ? -1 : 0);
        public static int Seed => Int("seed", 0);

        public static void Parse(string[] args)
        {
            foreach (string arg in args)
            {
                if (!arg.StartsWith("--mp-", StringComparison.Ordinal)) continue;
                string body = arg.Substring(5);
                int eq = body.IndexOf('=');
                if (eq < 0) values[body] = "";
                else values[body.Substring(0, eq)] = body.Substring(eq + 1).Trim('"');
            }
            if (values.TryGetValue("scenario-args", out string spec))
                foreach (string part in spec.Split(';'))
                {
                    int colon = part.IndexOf(':');
                    if (colon <= 0) continue;
                    var map = new Dictionary<string, string>();
                    foreach (string sw in part.Substring(colon + 1).Split('/'))
                    {
                        string t = sw.Trim();
                        if (t.StartsWith("--mp-", StringComparison.Ordinal)) t = t.Substring(5);
                        if (t.Length == 0) continue;
                        int e = t.IndexOf('=');
                        if (e < 0) map[t] = "";
                        else map[t.Substring(0, e)] = t.Substring(e + 1);
                    }
                    scenarioValues[part.Substring(0, colon).Trim()] = map;
                }
            if (Test != null) Scenario = Test.Split(',')[0].Trim();
        }

        private static Dictionary<string, string> ScenarioValues =>
            Scenario != null && scenarioValues.TryGetValue(Scenario, out var map) ? map : null;

        public static bool Has(string key) => values.ContainsKey(key) || (ScenarioValues?.ContainsKey(key) ?? false);

        public static string Get(string key)
        {
            var sv = ScenarioValues;
            if (sv != null && sv.TryGetValue(key, out string s) && s.Length > 0) return s;
            return values.TryGetValue(key, out string v) && v.Length > 0 ? v : null;
        }

        // This scenario's own switches, for the log.
        public static string DescribeScenario() =>
            ScenarioValues == null ? "" : string.Join(" ", ScenarioValues.Select(kv => kv.Value.Length > 0 ? $"{kv.Key}={kv.Value}" : kv.Key));

        public static int Int(string key, int fallback) =>
            int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        public static float Float(string key, float fallback) =>
            float.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

        public static string Describe() =>
            values.Count == 0 ? "No --mp-* switches." : "Switches: " + string.Join(" ", values.Select(kv => kv.Value.Length > 0 ? $"{kv.Key}={kv.Value}" : kv.Key));
    }

    internal class Settings
    {
        public readonly ConfigEntry<string> PlayerName;
        public readonly ConfigEntry<string> LastAddress;
        public readonly ConfigEntry<int> Port;
        public readonly ConfigEntry<UnityEngine.KeyCode> MenuKey;
        public readonly ConfigEntry<UnityEngine.KeyCode> ChatKey;
        public readonly ConfigEntry<bool> SkipSplash;
        public readonly ConfigEntry<bool> FastLoading;
        public readonly ConfigEntry<Steam.VoiceMode> VoiceMode;
        public readonly ConfigEntry<UnityEngine.KeyCode> VoiceKey;
        public readonly ConfigEntry<float> VoiceVolume;
        public readonly ConfigEntry<Sync.LootMode> LootMode;
        public readonly ConfigEntry<bool> DeathAnnouncements;
        public readonly ConfigEntry<UnityEngine.KeyCode> PingKey;
        public readonly ConfigEntry<bool> AllowTeleport;
        public readonly ConfigEntry<bool> EstateUnlocked;
        public readonly ConfigEntry<bool> KeepPlayingAfterDemo;
        public readonly ConfigEntry<bool> PartyHud;

        private readonly ConfigFile cfg;

        public Settings(ConfigFile cfg)
        {
            this.cfg = cfg;
            // Several game copies on one PC share this file; only write it on purpose (Save), never as a side effect.
            cfg.SaveOnConfigSet = false;
            PlayerName = cfg.Bind("Player", "Name", "", "Name shown to other players (empty: your character's name).");
            LastAddress = cfg.Bind("Network", "LastAddress", "127.0.0.1", "Address last joined.");
            Port = cfg.Bind("Network", "Port", 27015, "UDP port used when hosting.");
            MenuKey = cfg.Bind("UI", "MenuKey", UnityEngine.KeyCode.F8, "Opens the multiplayer window.");
            ChatKey = cfg.Bind("UI", "ChatKey", UnityEngine.KeyCode.Y, "Opens the chat line (Enter sends, Escape cancels).");
            PartyHud = cfg.Bind("UI", "PartyHud", true, "Teammates' health and Kymia at the top left, arrows to teammates off screen, teammates on the minimap and the world map.");
            SkipSplash = cfg.Bind("Speed", "SkipSplash", true, "Skip the logo screens at startup.");
            FastLoading = cfg.Bind("Speed", "FastLoading", true, "Let Unity spend more of each frame on loading (shorter loading screens).");
            VoiceMode = cfg.Bind("Voice", "Mode", Steam.VoiceMode.PushToTalk, "PushToTalk, Open (always on) or Off. Talking needs Steam.");
            VoiceKey = cfg.Bind("Voice", "PushToTalkKey", UnityEngine.KeyCode.V, "Hold to talk.");
            VoiceVolume = cfg.Bind("Voice", "Volume", 1f, "Other players' voices, 0 to 1.");
            LootMode = cfg.Bind("Coop", "LootMode", Sync.LootMode.Instanced,
                "Host's choice. Instanced: every player gets their own loot (chests, drops). Shared: one set of loot in the world, whoever picks it up gets it.");
            DeathAnnouncements = cfg.Bind("UI", "DeathAnnouncements", true, "Announce deaths in chat and with a banner (who died and what killed them), and boss kills.");
            PingKey = cfg.Bind("UI", "PingKey", UnityEngine.KeyCode.Mouse2, "Pings the spot under the mouse for everyone in your area (Mouse2: middle button).");
            AllowTeleport = cfg.Bind("Coop", "AllowTeleportToTeammates", true,
                "Host's choice. Players can teleport to a teammate (pause menu > MULTIPLAYER, or /tp <name> in chat), out of combat.");
            EstateUnlocked = cfg.Bind("Coop", "EstateUnlocked", false,
                "Host's choice. Sets the story flags the demo never sets (building and placing, repairs, portal repairs) in the host's world.");
            KeepPlayingAfterDemo = cfg.Bind("Coop", "KeepPlayingAfterDemo", false,
                "Host's choice. Gumu's introduction in Town ends the demo by freezing your character; with this on, co-op players keep playing.");
            if (!System.IO.File.Exists(cfg.ConfigFilePath))
                try { cfg.Save(); } catch { /* another copy is writing it */ }
        }

        public void Save()
        {
            if (Args.Test != null) return;
            try { cfg.Save(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Could not save settings: " + e.Message); }
        }
    }
}
