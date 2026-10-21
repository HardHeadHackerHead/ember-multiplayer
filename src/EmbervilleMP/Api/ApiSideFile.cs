using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using EmbervilleMP.Sync;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleMP.Api
{
    // Mods' data saved with the host's world: <world>_mp_mods.txt next to the save (a world side file: SideFiles
    // deletes it with its world and clears it for a new world in the slot; its first line names the world).
    //   store  <mod> <key> <type> <value>        a world store value (mod, key and strings base64)
    //   spawn  <scene> <id> <key> <x> <y> <init>  a spawned object of a spawnable registered as saved
    // Spawned objects' states (kinds with a handler) are in the object table, <world>_mp_objects.txt (ObjectSync).
    internal static class ApiSideFile
    {
        public static string PathOf(string worldPrefix) => SaveLoad.GetPath(worldPrefix + "_mp_mods.txt");
        public static string CurrentPath => PathOf(GameData.GetWorldPrefix);
        public static int Saves, Loads;

        private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        private static string Un64(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));
        private static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);

        // Host, whenever the game saves its world (and only into the world the data is of).
        public static void Save()
        {
            var s = Net.Session.Current;
            if (GuestWorld.Active || !GameData.HasWorldData || ApiStores.WorldKey == null || ApiStores.WorldKey != QuestRewards.WorldKey) return;
            if (s != null && !s.IsHost) return;
            try
            {
                var lines = new List<string> { SideFiles.WorldHeader };
                foreach (var (mod, key, v) in ApiStores.WorldEntries())
                    lines.Add($"store\t{B64(mod)}\t{B64(key)}\t{v.ToText()}");
                foreach (var r in ApiSpawns.Saved())
                    lines.Add($"spawn\t{B64(r.Scene)}\t{r.Id}\t{B64(r.Key)}\t{F(r.Pos.x)}\t{F(r.Pos.y)}\t{Convert.ToBase64String(r.Init ?? new byte[0])}");
                string path = CurrentPath;
                if (lines.Count == 1 && !File.Exists(path)) return;   // (nothing to keep: no file for worlds without mods' data)
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, lines);
                Saves++;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[api] Couldn't save the mods' world data: " + e.Message); }
        }

        // Host, as a session starts in a world (or the world changed): the file's values and saved spawns.
        public static void Load()
        {
            var store = new List<(string, string, StoreValue)>();
            var spawns = new List<ApiSpawns.Record>();
            ApiStores.WorldKey = QuestRewards.WorldKey;
            try
            {
                string path = CurrentPath;
                if (GameData.HasWorldData && File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    if (lines.Length == 0 || SideFiles.HeaderOk(lines[0], path))
                        foreach (var line in lines)
                        {
                            var p = line.Split('\t');
                            try
                            {
                                if (p[0] == "store" && p.Length == 5) store.Add((Un64(p[1]), Un64(p[2]), StoreValue.FromText(p[3], p[4])));
                                else if (p[0] == "spawn" && p.Length == 7)
                                    spawns.Add(new ApiSpawns.Record
                                    {
                                        Scene = Un64(p[1]), Id = int.Parse(p[2], CultureInfo.InvariantCulture), Key = Un64(p[3]),
                                        Pos = new Vector2(float.Parse(p[4], CultureInfo.InvariantCulture), float.Parse(p[5], CultureInfo.InvariantCulture)),
                                        Init = Convert.FromBase64String(p[6]),
                                    });
                            }
                            catch (Exception e) { Plugin.Log.LogWarning($"[api] Skipped a bad line in {Path.GetFileName(path)}: {e.Message}"); }
                        }
                    Loads++;
                    Plugin.Log.LogInfo($"[api] Loaded the mods' world data: {store.Count} values, {spawns.Count} spawned objects");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[api] Couldn't load the mods' world data: " + e.Message); }
            ApiStores.LoadWorld(store);
            ApiSpawns.Forget();
            ApiSpawns.LoadSaved(spawns);
        }
    }

    // The host saves the mods' world data whenever the game saves.
    [HarmonyPatch(typeof(GameData), nameof(GameData.SaveData))]
    internal static class ModsWorldSavePatch
    {
        private static void Postfix() => ApiSideFile.Save();
    }
}
