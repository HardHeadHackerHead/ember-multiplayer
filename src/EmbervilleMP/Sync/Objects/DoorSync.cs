using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // Doors and gates (GateDoor): open/closed/locked/blocked, and which side the player opened them from.
    // Reference handler for the object framework: copy this shape for other kinds.
    internal class DoorSync : ObjectHandler
    {
        public static DoorSync Instance { get; private set; }
        public DoorSync() { Instance = this; }

        public override string Kind => "door";

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<GateDoor>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w)
        {
            var d = (GateDoor)obj;
            w.Put((byte)d.state);
            w.Put((byte)d.relativePosition);
        }

        public override void Apply(Component obj, NetDataReader r)
        {
            var d = (GateDoor)obj;
            var state = (DoorState)r.GetByte();
            d.relativePosition = (Direction)r.GetByte();
            if (d.state == state) return;
            // The game's own transitions, so it animates, sounds and moves its colliders.
            if (!d.gameObject.activeInHierarchy) { d.state = state; return; }   // ForceState runs on enable
            switch (state)
            {
                case DoorState.Opened: d.Open(); break;
                case DoorState.Closed: d.Close(); break;
                default: d.ChangeState(state); break;
            }
        }

        public void OnChanged(GateDoor d) => ObjectSync.Instance?.Changed(this, d);
    }

    // Our player pressing E on a door.
    [HarmonyPatch(typeof(GateDoor), nameof(GateDoor.OnInteract))]
    internal static class DoorInteractPatch
    {
        private static void Prefix() => ObjectSync.LocalAction = true;
        private static void Postfix() => ObjectSync.LocalAction = false;
    }

    // Every way a door changes state; the target state is known at the start (Open/Close finish after an animation).
    [HarmonyPatch(typeof(GateDoor), nameof(GateDoor.Open))]
    internal static class DoorOpenPatch
    {
        private static void Prefix(GateDoor __instance, out DoorState __state) => __state = __instance.state;
        private static void Postfix(GateDoor __instance, DoorState __state)
        {
            // Open() sets the state after its animation; report the destination now.
            if (__state == DoorState.Opened || ObjectSync.Applying || !ObjectSync.Active) return;
            var keep = __instance.state;
            __instance.state = DoorState.Opened;
            Send(__instance);
            __instance.state = keep;
        }

        internal static void Send(GateDoor d)
        {
            if (ObjectSync.Applying || !ObjectSync.Active) return;
            DoorSync.Instance?.OnChanged(d);
        }
    }

    [HarmonyPatch(typeof(GateDoor), nameof(GateDoor.Close))]
    internal static class DoorClosePatch
    {
        private static void Prefix(GateDoor __instance, out DoorState __state) => __state = __instance.state;
        private static void Postfix(GateDoor __instance, DoorState __state)
        {
            // Close() sets the state after its animation; report the destination now.
            if (__state == DoorState.Opened && !ObjectSync.Applying && ObjectSync.Active)
            {
                var keep = __instance.state;
                __instance.state = DoorState.Closed;
                DoorOpenPatch.Send(__instance);
                __instance.state = keep;
            }
        }
    }

    [HarmonyPatch(typeof(GateDoor), nameof(GateDoor.ChangeState))]
    internal static class DoorChangeStatePatch
    {
        private static void Postfix(GateDoor __instance, DoorState newState)
        {
            if (newState == DoorState.Locked || newState == DoorState.Blocked) DoorOpenPatch.Send(__instance);
        }
    }

    [HarmonyPatch(typeof(GateDoor), nameof(GateDoor.ForceState))]
    internal static class DoorForceStatePatch
    {
        private static void Prefix(GateDoor __instance, DoorState newState, out bool __state) => __state = __instance.state != newState;
        private static void Postfix(GateDoor __instance, bool __state)
        {
            if (__state) DoorOpenPatch.Send(__instance);   // only real changes (OnEnable/OnLoad re-apply the same state)
        }
    }
}
