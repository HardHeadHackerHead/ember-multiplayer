using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Late joiners and the class lessons. The demo's first class, the Wanderer, comes from one place: the Mind's Journal
    // (a key item: the world's) teaches it in a dialogue passage offered only while the humming-sphere quest's objective
    // "qst03_hummingSphere_mindsJournal" is active, and that lesson (dialogue trigger "TeachWandererClass": Unlock
    // Player Class, learned, plus Set Quest Objective) completes the objective. The quest is the world's: once one player
    // took the lesson it's done for everyone. Those in the session then get the class (CharacterRewards); a character
    // that comes later (a new one, or one that wasn't there) never could: no Wanderer, no class skills or class XP.
    // So a dialogue trigger receiver that both unlocks classes and completes a quest objective is a one-time lesson:
    // once that objective is completed in the host's world, a guest's character without those classes gets them as the
    // lesson gives them, once per character and world, under the lesson's own reward key (a character that took it, or
    // got it from another player, has that key in its record already). If it has no class equipped, the first one is.
    // Found in the scene's own dialogue triggers (the journal's is in TutorialArea, Town and the cartographer's).
    internal static class ClassLessons
    {
        public static int Granted;
        private static int sceneHandle = -1;
        private static readonly List<(global::CustomEvent.UnlockPlayerClass unlock, string objective)> lessons =
            new List<(global::CustomEvent.UnlockPlayerClass, string)>();
        private static float nextCheck;

        public static int Lessons => lessons.Count;

        public static void Update()
        {
            var s = Session.Current;
            if (s == null || s.IsHost || !GuestWorld.Active || !CharacterRewards.Active || !Game.InGame) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextCheck) return;
            nextCheck = now + 2f;
            if (Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading) return;
            var qm = Singleton<QuestManager>.HasMain ? Singleton<QuestManager>.main : null;
            if (qm == null || QuestManager.model == null || Game.LocalPlayer == null) return;
            Scan();
            var pd = GameData.playerData;
            foreach (var (unlock, objective) in lessons)
            {
                if (unlock == null || qm.GetObjectiveState(objective) != QuestState.Completed) continue;
                var classes = unlock.classesToUnlock.Where(c => c != null && !string.IsNullOrEmpty(c.playerClassType.GetEmbervilleID())).ToList();
                var ids = classes.Select(c => c.playerClassType.GetEmbervilleID()).ToList();
                bool learned = unlock.setAsLearned;
                if (ids.Count == 0 || ids.All(id => pd.KnownClasses.Contains(id) && (!learned || pd.LearnedClasses.Contains(id)))) continue;
                string key = CharacterRewards.Key(CharacterRewards.ClassesWhat(ids, learned), unlock);
                if (!QuestRewards.Claim(key)) continue;
                foreach (var id in ids)
                {
                    pd.KnownClasses.Add(id);
                    if (learned) pd.LearnedClasses.Add(id);
                }
                var stats = Game.LocalPlayer.GetCharacterStats();
                if (stats != null && stats.equippedClass == null) stats.EquipClass(classes[0]);
                Granted++;
                SaveSync.Request("a class lesson");
                Plugin.Log.LogInfo($"[reward] {key}: a lesson the world already had (objective {objective} done), given to our late character");
            }
        }

        private static void Scan()
        {
            int h = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (h == sceneHandle) return;
            sceneHandle = h;
            lessons.Clear();
            lessons.AddRange(Find());
            if (lessons.Count > 0) Plugin.Log.LogInfo($"[reward] Class lessons here: {string.Join(", ", lessons.Select(l => l.objective))}");
        }

        // The scene's lessons: dialogue trigger receivers with an Unlock Player Class and a Set Quest Objective.
        public static List<(global::CustomEvent.UnlockPlayerClass unlock, string objective)> Find()
        {
            var found = new List<(global::CustomEvent.UnlockPlayerClass, string)>();
            foreach (var t in Object.FindObjectsByType<DialogueTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t == null || t.triggers == null) continue;
                foreach (var rec in t.triggers)
                {
                    var events = rec?.OnTriggerEvents?.events;
                    if (events == null) continue;
                    var objective = events.OfType<global::CustomEvent.SetQuestObjective>().FirstOrDefault(e => e.quest != null && !string.IsNullOrEmpty(e.objective))?.objective;
                    if (objective == null) continue;
                    foreach (var u in events.OfType<global::CustomEvent.UnlockPlayerClass>())
                        if (u.classesToUnlock != null && u.classesToUnlock.Count > 0) found.Add((u, objective));
                }
            }
            return found;
        }
    }
}
