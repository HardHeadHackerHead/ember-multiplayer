using HarmonyLib;

namespace EmbervilleMP.Patches
{
    // Two of the game's components reach for a singleton in OnDisable without checking it's still there. While a
    // scene unloads, the singleton may already be gone, and Singleton<T>.main then makes a bare new one (no
    // serialized fields) that survives into the next scene: the next scene's real GameManager / ItemDropHandler
    // destroy themselves as duplicates and the scene never starts (GameManager.SetupScene throws on its null
    // settings). Single player never unloads a scene with these rooms active; co-op does (a room kept running for
    // a guest, a guest's world swap or trip home). With nothing to unsubscribe from, there's nothing to do.
    [HarmonyPatch(typeof(DropItemListener), nameof(DropItemListener.OnDisable))]
    internal static class DropItemListenerUnloadPatch
    {
        private static bool Prefix() => Singleton<ItemDropHandler>.HasMain;
    }

    [HarmonyPatch(typeof(HummingSphereCutscene), nameof(HummingSphereCutscene.OnDisable))]
    internal static class HummingSphereUnloadPatch
    {
        private static bool Prefix() => Singleton<GameManager>.HasMain;
    }
}
