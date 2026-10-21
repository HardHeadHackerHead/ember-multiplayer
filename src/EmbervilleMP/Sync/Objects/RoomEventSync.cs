using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // Room events (DungeonRoomEvent and its stages: waves, breakables to destroy, totems): the scene's runner
    // starts, completes and fails them; everyone else follows its state with the game's own methods (stage
    // spawns, texts, timer, the event's UnityEvents). A player who isn't the runner asks it to start one.
    // Their enemies are spawned on the runner and reach the others as EnemySpawn.
    internal class RoomEventSync : ObjectHandler
    {
        public static RoomEventSync Instance { get; private set; }
        public RoomEventSync() { Instance = this; }
        public static int Applied, Intents;

        public override string Kind => "roomevent";
        public override bool Persistent => false;   // not saved by the game

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<DungeonRoomEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w)
        {
            var e = (DungeonRoomEvent)obj;
            w.Put((byte)e.state);
            w.Put(e.currentStage);
        }

        public override void Apply(Component obj, NetDataReader r)
        {
            var e = (DungeonRoomEvent)obj;
            var state = (DungeonRoomEvent.EventState)r.GetByte();
            int stage = r.GetInt();
            var local = e.state;
            if (!e.gameObject.activeInHierarchy || e.stages == null) return;   // its coroutines can't run
            bool running = local == DungeonRoomEvent.EventState.inStage || local == DungeonRoomEvent.EventState.cooldown;
            switch (state)
            {
                case DungeonRoomEvent.EventState.ready:
                    if (local != DungeonRoomEvent.EventState.ready) { e.StopAllCoroutines(); e.InitialiseEvent(); }
                    break;
                case DungeonRoomEvent.EventState.inStage:
                case DungeonRoomEvent.EventState.cooldown:
                    if (stage < 0 || stage >= e.stages.Length) break;
                    if (local == DungeonRoomEvent.EventState.ready)
                    {
                        e.currentStage = stage;
                        e.StartEvent();
                    }
                    else if (running && (e.currentStage != stage || local == DungeonRoomEvent.EventState.cooldown) && state == DungeonRoomEvent.EventState.inStage)
                    {
                        // Jump to the runner's stage.
                        e.StopAllCoroutines();
                        DestroyStages(e, stage);
                        e.currentStage = stage;
                        e.StartNextStage();
                    }
                    break;
                case DungeonRoomEvent.EventState.completed:
                    if (local == DungeonRoomEvent.EventState.completed) break;
                    e.StopAllCoroutines();
                    DestroyStages(e, e.stages.Length);
                    e.currentStage = e.stages.Length;
                    e.CompleteEvent();
                    break;
                case DungeonRoomEvent.EventState.failed:
                    if (running) { e.StopAllCoroutines(); e.FailEvent(); }
                    break;
            }
            Applied++;
        }

        // Moving on to the runner's stage (or its end). Stages the game keeps once over stay (a totem puzzle's
        // totems live in its first stage).
        private static void DestroyStages(DungeonRoomEvent e, int targetStage)
        {
            if (e.instantiatedStages == null) return;
            foreach (var go in e.instantiatedStages.ToList())
                if (go != null && !TotemSync.KeepsStage(e, go, targetStage)) e.DestroyStage(go);
        }

        public override void OnIntent(Component obj, byte from, NetDataReader r)
        {
            var e = (DungeonRoomEvent)obj;
            Intents++;
            Plugin.Log.LogInfo($"[objects] #{from} starts room event {e.name}");
            e.StartEvent();
        }
    }

    internal static class RoomEventPatches
    {
        // Not the runner: our copy follows, it doesn't decide (not even while applying the runner's state).
        public static bool Follower => ObjectSync.Active && !ObjectSync.RunsScene;
        public static bool Failing;
    }

    [HarmonyPatch(typeof(DungeonRoomEvent), nameof(DungeonRoomEvent.StartEvent))]
    internal static class RoomEventStartPatch
    {
        private static bool Prefix(DungeonRoomEvent __instance)
        {
            if (!RoomEventPatches.Follower || ObjectSync.Applying) return true;
            if (__instance.state == DungeonRoomEvent.EventState.ready)
                ObjectSync.Instance.SendIntent(RoomEventSync.Instance, __instance, null);
            return false;
        }
    }

    [HarmonyPatch(typeof(DungeonRoomEvent), nameof(DungeonRoomEvent.StartNextStage))]
    internal static class RoomEventStagePatch
    {
        private static void Postfix(DungeonRoomEvent __instance) => Interactables.Changed(RoomEventSync.Instance, __instance);
    }

    [HarmonyPatch(typeof(DungeonRoomEvent), nameof(DungeonRoomEvent.CompleteEvent))]
    internal static class RoomEventCompletePatch
    {
        private static void Postfix(DungeonRoomEvent __instance) => Interactables.Changed(RoomEventSync.Instance, __instance);
    }

    // Failing may restart it at once (canRetry): send "failed" before the reset, then "ready".
    [HarmonyPatch(typeof(DungeonRoomEvent), nameof(DungeonRoomEvent.FailEvent))]
    internal static class RoomEventFailPatch
    {
        private static void Prefix() => RoomEventPatches.Failing = true;
        private static void Postfix(DungeonRoomEvent __instance) => Interactables.Changed(RoomEventSync.Instance, __instance);
        private static void Finalizer() => RoomEventPatches.Failing = false;
    }

    [HarmonyPatch(typeof(DungeonRoomEvent), nameof(DungeonRoomEvent.InitialiseEvent))]
    internal static class RoomEventResetPatch
    {
        private static void Prefix(DungeonRoomEvent __instance)
        {
            if (RoomEventPatches.Failing) Interactables.Changed(RoomEventSync.Instance, __instance);
        }
    }

    // A stage's own outcome (its enemies dead, its timer, our player's death) is the runner's call.
    [HarmonyPatch]
    internal static class RoomEventStageOutcomePatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DungeonRoomEventStage), nameof(DungeonRoomEventStage.CompleteStage));
            yield return AccessTools.Method(typeof(DungeonRoomEventStage), nameof(DungeonRoomEventStage.FailStage));
        }

        private static bool Prefix() => !RoomEventPatches.Follower;
    }

    // Enemy waves (EnemyWaveHandler): the runner spawns them (they reach the others as EnemySpawn); the others
    // only follow its state, so the start/complete/fail events (doors closing and opening) run everywhere.
    internal class EnemyWaveSync : ObjectHandler
    {
        public static EnemyWaveSync Instance { get; private set; }
        public EnemyWaveSync() { Instance = this; }
        private const byte Failed = 4;

        public override string Kind => "waves";
        public override bool Persistent => false;

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<EnemyWaveHandler>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w)
        {
            var h = (EnemyWaveHandler)obj;
            w.Put(RoomEventPatches.Failing ? Failed : (byte)h.state);
            w.Put(h.currentWave);
        }

        public override void Apply(Component obj, NetDataReader r)
        {
            var h = (EnemyWaveHandler)obj;
            byte state = r.GetByte();
            int wave = r.GetInt();
            var local = h.state;
            switch (state)
            {
                case (byte)EnemyWaveHandler.waveState.wave:
                case (byte)EnemyWaveHandler.waveState.cooldown:
                    if (local == EnemyWaveHandler.waveState.ready)
                    {
                        h.state = EnemyWaveHandler.waveState.wave;
                        h.onWaveEventStart.Invoke();
                    }
                    h.currentWave = wave;
                    break;
                case (byte)EnemyWaveHandler.waveState.completed:
                    if (local != EnemyWaveHandler.waveState.completed) h.CompleteWaveEvent();
                    break;
                case Failed:
                    if (local != EnemyWaveHandler.waveState.ready && local != EnemyWaveHandler.waveState.completed) h.FailedWaveEvent();
                    break;
            }
        }

        public override void OnIntent(Component obj, byte from, NetDataReader r)
        {
            Plugin.Log.LogInfo($"[objects] #{from} starts enemy waves {obj.name}");
            ((EnemyWaveHandler)obj).StartWaveEvent();
        }
    }

    [HarmonyPatch(typeof(EnemyWaveHandler), nameof(EnemyWaveHandler.StartWaveEvent))]
    internal static class WaveStartPatch
    {
        private static bool Prefix(EnemyWaveHandler __instance)
        {
            if (!RoomEventPatches.Follower || ObjectSync.Applying) return true;
            if (__instance.state == EnemyWaveHandler.waveState.ready)
                ObjectSync.Instance.SendIntent(EnemyWaveSync.Instance, __instance, null);
            return false;
        }
    }

    // A follower's own death doesn't fail the runner's waves.
    [HarmonyPatch(typeof(EnemyWaveHandler), nameof(EnemyWaveHandler.OnPlayerDeath))]
    internal static class WavePlayerDeathPatch
    {
        private static bool Prefix() => !RoomEventPatches.Follower;
    }

    [HarmonyPatch]
    internal static class WaveChangePatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(EnemyWaveHandler), nameof(EnemyWaveHandler.SpawnNextWave));
            yield return AccessTools.Method(typeof(EnemyWaveHandler), nameof(EnemyWaveHandler.CompleteWaveEvent));
        }

        private static void Postfix(EnemyWaveHandler __instance) => Interactables.Changed(EnemyWaveSync.Instance, __instance);
    }

    [HarmonyPatch(typeof(EnemyWaveHandler), nameof(EnemyWaveHandler.FailedWaveEvent))]
    internal static class WaveFailPatch
    {
        private static void Postfix(EnemyWaveHandler __instance)
        {
            RoomEventPatches.Failing = true;
            try { Interactables.Changed(EnemyWaveSync.Instance, __instance); }
            finally { RoomEventPatches.Failing = false; }
        }
    }
}
