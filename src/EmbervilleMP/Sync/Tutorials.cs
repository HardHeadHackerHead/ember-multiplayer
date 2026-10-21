using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;

namespace EmbervilleMP.Sync
{
    // Tutorials in co-op.
    //  - Whose: the game keeps which tutorials were seen in the world (WorldData.tutorialsDone, TutorialManager's
    //    OnLoad/OnSave). A guest plays in the host's world, so it would skip whatever the host has seen and lose what it
    //    sees itself (a guest never saves the host's world). While it plays there, a guest uses its own record instead:
    //    <character save>_mp_tutorials.txt, saved with the character, started from its own world's list when it joins
    //    from in game. The host's world list is never written by a guest; the host's own tutorials work as in the game.
    //  - When: a tutorial window that stops time (the first hit taken, the quest window...) can't stop it with others in
    //    the scene (MPRunner keeps the clock running), so the player would stand in a modal window in the middle of a
    //    fight. With others in the scene such a window waits until we're out of combat (the game itself already holds
    //    tutorial windows back while a dialogue plays); alone, or out of combat, it opens at once as in the game.
    //  - World swaps (joining, going home, tests' fresh worlds) load the next scene while the outgoing one still runs,
    //    after clearing the object pools (GuestWorld.ResetWorldManagers). The outgoing scene's tutorials stop when the
    //    swap begins, as they would when its TutorialManager is destroyed: a tutorial window finishing its delay in
    //    between would build itself from the cleared pools and reach the game's singletons mid-load (which breaks the
    //    next scene's loading). Held-back windows are dropped then too (another world).
    internal static class Tutorials
    {
        public static int Deferred, ShownLate;
        private static HashSet<string> record = new HashSet<string>();
        private static string recordFor;
        private static bool dirty;
        // The scene's TutorialManager whose list is our record (loaded while playing in the host's world).
        private static TutorialManager fromRecord;
        private static readonly List<TutorialDataWindow> waiting = new List<TutorialDataWindow>();
        private static bool releasing;

        public static int WaitingCount => waiting.Count;
        public static string FilePath => SaveLoad.GetPath(GameData.playerFilePrefix + "_mp_tutorials.txt");
        private static string Who => GameData.playerFilePrefix + "|" + QuestRewards.CharacterKey;

        private static void Ensure()
        {
            string who = Who;
            if (who == recordFor) return;
            recordFor = who;
            record = new HashSet<string>();
            dirty = false;
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return;
                var lines = File.ReadAllLines(path);
                // The slot may hold a newer character than the one the record was written for.
                if (lines.Length == 0 || lines[0] != "character\t" + QuestRewards.CharacterKey) return;
                foreach (var l in lines.Skip(1)) if (l.Length > 0) record.Add(l);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[tutorial] Couldn't read the tutorial record: " + e.Message); }
        }

        // Written when the character file is (GameData.SavePlayer).
        public static void Save()
        {
            if (!dirty || recordFor == null || recordFor != Who) return;
            try
            {
                File.WriteAllLines(FilePath, new[] { "character\t" + QuestRewards.CharacterKey }.Concat(record.OrderBy(x => x, StringComparer.Ordinal)));
                dirty = false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[tutorial] Couldn't save the tutorial record: " + e.Message); }
        }

        public static bool InRecord(string id)
        {
            Ensure();
            return record.Contains(id);
        }

        // Tutorials this character saw in its own world (when it joins from there).
        public static void Seed(IEnumerable<string> done)
        {
            if (done == null) return;
            Ensure();
            foreach (var id in done) if (!string.IsNullOrEmpty(id) && record.Add(id)) dirty = true;
        }

        // After the game's OnLoad (which took the world's list).
        public static void OnLoaded(TutorialManager tm)
        {
            if (!GuestWorld.Active)
            {
                if (fromRecord == tm) fromRecord = null;
                return;
            }
            // Tests: in every other scenario a guest keeps what the host's world has seen too (as before tutorials were
            // per character), so tutorial windows don't land in the middle of unrelated checks.
            if (Args.Test != null && SelfTest.Current != "tutorials") Seed(tm.tutorialsDone);
            Ensure();
            tm.tutorialsDone = record.OrderBy(x => x, StringComparer.Ordinal).ToList();
            fromRecord = tm;
            Plugin.Log.LogInfo($"[tutorial] Playing in the host's world: this character's own tutorials ({record.Count} seen)");
        }

        // Before the game's OnSave: false skips it (it would write our list into the host's world).
        public static bool OnSaving(TutorialManager tm)
        {
            if (tm == null || tm != fromRecord) return true;
            Ensure();
            foreach (var id in tm.tutorialsDone) if (!string.IsNullOrEmpty(id) && record.Add(id)) dirty = true;
            return false;
        }

        // Others in the scene keep the clock running, and we're fighting or down: a window that stops time waits.
        private static bool MustWait()
        {
            if (!SceneAuthority.OthersInMyScene()) return false;
            var me = Game.LocalPlayer;
            if (!Game.InGame || me == null || me.IsDead) return true;
            var tp = TeamTeleport.Instance;
            return tp != null && tp.InCombat(me);
        }

        // A world swap begins (GuestWorld.BeginSwitch): the outgoing scene's tutorials stop here.
        public static void OnWorldSwitch()
        {
            if (waiting.Count > 0) Plugin.Log.LogInfo($"[tutorial] World swap: dropping {waiting.Count} held-back tutorial window(s)");
            waiting.Clear();
            try
            {
                if (Singleton<TutorialManager>.HasMain) Singleton<TutorialManager>.main.tokenSource?.Cancel();
                UIPanels.SetLocked(false);   // (a window that never opens would leave the panels locked in the next scene)
            }
            catch (Exception e) { Plugin.Log.LogWarning("[tutorial] Stopping the outgoing scene's tutorials failed: " + e.Message); }
        }

        private static bool Stopped(TutorialManager tm) =>
            GuestWorld.Switching || tm == null || tm.tokenSource == null || tm.tokenSource.IsCancellationRequested;

        // Before the game's ShowTutorial: false skips it (its scene is going away; it would lock the panels for good).
        public static bool MayStart(TutorialManager tm) => !Stopped(tm);

        // Before the game's ShowModularTutorial: false holds the window back (or drops it, mid world swap).
        public static bool MayShowNow(TutorialManager tm, TutorialDataWindow data)
        {
            if (Stopped(tm)) return false;
            if (releasing || data == null || !data.stopTime || !MustWait()) return true;
            if (!waiting.Contains(data))
            {
                waiting.Add(data);
                Deferred++;
                Plugin.Log.LogInfo($"[tutorial] '{data.id}' waits until we're out of combat (others keep the game running)");
            }
            // ShowTutorial locked the panels for it: meanwhile the player keeps the pause menu and the others.
            if (tm != null && tm.panelPopup != null && !tm.panelPopup.activeSelf) UIPanels.SetLocked(false);
            return false;
        }

        // Every frame: a waiting window opens once it may (the scene's TutorialManager shows it).
        public static void Update()
        {
            if (waiting.Count == 0 || !Game.InGame || GuestWorld.Switching || !Singleton<TutorialManager>.HasMain) return;
            if (Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading) return;
            var tm = Singleton<TutorialManager>.main;
            if (Stopped(tm) || tm.panelPopup == null || tm.panelPopup.activeSelf || Game.Manager.gameState != GameState.Playing || MustWait()) return;
            var data = waiting[0];
            waiting.RemoveAt(0);
            ShownLate++;
            Plugin.Log.LogInfo($"[tutorial] '{data.id}': out of combat, showing it now");
            releasing = true;
            try { tm.ShowModularTutorial(data); }
            catch (Exception e) { Plugin.Log.LogWarning("[tutorial] Couldn't show a held-back tutorial: " + e.Message); }
            finally { releasing = false; }
        }
    }

    [HarmonyPatch(typeof(TutorialManager), nameof(TutorialManager.OnLoad))]
    internal static class TutorialLoadPatch
    {
        private static void Postfix(TutorialManager __instance) => Tutorials.OnLoaded(__instance);
    }

    [HarmonyPatch(typeof(TutorialManager), nameof(TutorialManager.OnSave))]
    internal static class TutorialSavePatch
    {
        private static bool Prefix(TutorialManager __instance) => Tutorials.OnSaving(__instance);
    }

    [HarmonyPatch(typeof(GameData), nameof(GameData.SavePlayer))]
    internal static class TutorialRecordSavePatch
    {
        private static void Postfix() => Tutorials.Save();
    }

    [HarmonyPatch(typeof(TutorialManager), nameof(TutorialManager.ShowTutorial), new[] { typeof(string), typeof(bool), typeof(float) })]
    internal static class TutorialStartPatch
    {
        private static bool Prefix(TutorialManager __instance) => Tutorials.MayStart(__instance);
    }

    [HarmonyPatch(typeof(TutorialManager), nameof(TutorialManager.ShowModularTutorial))]
    internal static class TutorialWindowPatch
    {
        private static bool Prefix(TutorialManager __instance, TutorialDataWindow data) => Tutorials.MayShowNow(__instance, data);
    }
}
