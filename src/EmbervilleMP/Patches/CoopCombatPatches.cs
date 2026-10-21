using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbervilleMP.Sync;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleMP.Patches
{
    // ---- no friendly fire (Sync/FriendlyFire.cs) ----

    // A player's projectile flies on through a teammate's puppet, as through its own player (no hit, not stopped).
    [HarmonyPatch(typeof(Projectile), "OnTriggerEnter2D")]
    internal static class ProjectileThroughTeammatesPatch
    {
        private static bool Prefix(Projectile __instance, Collider2D other)
        {
            if (!FriendlyFire.IsPuppet(other)) return true;
            if (!FriendlyFire.IsPlayer(__instance.damageData.Aggressor) && !FriendlyFire.IgnoredLikePlayer(other, __instance.ignoreTags)) return true;
            FriendlyFire.ProjectilesPassed++;
            return false;
        }
    }

    // Damage that ignores the "Player" tag ignores the puppets too: the Matriarch's body only hurts on contact while
    // she dashes (SpiderMatriarch.HandleDashAwait takes her ignored tags off for the dash), a player's bomb.
    [HarmonyPatch]
    internal static class PuppetTagsPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(DamageOverTime), "OnTriggerEnter2D"),
            AccessTools.Method(typeof(Explosion), "HandleCollision"),
            AccessTools.Method(typeof(ExplosionOverTime), "HandleCollision"),
            AccessTools.Method(typeof(HeartwoodSpike), "HandleCollision"),
        }.Where(m => m != null);

        private static bool Prefix(object __instance, Collider2D collision)
        {
            var tags = __instance is DamageOverTime d ? d.ignoreTags
                : __instance is Explosion x ? x.ignoreTags
                : __instance is ExplosionOverTime o ? o.ignoreTags
                : __instance is HeartwoodSpike h ? h.ignoreTags : null;
            if (!FriendlyFire.IgnoredLikePlayer(collision, tags)) return true;
            FriendlyFire.TagSkips++;
            return false;
        }
    }

    // ---- replayed deaths and attacks (Sync/EnemySync.cs) ----

    // A non-runner replays a death as one last hit of the enemy's health: its number would pop after the killing
    // blow's (EnemyFx shows that one).
    [HarmonyPatch(typeof(UIManager), "OnDamageEvent")]
    internal static class MuteReplayedDeathNumberPatch
    {
        private static bool Prefix() => !EnemySync.MutingNumbers;
    }

    // Runner: a stagger, stun, hit-freeze or death interrupts an attack; the puppets drop theirs too (build-up VFX,
    // the projectile they would still fire).
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.CancelAttack))]
    internal static class EnemyCancelAttackPatch
    {
        private static void Prefix(Enemy __instance, out bool __state) => __state = __instance.isAttacking;

        private static void Postfix(Enemy __instance, bool __state)
        {
            if (__state && !__instance.isAttacking && EnemySync.IsAuthority && !EnemySync.Passthrough)
                EnemySync.Instance?.OnAttackCancelled(__instance);
        }
    }
}
