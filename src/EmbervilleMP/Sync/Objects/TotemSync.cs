using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // Totem puzzles: a room event (DungeonRoomEvent) whose first stage (TotemFirstStage) lights a random sequence
    // of its totems and whose second stage (TotemSecondStage) fails at the first totem hit out of order and
    // completes once the whole sequence was hit. Left alone, every copy rolls its own sequence and counts only its
    // own player's hits.
    //
    // The scene's runner owns the puzzle: its sequence is everyone's (a follower plays the runner's, never rolls
    // one), a follower's hit is an intent to the runner, which counts it with the game's own code and sends the
    // progress and the hit (everyone sees that totem light up). Success, failure and the reset are the room
    // event's (RoomEventSync), so they run once on every copy. The totems live in the first stage's runtime copy
    // ("(Clone)", no id of their own): the puzzle's state is kept per room event.
    internal class TotemSync : ObjectHandler
    {
        public static TotemSync Instance { get; private set; }
        public TotemSync() { Instance = this; }
        public static int Intents, HitsSent, HitsCounted, SequencesAdopted, SequencesShown, LateTotems;

        internal class Puzzle
        {
            public int Run;          // sequences the runner rolled (one per attempt)
            public int[] Pattern;
            public int Hits = -1;    // totems hit in order in this attempt (-1: second stage not started)
            public int Seq;          // hits the runner handled, ever: a new one lights its totem everywhere
            public int LastHit = -1;
            public byte By;
            public int ShownRun;     // the attempt whose sequence our first stage played
        }

        private static readonly Dictionary<DungeonRoomEvent, Puzzle> puzzles = new Dictionary<DungeonRoomEvent, Puzzle>();
        // The runner is counting a hit another player sent (its player id); null: our own player's.
        private static byte? hitter;

        public override string Kind => "totems";
        public override bool Persistent => false;   // room events aren't saved by the game

        public override IEnumerable<Component> FindAll()
        {
            foreach (var k in puzzles.Keys.Where(k => k == null).ToList()) puzzles.Remove(k);
            return Object.FindObjectsByType<DungeonRoomEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None).Where(IsTotemEvent);
        }

        public override void Capture(Component obj, NetDataWriter w)
        {
            var p = Get((DungeonRoomEvent)obj);
            w.Put(p.Run);
            w.Put(p.Pattern?.Length ?? -1);
            if (p.Pattern != null) foreach (int i in p.Pattern) w.Put((byte)i);
            w.Put(p.Hits);
            w.Put(p.Seq);
            w.Put(p.LastHit);
            w.Put(p.By);
        }

        public override void Apply(Component obj, NetDataReader r)
        {
            var e = (DungeonRoomEvent)obj;
            var p = Get(e);
            int run = r.GetInt();
            int n = r.GetInt();
            int[] pattern = null;
            if (n >= 0)
            {
                pattern = new int[Net.NetGuard.Count(n, r)];
                for (int i = 0; i < n; i++) pattern[i] = r.GetByte();
            }
            int hits = r.GetInt(), seq = r.GetInt(), last = r.GetInt();
            byte by = r.GetByte();
            bool newHit = seq > p.Seq;
            p.Run = run;
            p.Pattern = pattern;
            p.Hits = hits;
            p.Seq = seq;
            p.LastHit = last;
            p.By = by;
            if (!e.gameObject.activeInHierarchy || e.stages == null) return;

            // Arrived after the totems' stage was over (late, or a new runner): the runner still shows them.
            int first = FirstIndex(e);
            bool running = e.state == DungeonRoomEvent.EventState.inStage || e.state == DungeonRoomEvent.EventState.cooldown;
            if (e.state == DungeonRoomEvent.EventState.completed || (running && first >= 0 && e.currentStage > first)) EnsureFirstStage(e);

            var stage1 = FirstStageOf(e);
            if (stage1 != null && pattern != null)
            {
                if (stage1.totemOrder == null || !stage1.totemOrder.SequenceEqual(pattern)) Adopt(stage1, pattern);
                // Our first stage started before the runner's sequence got here: play it now.
                if (stage1.state == DungeonRoomEventStage.StageState.active && p.ShownRun != run) Show(stage1, p);
            }
            var stage2 = SecondStageOf(e);
            if (stage2 != null && hits >= 0) stage2.totemsHit = hits;
            if (newHit && Session.Current != null && by != Session.Current.LocalId) LightTotem(e, last);
        }

        // A follower hit a totem: the runner counts it as if its own player had.
        public override void OnIntent(Component obj, byte from, NetDataReader r)
        {
            var e = (DungeonRoomEvent)obj;
            int index = r.GetInt();
            Intents++;
            var stage2 = SecondStageOf(e);
            if (stage2 == null)
            {
                Plugin.Log.LogInfo($"[objects] #{from} hit totem {index} of {e.name}, but its sequence isn't being played");
                return;
            }
            LightTotem(e, index);
            hitter = from;
            try { stage2.OnTotemHit(index); }
            finally { hitter = null; }
        }

        // ---- the puzzle's parts ----

        public static Puzzle Get(DungeonRoomEvent e)
        {
            if (!puzzles.TryGetValue(e, out var p)) puzzles[e] = p = new Puzzle();
            return p;
        }

        public static bool IsTotemEvent(DungeonRoomEvent e)
        {
            var stages = e.stages ?? (e.stagesContainer != null ? e.stagesContainer : e.transform).GetComponentsInChildren<DungeonRoomEventStage>(true);
            return stages.Any(s => s is TotemFirstStage || s is TotemSecondStage);
        }

        // The room event running this stage (stages are copies spawned into the event's stage container).
        public static DungeonRoomEvent EventOf(DungeonRoomEventStage stage)
        {
            var go = stage.gameObject;
            var e = stage.GetComponentInParent<DungeonRoomEvent>(true);
            if (e != null && e.instantiatedStages != null && e.instantiatedStages.Contains(go)) return e;
            return Object.FindObjectsByType<DungeonRoomEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(x => x.instantiatedStages != null && x.instantiatedStages.Contains(go)) ?? e;
        }

        private static int FirstIndex(DungeonRoomEvent e) =>
            e.stages == null ? -1 : System.Array.FindIndex(e.stages, s => s is TotemFirstStage);

        private static T Live<T>(DungeonRoomEvent e) where T : DungeonRoomEventStage =>
            e.instantiatedStages?.Where(go => go != null).Select(go => go.GetComponent<T>()).LastOrDefault(s => s != null);

        public static TotemFirstStage FirstStageOf(DungeonRoomEvent e) => Live<TotemFirstStage>(e);
        public static TotemSecondStage SecondStageOf(DungeonRoomEvent e) => Live<TotemSecondStage>(e);

        private static Totem[] TotemsOf(DungeonRoomEvent e) => FirstStageOf(e)?.totems ?? TotemSecondStage.totems;

        // The runner's sequence, where the game keeps it (the second stage reads the static copy).
        private static void Adopt(TotemFirstStage stage, int[] pattern)
        {
            stage.totemOrder = (int[])pattern.Clone();
            TotemSecondStage.totemOrder = stage.totemOrder;
            TotemSecondStage.totems = stage.totems;
            SequencesAdopted++;
        }

        private static void Show(TotemFirstStage stage, Puzzle p)
        {
            p.ShownRun = p.Run;
            stage.StartCoroutine(stage.LightUpTotems());
            SequencesShown++;
        }

        private static void LightTotem(DungeonRoomEvent e, int index)
        {
            var totems = TotemsOf(e);
            if (totems == null || index < 0 || index >= totems.Length) return;
            var t = totems[index];
            if (t != null && t.isActiveAndEnabled) t.LightUp();
        }

        // The game keeps a completed first stage (it holds the totems) unless it's set to be destroyed; a follower
        // jumping to the runner's later stage, or to its end, keeps it too, as over (it won't complete again).
        public static bool KeepsStage(DungeonRoomEvent e, GameObject go, int targetStage)
        {
            var stage = go.GetComponent<TotemFirstStage>();
            if (stage == null || stage.destroyOnCompletion) return false;
            int first = FirstIndex(e);
            if (first < 0 || targetStage <= first) return false;
            stage.state = DungeonRoomEventStage.StageState.completed;
            return true;
        }

        // We joined past the first stage (straight into the second, or after the end): spawn the totems as the
        // runner has them, without playing the sequence again.
        public static void EnsureFirstStage(DungeonRoomEvent e)
        {
            if (FirstStageOf(e) != null || e.stages == null || e.instantiatedStages == null) return;
            int i = FirstIndex(e);
            if (i < 0 || e.stages[i].destroyOnCompletion) return;
            var stage = (TotemFirstStage)Object.Instantiate(e.stages[i], e.stagesContainer);
            e.instantiatedStages.Add(stage.gameObject);
            stage.gameObject.SetActive(true);
            stage.InitialiseStage();
            stage.state = DungeonRoomEventStage.StageState.completed;
            TotemSecondStage.totems = stage.totems;
            var p = Get(e);
            if (p.Pattern != null) Adopt(stage, p.Pattern);
            LateTotems++;
            Plugin.Log.LogInfo($"[objects] Totems of {e.name} spawned late (the runner is past its sequence)");
        }

        // ---- hooks ----

        // The runner rolled a sequence (the game's own OnStart ran): it's everyone's.
        public static void Rolled(TotemFirstStage stage)
        {
            var e = EventOf(stage);
            if (e == null || stage.totemOrder == null) return;
            var p = Get(e);
            p.Run++;
            p.Pattern = (int[])stage.totemOrder.Clone();
            p.ShownRun = p.Run;
            p.Hits = -1;
            p.LastHit = -1;
            Interactables.Changed(Instance, e);
        }

        // A follower's first stage starts: play the runner's sequence if it's here, else wait for it (Apply).
        public static void FollowerStart(TotemFirstStage stage)
        {
            TotemSecondStage.totems = stage.totems;
            var e = EventOf(stage);
            if (e == null) return;
            var p = Get(e);
            if (p.Pattern != null && p.ShownRun != p.Run)
            {
                Adopt(stage, p.Pattern);
                Show(stage, p);
            }
            else Plugin.Log.LogInfo($"[objects] Totems of {e.name}: waiting for the runner's sequence");
        }

        // The runner counts a hit (its own player's, or one a follower sent). false: the game's code doesn't run.
        public static bool RunnerHit(TotemSecondStage stage, DungeonRoomEvent e, int index)
        {
            var order = TotemSecondStage.totemOrder;
            // Over (completed, failed, reset): a late hit counts for nothing.
            if (stage.state != DungeonRoomEventStage.StageState.active || order == null || stage.totemsHit >= order.Length) return false;
            var p = Get(e);
            p.Seq++;
            p.LastHit = index;
            p.By = hitter ?? Session.Current?.LocalId ?? (byte)0;
            HitsCounted++;
            if (order[stage.totemsHit] != index)
            {
                // Everyone sees the wrong totem light, then the reset. The game would keep counting a failed stage
                // (and could still "complete" it): it stops here.
                p.Hits = stage.totemsHit;
                Interactables.Changed(Instance, e);
                stage.FailStage();
                return false;
            }
            p.Hits = stage.totemsHit + 1;
            Interactables.Changed(Instance, e);   // before the game's code: the progress arrives before the completion
            return true;
        }
    }

    [HarmonyPatch(typeof(TotemFirstStage), nameof(TotemFirstStage.OnStart))]
    internal static class TotemSequencePatch
    {
        private static bool Prefix(TotemFirstStage __instance)
        {
            if (!RoomEventPatches.Follower) return true;   // the runner (or a solo game) rolls the sequence
            TotemSync.FollowerStart(__instance);
            return false;
        }

        private static void Postfix(TotemFirstStage __instance)
        {
            if (!RoomEventPatches.Follower && TotemSync.Instance != null) TotemSync.Rolled(__instance);
        }
    }

    // The second stage hooks the totems of the first: make sure they're there (and the game's statics point at
    // this puzzle's, if a scene had two).
    [HarmonyPatch(typeof(TotemSecondStage), nameof(TotemSecondStage.OnInitialise))]
    internal static class TotemSecondStageInitPatch
    {
        private static void Prefix(TotemSecondStage __instance)
        {
            if (!ObjectSync.Active) return;
            var e = TotemSync.EventOf(__instance);
            if (e == null) return;
            TotemSync.EnsureFirstStage(e);
            var first = TotemSync.FirstStageOf(e);
            if (first == null) return;
            TotemSecondStage.totems = first.totems;
            if (first.totemOrder != null) TotemSecondStage.totemOrder = first.totemOrder;
        }
    }

    // A follower joining mid-sequence: the runner's count, not zero.
    [HarmonyPatch(typeof(TotemSecondStage), nameof(TotemSecondStage.OnStart))]
    internal static class TotemSecondStageStartPatch
    {
        private static void Postfix(TotemSecondStage __instance)
        {
            if (!RoomEventPatches.Follower) return;
            var e = TotemSync.EventOf(__instance);
            if (e == null) return;
            var p = TotemSync.Get(e);
            if (p.Hits > 0) __instance.totemsHit = p.Hits;
        }
    }

    [HarmonyPatch(typeof(TotemSecondStage), nameof(TotemSecondStage.OnTotemHit))]
    internal static class TotemHitPatch
    {
        private static bool Prefix(TotemSecondStage __instance, int totemNumber)
        {
            if (!ObjectSync.Active || ObjectSync.Applying || TotemSync.Instance == null) return true;
            var e = TotemSync.EventOf(__instance);
            if (e == null) return true;
            if (RoomEventPatches.Follower)
            {
                // Our player hit it (it lit up here already): the runner counts it.
                ObjectSync.Instance.SendIntent(TotemSync.Instance, e, w => w.Put(totemNumber));
                TotemSync.HitsSent++;
                return false;
            }
            return TotemSync.RunnerHit(__instance, e, totemNumber);
        }
    }

    // A totem stage that's over (kept after the runner moved on, spawned late) doesn't complete or fail again,
    // e.g. when its sequence finishes playing after this copy took the scene over.
    [HarmonyPatch]
    internal static class TotemStageOverPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DungeonRoomEventStage), nameof(DungeonRoomEventStage.CompleteStage));
            yield return AccessTools.Method(typeof(DungeonRoomEventStage), nameof(DungeonRoomEventStage.FailStage));
        }

        private static bool Prefix(DungeonRoomEventStage __instance) =>
            !ObjectSync.Active || !(__instance is TotemFirstStage || __instance is TotemSecondStage)
            || __instance.state == DungeonRoomEventStage.StageState.active;
    }

    // A late arrival finishing the event with the runner's state: the totems stay, as on the runner.
    [HarmonyPatch(typeof(DungeonRoomEvent), nameof(DungeonRoomEvent.CompleteEvent))]
    internal static class TotemEventCompletePatch
    {
        private static void Postfix(DungeonRoomEvent __instance)
        {
            if (ObjectSync.Applying && TotemSync.IsTotemEvent(__instance)) TotemSync.EnsureFirstStage(__instance);
        }
    }
}
