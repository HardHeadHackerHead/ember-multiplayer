using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // Repairable buildings (RepairableBuilding, the town's ruined houses) and destroyed portals (DestructedPortal):
    // the repaired state is shared, the player who repairs pays (the game's own Repair runs on their machine, taking
    // the resources from their bag and the shared chest). Everyone else plays the repair (effects, art swap, the
    // building's events, minus the ones that touch the repairer's inventory). Repairs never undo.
    internal class RepairSync : ObjectHandler
    {
        public static RepairSync Instance { get; private set; }
        public RepairSync() { Instance = this; }

        public override string Kind => "repair";
        public int Sent, Applied;

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<RepairableBuilding>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w) => w.Put(((RepairableBuilding)obj).isRepaired);

        public override void Apply(Component obj, NetDataReader r)
        {
            var b = (RepairableBuilding)obj;
            if (!r.GetBool() || b.isRepaired) return;
            b.isRepaired = true;
            Applied++;
            // Live (someone repaired it while we're here): play it. On arrival: just the repaired look, as a load does.
            if (RepairFx.Live && b.isActiveAndEnabled) b.StartCoroutine(RemoteRepair(b));
            else b.UpdateVisuals();
        }

        private static IEnumerator RemoteRepair(RepairableBuilding b)
        {
            b.RemoveActionBinding();
            if (b.panelMain != null && b.panelMain.activeSelf) b.panelMain.AnimExit();
            RepairFx.Play(b, b.gameObject);
            yield return new WaitForSeconds(b.transitionTime);
            b.UpdateVisuals();
            RepairFx.InvokeShared(b.onRepairCustomEvents);
        }

        public void OnLocalRepair(RepairableBuilding b)
        {
            ObjectSync.LocalAction = true;
            try { ObjectSync.Instance.Changed(this, b); Sent++; }
            finally { ObjectSync.LocalAction = false; }
        }
    }

    internal class PortalRepairSync : ObjectHandler
    {
        public static PortalRepairSync Instance { get; private set; }
        public PortalRepairSync() { Instance = this; }

        public override string Kind => "portalfix";

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<DestructedPortal>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w) => w.Put(((DestructedPortal)obj).isRepaired);

        public override void Apply(Component obj, NetDataReader r)
        {
            var p = (DestructedPortal)obj;
            if (!r.GetBool() || p.isRepaired) return;
            p.isRepaired = true;
            p.bindingDispose?.Dispose();
            if (p.panelMain != null && p.panelMain.activeSelf) p.panelMain.AnimExit();
            if (RepairFx.Live && p.isActiveAndEnabled) p.StartCoroutine(RemoteRepair(p));
            else p.onRepairEvent?.Invoke();   // the portal's own switch (it has no saved state of its own)
        }

        private static IEnumerator RemoteRepair(DestructedPortal p)
        {
            RepairFx.Play(p, p.gameObject);
            yield return new WaitForSeconds(p.transitionTime);
            p.onRepairEvent?.Invoke();
        }

        public void OnLocalRepair(DestructedPortal p)
        {
            ObjectSync.LocalAction = true;
            try { ObjectSync.Instance.Changed(this, p); }
            finally { ObjectSync.LocalAction = false; }
        }
    }

    internal static class RepairFx
    {
        // States that arrive right after a scene load are the remembered ones (snapshot), not a repair happening now.
        public static bool Live => Time.timeSinceLevelLoad > 3f;

        // The repair's sound and particles (by reflection: their types live in assemblies the mod doesn't reference).
        public static void Play(object owner, GameObject go)
        {
            try
            {
                var t = Traverse.Create(owner);
                var sfx = t.Field("transitionSfx").GetValue();
                sfx?.GetType().GetMethod("Post", new[] { typeof(GameObject) })?.Invoke(sfx, new object[] { go });
                var vfx = t.Field("transitionVfx").GetValue() as Component;
                if (vfx != null) vfx.GetType().GetMethod("Play", System.Type.EmptyTypes)?.Invoke(vfx, null);
            }
            catch (System.Exception e) { Plugin.Log.LogInfo("[objects] Repair fx: " + e.Message); }
        }

        // The building's events, without the ones about the repairer's own inventory or tutorials.
        public static void InvokeShared(CustomEventList list)
        {
            if (list?.events == null) return;
            foreach (var e in list.events)
            {
                if (e == null || e is CustomEvent.ConsumeFromInventory || e is CustomEventAddToInventory || e is CustomEvent.CallTutorial) continue;
                try { e.Invoke(); }
                catch (System.Exception ex) { Plugin.Log.LogWarning($"[objects] Repair event {e.GetType().Name}: {ex.Message}"); }
            }
        }
    }

    // Repair() starts the repair (isRepaired goes true at once) only when our player can pay; it's always our input.
    [HarmonyPatch(typeof(RepairableBuilding), nameof(RepairableBuilding.Repair))]
    internal static class RepairablePatch
    {
        private static void Prefix(RepairableBuilding __instance, out bool __state) => __state = __instance.isRepaired;
        private static void Postfix(RepairableBuilding __instance, bool __state)
        {
            if (!__state && __instance.isRepaired && ObjectSync.Active && !ObjectSync.Applying) RepairSync.Instance?.OnLocalRepair(__instance);
        }
    }

    [HarmonyPatch(typeof(DestructedPortal), nameof(DestructedPortal.Repair))]
    internal static class PortalRepairPatch
    {
        private static void Prefix(DestructedPortal __instance, out bool __state) => __state = __instance.isRepaired;
        private static void Postfix(DestructedPortal __instance, bool __state)
        {
            if (!__state && __instance.isRepaired && ObjectSync.Active && !ObjectSync.Applying) PortalRepairSync.Instance?.OnLocalRepair(__instance);
        }
    }
}
