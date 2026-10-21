using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // Breakables (DamageableObject: pots, crates, furniture, wooden doors, walls): health and destroyed.
    // Whoever breaks one gets its drop, as in the game; the others only see it break (no second drop).
    // The game rebuilds them when a scene loads, so the state is transient.
    internal class DestructibleSync : ObjectHandler
    {
        public static DestructibleSync Instance { get; private set; }
        public DestructibleSync() { Instance = this; }

        // Set while we break one because someone else did: its ItemDropper must not drop here.
        public static bool SuppressDrops;
        // The next capture is a rewind (a solo death): health goes back up exactly.
        public static bool Exact;
        public static int DropsSuppressed, Broken, BrokenApplied;

        public override string Kind => "breakable";
        public override bool Persistent => false;

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<DamageableObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w)
        {
            var d = (DamageableObject)obj;
            w.Put(d.health <= 0f);
            w.Put(d.health);
            w.Put(Exact);
        }

        public override void Apply(Component obj, NetDataReader r)
        {
            var d = (DamageableObject)obj;
            bool destroyed = r.GetBool();
            float health = r.GetFloat();
            bool exact = r.GetBool();
            if (destroyed)
            {
                if (d.health <= 0f) return;
                // The game's own destruction: art off, colliders off, effects, and what it's wired to (secret rooms).
                SuppressDrops = true;
                try { d.Destroy(); }
                finally { SuppressDrops = false; }
                BrokenApplied++;
                return;
            }
            if (d.health <= 0f)
            {
                // We broke it, they didn't: only a rewind brings it back (else our own message is on its way).
                if (!exact) return;
                d.health = health;
                d.isDead = false;
                d.gameObject.SetActive(true);
                d.Restore();
                return;
            }
            // Hits from two players at once: keep the lower health, so none is lost.
            float now = exact ? health : Mathf.Min(d.health, health);
            if (now < d.health && d.isActiveAndEnabled) d.onHitEvent.Invoke();
            d.health = now;
        }
    }

    // A hit on a breakable. Everything it sets off (its destruction, a secret room revealed) is sent by whoever
    // made it: on a copy that doesn't run the scene only our own character (or our projectiles) hits things here,
    // since enemy attacks are the runner's.
    [HarmonyPatch(typeof(DamageableObject), nameof(DamageableObject.ApplyDamage))]
    internal static class BreakableHitPatch
    {
        private static void Prefix(DamageableObject __instance, out (bool local, float health) __state) =>
            __state = (Interactables.BeginLocal(), __instance.health);

        private static void Postfix(DamageableObject __instance, (bool local, float health) __state)
        {
            // Destroy() reports the destruction itself.
            if (__instance.health > 0f && __instance.health != __state.health)
                Interactables.Changed(DestructibleSync.Instance, __instance);
        }

        private static void Finalizer((bool local, float health) __state) => ObjectSync.LocalAction = __state.local;
    }

    // Destroyed by a hit, or straight away (skeletons crumbling when walked over, scripts).
    [HarmonyPatch(typeof(DamageableObject), nameof(DamageableObject.Destroy), new[] { typeof(int) })]
    internal static class BreakableDestroyPatch
    {
        // A hit sets health to 0 before calling Destroy, so "already broken" is isDead (set by Destroy; an inactive
        // object is isDead too, then we just send it again).
        private static void Prefix(DamageableObject __instance, out (bool local, bool wasBroken) __state) =>
            __state = (Interactables.BeginLocal(), __instance.isDead && __instance.isActiveAndEnabled);

        private static void Postfix(DamageableObject __instance, (bool local, bool wasBroken) __state)
        {
            if (__state.wasBroken || ObjectSync.Applying) return;
            DestructibleSync.Broken++;
            Interactables.Changed(DestructibleSync.Instance, __instance);
        }

        private static void Finalizer((bool local, bool wasBroken) __state) => ObjectSync.LocalAction = __state.local;
    }

    // A solo death rewinds the room's breakables (GameManager.ResetScene); others arriving later must see that.
    [HarmonyPatch(typeof(DamageableObject), nameof(DamageableObject.ResetToLastState))]
    internal static class BreakableRewindPatch
    {
        private static void Prefix(DamageableObject __instance, out float __state) => __state = __instance.health;

        private static void Postfix(DamageableObject __instance, float __state)
        {
            if (__instance.health == __state) return;
            DestructibleSync.Exact = true;
            try { Interactables.Changed(DestructibleSync.Instance, __instance); }
            finally { DestructibleSync.Exact = false; }
        }
    }

    // The breaker's copy drops the loot; a breakable broken because someone else broke it drops nothing here.
    [HarmonyPatch(typeof(ItemDropper), nameof(ItemDropper.Drop))]
    internal static class BreakableDropPatch
    {
        private static bool Prefix()
        {
            if (!DestructibleSync.SuppressDrops) return true;
            DestructibleSync.DropsSuppressed++;
            return false;
        }
    }

    [HarmonyPatch(typeof(ItemDropper), nameof(ItemDropper.TryDrop))]
    internal static class BreakableTryDropPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!DestructibleSync.SuppressDrops) return true;
            DestructibleSync.DropsSuppressed++;
            __result = false;
            return false;
        }
    }
}
