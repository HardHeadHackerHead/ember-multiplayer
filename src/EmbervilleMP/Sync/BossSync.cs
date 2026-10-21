using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using Unity.Behavior;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync
{
    // The Spider Matriarch, beyond what every enemy gets (EnemySync) and her eggs (EggSync):
    //   - Her stage (blackboard "Stage": she changes stage at 80% and 40% health, laying / hatching eggs and healing).
    //     Her OnEnable sets Stage1 on every copy, and a handoff only carries health and position, so a new runner (the
    //     host left the scene mid-fight) would play the fight from stage 1 again. The runner sends her stage
    //     (MatriarchState, on change and every few seconds); the others keep the latest, and when they take the fight
    //     over they write it into their own copy as its graph starts again.
    //   - The web. Her Start only listens to the local player's effects: when another player is webbed (their effects
    //     arrive through StatusFx), the runner's copy does what her HandleEffectApplied does for that player (the
    //     "player trapped" event, the spiders around rush to them at run speed with a speed buff) and, while they're
    //     webbed, she and those spiders chase that player's puppet (HostWorld.ForcedTarget), the runner's own webbed
    //     character too. The web itself reaches a non-runner as a forwarded hit (CombatResolvePatch: the Attack travels
    //     by name and its Trapped effect is applied on the webbed player's own character). On non-runners her handlers
    //     do nothing: their spiders are puppets.
    internal class BossSync
    {
        public static BossSync Instance { get; private set; }
        public int StatesSent, StatesReceived, StagesInherited, RemoteTraps, RemoteFrees;

        private readonly Session session;
        private readonly Presence presence;
        private readonly List<SpiderMatriarch> matriarchs = new List<SpiderMatriarch>();
        private int registryScene = -1;
        private readonly Dictionary<int, BossStage> lastSent = new Dictionary<int, BossStage>();
        private readonly Dictionary<int, BossStage> known = new Dictionary<int, BossStage>();      // non-runner: the runner's stage
        private readonly HashSet<int> inherit = new HashSet<int>();   // stages still to write when we take the fight over
        private readonly HashSet<byte> trapped = new HashSet<byte>();   // runner: other players webbed here
        private float nextPoll, nextFull, rewoundAt = -100f;
        private bool wasAuthority;

        public const float PollInterval = 0.25f, FullInterval = 3f, RushRange = 30f;

        public BossSync(Session session, Presence presence)
        {
            Instance = this;
            this.session = session;
            this.presence = presence;
            session.On(Msg.MatriarchState, OnState);
            HostWorld.ForcedTarget = ForcedTargetFor;
            session.Ended += () => { if (Instance == this) HostWorld.ForcedTarget = null; };
        }

        private void EnsureRegistry()
        {
            int scene = SceneManager.GetActiveScene().handle;
            if (scene == registryScene) return;
            registryScene = scene;
            matriarchs.Clear();
            lastSent.Clear();
            known.Clear();
            inherit.Clear();
            trapped.Clear();
            matriarchs.AddRange(Object.FindObjectsByType<SpiderMatriarch>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(m => m != null && m.enemy != null));
        }

        // Her copy is fighting here: awake, alive, her graph running.
        private static bool Fighting(SpiderMatriarch m) =>
            m != null && m.enemy != null && m.isActiveAndEnabled && m.enemy.isActiveAndEnabled && !m.enemy.IsDead &&
            m.behaviorGraphAgent != null && m.behaviorGraphAgent.enabled;

        public static bool TryGetStage(SpiderMatriarch m, out BossStage stage)
        {
            stage = BossStage.Stage1;
            try
            {
                if (m?.behaviorGraphAgent == null || !m.behaviorGraphAgent.GetVariable("Stage", out BlackboardVariable<BossStage> v) || v == null) return false;
                stage = v.Value;
                return true;
            }
            catch (System.Exception) { return false; }
        }

        public static void SetStage(SpiderMatriarch m, BossStage stage)
        {
            try { m.behaviorGraphAgent.SetVariableValue("Stage", stage); }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[boss] Setting the stage of {m.name}: {ex.Message}"); }
        }

        // Our copy rewound its scene (a party wipe: PartyWipe; or a death alone in it): her fight starts over, so
        // what we knew of it is stale (and a stage still on its way is ignored for a moment).
        public void OnRewound()
        {
            lastSent.Clear();
            known.Clear();
            inherit.Clear();
            trapped.Clear();
            rewoundAt = Time.realtimeSinceStartup;
        }

        public SpiderMatriarch Matriarch { get { if (Game.InGame) EnsureRegistry(); return matriarchs.FirstOrDefault(m => m != null); } }
        public bool KnownStage(int enemyId, out BossStage stage) => known.TryGetValue(enemyId, out stage);
        public bool IsRemoteTrapped(byte id) => trapped.Contains(id);

        // ---------------- per frame ----------------

        public void Update()
        {
            if (!session.Ready || !Game.InScene) return;
            EnsureRegistry();
            bool authority = EnemySync.IsAuthority;
            if (!authority)
            {
                if (wasAuthority) ReleaseTraps();
                wasAuthority = false;
                return;
            }
            bool tookOver = !wasAuthority;
            wasAuthority = true;
            if (matriarchs.Count == 0) return;
            foreach (var m in matriarchs) if (Fighting(m)) Inherit(m);
            // Webbed while someone else ran the fight: her reaction (skipped on our puppet copy) happens now.
            if (tookOver && LocalWebbed() && Webbing() is SpiderMatriarch web)
            {
                var effect = Game.LocalPlayer.EffectsHandler.GetEffect(EffectType.Trapped);
                if (effect != null)
                    try { web.HandleEffectApplied(effect); }
                    catch (System.Exception ex) { Plugin.Log.LogWarning($"[boss] Web on taking over: {ex.Message}"); }
            }
            float now = Time.realtimeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + PollInterval;
            SendStages(now);
            WatchTraps();
        }

        // ---- stage ----

        private void SendStages(float now)
        {
            if (session.Players.Count < 2) return;
            bool full = now >= nextFull;
            if (full) nextFull = now + FullInterval;
            int scene = NetIds.SceneHash(Game.SceneName);
            foreach (var m in matriarchs)
            {
                if (!Fighting(m) || !TryGetStage(m, out var stage)) continue;
                int id = EnemySync.Instance?.IdOf(m.enemy) ?? 0;
                if (id == 0) continue;
                if (!full && lastSent.TryGetValue(id, out var last) && last == stage) continue;
                lastSent[id] = stage;
                session.Send(Msg.MatriarchState, w => { w.Put(scene); w.Put(id); w.Put((byte)stage); });
                StatesSent++;
            }
        }

        private void OnState(byte from, NetDataReader r)
        {
            int scene = r.GetInt();
            int id = r.GetInt();
            byte raw = r.GetByte();
            if (!Game.InGame || scene != NetIds.SceneHash(Game.SceneName) || raw > (byte)BossStage.Stage4) return;
            EnsureRegistry();
            var es = EnemySync.Instance;
            if (es == null || from == session.LocalId || Time.realtimeSinceStartup - rewoundAt < 2f) return;
            var stage = (BossStage)raw;
            if (!EnemySync.IsAuthority)
            {
                if (from != es.Authority) return;
                known[id] = stage;
                inherit.Add(id);
                StatesReceived++;
                return;
            }
            // We run the fight now: the former runner's last word (sent before it left) still counts if it's further on.
            if (!Senders.IsRunner(from)) return;
            var m = matriarchs.FirstOrDefault(x => x != null && es.IdOf(x.enemy) == id);
            if (m == null || !TryGetStage(m, out var mine) || mine >= stage) return;
            known[id] = stage;
            inherit.Add(id);
            StatesReceived++;
        }

        // We run the fight: the stage it had on the runner before us, written once, as soon as her graph runs here.
        private void Inherit(SpiderMatriarch m)
        {
            if (inherit.Count == 0) return;
            int id = EnemySync.Instance?.IdOf(m.enemy) ?? 0;
            if (id == 0 || !inherit.Remove(id) || !known.TryGetValue(id, out var stage)) return;
            if (TryGetStage(m, out var mine) && mine == stage) return;
            SetStage(m, stage);
            StagesInherited++;
            Plugin.Log.LogInfo($"[boss] {m.name} carries on from {stage} (was {mine} on our copy)");
        }

        // Postfix on Enemy.RestartGraph: taking the fight over restarts her graph; the stage goes in before it ticks.
        public void OnGraphRestarted(Enemy e)
        {
            if (!EnemySync.IsAuthority || inherit.Count == 0 || e == null) return;
            var m = e.GetComponent<SpiderMatriarch>();
            if (m != null && matriarchs.Contains(m)) Inherit(m);
        }

        // ---- the web ----

        private SpiderMatriarch Webbing() => matriarchs.FirstOrDefault(Fighting);

        private void WatchTraps()
        {
            var m = Webbing();
            var fx = StatusFx.Instance;
            string scene = Game.SceneName;
            foreach (var rp in presence.Remotes.Values)
            {
                byte id = rp.Info.Id;
                bool here = rp.Latest != null && rp.Latest.Scene == scene && !rp.Latest.Dead && rp.Puppet != null;
                bool webbed = m != null && here && fx != null && fx.EffectsOf(id).Any(e => e.Type == EffectType.Trapped);
                if (webbed && trapped.Add(id)) OnTrapped(m, rp);
                else if (!webbed && trapped.Remove(id)) OnFreed(m, rp);
            }
            foreach (var gone in trapped.Where(id => !presence.Remotes.ContainsKey(id)).ToList())
            {
                trapped.Remove(gone);
                OnFreed(m, null);
            }
        }

        // What SpiderMatriarch.HandleEffectApplied does for the local player, for another player's puppet.
        private void OnTrapped(SpiderMatriarch m, RemotePlayer rp)
        {
            Vector3 pos = rp.Puppet.transform.position;
            RemoteTraps++;
            int rushed = 0;
            try
            {
                m.playerTrappedEvent?.SendEventMessage(true);
                foreach (var g in EnemiesManager.AllGroups.Where(x => x != null && x.EnemyTag == m.tagToBuff && Vector3.Distance(x.Area.center, pos) < RushRange).ToList())
                {
                    g.LastPlayerSeenPosition = pos;
                    g.TimeToExpirePlayerLastSeen = 15f;
                    g.IsForcedToPosition = true;
                    foreach (var e in g.Enemies.ToList())
                    {
                        if (e == null || e.IsDead) continue;
                        if (!e.TryGetComponent<GraphTrigger>(out var trigger)) trigger = e.gameObject.AddComponent<GraphTrigger>();
                        trigger.Trigger("ForceNewPosition");
                        e.CurrentMoveSpeed = e.MoveSpeeds.Get("Run");
                        e.EffectsHandler.HandleEffect(new DamageEffect { Effect = EffectType.MoveSpeedBuff, DamagePerSec = 1 });
                        rushed++;
                    }
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[boss] Web on {rp.Info}: {ex.Message}"); }
            MPRunner.Instance?.World?.RetargetSoon();
            Plugin.Log.LogInfo($"[boss] {rp.Info} is webbed: {rushed} spiders rush them");
        }

        // What SpiderMatriarch.HandleEffectRemoved does, once nobody is webbed any more.
        private void OnFreed(SpiderMatriarch m, RemotePlayer rp)
        {
            RemoteFrees++;
            MPRunner.Instance?.World?.RetargetSoon();
            Plugin.Log.LogInfo($"[boss] {(rp != null ? rp.Info.ToString() : "a player who left")} is out of the web");
            if (trapped.Count > 0 || LocalWebbed()) return;
            try
            {
                if (m != null) m.playerTrappedEvent?.SendEventMessage(false);
                foreach (var e in EnemiesManager.AliveEnemies.ToList())
                    if (e != null) e.EffectsHandler?.RemoveEffect(EffectType.MoveSpeedBuff);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[boss] Web off: {ex.Message}"); }
        }

        // No longer the runner: the next one decides about the web.
        private void ReleaseTraps()
        {
            trapped.Clear();
        }

        private static bool LocalWebbed()
        {
            var me = Game.LocalPlayer;
            return me != null && !me.IsDead && me.EffectsHandler != null && me.EffectsHandler.HasEffect(EffectType.Trapped);
        }

        // HostWorld: while someone is webbed, she and the spiders around them chase that player.
        private GameObject ForcedTargetFor(Enemy e)
        {
            if (e == null || !EnemySync.IsAuthority || matriarchs.Count == 0) return null;
            var m = Webbing();
            if (m == null) return null;
            Transform webbed = null;
            if (LocalWebbed()) webbed = Game.LocalPlayer.transform;
            else
                foreach (var id in trapped)
                    if (presence.Remotes.TryGetValue(id, out var rp) && rp.Puppet != null && rp.Shown) { webbed = rp.Puppet.transform; break; }
            if (webbed == null) return null;
            if (e == m.enemy) return webbed.gameObject;
            if (e.EnemyTag != m.tagToBuff) return null;
            Vector3 from = e.Group != null ? (Vector3)e.Group.Area.center : e.transform.position;
            return Vector3.Distance(from, webbed.position) < RushRange ? webbed.gameObject : null;
        }
    }

    [HarmonyPatch(typeof(Enemy), nameof(Enemy.RestartGraph))]
    internal static class MatriarchGraphRestartPatch
    {
        private static void Postfix(Enemy __instance) => BossSync.Instance?.OnGraphRestarted(__instance);
    }

    // Non-runners: her reactions to our web are the runner's to make (our spiders are its puppets; it hears of our web
    // through StatusFx).
    [HarmonyPatch(typeof(SpiderMatriarch), nameof(SpiderMatriarch.HandleEffectApplied))]
    internal static class MatriarchWebAppliedPatch
    {
        private static bool Prefix() => !EnemySync.GuestPuppets;
    }

    [HarmonyPatch(typeof(SpiderMatriarch), nameof(SpiderMatriarch.HandleEffectRemoved))]
    internal static class MatriarchWebRemovedPatch
    {
        private static bool Prefix() => !EnemySync.GuestPuppets;
    }
}
