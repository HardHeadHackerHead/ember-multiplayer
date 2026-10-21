using EmbervilleMP.Sync;
using HarmonyLib;

namespace EmbervilleMP.Patches
{
    // RandomEventSelector picks one of its events with each copy's own roll. In the demo that is the colour of every
    // placed Lilshroom and LilBomber (Animator.runtimeAnimatorController), the spider eggs' looks and the Kymia
    // trail's variants: different on every screen. A placed object picks by its scene path instead, so every copy
    // shows the same one (also when single player: a placed mushroom keeps its colour). Objects spawned at runtime
    // (pooled effects, wave spawns) keep the game's roll.
    // Not covered: RandomAsset (only the procedural dungeons roll it, not in the demo: see ROADMAP D6) and
    // RandomAnimations (idle variations of the chickens, a moment long).
    [HarmonyPatch(typeof(RandomEventSelector), nameof(RandomEventSelector.Trigger))]
    internal static class RandomEventPickPatch
    {
        public static int Picked;

        private static bool Prefix(RandomEventSelector __instance)
        {
            var events = __instance.events;
            if (events == null || events.Length < 2 || NetIds.IsRuntimeSpawned(__instance.transform)) return true;
            events[Index(__instance)]?.Invoke();
            Picked++;
            return false;
        }

        public static int Index(RandomEventSelector s)
        {
            uint h = (uint)NetIds.Hash(NetIds.ScenePath(s.transform));
            h ^= h >> 15; h *= 0x2C1B3C6D; h ^= h >> 12;   // FNV's low bits alone follow the last characters
            return (int)(h % (uint)s.events.Length);
        }
    }
}
