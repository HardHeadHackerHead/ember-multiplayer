using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // Secret rooms (RoomHider): the dark cover over a room until a door opens or a wall breaks. Revealed for one,
    // revealed for all (the game saves these with the world, so the host's save keeps them too).
    internal class RoomHiderSync : ObjectHandler
    {
        public static RoomHiderSync Instance { get; private set; }
        public RoomHiderSync() { Instance = this; }

        public override string Kind => "hider";

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<RoomHider>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w) => w.Put(((RoomHider)obj).isHiding == false);

        public override void Apply(Component obj, NetDataReader r)
        {
            var h = (RoomHider)obj;
            bool revealed = r.GetBool();
            if (revealed && h.isHiding != false) h.Reveal();
            else if (!revealed && h.isHiding == false) h.Hide();
        }

        // Reveal/Hide fade first and set the state at the end: report where it's going.
        public static void Send(RoomHider h, bool revealed)
        {
            if (Instance == null || ObjectSync.Applying || !ObjectSync.Active) return;
            var keep = h.isHiding;
            h.isHiding = !revealed;
            try { Interactables.Changed(Instance, h); }
            finally { h.isHiding = keep; }
        }
    }

    [HarmonyPatch(typeof(RoomHider), nameof(RoomHider.Reveal))]
    internal static class RoomHiderRevealPatch
    {
        private static void Prefix(RoomHider __instance, out bool __state) => __state = __instance.isHiding != false;
        private static void Postfix(RoomHider __instance, bool __state)
        {
            if (__state) RoomHiderSync.Send(__instance, true);
        }
    }

    [HarmonyPatch(typeof(RoomHider), nameof(RoomHider.Hide))]
    internal static class RoomHiderHidePatch
    {
        private static void Prefix(RoomHider __instance, out bool __state) => __state = __instance.isHiding != true;
        private static void Postfix(RoomHider __instance, bool __state)
        {
            if (__state) RoomHiderSync.Send(__instance, false);
        }
    }
}
