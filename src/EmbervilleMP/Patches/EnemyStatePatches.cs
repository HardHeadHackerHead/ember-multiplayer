using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using EmbervilleMP.Sync;
using HarmonyLib;

namespace EmbervilleMP.Patches
{
    // Hooks for the fight's state beyond one runner: deaths for late arrivals (Sync/EnemySync.Dead.cs), the Acolyte
    // Protector's aura and march (Sync/EnemyFx.Protector.cs), who's in combat (Sync/EnemyCombat.cs).

    // Every death on this copy since the scene loaded (also before a session: what a runner tells arrivals).
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.SetKilled))]
    internal static class EnemyDeathNotePatch
    {
        private static void Postfix(Enemy __instance) => EnemySync.NoteKilled(__instance);
    }

    // ---- the Acolyte Protector ----

    [HarmonyPatch(typeof(AcolyteProtector), nameof(AcolyteProtector.StartAura))]
    internal static class ProtectorStartAuraPatch
    {
        private static void Prefix(AcolyteProtector __instance) => EnemyFx.BeforeStart(__instance, true);
        private static void Postfix(AcolyteProtector __instance, bool __result) { if (__result) EnemyFx.OnAura(__instance, true); }
    }

    [HarmonyPatch(typeof(AcolyteProtector), nameof(AcolyteProtector.EndAura))]
    internal static class ProtectorEndAuraPatch
    {
        private static void Postfix(AcolyteProtector __instance) => EnemyFx.OnAura(__instance, false);
    }

    [HarmonyPatch(typeof(AcolyteProtector), nameof(AcolyteProtector.StartMarch))]
    internal static class ProtectorStartMarchPatch
    {
        private static void Prefix(AcolyteProtector __instance) => EnemyFx.BeforeStart(__instance, false);
        private static void Postfix(AcolyteProtector __instance) => EnemyFx.OnMarch(__instance, true);
    }

    [HarmonyPatch(typeof(AcolyteProtector), nameof(AcolyteProtector.EndMarch))]
    internal static class ProtectorEndMarchPatch
    {
        private static void Postfix(AcolyteProtector __instance) => EnemyFx.OnMarch(__instance, false);
    }

    // Its modifiers go when it's switched off (OnDisable), without EndAura / EndMarch.
    [HarmonyPatch(typeof(AcolyteProtector), "OnDisable")]
    internal static class ProtectorDisablePatch
    {
        private static void Postfix(AcolyteProtector __instance)
        {
            EnemyFx.OnAura(__instance, false);
            EnemyFx.OnMarch(__instance, false);
        }
    }

    // Runner: the absorb's VFX for the others.
    [HarmonyPatch(typeof(AcolyteProtector.DamageAbsorbModifier), nameof(AcolyteProtector.DamageAbsorbModifier.ModifyValue))]
    internal static class ProtectorAbsorbRelayPatch
    {
        private static void Postfix(AcolyteProtector.DamageAbsorbModifier __instance, DamageData value)
        {
            if (!EnemySync.IsAuthority || EnemyFx.Instance == null || value.Target == null) return;
            var from = EnemyFx.HitSource ?? value.Aggressor;
            if (from == null) return;
            try { EnemyFx.Instance.OnRunnerAbsorb(__instance.toAbsorb, value.Target, from.GetTransform().position); }
            catch (Exception e) { Plugin.Log.LogWarning("[enemyfx] Absorb relay: " + e.Message); }
        }
    }

    // ---- who's in combat ----

    // The quick portal's "any group engaged with the player": with others in the scene, whether our character is in a
    // fight (EnemyCombat.AnyEngaged). The rest of Execute is the game's.
    [HarmonyPatch(typeof(CollectablePortal), nameof(CollectablePortal.Execute))]
    internal static class QuickPortalCombatPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var any = typeof(Enumerable).GetMethods().First(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
                .MakeGenericMethod(typeof(EnemyGroup));
            var ours = AccessTools.Method(typeof(EnemyCombat), nameof(EnemyCombat.AnyEngaged));
            var code = instructions.ToList();
            int replaced = 0;
            foreach (var c in code)
                if (c.Calls(any)) { c.opcode = OpCodes.Call; c.operand = ours; replaced++; }
            // (a game update that moved the check: the patch fails and the self-tests say so)
            if (replaced != 1) throw new InvalidOperationException($"CollectablePortal.Execute: {replaced} engagement checks found, expected 1");
            return code;
        }
    }

    // A merge (MergeAgentGroupAction) remembers the tagged Player, our character, as where the player was last seen;
    // for an enemy after another player's puppet it's the puppet's spot (as EnemyFxLastSeenPatch for SetInSight).
    [HarmonyPatch(typeof(MergeAgentGroupAction), "OnStart")]
    internal static class MergeLastSeenPatch
    {
        private static void Postfix(MergeAgentGroupAction __instance)
        {
            if (!EnemySync.IsAuthority || EnemyFx.Instance == null) return;
            var e = __instance.Agent?.Value;
            if (e != null && e.Group != null) EnemyFx.Instance.FixLastSeen(e.Group, e);
        }
    }
}
