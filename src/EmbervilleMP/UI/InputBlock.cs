using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace EmbervilleMP
{
    // Keeps the game from reacting to keys while one of our text fields (chat, the F8 window) has the keyboard: every
    // action map of the game's input is switched off (Player: moving, attacking, Esc; Menus: I, J, C, P, K, M, Tab...;
    // UI, Placement, the world map), and the UI's keyboard navigation with them.
    // Letting go gives each action the state the GAME wants it in now, not "on": the game switches maps on and off by
    // itself all the time (dialogue, cutscenes, shops, panels, the death fade), so while we block, its Enable calls on
    // its own actions are recorded instead of applied (its Disable calls are recorded and applied: they're off already)
    // and replayed when the field closes. Closing the chat mid-dialogue leaves movement off, as the dialogue wants.
    internal static class InputBlock
    {
        private static readonly HashSet<string> owners = new HashSet<string>();
        // Each of the game's actions: what it should be when we let go (null: not blocking).
        private static Dictionary<InputAction, bool> wanted;
        private static InputActionAsset blocked;
        private static EventSystem navOf;
        private static bool navWas;

        public static bool Blocking => wanted != null;
        public static int Recorded;          // the game's switches recorded while blocking (tests)

        public static void Set(string owner, bool block)
        {
            if (block) owners.Add(owner); else owners.Remove(owner);
            bool want = owners.Count > 0 && Game.InGame;
            if (want == Blocking) return;
            try
            {
                if (want) Block();
                else Release();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[input] " + (want ? "Blocking" : "Releasing") + " the game's input failed: " + e.Message);
                wanted = null;
                blocked = null;
            }
        }

        private static void Block()
        {
            var asset = MainInput.Actions.asset;
            var w = new Dictionary<InputAction, bool>();
            foreach (var map in asset.actionMaps)
                foreach (var a in map.actions) w[a] = a.enabled;
            // (switched off before recording starts: these are ours, not the game's wish)
            foreach (var map in asset.actionMaps) map.Disable();
            wanted = w;
            blocked = asset;
            navOf = EventSystem.current;
            if (navOf != null)
            {
                navWas = navOf.sendNavigationEvents;
                navOf.sendNavigationEvents = false;
            }
        }

        private static void Release()
        {
            var w = wanted;
            wanted = null;   // first: the switches below are ours and go through
            blocked = null;
            foreach (var kv in w)
            {
                if (kv.Value && !kv.Key.enabled) kv.Key.Enable();
                else if (!kv.Value && kv.Key.enabled) kv.Key.Disable();
            }
            if (navOf != null) navOf.sendNavigationEvents = navWas;
            navOf = null;
        }

        // The game switches one of its maps while we block: true = let the call run.
        public static bool GameSwitch(InputActionMap map, bool on)
        {
            if (wanted == null || map == null || map.asset != blocked) return true;
            foreach (var a in map.actions) wanted[a] = on;
            Recorded++;
            return !on;
        }

        // ... or a single action (placement mode turns Pause off and on, for one).
        public static bool GameSwitch(InputAction action, bool on)
        {
            if (wanted == null || action == null || action.actionMap == null || action.actionMap.asset != blocked) return true;
            wanted[action] = on;
            Recorded++;
            return !on;
        }
    }

    // (MainInput.Actions.Enable()/Disable() and the game's Player/Menus/UI...Enable() all end up in these.)
    [HarmonyPatch(typeof(InputActionMap), nameof(InputActionMap.Enable))]
    internal static class InputBlockMapEnablePatch
    {
        private static bool Prefix(InputActionMap __instance) => InputBlock.GameSwitch(__instance, true);
    }

    [HarmonyPatch(typeof(InputActionMap), nameof(InputActionMap.Disable))]
    internal static class InputBlockMapDisablePatch
    {
        private static bool Prefix(InputActionMap __instance) => InputBlock.GameSwitch(__instance, false);
    }

    [HarmonyPatch(typeof(InputAction), nameof(InputAction.Enable))]
    internal static class InputBlockActionEnablePatch
    {
        private static bool Prefix(InputAction __instance) => InputBlock.GameSwitch(__instance, true);
    }

    [HarmonyPatch(typeof(InputAction), nameof(InputAction.Disable))]
    internal static class InputBlockActionDisablePatch
    {
        private static bool Prefix(InputAction __instance) => InputBlock.GameSwitch(__instance, false);
    }
}
