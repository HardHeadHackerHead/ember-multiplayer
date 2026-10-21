using EmbervilleMP.Sync;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleMP.Patches
{
    // What killed us (Sync/Deaths.cs): the damage our character takes, what dealt it, and the death itself.
    // Every patch here only records; none changes what the game does.

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.ApplyDamage))]
    internal static class PlayerDamageCausePatch
    {
        private static void Prefix(PlayerMaster __instance, DamageData damageData)
        {
            if (__instance == Game.LocalPlayer && !__instance.IsDead) Deaths.OnLocalDamage(damageData);
        }
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.Die))]
    internal static class PlayerDieAnnouncePatch
    {
        private static void Prefix(PlayerMaster __instance)
        {
            if (__instance == Game.LocalPlayer) Deaths.OnLocalDie();
        }
    }

    // The attack asset doesn't survive into the damage the player receives; note it on the way in.
    [HarmonyPatch(typeof(CombatSystem), nameof(CombatSystem.Resolve), new[] { typeof(DamageData), typeof(DamageData) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
    internal static class ResolveCausePatch
    {
        private static void Prefix(DamageData damageData) => Deaths.OnResolve(damageData);
    }

    // Damage over time (poison, burn, bleed...) arrives without an attacker: note which effect is ticking.
    [HarmonyPatch(typeof(DamageOverTimeEffect), nameof(DamageOverTimeEffect.InternalUpdate))]
    internal static class DotTickCausePatch
    {
        private static void Prefix(DamageOverTimeEffect __instance, out Effect __state)
        {
            __state = Deaths.DotTick;
            Deaths.DotTick = __instance;
        }

        private static void Finalizer(Effect __state) => Deaths.DotTick = __state;
    }

    // The objects that deal damage (traps' damage areas, projectiles, explosions): note which one is hitting.
    [HarmonyPatch(typeof(DamageOverTime), nameof(DamageOverTime.HandleTarget))]
    internal static class DamageAreaSourcePatch
    {
        private static void Prefix(DamageOverTime __instance, out Component __state) { __state = Deaths.Source; Deaths.Source = __instance; }
        private static void Finalizer(Component __state) => Deaths.Source = __state;
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.OnTriggerEnter2D))]
    internal static class ProjectileSourcePatch
    {
        private static void Prefix(Projectile __instance, out Component __state) { __state = Deaths.Source; Deaths.Source = __instance; }
        private static void Finalizer(Component __state) => Deaths.Source = __state;
    }

    [HarmonyPatch(typeof(Explosion), nameof(Explosion.HandleCollision))]
    internal static class ExplosionSourcePatch
    {
        private static void Prefix(Explosion __instance, out Component __state) { __state = Deaths.Source; Deaths.Source = __instance; }
        private static void Finalizer(Component __state) => Deaths.Source = __state;
    }

    [HarmonyPatch(typeof(ExplosionOverTime), nameof(ExplosionOverTime.HandleCollision))]
    internal static class ExplosionOverTimeSourcePatch
    {
        private static void Prefix(ExplosionOverTime __instance, out Component __state) { __state = Deaths.Source; Deaths.Source = __instance; }
        private static void Finalizer(Component __state) => Deaths.Source = __state;
    }

    // A boss fell: the scene's runner announces it.
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.SetKilled))]
    internal static class BossDefeatAnnouncePatch
    {
        private static void Postfix(Enemy __instance) => Deaths.OnEnemyKilled(__instance);
    }
}
