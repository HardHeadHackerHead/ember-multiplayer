using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleMP.Patches
{
    // Shorter startup: skip the logo screens and go straight to the next scene.
    [HarmonyPatch(typeof(SplashManager), nameof(SplashManager.RunSplashes))]
    internal static class SkipSplashPatch
    {
        private static bool Prefix(SplashManager __instance, ref IEnumerator __result)
        {
            if (!Plugin.Settings.SkipSplash.Value) return true;
            __result = Skip(__instance);
            return false;
        }

        private static IEnumerator Skip(SplashManager splash)
        {
            yield return null;
            Plugin.Log.LogInfo($"[speed] Skipping splash screens at {Time.realtimeSinceStartup:F1}s");
            splash.onFinishSplashesEvent?.Invoke();
            Singleton<SceneLoader>.main.CallSceneNoProgress(splash.nextSceneName, null).Forget();
        }
    }

    internal static class Speed
    {
        public static void Apply()
        {
            // Unity's default (BelowNormal) gives async loads only a few ms per frame.
            if (Plugin.Settings.FastLoading.Value)
                Application.backgroundLoadingPriority = ThreadPriority.High;
        }
    }
}
