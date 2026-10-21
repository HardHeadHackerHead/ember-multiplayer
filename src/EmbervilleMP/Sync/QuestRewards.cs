using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbervilleMP.Net;
using HarmonyLib;

namespace EmbervilleMP.Sync
{
    // Quest rewards in co-op. The game grants them in Quest.CompleteQuest -> Reward(), on whichever copy sees the quest's
    // last step finish: its own play, or our merge of another player's progress (WorldState). Rules:
    //  - Character rewards (XP, items, gold, "add to inventory", classes, known items) go to every player whose copy
    //    completes the quest while they're in the session's world, any scene: once per character and world. A record
    //    (<character save>_mp_quest_rewards.txt, saved with the character) remembers what this character already got
    //    in which world, so a re-applied snapshot, a reconnect or a second local completion grants nothing more.
    //  - A guest that joins after the quest was completed loads it as completed: no reward (it didn't take part).
    //  - A guest whose connection dropped while a quest it had going (active on its copy) was completed gets the
    //    character rewards when it rejoins (WorldState.ApplyCarried).
    //  - World effects (custom events: story flags, quest states, map markers, recipes...) run on every copy that
    //    completes the quest, as the game does (the host's copy is the one saved). "Consume from inventory" only runs
    //    for the player whose own play completed it (the one delivering), not for those who got it through the network.
    // The same record holds the character rewards given outside quests (CharacterRewards: dialogue, triggers, graphs).
    internal static class QuestRewards
    {
        public static int Granted, Skipped, Failed;
        private static readonly Dictionary<string, int> grantedNow = new Dictionary<string, int>();
        private static HashSet<string> record = new HashSet<string>();
        private static string recordFor;
        private static bool dirty;

        // Outside co-op the game's own Reward() runs untouched.
        public static bool Active => Session.Current != null || GuestWorld.Active;

        public static string WorldKey
        {
            get
            {
                var wd = GameData.worldData;
                return wd == null ? "" : Clean(wd.Name) + "|" + wd.createdTime;
            }
        }

        internal static string CharacterKey
        {
            get
            {
                var pd = GameData.playerData;
                return pd == null ? "" : Clean(pd.playerName) + "|" + pd.createdTime;
            }
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        private static string FilePath => SaveLoad.GetPath(GameData.playerFilePrefix + "_mp_quest_rewards.txt");
        private static string Who => GameData.playerFilePrefix + "|" + CharacterKey;
        private static string Key(string questId) => WorldKey + "\t" + questId;

        // How many times this run granted the quest's character rewards (tests).
        public static int TimesGranted(string questId) => grantedNow.TryGetValue(questId, out int n) ? n : 0;

        public static bool HasRecord(string questId)
        {
            Ensure();
            return record.Contains(Key(questId));
        }

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
                if (lines.Length == 0 || lines[0] != "character\t" + CharacterKey) return;
                foreach (var l in lines.Skip(1)) if (l.Length > 0) record.Add(l);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[quest] Couldn't read the reward record: " + e.Message); }
        }

        // Written when the character file is (GameData.SavePlayer), so the record and the XP/items it stands for
        // reach the disk together.
        public static void Save()
        {
            if (!dirty || recordFor == null || recordFor != Who) return;
            try
            {
                File.WriteAllLines(FilePath, new[] { "character\t" + CharacterKey }.Concat(record.OrderBy(x => x, StringComparer.Ordinal)));
                dirty = false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[quest] Couldn't save the reward record: " + e.Message); }
        }

        // localCompletion: this copy's own play completed the quest (not a merge of someone else's progress).
        // worldEffects: false when the world already has them (a reward caught up after a reconnect).
        public static void Grant(Quest q, bool localCompletion, bool worldEffects = true)
        {
            Ensure();
            string id = q.data.questID;
            bool already = !record.Add(Key(id));
            if (already) Skipped++;
            else
            {
                dirty = true;
                Granted++;
                grantedNow[id] = TimesGranted(id) + 1;
            }
            Plugin.Log.LogInfo($"[quest] '{id}' completed{(localCompletion ? "" : " (merged)")}: character rewards {(already ? "already granted in this world, skipped" : "granted")}");
            CharacterRewards.Suppress++;   // (quest rewards have their own rules: not CharacterRewards')
            try
            {
                foreach (var reward in q.data.rewards)
                {
                    if (reward == null) continue;
                    if (reward is QuestReward.CustomEvent ce)
                    {
                        var events = ce.rewardEvents?.events;
                        if (events != null)
                            foreach (var ev in events.ToList()) RunEvent(ev, already, localCompletion, worldEffects, id);
                        continue;
                    }
                    if (!already) Try(reward.Reward, id, reward.GetType().Name);
                }
            }
            finally { CharacterRewards.Suppress--; }
        }

        // Character rewards outside quests (CharacterRewards) share the record, under their own keys ("~..."):
        // true the first time for this character in this world.
        public static bool Claim(string key)
        {
            Ensure();
            if (!record.Add(Key(key))) return false;
            dirty = true;
            return true;
        }

        private static void RunEvent(global::CustomEvent ev, bool already, bool localCompletion, bool worldEffects, string id)
        {
            if (ev == null) return;
            bool character = ev is CustomEventAddToInventory || ev is global::CustomEvent.UnlockPlayerClass || ev is global::CustomEvent.SetItemKnown;
            bool run = ev is global::CustomEvent.ConsumeFromInventory ? localCompletion && worldEffects
                : character ? !already
                : worldEffects;
            if (run) Try(ev.Invoke, id, ev.GetType().Name);
        }

        private static void Try(Action a, string id, string what)
        {
            try { a(); }
            catch (Exception e)
            {
                Failed++;
                Plugin.Log.LogWarning($"[quest] Reward {what} of '{id}' failed: {e.Message}");
            }
        }
    }

    // Kill credit. Every copy runs the deaths it can see (the scene runner's, and the others' replay of it through
    // EnemyDied): each player gets the XP for kills in the scene they're in, from their own copy, once. "Kill X"
    // objectives count each real death once per copy (in our scene: the local death; elsewhere: EnemyDied ->
    // CountKill) and merge "highest wins", so the group's count is the number of kills, whoever made them.
    internal static class KillCredit
    {
        public static int RealDeaths;   // > 0 while a real death (or a kill seen elsewhere) is being counted
        public static int PhantomsDropped, RemoteCounted, OutOfSightXp;
        private static readonly Dictionary<int, int> xpKills = new Dictionary<int, int>();

        public static bool Filtering => Session.Current != null || GuestWorld.Active;

        // How many times our character got the XP for this enemy's death (tests).
        public static int XpKills(int enemyId) => xpKills.TryGetValue(enemyId, out int n) ? n : 0;

        public static void OnXpKill(Enemy e)
        {
            int id = EnemySync.Instance?.IdOf(e) ?? 0;
            if (id != 0) xpKills[id] = XpKills(id) + 1;
        }

        // A kill this copy didn't run: straight to our active "kill X" objectives (the game's OnEnemyDied event would
        // also stir enemies here whose graphs listen for deaths).
        public static void CountKill(EnemyType type)
        {
            var model = QuestManager.model;
            if (model == null || !Singleton<QuestManager>.HasMain) return;
            RealDeaths++;
            try
            {
                foreach (var q in model.Quests.Values.ToList())
                {
                    if (q.mainState.Value != QuestState.Active || q.stepIndex >= q.data.steps.Count) continue;
                    foreach (var o in q.data.steps[q.stepIndex].questObjectives.ToList())
                        if (o is QuestObjective.KillEnemies k && k.state.Value == QuestState.Active) k.OnEnemyDie(type);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[quest] Counting a kill failed: " + e.Message); }
            finally { RealDeaths--; }
            RemoteCounted++;
        }

        // The scene's runner killed an enemy we have but can't show (it's in another room of our scene): we're in the
        // scene, so our character gets the XP as if we'd seen it die.
        public static void GrantOutOfSightXp(Enemy e)
        {
            var me = Game.LocalPlayer;
            if (me == null || e == null) return;
            try
            {
                int xp = e.expRange.Random();
                if (xp > 0) me.GainXP(xp);
                OnXpKill(e);
                OutOfSightXp++;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[enemy] Out-of-sight XP failed: " + ex.Message); }
        }
    }

    // ---- hooks ----

    [HarmonyPatch(typeof(Quest), "Reward")]
    internal static class QuestRewardPatch
    {
        private static bool Prefix(Quest __instance)
        {
            if (!QuestRewards.Active) return true;
            KeyItemSync.Instance?.BeginRewards();
            try { QuestRewards.Grant(__instance, !WorldState.Applying); }
            finally { KeyItemSync.Instance?.EndRewards(); }
            return false;
        }
    }

    [HarmonyPatch(typeof(GameData), nameof(GameData.SavePlayer))]
    internal static class QuestRewardRecordSavePatch
    {
        private static void Postfix() => QuestRewards.Save();
    }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.SetKilled))]
    internal static class RealDeathPatch
    {
        private static void Prefix() => KillCredit.RealDeaths++;

        private static Exception Finalizer(Exception __exception)
        {
            KillCredit.RealDeaths--;
            return __exception;
        }
    }

    // The game's OnEnemyDied also fires for living enemies whose room gets switched off (Enemy.OnDisable) and for dead
    // ones a room reset restores. In co-op rooms switch on and off for the other players all the time, so "kill X"
    // objectives only count real deaths (SetKilled) and kills seen elsewhere (CountKill).
    [HarmonyPatch(typeof(QuestObjective.KillEnemies), "OnEnemyDie")]
    internal static class KillObjectiveFilterPatch
    {
        private static bool Prefix()
        {
            if (!KillCredit.Filtering || KillCredit.RealDeaths > 0) return true;
            KillCredit.PhantomsDropped++;
            return false;
        }
    }

    // Counts the game's own XP grant for a kill (an enemy dying from a player's hit), for the tests and the log.
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage))]
    internal static class KillXpCountPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Enemy __instance, out bool __state) => __state = __instance.IsDead;

        private static void Postfix(Enemy __instance, DamageData damageData, bool __state)
        {
            if (!__state && __instance.IsDead && damageData.Aggressor is PlayerMaster) KillCredit.OnXpKill(__instance);
        }
    }
}
