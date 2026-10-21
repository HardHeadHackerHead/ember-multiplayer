using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Shared world progress (phase B): story flags, unlock lists, dialogue variables, time of day, quests.
    // Everything here only grows (a flag once set, a lore page read, an objective's progress), so every copy
    // merges what the others send with "most advanced wins" and they all converge; the host's copy is the one
    // that gets saved. Time of day is the host's (scripts setting it, midnight, pauses: WorldClock).
    internal class WorldState
    {
        public static WorldState Instance { get; private set; }
        public static bool Applying;   // set while we apply something from the network (no echo)
        public int BoolsSent, BoolsApplied, ListAddsApplied, QuestSnapshotsSent, QuestMerges, TimeSyncs, DialogueVarsApplied, RemoteKills;
        public static int QuestSnapshotsReceived, CarriedGrants, CarriedMerges;

        private readonly Session session;
        private float nextQuestCheck, nextListCheck, nextTimeSend;
        private string lastQuestHash = "";
        private readonly Dictionary<string, int> listCounts = new Dictionary<string, int>();
        private int lastTinetia = -1;

        private static readonly string[] Lists = { "lore", "recipes", "blueprints" };

        public WorldState(Session session)
        {
            Instance = this;
            this.session = session;
            session.On(Msg.WorldBool, OnBool);
            session.On(Msg.WorldListAdd, OnListAdd);
            session.On(Msg.WorldTinetia, OnTinetia);
            session.On(Msg.DialogueVar, OnDialogueVar);
            session.On(Msg.TimeSync, OnTime);
            session.On(Msg.QuestSnapshot, OnQuests);
            WorldClock.Hook(session);
            CharacterRewards.Hook(session);
            CartographerSync.Hook(session);
            WorldNotices.Hook(session);
            session.Welcomed += () => { lastQuestHash = ""; listCounts.Clear(); lastTinetia = -1; };
            session.PlayerJoined += _ => { lastQuestHash = ""; };   // newcomers get our quests at once
        }

        public void Update()
        {
            if (!session.Ready || !GameData.HasWorldData || GuestWorld.Switching) return;
            float now = Time.realtimeSinceStartup;
            // A guest's own world isn't the session's: nothing of it goes out before it plays in the host's.
            if (now >= nextListCheck && InSharedWorld) { nextListCheck = now + 1f; CheckLists(); }
            if (QuestsReady)
            {
                ApplyCarried();
                ApplyPendingQuests();
                if (now >= nextQuestCheck) { nextQuestCheck = now + 0.5f; CheckQuests(); }
            }
            if (session.IsHost && Game.InGame && now >= nextTimeSend) { nextTimeSend = now + 2f; SendTime(); }
            WorldClock.Update();
            LiveFlags.Update();
            CartographerSync.Update();
            CharacterRewards.Update();
        }

        // ---------------- story flags ----------------
        // Flags only ever get set. Every write in the game's code sets one to true, and they all mean "this happened"
        // (met, defeated, freed, unlocked, cutscene seen...); a false comes from data (SetGameBool components and events,
        // graph actions, dialogue variables) re-initialising a copy's own scene, and must not take the others' progress
        // back. So a false is neither sent nor merged (the copy that set it keeps it, until the world reloads).
        // A flag that arrives re-runs the checks in our loaded scene that depend on it (LiveFlags).

        public void OnLocalBool(BoolID id, bool value)
        {
            if (!InSharedWorld) return;   // a world that isn't the session's (a guest's own, one picked on the menu)
            if (!value) return;
            session.Send(Msg.WorldBool, w => { w.Put((int)id); w.Put(value); });
            BoolsSent++;
        }

        private void OnBool(byte from, NetDataReader r)
        {
            var id = (BoolID)r.GetInt();
            bool value = r.GetBool();
            if (!GameData.HasWorldData || !InSharedWorld || !value || GameData.gameBools.IsTrue(id)) return;
            var checks = LiveFlags.Before(id);
            Applying = true;
            try { GameData.gameBools[id] = true; }
            finally { Applying = false; }
            BoolsApplied++;
            LiveFlags.After(checks);
        }

        // ---------------- lists that only grow ----------------

        private static List<string> ListOf(string name)
        {
            var wd = GameData.worldData;
            switch (name)
            {
                case "lore": return wd.readLoreContents;
                case "recipes": return wd.knownRecipes;
                case "blueprints": return wd.knownBlueprints;
            }
            return null;
        }

        private void CheckLists()
        {
            foreach (string name in Lists)
            {
                var list = ListOf(name);
                if (list == null) continue;
                listCounts.TryGetValue(name, out int seen);
                if (list.Count > seen)
                    for (int i = seen; i < list.Count; i++)
                    {
                        string item = list[i];
                        session.Send(Msg.WorldListAdd, w => { w.Put(name); w.Put(item); });
                    }
                listCounts[name] = list.Count;
            }
            int t = (int)GameData.worldData.TinetiaUnlock.Value;
            if (lastTinetia >= 0 && t != lastTinetia) session.Send(Msg.WorldTinetia, w => w.Put(t));
            lastTinetia = t;
        }

        private void OnListAdd(byte from, NetDataReader r)
        {
            string name = r.GetString(), item = r.GetString();
            var list = GameData.HasWorldData && InSharedWorld ? ListOf(name) : null;
            if (list == null || list.Contains(item)) return;
            list.Add(item);
            listCounts[name] = list.Count;
            ListAddsApplied++;
            if (name == "recipes") WorldNotices.RecipeLearned(item);   // (the game only notifies the copy that learned it)
        }

        private void OnTinetia(byte from, NetDataReader r)
        {
            int t = r.GetInt();
            if (!GameData.HasWorldData || !InSharedWorld) return;
            int merged = (int)GameData.worldData.TinetiaUnlock.Value | t;
            lastTinetia = merged;
            GameData.worldData.TinetiaUnlock.Value = (TinetiaUnlocks)merged;
        }

        // ---------------- dialogue variables ----------------

        // Only saved dialogue bools are the world's: a "Runtime Bool (not saved)" belongs to the conversation going on
        // (a teammate's talk set ours back mid-conversation), and a "Game Bool" is a story flag (shared as one, grow-only).
        private static bool SharedVar(DialogueTrigger t, string key) => t.variables?.Find(x => x.key == key)?.varType is DialogueSimpleBool;

        public void OnLocalDialogueVar(DialogueTrigger trigger, string key, bool value)
        {
            if (string.IsNullOrEmpty(trigger.uniqueID) || !InSharedWorld || !SharedVar(trigger, key)) return;
            session.Send(Msg.DialogueVar, w => { w.Put(trigger.uniqueID); w.Put(key); w.Put(value); });
        }

        private void OnDialogueVar(byte from, NetDataReader r)
        {
            string id = r.GetString(), key = r.GetString();
            bool value = r.GetBool();
            if (!GameData.HasWorldData || !InSharedWorld) return;
            Applying = true;
            try
            {
                bool live = false;
                foreach (var t in UnityEngine.Object.FindObjectsByType<DialogueTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (t.uniqueID == id)
                    {
                        live = true;
                        if (SharedVar(t, key)) { t.SetVariableValue(key, value); GameData.worldData.SaveDialogueInternalBools(t); }
                    }
                if (!live)
                {
                    // Not in this scene: change the saved copy, which the trigger reads when its scene loads.
                    var saved = GameData.worldData.savedDialogueVars.FirstOrDefault(x => x.id == id);
                    if (saved == null) { saved = new SavedDialogueTrigger { id = id }; GameData.worldData.savedDialogueVars.Add(saved); }
                    int at = saved.internalVars.FindIndex(x => x.Key == key);
                    var entry = new BoolNamed { Key = key, Value = value };
                    if (at < 0) saved.internalVars.Add(entry);
                    else saved.internalVars[at] = entry;
                }
            }
            finally { Applying = false; }
            DialogueVarsApplied++;
        }

        // ---------------- time of day (the host's) ----------------

        private void SendTime()
        {
            if (!Singleton<TimeManager>.HasMain) return;
            var tm = Singleton<TimeManager>.main;
            session.Send(Msg.TimeSync, w => { w.Put(tm.dayCount); w.Put(tm.dayTime); }, DeliveryMethod.Sequenced);
        }

        // Host: send the clock with the next update (a guest's script just set it).
        public void SendTimeSoon() => nextTimeSend = 0f;

        private void OnTime(byte from, NetDataReader r)
        {
            int day = r.GetInt();
            float t = r.GetFloat();
            if (session.IsHost || from != Protocol.HostId || !Singleton<TimeManager>.HasMain || WorldClock.Holding) return;
            if (float.IsNaN(t) || t < 0f || t >= 24f) return;
            var tm = Singleton<TimeManager>.main;
            float diff = Mathf.Abs((tm.dayCount * 24f + tm.dayTime) - (day * 24f + t));
            if (diff < 0.05f) return;   // ~3 game minutes
            WorldClock.Correct(tm, day, t);   // (across midnight: the game's own new day)
            TimeSyncs++;
        }

        // ---------------- quests ----------------
        // Every started quest (state, step, objectives' states and progress) goes out on change and is merged "most
        // advanced wins"; completing a quest through a merge runs the game's own completion, rewards included (see
        // QuestRewards for who gets what). A guest only sends and merges once it plays in the host's world (before
        // that its quests are its own world's); snapshots arriving meanwhile, or during a load, wait in `pending`.

        private class QuestInfo
        {
            public QuestState Main;
            public int Step;
            public List<(string id, QuestState state, int progress)> Objs;
        }

        private readonly Dictionary<byte, byte[]> pendingQuests = new Dictionary<byte, byte[]>();

        // This copy plays the session's world (the host's; or each its own in the stage 1/2 tests). A guest: the host's
        // world, loaded under the throw-away name (not its own, not one picked on the main menu). The host: whichever
        // world it plays, since it can only change worlds through the main menu, which ends the session (MPRunner);
        // on the menu itself there is no session world.
        public static bool InSharedWorld
        {
            get
            {
                if (Args.Has("own-world")) return true;
                if (GuestWorld.Active) return GameData.GetWorldPrefix == GuestWorld.GuestPrefix;
                var s = Session.Current;
                return s != null && s.IsHost && Game.SceneName != Game.MainMenuScene && Game.SceneName != Game.SplashScene;
            }
        }

        // The quest model is the session world's and fully loaded (not mid-scene-load, not mid-world-swap).
        private static bool QuestsReady =>
            Game.InGame && !GuestWorld.Switching && InSharedWorld && QuestManager.model != null &&
            Singleton<QuestManager>.HasMain && Singleton<QuestManager>.main.isInitialized &&
            !(Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading);

        private static int Rank(QuestState s) => s == QuestState.Inactive ? 0 : s == QuestState.Active ? 1 : 2;

        private static Dictionary<string, QuestInfo> CaptureQuests(QuestModel model)
        {
            var all = new Dictionary<string, QuestInfo>();
            foreach (var q in model.Quests.Values.Where(q => q.mainState.Value != QuestState.Inactive).OrderBy(q => q.data.questID, StringComparer.Ordinal))
                all[q.data.questID] = new QuestInfo
                {
                    Main = q.mainState.Value,
                    Step = q.stepIndex,
                    Objs = q.data.steps.SelectMany(st => st.questObjectives)
                        .Where(o => o.state.Value != QuestState.Inactive || o.Progress > 0)
                        .Select(o => (o.ID ?? "", o.state.Value, o.Progress)).ToList(),
                };
            return all;
        }

        // Next check sends everything again even if nothing changed (tests: re-applying a snapshot).
        public void ResendQuests() => lastQuestHash = "";

        private void CheckQuests()
        {
            var model = QuestManager.model;
            if (model == null) return;
            var w = new NetDataWriter();
            var sb = new StringBuilder();
            var quests = CaptureQuests(model);
            w.Put(quests.Count);
            foreach (var kv in quests)
            {
                var q = kv.Value;
                w.Put(kv.Key);
                w.Put((byte)q.Main);
                w.Put((short)q.Step);
                sb.Append(kv.Key).Append((int)q.Main).Append(q.Step);
                w.Put((short)q.Objs.Count);
                foreach (var o in q.Objs)
                {
                    w.Put(o.id);
                    w.Put((byte)o.state);
                    w.Put(o.progress);
                    sb.Append(o.id).Append((int)o.state).Append(o.progress);
                }
            }
            string hash = sb.ToString();
            if (hash == lastQuestHash) return;
            lastQuestHash = hash;
            session.Send(Msg.QuestSnapshot, x => x.Put(w.Data, 0, w.Length));
            QuestSnapshotsSent++;
        }

        // Snapshots are complete states: only each player's latest one matters.
        private void OnQuests(byte from, NetDataReader r)
        {
            pendingQuests[from] = r.GetRemainingBytes();
            QuestSnapshotsReceived++;
        }

        private void ApplyPendingQuests()
        {
            if (pendingQuests.Count == 0) return;
            foreach (var kv in pendingQuests.ToList())
            {
                pendingQuests.Remove(kv.Key);
                MergeSnapshot(new NetDataReader(kv.Value));
            }
        }

        private void MergeSnapshot(NetDataReader r)
        {
            var model = QuestManager.model;
            var checks = LiveQuests.Before();   // (the scene's quest checks see the merge: LiveFlags.cs)
            Applying = true;
            try
            {
                int n = r.GetInt();
                for (int i = 0; i < n; i++)
                {
                    string id = r.GetString();
                    var main = (QuestState)r.GetByte();
                    int step = r.GetShort();
                    int count = r.GetShort();
                    var objs = new List<(string id, QuestState state, int progress)>();
                    for (int k = 0; k < count; k++) objs.Add((r.GetString(), (QuestState)r.GetByte(), r.GetInt()));
                    if (model.Quests.TryGetValue(id, out var q)) MergeQuest(q, main, step, objs);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[quest] Merge failed: " + e.Message); }
            finally { Applying = false; }
            LiveQuests.After(checks);
        }

        private void MergeQuest(Quest q, QuestState main, int step, List<(string id, QuestState state, int progress)> objs)
        {
            var qm = Singleton<QuestManager>.main;
            bool changed = false;
            if (Rank(main) > Rank(q.mainState.Value))
            {
                if (q.mainState.Value == QuestState.Inactive) qm.SetQuestState(q.data.questID, QuestState.Active);
                if (main == QuestState.Failed || main == QuestState.Disabled) { qm.SetQuestState(q.data.questID, main); QuestMerges++; return; }
                changed = true;
            }
            if (q.mainState.Value != QuestState.Active) { if (changed) QuestMerges++; return; }
            // Steps the other copy has finished: finish their objectives here (the game then moves on by itself, and
            // completes the quest after the last one: rewards and all).
            for (int guard = 0; q.stepIndex < step && q.stepIndex < q.data.steps.Count && guard < 50; guard++)
            {
                int before = q.stepIndex;
                foreach (var o in q.data.steps[q.stepIndex].questObjectives) Complete(o);
                if (q.stepIndex == before) break;
                changed = true;
            }
            // The current step: more progress, more advanced states.
            if (q.mainState.Value == QuestState.Active && q.stepIndex < q.data.steps.Count)
                foreach (var o in q.data.steps[q.stepIndex].questObjectives)
                {
                    var remote = objs.FirstOrDefault(x => x.id == o.ID);
                    if (remote.id == null) continue;
                    if (remote.progress > o.Progress && o.state.Value == QuestState.Active)
                    {
                        o.Progress = remote.progress;
                        o.CallValueChanged();
                        changed = true;
                    }
                    if (Rank(remote.state) > Rank(o.state.Value)) { Complete(o); changed = true; }
                }
            if (main == QuestState.Completed && q.mainState.Value == QuestState.Active)
            {
                if (step >= q.data.steps.Count)
                {
                    // They went through every step but ours is stuck on one: finish it the game's way (rewards).
                    q.stepIndex = q.data.steps.Count;
                    q.CheckStep();
                }
                else qm.SetQuestState(q.data.questID, QuestState.Completed);   // set completed directly there: no rewards there either
                changed = true;
            }
            if (changed) QuestMerges++;
        }

        private static void Complete(QuestObjective o)
        {
            if (o.state.Value == QuestState.Completed) return;
            if (o.Progress < o.Count) o.Progress = o.Count;
            o.state.Value = QuestState.Completed;
            o.OnComplete();
            o.CallValueChanged();
        }

        // ---------------- a guest's quests across a reconnect ----------------
        // Rejoining reloads the host's world, quests included. What our copy had (progress made while cut off, which
        // quests we were part of) is carried over: quests we had going that the others completed meanwhile give us
        // their character rewards, and our own progress is merged into the reloaded world (and so reaches the host).

        private static Dictionary<string, QuestInfo> carried;
        private static QuestModel carriedFrom;

        public static void CarryQuests()
        {
            var model = QuestManager.model;
            if (model == null) return;
            carried = CaptureQuests(model);
            carriedFrom = model;
            Plugin.Log.LogInfo($"[quest] Carrying {carried.Count} quest states over the world reload");
        }

        // Not rejoining after all (gone home, or a different world): nothing to carry.
        public static void DropCarried()
        {
            carried = null;
            carriedFrom = null;
        }

        private void ApplyCarried()
        {
            if (carried == null || QuestManager.model == carriedFrom) return;
            var c = carried;
            carried = null;
            carriedFrom = null;
            var model = QuestManager.model;
            var checks = LiveQuests.Before();
            Applying = true;
            try
            {
                foreach (var kv in c)
                {
                    if (!model.Quests.TryGetValue(kv.Key, out var q)) continue;
                    if (kv.Value.Main == QuestState.Active && q.mainState.Value == QuestState.Completed)
                    {
                        // Completed while we were cut off; the world already has its effects.
                        QuestRewards.Grant(q, localCompletion: false, worldEffects: false);
                        CarriedGrants++;
                    }
                    int before = QuestMerges;
                    MergeQuest(q, kv.Value.Main, kv.Value.Step, kv.Value.Objs);
                    if (QuestMerges > before) CarriedMerges++;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[quest] Carrying quests over failed: " + e.Message); }
            finally { Applying = false; }
            LiveQuests.After(checks);
            Plugin.Log.LogInfo($"[quest] Carried quests applied: {CarriedGrants} caught-up rewards, {CarriedMerges} merges");
        }

        // ---------------- kills elsewhere (for "kill X" objectives) ----------------

        // A kill this copy didn't run (another scene, another room, or while loading).
        public void OnRemoteKill(EnemyType type)
        {
            if (!InSharedWorld) return;
            KillCredit.CountKill(type);
            RemoteKills++;
        }
    }

    // ---- hooks ----

    [HarmonyPatch]
    internal static class GameBoolSetPatch
    {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.PropertySetter(typeof(HookedDictionary<BoolID, bool>), "Item");

        private static void Prefix(HookedDictionary<BoolID, bool> __instance, BoolID key, out bool __state) =>
            __state = __instance.TryGetValue(key, out bool v) && v;

        private static void Postfix(HookedDictionary<BoolID, bool> __instance, BoolID key, bool value, bool __state)
        {
            if (WorldState.Applying || WorldState.Instance == null || __instance != GameData.gameBools) return;
            if (__state == value && __instance.ContainsKey(key)) return;
            WorldState.Instance.OnLocalBool(key, value);
        }
    }

    [HarmonyPatch(typeof(DialogueTrigger), nameof(DialogueTrigger.SetVariableValue))]
    internal static class DialogueVarPatch
    {
        private static void Postfix(DialogueTrigger __instance, string key, bool value)
        {
            if (!WorldState.Applying) WorldState.Instance?.OnLocalDialogueVar(__instance, key, value);
        }
    }
}
