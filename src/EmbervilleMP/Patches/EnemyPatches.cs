using System.Reflection;
using EmbervilleMP.Sync;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleMP.Patches
{
    // ---- guest: enemies are puppets of the host's ----

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.RestartGraph))]
    internal static class EnemyRestartGraphPatch
    {
        private static bool Prefix(Enemy __instance) => !EnemySync.GuestPuppets || EnemySync.Passthrough || EnemySync.RunsHere(__instance);
    }

    // Guest: attacks only start when the host says so (EnemyAttackStart); the hit itself then plays
    // locally for the visuals, and its damage is dropped in CombatResolvePatch.
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.PerformAttack))]
    internal static class EnemyPerformAttackPatch
    {
        private static bool Prefix(Enemy __instance) => !EnemySync.GuestPuppets || EnemySync.Passthrough || EnemySync.RunsHere(__instance);

        private static void Postfix(Enemy __instance, Attack attack, GameObject target, Vector3? targetPos)
        {
            if (EnemySync.IsAuthority && __instance.isAttacking && __instance.currentAttack == attack)
                EnemySync.Instance?.OnAttackStarted(__instance, attack, target, targetPos);
        }
    }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.Knockback))]
    internal static class EnemyKnockbackPatch
    {
        private static bool Prefix(Enemy __instance) => !EnemySync.GuestPuppets || EnemySync.Passthrough || EnemySync.RunsHere(__instance);
    }

    // StateMachine<T>.ChangeState is shared with the player: only enemies are filtered, and only the
    // states that would make a puppet act on its own (behaviour, hit-freeze, stun). Idle, die and dead still run.
    [HarmonyPatch]
    internal static class EnemyChangeStatePatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(StateMachine<Enemy>), nameof(StateMachine<Enemy>.ChangeState));

        private static bool Prefix(object __instance, State<Enemy> newState)
        {
            if (!(__instance is Enemy e) || !EnemySync.GuestPuppets || EnemySync.Passthrough || EnemySync.RunsHere(e)) return true;
            return !(newState == e.runBehaviorState || newState == e.hitFreezeState || newState == e.stunState);
        }
    }

    // Guest: our hits on an enemy go to the host instead of changing the puppet's health.
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage))]
    internal static class EnemyApplyDamagePatch
    {
        private static bool Prefix(Enemy __instance, DamageData damageData)
        {
            if (!EnemySync.GuestPuppets || EnemySync.Passthrough) return true;
            // Only our own character's hits count (and go to the host); anything else that would hurt a
            // puppet here (its own self-destruct, effects) happens on the host already.
            if (__instance.IsDead || !__instance.CanTakeDamage || !(damageData.Aggressor is PlayerMaster)) return false;
            EnemySync.Instance.SendHit(__instance, damageData);
            return false;
        }
    }

    // Host: damage numbers for the guests (who hit it is remembered by EnemySync for kill credit).
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ApplyDamage))]
    internal static class EnemyApplyDamageHostPatch
    {
        private static void Prefix(Enemy __instance, out float __state) => __state = __instance.currentHealth;

        private static void Postfix(Enemy __instance, DamageData damageData, float __state)
        {
            if (!EnemySync.IsAuthority || EnemySync.Instance == null) return;
            int dealt = Mathf.RoundToInt(__state - __instance.currentHealth);
            // A parried hit takes nothing but shows "Dodged" (EnemyFx.ShowDamage); not on a dead or invulnerable enemy,
            // where ApplyDamage returned at once.
            bool parried = damageData.Result == DamageResult.Parry && __instance.CanTakeDamage && !__instance.IsDead;
            if (dealt > 0 || parried) EnemySync.Instance.OnEnemyDamaged(__instance, Mathf.Max(0, dealt), damageData.Result, EnemySync.Instance.CurrentHitter(__instance));
        }
    }

    // Spawns: the host announces pool enemies; a guest drops the ones its own copy spawns.
    [HarmonyPatch(typeof(EnemiesManager), nameof(EnemiesManager.SetEnemyAlive))]
    internal static class EnemyAlivePatch
    {
        private static void Postfix(Enemy enemy)
        {
            if (EnemySync.Instance == null || enemy == null) return;
            if (EnemySync.IsAuthority) EnemySync.Instance.OnEnemyAlive(enemy);
            else if (EnemySync.GuestPuppets)
            {
                if (Sync.NetIds.IsRuntimeSpawned(enemy.transform)) EnemySync.Instance.OnLocalEnemyAlive(enemy);
                else EnemySync.Instance.OnLocalPlacedEnemyAlive(enemy);
            }
        }
    }

    // A death normally rewinds every enemy to when the player entered the room. With other players in the
    // scene that would undo their fight, so in co-op only the player who died is put back (unless everyone in
    // the scene went down: Sync/PartyWipe).
    [HarmonyPatch(typeof(EnemiesManager), nameof(EnemiesManager.ResetAll))]
    internal static class EnemiesResetAllPatch
    {
        private static bool Prefix()
        {
            return !CoopResetScenePatch.OthersInScene() || Sync.PartyWipe.Rewinding;
        }
    }

    // Host: tell the guests an enemy died.
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.SetKilled))]
    internal static class EnemySetKilledPatch
    {
        private static void Postfix(Enemy __instance)
        {
            if (EnemySync.IsAuthority) EnemySync.Instance?.OnKilled(__instance);
        }
    }

    // Both sides, at the point where any attack is resolved against a target.
    [HarmonyPatch(typeof(CombatSystem), nameof(CombatSystem.Resolve), new[] { typeof(DamageData), typeof(DamageData) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
    internal static class CombatResolvePatch
    {
        private static bool Prefix(DamageData damageData, ref DamageData attackDamage, ref DamageData __result)
        {
            // Host: an enemy reached a guest's puppet -> that guest resolves it on its own character.
            // Someone else's projectile or spell as seen here: only the caster's machine decides what it hits.
            if (damageData.Aggressor is IVisualOnly)
            {
                attackDamage = damageData;
                __result = new DamageData { Effects = new System.Collections.Generic.List<DamageEffect>() };
                return false;
            }
            var remote = damageData.Target as RemotePlayerTarget ?? (damageData.Target as RemotePlayerHurtbox)?.Owner;
            // A player's sword, arrow or bomb on a teammate's puppet: no friendly fire (Sync/FriendlyFire.cs).
            if (remote != null && FriendlyFire.IsPlayer(damageData.Aggressor))
            {
                FriendlyFire.HitsDropped++;
                attackDamage = damageData;
                __result = new DamageData { Effects = new System.Collections.Generic.List<DamageEffect>() };
                return false;
            }
            // Traps run on every copy: the other player's own copy hurts their character, not ours.
            if (remote != null && Sync.Objects.TrapSync.IsHazard(damageData))
            {
                Sync.Objects.TrapSync.HazardHitsDropped++;
                attackDamage = damageData;
                __result = new DamageData { Effects = new System.Collections.Generic.List<DamageEffect>() };
                return false;
            }
            if (EnemySync.IsAuthority && remote != null)
            {
                if (!remote.IsDead) EnemySync.Instance?.ForwardAttack(remote.PlayerId, damageData.Aggressor, damageData);
                attackDamage = damageData;
                __result = new DamageData { Effects = new System.Collections.Generic.List<DamageEffect>() };
                return false;
            }
            // Guest: enemy attacks only land through EnemyAttack from the host.
            if (EnemySync.GuestPuppets && !EnemySync.Passthrough && damageData.Aggressor is Enemy)
            {
                attackDamage = damageData;
                __result = new DamageData { Effects = new System.Collections.Generic.List<DamageEffect>() };
                return false;
            }
            return true;
        }
    }
}

namespace EmbervilleMP.Patches
{
    // An enemy can briefly run its graph without a group (seen in co-op boss fights); the game's condition
    // then throws every frame. Answer "no" instead and say who it was, once.
    [HarmonyLib.HarmonyPatch(typeof(IsGroupForcedToPositionCondition), nameof(IsGroupForcedToPositionCondition.IsTrue))]
    internal static class GroupConditionGuardPatch
    {
        private static readonly System.Collections.Generic.HashSet<Enemy> reported = new System.Collections.Generic.HashSet<Enemy>();

        private static bool Prefix(IsGroupForcedToPositionCondition __instance, ref bool __result)
        {
            var e = __instance.Enemy?.Value;
            if (e != null && e.Group != null) return true;
            if (e != null && reported.Add(e))
                Plugin.Log.LogWarning($"[enemy] {e.name} runs its graph without a group (active={e.isActiveAndEnabled} dead={e.IsDead} state={e.currentState?.GetType().Name} agent={(e.behaviorGraphAgent != null && e.behaviorGraphAgent.enabled)})");
            __result = false;
            return false;
        }
    }

    // The same for "is the enemy in sight of the player": a group-less enemy (e.g. after the host died with enemies
    // on it) threw here every frame. Without a group it is in no one's sight.
    [HarmonyLib.HarmonyPatch(typeof(IsEnemyInSightOfPlayerCondition), nameof(IsEnemyInSightOfPlayerCondition.IsTrue))]
    internal static class SightConditionGuardPatch
    {
        private static bool Prefix(IsEnemyInSightOfPlayerCondition __instance, ref bool __result)
        {
            var e = __instance.Enemy?.Value;
            if (e != null && e.Group != null && e.Group.IsInSightOfPlayer != null) return true;
            __result = __instance.Bool != null && !__instance.Bool.Value;   // "not in sight" == expected?
            return false;
        }
    }

    // And the action marking an enemy seen by its group: a group-less enemy (a wave's spawn chasing a guest after the
    // host died) threw in it every frame (~50k log lines a test). A live enemy we run gets a group the way its
    // OnEnable gives one; otherwise the action fails, as it does without an agent.
    [HarmonyLib.HarmonyPatch(typeof(SetTargetInSightAction), "OnStart")]
    internal static class SightActionGuardPatch
    {
        private static bool Prefix(SetTargetInSightAction __instance, ref Unity.Behavior.Node.Status __result)
        {
            var e = __instance.Agent?.Value;
            if (e == null || e.Group != null) return true;
            if (e.isActiveAndEnabled && !e.IsDead && (!EnemySync.GuestPuppets || EnemySync.RunsHere(e)))
            {
                try { e.SetupGroup(); }
                catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemy] Giving {e.name} a group: {ex.Message}"); }
                if (e.Group != null) return true;
            }
            __result = Unity.Behavior.Node.Status.Failure;
            return false;
        }
    }

    // Moving to a target that's gone (a teammate's puppet destroyed by a world swap while a graph still held it, as the
    // old scene's enemies restart): the action threw at every start. Without a target there's nowhere to go.
    [HarmonyLib.HarmonyPatch(typeof(MoveToTargetAction), "OnStart")]
    internal static class MoveToGoneTargetGuardPatch
    {
        private static bool Prefix(MoveToTargetAction __instance, ref Unity.Behavior.Node.Status __result)
        {
            if (__instance.Agent?.Value == null || __instance.Target == null || __instance.Target.Value != null) return true;
            __result = Unity.Behavior.Node.Status.Failure;
            return false;
        }
    }

    // The rest of the game's group conditions read Enemy.Group without a check: a group-less enemy (while a scene
    // reloads, after the host's death) threw in each of them every frame. Without a group, the answer is "no".
    [HarmonyLib.HarmonyPatch]
    internal static class GroupConditionsGuardPatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var t in new[] { typeof(GroupCountCondition), typeof(GroupCountWithIDCondition), typeof(GroupSkillCooldownCondition), typeof(HasGroupFleedCondition) })
                yield return HarmonyLib.AccessTools.Method(t, "IsTrue");
        }

        private static readonly System.Collections.Generic.Dictionary<System.Type, System.Reflection.FieldInfo> enemyField =
            new System.Collections.Generic.Dictionary<System.Type, System.Reflection.FieldInfo>();

        private static bool Prefix(object __instance, ref bool __result)
        {
            var type = __instance.GetType();
            if (!enemyField.TryGetValue(type, out var f)) enemyField[type] = f = HarmonyLib.AccessTools.Field(type, "Enemy");
            var e = (f?.GetValue(__instance) as Unity.Behavior.BlackboardVariable<Enemy>)?.Value;
            if (e != null && e.Group != null) return true;
            __result = false;
            return false;
        }
    }
}
