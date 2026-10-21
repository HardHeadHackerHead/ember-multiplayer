using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // Traps run on every copy (each player is hurt by their own copy's traps, on their own character), so what
    // must match is their timing. GroundTrap and ShooterTrap time themselves from Time.time; in a session they
    // use the host's clock instead (TrapClock). Arrow shooters can also be switched on and off (by levers).
    internal class TrapSync : ObjectHandler
    {
        public static TrapSync Instance { get; private set; }
        public TrapSync() { Instance = this; }

        public override string Kind => "shooter";
        public override bool Persistent => false;

        // Attacks of the traps in the scene: their hits on another player's puppet are that player's business.
        private static readonly HashSet<Attack> trapAttacks = new HashSet<Attack>();
        public static int HazardHitsDropped;

        public override IEnumerable<Component> FindAll()
        {
            trapAttacks.Clear();
            foreach (var t in UnityEngine.Object.FindObjectsByType<GroundTrap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (t.damageCollider != null)
                    foreach (var d in t.damageCollider.GetComponentsInChildren<DamageOverTime>(true))
                        if (d.attack != null) trapAttacks.Add(d.attack);
            var shooters = UnityEngine.Object.FindObjectsByType<ShooterTrap>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var s in shooters)
                if (s.attack != null) trapAttacks.Add(s.attack);
            return shooters;
        }

        public static bool IsHazard(DamageData d) => d.Attack != null && trapAttacks.Contains(d.Attack);

        public override void Capture(Component obj, NetDataWriter w) => w.Put((byte)((ShooterTrap)obj).state);

        public override void Apply(Component obj, NetDataReader r)
        {
            var t = (ShooterTrap)obj;
            var state = (SwitchState)r.GetByte();
            if (state == SwitchState.On) t.TurnOn();
            else if (state == SwitchState.Off) t.TurnOff();
            else t.state = state;
        }

        public override void Init(Session session) => TrapClock.Init(session);
        public override void Update() => TrapClock.Update();
    }

    // The host's real time (not Time.time: that one stops in pauses and lags when frames are slow, differently on
    // every copy), as seen from here. Guests get it every second and correct for the one-way delay.
    internal static class TrapClock
    {
        private static Session session;
        private static float offset, nextSend;
        private static bool synced;
        private static readonly float[] samples = new float[8];
        private static int next;
        public static int Syncs;

        // Shared once we have the host's clock (always on the host); the game's Time.time when not in a session.
        public static bool Shared => session != null && Session.Current == session && session.Ready && (session.IsHost || synced);
        public static float Now => !Shared ? Time.time : session.IsHost ? Time.realtimeSinceStartup : Time.realtimeSinceStartup + offset;
        public static float Offset => offset;

        public static void Init(Session s)
        {
            session = s;
            synced = false;
            offset = 0f;
            next = 0;
            s.On(Msg.TrapClock, OnClock);
        }

        public static void Update()
        {
            if (session == null || !session.IsHost || !session.Ready) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextSend) return;
            nextSend = now + 1f;
            session.Send(Msg.TrapClock, w => w.Put(Time.realtimeSinceStartup), DeliveryMethod.Sequenced);
        }

        private static void OnClock(byte from, NetDataReader r)
        {
            float hostTime = r.GetFloat();
            if (session == null || session.IsHost || from != Protocol.HostId) return;
            float delay = session.Players.TryGetValue(Protocol.HostId, out var h) ? h.Ping / 1000f : 0f;
            float sample = hostTime + delay - Time.realtimeSinceStartup;
            // A late sample (a hitch on either side, a queued packet) only ever makes it smaller: keep the largest
            // of the last few.
            samples[next++ % samples.Length] = sample;
            offset = float.MinValue;
            for (int i = 0; i < Mathf.Min(next, samples.Length); i++) offset = Mathf.Max(offset, samples[i]);
            synced = true;
            Syncs++;
            if (Syncs == 1 || Syncs % 60 == 0) Plugin.Log.LogInfo($"[traps] clock sample {sample:F3} delay {delay:F3} offset {offset:F3}");
        }
    }

    // GroundTrap fires every hitInterval, phase startDelay, on Time.time. Same beats from the shared clock.
    [HarmonyPatch(typeof(GroundTrap), "Update")]
    internal static class GroundTrapUpdatePatch
    {
        private static readonly int HitHash = Animator.StringToHash("Hit");
        private static readonly Dictionary<GroundTrap, long> lastBeat = new Dictionary<GroundTrap, long>();

        public static void Forget(GroundTrap t) => lastBeat.Remove(t);

        private static bool Prefix(GroundTrap __instance)
        {
            if (!TrapClock.Shared) return true;
            var t = __instance;
            if (!t.isActive || t.hitInterval <= 0f) return false;
            long beat = (long)Math.Floor((TrapClock.Now - t.startDelay) / t.hitInterval);
            if (!lastBeat.TryGetValue(t, out long last))
            {
                // Just enabled: wait for the next beat, like the game's first full interval.
                if (lastBeat.Count > 512) foreach (var dead in lastBeat.Keys.Where(k => k == null).ToList()) lastBeat.Remove(dead);
                lastBeat[t] = beat;
                return false;
            }
            if (beat == last) return false;
            lastBeat[t] = beat;
            if (t.animator != null) t.animator.SetTrigger(HitHash);
            return false;
        }
    }

    [HarmonyPatch(typeof(GroundTrap), "OnEnable")]
    internal static class GroundTrapEnablePatch
    {
        private static void Postfix(GroundTrap __instance) => GroundTrapUpdatePatch.Forget(__instance);
    }

    // ShooterTrap's cycle: (Time.time / cycleTime + dephase) % 1, on the shared clock.
    [HarmonyPatch(typeof(ShooterTrap), "LateUpdate")]
    internal static class ShooterTrapClockPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var time = AccessTools.PropertyGetter(typeof(Time), nameof(Time.time));
            var shared = AccessTools.PropertyGetter(typeof(TrapClock), nameof(TrapClock.Now));
            foreach (var c in code)
            {
                if (c.Calls(time)) c.operand = shared;
                yield return c;
            }
        }
    }

    [HarmonyPatch]
    internal static class ShooterTrapSwitchPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ShooterTrap), nameof(ShooterTrap.TurnOn));
            yield return AccessTools.Method(typeof(ShooterTrap), nameof(ShooterTrap.TurnOff));
        }

        private static void Prefix(ShooterTrap __instance, out SwitchState __state) => __state = __instance.state;

        private static void Postfix(ShooterTrap __instance, SwitchState __state)
        {
            if (__instance.state != __state) Interactables.Changed(TrapSync.Instance, __instance);
        }
    }
}
