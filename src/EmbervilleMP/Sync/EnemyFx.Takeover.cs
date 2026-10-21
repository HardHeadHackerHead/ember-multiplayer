using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // The runner left the scene mid-fight, or dropped: it hands nothing over (EnemyFxHandoff only goes to a
    // higher-priority player walking in). The player taking over holds the runner's last lists on its puppets: they
    // become real on its copies, the game's way (EffectsHandler, as ApplyReal does for a handoff), for the time they had
    // left: burns, poisons, freezes, Sorcerers' shields, skills' named effects, the Protector's aura and march.
    // Invulnerability and tints are left to the graphs, which start over on the new runner and set them again when they
    // get there (kept, they could stay forever: the action that ends them never runs).
    internal partial class EnemyFx
    {
        public const float TakeoverWindow = 8f;   // a drop: the runner goes quiet (2 s), then its presence times out (3 s)
        public int TakeoverEffects, TakeoverEnemies;

        private readonly Dictionary<int, Mirror> takeover = new Dictionary<int, Mirror>();
        private float takeoverAt;

        // ReleaseMirrors(true): we stopped showing puppets. Keep their lists until we know whether the scene is ours now.
        private void KeepForTakeover()
        {
            takeover.Clear();
            foreach (var kv in mirrors)
                if (kv.Value.Effects.Count > 0 || (kv.Value.Flags & (FlagAura | FlagMarch)) != 0) takeover[kv.Key] = kv.Value;
            takeoverAt = Time.realtimeSinceStartup;
        }

        private void TakeoverTick(float now)
        {
            ProtectorTick(now);
            if (takeover.Count == 0) return;
            // Puppets again (the runner was only quiet), or the scene isn't ours after all.
            if (EnemySync.GuestPuppets || now - takeoverAt > TakeoverWindow) { takeover.Clear(); return; }
            if (!EnemySync.IsAuthority) return;
            var es = EnemySync.Instance;
            int effects = 0, enemies = 0;
            Applying = true;
            try
            {
                foreach (var kv in takeover)
                {
                    var e = es.ById(kv.Key);
                    if (e == null || !e.isActiveAndEnabled || e.IsDead) continue;
                    int n = Adopt(e, kv.Value, now);
                    if (n > 0) { effects += n; enemies++; }
                }
            }
            finally { Applying = false; }
            takeover.Clear();
            TakeoverEffects += effects;
            TakeoverEnemies += enemies;
            Plugin.Log.LogInfo($"[enemyfx] Took over the fight: {effects} effects on {enemies} enemies carry on");
        }

        private static int Adopt(Enemy e, Mirror m, float now)
        {
            var h = e.EffectsHandler;
            if (h == null) return 0;
            int n = 0;
            float elapsed = now - m.At;
            foreach (var x in m.Effects)
            {
                // What our copy has of its own already (an effect our character put on it, a shadow until now) stays.
                if (h.effects != null && h.effects.Any(y => y != null && !y.IsRemoved && y.Type == x.Type && (x.Type != EffectType.Other || y.Name == x.Name))) continue;
                var left = x;
                if (x.Remaining >= 0f)
                {
                    left.Remaining = x.Remaining - elapsed;
                    if (left.Remaining < 0.2f) continue;
                }
                try { if (ApplyReal(e, left)) n++; }
                catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemyfx] Taking over {x.Type} on {e.name}: {ex.Message}"); }
            }
            if ((m.Flags & FlagAura) != 0 && AdoptProtector(e, AuraEntry)) n++;
            if ((m.Flags & FlagMarch) != 0 && AdoptProtector(e, MarchEntry)) n++;
            return n;
        }
    }
}
