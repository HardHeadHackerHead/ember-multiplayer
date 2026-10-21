using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using EmbervilleMP.Net;
using LiteNetLib.Utils;
using Unity.Behavior;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync
{
    // What a fight looks like and does beyond EnemySync's positions, animations, hits and deaths. The scene's runner
    // simulates the enemies; everyone else shows puppets (see EnemySync.cs).
    //   runner -> all   : EnemyEffects, on change: each enemy's active status effects, invulnerability and tint. A
    //                     puppet replays what the game's EffectsHandler wiring does on the real enemy (setEffectType ->
    //                     Enemy.SetEffect's status loop on the extra FX animator, the per-type enter/exit events)
    //   runner -> all   : EnemyVisual: effect popups ("Burn", "Stunned"), sprite fades from enemy graphs, the VFX of
    //                     enemy groups a graph spawns
    //   others -> runner: EnemyEffectIntent: an effect our character put on a puppet (passives like Bleeding Edge,
    //                     skill graphs, our skills' effect areas) and its removal; the runner applies it to the real
    //                     enemy, crediting us
    //   others -> runner: EnemyHitRaw: our hit as it leaves our side of CombatSystem (amount, crit, rolled effects).
    //                     The target's side (defence, damage-received modifiers like the Acolyte Protector's aura and
    //                     absorb, block, parry) is the real enemy's, on the runner; everyone, the hitter too, shows
    //                     the number it got (EnemyDamaged)
    //   old runner -> new runner: EnemyFxHandoff, next to EnemySync's handoff: active effects and boss stages
    // A runner that leaves or drops hands nothing over: the player taking over makes its mirrored lists real
    // (EnemyFx.Takeover.cs). The Acolyte Protector's aura and march (EnemyFx.Protector.cs) go as flags and handoff entries.
    // Effects that land on a puppet here (from anything) stay as "shadows": their logic runs (a mastery may ask whether
    // the target bleeds), but their ticks never land (no attacker: EnemyApplyDamagePatch drops them) and their visuals
    // are muted (EnemyFxMuteShadowPatch). What a puppet shows is the runner's list.
    internal partial class EnemyFx
    {
        public static EnemyFx Instance { get; private set; }
        public int ListsSent, ListsReceived, IntentsSent, IntentsApplied, RawHitsSent, RawHitsApplied, PopupsSent, PopupsShown,
            FadesShown, SpawnVfxShown, HitEventsReplayed, HandoffEffects, HandoffStages, GroupReplays, SightFixes;
        // Tests: numbers this copy showed for its own hits (the runner's), and what each raw hit dealt on the runner.
        public readonly List<int> OwnNumbers = new List<int>();
        public readonly List<int> RawDealt = new List<int>();

        // ---- context, read by the patches (Patches/EnemyFxPatches.cs) ----
        internal static bool Applying;           // we're applying something from the network: let it through
        internal static bool LocalSource;        // the effect being added comes from our own character
        internal static int[] LocalLevel;        // ... at this skill level, when the caller knows it
        internal static Skill LocalSkill;        // ... from this skill's effects (names repeat across skills)
        internal static bool InHandleEffect;     // HandleEffect calls AddEffect: forward once
        internal static IDamageable HitSource;   // runner: the other player behind the hit being applied
        internal static bool HitterShowed;       // runner: that player already showed this hit's number
        internal static byte IntentFrom;         // runner: who asked for the effect being applied (0: nobody)

        public const byte CausePlayer = 1, CauseNone = 2;   // EnemyDied: a player's hit, or nobody's (self-destruct, DoT...)
        private enum Op : byte { Effect = 1, Creator = 2, RemoveName = 3, RemoveType = 4 }
        private enum Visual : byte { Popup = 1, Fade = 2, SpawnVfx = 3, Absorb = 4 }
        private const byte FlagInvulnerable = 1, FlagTinted = 2;
        private const float PollInterval = 0.2f;
        private const int MaxEffects = 32;

        public struct NetEffect
        {
            public EffectType Type;
            public string Name;     // EffectType.Other only
            public int Stacks;
            public float Remaining; // seconds, -1 without a duration
            public int Dps;         // damage over time per second (shield: stacks)
        }

        private class Snap
        {
            public readonly List<NetEffect> Effects = new List<NetEffect>();
            public byte Flags;
            public Color32 Tint;
            public string Signature = "";
            public readonly Dictionary<string, float> EndsAt = new Dictionary<string, float>();
            public bool Empty => Effects.Count == 0 && Flags == 0;
        }

        // A puppet's mirrored state, and what we changed on it (to put back when it stops being a puppet).
        private class Mirror
        {
            public List<NetEffect> Effects = new List<NetEffect>();
            public byte Flags;
            public Color32 Tint;
            public bool Dirty = true;
            public Enemy AppliedTo;
            public bool? OriginalCanTakeDamage;
            public Color[] OriginalColors;
            public readonly List<EffectType> Entered = new List<EffectType>();   // types whose enter events we ran
            public float At;              // when the list came (its times left count from then)
            public bool AuraShown;        // the Protector's aura visuals we started on it
        }

        private readonly Session session;
        private readonly Presence presence;
        private readonly Dictionary<int, Snap> sent = new Dictionary<int, Snap>();
        private HashSet<(byte, string)> seenPairs = new HashSet<(byte, string)>();
        private readonly Dictionary<Enemy, Color> tints = new Dictionary<Enemy, Color>();
        private readonly Dictionary<int, Mirror> mirrors = new Dictionary<int, Mirror>();
        private readonly HashSet<(int, string)> forwardedNames = new HashSet<(int, string)>();
        private readonly Dictionary<EnemyGroupAttack, ushort> groupTokens = new Dictionary<EnemyGroupAttack, ushort>();
        private ushort nextGroupToken;
        private class ReplayGroup { public EnemyGroupAttack Group; public float At; }
        private readonly Dictionary<(byte, ushort), ReplayGroup> replayGroups = new Dictionary<(byte, ushort), ReplayGroup>();
        private readonly Dictionary<Enemy, bool> damageByPlayer = new Dictionary<Enemy, bool>();
        private int scene = -1;
        private float nextPoll, nextSweep;
        private bool wasPuppets;

        public EnemyFx(Session session, Presence presence)
        {
            Instance = this;
            this.session = session;
            this.presence = presence;
            session.On(Msg.EnemyEffects, OnEffects);
            session.On(Msg.EnemyVisual, OnVisual);
            session.On(Msg.EnemyEffectIntent, OnIntent);
            session.On(Msg.EnemyFxHandoff, OnHandoff);
            session.On(Msg.EnemyHitRaw, OnHitRaw);
            session.Ended += () => ReleaseMirrors(false);
        }

        private static int SceneHash => NetIds.SceneHash(Game.SceneName);

        public void Update()
        {
            if (!session.Ready || Session.Current != session || !Game.InScene || EnemySync.Instance == null) return;
            int handle = SceneManager.GetActiveScene().handle;
            if (handle != scene)
            {
                // A new scene: its enemies are new objects, nothing of the last one's applies.
                scene = handle;
                sent.Clear();
                seenPairs.Clear();
                tints.Clear();
                mirrors.Clear();
                forwardedNames.Clear();
                groupTokens.Clear();
                replayGroups.Clear();
                damageByPlayer.Clear();
                ClearProtectors();
            }
            bool puppets = EnemySync.GuestPuppets;
            if (wasPuppets && !puppets) ReleaseMirrors(true);
            if (!wasPuppets && puppets) EndLocalProtectors();   // the runner's flags say what shows now
            wasPuppets = puppets;
            float now = Time.realtimeSinceStartup;
            if (EnemySync.IsAuthority)
            {
                if (now >= nextPoll)
                {
                    nextPoll = now + PollInterval;
                    RunnerTick(now);
                }
            }
            else if (puppets) ApplyMirrors(now);
            TakeoverTick(now);
        }

        // ================= runner: effects, invulnerability, tint =================

        private void RunnerTick(float now)
        {
            var es = EnemySync.Instance;
            string sceneName = Game.SceneName;
            var here = presence.Remotes.Values.Where(r => r.Latest != null && r.Latest.Scene == sceneName).ToList();
            if (here.Count == 0)
            {
                sent.Clear();
                seenPairs.Clear();
                return;
            }
            var areas = new HashSet<string>(here.Select(r => r.Latest.Area));
            // Someone new in a room (arrived, or walked in): they get the whole list of that room once.
            var pairs = new HashSet<(byte, string)>(here.Select(r => (r.Info.Id, r.Latest.Area)));
            var forced = new HashSet<string>(pairs.Where(p => !seenPairs.Contains(p)).Select(p => p.Item2));
            seenPairs = pairs;

            var entries = new List<(int id, Snap s)>();
            var visited = new HashSet<int>();
            foreach (var e in EnemiesManager.AllEnemies)
            {
                if (e == null || !e.isActiveAndEnabled || e.IsDead) continue;
                int id = es.IdOf(e);
                string area = es.AreaOf(e);
                if (id == 0 || !areas.Contains(area)) continue;
                visited.Add(id);
                var snap = Take(e, now);
                sent.TryGetValue(id, out var last);
                bool changed = last == null ? !snap.Empty : last.Signature != snap.Signature || Refreshed(last, snap);
                if (!changed && !(forced.Contains(area) && !snap.Empty)) continue;
                entries.Add((id, snap));
                sent[id] = snap;
            }
            // Gone from the rooms we report (dead, room switched off, players left): its list ends.
            foreach (var id in sent.Keys.Where(k => !visited.Contains(k)).ToList())
            {
                if (!sent[id].Empty) entries.Add((id, new Snap()));
                sent.Remove(id);
            }
            for (int i = 0; i < entries.Count; i += 64)
            {
                var chunk = entries.Skip(i).Take(64).ToList();
                session.Send(Msg.EnemyEffects, w =>
                {
                    w.Put(SceneHash);
                    w.Put((byte)chunk.Count);
                    foreach (var (id, s) in chunk)
                    {
                        w.Put(id);
                        w.Put(s.Flags);
                        if ((s.Flags & FlagTinted) != 0) { w.Put(s.Tint.r); w.Put(s.Tint.g); w.Put(s.Tint.b); }
                        WriteEffects(w, s.Effects);
                    }
                });
                ListsSent += chunk.Count;
            }
        }

        // A duration refreshed (re-applied burn) re-sends the list: the handoff and the log want time left.
        private static bool Refreshed(Snap last, Snap now)
        {
            foreach (var kv in now.EndsAt)
                if (last.EndsAt.TryGetValue(kv.Key, out float prev) && kv.Value > prev + 0.5f) return true;
            return false;
        }

        private Snap Take(Enemy e, float now)
        {
            var s = new Snap();
            s.Effects.AddRange(Effects(e));
            if (!e.CanTakeDamage) s.Flags |= FlagInvulnerable;
            if (tints.TryGetValue(e, out var c)) { s.Flags |= FlagTinted; s.Tint = c; }
            s.Flags |= ProtectorFlags(e);
            var sig = new StringBuilder();
            foreach (var x in s.Effects)
            {
                string key = (int)x.Type + x.Name;
                sig.Append(key).Append(':').Append(x.Stacks).Append(';');
                if (x.Remaining >= 0f) s.EndsAt[key] = now + x.Remaining;
            }
            sig.Append('|').Append(s.Flags).Append('|').Append(s.Tint.r).Append(',').Append(s.Tint.g).Append(',').Append(s.Tint.b);
            s.Signature = sig.ToString();
            return s;
        }

        public static List<NetEffect> Effects(Enemy e)
        {
            var list = new List<NetEffect>();
            var h = e != null ? e.EffectsHandler : null;
            if (h == null || h.effects == null) return list;
            foreach (var x in h.effects)
            {
                if (x == null || x.IsRemoved || list.Count >= MaxEffects) continue;
                list.Add(new NetEffect
                {
                    Type = x.Type,
                    Name = x.Type == EffectType.Other ? x.Name ?? "" : "",
                    Stacks = x is ShieldEffect sh ? sh.Stacks : x.Stacks,
                    Remaining = x.Duration.HasValue ? Mathf.Max(0f, x.Duration.Value - (float)x.timeSinceApplied) : -1f,
                    Dps = x is DamageOverTimeEffect dot ? dot.DamagePerSec : x is ShieldEffect sh2 ? sh2.Stacks : 0,
                });
            }
            return list;
        }

        private static void WriteEffects(NetDataWriter w, List<NetEffect> list)
        {
            int n = Mathf.Min(list.Count, MaxEffects);
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
            {
                var x = list[i];
                w.Put((ushort)x.Type);
                w.Put(x.Name ?? "");
                w.Put((short)Mathf.Clamp(x.Stacks, short.MinValue, short.MaxValue));
                w.Put(x.Remaining);
                w.Put(x.Dps);
            }
        }

        private static List<NetEffect> ReadEffects(NetDataReader r)
        {
            int n = NetGuard.Count(r.GetByte(), r, 14);
            var list = new List<NetEffect>(n);
            for (int i = 0; i < n; i++)
                list.Add(new NetEffect { Type = (EffectType)r.GetUShort(), Name = r.GetString(), Stacks = r.GetShort(), Remaining = r.GetFloat(), Dps = r.GetInt() });
            return list;
        }

        // Runner: TintEnemyAction (Enemy.SetSpriteColor) tinted one of ours.
        public void NoteTint(Enemy e, Color c)
        {
            if (Mathf.Approximately(c.r, 1f) && Mathf.Approximately(c.g, 1f) && Mathf.Approximately(c.b, 1f)) tints.Remove(e);
            else tints[e] = c;
        }

        // ================= everyone else: the puppets show the runner's list =================

        private void OnEffects(byte from, NetDataReader r)
        {
            var es = EnemySync.Instance;
            if (es == null || EnemySync.IsAuthority || from != es.Authority) return;
            if (r.GetInt() != SceneHash) return;
            int n = NetGuard.Count(r.GetByte(), r, 6);
            for (int i = 0; i < n; i++)
            {
                int id = r.GetInt();
                byte flags = r.GetByte();
                var tint = new Color32(255, 255, 255, 255);
                if ((flags & FlagTinted) != 0) tint = new Color32(r.GetByte(), r.GetByte(), r.GetByte(), 255);
                var list = ReadEffects(r);
                if (!mirrors.TryGetValue(id, out var m)) mirrors[id] = m = new Mirror();
                m.Effects = list;
                m.Flags = flags;
                m.Tint = tint;
                m.Dirty = true;
                m.At = Time.realtimeSinceStartup;
                ListsReceived++;
                if (Args.Test != null || list.Count > 0)
                    Plugin.Log.LogInfo($"[enemyfx] #{id} {Describe(list)}{((flags & FlagInvulnerable) != 0 ? " invulnerable" : "")}{((flags & FlagTinted) != 0 ? " tinted" : "")}");
            }
        }

        private static string Describe(List<NetEffect> list) =>
            list.Count == 0 ? "no effects" : string.Join(", ", list.Select(x => (x.Type == EffectType.Other ? x.Name : x.Type.ToString()) + (x.Stacks != 1 ? " x" + x.Stacks : "") + (x.Remaining >= 0f ? $" {x.Remaining:F1}s" : "")));

        public IReadOnlyList<NetEffect> MirroredOf(int id) => mirrors.TryGetValue(id, out var m) ? m.Effects : (IReadOnlyList<NetEffect>)new NetEffect[0];

        private void ApplyMirrors(float now)
        {
            var es = EnemySync.Instance;
            foreach (var kv in mirrors)
            {
                var m = kv.Value;
                var e = es.ById(kv.Key);
                bool puppet = e != null && es.IsPuppet(e) && e.isActiveAndEnabled && !e.IsDead;
                if (m.AppliedTo != null && (m.AppliedTo != e || !puppet)) Release(m);
                if (!puppet || (!m.Dirty && m.AppliedTo == e)) continue;
                m.Dirty = false;
                m.AppliedTo = e;
                ApplyStatus(e, m);
                ApplyFlags(e, m);
            }
            // Leftovers: an enemy that showed effects of its own before it became a puppet, and has no list here.
            if (now >= nextSweep)
            {
                nextSweep = now + 0.5f;
                foreach (var e in EnemiesManager.AllEnemies)
                {
                    if (e == null || !es.IsPuppet(e) || e.appliedEffects == null || e.appliedEffects.Count == 0) continue;
                    int id = es.IdOf(e);
                    if (id == 0 || mirrors.ContainsKey(id)) continue;
                    if (!mirrors.TryGetValue(id, out var m)) mirrors[id] = m = new Mirror();
                    m.Dirty = true;
                }
            }
        }

        // What the game's own wiring does when an effect starts or ends on the real enemy: setEffectType (Enemy.SetEffect:
        // the latest effect's status loop, "EndStatus" otherwise) and the type's enter/exit events. Compared with what the
        // puppet shows (Enemy.appliedEffects), so leftovers go too.
        private static void ApplyStatus(Enemy e, Mirror m)
        {
            var h = e.EffectsHandler;
            if (h == null) return;
            var desired = new List<EffectType>();
            foreach (var x in m.Effects) if (!desired.Contains(x.Type)) desired.Add(x.Type);
            var shown = e.appliedEffects != null ? e.appliedEffects.ToList() : new List<EffectType>();
            foreach (var t in shown.Union(m.Entered).Distinct().ToList())
            {
                if (desired.Contains(t)) continue;
                Unwire(h, t, m.Entered.Contains(t));
                m.Entered.Remove(t);
            }
            foreach (var t in desired)
            {
                bool showing = e.appliedEffects != null && e.appliedEffects.Contains(t);
                bool entered = m.Entered.Contains(t);
                if (showing && entered) continue;
                Wire(h, t, !showing, !entered);
                if (!entered) m.Entered.Add(t);
            }
        }

        private static void Wire(EffectsHandler h, EffectType t, bool setType, bool enter)
        {
            try
            {
                if (enter) h.eventsReceiver?.FirstOrDefault(x => x != null && x.Effect.HasFlag(t))?.OnEnterEffect?.Invoke();
                if (setType) h.setEffectType?.Invoke(t, true);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemyfx] Showing {t} on {h.name}: {ex.Message}"); }
        }

        private static void Unwire(EffectsHandler h, EffectType t, bool events)
        {
            try
            {
                var receiver = h.eventsReceiver?.FirstOrDefault(x => x != null && x.Effect.HasFlag(t));
                if (events) receiver?.OnExitEffect?.Invoke();
                h.setEffectType?.Invoke(t, false);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemyfx] Ending {t} on {h.name}: {ex.Message}"); }
        }

        private static void ApplyFlags(Enemy e, Mirror m)
        {
            // SetEnemyInvulnerableAction on the runner: the puppet then ignores our hits the way the game does
            // (no number, no sound, nothing sent: EnemyApplyDamagePatch checks CanTakeDamage).
            bool invulnerable = (m.Flags & FlagInvulnerable) != 0;
            if (invulnerable == e.CanTakeDamage)
            {
                if (m.OriginalCanTakeDamage == null) m.OriginalCanTakeDamage = e.CanTakeDamage;
                e.CanTakeDamage = !invulnerable;
            }
            bool tinted = (m.Flags & FlagTinted) != 0;
            if (tinted)
            {
                if (m.OriginalColors == null) m.OriginalColors = e.fadableRenderers?.Select(x => x != null ? x.color : Color.white).ToArray();
                try { e.SetSpriteColor(m.Tint); } catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemyfx] Tint {e.name}: {ex.Message}"); }
            }
            else RestoreColors(e, m);
            ShowProtector(e, m);
        }

        // Back to the colours the puppet had (some enemies get random ones), keeping the alpha (fades) it has now.
        private static void RestoreColors(Enemy e, Mirror m)
        {
            var original = m.OriginalColors;
            m.OriginalColors = null;
            if (original == null || e.fadableRenderers == null) return;
            for (int i = 0; i < e.fadableRenderers.Length && i < original.Length; i++)
            {
                var r = e.fadableRenderers[i];
                if (r == null) continue;
                var c = original[i];
                c.a = r.color.a;
                r.color = c;
            }
        }

        // The enemy stopped being a puppet (we run it now, it died, it's gone): put back what we changed.
        private static void Release(Mirror m)
        {
            var e = m.AppliedTo;
            m.AppliedTo = null;
            m.Dirty = true;
            if (e == null) { m.Entered.Clear(); return; }
            var h = e.EffectsHandler;
            if (h != null)
            {
                var shown = (e.appliedEffects ?? new List<EffectType>()).Union(m.Entered).Distinct().ToList();
                foreach (var t in shown) Unwire(h, t, m.Entered.Contains(t));
            }
            m.Entered.Clear();
            if (m.OriginalCanTakeDamage != null) { e.CanTakeDamage = m.OriginalCanTakeDamage.Value; m.OriginalCanTakeDamage = null; }
            RestoreColors(e, m);
            HideProtector(e, m);
        }

        // We stopped showing puppets (we run the scene now, or its runner went quiet): our own copies take over. Their
        // effects' visuals were muted while they were puppets; what's really on them shows again.
        private void ReleaseMirrors(bool resync)
        {
            if (resync) KeepForTakeover();
            foreach (var m in mirrors.Values) if (m.AppliedTo != null) Release(m);
            mirrors.Clear();
            if (!resync) return;
            foreach (var e in EnemiesManager.AllEnemies)
            {
                var h = e != null && e.isActiveAndEnabled && !e.IsDead ? e.EffectsHandler : null;
                if (h == null || h.effects == null) continue;
                foreach (var t in h.effects.Where(x => x != null && !x.IsRemoved).Select(x => x.Type).Distinct().ToList())
                    Wire(h, t, true, true);
            }
        }

        // ================= popups, fades, spawn VFX =================

        // Runner: EffectsHandler.HandleEffect showed a popup over one of ours ("Burn", "Stunned").
        public void OnRunnerPopup(Enemy e, string text)
        {
            int id = EnemySync.Instance?.IdOf(e) ?? 0;
            if (id == 0 || string.IsNullOrEmpty(text) || !OthersHere()) return;
            byte by = IntentFrom;
            session.Send(Msg.EnemyVisual, w => { w.Put(SceneHash); w.Put(id); w.Put((byte)Visual.Popup); w.Put(by); w.Put(text); });
            PopupsSent++;
        }

        // Runner: an enemy graph faded one of ours (FadeEnemySpriteAction).
        public void OnRunnerFade(Enemy e, float duration)
        {
            int id = EnemySync.Instance?.IdOf(e) ?? 0;
            if (id == 0 || !OthersHere()) return;
            session.Send(Msg.EnemyVisual, w => { w.Put(SceneHash); w.Put(id); w.Put((byte)Visual.Fade); w.Put(duration); });
        }

        // Runner: an enemy graph spawned a group with a VFX at each spot (SpawnEnemyGroupAction); the enemies themselves
        // arrive as EnemySpawn.
        public void OnRunnerSpawnVfx(GameObject prefab, List<Vector3> spots)
        {
            if (prefab == null || spots.Count == 0 || !OthersHere()) return;
            int n = Mathf.Min(spots.Count, 32);
            session.Send(Msg.EnemyVisual, w =>
            {
                w.Put(SceneHash);
                w.Put(0);
                w.Put((byte)Visual.SpawnVfx);
                w.Put(prefab.name);
                w.Put((byte)n);
                for (int i = 0; i < n; i++) { w.Put(spots[i].x); w.Put(spots[i].y); }
            });
        }

        private bool OthersHere()
        {
            string sceneName = Game.SceneName;
            return presence.Remotes.Values.Any(r => r.Latest != null && r.Latest.Scene == sceneName);
        }

        private void OnVisual(byte from, NetDataReader r)
        {
            var es = EnemySync.Instance;
            if (es == null || EnemySync.IsAuthority || from != es.Authority) return;
            if (r.GetInt() != SceneHash) return;
            int id = r.GetInt();
            var kind = (Visual)r.GetByte();
            var e = id != 0 ? es.ById(id) : null;
            bool shown = e != null && e.isActiveAndEnabled && !EnemySync.RunsHere(e);
            switch (kind)
            {
                case Visual.Popup:
                {
                    byte by = r.GetByte();
                    string text = r.GetString(64);
                    if (!shown || by == session.LocalId) return;   // whoever caused it saw it on its own copy
                    var at = e.EffectsHandler != null ? e.EffectsHandler.transform.position : e.transform.position;
                    Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, text, at, DamageResult.None));
                    PopupsShown++;
                    break;
                }
                case Visual.Fade:
                {
                    float duration = Mathf.Clamp(r.GetFloat(), 0f, 30f);
                    if (!shown || e.IsDead) return;
                    try { e.FadeSprites(duration, CancellationToken.None).Forget(); FadesShown++; }
                    catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemyfx] Fade {e.name}: {ex.Message}"); }
                    break;
                }
                case Visual.Absorb:
                    ShowAbsorb(r, e, shown);
                    break;
                case Visual.SpawnVfx:
                {
                    string name = r.GetString(128);
                    int n = NetGuard.Count(r.GetByte(), r, 8);
                    var spots = new List<Vector3>(n);
                    for (int i = 0; i < n; i++) spots.Add(new Vector3(r.GetFloat(), r.GetFloat(), 0f));
                    var prefab = FindPrefab(name);
                    if (prefab == null) return;
                    PoolManager.CreatePool(prefab, "VFX", Mathf.Max(4, n), disableFirst: true);
                    foreach (var p in spots)
                    {
                        var go = PoolManager.Get(prefab);
                        if (go == null) continue;
                        go.transform.position = p;
                        go.SetActive(true);
                        SpawnVfxShown++;
                    }
                    break;
                }
            }
        }

        private static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();

        // Spawn VFX are prefabs (assets, not scene objects); looked up once per name.
        private static GameObject FindPrefab(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (prefabs.TryGetValue(name, out var p) && p != null) return p;
            p = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g != null && g.name == name && !g.scene.IsValid() && g.transform.parent == null);
            prefabs[name] = p;
            if (p == null) Plugin.Log.LogWarning($"[enemyfx] No spawn VFX prefab '{name}' here");
            return p;
        }

        // A damage message about a puppet (EnemySync.OnDamaged): the number, "Blocked"/"Dodged" as the runner's
        // Enemy.ApplyDamage shows them, and the enemy's hit event (its flash) when it lost health.
        public void ShowDamage(Enemy e, int amount, DamageResult result, bool ours)
        {
            var at = e.CenterOfMass.position;
            if (result == DamageResult.Parry)
                Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Dodged", at, DamageResult.Parry));
            else
            {
                if (result == DamageResult.Block) Bus<UIDamageEvent>.Invoke(new UIDamageEvent(0f, "Blocked", at, DamageResult.Block));
                if (amount > 0) Bus<UIDamageEvent>.Invoke(new UIDamageEvent(amount, "", at, result == DamageResult.Critical ? DamageResult.Critical : DamageResult.Success));
            }
            if (ours) OwnNumbers.Add(amount);
            if (amount > 0 && EnemySync.Instance != null && EnemySync.Instance.IsPuppet(e))
            {
                try { e.onHitEvent?.Invoke(); HitEventsReplayed++; }
                catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemyfx] Hit event of {e.name}: {ex.Message}"); }
            }
        }

        // ================= effects our character puts on puppets go to the runner =================

        private static bool PuppetHere(EffectsHandler h, out Enemy e, out int id)
        {
            e = h != null ? h.owner as Enemy : null;
            id = 0;
            var es = EnemySync.Instance;
            if (e == null || es == null || !EnemySync.GuestPuppets || EnemySync.Passthrough || Applying || EnemySync.RunsHere(e) || e.IsDead) return false;
            id = es.IdOf(e);
            return id != 0;
        }

        // Shadows (effects on puppets) show nothing of their own: the runner's list does.
        public static bool MuteShadow(EffectsHandler h)
        {
            var e = h != null ? h.owner as Enemy : null;
            var es = EnemySync.Instance;
            return e != null && es != null && EnemySync.GuestPuppets && !EnemySync.Passthrough && !Applying && es.IsPuppet(e);
        }

        public void OnPuppetHandleEffect(EffectsHandler h, DamageEffect de)
        {
            if (!LocalSource || !PuppetHere(h, out var e, out int id)) return;
            if (de.Effect == EffectType.Neutral || de.Effect == EffectType.Trapped) return;
            SendIntent(id, Op.Effect, w =>
            {
                w.Put((int)de.Effect);
                w.Put(de.OtherEffect ?? "");
                w.Put(de.Chance);
                w.Put(de.DamagePerSec);
                w.Put(de.Duration);
                w.Put(de.HideUI);
            });
        }

        public void OnPuppetAddEffect(EffectsHandler h, Effect effect)
        {
            if (effect == null || !LocalSource || !PuppetHere(h, out var e, out int id)) return;
            if (effect is DamageOverTimeEffect dot)
            {
                SendIntent(id, Op.Effect, w =>
                {
                    w.Put((int)effect.Type);
                    w.Put("");
                    w.Put(0f);
                    w.Put(dot.DamagePerSec);
                    w.Put(effect.Duration ?? 5f);
                    w.Put(true);
                });
                return;
            }
            var skill = LocalSkill;
            if (effect.Type != EffectType.Other || string.IsNullOrEmpty(effect.Name) || FindCreator(effect.Name, skill).creator == null) return;
            var level = LocalLevel ?? new int[0];
            SendIntent(id, Op.Creator, w =>
            {
                w.Put(effect.Name);
                w.Put(skill != null ? skill.name : "");
                w.Put((short)Mathf.Clamp(effect.Stacks, 1, short.MaxValue));
                w.Put(effect.Duration ?? -1f);
                w.Put((byte)Mathf.Min(level.Length, 8));
                for (int i = 0; i < level.Length && i < 8; i++) w.Put(level[i]);
            });
            forwardedNames.Add((id, effect.Name));
        }

        public void OnPuppetRemoveEffect(EffectsHandler h, string name)
        {
            if (string.IsNullOrEmpty(name) || !PuppetHere(h, out var e, out int id)) return;
            // Removals of effects we put there go too, from wherever (a skill's end, its area left).
            if (!forwardedNames.Remove((id, name)) && !LocalSource) return;
            SendIntent(id, Op.RemoveName, w => w.Put(name));
        }

        public void OnPuppetRemoveEffect(EffectsHandler h, EffectType type)
        {
            if (!LocalSource || type == EffectType.Other || !PuppetHere(h, out var e, out int id)) return;
            SendIntent(id, Op.RemoveType, w => w.Put((ushort)type));
        }

        private void SendIntent(int id, Op op, System.Action<NetDataWriter> body)
        {
            session.Send(Msg.EnemyEffectIntent, w =>
            {
                w.Put(SceneHash);
                w.Put(id);
                w.Put((byte)op);
                body(w);
            });
            IntentsSent++;
        }

        private void OnIntent(byte from, NetDataReader r)
        {
            var es = EnemySync.Instance;
            if (es == null || !EnemySync.IsAuthority) return;
            if (r.GetInt() != SceneHash) return;
            int id = r.GetInt();
            var op = (Op)r.GetByte();
            DamageEffect de = default;
            string name = null, skillName = null;
            int stacks = 1;
            float duration = -1f;
            int[] level = null;
            EffectType removeType = EffectType.Neutral;
            switch (op)
            {
                case Op.Effect:
                    de = new DamageEffect
                    {
                        Effect = (EffectType)r.GetInt(), OtherEffect = r.GetString(64), Chance = r.GetFloat(),
                        DamagePerSec = r.GetInt(), Duration = r.GetFloat(), HideUI = r.GetBool(),
                    };
                    break;
                case Op.Creator:
                    name = r.GetString(128);
                    skillName = r.GetString(128);
                    stacks = r.GetShort();
                    duration = r.GetFloat();
                    int n = NetGuard.Count(r.GetByte(), r, 4);
                    level = new int[n];
                    for (int i = 0; i < n; i++) level[i] = r.GetInt();
                    break;
                case Op.RemoveName: name = r.GetString(128); break;
                case Op.RemoveType: removeType = (EffectType)r.GetUShort(); break;
                default: return;
            }
            var e = es.ById(id);
            var h = e != null && e.isActiveAndEnabled && !e.IsDead ? e.EffectsHandler : null;
            if (h == null) return;
            Applying = true;
            IntentFrom = from;
            es.NoteHitBy(e, from);   // its ticks and a death by it are that player's (death notices, kill credit)
            try
            {
                switch (op)
                {
                    case Op.Effect:
                        h.HandleEffect(de);
                        break;
                    case Op.Creator:
                    {
                        var (creator, skill) = FindCreator(name, FindSkill(skillName));
                        if (creator == null) { Plugin.Log.LogWarning($"[enemyfx] No effect '{name}' ({skillName}) here"); return; }
                        var effect = creator.CreateEffect(new EffectCreationContext
                        {
                            Level = level != null && level.Length > 0 ? level : new[] { 1 },
                            Caster = Game.LocalPlayer,   // the stand-in for the other player, as for their hits
                            CreatedFromSkill = skill,
                        });
                        if (duration >= 0f) effect.Duration = duration;
                        if (stacks > 1) effect.Stacks = stacks;
                        h.AddEffect(effect);
                        break;
                    }
                    case Op.RemoveName:
                        if (h.HasEffect(name)) h.RemoveEffect(name);
                        break;
                    case Op.RemoveType:
                        h.RemoveEffect(removeType);
                        break;
                }
                IntentsApplied++;
                Plugin.Log.LogInfo($"[enemyfx] #{from} {op} {(op == Op.Effect ? de.Effect.ToString() : op == Op.RemoveType ? removeType.ToString() : name)} on {e.name}");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemyfx] Effect from #{from} on {e.name}: {ex.Message}"); }
            finally
            {
                Applying = false;
                IntentFrom = 0;
            }
        }

        private static Dictionary<string, (EffectCreator creator, Skill skill)> creators;
        private static float creatorsScannedAt = -100f;

        // The effect creators our character's things use, by the name their effects carry: the skill's own first, then
        // any skill's named effects, attacks' custom effects, effect areas.
        private static (EffectCreator creator, Skill skill) FindCreator(string name, Skill from = null)
        {
            if (string.IsNullOrEmpty(name)) return (null, null);
            var own = from?.effects?.FirstOrDefault(x => x?.Effect != null && x.Effect.Name == name)?.Effect;
            if (own != null) return (own, from);
            if (creators != null && creators.TryGetValue(name, out var found)) return found;
            if (creators != null && Time.realtimeSinceStartup - creatorsScannedAt < 10f) return (null, null);
            creatorsScannedAt = Time.realtimeSinceStartup;
            creators = new Dictionary<string, (EffectCreator, Skill)>();
            void Add(EffectCreator c, Skill s)
            {
                if (c != null && !string.IsNullOrEmpty(c.Name) && !creators.ContainsKey(c.Name)) creators[c.Name] = (c, s);
            }
            foreach (var s in Resources.FindObjectsOfTypeAll<Skill>())
                if (s != null && s.effects != null)
                    foreach (var ne in s.effects) Add(ne?.Effect, s);
            foreach (var a in Resources.FindObjectsOfTypeAll<Attack>())
                if (a != null && a.customEffects != null)
                    foreach (var c in a.customEffects) Add(c, null);
            foreach (var area in Resources.FindObjectsOfTypeAll<EffectArea>())
                if (area != null && area.toAddEffects != null)
                    foreach (var c in area.toAddEffects) Add(c, null);
            return creators.TryGetValue(name, out found) ? found : (null, null);
        }

        private static Dictionary<string, Skill> skills;

        private static Skill FindSkill(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (skills == null || !skills.ContainsKey(name))
            {
                skills = new Dictionary<string, Skill>();
                foreach (var x in Resources.FindObjectsOfTypeAll<Skill>())
                    if (x != null && !skills.ContainsKey(x.name)) skills[x.name] = x;
            }
            return skills.TryGetValue(name, out var found) ? found : null;
        }

        // ================= hits: the target's side resolves on the runner =================

        private struct PendingHit { public Enemy Target; public int Raw; public bool Crit; public int[] Level; public int Frame; }
        private static PendingHit pending;

        // Guest side, in CombatSystem.GetDamageData for our character's hit on a puppet: the crit is decided here (our
        // chance) and kept, so our copy and the runner agree on it; the rest of the target's side is the runner's.
        public static void CaptureHit(Enemy target, ref DamageData d)
        {
            bool crit = Random.value * 100f < d.BaseCriticalChance;
            d.BaseCriticalChance = crit ? 1000f : -1000f;
            pending = new PendingHit { Target = target, Raw = d.Amount, Crit = crit, Level = d.Level, Frame = Time.frameCount };
        }

        // EnemySync.SendHit: false when the hit didn't come through our CombatSystem (it then goes as it is).
        public bool SendRawHit(Enemy e, int id, DamageData resolved)
        {
            var p = pending;
            if (p.Target != e || p.Frame != Time.frameCount) return false;
            pending = default;
            resolved.Level = p.Level;   // (the resolved hit doesn't carry it; the attack's custom effects scale with it)
            session.Send(Msg.EnemyHitRaw, w =>
            {
                w.Put(SceneHash);
                w.Put(id);
                w.Put((byte)(p.Crit ? 1 : 0));
                w.Put(p.Raw);
                DamageCodec.Write(w, resolved);   // the effects we rolled, the attack, its level
            });
            RawHitsSent++;
            return true;
        }

        private void OnHitRaw(byte from, NetDataReader r)
        {
            var es = EnemySync.Instance;
            if (es == null || !EnemySync.IsAuthority) return;
            int sceneHash = r.GetInt();
            int id = r.GetInt();
            bool crit = (r.GetByte() & 1) != 0;
            int raw = r.GetInt();
            var d = DamageCodec.Read(r);
            if (sceneHash != SceneHash) return;
            var e = es.ById(id);
            var me = Game.LocalPlayer;
            if (e == null || me == null || !e.isActiveAndEnabled || e.IsDead)
            {
                if (es.HitsIgnored++ < 10) Plugin.Log.LogInfo($"[enemy] Raw hit from #{from} on #{id} ignored: {(e == null ? "no such enemy here" : e.IsDead ? e.name + " is dead here" : e.name + " is inactive here")}");
                return;
            }
            // The game's own target side, against the real enemy: defence, damage-received modifiers (the aura, the
            // absorb), block, parry, the attack's knockback and custom effects. Effect chances were rolled by the hitter.
            var input = new DamageData
            {
                Aggressor = me,
                Target = e,
                Amount = raw,
                Attack = d.Attack,
                Level = d.Level,
                HitType = d.HitType,
                BaseCriticalChance = crit ? 1000f : -1000f,
                BaseEffectsChance = -1000f,
                Effects = new List<DamageEffect>(),
                CreatedEffects = new List<Effect>(),
                Processors = new List<DamageDataProcessor>(),
            };
            var prevSource = HitSource;
            HitSource = es.HitSourceOf(from);
            SkillFx.InGuestHit = true;
            try
            {
                CombatSystem.GetDamageData(input, out var hit, out _);
                hit.Effects = d.Effects ?? new List<DamageEffect>();
                if (d.Attack == null) { hit.KnockBackForce = d.KnockBackForce; hit.NoStagger = d.NoStagger; }
                hit.NoSound = d.NoSound;
                hit.Level = d.Level;
                float before = e.currentHealth;
                es.ApplyRemoteHit(from, e, hit, false);
                RawDealt.Add(Mathf.RoundToInt(before - e.currentHealth));
                RawHitsApplied++;
            }
            finally
            {
                HitSource = prevSource;
                SkillFx.InGuestHit = false;
            }
        }

        // ================= deaths: who caused it =================

        // Runner: Enemy.ApplyDamage is running on one of ours, with a player's hit or not. Returns what was there before
        // (-1 nothing, 0/1), for the patch to put back when the call ends (a nested call on the same enemy).
        public sbyte DamageStarted(Enemy e, bool byPlayer)
        {
            sbyte before = damageByPlayer.TryGetValue(e, out bool b) ? (sbyte)(b ? 1 : 0) : (sbyte)-1;
            damageByPlayer[e] = byPlayer;
            return before;
        }

        public void DamageEnded(Enemy e, sbyte before)
        {
            if (before < 0) damageByPlayer.Remove(e);
            else damageByPlayer[e] = before == 1;
        }

        // EnemySync.OnKilled: a death inside a player's hit gives XP (Enemy.ApplyDamage), anything else doesn't: a
        // self-destruct (SetEnemyKilledAction), a burn's tick, another enemy's attack, a trap.
        public byte CauseOf(Enemy e) => damageByPlayer.TryGetValue(e, out bool b) && b ? CausePlayer : CauseNone;

        // ================= group attacks =================

        // Runner: a group skill (CastGroupSkillAction) shares one EnemyGroupAttack between its casters: one build-up and
        // one projectile, from the middle of the group. Each caster's EnemyAttackStart says which group and who's in it.
        public void WriteAttackGroup(NetDataWriter w, Enemy e)
        {
            var g = e.attackGroup;
            var es = EnemySync.Instance;
            if (g == null || g.InvolvedEnemies == null || es == null)
            {
                w.Put((ushort)0);
                w.Put((byte)0);
                return;
            }
            if (!groupTokens.TryGetValue(g, out ushort token))
            {
                if (groupTokens.Count > 64) groupTokens.Clear();
                token = ++nextGroupToken;
                if (token == 0) token = ++nextGroupToken;
                groupTokens[g] = token;
            }
            var ids = g.InvolvedEnemies.Select(x => es.IdOf(x)).Where(x => x != 0).Take(16).ToList();
            w.Put(token);
            w.Put((byte)ids.Count);
            foreach (int id in ids) w.Put(id);
        }

        // Everyone else: the same group for the replays, so the projectile flies once, from the group's centre.
        public EnemyGroupAttack ReadAttackGroup(NetDataReader r, byte from, Enemy e, Attack attack)
        {
            if (r.AvailableBytes < 3) return null;   // a runner without groups on the wire
            ushort token = r.GetUShort();
            int n = NetGuard.Count(r.GetByte(), r, 4);
            var ids = new int[n];
            for (int i = 0; i < n; i++) ids[i] = r.GetInt();
            if (token == 0 || e == null || attack == null) return null;
            float now = Time.realtimeSinceStartup;
            foreach (var k in replayGroups.Where(kv => now - kv.Value.At > 15f).Select(kv => kv.Key).ToList()) replayGroups.Remove(k);
            if (!replayGroups.TryGetValue((from, token), out var rg) || rg.Group.Skill != attack)
                replayGroups[(from, token)] = rg = new ReplayGroup { Group = new EnemyGroupAttack { Skill = attack, InvolvedEnemies = new HashSet<Enemy>(), HasStarted = true } };
            rg.At = now;
            var es = EnemySync.Instance;
            foreach (int id in ids)
            {
                var x = es?.ById(id);
                if (x != null && x.isActiveAndEnabled && !x.IsDead) rg.Group.InvolvedEnemies.Add(x);
            }
            rg.Group.InvolvedEnemies.Add(e);
            GroupReplays++;
            return rg.Group;
        }

        // ================= handoff =================

        // Old runner: the fight's effects and boss stages go with EnemySync's handoff (a boss's graph starts over at
        // Stage1 on a copy that only showed it).
        public void SendHandoff(byte to)
        {
            var es = EnemySync.Instance;
            if (es == null) return;
            var entries = new List<(int id, int stage, List<NetEffect> fx)>();
            foreach (var e in EnemiesManager.AllEnemies)
            {
                if (e == null || !e.isActiveAndEnabled || e.IsDead) continue;
                int id = es.IdOf(e);
                if (id == 0) continue;
                int stage = StageOf(e);
                var fx = Effects(e);
                fx.AddRange(ProtectorHandoff(e));
                if (fx.Count == 0 && stage <= 0) continue;
                entries.Add((id, stage, fx));
                if (entries.Count == 255) break;
            }
            if (entries.Count == 0) return;
            session.Send(Msg.EnemyFxHandoff, w =>
            {
                w.Put(to);
                w.Put(SceneHash);
                w.Put((byte)entries.Count);
                foreach (var (id, stage, fx) in entries)
                {
                    w.Put(id);
                    w.Put((sbyte)Mathf.Clamp(stage, -1, 100));
                    WriteEffects(w, fx);
                }
            });
            Plugin.Log.LogInfo($"[enemyfx] Handed over {entries.Count} enemies' effects and stages to #{to}");
        }

        private void OnHandoff(byte from, NetDataReader r)
        {
            if (r.GetByte() != session.LocalId) return;
            if (r.GetInt() != SceneHash) return;
            var es = EnemySync.Instance;
            int n = NetGuard.Count(r.GetByte(), r, 6);
            for (int i = 0; i < n; i++)
            {
                int id = r.GetInt();
                int stage = r.GetSByte();
                var fx = ReadEffects(r);
                var e = es?.ById(id);
                if (e == null || !e.isActiveAndEnabled || e.IsDead || !EnemySync.IsAuthority) continue;
                Applying = true;
                try
                {
                    foreach (var x in fx) if (ApplyReal(e, x)) HandoffEffects++;
                    if (stage > 0 && e.behaviorGraphAgent != null && e.behaviorGraphAgent.enabled && StageOf(e) >= 0)
                    {
                        e.behaviorGraphAgent.SetVariableValue("Stage", (BossStage)stage);
                        HandoffStages++;
                        Plugin.Log.LogInfo($"[enemyfx] {e.name} goes on at {(BossStage)stage}");
                    }
                }
                catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemyfx] Handoff of {e.name}: {ex.Message}"); }
                finally { Applying = false; }
            }
        }

        // An effect from the old runner's list, for real on our copy, for the time it had left.
        private static bool ApplyReal(Enemy e, NetEffect x)
        {
            var h = e.EffectsHandler;
            if (h == null) return false;
            switch (x.Type)
            {
                case EffectType.Burn: case EffectType.Bleed: case EffectType.Freeeze:
                case EffectType.Poison: case EffectType.Kymia: case EffectType.Shock:
                    return h.HandleEffect(new DamageEffect { Effect = x.Type, DamagePerSec = x.Dps, Duration = x.Remaining >= 0f ? Mathf.Max(0.1f, x.Remaining) : 5f, HideUI = true });
                case EffectType.Shield:
                    return h.HandleEffect(new DamageEffect { Effect = x.Type, DamagePerSec = Mathf.Max(1, x.Stacks), HideUI = true });
                case EffectType.Other:
                {
                    if (IsProtectorEntry(x.Name)) return AdoptProtector(e, x.Name);
                    var (creator, skill) = FindCreator(x.Name);
                    if (creator == null) return false;
                    var effect = creator.CreateEffect(new EffectCreationContext { Level = new[] { 1 }, Caster = Game.LocalPlayer, CreatedFromSkill = skill });
                    if (x.Remaining >= 0f) effect.Duration = Mathf.Max(0.1f, x.Remaining);
                    if (x.Stacks > 1) effect.Stacks = x.Stacks;
                    return h.AddEffect(effect);
                }
                default: return false;   // speed buffs carry a value we don't have; webs are for players
            }
        }

        // A boss's stage: the "Stage" variable its graph keeps (SpiderMatriarch, Heartwood), -1 without one.
        public static int StageOf(Enemy e)
        {
            try
            {
                var agent = e != null ? e.behaviorGraphAgent : null;
                if (agent != null && agent.GetVariable("Stage", out BlackboardVariable<BossStage> v) && v != null) return (int)v.Value;
            }
            catch (System.Exception) { }
            return -1;
        }

        // ================= sight =================

        // Runner: an enemy chasing another player's puppet lost sight of it. EnemyGroup.SetInSight remembers where the
        // tagged Player (our character) is as the last-seen spot; it was the puppet.
        public void FixLastSeen(EnemyGroup group, Enemy e)
        {
            try
            {
                var agent = e != null ? e.behaviorGraphAgent : null;
                if (group == null || agent == null || !agent.GetVariable("Player", out BlackboardVariable<GameObject> v) || v == null || v.Value == null) return;
                var target = v.Value;
                if (Game.LocalPlayer != null && target == Game.LocalPlayer.gameObject) return;
                if (!presence.Remotes.Values.Any(r => r.Puppet == target)) return;
                group.LastPlayerSeenPosition = target.transform.position;
                SightFixes++;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemyfx] Last seen of {e?.name}: {ex.Message}"); }
        }
    }
}
