using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // ---- quest test helpers ----

        public const int TestQuestXp = 37, TestQuestGold = 13;
        internal static bool InjectQuestRewards;
        private static readonly Dictionary<string, int> testRewardRuns = new Dictionary<string, int>();

        private static int TestRewardRuns(string questId) => questId != null && testRewardRuns.TryGetValue(questId, out int n) ? n : 0;

        // A character reward we can count: XP and gold, and how many times it ran on this copy.
        private class TestQuestReward : QuestReward
        {
            public string QuestId;

            public override void Reward()
            {
                testRewardRuns[QuestId] = TestRewardRuns(QuestId) + 1;
                try { Game.LocalPlayer?.GainXP(TestQuestXp); } catch { }
                try { if (Singleton<Inventory>.HasMain) Singleton<Inventory>.main.AddGold(TestQuestGold); } catch { }
            }
        }

        // Every quest of this copy's model rewards TestQuestReward only (each new model too: see the patch below).
        internal static void InjectRewards(QuestModel model)
        {
            if (model == null) return;
            foreach (var q in model.Quests.Values)
                if (!(q.data.rewards.Count == 1 && q.data.rewards[0] is TestQuestReward))
                    q.data.rewards = new List<QuestReward> { new TestQuestReward { QuestId = q.data.questID } };
        }

        private static bool QuestModelReady() =>
            QuestManager.model != null && Singleton<QuestManager>.HasMain && Singleton<QuestManager>.main.isInitialized;

        private static Quest FindQuest(string id) =>
            id != null && QuestManager.model != null && QuestManager.model.Quests.TryGetValue(id, out var q) ? q : null;

        // Quests nobody started yet whose every step can be finished through its objectives; preferably ones nothing in
        // the loaded scenes reacts to (completing them then has no story side effects in the test).
        private static IEnumerable<Quest> QuestCandidates()
        {
            var all = QuestManager.model.Quests.Values
                .Where(q => q.mainState.Value == QuestState.Inactive && q.data.steps.Count > 0 && q.data.steps.All(st => st.questObjectives.Count > 0))
                .OrderBy(q => q.data.questID, StringComparer.Ordinal).ToList();
            var watched = new HashSet<string>();
            foreach (var l in UnityEngine.Object.FindObjectsByType<QuestListener>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (l.quest != null) watched.Add(l.quest.questID);
            foreach (var c in UnityEngine.Object.FindObjectsByType<QuestChecker>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (c.quest != null) watched.Add(c.quest.questID);
            foreach (var t in UnityEngine.Object.FindObjectsByType<QuestTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (t.quest != null) watched.Add(t.quest.questID);
            var quiet = all.Where(q => !watched.Contains(q.data.questID)).ToList();
            return quiet.Count >= 3 ? quiet : all;
        }

        // Finishes the quest's objectives step by step, as playing it would.
        private static void CompleteForTest(Quest q)
        {
            var qm = Singleton<QuestManager>.main;
            if (q.mainState.Value == QuestState.Inactive) qm.SetQuestState(q.data.questID, QuestState.Active);
            for (int guard = 0; q.mainState.Value == QuestState.Active && q.stepIndex < q.data.steps.Count && guard < 50; guard++)
            {
                int before = q.stepIndex;
                foreach (var o in q.data.steps[q.stepIndex].questObjectives.ToList()) qm.SetObjectiveState(o.ID, QuestState.Completed);
                if (q.stepIndex == before) break;
            }
        }

        private static int Gold() => Singleton<Inventory>.HasMain ? Singleton<Inventory>.main.totalGold.Value : -1;

        private static string StateOf(string id) => FindQuest(id)?.mainState.Value.ToString() ?? "missing";

        // Quest rewards: the host completes a quest -> host and guest each get its rewards once, on their own
        // characters; re-sent snapshots and a reconnect grant nothing more; a quest completed while the guest was cut
        // off but that it had going is caught up once on rejoin; one that started and completed entirely while it was
        // away isn't its, nor (standalone run) one completed before the guest first got the host's world.
        // In a chain (--mp-test=a,b,c) each copy starts the scenario in its own fresh world: the guest is then in the
        // session from the start, so the "before the guest joined" part becomes an ordinary shared completion.
        [Scenario("questrewards", Timeout = 220, Start = "boot")]
        private static IEnumerator QuestRewardsTest()
        {
            testRewardRuns.Clear();
            InjectQuestRewards = true;
            InjectRewards(QuestManager.model);
            string earlyId = null;
            if (Args.Host)
            {
                // One quest done before any guest can have our world: it only goes out once we're in game.
                yield return WaitFor(() => Game.LocalPlayer != null && QuestModelReady(), 120f);
                InjectRewards(QuestManager.model);
                var early = QuestModelReady() ? QuestCandidates().FirstOrDefault() : null;
                bool noWorldSent = GuestWorld.Instance == null || GuestWorld.Instance.SnapshotsSent == 0;
                if (Check("host has a quest to complete before the guest has its world", early != null && noWorldSent, $"in game {Game.InGame}, worlds sent {GuestWorld.Instance?.SnapshotsSent}"))
                {
                    CompleteForTest(early);
                    earlyId = early.data.questID;
                    Check("host completed it early", early.mainState.Value == QuestState.Completed, $"{earlyId}: {early.mainState.Value}");
                }
            }
            yield return WaitInGame();
            yield return WaitFor(() => Session.Current != null && Session.Current.Ready && Session.Current.Players.Count >= 2, 90f);
            var s = Session.Current;
            if (!Check("session ready", s != null && s.Ready)) { yield return EndQuestRewards(""); yield break; }
            if (!s.IsHost) yield return WaitFor(() => GuestWorld.Active && Game.InGame && !GuestWorld.Switching && QuestModelReady(), 120f);
            yield return Barrier("ready", 120f);
            string role = s.IsHost ? "host" : "guest";

            string q1Id = null, q2Id = null, q3Id = null;
            bool hostsWorld = true;   // guest: plays a copy of the host's world (standalone), not its own fresh one (chain)
            if (s.IsHost)
            {
                Check("early quest still completed in game", earlyId == null || StateOf(earlyId) == "Completed", StateOf(earlyId));
                var pick = QuestCandidates().Take(3).ToList();
                if (pick.Count == 3) { q1Id = pick[0].data.questID; q2Id = pick[1].data.questID; q3Id = pick[2].data.questID; }
                Chat.Send($"#quests={earlyId ?? "none"}|{q1Id ?? "none"}|{q2Id ?? "none"}|{q3Id ?? "none"}");
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("quests"), 20f);
                var parts = (peerData.TryGetValue("quests", out var qs) ? qs : "none|none|none|none").Split('|');
                string Part(int i) => parts.Length > i && parts[i] != "none" ? parts[i] : null;
                earlyId = Part(0);
                q1Id = Part(1);
                q2Id = Part(2);
                q3Id = Part(3);
                // Standalone we got the host's world from this session; a chain gives each copy its own fresh world.
                hostsWorld = GuestWorld.Instance != null && GuestWorld.Instance.SnapshotsLoaded > 0;
                Plugin.Log.LogInfo($"[test] Guest plays {(hostsWorld ? "a copy of the host's world" : "its own fresh world (chain)")}");
                // The early quest reaches us now (chain: we're in the session, so it's ours too) or is already done.
                if (earlyId != null) yield return WaitFor(() => StateOf(earlyId) == "Completed", 15f);
                yield return new WaitForSecondsRealtime(1f);
            }
            if (!Check("both know the test quests", FindQuest(q1Id) != null && FindQuest(q2Id) != null && FindQuest(q3Id) != null, $"{earlyId} {q1Id} {q2Id} {q3Id}"))
            {
                yield return Barrier("done");
                yield return EndQuestRewards("");
                yield break;
            }
            if (!s.IsHost && earlyId != null)
            {
                Check("guest has the early quest completed", StateOf(earlyId) == "Completed", StateOf(earlyId));
                if (hostsWorld)
                    Check("the guest didn't get the reward of a quest completed before it had the host's world", TestRewardRuns(earlyId) == 0 && !QuestRewards.HasRecord(earlyId),
                        $"ran {TestRewardRuns(earlyId)}x");
                else
                    Check("in the session when it completed (chain): the guest got the early quest's reward once", TestRewardRuns(earlyId) == 1,
                        $"ran {TestRewardRuns(earlyId)}x");
            }
            yield return Barrier("picked");

            // 1) The host completes a quest: each player gets its rewards once, on their own character.
            int gold0 = Gold(), xp0 = TotalXp();
            int granted0 = QuestRewards.TimesGranted(q1Id);
            if (s.IsHost) CompleteForTest(FindQuest(q1Id));
            else yield return WaitFor(() => StateOf(q1Id) == "Completed" && TestRewardRuns(q1Id) > 0, 20f);
            yield return new WaitForSecondsRealtime(1f);
            Check($"{role}: the quest is completed", StateOf(q1Id) == "Completed", StateOf(q1Id));
            Check($"{role} got the quest's reward once", TestRewardRuns(q1Id) == 1 && QuestRewards.TimesGranted(q1Id) - granted0 == 1,
                $"reward ran {TestRewardRuns(q1Id)}x, granted {QuestRewards.TimesGranted(q1Id) - granted0}x");
            Check($"{role}'s gold went up by the reward", Gold() - gold0 == TestQuestGold, $"{gold0} -> {Gold()}");
            Check($"{role}'s XP went up", TotalXp() > xp0, $"{xp0} -> {TotalXp()}");
            yield return Barrier("q1");

            // 2) Everything sent and merged again: nothing more.
            WorldState.Instance.ResendQuests();
            yield return new WaitForSecondsRealtime(3f);
            Check($"{role}: re-applied snapshots grant nothing more", TestRewardRuns(q1Id) == 1 && Gold() - gold0 == TestQuestGold,
                $"reward ran {TestRewardRuns(q1Id)}x, gold {gold0} -> {Gold()}, snapshots got {WorldState.QuestSnapshotsReceived}");
            yield return Barrier("resent");

            // 3) A second quest both have going; the guest's connection drops, the host completes it (and a third one
            //    the guest never saw) meanwhile; the guest rejoins: the second quest's reward once (it took part), not
            //    the third's (it wasn't there), and nothing it already had again.
            int granted2 = QuestRewards.TimesGranted(q2Id);
            if (s.IsHost) Singleton<QuestManager>.main.SetQuestState(q2Id, QuestState.Active);
            yield return WaitFor(() => StateOf(q2Id) == "Active", 20f);
            Check($"{role}: the second quest is active", StateOf(q2Id) == "Active", StateOf(q2Id));
            Check($"{role}: the third quest isn't started", StateOf(q3Id) == "Inactive", StateOf(q3Id));
            yield return Barrier("q2-active");
            var signals = Bucket(scenarioIndex).signals;
            if (s.IsHost)
            {
                int worldsSent = GuestWorld.Instance.SnapshotsSent;
                yield return WaitFor(() => signals.ContainsKey("#drop"), 20f);
                Chat.Send("#drop-ok");
                yield return new WaitForSecondsRealtime(0.5f);
                CompleteForTest(FindQuest(q2Id));
                CompleteForTest(FindQuest(q3Id));
                Check("host completed the second and third quests while the guest was cut off", StateOf(q2Id) == "Completed" && StateOf(q3Id) == "Completed", $"{StateOf(q2Id)}, {StateOf(q3Id)}");
                Check("host got their rewards once", TestRewardRuns(q2Id) == 1 && TestRewardRuns(q3Id) == 1, $"{TestRewardRuns(q2Id)}x, {TestRewardRuns(q3Id)}x");
                yield return WaitFor(() => GuestWorld.Instance.SnapshotsSent > worldsSent, 60f);
                Check("the guest came back", GuestWorld.Instance.SnapshotsSent > worldsSent);
                yield return new WaitForSecondsRealtime(2f);
                yield return Barrier("rejoined", 60f);
                Check("host: no reward granted twice", TestRewardRuns(earlyId) <= 1 && TestRewardRuns(q1Id) == 1 && TestRewardRuns(q2Id) == 1 && TestRewardRuns(q3Id) == 1,
                    $"early {TestRewardRuns(earlyId)}, q1 {TestRewardRuns(q1Id)}, q2 {TestRewardRuns(q2Id)}, q3 {TestRewardRuns(q3Id)}");
            }
            else
            {
                int earlyRuns = TestRewardRuns(earlyId);
                Chat.Send("#drop");
                yield return WaitFor(() => signals.ContainsKey("#drop-ok"), 20f);
                Session.Current.SimulateDrop();
                yield return null;
                Check("reconnecting after the drop", MPRunner.Instance.Reconnecting && GuestWorld.Active);
                yield return WaitFor(() => Session.Current != null && Session.Current.Ready && !MPRunner.Instance.Reconnecting, 60f);
                Check("reconnected", Session.Current != null && Session.Current.Ready, Session.Current?.Status ?? "no session");
                yield return WaitFor(() => GuestWorld.Rejoins > 0 && Game.InGame && !GuestWorld.Switching && QuestModelReady(), 60f);
                yield return WaitFor(() => StateOf(q2Id) == "Completed" && TestRewardRuns(q2Id) > 0, 15f);
                yield return new WaitForSecondsRealtime(2f);
                Check("rejoined guest has the second quest completed", StateOf(q2Id) == "Completed", StateOf(q2Id));
                Check("rejoined guest got the second quest's reward once", TestRewardRuns(q2Id) == 1 && QuestRewards.TimesGranted(q2Id) - granted2 == 1,
                    $"reward ran {TestRewardRuns(q2Id)}x, caught up on rejoin {WorldState.CarriedGrants}x");
                Check("rejoined guest has the third quest completed", StateOf(q3Id) == "Completed", StateOf(q3Id));
                Check("the guest didn't get the third quest's reward (started and done while it was away)", TestRewardRuns(q3Id) == 0 && !QuestRewards.HasRecord(q3Id),
                    $"ran {TestRewardRuns(q3Id)}x");
                Check("the reload granted nothing it already had", TestRewardRuns(q1Id) == 1 && TestRewardRuns(earlyId) == earlyRuns && Gold() - gold0 == 2 * TestQuestGold,
                    $"q1 ran {TestRewardRuns(q1Id)}x, early {earlyRuns} -> {TestRewardRuns(earlyId)}, gold {gold0} -> {Gold()}");
                yield return Barrier("rejoined", 60f);
            }
            Plugin.Log.LogInfo($"[quest] granted {QuestRewards.Granted}, skipped {QuestRewards.Skipped}, failed {QuestRewards.Failed}; snapshots sent {WorldState.Instance.QuestSnapshotsSent}, got {WorldState.QuestSnapshotsReceived}, merges {WorldState.Instance.QuestMerges}");
            yield return Barrier("done");
            yield return EndQuestRewards("quest rewards once per player");
        }

        // The test rewards stop with the scenario (a chained scenario after this one gets the game's own).
        private static IEnumerator EndQuestRewards(string summary)
        {
            InjectQuestRewards = false;
            yield return Finish(summary);
        }

        // A "kill X" objective with kills by the host and by the guest: it counts exactly the kills, each player gets
        // each kill's XP once, and rooms switching off (living enemies disabled) count nothing.
        [Scenario("killcredit", Timeout = 220, Quick = true)]
        private static IEnumerator KillCreditTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            var es = EnemySync.Instance;
            if (!s.IsHost) yield return WaitFor(() => GuestWorld.Active && Game.InGame && !GuestWorld.Switching && QuestModelReady(), 120f);
            yield return Barrier("inworld", 120f);
            yield return TeleportTo(CombatArea, s.IsHost ? new Vector2(51.8f, 76.3f) : new Vector2(53f, 77f));
            yield return Barrier("arrived", 40f);
            var me = Game.LocalPlayer;

            // The host picks: a quest, the enemy type with the most living enemies here, two of them.
            string plan = null;
            if (s.IsHost)
            {
                yield return new WaitForSecondsRealtime(1f);
                var group = EnemiesIn(CombatArea).Where(e => !e.IsDead && es.IdOf(e) != 0).GroupBy(e => e.enemyType)
                    .OrderByDescending(g => g.Count()).ThenBy(g => (int)g.Key).FirstOrDefault();
                var quest = QuestCandidates().FirstOrDefault();
                if (group != null && group.Count() >= 2 && quest != null)
                {
                    var two = group.OrderBy(e => (e.transform.position - me.transform.position).sqrMagnitude).Take(2).ToList();
                    plan = $"{quest.data.questID}|{(int)group.Key}|{es.IdOf(two[0])}|{es.IdOf(two[1])}|{group.Count()}";
                }
                else plan = "none";
                Chat.Send("#kill=" + plan);
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("kill"), 20f);
                plan = peerData.TryGetValue("kill", out var p) ? p : "none";
            }
            var parts = plan.Split('|');
            if (!Check("found a quest and two enemies of one type", parts.Length == 5 && FindQuest(parts[0]) != null, plan))
            {
                yield return Barrier("done");
                yield return Finish("");
                yield break;
            }
            string questId = parts[0];
            var type = (EnemyType)int.Parse(parts[1]);
            int idA = int.Parse(parts[2]), idB = int.Parse(parts[3]);
            Plugin.Log.LogInfo($"[test] Kill objective on {questId}: {type} x2 ({idA}, {idB}) of {parts[4]} here");

            // Same test objective on both copies before the quest starts: kill 50 of that type.
            var q = FindQuest(questId);
            var kill = new QuestObjective.KillEnemies { ID = "mp_test_kill", enemyType = type, amount = 50 };
            kill.Title = q.data.steps[0].questObjectives.FirstOrDefault()?.Title;   // the "new quest" popup reads it
            q.data.steps[0].questObjectives = new List<QuestObjective> { kill };
            yield return Barrier("setup");
            if (s.IsHost) Singleton<QuestManager>.main.SetQuestState(questId, QuestState.Active);
            yield return WaitFor(() => q.mainState.Value == QuestState.Active && kill.state.Value == QuestState.Active, 20f);
            Check("the kill objective is active", kill.state.Value == QuestState.Active, $"quest {q.mainState.Value}, objective {kill.state.Value}");
            yield return Barrier("started");

            int xp0 = TotalXp(), xpA0 = KillCredit.XpKills(idA), xpB0 = KillCredit.XpKills(idB);   // (ids repeat across chained scenarios)
            var a = es.ById(idA);
            var b = es.ById(idB);
            if (s.IsHost)
            {
                if (a != null) CombatSystem.Resolve(Hit(me, a, 99999));
                yield return WaitFor(() => a != null && a.IsDead, 5f);
                Check("host killed the first enemy", a != null && a.IsDead);
                yield return Barrier("host-killed");
                yield return WaitFor(() => b != null && b.IsDead, 20f);
                Check("the guest's kill happened on the host", b != null && b.IsDead);
            }
            else
            {
                yield return Barrier("host-killed");
                yield return WaitFor(() => a == null || a.IsDead, 5f);
                Check("guest saw the host's kill", a != null && a.IsDead);
                for (int attempt = 0; attempt < 3 && b != null && !b.IsDead; attempt++)
                {
                    if (me.IsDead || me.CurrentHealthPoints < me.MaxHealthPoints / 3) me.ResetToFullHealth();
                    CombatSystem.Resolve(Hit(me, b, 99999));
                    yield return WaitFor(() => b.IsDead, 4f);
                }
                Check("guest killed the second enemy", b != null && b.IsDead);
            }
            yield return Barrier("guest-killed");
            yield return new WaitForSecondsRealtime(2f);
            Check("the kill objective counts exactly the two kills", kill.Progress == 2, $"progress {kill.Progress}");
            Check("XP for the host's kill once", KillCredit.XpKills(idA) - xpA0 == 1, $"{KillCredit.XpKills(idA) - xpA0}x");
            Check("XP for the guest's kill once", KillCredit.XpKills(idB) - xpB0 == 1, $"{KillCredit.XpKills(idB) - xpB0}x");
            Check("XP went up", TotalXp() > xp0, $"{xp0} -> {TotalXp()}");
            yield return Barrier("counted");

            // The guest walks off: its copy switches the fight's room off (the living enemies of that type with it).
            if (!s.IsHost) yield return TeleportTo("004_Gather", new Vector2(150f, 130f));
            yield return new WaitForSecondsRealtime(3f);
            Check("rooms switching off count no kills", kill.Progress == 2, $"progress {kill.Progress}, phantom deaths dropped {KillCredit.PhantomsDropped}");
            Plugin.Log.LogInfo($"[enemy] kills seen elsewhere {KillCredit.RemoteCounted}, out-of-sight XP {KillCredit.OutOfSightXp}, phantoms dropped {KillCredit.PhantomsDropped}; deaths {es.DeathsSent}/{es.DeathsApplied}");
            yield return Barrier("done");
            yield return Finish("kill credit once per player");
        }
    }

    // Tests: each new quest model (a world loaded, a reconnect) gets the countable test rewards too.
    [HarmonyPatch(typeof(QuestManager), "QuestsSetup")]
    internal static class TestQuestRewardsPatch
    {
        private static void Postfix()
        {
            if (SelfTest.InjectQuestRewards) SelfTest.InjectRewards(QuestManager.model);
        }
    }
}
