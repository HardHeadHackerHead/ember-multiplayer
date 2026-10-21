using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleMP
{
    [BepInPlugin(Guid, "Ember Multiplayer", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "dev.emberville.mp";
        public const string Version = "0.2.0";

        internal static ManualLogSource Log;
        internal static Settings Settings;
        internal static GameObject Root;

        private void Awake()
        {
            Log = Logger;
            Settings = new Settings(Config);
            Args.Parse(Environment.GetCommandLineArgs());
            FileLog.Start();
            ApplyPatches();

            Root = new GameObject("EmbervilleMP");
            DontDestroyOnLoad(Root);
            Root.hideFlags = HideFlags.HideAndDontSave;
            Root.AddComponent<MPRunner>();
            Log.LogInfo($"Ember Multiplayer {Version} loaded. {Args.Describe()}");
        }

        // Hooks the game failed to take (a game update renamed or changed them): each patch class on its own, so one
        // broken hook costs that feature, not the whole mod.
        internal static readonly System.Collections.Generic.List<string> FailedPatches = new System.Collections.Generic.List<string>();
        internal static int PatchClasses;

        private static void ApplyPatches()
        {
            var harmony = new Harmony(Guid);
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                // the same per-type processing as PatchAll (it skips types without patches)
                try { if (harmony.CreateClassProcessor(type).Patch()?.Count > 0) PatchClasses++; }
                catch (Exception e)
                {
                    FailedPatches.Add(type.FullName);
                    Log.LogError($"[patch] {type.FullName} couldn't hook the game (changed by an update?): {(e.InnerException ?? e).Message}");
                }
            }
            if (FailedPatches.Count > 0) Log.LogWarning($"[patch] {FailedPatches.Count} of {PatchClasses} patch classes failed: {string.Join(", ", FailedPatches)}");
        }
    }
}
