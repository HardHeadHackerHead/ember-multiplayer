using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Host side: lets enemies see and hit a guest's puppet. Hits never land here; CombatResolvePatch
    // forwards them to the guest, who resolves them on the real character.
    internal class RemotePlayerTarget : MonoBehaviour, IDamageable, ICenterOfMass
    {
        public byte PlayerId;
        public bool Dead;
        public Transform Center;

        public EffectsHandler EffectsHandler => null;
        public bool IsDead => Dead;
        public DamageableType DamageableType => DamageableType.Player;
        public Transform CenterOfMass => Center != null ? Center : transform;
        public void ApplyDamage(DamageData damageData) { }
        public CharacterStats GetCharacterStats() => null;
        public Transform GetTransform() => transform;

        // Adds a hurtbox shaped like the real player's to the puppet.
        public static RemotePlayerTarget Attach(GameObject puppet, PlayerMaster template, byte playerId)
        {
            var target = puppet.AddComponent<RemotePlayerTarget>();
            target.PlayerId = playerId;
            var center = new GameObject("CenterOfMass").transform;
            center.SetParent(puppet.transform, false);
            center.localPosition = template.CenterOfMass != null ? template.CenterOfMass.localPosition : Vector3.up * 0.6f;
            target.Center = center;

            var src = template.transform.Find("DamageableCollider")?.GetComponent<CapsuleCollider2D>();
            var box = new GameObject("MP_Hurtbox");
            box.layer = src != null ? src.gameObject.layer : LayerMask.NameToLayer("IDamageables");
            box.transform.SetParent(puppet.transform, false);
            if (src != null) box.transform.localPosition = src.transform.localPosition;
            var cap = box.AddComponent<CapsuleCollider2D>();
            cap.isTrigger = true;
            if (src != null)
            {
                cap.size = src.size;
                cap.offset = src.offset;
                cap.direction = src.direction;
            }
            else cap.size = new Vector2(0.6f, 1.2f);
            // Trigger contacts need a body; a kinematic one keeps the puppet unaffected by physics.
            var rb = puppet.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Kinematic;
            rb.simulated = true;
            box.AddComponent<RemotePlayerHurtbox>().Owner = target;
            return target;
        }
    }

    // The collider's IDamageable (enemies look for IDamageable on the collider they overlap).
    internal class RemotePlayerHurtbox : MonoBehaviour, IDamageable
    {
        public RemotePlayerTarget Owner;
        public EffectsHandler EffectsHandler => null;
        public bool IsDead => Owner == null || Owner.IsDead;
        public DamageableType DamageableType => DamageableType.Player;
        public void ApplyDamage(DamageData damageData) { }
        public CharacterStats GetCharacterStats() => null;
        public Transform GetTransform() => Owner != null ? Owner.transform : transform;
    }
}
