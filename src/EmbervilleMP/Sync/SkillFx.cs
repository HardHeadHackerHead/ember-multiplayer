using System;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace EmbervilleMP.Sync
{
    // Marks an attacker whose hits are only for show: CombatResolvePatch drops its damage, SkillFxPatches skip the
    // effects and heals that don't go through CombatSystem.
    internal interface IVisualOnly { }

    // Other players' skills and spells. What a player's attacks and skills spawn (ground areas, slams, shockwaves,
    // beams, explosions, objects spawned by skill graphs) is replayed on everyone else's screen as a copy owned by
    // a PuppetFxOwner: it plays its own animations, particles and sounds, but hits, heals and buffs nobody. The
    // caster's machine already did all that, and its hits reach the scene's runner through EnemySync.
    // Projectiles go through PlayerFx; animations and the extra-effects animator through the player state.
    internal class SkillFx
    {
        public enum Kind : byte { AttackObject = 0, GraphAttackObject = 1, GraphPooled = 2 }
        private enum Op : byte { End = 0, Release = 1, Move = 2 }
        private const byte FlagReleased = 1, FlagRotate = 2;

        public static SkillFx Instance { get; private set; }
        public int SpawnsSent, UpdatesSent, Shown, Updates, Missing, Skipped, Errors;
        public readonly Dictionary<string, int> SentByKey = new Dictionary<string, int>();
        public readonly Dictionary<string, int> ShownByKey = new Dictionary<string, int>();
        public string LastShownKey = "";
        public float LastShownDistance = -1f;   // from the caster's puppet
        public GameObject LastShownCopy;

        // Any machine: hits/effects/heals of copies that were stopped, and copies' damage that reached a target anyway.
        public static int Blocked, Leaks;
        // Scene runner: damage to enemies that came neither from another player's real hit nor from an effect tick.
        public static float EnemyDamageNotFromGuests;
        internal static bool InGuestHit;

        private class Tracked { public ushort Seq; public Vector3 Pos; public float Rot; }
        private readonly Dictionary<GameObject, Tracked> tracked = new Dictionary<GameObject, Tracked>();
        private class Copy { public byte From; public ushort Seq; public float At; }
        private readonly Dictionary<(byte, ushort), GameObject> copies = new Dictionary<(byte, ushort), GameObject>();
        private readonly Dictionary<GameObject, Copy> live = new Dictionary<GameObject, Copy>();
        private readonly HashSet<string> missingLogged = new HashSet<string>();
        private readonly Session session;
        private readonly Presence presence;
        private ushort nextSeq;
        private float nextTick;

        private static readonly Dictionary<byte, PuppetFxOwner> owners = new Dictionary<byte, PuppetFxOwner>();
        private static GameObject ticker;

        public SkillFx(Session session, Presence presence)
        {
            Instance = this;
            this.session = session;
            this.presence = presence;
            session.On(Msg.SkillFxSpawn, OnSpawn);
            session.On(Msg.SkillFxUpdate, OnUpdate);
            if (ticker == null)
            {
                ticker = new GameObject("MP_SkillFxTicker") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(ticker);
                ticker.AddComponent<SkillFxTicker>();
            }
        }

        // ---- the caster's side ----

        // The game spawned an effect object for our character. instance: the spawned object if still alive (then its
        // end, release and movement follow), pos/dir/rot: where and how the game placed it.
        public void OnLocalSpawn(Kind kind, string key, GameObject instance, Vector3 pos, Vector2 dir, float rot, int[] level, bool released, bool rotate)
        {
            if (!session.Ready || string.IsNullOrEmpty(key)) return;
            ushort seq = 0;
            if (instance != null && instance.activeInHierarchy)
            {
                // The pool handed out the same object again before we saw it end: end the old copy first.
                if (tracked.TryGetValue(instance, out var prev)) SendUpdate(Op.End, prev.Seq, null);
                if (++nextSeq == 0) nextSeq = 1;
                seq = nextSeq;
                tracked[instance] = new Tracked { Seq = seq, Pos = instance.transform.position, Rot = instance.transform.eulerAngles.z };
            }
            session.Send(Msg.SkillFxSpawn, w =>
            {
                w.Put((byte)kind);
                w.Put(key);
                w.Put(seq);
                w.Put(pos.x); w.Put(pos.y);
                w.Put(dir.x); w.Put(dir.y);
                w.Put(rot);
                w.Put((byte)((released ? FlagReleased : 0) | (rotate ? FlagRotate : 0)));
                int n = level == null ? 0 : Mathf.Min(level.Length, 8);
                w.Put((byte)n);
                for (int i = 0; i < n; i++) w.Put(level[i]);
            });
            SpawnsSent++;
            SentByKey[key] = SentByKey.TryGetValue(key, out int c) ? c + 1 : 1;
            if (Args.Test != null) Plugin.Log.LogInfo($"[skillfx] sent {kind} {key} seq={seq} at {pos}");
        }

        // A held attack (beam) of ours was let go.
        public void OnLocalRelease(GameObject instance)
        {
            if (instance != null && tracked.TryGetValue(instance, out var t)) SendUpdate(Op.Release, t.Seq, instance.transform);
        }

        private void SendUpdate(Op op, ushort seq, Transform t)
        {
            session.Send(Msg.SkillFxUpdate, w =>
            {
                w.Put((byte)op);
                w.Put(seq);
                if (op == Op.Move) { w.Put(t.position.x); w.Put(t.position.y); w.Put(t.eulerAngles.z); }
            });
            UpdatesSent++;
        }

        // ~10 Hz: our spawned objects that ended or moved (auras following us, objects moved by their graph), and
        // copies whose caster left (or that nobody will end: untracked and still on after 30 s).
        internal void Tick()
        {
            if (Session.Current != session || tracked.Count + live.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextTick) return;
            nextTick = now + 0.1f;
            foreach (var kv in live.ToList())
            {
                var go = kv.Key;
                var c = kv.Value;
                if (go == null || !go.activeSelf) { Forget(go, c); continue; }
                bool casterHere = presence.Remotes.TryGetValue(c.From, out var rp) && rp.Shown;
                if (!casterHere || (c.Seq == 0 && now - c.At > 30f))
                {
                    Forget(go, c);
                    go.SetActive(false);
                }
            }
            foreach (var kv in tracked.ToList())
            {
                var go = kv.Key;
                var t = kv.Value;
                if (go == null || !go.activeInHierarchy)
                {
                    tracked.Remove(go);
                    if (session.Ready) SendUpdate(Op.End, t.Seq, null);
                    continue;
                }
                var p = go.transform.position;
                float rz = go.transform.eulerAngles.z;
                if ((p - t.Pos).sqrMagnitude > 0.0025f || Mathf.Abs(Mathf.DeltaAngle(rz, t.Rot)) > 2f)
                {
                    t.Pos = p;
                    t.Rot = rz;
                    if (session.Ready) SendUpdate(Op.Move, t.Seq, go.transform);
                }
            }
        }

        // Pool helpers for the patches: which instances of a prefab are active, and which one the game just took.
        internal static HashSet<GameObject> ActiveInstances(GameObject prefab)
        {
            var set = new HashSet<GameObject>();
            var list = prefab != null ? PoolManager.GetPoolList(prefab) : null;
            if (list != null)
                foreach (var x in list)
                    if (x != null && x.activeSelf) set.Add(x);
            return set;
        }

        internal static GameObject NewInstance(GameObject prefab, HashSet<GameObject> before)
        {
            var list = prefab != null ? PoolManager.GetPoolList(prefab) : null;
            if (list == null) return null;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] != null && list[i].activeSelf && (before == null || !before.Contains(list[i]))) return list[i];
            return null;
        }

        // ---- everyone else ----

        private void OnSpawn(byte from, NetDataReader r)
        {
            var kind = (Kind)r.GetByte();
            string key = r.GetString();
            ushort seq = r.GetUShort();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
            var dir = new Vector2(r.GetFloat(), r.GetFloat());
            float rot = r.GetFloat();
            byte flags = r.GetByte();
            int n = r.GetByte();
            int[] level = n > 0 ? new int[n] : null;
            for (int i = 0; i < n; i++) level[i] = r.GetInt();

            if (!presence.Remotes.TryGetValue(from, out var caster) || !caster.Shown) { Skipped++; return; }
            Attack attack = null;
            GameObject prefab;
            if (kind == Kind.AttackObject)
            {
                attack = DamageCodec.FindAttackByName(key);
                prefab = attack != null ? attack.attackSpawner : null;
            }
            else prefab = FindPrefab(key);
            if (prefab == null)
            {
                Missing++;
                if (missingLogged.Add(key)) Plugin.Log.LogWarning($"[skillfx] can't show {kind} '{key}': not loaded here");
                return;
            }
            try
            {
                var go = Show(kind, prefab, attack, caster, pos, dir, rot, flags, level);
                if (go == null) return;
                if (live.TryGetValue(go, out var old)) Forget(go, old);   // a reused copy
                live[go] = new Copy { From = from, Seq = seq, At = Time.realtimeSinceStartup };
                if (seq != 0) copies[(from, seq)] = go;
                Shown++;
                ShownByKey[key] = ShownByKey.TryGetValue(key, out int c) ? c + 1 : 1;
                LastShownKey = key;
                LastShownCopy = go;
                LastShownDistance = Vector2.Distance(pos, caster.Puppet.transform.position);
                if (Args.Test != null) Plugin.Log.LogInfo($"[skillfx] showing {kind} {key} of {caster.Info} at {pos} ({LastShownDistance:F2} from the puppet)");
            }
            catch (Exception e)
            {
                Errors++;
                Plugin.Log.LogWarning($"[skillfx] showing {key} failed: {e.Message}");
            }
        }

        // Same order as the game's own spawning code for each kind, with our harmless owner.
        private static GameObject Show(Kind kind, GameObject prefab, Attack attack, RemotePlayer caster, Vector3 pos, Vector2 dir, float rot, byte flags, int[] level)
        {
            var go = FxCopies.Get(prefab, keepColliders: prefab.GetComponent<Projectile>() != null);
            if (go == null) return null;
            try { Setup(go, kind, attack, caster, pos, dir, rot, flags, level); }
            catch { go.SetActive(false); throw; }   // half set up: don't leave it on screen
            return go;
        }

        private static void Setup(GameObject go, Kind kind, Attack attack, RemotePlayer caster, Vector3 pos, Vector2 dir, float rot, byte flags, int[] level)
        {
            var owner = OwnerFor(caster);
            var all = go.GetComponents<IAttackObject>();
            foreach (var o in all) o.Owner = owner;   // the game sets only the first; no copy keeps a stale owner
            switch (kind)
            {
                case Kind.AttackObject:   // PlayerMaster.PerformAttackHit
                {
                    go.SetActive(true);
                    go.transform.position = pos;
                    var main = all.FirstOrDefault();
                    if (main == null) break;
                    if (main is IDirectionAttackSpawner ds) ds.SetDirection(dir);
                    if (main is IDamageDataAttack da) da.DamageData = VisualDamage(owner, attack, level);
                    if (main is ILeveledAttack la && level != null) la.Level = level;
                    if (attack == null || !attack.preventTrigger) main.Trigger();
                    if ((flags & FlagReleased) != 0 && main is IHoldAttack hold) hold.Release();
                    break;
                }
                case Kind.GraphAttackObject:   // InstantiateIAttackObjectAction
                    go.transform.position = pos;
                    if ((flags & FlagRotate) != 0) go.transform.rotation = Quaternion.Euler(0f, 0f, rot);
                    foreach (var o in all)
                    {
                        if (o is ILeveledAttack la && level != null) la.Level = level;
                        o.Trigger();
                    }
                    go.SetActive(true);
                    break;
                default:   // PoolObjectAction
                    go.SetActive(true);
                    go.transform.position = pos;
                    if ((flags & FlagRotate) != 0) go.transform.rotation = Quaternion.Euler(0f, 0f, rot);
                    break;
            }
        }

        private void OnUpdate(byte from, NetDataReader r)
        {
            var op = (Op)r.GetByte();
            ushort seq = r.GetUShort();
            Vector3 pos = default;
            float rot = 0f;
            if (op == Op.Move) { pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f); rot = r.GetFloat(); }
            if (!copies.TryGetValue((from, seq), out var go)) return;
            if (go == null || !live.TryGetValue(go, out var cur) || cur.From != from || cur.Seq != seq) { copies.Remove((from, seq)); return; }
            Updates++;
            try
            {
                switch (op)
                {
                    case Op.End:
                        Forget(go, cur);
                        if (go.activeSelf) go.SetActive(false);
                        break;
                    case Op.Release:
                        foreach (var h in go.GetComponents<IHoldAttack>()) h.Release();
                        break;
                    case Op.Move:
                        go.transform.position = new Vector3(pos.x, pos.y, go.transform.position.z);
                        go.transform.rotation = Quaternion.Euler(0f, 0f, rot);
                        break;
                }
            }
            catch (Exception e)
            {
                Errors++;
                Plugin.Log.LogWarning($"[skillfx] update {op} failed: {e.Message}");
            }
        }

        private void Forget(GameObject go, Copy c)
        {
            if (go is object) live.Remove(go);
            if (c != null && c.Seq != 0 && copies.TryGetValue((c.From, c.Seq), out var mapped) && ReferenceEquals(mapped, go)) copies.Remove((c.From, c.Seq));
        }

        // The harmless owner standing in for a remote player (a MonoBehaviour: some attack objects take its GameObject).
        public static PuppetFxOwner OwnerFor(RemotePlayer rp)
        {
            if (!owners.TryGetValue(rp.Info.Id, out var o) || o == null)
            {
                var go = new GameObject("MP_FxOwner_" + rp.Info.Id);   // scene object: goes with the scene
                o = go.AddComponent<PuppetFxOwner>();
                owners[rp.Info.Id] = o;
            }
            o.Puppet = rp.Puppet != null ? rp.Puppet.transform : null;
            return o;
        }

        public static DamageData VisualDamage(IDamageable owner, Attack attack, int[] level) => new DamageData
        {
            Aggressor = owner,
            Attack = attack,
            Level = level,
            Amount = 0,
            Effects = new List<DamageEffect>(),
            CreatedEffects = new List<Effect>(),
            Processors = new List<DamageDataProcessor>(),
        };

        // Prefabs spawned by skill graphs, by name (they're loaded with the skills that reference them).
        private static Dictionary<string, GameObject> prefabs;
        private static float prefabsAt = -100f;

        private static GameObject FindPrefab(string name)
        {
            if (prefabs == null || (!prefabs.ContainsKey(name) && Time.realtimeSinceStartup - prefabsAt > 5f))
            {
                prefabs = new Dictionary<string, GameObject>();
                prefabsAt = Time.realtimeSinceStartup;
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (go != null && !go.scene.IsValid() && go.transform.parent == null && !prefabs.ContainsKey(go.name))
                        prefabs[go.name] = go;
            }
            return prefabs.TryGetValue(name, out var p) ? p : null;
        }
    }

    // Stands in for another player as the owner of the copies of their effects.
    internal class PuppetFxOwner : MonoBehaviour, IDamageable, IVisualOnly
    {
        public Transform Puppet;
        public EffectsHandler EffectsHandler => null;
        public bool IsDead => false;
        public DamageableType DamageableType => DamageableType.Player;
        public void ApplyDamage(DamageData damageData) { }
        public CharacterStats GetCharacterStats() => null;
        public Transform GetTransform() => Puppet != null ? Puppet : transform;
    }

    internal class SkillFxTicker : MonoBehaviour
    {
        private void Update()
        {
            try { SkillFx.Instance?.Tick(); }
            catch (Exception e) { Plugin.Log.LogWarning("[skillfx] tick: " + e.Message); }
        }
    }

    // Our own instances of effect prefabs for the copies (never the game's pools: the game reuses its pooled objects
    // for our own character, and some fields it doesn't reset would carry a copy's owner over).
    internal static class FxCopies
    {
        public const int MaxPerPrefab = 12;
        public static int Created;
        private static readonly Dictionary<GameObject, List<GameObject>> pools = new Dictionary<GameObject, List<GameObject>>();
        private static GameObject holder;
        private static int scene = -1;

        public static GameObject Get(GameObject prefab, bool keepColliders)
        {
            if (prefab == null) return null;
            int handle = SceneManager.GetActiveScene().handle;
            if (handle != scene) { pools.Clear(); scene = handle; holder = null; }   // the old copies went with the scene
            if (!pools.TryGetValue(prefab, out var list)) pools[prefab] = list = new List<GameObject>();
            list.RemoveAll(x => x == null);
            foreach (var x in list)
                if (!x.activeSelf) return x;
            if (list.Count >= MaxPerPrefab)
            {
                var oldest = list[0];
                list.RemoveAt(0);
                list.Add(oldest);
                oldest.SetActive(false);
                return oldest;
            }
            // Built under an inactive holder: nothing on it wakes up before it's made harmless.
            if (holder == null) { holder = new GameObject("MP_FxCopies"); holder.SetActive(false); }
            var copy = Object.Instantiate(prefab, holder.transform);
            copy.name = prefab.name + " (MP copy)";
            Sanitize(copy, keepColliders);
            copy.SetActive(false);
            copy.transform.SetParent(null, true);
            list.Add(copy);
            Created++;
            return copy;
        }

        // Nothing may find, target, hit or heal a copy, and a copy touches nobody: no damageables, no AI, no navigation,
        // no tags, and (except flying projectiles, which need them to stop at walls) no colliders.
        private static void Sanitize(GameObject go, bool keepColliders)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.tag = "Untagged";
            for (int pass = 0; pass < 4; pass++)
            {
                var doomed = go.GetComponentsInChildren<Component>(true).Where(Unwanted).OrderBy(c => c is MonoBehaviour ? 0 : 1).ToList();
                if (doomed.Count == 0) break;
                foreach (var c in doomed)
                {
                    if (c == null) continue;
                    try { Object.DestroyImmediate(c); } catch (Exception) { /* something still needs it; next pass */ }
                }
            }
            if (!keepColliders)
                foreach (var col in go.GetComponentsInChildren<Collider2D>(true)) col.enabled = false;
        }

        private static bool Unwanted(Component c)
        {
            if (c == null || c is Transform) return false;
            if (c is IDamageable || c is IHealable) return true;
            string n = c.GetType().Name;
            return n == "BehaviorGraphAgent" || n == "GenericAttacker" || n == "NavMeshMover" || n == "NavMeshAgent";
        }
    }
}
