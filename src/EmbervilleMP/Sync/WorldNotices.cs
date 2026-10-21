using System;
using System.Linq;
using EmbervilleMP.Net;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.SmartFormat.PersistentVariables;

namespace EmbervilleMP.Sync
{
    // The game's own notification for world progress another player made: "Learned <recipe>" when a recipe they
    // learned reaches us (RecipeUnlocker and the "Unlock Recipes" event only notify the copy that ran them). Lore pages
    // and Tinetia's map unlocks have no notification in the game: nothing to show for those. Nothing while a world is
    // being sent or loaded (a join, a reconnect: what comes then isn't news), and a burst is cut short.
    internal static class WorldNotices
    {
        public static int Shown, Quiet;
        private static float quietUntil, windowStart;
        private static int inWindow;

        public static void Hook(Session s) => s.Welcomed += () => QuietFor(20f);

        public static void QuietFor(float seconds) => quietUntil = Mathf.Max(quietUntil, Time.realtimeSinceStartup + seconds);
        public static bool Quieted => Time.realtimeSinceStartup < quietUntil;

        public static void RecipeLearned(string id)
        {
            float now = Time.realtimeSinceStartup;
            if (GuestWorld.Switching) QuietFor(10f);
            if (!Game.InGame || now < quietUntil) { Quiet++; return; }
            if (now - windowStart > 3f) { windowStart = now; inWindow = 0; }
            if (++inWindow > 3) { Quiet++; return; }
            try
            {
                var recipe = Resources.FindObjectsOfTypeAll<CraftingRecipe>().FirstOrDefault(r => r != null && r.RecipeID == id);
                if (recipe?.Result?.itemData == null) return;
                // (RecipeUnlocker.Notify)
                object name = new LocalizedString("Notifications", "nt_learned");
                // (by reflection: LocalizedString's dictionary side drags in UI Toolkit types we don't reference)
                typeof(System.Collections.Generic.IDictionary<string, IVariable>).GetMethod("Add", new[] { typeof(string), typeof(IVariable) })
                    .Invoke(name, new object[] { "item", HarmonyLib.AccessTools.Property(typeof(CraftingRecipe), nameof(CraftingRecipe.RecipeName)).GetValue(recipe) });
                Bus<UINotificationEvent>.Invoke(new UINotificationEvent
                {
                    Owner = recipe,
                    EventName = (LocalizedString)name,
                    Icon = recipe.Result.itemData.itemIconUI,
                    EventType = UINotificationEvent.Event_Type.Instant,
                });
                Shown++;
                Plugin.Log.LogInfo($"[world] Learned {id} (another player's): notified");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[world] Recipe notification {id}: {e.Message}"); }
        }
    }
}
