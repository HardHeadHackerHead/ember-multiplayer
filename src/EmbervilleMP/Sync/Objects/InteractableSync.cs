using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // Switches and levers (InteractableSwitch, InteractableLever): on / off / disabled. Radio groups
    // (InteractableGroup) need nothing more: the group turns the others off through the same methods, on every copy.
    internal class SwitchSync : ObjectHandler
    {
        public static SwitchSync Instance { get; private set; }
        public SwitchSync() { Instance = this; }

        public override string Kind => "switch";
        public override bool Persistent => false;   // the game doesn't save switches either

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<InteractableSwitch>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w) => w.Put((byte)((InteractableSwitch)obj).state);

        public override void Apply(Component obj, NetDataReader r)
        {
            var s = (InteractableSwitch)obj;
            var state = (SwitchState)r.GetByte();
            if (s.state == state) return;
            // The game's own methods: sprite, sound and the switch's events (traps, doors, its group).
            switch (state)
            {
                case SwitchState.On: s.TurnOn(); break;
                case SwitchState.Off: s.TurnOff(); break;
                default: s.TurnDisabled(); break;
            }
        }
    }

    [HarmonyPatch]
    internal static class SwitchChangePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(InteractableSwitch), nameof(InteractableSwitch.TurnOn));
            yield return AccessTools.Method(typeof(InteractableSwitch), nameof(InteractableSwitch.TurnOff));
            yield return AccessTools.Method(typeof(InteractableSwitch), nameof(InteractableSwitch.TurnDisabled));
        }

        private static void Postfix(InteractableSwitch __instance) => Interactables.Changed(SwitchSync.Instance, __instance);
    }

    // Pressure plates (GroundButton): only a player's own character presses them, so a change here is ours.
    internal class GroundButtonSync : ObjectHandler
    {
        public static GroundButtonSync Instance { get; private set; }
        public GroundButtonSync() { Instance = this; }

        public override string Kind => "button";
        public override bool Persistent => false;

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<GroundButton>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w) => w.Put((byte)((GroundButton)obj).state);

        public override void Apply(Component obj, NetDataReader r)
        {
            var b = (GroundButton)obj;
            var state = (SwitchState)r.GetByte();
            if (b.state == state) return;
            switch (state)
            {
                case SwitchState.On: b.TurnOn(); break;
                case SwitchState.Off: b.TurnOff(); break;
                default: b.TurnDisabled(); break;
            }
        }
    }

    [HarmonyPatch]
    internal static class GroundButtonPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GroundButton), nameof(GroundButton.TurnOn));
            yield return AccessTools.Method(typeof(GroundButton), nameof(GroundButton.TurnOff));
            yield return AccessTools.Method(typeof(GroundButton), nameof(GroundButton.TurnDisabled));
        }

        // What the plate's events change (a door) is our player's doing too.
        private static void Prefix(out bool __state) => __state = Interactables.BeginLocal();
        private static void Postfix(GroundButton __instance) => Interactables.Changed(GroundButtonSync.Instance, __instance);
        private static void Finalizer(bool __state) => ObjectSync.LocalAction = __state;
    }

    // Torches, candles and lamps a player can light or put out (InteractableLight). Only players' toggles are
    // shared: the day/night schedule runs on every copy from the shared time of day.
    internal class LightSync : ObjectHandler
    {
        public static LightSync Instance { get; private set; }
        public LightSync() { Instance = this; }

        public override string Kind => "light";
        public override bool Persistent => false;

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<InteractableLight>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w) => w.Put(((InteractableLight)obj).IsOn);

        public override void Apply(Component obj, NetDataReader r)
        {
            var l = (InteractableLight)obj;
            bool on = r.GetBool();
            if (l.lightsFolder != null && l.IsOn != on) l.SwitchLight(on);
        }
    }

    [HarmonyPatch(typeof(InteractableLight), nameof(InteractableLight.SwitchLight))]
    internal static class LightSwitchPatch
    {
        private static void Postfix(InteractableLight __instance)
        {
            if (ObjectSync.LocalAction) Interactables.Changed(LightSync.Instance, __instance);
        }
    }

    // Item locks (ItemRequester): "unlocked" once someone had the items. The others run the unlock's effects
    // without paying for it.
    internal class ItemRequesterSync : ObjectHandler
    {
        public static ItemRequesterSync Instance { get; private set; }
        public ItemRequesterSync() { Instance = this; }
        public readonly HashSet<ItemRequester> Unlocked = new HashSet<ItemRequester>();

        public override string Kind => "requester";
        public override bool Persistent => false;

        public override IEnumerable<Component> FindAll()
        {
            Unlocked.RemoveWhere(x => x == null);
            return Object.FindObjectsByType<ItemRequester>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        public override void Capture(Component obj, NetDataWriter w) => w.Put(Unlocked.Contains((ItemRequester)obj));

        public override void Apply(Component obj, NetDataReader r)
        {
            var q = (ItemRequester)obj;
            if (!r.GetBool() || !Unlocked.Add(q)) return;
            q.onUnlockEvent.Invoke();
            q.titlePivot?.SetActive(false);
        }

        // Same test as ItemRequester.OnInteract.
        public static bool WillUnlock(ItemRequester q)
        {
            var inv = Singleton<Inventory>.main;
            return inv != null && q.requiredItems.All(i => inv.ContainsItem(i, out _));
        }
    }

    [HarmonyPatch(typeof(ItemRequester), nameof(ItemRequester.OnInteract))]
    internal static class ItemRequesterPatch
    {
        private static void Prefix(ItemRequester __instance, out bool __state) => __state = ItemRequesterSync.WillUnlock(__instance);

        private static void Postfix(ItemRequester __instance, bool __state)
        {
            if (!__state || ItemRequesterSync.Instance == null) return;
            ItemRequesterSync.Instance.Unlocked.Add(__instance);
            Interactables.Changed(ItemRequesterSync.Instance, __instance);
        }
    }

    // Our player interacting with something (E on a lever, a torch, a lock...): what changes because of it is ours
    // to send, even on a copy that doesn't run the scene.
    [HarmonyPatch]
    internal static class InteractLocalActionPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(InteractablesHandler), nameof(InteractablesHandler.OnInteract));
            yield return AccessTools.Method(typeof(InteractableSwitch), nameof(InteractableSwitch.OnInteract));
            yield return AccessTools.Method(typeof(InteractableLever), nameof(InteractableLever.OnInteract));
            yield return AccessTools.Method(typeof(SimpleInteractable), nameof(SimpleInteractable.OnInteract));
            yield return AccessTools.Method(typeof(InteractableLight), nameof(InteractableLight.OnInteract));
            yield return AccessTools.Method(typeof(ItemRequester), nameof(ItemRequester.OnInteract));
        }

        private static void Prefix(out bool __state) => __state = Interactables.BeginLocal();
        private static void Finalizer(bool __state) => ObjectSync.LocalAction = __state;
    }

    internal static class Interactables
    {
        // Marks what follows as our player's doing (unless we're applying someone else's change); returns the old flag.
        public static bool BeginLocal()
        {
            bool before = ObjectSync.LocalAction;
            if (!ObjectSync.Applying) ObjectSync.LocalAction = true;
            return before;
        }

        public static void Changed(ObjectHandler h, Component obj)
        {
            if (h == null || obj == null || ObjectSync.Applying || !ObjectSync.Active || NetIds.IsRuntimeSpawned(obj.transform)) return;
            ObjectSync.Instance.Changed(h, obj);
        }
    }
}
