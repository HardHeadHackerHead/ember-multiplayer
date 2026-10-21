using System.Collections.Generic;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Other players' projectiles (bows, staffs): the shooter's machine decides hits; everyone else sees a
    // harmless copy flying the same way. Other spawned effects (areas, slams, beams) are in SkillFx.
    internal class PlayerFx
    {
        public static PlayerFx Instance { get; private set; }
        public int ProjectilesSent, ProjectilesShown;
        public readonly SkillFx Skills;

        private readonly Session session;
        private readonly Presence presence;

        public PlayerFx(Session session, Presence presence)
        {
            Instance = this;
            this.session = session;
            this.presence = presence;
            session.On(Msg.PlayerProjectile, OnProjectile);
            Skills = new SkillFx(session, presence);
        }

        public void OnLocalProjectile(Attack attack, Vector3 pos, Vector3 dir)
        {
            session.Send(Msg.PlayerProjectile, w =>
            {
                w.Put(attack.name);
                w.Put(pos.x); w.Put(pos.y);
                w.Put(dir.x); w.Put(dir.y);
            });
            ProjectilesSent++;
        }

        private void OnProjectile(byte from, NetDataReader r)
        {
            var attack = DamageCodec.FindAttackByName(r.GetString());
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
            var dir = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
            if (attack == null || attack.projectile == null || !presence.Remotes.TryGetValue(from, out var shooter) || !shooter.Shown) return;
            // Our own copies, not the game's pool: the game never sets a pooled projectile's Owner for the player,
            // so a copy's owner would stay on an instance our own character fires later.
            var go = FxCopies.Get(attack.projectile, keepColliders: true);
            if (go == null || !go.TryGetComponent<Projectile>(out var p)) return;
            var owner = SkillFx.OwnerFor(shooter);
            p.transform.position = pos;
            p.Direction = dir;
            p.Owner = owner;   // explosions on hit (TriggerExplosion) take it over
            p.damageData = SkillFx.VisualDamage(owner, attack, null);
            go.SetActive(true);
            ProjectilesShown++;
        }
    }

    // Marks damage that is only for show (another player's projectile as seen here): CombatResolvePatch drops it.
    internal class VisualOnlyAttacker : IDamageable, IVisualOnly
    {
        private readonly Transform transform;
        public VisualOnlyAttacker(Transform t) { transform = t; }
        public EffectsHandler EffectsHandler => null;
        public bool IsDead => false;
        public DamageableType DamageableType => DamageableType.Player;
        public void ApplyDamage(DamageData damageData) { }
        public CharacterStats GetCharacterStats() => null;
        public Transform GetTransform() => transform;
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.PerformAttackHit))]
    internal static class PlayerProjectilePatch
    {
        private static void Prefix(PlayerMaster __instance, out Attack __state) => __state = __instance.currentAttack;

        private static void Postfix(PlayerMaster __instance, Attack __state)
        {
            var attack = __state;
            if (PlayerFx.Instance == null || attack == null || attack.projectile == null || (attack.attackHit & AttackHit.Projectile) == 0) return;
            if (__instance != Game.LocalPlayer) return;
            var proto = attack.projectile.GetComponent<Projectile>();
            Vector3 pos = __instance.spawnPointHolder.GetPoint(attack.spawnPointName, __instance.direction).position;
            Vector3 dir = proto != null && proto.OmniDirectional ? (Vector3)__instance.lastLookDir : __instance.direction.ToVector();
            PlayerFx.Instance.OnLocalProjectile(attack, pos, dir);
        }
    }
}
