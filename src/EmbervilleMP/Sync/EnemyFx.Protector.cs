using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // The Acolyte Protector: its aura (StartAura: -80% damage to itself, its group's hits redirected to it, no
    // hit-freeze) and its march (stat modifiers, no hit-freeze) are runtime modifiers its graph puts on and takes off
    // on the runner (Patches/EnemyStatePatches.cs tells us when).
    //   - Puppets: the aura is a flag of the runner's effect list; the puppet runs the protector's own
    //     startProtectingBuff / endProtectingBuff events (its look), never the modifiers. The absorb (a protected
    //     enemy's hit flying to the protector as an orb, the hit VFX, the protector's self-damage VFX) is an EnemyVisual.
    //   - A new runner: aura and march come as flags (the runner left or dropped: EnemyFx.Takeover.cs) or as two named
    //     entries of EnemyFxHandoff ("mp:aura", "mp:march": older builds find no such effect and skip them). It starts
    //     them the game's way for AdoptedSeconds, then ends them the game's way, unless the graph ended them first: the
    //     graph starts over on the new runner and would never run the action that ends them.
    //   - A runner that becomes a puppet ends its own (they were the fight's; the new runner's flags show what's on).
    internal partial class EnemyFx
    {
        private const byte FlagAura = 4, FlagMarch = 8;
        public const string AuraEntry = "mp:aura", MarchEntry = "mp:march";
        public const float AdoptedSeconds = 5f;
        public int AuraShown, AuraHidden, AbsorbsSent, AbsorbsShown, ProtectorsAdopted, AdoptedEnded;

        private static readonly HashSet<AcolyteProtector> auraOn = new HashSet<AcolyteProtector>(), marchOn = new HashSet<AcolyteProtector>();
        private static readonly Dictionary<AcolyteProtector, float> adoptedAura = new Dictionary<AcolyteProtector, float>(), adoptedMarch = new Dictionary<AcolyteProtector, float>();
        private static readonly Dictionary<Enemy, AcolyteProtector> protectors = new Dictionary<Enemy, AcolyteProtector>();
        private static int protectorsScene = -1;

        // The protector component points at its enemy (it isn't always on the enemy's own object): looked up once per
        // scene from every protector there, then by enemy.
        public static AcolyteProtector ProtectorOf(Enemy e)
        {
            if (e == null) return null;
            int handle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (handle != protectorsScene)
            {
                protectorsScene = handle;
                protectors.Clear();
                foreach (var x in Object.FindObjectsByType<AcolyteProtector>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (x != null && x.enemy != null) protectors[x.enemy] = x;
            }
            if (!protectors.TryGetValue(e, out var p))
            {
                // (one made at runtime, after the scan)
                p = e.GetComponentInParent<AcolyteProtector>(true) ?? e.GetComponentInChildren<AcolyteProtector>(true);
                if (p != null && p.enemy != e) p = null;
                protectors[e] = p;
            }
            return p;
        }

        public static bool AuraOn(AcolyteProtector p) => p != null && auraOn.Contains(p);
        public static bool MarchOn(AcolyteProtector p) => p != null && marchOn.Contains(p);
        public static bool AuraAdopted(AcolyteProtector p) => p != null && adoptedAura.ContainsKey(p);

        private static void ClearProtectors()
        {
            auraOn.Clear();
            marchOn.Clear();
            adoptedAura.Clear();
            adoptedMarch.Clear();
            protectors.Clear();
            protectorsScene = -1;
        }

        // ---- the game's calls (patches) ----

        internal static void OnAura(AcolyteProtector p, bool on)
        {
            if (on) auraOn.Add(p);
            else { auraOn.Remove(p); adoptedAura.Remove(p); }
        }

        internal static void OnMarch(AcolyteProtector p, bool on)
        {
            if (on) marchOn.Add(p);
            else { marchOn.Remove(p); adoptedMarch.Remove(p); }
        }

        // The graph starts one of what we adopted: ours ends first, or the modifiers would go on twice (StartMarch
        // doesn't check; StartAura only checks its group's cooldown, which a merged group doesn't have).
        internal static void BeforeStart(AcolyteProtector p, bool aura)
        {
            if (aura && adoptedAura.ContainsKey(p) && p.enemy != null && p.enemy.Group != null && !p.enemy.Group.IsSkillOnCooldown(p.groupSkill)) p.EndAura();
            if (!aura && adoptedMarch.ContainsKey(p)) p.EndMarch();
        }

        // ---- runner: the flags ----

        private static byte ProtectorFlags(Enemy e)
        {
            var p = ProtectorOf(e);
            if (p == null || !p.isActiveAndEnabled) return 0;
            return (byte)((auraOn.Contains(p) ? FlagAura : 0) | (marchOn.Contains(p) ? FlagMarch : 0));
        }

        private static IEnumerable<NetEffect> ProtectorHandoff(Enemy e)
        {
            byte f = ProtectorFlags(e);
            if ((f & FlagAura) != 0) yield return new NetEffect { Type = EffectType.Other, Name = AuraEntry, Stacks = 1, Remaining = -1f };
            if ((f & FlagMarch) != 0) yield return new NetEffect { Type = EffectType.Other, Name = MarchEntry, Stacks = 1, Remaining = -1f };
        }

        private static bool IsProtectorEntry(string name) => name == AuraEntry || name == MarchEntry;

        // ---- puppets: the aura's look ----

        private static void ShowProtector(Enemy e, Mirror m)
        {
            bool want = (m.Flags & FlagAura) != 0;
            if (want == m.AuraShown) return;
            var p = ProtectorOf(e);
            if (p == null) return;
            m.AuraShown = want;
            try
            {
                if (want) p.startProtectingBuff?.Invoke();
                else p.endProtectingBuff?.Invoke();
                if (Instance != null) { if (want) Instance.AuraShown++; else Instance.AuraHidden++; }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemyfx] Aura look on {e.name}: {ex.Message}"); }
        }

        private static void HideProtector(Enemy e, Mirror m)
        {
            if (!m.AuraShown) return;
            m.AuraShown = false;
            var p = ProtectorOf(e);
            // (a death ran the game's own EndAura on it already)
            if (p == null || e.IsDead) return;
            try { p.endProtectingBuff?.Invoke(); }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemyfx] Aura look on {e.name}: {ex.Message}"); }
        }

        // We show puppets now: what our copy ran as the scene's runner ends here (no modifiers on puppets).
        private void EndLocalProtectors()
        {
            foreach (var p in auraOn.ToList()) if (p != null && p.isActiveAndEnabled) Quietly(p.EndAura);
            foreach (var p in marchOn.ToList()) if (p != null && p.isActiveAndEnabled) Quietly(p.EndMarch);
            auraOn.Clear();
            marchOn.Clear();
            adoptedAura.Clear();
            adoptedMarch.Clear();
        }

        private static void Quietly(System.Action a)
        {
            try { a(); } catch (System.Exception ex) { Plugin.Log.LogWarning("[enemyfx] Protector: " + ex.Message); }
        }

        // ---- a new runner: aura and march carry on ----

        private static bool AdoptProtector(Enemy e, string entry)
        {
            var p = ProtectorOf(e);
            if (p == null || !p.isActiveAndEnabled || e.Group == null || !EnemySync.IsAuthority) return false;
            float until = Time.realtimeSinceStartup + AdoptedSeconds;
            try
            {
                if (entry == AuraEntry)
                {
                    if (auraOn.Contains(p) || !p.StartAura()) return false;   // (sets the group's cooldown: the graph won't start another)
                    adoptedAura[p] = until;
                }
                else
                {
                    if (marchOn.Contains(p)) return false;
                    p.StartMarch();
                    adoptedMarch[p] = until;
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemyfx] Carrying on {entry} of {e.name}: {ex.Message}"); return false; }
            if (Instance != null) Instance.ProtectorsAdopted++;
            Plugin.Log.LogInfo($"[enemyfx] {e.name}'s {(entry == AuraEntry ? "aura" : "march")} carries on for {AdoptedSeconds:F0} s");
            return true;
        }

        private void ProtectorTick(float now)
        {
            if (adoptedAura.Count == 0 && adoptedMarch.Count == 0) return;
            foreach (var kv in adoptedAura.Where(kv => now >= kv.Value).ToList())
            {
                adoptedAura.Remove(kv.Key);
                if (kv.Key != null && kv.Key.isActiveAndEnabled && auraOn.Contains(kv.Key)) { Quietly(kv.Key.EndAura); AdoptedEnded++; }
            }
            foreach (var kv in adoptedMarch.Where(kv => now >= kv.Value).ToList())
            {
                adoptedMarch.Remove(kv.Key);
                if (kv.Key != null && kv.Key.isActiveAndEnabled && marchOn.Contains(kv.Key)) { Quietly(kv.Key.EndMarch); AdoptedEnded++; }
            }
        }

        // ---- the absorb ----

        // Runner: a hit on a protected enemy went to its protector (AcolyteProtector.DamageAbsorbModifier.ModifyValue):
        // its VFX for the others. from: where the hit came from (another player's puppet for their hits).
        public void OnRunnerAbsorb(Enemy protector, IDamageable target, Vector3 from)
        {
            var es = EnemySync.Instance;
            var t = target is DamageableCollider dc ? dc.Damageable as Enemy : target as Enemy;
            int pid = es?.IdOf(protector) ?? 0, tid = es?.IdOf(t) ?? 0;
            if (pid == 0 || tid == 0 || !OthersHere()) return;
            Vector3 dir = t.GetTransform().position - from;
            session.Send(Msg.EnemyVisual, w =>
            {
                w.Put(SceneHash);
                w.Put(pid);
                w.Put((byte)Visual.Absorb);
                w.Put(tid);
                w.Put(dir.x);
                w.Put(dir.y);
            });
            AbsorbsSent++;
        }

        // Puppets: the same VFX as ModifyValue, for show: the hit flash at the protected enemy, turned away from the
        // hitter, and the orb flying to the protector, which lights its self-damage VFX when it gets there (the damage
        // itself is the runner's).
        private void ShowAbsorb(NetDataReader r, Enemy protector, bool shown)
        {
            int tid = r.GetInt();
            var dir = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
            var t = EnemySync.Instance?.ById(tid);
            var p = ProtectorOf(protector);
            if (!shown || p == null || t == null || !t.isActiveAndEnabled) return;
            Vector3 at = t.GetTransform().position;
            try
            {
                if (p.damageHitVFXPrefab != null)
                {
                    var vfx = PoolManager.Get(p.damageHitVFXPrefab);
                    if (vfx != null)
                    {
                        vfx.SetActive(true);
                        vfx.transform.position = at;
                        var d = dir.ToDirection();
                        var anim = vfx.GetComponent<Animator>();
                        if (anim != null) anim.SetFloat("DirFactor", d == Direction.Up ? 1f : d == Direction.Down ? -1f : 0f);
                        vfx.transform.rotation = Quaternion.Euler(0f, dir.x < 0f ? 180f : 0f, 0f);
                    }
                }
                if (p.damageVFXPrefab != null)
                {
                    var orb = Object.Instantiate(p.damageVFXPrefab);
                    orb.target = protector.transform;
                    orb.transform.position = at;
                    var self = p.selfDamageVFX;
                    orb.OnArrived += _ => { if (self != null) self.SetActive(true); };
                }
                AbsorbsShown++;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemyfx] Absorb on {protector.name}: {ex.Message}"); }
        }
    }
}
