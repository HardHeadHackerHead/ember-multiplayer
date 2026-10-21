using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cysharp.Threading.Tasks;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using EmbervilleMP.Sync.Objects;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // The world changing live for everyone: quest checks re-run on a teammate's progress, a teleporter's reveal,
        // an NPC talking to someone else (Sync/LiveFlags.cs LiveQuests, ScriptSync's teleporter and room scopes,
        // NpcTalkSync).

        // livequests: the host's quest progress re-runs the quest and objective checks in the guest's loaded scene (no
        // re-entry): a QuestChecker (checked on start) and an ObjectiveChecker (on enable, switching an object) run
        // again; one whose answer didn't change and one other scripts call (Remote) don't.
        [Scenario("livequests", Timeout = 150)]
        private static IEnumerator LiveQuestsTest()
        {
            yield return ScriptsReady();
            var s = Session.Current;
            yield return WaitFor(QuestModelReady, 30f);
            Plugin.Log.LogInfo($"[test] scene checks: {Object.FindObjectsByType<ObjectiveChecker>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length} ObjectiveChecker, " +
                               $"{Object.FindObjectsByType<QuestChecker>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length} QuestChecker");
            // A quest nobody started, two steps or more (finishing the first doesn't complete it); the host's pick.
            Quest quest = null;
            if (s.IsHost)
            {
                quest = QuestCandidates().FirstOrDefault(q => q.data.steps.Count >= 2 && q.data.steps[1].questObjectives.Count > 0);
                Chat.Send("#livequest=" + (quest != null ? quest.data.questID : "none"));
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("livequest"), 15f);
                quest = FindQuest(peerData.TryGetValue("livequest", out var lq) ? lq : null);
            }
            if (!Check("found a quest with two steps", quest != null && quest.mainState.Value == QuestState.Inactive)) { yield return Finish(""); yield break; }
            var first = quest.data.steps[0].questObjectives[0];
            var later = quest.data.steps[1].questObjectives[0];
            Plugin.Log.LogInfo($"[test] quest {quest.data.questID}: objective {first.ID}, later {later.ID}");

            int questMatch = 0, questMismatch = 0, objMatch = 0, objMismatch = 0, laterRuns = 0, remoteRuns = 0;
            GameObject target = null;
            int rechecked0 = LiveQuests.Rechecked;
            if (!s.IsHost)
            {
                target = new GameObject("MP_TestQuestTarget");
                target.SetActive(false);
                ObjectiveChecker Obj(string name, string objective, UnityCalls when, UnityAction match, UnityAction mismatch)
                {
                    var go = new GameObject(name);
                    go.SetActive(false);
                    var c = go.AddComponent<ObjectiveChecker>();
                    c.quest = quest.data;
                    c.objective = objective;
                    c.stateToMatch = QuestState.Completed;
                    c.checkWhen = when;
                    c.onMatchEvent = new UnityEvent();
                    c.onMismatchEvent = new UnityEvent();
                    c.onMatchEvent.AddListener(match);
                    c.onMismatchEvent.AddListener(mismatch);
                    go.SetActive(true);
                    return c;
                }
                QuestChecker QuestCheck(string name, UnityCalls when, UnityAction match, UnityAction mismatch)
                {
                    var go = new GameObject(name);
                    go.SetActive(false);
                    var c = go.AddComponent<QuestChecker>();
                    c.quest = quest.data;
                    c.stateToMatch = QuestState.Active;
                    c.checkWhen = when;
                    c.onMatchEvent = new UnityEvent();
                    c.onMismatchEvent = new UnityEvent();
                    c.onMatchEvent.AddListener(match);
                    c.onMismatchEvent.AddListener(mismatch);
                    go.SetActive(true);
                    return c;
                }
                // (what the scene's own do: switch an object on or off)
                Obj("MP_TestObjectiveCheck", first.ID, UnityCalls.OnEnable, () => { objMatch++; target.SetActive(true); }, () => { objMismatch++; target.SetActive(false); });
                Obj("MP_TestLaterCheck", later.ID, UnityCalls.OnEnable, () => laterRuns++, () => laterRuns++);
                QuestCheck("MP_TestQuestCheck", UnityCalls.Start, () => questMatch++, () => questMismatch++);
                QuestCheck("MP_TestQuestRemote", UnityCalls.Remote, () => remoteRuns++, () => remoteRuns++);
                yield return null;
                yield return null;
                Check("the checks ran as they started (quest not started)", objMismatch == 1 && objMatch == 0 && questMismatch == 1 && questMatch == 0 && laterRuns == 1,
                    $"objective {objMatch}/{objMismatch}, quest {questMatch}/{questMismatch}, later {laterRuns}");
            }
            yield return Barrier("checks-ready");

            // 1. the host starts the quest
            if (s.IsHost) Singleton<QuestManager>.main.SetQuestState(quest.data.questID, QuestState.Active);
            yield return Barrier("started");
            if (!s.IsHost)
            {
                yield return WaitFor(() => quest.mainState.Value == QuestState.Active && questMatch > 0, 8f);
                Check("the host's quest start reached us", quest.mainState.Value == QuestState.Active, quest.mainState.Value.ToString());
                Check("the quest check here ran again", questMatch == 1, $"match {questMatch}, rechecked {LiveQuests.Rechecked - rechecked0}");
                Check("the objective check didn't (its answer is the same)", objMatch == 0 && objMismatch == 1, $"{objMatch}/{objMismatch}");
            }
            yield return Barrier("start-checked");

            // 2. the host finishes the first step
            if (s.IsHost)
                foreach (var o in quest.data.steps[0].questObjectives.ToList())
                    Singleton<QuestManager>.main.SetObjectiveState(o.ID, QuestState.Completed);
            yield return Barrier("step-done");
            if (!s.IsHost)
            {
                yield return WaitFor(() => first.state.Value == QuestState.Completed && objMatch > 0, 8f);
                Check("the host's objective reached us", first.state.Value == QuestState.Completed, first.state.Value.ToString());
                Check("the objective check ran again and switched its object", objMatch == 1 && target.activeSelf, $"match {objMatch}, object active={target.activeSelf}");
                Check("a check whose answer didn't change wasn't run", laterRuns == 1, $"{laterRuns} (later objective {later.state.Value})");
                Check("a check other scripts call wasn't run", remoteRuns == 0, remoteRuns.ToString());
                Check("the quest check stayed (still active)", questMatch == 1 && questMismatch == 1, $"{questMatch}/{questMismatch}");
                Check("checked again through LiveQuests", LiveQuests.Rechecked - rechecked0 == 2, (LiveQuests.Rechecked - rechecked0).ToString());
            }
            yield return Barrier("done");
            yield return Finish("quest checks follow a teammate's progress");
        }

        // A teleporter the game can take our player through by itself (Teleporter.Teleport: in the scene, to a
        // destination teleporter or id; scene loads and dungeon resolvers aside).
        private static bool TravelsInScene(Teleporter t) =>
            t.teleportResolver == null && string.IsNullOrEmpty(t.sceneToLoad) &&
            (t.destination != null || !string.IsNullOrEmpty(t.destinationID) && Teleporter.GlobalTeleporters.ContainsKey(t.destinationID));

        private static string TeleporterInfo(Teleporter t) =>
            $"{NetIds.ScenePath(t.transform)} id='{t.teleporterID}' dest={(t.destination != null ? NetIds.ScenePath(t.destination.transform) : "-")} destId='{t.destinationID}' " +
            $"scene='{t.sceneToLoad}' resolver={(t.teleportResolver != null ? t.teleportResolver.GetType().Name : "-")} disabled={t.isDisabled} area={(t.gameArea != null ? t.gameArea.name : "-")} " +
            $"spawn={(t.spawnPoint != null)} anim={(t.teleporterAnimation != null)} active={t.gameObject.activeInHierarchy} leave={t.onLeaveEvent?.GetPersistentEventCount()} arrive={t.onAriveEvent?.GetPersistentEventCount()}";

        // teleportreveal: the guest goes through a teleporter whose leave event shows something (the trap room's
        // secret teleporter's art, 05_Trap_Room1/==Teleporters/Teleporter_TrapRoom_Secret/Art); it shows for the host
        // too, who isn't moved or teleported. The demo's secret teleporter has no destination (the game's Teleport
        // stops at "No destination Teleporter was defined": its leave event never runs in the demo); a teleporter
        // that does travel is used if the scene has one with such an event, else the secret one is taken through
        // the game's in-scene path (InSceneTeleport, what Teleport does with a destination) to another teleporter of
        // its room.
        [Scenario("teleportreveal", Timeout = 180)]
        private static IEnumerator TeleportRevealTest()
        {
            yield return ScriptsReady();
            var s = Session.Current;
            var all = Object.FindObjectsByType<Teleporter>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(t => NetIds.ScenePath(t.transform)).ToList();
            List<GameObject> LeaveTargets(Teleporter t)
            {
                var leave = ScriptScope.Calls(t.onLeaveEvent).Select(c => c.target).OfType<GameObject>().ToList();
                return (ScriptedActiveSync.Instance.TargetsOf(t) ?? new List<GameObject>()).Where(g => g != null && leave.Contains(g)).ToList();
            }
            foreach (var t in all.Where(t => t.name == "Teleporter_TrapRoom_Secret" || LeaveTargets(t).Count > 0 || t.destination != null && t.destination.name == "Teleporter_TrapRoom_Secret"))
                Plugin.Log.LogInfo($"[test] teleporter {TeleporterInfo(t)} travels={TravelsInScene(t)} shares [{string.Join(", ", LeaveTargets(t).Select(g => g.name))}]");
            var secret = all.FirstOrDefault(t => t.name == "Teleporter_TrapRoom_Secret");
            var tp = secret != null && TravelsInScene(secret) && LeaveTargets(secret).Count > 0 ? secret
                : all.FirstOrDefault(t => TravelsInScene(t) && LeaveTargets(t).Count > 0) ?? secret;
            if (!Check("found a teleporter whose leave event switches something", tp != null && LeaveTargets(tp).Count > 0)) { yield return Finish(""); yield break; }
            // Where the game's in-scene path takes us if the teleporter can't travel by itself: another teleporter of its room.
            string room = tp.GetComponentInParent<GameArea>(true)?.name ?? "";
            Teleporter via = TravelsInScene(tp) ? null : all.FirstOrDefault(t => t != tp && t.spawnPoint != null && t.gameArea != null && t.gameArea.name == room)
                ?? all.FirstOrDefault(t => t != tp && t.spawnPoint != null && t.gameArea != null);
            Plugin.Log.LogInfo($"[test] using {NetIds.ScenePath(tp.transform)} ({(via == null ? "its own Teleport" : "InSceneTeleport to " + NetIds.ScenePath(via.transform))})");
            if (!Check("it can take us somewhere", via != null || TravelsInScene(tp))) { yield return Finish(""); yield break; }
            var art = LeaveTargets(tp).OrderBy(g => NetIds.ScenePath(g.transform)).First();
            Plugin.Log.LogInfo($"[test] its leave event switches {NetIds.ScenePath(art.transform)}");
            // The rooms' shared switches are one-way and outside the room.
            var areaTargets = Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .SelectMany(a => ScriptedActiveSync.Instance.TargetsOf(a).Select(g => (a, g))).ToList();
            Plugin.Log.LogInfo($"[test] rooms' shared switches: {string.Join("; ", areaTargets.Select(x => x.a.name + " -> " + NetIds.ScenePath(x.g.transform)))}");
            Check("no room shares a switch of its own content", areaTargets.All(x => !x.g.transform.IsChildOf(x.a.transform)), areaTargets.Count.ToString());
            bool was = art.activeSelf;
            Chat.Send("#art" + s.LocalId + "=" + was);
            yield return WaitFor(() => peerData.ContainsKey("art" + (s.IsHost ? 2 : 1)), 10f);
            Check("both see the art the same before", peerData.TryGetValue("art" + (s.IsHost ? 2 : 1), out var theirs) && theirs == was.ToString(), $"{was} / {theirs}");
            Vector3 hostAt = Game.LocalPlayer.transform.position;
            string hostArea = Game.AreaName;
            if (!s.IsHost) yield return TeleportTo(room, (Vector2)tp.transform.position + Vector2.down * 2.5f);
            yield return Barrier("placed", 60f);

            if (!s.IsHost)
            {
                int wrapped = TeleportEventsPatch.Wrapped;
                Vector3 before = Game.LocalPlayer.transform.position;
                if (via == null) tp.TriggerTeleport();   // (what walking onto it does)
                else tp.InSceneTeleport(via).Forget();   // (what its Teleport does, given a destination)
                yield return WaitFor(() => art.activeSelf != was, 10f);
                yield return WaitFor(() => Game.Manager.gameState == GameState.Playing, 10f);
                Check("going through switched the art here", art.activeSelf != was, $"active={art.activeSelf}");
                Check("through the teleporter's own events", TeleportEventsPatch.Wrapped > wrapped, $"{TeleportEventsPatch.Wrapped - wrapped} wrapped");
                Check("and we were teleported", (Game.LocalPlayer.transform.position - before).sqrMagnitude > 0.05f, $"{before} -> {Game.LocalPlayer.transform.position}");
                Chat.Send("#artnow=" + art.activeSelf);
            }
            yield return Barrier("went-through");
            if (s.IsHost)
            {
                yield return WaitFor(() => peerData.ContainsKey("artnow"), 10f);
                bool want = peerData.TryGetValue("artnow", out var an) && an == "True";
                yield return WaitFor(() => art.activeSelf == want, 8f);
                Check("the guest's reveal shows here", art.activeSelf == want && want != was, $"active={art.activeSelf}, guest's {want}");
                Check("we weren't moved", (Game.LocalPlayer.transform.position - hostAt).sqrMagnitude < 1f && Game.AreaName == hostArea,
                    $"{hostAt} -> {Game.LocalPlayer.transform.position}, {hostArea} -> {Game.AreaName}");
            }
            yield return Barrier("done");
            yield return Finish("a teleporter's reveal is everyone's");
        }

        // npclook: while the guest talks to an NPC, the host (who runs the scene) sees it turned to the guest with
        // its talking bubble; on the guest's screen the runner's states don't turn it back. Afterwards the bubble goes
        // back to the host's own (hidden: the host isn't the closest) and the NPC stays turned, as in the game.
        [Scenario("npclook", Timeout = 180)]
        private static IEnumerator NpcLookTest()
        {
            yield return ScriptsReady();
            var s = Session.Current;
            var all = Object.FindObjectsByType<DialogueTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(d => d.selectArrow != null && d.GetComponentInParent<GameArea>(true) != null)
                .OrderBy(d => d.GetComponent<Enemy>() != null ? 0 : 1)
                .ThenBy(d => ScriptScope.Calls(d.GetComponentInParent<GameArea>(true)).Any() ? 1 : 0)   // (not a room whose entering sets something off)
                .ThenBy(d => NetIds.ScenePath(d.transform)).ToList();
            Plugin.Log.LogInfo($"[test] NPCs: {string.Join("; ", all.Select(d => $"{NetIds.ScenePath(d.transform)} face={d.facePlayer} enemy={d.GetComponent<Enemy>() != null}"))}");
            var npc = all.FirstOrDefault();
            if (!Check("found an NPC with a talk bubble", npc != null)) { yield return Finish(""); yield break; }
            var e = npc.GetComponent<Enemy>();
            if (e != null && !npc.facePlayer)
            {
                Plugin.Log.LogInfo($"[test] {npc.name} doesn't turn to its talker in the demo: turned on for the test");
                npc.facePlayer = true;
            }
            var bubble = npc.selectArrow;
            string room = npc.GetComponentInParent<GameArea>(true).name;
            Vector2 at = npc.transform.position;
            // Guest on its left, host on its right; the NPC faces right to begin with (the runner's).
            yield return TeleportTo(room, at + (s.IsHost ? Vector2.right * 2.5f : Vector2.left * 2f) + Vector2.down * 0.5f);
            if (s.IsHost && e != null) e.CurrentDirection = Vector3.right;
            yield return new WaitForSecondsRealtime(1.5f);
            yield return Barrier("placed", 60f);
            bool Talking() => bubble.gameObject.activeSelf && bubble.bubbleImage != null && bubble.bubbleImage.sprite == bubble.talkingSprite;
            var sync = NpcTalkSync.Instance;
            int shown0 = sync.Shown, restored0 = sync.Restored;

            if (!s.IsHost)
            {
                if (e != null) Check("the NPC faces right here too (the runner's)", !e.IsFlipped);
                // What E on it does here (without the dialogue window): the game turns it, the talk starts.
                if (e != null) e.CurrentDirection = (Game.LocalPlayer.transform.position - npc.transform.position).normalized;
                sync.OnStarted(npc);
                bubble.Show(isShowing: true, isNormal: false);
                if (e != null) Check("the runner's states don't turn it meanwhile", EnemySync.HoldFacing != null && EnemySync.HoldFacing(e));
                yield return new WaitForSecondsRealtime(2.5f);
                if (e != null) Check("it stays turned to us", e.IsFlipped);
            }
            yield return Barrier("talking");
            if (s.IsHost)
            {
                yield return WaitFor(() => Talking() && (e == null || e.IsFlipped), 6f);
                Check("the guest's talk shows here: talking bubble", Talking(), $"active={bubble.gameObject.activeSelf} shown={sync.Shown - shown0}");
                if (e != null) Check("the NPC turned to the guest", e.IsFlipped && sync.Turned > 0, $"flipped={e.IsFlipped}");
                npc.OnDisableInteraction(Game.LocalPlayer.gameObject);   // our own prompt going away meanwhile
                Check("our prompt doesn't take the bubble away", Talking());
            }
            yield return Barrier("checked");
            if (!s.IsHost) sync.OnEnded(npc);
            yield return Barrier("ended");
            if (s.IsHost)
            {
                yield return WaitFor(() => sync.Restored > restored0, 6f);
                Check("the talk ended here: the bubble is ours again", sync.Restored > restored0 && !Talking(), $"active={bubble.gameObject.activeSelf}");
                if (e != null) Check("the NPC stays turned (as in the game)", e.IsFlipped);
            }
            else
            {
                yield return new WaitForSecondsRealtime(2f);
                if (e != null) Check("still turned to us after the talk (the runner's turn)", e.IsFlipped);
            }
            yield return Barrier("done");
            yield return Finish("an NPC talking to someone looks it on every screen");
        }

        // learnednotice: a recipe the host learns shows the game's "learned" notification on the guest, once; a burst
        // of them (five at once) is cut short.
        [Scenario("learnednotice", Timeout = 150)]
        private static IEnumerator LearnedNoticeTest()
        {
            yield return ScriptsReady();
            var s = Session.Current;
            var known = GameData.worldData.knownRecipes;
            var fresh = Resources.FindObjectsOfTypeAll<CraftingRecipe>()
                .Where(r => r != null && !string.IsNullOrEmpty(r.RecipeID) && r.Result?.itemData != null && !known.Contains(r.RecipeID))
                .GroupBy(r => r.RecipeID).Select(g => g.First()).OrderBy(r => r.RecipeID, StringComparer.Ordinal).ToList();
            Chat.Send("#recipes" + s.LocalId + "=" + string.Join("|", fresh.Select(r => r.RecipeID)));
            yield return WaitFor(() => peerData.ContainsKey("recipes" + (s.IsHost ? 2 : 1)), 10f);
            var theirs = (peerData.TryGetValue("recipes" + (s.IsHost ? 2 : 1), out var tr) ? tr : "").Split('|');
            fresh = fresh.Where(r => theirs.Contains(r.RecipeID)).ToList();   // (both copies have them loaded)
            if (!Check("found a recipe nobody knows", fresh.Count > 0)) { yield return Finish(""); yield break; }
            var notified = new List<object>();
            Action<UINotificationEvent> listen = ev => notified.Add(ev.Owner);
            Bus<UINotificationEvent>.Listeners += listen;
            // (the join's quiet time over)
            yield return WaitFor(() => !WorldNotices.Quieted, 30f);
            yield return Barrier("ready-to-learn", 40f);
            int shown0 = WorldNotices.Shown;

            if (s.IsHost) known.Add(fresh[0].RecipeID);   // (what RecipeUnlocker does, its own notification here)
            yield return Barrier("learned");
            if (!s.IsHost)
            {
                yield return WaitFor(() => known.Contains(fresh[0].RecipeID) && WorldNotices.Shown > shown0, 8f);
                Check("the host's recipe reached us", known.Contains(fresh[0].RecipeID));
                Check("with the game's notification, once", WorldNotices.Shown - shown0 == 1 && notified.Count(o => ReferenceEquals(o, fresh[0])) == 1,
                    $"shown {WorldNotices.Shown - shown0}, events {notified.Count}");
            }
            yield return Barrier("checked");
            if (fresh.Count >= 6)
            {
                int shown1 = WorldNotices.Shown;
                yield return new WaitForSecondsRealtime(3.5f);   // (a new burst window)
                if (s.IsHost) foreach (var r in fresh.Skip(1).Take(5)) known.Add(r.RecipeID);
                yield return Barrier("burst");
                if (!s.IsHost)
                {
                    yield return WaitFor(() => fresh.Skip(1).Take(5).All(r => known.Contains(r.RecipeID)), 8f);
                    yield return new WaitForSecondsRealtime(1f);
                    Check("all five reached us", fresh.Skip(1).Take(5).All(r => known.Contains(r.RecipeID)));
                    Check("a burst is cut short", WorldNotices.Shown - shown1 == 3, $"shown {WorldNotices.Shown - shown1}, quiet {WorldNotices.Quiet}");
                }
            }
            else Plugin.Log.LogInfo($"[test] only {fresh.Count} unknown recipes: burst not tested");
            Bus<UINotificationEvent>.Listeners -= listen;
            yield return Barrier("done");
            yield return Finish("recipes learned by others are announced");
        }

        // worldsurvey (one copy): what the scene's quests, teleporters, rooms and NPCs do, for these syncs: each
        // quest's objectives (kind, count) and what in the scene feeds them; teleporters' and rooms' SetActive calls
        // and whether they're shared; NPCs (turn to the talker, bubble, variable kinds).
        [Scenario("worldsurvey")]
        private static IEnumerator WorldSurvey()
        {
            yield return WaitInGame();
            yield return WaitFor(QuestModelReady, 30f);
            yield return new WaitForSecondsRealtime(3f);
            var sb = new StringBuilder();
            sb.AppendLine($"scene={Game.SceneName}");
            foreach (var q in QuestManager.model.Quests.Values.OrderBy(q => q.data.questID, StringComparer.Ordinal))
            {
                sb.AppendLine($"quest {q.data.questID} steps={q.data.steps.Count}");
                foreach (var st in q.data.steps)
                    foreach (var o in st.questObjectives)
                        sb.AppendLine($"  objective {o.ID} {o.GetType().Name} count={o.Count}{(o.Count > 1 && !(o is QuestObjective.KillEnemies) && !(o is QuestObjective.CollectItem) ? "  <-- counted steps (each player's own?)" : "")}");
            }
            foreach (var oi in Object.FindObjectsByType<ObjectiveIncrementer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"incrementer '{FullPath(oi.transform)}' objective={oi.objectiveID}");
            foreach (var h in Object.FindObjectsByType<QuestEventsHandler>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"events-handler '{FullPath(h.transform)}' objective={h.hummingSphereObjective}");
            foreach (var l in Object.FindObjectsByType<QuestListener>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"listener '{FullPath(l.transform)}' quest={l.quest?.questID} collect={l.itemCollectingListeners?.Count}");
            foreach (var c in Object.FindObjectsByType<ObjectiveChecker>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"objective-check '{FullPath(c.transform)}' {c.objective}=={c.stateToMatch} when={c.checkWhen}");
            foreach (var c in Object.FindObjectsByType<QuestChecker>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"quest-check '{FullPath(c.transform)}' {c.quest?.questID}=={c.stateToMatch} when={c.checkWhen}");
            var sa = ScriptedActiveSync.Instance;
            var owners = Object.FindObjectsByType<Teleporter>(FindObjectsInactive.Include, FindObjectsSortMode.None).Cast<Component>()
                .Concat(Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None)).OrderBy(c => FullPath(c.transform));
            foreach (var c in owners)
            {
                var shared = sa?.TargetsOf(c) ?? new List<GameObject>();
                foreach (var f in AllFields(c.GetType()).Where(f => typeof(UnityEventBase).IsAssignableFrom(f.FieldType)))
                {
                    var ev = f.GetValue(c) as UnityEventBase;
                    for (int i = 0; ev != null && i < ev.GetPersistentEventCount(); i++)
                    {
                        var t = ev.GetPersistentTarget(i);
                        string tn = t is Component tc ? $"{tc.GetType().Name}@{FullPath(tc.transform)}" : t is GameObject go ? $"GameObject@{FullPath(go.transform)}" : t?.ToString() ?? "null";
                        bool share = t is GameObject g && shared.Contains(g);
                        sb.AppendLine($"{c.GetType().Name} '{FullPath(c.transform)}' {f.Name} -> {tn}.{ev.GetPersistentMethodName(i)}{(share ? "  [shared]" : "")}");
                    }
                }
            }
            foreach (var d in Object.FindObjectsByType<DialogueTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(d => FullPath(d.transform)))
                sb.AppendLine($"npc '{FullPath(d.transform)}' uid={d.uniqueID} face={d.facePlayer} enemy={(d.GetComponent<Enemy>() != null)} bubble={(d.selectArrow != null)} " +
                              $"vars={string.Join(",", d.GetVariables().Select(v => v.key + ":" + (v.varType?.GetType().Name ?? "-")))}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, $"worldsurvey-{Game.SceneName}.txt"), sb.ToString());
            Plugin.Log.LogInfo("[dev] world survey written");
            Report(true, "surveyed");
            DevHarness.Quit("done");
        }
    }
}
