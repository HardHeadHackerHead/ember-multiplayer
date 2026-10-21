using System;
using System.IO;
using System.Linq;

namespace EmbervilleMP.Sync
{
    // Copies of the save files multiplayer can change, made before it does: a guest's character before joining, the
    // host's character and world before others play in it. Saves\EmbervilleMP_Backups\<time> <why>\, the last 10 kept.
    internal static class Backups
    {
        private const int Keep = 10;
        public static int Made;

        public static string Folder => Path.Combine(Path.GetDirectoryName(SaveLoad.GetPath("x")), "EmbervilleMP_Backups");

        // prefixes: save names without extension ("player_slot1", "world_slot2"); every file of each is copied.
        public static void Make(string why, params string[] prefixes)
        {
            try
            {
                string saves = Path.GetDirectoryName(SaveLoad.GetPath("x"));
                var files = prefixes.Where(p => !string.IsNullOrEmpty(p) && p != GuestWorld.GuestPrefix)
                    .SelectMany(p => Directory.GetFiles(saves).Where(f =>
                    {
                        string n = Path.GetFileName(f);
                        return n.StartsWith(p + ".") || n.StartsWith(p + "_");
                    }))
                    .Distinct().ToList();
                if (files.Count == 0) return;
                string dir = Path.Combine(Folder, $"{DateTime.Now:yyyy-MM-dd HH.mm.ss} {why}");
                Directory.CreateDirectory(dir);
                foreach (var f in files) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
                Made++;
                Plugin.Log.LogInfo($"[backup] {files.Count} save file(s) copied {why}: {dir}");
                foreach (var old in Directory.GetDirectories(Folder).OrderByDescending(d => d).Skip(Keep))
                    Directory.Delete(old, true);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[backup] Couldn't back up the saves: " + e.Message); }
        }
    }
}
