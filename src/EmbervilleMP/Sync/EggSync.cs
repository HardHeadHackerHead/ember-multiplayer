using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync
{
    // The Spider Matriarch's eggs, breakable by anyone, hatching into spiderlings (which then travel as EnemySpawn).
    // Two kinds, one id space:
    //   - placed in her arena: the boss's graph finds the closest living one by tag and hatches it
    //     (DestroyDamageableAction: Destroy(SpidersInEachEgg)) or eats it. Ids from their scene path (NetIds), the same
    //     on every copy.
    //   - laid by the boss (SpiderMatriarch.SpawnEgg -> SetAlive): ids from the runner (top bit set), sent as EggSpawn.
    //   others -> runner: EggHit (their hit on an egg; the runner applies it)
    //   runner -> all   : EggBroken (broken: 0, hatched: n spiderlings), EggStates (the placed eggs: active, idle /
    //                     broken / hatched; on change and every few seconds, so late arrivals and a new runner have the
    //                     runner's eggs, and anything missed is put right)
    internal class EggSync
    {
        public static EggSync Instance { get; private set; }
        public int Spawned, SpawnApplied, Hits, HitsApplied, Broken, BrokenApplied, StatesSent, StatesApplied, StateFixes;

        private readonly Session session;
        private readonly Presence presence;
        private readonly Dictionary<int, SpiderEgg> byId = new Dictionary<int, SpiderEgg>();
        private readonly Dictionary<SpiderEgg, int> idOf = new Dictionary<SpiderEgg, int>();
        private readonly List<(int id, SpiderEgg egg)> placed = new List<(int, SpiderEgg)>();
        private uint nextId = 1;
        private int registryScene = -1;
        private byte[] lastStates;
        private float nextPoll, nextFull;
        private int unknownHits;

        public const float PollInterval = 0.25f, FullInterval = 3f;

        public EggSync(Session session, Presence presence)
        {
            Instance = this;
            this.session = session;
            this.presence = presence;
            session.On(Msg.EggSpawn, OnSpawn);
            session.On(Msg.EggHit, OnHit);
            session.On(Msg.EggBroken, OnBroken);
            session.On(Msg.EggStates, OnStates);
        }

        private static string PrefabName(GameObject go)
        {
            string n = go.name;
            return n.EndsWith("(Clone)") ? n.Substring(0, n.Length - 7).Trim() : n;
        }

        // ---------------- registry ----------------

        private void EnsureRegistry()
        {
            var active = SceneManager.GetActiveScene();
            if (active.handle == registryScene) return;
            registryScene = active.handle;
            byId.Clear();
            idOf.Clear();
            placed.Clear();
            lastStates = null;
            foreach (var egg in Object.FindObjectsByType<SpiderEgg>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (egg == null || egg.gameObject.scene != active || NetIds.IsRuntimeSpawned(egg.transform)) continue;
                int id = NetIds.Hash(NetIds.ScenePath(egg.transform));
                if (byId.TryGetValue(id, out var clash))
                {
                    Plugin.Log.LogWarning($"[egg] Id clash {id}: {egg.name} vs {clash.name}");
                    continue;
                }
                byId[id] = egg;
                idOf[egg] = id;
                placed.Add((id, egg));
            }
            if (placed.Count > 0) Plugin.Log.LogInfo($"[egg] {placed.Count} eggs placed in {active.name}");
        }

        public int PlacedCount { get { if (Game.InGame) EnsureRegistry(); return placed.Count; } }
        public IEnumerable<(int id, SpiderEgg egg)> Placed { get { if (Game.InGame) EnsureRegistry(); return placed; } }
        public int IdOf(SpiderEgg egg) => egg != null && idOf.TryGetValue(egg, out int id) ? id : 0;
        public SpiderEgg ById(int id) => byId.TryGetValue(id, out var e) ? e : null;
        public bool IsPlaced(SpiderEgg egg) => egg != null && idOf.TryGetValue(egg, out int id) && id > 0;

        // ---------------- per frame (runner: the placed eggs' states) ----------------

        public void Update()
        {
            if (!session.Ready || !Game.InScene) return;
            EnsureRegistry();
            if (!EnemySync.IsAuthority || placed.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + PollInterval;
            string scene = Game.SceneName;
            if (!presence.Remotes.Values.Any(r => r.Latest != null && r.Latest.Scene == scene)) { lastStates = null; return; }
            int count = Mathf.Min(placed.Count, 255);
            var states = new byte[count];
            for (int i = 0; i < count; i++) states[i] = Flags(placed[i].egg);
            if (lastStates != null && now < nextFull && states.SequenceEqual(lastStates)) return;
            lastStates = states;
            nextFull = now + FullInterval;
            int sceneHash = NetIds.SceneHash(scene);
            session.Send(Msg.EggStates, w =>
            {
                w.Put(sceneHash);
                w.Put((byte)count);
                for (int i = 0; i < count; i++) { w.Put(placed[i].id); w.Put(states[i]); }
            });
            StatesSent++;
        }

        // Our copy rewound its scene (a party wipe: PartyWipe): the runner sends its eggs again at once.
        public void OnRewound()
        {
            lastStates = null;
            nextPoll = 0f;
        }

        // bit 0: active; bits 1-2: the egg's state (inactive, idle, destroyed, hatched)
        private static byte Flags(SpiderEgg egg) =>
            egg == null ? (byte)0 : (byte)((egg.gameObject.activeSelf ? 1 : 0) | (((int)egg.state & 3) << 1));

        private static bool Gone(SpiderEgg.EggState s) => s == SpiderEgg.EggState.Destroyed || s == SpiderEgg.EggState.Hatched;

        private void OnStates(byte from, NetDataReader r)
        {
            if (EnemySync.IsAuthority || from != EnemySync.Instance?.Authority || !Game.InGame) return;
            EnsureRegistry();
            if (r.GetInt() != NetIds.SceneHash(Game.SceneName)) return;
            int n = NetGuard.Count(r.GetByte(), r, 5);
            for (int i = 0; i < n; i++)
            {
                int id = r.GetInt();
                byte flags = r.GetByte();
                var egg = ById(id);
                if (egg != null && id > 0) Apply(egg, flags);
            }
            StatesApplied++;
        }

        // The runner's egg as it is: what SpiderEgg.ResetToLastState does, without its events (a break or a hatch plays
        // through EggBroken; this only puts right what we missed or never saw).
        private void Apply(SpiderEgg egg, byte flags)
        {
            bool active = (flags & 1) != 0;
            var state = (SpiderEgg.EggState)((flags >> 1) & 3);
            bool fix = Gone(state) != Gone(egg.state) || (state == SpiderEgg.EggState.Inactive) != (egg.state == SpiderEgg.EggState.Inactive);
            if (fix)
            {
                if (StateFixes++ < 20) Plugin.Log.LogInfo($"[egg] {egg.name}: {egg.state} here, {state} on the runner");
                egg.state = state;
                if (Gone(state)) egg.health = 0f;
                else if (state == SpiderEgg.EggState.Idle)
                {
                    egg.health = egg.initialHealth;
                    egg.Restore();
                }
                if (egg.isActiveAndEnabled && egg.animator != null)
                    egg.animator.SetTrigger(state == SpiderEgg.EggState.Destroyed ? "SetDestructed" : state == SpiderEgg.EggState.Hatched ? "SetHatched" : "Idle");
            }
            if (egg.gameObject.activeSelf != active) egg.gameObject.SetActive(active);
        }

        // ---------------- hits, breaks, laid eggs ----------------

        // runner
        public void OnLaid(SpiderEgg egg)
        {
            if (Game.InGame) EnsureRegistry();
            if (IsPlaced(egg)) return;   // a placed egg keeps its id (its state goes out with EggStates)
            if (idOf.TryGetValue(egg, out int old)) byId.Remove(old);
            // top bit: laid at runtime; next 7 bits: who laid it (ids stay unique when the runner changes)
            int id = unchecked((int)(0x80000000u | ((uint)(session.LocalId & 0x7F) << 24) | (nextId++ & 0xFFFFFF)));
            byId[id] = egg;
            idOf[egg] = id;
            var p = egg.transform.position;
            session.Send(Msg.EggSpawn, w => { w.Put(id); w.Put(PrefabName(egg.gameObject)); w.Put(p.x); w.Put(p.y); });
            Spawned++;
        }

        public void OnDestroyed(SpiderEgg egg, int index)
        {
            if (Game.InGame) EnsureRegistry();
            if (!idOf.TryGetValue(egg, out int id)) return;
            session.Send(Msg.EggBroken, w => { w.Put(id); w.Put(index); });
            Broken++;
        }

        private void OnHit(byte from, NetDataReader r)
        {
            if (!EnemySync.IsAuthority || !Game.InGame) return;
            EnsureRegistry();
            int id = r.GetInt();
            int amount = r.GetInt();
            if (!byId.TryGetValue(id, out var egg) || egg == null || !egg.isActiveAndEnabled) return;
            egg.ApplyDamage(new DamageData { Aggressor = Game.LocalPlayer, Target = egg, Amount = amount, Effects = new List<DamageEffect>() });
            HitsApplied++;
        }

        // non-runner
        public void SendHit(SpiderEgg egg, DamageData d)
        {
            if (Game.InGame) EnsureRegistry();
            if (!idOf.TryGetValue(egg, out int id))
            {
                if (unknownHits++ < 5) Plugin.Log.LogInfo($"[egg] Hit on {egg.name}, which has no network id");
                return;
            }
            session.Send(Msg.EggHit, w => { w.Put(id); w.Put(d.Amount); });
            Bus<UIDamageEvent>.Invoke(new UIDamageEvent(d.Amount, "", egg.transform.position, DamageResult.Success));
            Hits++;
        }

        private void OnSpawn(byte from, NetDataReader r)
        {
            if (EnemySync.IsAuthority || from != EnemySync.Instance?.Authority) return;
            int id = r.GetInt();
            string prefabName = r.GetString();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
            if (id > 0) return;   // placed eggs aren't spawned
            var prefab = PoolManager.pools.Keys.FirstOrDefault(k => k != null && k.name == prefabName);
            if (prefab == null) { Plugin.Log.LogWarning($"[egg] No pool for {prefabName}"); return; }
            var go = PoolManager.Get(prefab);
            var egg = go != null ? go.GetComponent<SpiderEgg>() : null;
            if (egg == null) return;
            if (idOf.TryGetValue(egg, out int old)) byId.Remove(old);
            byId[id] = egg;
            idOf[egg] = id;
            go.transform.position = pos;
            egg.SetAlive();
            SpawnApplied++;
        }

        private void OnBroken(byte from, NetDataReader r)
        {
            if (EnemySync.IsAuthority || from != EnemySync.Instance?.Authority) return;
            if (Game.InGame) EnsureRegistry();
            int id = r.GetInt();
            int index = r.GetInt();
            if (!byId.TryGetValue(id, out var egg) || egg == null) return;
            EnemySync.Passthrough = true;
            try { egg.Destroy(Mathf.Clamp(index, 0, 16)); }
            finally { EnemySync.Passthrough = false; }
            BrokenApplied++;
        }
    }

    [HarmonyPatch(typeof(SpiderEgg), nameof(SpiderEgg.SetAlive))]
    internal static class EggSetAlivePatch
    {
        private static void Postfix(SpiderEgg __instance)
        {
            if (EnemySync.IsAuthority) EggSync.Instance?.OnLaid(__instance);
        }
    }

    [HarmonyPatch(typeof(SpiderEgg), nameof(SpiderEgg.Destroy))]
    internal static class EggDestroyPatch
    {
        // Non-runners: eggs only break or hatch when the runner's do.
        private static bool Prefix() => !EnemySync.GuestPuppets || EnemySync.Passthrough;

        private static void Postfix(SpiderEgg __instance, int index)
        {
            if (EnemySync.IsAuthority) EggSync.Instance?.OnDestroyed(__instance, index);
        }
    }

    [HarmonyPatch(typeof(SpiderEgg), nameof(SpiderEgg.ApplyDamage))]
    internal static class EggApplyDamagePatch
    {
        private static bool Prefix(SpiderEgg __instance, DamageData damageData)
        {
            if (!EnemySync.GuestPuppets || EnemySync.Passthrough) return true;
            if (damageData.Aggressor is PlayerMaster && __instance.health > 0f) EggSync.Instance?.SendHit(__instance, damageData);
            return false;
        }
    }
}
