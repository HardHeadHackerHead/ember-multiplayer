using System.Collections;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Shared progress: story flags both ways, a lore page, a dialogue variable, the host's clock, a quest.
        [Scenario("progress", Timeout = 200, Quick = true)]
        private static IEnumerator ProgressTest()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            yield return WaitFor(() => s.Ready && s.Players.Count >= 2, 90f);
            yield return WaitFor(() => Game.InGame && (s.IsHost || GuestWorld.Active), 90f);
            yield return Barrier("ready", 120f);
            var ws = WorldState.Instance;

            // Pick test subjects both sides agree on: an inactive quest with 2+ steps, a dialogue trigger with a saved
            // variable (the only kind shared).
            var quest = QuestManager.model?.Quests.Values.Where(q => q.mainState.Value == QuestState.Inactive && q.data.steps.Count >= 2 && q.data.steps[0].questObjectives.Count > 0)
                .OrderBy(q => q.data.questID).FirstOrDefault();
            Check("found a quest to play with", quest != null);
            var trigger = Object.FindObjectsByType<DialogueTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => !string.IsNullOrEmpty(t.uniqueID) && t.GetVariables().Any(v => v.varType is DialogueSimpleBool)).OrderBy(t => t.uniqueID).FirstOrDefault();
            string varKey = trigger?.GetVariables().Where(v => v.varType is DialogueSimpleBool).Select(v => v.key).OrderBy(k => k).FirstOrDefault();

            if (s.IsHost)
            {
                GameData.gameBools[BoolID.MetAtwor] = true;
                if (Singleton<TimeManager>.HasMain) Singleton<TimeManager>.main.dayTime = 15.5f;
            }
            else
            {
                GameData.gameBools[BoolID.DefeatedKaldhor] = true;
                GameData.worldData.readLoreContents.Add("mp_test_lore");
                if (trigger != null && varKey != null) trigger.SetVariableValue(varKey, !trigger.GetVariableValue(varKey));
                if (quest != null)
                {
                    Singleton<QuestManager>.main.SetQuestState(quest.data.questID, QuestState.Active);
                    yield return new WaitForSecondsRealtime(1f);
                    foreach (var o in quest.data.steps[0].questObjectives)
                        Singleton<QuestManager>.main.SetObjectiveState(o.ID, QuestState.Completed);
                }
                Chat.Send("#dialogue=" + (trigger != null && varKey != null ? $"{trigger.uniqueID}|{varKey}|{trigger.GetVariableValue(varKey)}" : "none"));
            }
            yield return Barrier("changed");
            // Until everything checked below has arrived (at most the 3 s it was given before).
            DialogueTrigger theirTrigger = null;
            System.Func<bool> dialogueArrived = () =>
            {
                if (!peerData.TryGetValue("dialogue", out var dv)) return false;
                if (dv == "none") return true;
                var parts = dv.Split('|');
                if (theirTrigger == null) theirTrigger = Object.FindObjectsByType<DialogueTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(x => x.uniqueID == parts[0]);
                return theirTrigger != null && theirTrigger.GetVariableValue(parts[1]).ToString() == parts[2];
            };
            System.Func<bool> arrived = () => s.IsHost
                ? GameData.gameBools.IsTrue(BoolID.DefeatedKaldhor) && GameData.worldData.readLoreContents.Contains("mp_test_lore") && dialogueArrived()
                  && (quest == null || (quest.mainState.Value != QuestState.Inactive && quest.stepIndex >= 1))
                : GameData.gameBools.IsTrue(BoolID.MetAtwor) && (!Singleton<TimeManager>.HasMain || Mathf.Abs(Singleton<TimeManager>.main.dayTime - 15.5f) < 0.6f);
            yield return WaitFor(arrived, 3f);

            if (s.IsHost)
            {
                Check("guest's story flag reached the host", GameData.gameBools.IsTrue(BoolID.DefeatedKaldhor));
                Check("guest's lore page reached the host", GameData.worldData.readLoreContents.Contains("mp_test_lore"));
                yield return WaitFor(() => peerData.ContainsKey("dialogue"), 10f);
                string dv = peerData.TryGetValue("dialogue", out var d) ? d : "none";
                if (dv != "none")
                {
                    var parts = dv.Split('|');
                    var t = Object.FindObjectsByType<DialogueTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(x => x.uniqueID == parts[0]);
                    Check("guest's dialogue variable reached the host", t != null && t.GetVariableValue(parts[1]).ToString() == parts[2], dv);
                }
                if (quest != null)
                {
                    Check("guest's quest start reached the host", quest.mainState.Value != QuestState.Inactive, quest.mainState.Value.ToString());
                    Check("guest's quest step reached the host", quest.stepIndex >= 1, $"step {quest.stepIndex}, merges {ws.QuestMerges}");
                }
            }
            else
            {
                Check("host's story flag reached the guest", GameData.gameBools.IsTrue(BoolID.MetAtwor));
                if (Singleton<TimeManager>.HasMain)
                {
                    float t = Singleton<TimeManager>.main.dayTime;
                    Check("guest's clock follows the host's", Mathf.Abs(t - 15.5f) < 0.6f, $"{t:F2} vs 15.5 (+drift)");
                }
            }
            Plugin.Log.LogInfo($"[world] bools {ws.BoolsSent}/{ws.BoolsApplied} lists {ws.ListAddsApplied} quests {ws.QuestSnapshotsSent}/{ws.QuestMerges} time {ws.TimeSyncs} dialogue {ws.DialogueVarsApplied}");
            yield return Barrier("done");
            yield return Finish("shared progress works");
        }
    }
}
