using System;
using System.IO;
using HarmonyLib;

namespace EmbervilleMP.Sync
{
    // The mod's side files next to the game's saves, "<save>_mp_*": the host's object states and chest records with
    // its world (<world>_mp_objects.txt, _mp_loot.txt), a character's reward and tutorial records with it
    // (<character>_mp_quest_rewards.txt, _mp_tutorials.txt). The game gives a deleted save's slot name to the next
    // world or character (FindFirstAvailable*Slot), so a side file left behind would be read as the new save's:
    //   - Deleting a world or a character (the game's menus, GameData.Delete*) deletes its side files too.
    //   - Creating one (its first save, on the main menu, while the slot has no game file) first deletes whatever side
    //     files the slot has: they belong to a save deleted some other way (by hand).
    //   - World side files start with the world they belong to ("world\t<name>|<created>") and are ignored in another
    //     world; older files without that line are read once and get it when next written.
    //   - A new character is stamped with today's date only (DateTime.Today), so deleting one and making another of
    //     the same name on the same day looked like the same character to the records' "character" line. Its creation
    //     time gets a random fraction of a second (the menus show the date).
    internal static class SideFiles
    {
        public static int Deleted, Ignored;

        public static string Folder => Path.GetDirectoryName(SaveLoad.GetPath("x"));

        public static string WorldHeader => "world\t" + QuestRewards.WorldKey;

        // A world side file's first line: false if it names another world.
        public static bool HeaderOk(string firstLine, string file)
        {
            if (firstLine == null || !firstLine.StartsWith("world\t") || firstLine == WorldHeader) return true;
            Ignored++;
            Plugin.Log.LogWarning($"[saves] {Path.GetFileName(file)} belongs to another world ({firstLine.Substring(6)}, this is {QuestRewards.WorldKey}): ignored");
            return false;
        }

        // Every side file of a save ("world_slot1" -> world_slot1_mp_*; not world_slot10_mp_*).
        public static void DeleteFor(string prefix, string why)
        {
            if (string.IsNullOrEmpty(prefix) || prefix.StartsWith(GuestWorld.GuestPrefix)) return;
            try
            {
                if (!Directory.Exists(Folder)) return;
                foreach (var f in Directory.GetFiles(Folder, prefix + "_mp_*"))
                {
                    File.Delete(f);
                    Deleted++;
                    Plugin.Log.LogInfo($"[saves] Deleted {Path.GetFileName(f)} ({why})");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[saves] Couldn't delete the side files of {prefix}: {e.Message}"); }
        }

        private static bool OnMenu => Game.SceneName == Game.MainMenuScene;

        public static bool IsNew(string prefix, string extension) =>
            OnMenu && !string.IsNullOrEmpty(prefix) && !prefix.StartsWith(GuestWorld.GuestPrefix) && !File.Exists(SaveLoad.GetPath(prefix + extension));

        // A character being created (its first save, on the menu): its own creation time, nothing of an earlier one.
        public static void BeforeSavePlayer()
        {
            if (GameData.playerData != null && IsNew(GameData.playerFilePrefix, GameData.GetPlayerExtension)) NewCharacter(GameData.playerFilePrefix, GameData.playerData);
        }

        public static void NewCharacter(string prefix, PlayerData pd)
        {
            DeleteFor(prefix, "a new character in that slot");
            if (pd.createdTime % TimeSpan.TicksPerSecond == 0)
                pd.createdTime += new Random().Next(1, (int)TimeSpan.TicksPerSecond);
        }

        public static void BeforeSaveWorld()
        {
            if (GameData.worldData != null && IsNew(GameData.GetWorldPrefix, GameData.GetWorldExtension)) NewWorld(GameData.GetWorldPrefix);
        }

        public static void NewWorld(string prefix) => DeleteFor(prefix, "a new world in that slot");
    }

    [HarmonyPatch(typeof(GameData), nameof(GameData.DeleteWorldSave))]
    internal static class DeleteWorldSideFilesPatch
    {
        private static void Postfix() => SideFiles.DeleteFor(GameData.GetWorldPrefix, "the world was deleted");
    }

    [HarmonyPatch(typeof(GameData), nameof(GameData.DeletePlayerSave))]
    internal static class DeletePlayerSideFilesPatch
    {
        private static void Postfix() => SideFiles.DeleteFor(GameData.playerFilePrefix, "the character was deleted");
    }

    [HarmonyPatch(typeof(GameData), nameof(GameData.DeleteAllCurrentSave))]
    internal static class DeleteAllSideFilesPatch
    {
        private static void Postfix()
        {
            SideFiles.DeleteFor(GameData.playerFilePrefix, "the character was deleted");
            SideFiles.DeleteFor(GameData.GetWorldPrefix, "the world was deleted");
        }
    }

    [HarmonyPatch(typeof(GameData), nameof(GameData.SavePlayer))]
    internal static class NewCharacterSideFilesPatch
    {
        private static void Prefix() => SideFiles.BeforeSavePlayer();
    }

    [HarmonyPatch(typeof(GameData), nameof(GameData.SaveWorld))]
    internal static class NewWorldSideFilesPatch
    {
        private static void Prefix() => SideFiles.BeforeSaveWorld();
    }
}
