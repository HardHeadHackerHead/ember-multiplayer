using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace EmbervilleMP.Sync
{
    // Story flags that arrive from another player change the scene we're in now. The game reads them with CheckGameBool
    // components, which only check when they wake, start or are enabled (a flag set by the player's own play shows on
    // the next visit; most of what it changes right away comes with the script that set it). So a flag merged from
    // someone else (WorldState.OnBool) changed nothing here until we left and came back: now the checks in our loaded
    // scene that depend on it run again, the ones whose answer it changed. Checks called by other scripts
    // (UnityCalls.Remote) are left to them; those that haven't woken/started yet, or are switched off waiting for
    // OnEnable, check by themselves.
    internal static class LiveFlags
    {
        public static int Rechecked;
        private static readonly Dictionary<BoolID, List<CheckGameBool>> index = new Dictionary<BoolID, List<CheckGameBool>>();
        private static int sceneHandle = -1, indexed = -1;

        // The scene's checks by flag; rebuilt for each new scene, and when scenes were added to it (additive loads,
        // objects spawned by scripts) since the last look.
        private static List<CheckGameBool> ChecksOf(BoolID id)
        {
            int h = SceneManager.GetActiveScene().handle;
            var all = Object.FindObjectsByType<CheckGameBool>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (h != sceneHandle || all.Length != indexed)
            {
                sceneHandle = h;
                indexed = all.Length;
                index.Clear();
                foreach (var c in all)
                    if (c != null && c.checkBools != null)
                        foreach (var b in c.checkBools.Where(b => b != null).Select(b => b.id).Distinct())
                        {
                            if (!index.TryGetValue(b, out var list)) index[b] = list = new List<CheckGameBool>();
                            list.Add(c);
                        }
            }
            return index.TryGetValue(id, out var found) ? found : null;
        }

        private static bool Live(CheckGameBool c)
        {
            if (c == null) return false;
            switch (c.checkWhen)
            {
                case UnityCalls.Awake: return c.didAwake;
                case UnityCalls.Start: return c.didStart;
                case UnityCalls.OnEnable: return c.isActiveAndEnabled;
                default: return false;
            }
        }

        // Before a remote flag is applied: the checks that depend on it, with their answer now.
        public static List<(CheckGameBool check, bool matched)> Before(BoolID id)
        {
            try
            {
                if (!Game.InGame) return null;
                return ChecksOf(id)?.Where(Live).Select(c => (c, c.AllMatch())).ToList();
            }
            catch (Exception e) { Plugin.Log.LogWarning("[world] Flag checks: " + e.Message); return null; }
        }

        // Checks waiting for our own cutscene to end (scene handle, check, answer before).
        private static readonly List<(int scene, CheckGameBool check, bool matched)> waiting = new List<(int, CheckGameBool, bool)>();
        public static int Deferred;

        // A cutscene step plays here (ours or one we play along with): its objects are its own until it ends. A flag
        // it sets on another copy first (the same cutscene, a bit ahead) must not switch them under it (the intro's
        // "acolyte disabler" taking the acolyte away mid-scene).
        internal static bool Held => Sync.Objects.CutsceneSync.Instance != null && Sync.Objects.CutsceneSync.Instance.HasActive;

        // After: those whose answer changed run again (their match / mismatch events); later if a cutscene plays here.
        public static void After(List<(CheckGameBool check, bool matched)> before)
        {
            if (before == null) return;
            if (Held)
            {
                int h = SceneManager.GetActiveScene().handle;
                foreach (var (c, was) in before)
                    if (c != null && !waiting.Any(w => w.check == c)) { waiting.Add((h, c, was)); Deferred++; }
                return;
            }
            foreach (var (c, was) in before) Recheck(c, was);
        }

        // Every frame (WorldState.Update): the waiting ones, once no cutscene plays here.
        public static void Update()
        {
            LiveQuests.Update();
            if (waiting.Count == 0 || Held || !Game.InGame) return;
            int h = SceneManager.GetActiveScene().handle;
            var list = waiting.ToList();
            waiting.Clear();
            foreach (var (scene, c, was) in list)
                if (scene == h) Recheck(c, was);   // (another scene: it checked by itself when it loaded)
        }

        private static void Recheck(CheckGameBool c, bool was)
        {
            if (c == null) return;
            try
            {
                if (c.AllMatch() == was) return;
                c.Check();
                Rechecked++;
                Plugin.Log.LogInfo($"[world] A flag from another player: '{c.Name}' ({c.name}) checked again");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[world] Checking '{c.name}' again: {e.Message}"); }
        }
    }

    // The same for quests: ObjectiveChecker and QuestChecker compare an objective's or a quest's state when they wake,
    // start or are enabled, and run their match / mismatch events (objects on or off). A teammate's progress comes in
    // through the quest merge (WorldState.MergeSnapshot): the checks in our loaded scene whose answer it changed run
    // again; later if a cutscene plays here (as for flags).
    internal static class LiveQuests
    {
        public static int Rechecked, Deferred;
        private static readonly List<(int scene, MonoBehaviour check, bool matched)> waiting = new List<(int, MonoBehaviour, bool)>();

        private static IEnumerable<MonoBehaviour> All() =>
            Object.FindObjectsByType<ObjectiveChecker>(FindObjectsInactive.Include, FindObjectsSortMode.None).Cast<MonoBehaviour>()
                .Concat(Object.FindObjectsByType<QuestChecker>(FindObjectsInactive.Include, FindObjectsSortMode.None));

        private static bool Live(MonoBehaviour c)
        {
            var when = c is ObjectiveChecker o ? o.checkWhen : c is QuestChecker q ? q.checkWhen : UnityCalls.Remote;
            switch (when)
            {
                case UnityCalls.Awake: return c.didAwake;
                case UnityCalls.Start: return c.didStart;
                case UnityCalls.OnEnable: return c.isActiveAndEnabled;
                default: return false;
            }
        }

        // What the check would answer now (its own Check()); null: it checks nothing.
        private static bool? Matches(MonoBehaviour c)
        {
            var qm = Singleton<QuestManager>.main;
            if (c is ObjectiveChecker o) return o.quest == null ? (bool?)null : qm.GetObjectiveState(o.objective) == o.stateToMatch;
            if (c is QuestChecker q) return q.quest == null ? (bool?)null : qm.GetQuestState(q.quest.questID) == q.stateToMatch;
            return null;
        }

        // Before a merge: the live checks in the loaded scene, with their answer now.
        public static List<(MonoBehaviour check, bool matched)> Before()
        {
            try
            {
                if (!Game.InGame || !Singleton<QuestManager>.HasMain || !Singleton<QuestManager>.main.isInitialized) return null;
                var list = new List<(MonoBehaviour, bool)>();
                foreach (var c in All())
                    if (c != null && Live(c) && Matches(c) is bool m) list.Add((c, m));
                return list;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[quest] Quest checks: " + e.Message); return null; }
        }

        public static void After(List<(MonoBehaviour check, bool matched)> before)
        {
            if (before == null || before.Count == 0) return;
            if (LiveFlags.Held)
            {
                int h = SceneManager.GetActiveScene().handle;
                foreach (var (c, was) in before)
                    if (c != null && Matches(c) != was && !waiting.Any(w => w.check == c)) { waiting.Add((h, c, was)); Deferred++; }
                return;
            }
            foreach (var (c, was) in before) Recheck(c, was);
        }

        public static void Update()
        {
            if (waiting.Count == 0 || LiveFlags.Held || !Game.InGame) return;
            int h = SceneManager.GetActiveScene().handle;
            var list = waiting.ToList();
            waiting.Clear();
            foreach (var (scene, c, was) in list)
                if (scene == h) Recheck(c, was);
        }

        private static void Recheck(MonoBehaviour c, bool was)
        {
            if (c == null) return;
            try
            {
                if (!(Matches(c) is bool now) || now == was) return;
                var ev = c is ObjectiveChecker o ? (now ? o.onMatchEvent : o.onMismatchEvent)
                    : c is QuestChecker q ? (now ? q.onMatchEvent : q.onMismatchEvent) : null;
                ev?.Invoke();
                Rechecked++;
                Plugin.Log.LogInfo($"[quest] A quest from another player: {c.GetType().Name} '{c.name}' checked again ({(now ? "match" : "mismatch")})");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[quest] Checking '{c.name}' again: {e.Message}"); }
        }
    }
}
