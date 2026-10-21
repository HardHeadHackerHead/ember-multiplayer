using System.Collections.Generic;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // No friendly fire. The game keeps a player's own hits off the player by its "Player" tag; a teammate's puppet is
    // untagged (the tag drives interactions), so on every copy:
    //   - a hit whose attacker is a player (ours, or a copy of someone's) on a puppet is dropped (CombatResolvePatch);
    //   - anything that ignores the "Player" tag (a player's arrows and bombs, the Matriarch's body outside her dash)
    //     ignores the puppets too, and a player's projectile flies on through them (CoopCombatPatches).
    internal static class FriendlyFire
    {
        public static int HitsDropped, ProjectilesPassed, TagSkips;

        public static bool IsPlayer(IDamageable a) =>
            a is PlayerMaster || a is IVisualOnly || a is RemotePlayerTarget || a is RemotePlayerHurtbox;

        public static bool IsPuppet(Collider2D c) => c != null && c.GetComponent<RemotePlayerHurtbox>() != null;

        // Would the game skip our own character here? Its hurtbox and its body carry the tags a puppet stands for.
        public static bool IgnoredLikePlayer(Collider2D c, List<string> ignoreTags)
        {
            if (ignoreTags == null || ignoreTags.Count == 0 || !IsPuppet(c)) return false;
            ReadTags();
            foreach (var t in ignoreTags)
                if (t == bodyTag || t == hurtboxTag) return true;
            return false;
        }

        private static string bodyTag = "Player", hurtboxTag = "Player";
        private static PlayerMaster tagsOf;

        private static void ReadTags()
        {
            var me = Game.LocalPlayer;
            if (me == null || me == tagsOf) return;
            tagsOf = me;
            var rb = me.rb;
            bodyTag = rb != null ? rb.gameObject.tag : me.gameObject.tag;
            var hurtbox = me.transform.Find("DamageableCollider");
            hurtboxTag = hurtbox != null ? hurtbox.gameObject.tag : bodyTag;
        }
    }
}
