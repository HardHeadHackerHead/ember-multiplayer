using System;
using System.Collections.Generic;
using System.Reflection;
using EmbervilleMP.Sync;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleMP.Patches
{
    // Hooks for Sync/EnemyFx.cs: effects, hits and visuals of enemies between the scene's runner and the puppets.

    // ---- a puppet's copy: our hits leave our side as they are; the runner does the target's side ----

    [HarmonyPatch(typeof(CombatSystem), nameof(CombatSystem.GetDamageData))]
    internal static class EnemyFxRawHitPatch
    {
        private static void Prefix(ref DamageData damageData, bool checkExpectedDamage)
        {
            if (checkExpectedDamage || EnemyFx.Instance == null || !EnemySync.GuestPuppets || EnemySync.Passthrough) return;
            var me = Game.LocalPlayer;
            if (me == null || !ReferenceEquals(damageData.Aggressor, me)) return;
            var target = damageData.Target is DamageableCollider dc ? dc.Damageable : damageData.Target;
            if (!(target is Enemy e) || e.IsDead || !e.CanTakeDamage || EnemySync.RunsHere(e)) return;
            EnemyFx.CaptureHit(e, ref damageData);
        }
    }

    // ---- where an effect on a puppet comes from: our character, or something else ----

    // Our hits (and what they set off: a passive like Bleeding Edge listens to our OnAppliedDamage).
    [HarmonyPatch(typeof(CombatSystem), nameof(CombatSystem.Resolve), new[] { typeof(DamageData), typeof(DamageData) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
    internal static class EnemyFxResolveSourcePatch
    {
        private static void Prefix(DamageData damageData, out bool __state)
        {
            __state = EnemyFx.LocalSource;
            var me = Game.LocalPlayer;
            if (me != null && ReferenceEquals(damageData.Aggressor, me)) EnemyFx.LocalSource = true;
        }

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            EnemyFx.LocalSource = __state;
            return __exception;
        }
    }

    // Our skills' effect areas (other players' copies of theirs never apply anything: SkillFxCopyHitsPatch).
    [HarmonyPatch]
    internal static class EnemyFxAreaSourcePatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(EffectArea), "OnTriggerEnter2D"),
            AccessTools.Method(typeof(EffectArea), "OnTriggerExit2D"),
        };

        private static void Prefix(EffectArea __instance, out (bool, int[]) __state)
        {
            __state = (EnemyFx.LocalSource, EnemyFx.LocalLevel);
            var me = Game.LocalPlayer;
            if (me == null || !ReferenceEquals(__instance.Owner, me)) return;
            EnemyFx.LocalSource = true;
            EnemyFx.LocalLevel = __instance.Level;
        }

        private static Exception Finalizer(Exception __exception, (bool, int[]) __state)
        {
            (EnemyFx.LocalSource, EnemyFx.LocalLevel) = __state;
            return __exception;
        }
    }

    // Our skill graphs putting a skill's effect on a target.
    [HarmonyPatch(typeof(ApplyEffectFromSkillOnPlayerAction), "OnStart")]
    internal static class EnemyFxSkillSourcePatch
    {
        private static void Prefix(ApplyEffectFromSkillOnPlayerAction __instance, out (bool, int[], Skill) __state)
        {
            __state = (EnemyFx.LocalSource, EnemyFx.LocalLevel, EnemyFx.LocalSkill);
            var me = Game.LocalPlayer;
            if (me == null || __instance.Caster == null || __instance.Caster.Value != me) return;
            EnemyFx.LocalSource = true;
            var skill = __instance.Skill?.Value;
            EnemyFx.LocalSkill = skill;
            try { EnemyFx.LocalLevel = skill != null && me.TryGetComponent<SkillHandler>(out var sh) ? sh.GetSkillLevels(skill) : null; }
            catch (Exception) { EnemyFx.LocalLevel = null; }
        }

        private static Exception Finalizer(Exception __exception, (bool, int[], Skill) __state)
        {
            (EnemyFx.LocalSource, EnemyFx.LocalLevel, EnemyFx.LocalSkill) = __state;
            return __exception;
        }
    }

    // ---- effects landing on a puppet: ours go to the runner; the runner's popups go to everyone ----

    [HarmonyPatch(typeof(EffectsHandler), nameof(EffectsHandler.HandleEffect))]
    internal static class EnemyFxHandleEffectPatch
    {
        private static void Prefix(EffectsHandler __instance, DamageEffect effectCtr, out bool __state)
        {
            __state = EnemyFx.InHandleEffect;
            EnemyFx.InHandleEffect = true;
            if (!__state) EnemyFx.Instance?.OnPuppetHandleEffect(__instance, effectCtr);
        }

        // Runner: the popup it showed over one of ours ("Burn", "Stunned"), for the others.
        private static void Postfix(EffectsHandler __instance, DamageEffect effectCtr, bool __state)
        {
            if (__state || !EnemySync.IsAuthority || EnemyFx.Instance == null || !(__instance.owner is Enemy e) || e.IsDead) return;
            string text;
            switch (effectCtr.Effect)
            {
                case EffectType.Stun: text = "Stunned"; break;
                case EffectType.Bleed: case EffectType.Burn: case EffectType.Freeeze: case EffectType.Kymia: case EffectType.Poison:
                case EffectType.Shock: case EffectType.Trapped: case EffectType.Shield: case EffectType.MoveSpeedBuff:
                    text = effectCtr.HideUI ? null : effectCtr.Effect.ToString();
                    break;
                default: text = null; break;
            }
            if (text != null) EnemyFx.Instance.OnRunnerPopup(e, text);
        }

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            EnemyFx.InHandleEffect = __state;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(EffectsHandler), nameof(EffectsHandler.AddEffect))]
    internal static class EnemyFxAddEffectPatch
    {
        private static void Prefix(EffectsHandler __instance, Effect effect)
        {
            if (!EnemyFx.InHandleEffect) EnemyFx.Instance?.OnPuppetAddEffect(__instance, effect);
        }
    }

    [HarmonyPatch(typeof(EffectsHandler), nameof(EffectsHandler.RemoveEffect), new[] { typeof(string) })]
    internal static class EnemyFxRemoveByNamePatch
    {
        private static void Prefix(EffectsHandler __instance, string name) => EnemyFx.Instance?.OnPuppetRemoveEffect(__instance, name);
    }

    [HarmonyPatch(typeof(EffectsHandler), nameof(EffectsHandler.RemoveEffect), new[] { typeof(EffectType) })]
    internal static class EnemyFxRemoveByTypePatch
    {
        private static void Prefix(EffectsHandler __instance, EffectType effectType) => EnemyFx.Instance?.OnPuppetRemoveEffect(__instance, effectType);
    }

    // A puppet's own effects (shadows) show nothing: what it shows is the runner's list (EnemyFx.ApplyMirrors).
    [HarmonyPatch(typeof(EffectsHandler), nameof(EffectsHandler.CallEvent))]
    internal static class EnemyFxMuteShadowPatch
    {
        private static bool Prefix(EffectsHandler __instance) => !EnemyFx.MuteShadow(__instance);
    }

    // ---- runner: other players' hits point away from them ----

    // Enemy.HandleKnockback pushes away from the attacker, and another player's hit is credited to our character.
    [HarmonyPatch(typeof(Enemy), "HandleKnockback")]
    internal static class EnemyFxKnockbackPatch
    {
        private static void Prefix(ref DamageData damageData)
        {
            if (EnemyFx.HitSource != null) damageData.Aggressor = EnemyFx.HitSource;
        }
    }

    // The Acolyte Protector's absorb shows a hit VFX turned towards the attacker: for another player's hit, them.
    // (The redirected damage keeps our character as its attacker: the base damage and the credit come from it.)
    [HarmonyPatch(typeof(AcolyteProtector.DamageAbsorbModifier), nameof(AcolyteProtector.DamageAbsorbModifier.ModifyValue))]
    internal static class EnemyFxAbsorbDirectionPatch
    {
        private static void Prefix(AcolyteProtector.DamageAbsorbModifier __instance, out HashSet<GameObject> __state) =>
            __state = EnemyFx.HitSource != null ? SkillFx.ActiveInstances(__instance.takeDamageVFX) : null;

        private static void Postfix(AcolyteProtector.DamageAbsorbModifier __instance, DamageData value, HashSet<GameObject> __state)
        {
            var source = EnemyFx.HitSource;
            if (__state == null || source == null || value.Target == null) return;
            try
            {
                var vfx = SkillFx.NewInstance(__instance.takeDamageVFX, __state);
                if (vfx == null) return;
                Vector3 dir = value.Target.GetTransform().position - source.GetTransform().position;
                var d = dir.ToDirection();
                var anim = vfx.GetComponent<Animator>();
                if (anim != null) anim.SetFloat("DirFactor", d == Direction.Up ? 1f : d == Direction.Down ? -1f : 0f);
                vfx.transform.rotation = Quaternion.Euler(0f, dir.x < 0f ? 180f : 0f, 0f);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[enemyfx] Absorb direction: " + e.Message); }
        }
    }

    // ---- runner: what killed an enemy (EnemyDied's cause) ----

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage))]
    internal static class EnemyFxDeathCausePatch
    {
        private static void Prefix(Enemy __instance, DamageData damageData, out sbyte __state)
        {
            __state = -2;
            if (!EnemySync.IsAuthority || EnemyFx.Instance == null) return;
            __state = EnemyFx.Instance.DamageStarted(__instance, damageData.Aggressor is PlayerMaster);
        }

        private static Exception Finalizer(Exception __exception, Enemy __instance, sbyte __state)
        {
            if (__state != -2) EnemyFx.Instance?.DamageEnded(__instance, __state);
            return __exception;
        }
    }

    // ---- runner: what enemy graphs do to how ours look ----

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.SetSpriteColor))]
    internal static class EnemyFxTintPatch
    {
        private static void Postfix(Enemy __instance, Color newColor)
        {
            if (EnemySync.IsAuthority && !EnemyFx.Applying) EnemyFx.Instance?.NoteTint(__instance, newColor);
        }
    }

    [HarmonyPatch(typeof(FadeEnemySpriteAction), "OnStart")]
    internal static class EnemyFxFadePatch
    {
        private static void Prefix(FadeEnemySpriteAction __instance)
        {
            var e = __instance.Enemy?.Value;
            if (e != null && EnemySync.IsAuthority) EnemyFx.Instance?.OnRunnerFade(e, __instance.Duration != null ? __instance.Duration.Value : 0f);
        }
    }

    [HarmonyPatch(typeof(SpawnEnemyGroupAction), "OnStart")]
    internal static class EnemyFxSpawnVfxPatch
    {
        private static void Prefix(SpawnEnemyGroupAction __instance, out HashSet<GameObject> __state)
        {
            var vfx = __instance.SpawnVFX?.Value;
            __state = vfx != null && EnemySync.IsAuthority && EnemyFx.Instance != null ? SkillFx.ActiveInstances(vfx) : null;
        }

        private static void Postfix(SpawnEnemyGroupAction __instance, HashSet<GameObject> __state)
        {
            if (__state == null) return;
            var vfx = __instance.SpawnVFX.Value;
            var spots = new List<Vector3>();
            var list = PoolManager.GetPoolList(vfx);
            if (list != null)
                foreach (var go in list)
                    if (go != null && go.activeSelf && !__state.Contains(go)) spots.Add(go.transform.position);
            EnemyFx.Instance?.OnRunnerSpawnVfx(vfx, spots);
        }
    }

    // ---- runner: losing sight of another player ----

    // EnemyGroup.SetInSight remembers the tagged Player (our character) as where the player was last seen; when the
    // enemy was after another player's puppet, it's the puppet's spot.
    [HarmonyPatch(typeof(EnemyGroup), nameof(EnemyGroup.SetInSight))]
    internal static class EnemyFxLastSeenPatch
    {
        private static void Prefix(EnemyGroup __instance, Enemy enemy, bool isInSight, out bool __state) =>
            __state = !isInSight && EnemySync.IsAuthority && enemy != null && __instance.IsInSightOfPlayer.Contains(enemy) && __instance.IsEngagedWithPlayer;

        private static void Postfix(EnemyGroup __instance, Enemy enemy, bool __state)
        {
            if (__state && __instance.LastPlayerSeenPosition.HasValue) EnemyFx.Instance?.FixLastSeen(__instance, enemy);
        }
    }
}
