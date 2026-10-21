using System.Collections.Generic;
using System.Linq;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // The parts of a DamageData that matter on the other machine. Object references (attacker, target,
    // weapon) are filled in by the receiver; the Attack asset travels by name.
    internal static class DamageCodec
    {
        private static Dictionary<string, Attack> attacks;

        public static void Write(NetDataWriter w, DamageData d)
        {
            w.Put(d.Amount);
            w.Put((int)d.Result);
            w.Put((int)d.HitType);
            w.Put((int)d.KnockBackForce);
            w.Put((byte)((d.NoStagger ? 1 : 0) | (d.AutoKill ? 2 : 0) | (d.HadCounterAttack ? 4 : 0) | (d.NoSound ? 8 : 0)));
            w.Put(d.BaseCriticalChance);
            w.Put(d.BaseEffectsChance);
            w.Put(d.Attack != null ? d.Attack.name : "");
            var effects = d.Effects ?? new List<DamageEffect>();
            w.Put((byte)Mathf.Min(effects.Count, 255));
            foreach (var e in effects.Take(255))
            {
                w.Put((int)e.Effect);
                w.Put(e.OtherEffect ?? "");
                w.Put(e.Chance);
                w.Put(e.DamagePerSec);
                w.Put(e.Duration);
                w.Put(e.HideUI);
            }
            var level = d.Level ?? new int[0];
            w.Put((byte)level.Length);
            foreach (int l in level) w.Put(l);
        }

        public static DamageData Read(NetDataReader r)
        {
            var d = new DamageData
            {
                Amount = r.GetInt(),
                Result = (DamageResult)r.GetInt(),
                HitType = (DamageHitType)r.GetInt(),
                KnockBackForce = (KnockBack)r.GetInt(),
            };
            byte flags = r.GetByte();
            d.NoStagger = (flags & 1) != 0;
            d.AutoKill = (flags & 2) != 0;
            d.HadCounterAttack = (flags & 4) != 0;
            d.NoSound = (flags & 8) != 0;
            d.BaseCriticalChance = r.GetFloat();
            d.BaseEffectsChance = r.GetFloat();
            d.Attack = FindAttack(r.GetString());
            int n = r.GetByte();
            d.Effects = new List<DamageEffect>(n);
            for (int i = 0; i < n; i++)
                d.Effects.Add(new DamageEffect
                {
                    Effect = (EffectType)r.GetInt(),
                    OtherEffect = r.GetString(),
                    Chance = r.GetFloat(),
                    DamagePerSec = r.GetInt(),
                    Duration = r.GetFloat(),
                    HideUI = r.GetBool(),
                });
            int levels = r.GetByte();
            if (levels > 0)
            {
                d.Level = new int[levels];
                for (int i = 0; i < levels; i++) d.Level[i] = r.GetInt();
            }
            d.CreatedEffects = new List<Effect>();
            d.Processors = new List<DamageDataProcessor>();
            return d;
        }

        public static Attack FindAttackByName(string name) => FindAttack(name);

        private static Attack FindAttack(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (attacks == null || !attacks.ContainsKey(name))
            {
                attacks = new Dictionary<string, Attack>();
                foreach (var a in Resources.FindObjectsOfTypeAll<Attack>())
                    if (!attacks.ContainsKey(a.name)) attacks[a.name] = a;
            }
            return attacks.TryGetValue(name, out var found) ? found : null;
        }
    }
}
