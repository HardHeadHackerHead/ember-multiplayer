using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbervilleMP.Sync;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleMP.Patches
{
    // ---- the caster's side: what our character's attacks and skills spawn (see Sync/SkillFx.cs) ----

    // Weapon attacks and attack skills with an attack object (axe shockwave, magic beam, slams, ground areas).
    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.PerformAttackHit))]
    internal static class SkillFxAttackHitPatch
    {
        internal struct State
        {
            public Attack Attack;
            public Skill Skill;
            public HashSet<GameObject> Before;
        }

        private static void Prefix(PlayerMaster __instance, out State __state)
        {
            __state = default;
            var a = __instance.currentAttack;
            if (SkillFx.Instance == null || __instance != Game.LocalPlayer || a == null || a.attackSpawner == null || (a.attackHit & AttackHit.AttackObject) == 0) return;
            // The hit's callbacks can end the attack (and clear currentAttack/currentSkill) before the postfix.
            __state.Attack = a;
            __state.Skill = __instance.currentSkill;
            __state.Before = SkillFx.ActiveInstances(a.attackSpawner);
        }

        private static void Postfix(PlayerMaster __instance, State __state)
        {
            var a = __state.Attack;
            if (a == null || SkillFx.Instance == null) return;
            try
            {
                var inst = SkillFx.NewInstance(a.attackSpawner, __state.Before);
                Vector3 pos = inst != null ? inst.transform.position
                    : string.IsNullOrEmpty(a.attackObjectSpawnPointName) ? __instance.transform.position
                    : __instance.spawnPointHolder.GetPoint(a.attackObjectSpawnPointName, __instance.direction).position;
                int[] level = __state.Skill != null ? __instance.SkillHandler.GetSkillLevels(__state.Skill) : null;
                bool released = inst != null && inst.GetComponent<IHoldAttack>() != null && __instance.releasedAttack;
                SkillFx.Instance.OnLocalSpawn(SkillFx.Kind.AttackObject, a.name, inst, pos, __instance.lookDir, 0f, level, released, false);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[skillfx] attack hit: " + e.Message); }
        }
    }

    // Skill graphs: "Instantiate IAttackObject" by our character.
    [HarmonyPatch(typeof(InstantiateIAttackObjectAction), "OnStart")]
    internal static class SkillFxGraphAttackObjectPatch
    {
        private static void Prefix(InstantiateIAttackObjectAction __instance, out HashSet<GameObject> __state)
        {
            __state = null;
            var me = Game.LocalPlayer;
            if (SkillFx.Instance == null || me == null) return;
            var prefab = __instance.AttackObject?.Value;
            if (prefab == null || __instance.Attacker?.Value != me.gameObject) return;
            __state = SkillFx.ActiveInstances(prefab);
        }

        private static void Postfix(InstantiateIAttackObjectAction __instance, HashSet<GameObject> __state)
        {
            if (__state == null || SkillFx.Instance == null) return;
            try
            {
                var me = Game.LocalPlayer;
                var prefab = __instance.AttackObject.Value;
                var inst = SkillFx.NewInstance(prefab, __state);
                var target = __instance.Target?.Value;
                Vector3 pos = inst != null ? inst.transform.position : target != null ? target.position : me.transform.position;
                float rot = inst != null ? inst.transform.eulerAngles.z : 0f;
                bool rotate = __instance.TargetUsage != null && __instance.TargetUsage.Value == InstantiateIAttackObjectAction.TargetMode.PositionAndRotation;
                var skill = __instance.Skill?.Value;
                int[] level = skill != null ? me.SkillHandler.GetSkillLevels(skill) : null;
                SkillFx.Instance.OnLocalSpawn(SkillFx.Kind.GraphAttackObject, prefab.name, inst, pos, Vector2.zero, rot, level, false, rotate);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[skillfx] graph attack object: " + e.Message); }
        }
    }

    // Skill graphs: "Pool Object" (visual objects a skill places), when the graph is one of our skills.
    [HarmonyPatch(typeof(PoolObjectAction), "OnStart")]
    internal static class SkillFxGraphPooledPatch
    {
        private static void Prefix(PoolObjectAction __instance, out HashSet<GameObject> __state)
        {
            __state = null;
            var me = Game.LocalPlayer;
            if (SkillFx.Instance == null || me == null) return;
            var prefab = __instance.Object?.Value;
            if (prefab == null || !IsOurSkillGraph(__instance, me)) return;
            __state = SkillFx.ActiveInstances(prefab);
        }

        private static void Postfix(PoolObjectAction __instance, HashSet<GameObject> __state)
        {
            if (__state == null || SkillFx.Instance == null) return;
            try
            {
                var me = Game.LocalPlayer;
                var prefab = __instance.Object.Value;
                var inst = SkillFx.NewInstance(prefab, __state);
                var target = __instance.Target?.Value;
                Vector3 pos = inst != null ? inst.transform.position : target != null ? target.position : me.transform.position;
                float rot = inst != null ? inst.transform.eulerAngles.z : 0f;
                bool rotate = __instance.TargetUsage != null && __instance.TargetUsage.Value == PoolObjectAction.TargetMode.PositionAndRotation;
                SkillFx.Instance.OnLocalSpawn(SkillFx.Kind.GraphPooled, prefab.name, inst, pos, Vector2.zero, rot, null, false, rotate);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[skillfx] graph pooled object: " + e.Message); }
        }

        // Skill graphs run on SkillAgents, and only for our own character (others' skills don't run here). Effect
        // graphs (statuses) and enemy graphs aiming at us are not our skills.
        private static bool IsOurSkillGraph(PoolObjectAction action, PlayerMaster me)
        {
            var agent = action.GameObject;
            return agent != null && agent.GetComponent<SkillAgent>() != null;
        }
    }

    // A held beam was let go (our own; copies aren't tracked, so they don't echo).
    [HarmonyPatch(typeof(BeamAttack), nameof(BeamAttack.Release))]
    internal static class SkillFxReleasePatch
    {
        private static void Postfix(BeamAttack __instance) => SkillFx.Instance?.OnLocalRelease(__instance.gameObject);
    }

    // ---- copies are harmless ----

    // Attack objects that hurt, heal or apply effects without going through CombatSystem.Resolve (or around it):
    // a copy (its owner is a stand-in for another player) skips that part and keeps its visuals; heal and buff areas
    // still reach our own character.
    [HarmonyPatch]
    internal static class SkillFxCopyHitsPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(EffectArea), "OnTriggerEnter2D"),
            AccessTools.Method(typeof(EffectArea), "OnTriggerExit2D"),
            AccessTools.Method(typeof(HealOverTime), "HandleCollision"),
            AccessTools.Method(typeof(Explosion), "HandleCollision"),
            AccessTools.Method(typeof(ExplosionOverTime), "HandleCollision"),
            AccessTools.Method(typeof(HeartwoodSpike), "HandleCollision"),
            AccessTools.Method(typeof(DamageOverTime), "HandleTarget"),
        }.Where(m => m != null);

        // A copy's heal or buff area that reached our own character.
        internal static int LocalAreaHits;

        private static bool Prefix(object __instance, object[] __args)
        {
            if (!((__instance as IAttackObject)?.Owner is IVisualOnly)) return true;
            // A teammate's heal or buff area works on our own character here (the caster's machine can't: our puppet
            // there has no health or effects), through the area's own tag filter. Enemies stay the caster's to hit.
            if ((__instance is HealOverTime || __instance is EffectArea) && __args.Length > 0 && OnLocalPlayer(__args[0] as Collider2D))
            {
                LocalAreaHits++;
                return true;
            }
            SkillFx.Blocked++;
            return false;
        }

        private static bool OnLocalPlayer(Collider2D c)
        {
            var me = Game.LocalPlayer;
            return c != null && me != null && (c.attachedRigidbody != null ? c.attachedRigidbody.gameObject == me.gameObject : c.transform.IsChildOf(me.transform));
        }
    }

    // Last line: damage from a copy that still reached a character is dropped and counted (the self-test wants 0).
    [HarmonyPatch]
    internal static class SkillFxLeakGuardPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(Enemy), nameof(Enemy.ApplyDamage)),
            AccessTools.Method(typeof(PlayerMaster), nameof(PlayerMaster.ApplyDamage)),
        }.Where(m => m != null);

        private static bool Prefix(DamageData damageData)
        {
            if (!(damageData.Aggressor is IVisualOnly)) return true;
            SkillFx.Leaks++;
            Plugin.Log.LogWarning($"[skillfx] a copy's damage reached {damageData.Target?.GetTransform()?.name} (dropped)");
            return false;
        }
    }

    // Scene runner, for the self-test: enemy damage that came neither from another player's real hit nor from an
    // effect tick (with only a guest attacking, any of it would be a copy hitting for real).
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage))]
    internal static class SkillFxEnemyDamageAuditPatch
    {
        private static void Prefix(Enemy __instance, out float __state) => __state = __instance.currentHealth;

        private static void Postfix(Enemy __instance, DamageData damageData, float __state)
        {
            if (!EnemySync.IsAuthority || SkillFx.InGuestHit) return;
            float dealt = __state - __instance.currentHealth;
            if (dealt <= 0f || damageData.Result == DamageResult.Effect || damageData.Aggressor is Enemy) return;
            SkillFx.EnemyDamageNotFromGuests += dealt;
            if (Args.Test != null) Plugin.Log.LogInfo($"[skillfx] {__instance.name} lost {dealt} not from a guest's hit (aggressor {damageData.Aggressor?.GetType().Name ?? "none"}, attack {damageData.Attack?.name ?? "-"})");
        }
    }

    [HarmonyPatch(typeof(EnemySync), "OnHit")]
    internal static class SkillFxGuestHitFlagPatch
    {
        private static void Prefix() => SkillFx.InGuestHit = true;

        private static Exception Finalizer(Exception __exception)
        {
            SkillFx.InGuestHit = false;
            return __exception;
        }
    }

    // A puppet plays the other player's attack states, and this state behaviour reads the player's CharacterStats
    // (a puppet has none): it threw on every bow shot and combo of another player. The attack rate itself comes
    // with the synced animator floats.
    [HarmonyPatch(typeof(AttackRateChange), nameof(AttackRateChange.OnStateEnter))]
    internal static class PuppetAttackRatePatch
    {
        private static bool Prefix(Animator animator) => animator != null && animator.GetComponent<CharacterStats>() != null;
    }
}
