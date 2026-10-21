using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using HarmonyLib;

namespace EmbervilleMP.Sync
{
    // Stage 2: the machine running a scene (SceneAuthority: the host if it's there) runs that scene's enemies;
    // everyone else in the scene shows them as puppets and sends their hits to it.
    //   runner -> all  : EnemyStates (unreliable, 15 Hz) for enemies in rooms where other players are
    //   others -> runner: EnemyHit (a player's resolved hit on an enemy)
    //   runner -> all  : EnemyDied (each copy runs the death locally: animation, its own XP and loot)
    //   runner -> one  : EnemyAttack (an enemy hit that player's puppet; it resolves it on its real character)
    //   old runner -> new runner: EnemyHandoff (who's dead, health, positions) when a higher-priority player arrives
    //   runner -> one  : EnemyDeadList (who's dead) to each player arriving in the scene (EnemySync.Dead.cs)
    internal partial class EnemySync
    {
        public static EnemySync Instance { get; private set; }

        // This machine runs the enemies of the scene it's in (alone, or the host, or the lowest-numbered player).
        public static bool IsAuthority => Session.Current != null && Instance != null && Instance.iAmAuthority;
        // Someone else runs this scene and their states are arriving: our enemies are their puppets.
        public static bool GuestPuppets => Session.Current != null && Instance != null && !Instance.iAmAuthority && Instance.authorityFresh;
        public byte Authority => authority;
        // Set while we apply something that came from the network, so our own patches let it through.
        public static bool Passthrough;
        // NPCs a story cutscene playing here moves (CutsceneSync): our copy runs them, they're nobody's puppets.
        public static System.Func<Enemy, bool> LocalActor;
        public static bool RunsHere(Enemy e) => e != null && LocalActor != null && LocalActor(e);
        // NPCs turned to a player talking to them (NpcTalkSync): each copy turns them itself, the states don't.
        public static System.Func<Enemy, bool> HoldFacing;

        public const float SendRate = 15f;
        public int Revived, SpawnsSent, SpawnsApplied, LocalSpawnsDropped, Wakes;
        public int StatesSent, StatesApplied, HitsSent, HitsApplied, DeathsSent, DeathsApplied, AttacksForwarded, AttacksTaken, AttackStartsSent, AttackStartsApplied;
        public int EnemyAttacksForwarded, AttackCancelsSent, AttackCancelsApplied, AimsSent, AimsApplied;
        public float LastAimLag = -1f;
        // Self-tests: the last attack starts here (who attacked whom), for a check that fails.
        public readonly Queue<string> RecentStarts = new Queue<string>();
        // Set while a non-runner replays a death: its "last hit" pops no number (MuteReplayedDeathNumberPatch).
        public static bool MutingNumbers;

        private readonly Session session;
        private readonly Presence presence;
        private readonly Dictionary<int, Enemy> byId = new Dictionary<int, Enemy>();
        private readonly Dictionary<Enemy, int> idOf = new Dictionary<Enemy, int>();
        private readonly Dictionary<Enemy, string> areaOf = new Dictionary<Enemy, string>();
        private readonly Dictionary<int, EnemyPuppetState> targets = new Dictionary<int, EnemyPuppetState>();
        private readonly HashSet<Enemy> puppets = new HashSet<Enemy>();
        private int registryScene = -1;
        private uint nextSpawnId = 1;
        private bool spawningFromNet;
        private readonly Dictionary<int, float> localDeathTime = new Dictionary<int, float>();
        private readonly HashSet<int> killed = new HashSet<int>();   // host: enemies the players have killed
        private float nextSend;
        private byte authority;
        private bool iAmAuthority = true;
        private bool authorityFresh;
        private float lastAuthorityStates = -100f;
        private bool awaitingHandoff;
        private float registeredAt;
        private float handoffDeadline;
        public int HandoffsSent, HandoffsApplied, AuthorityChanges, HitsIgnored;
        private readonly NetDataWriter batch = new NetDataWriter();
        private readonly NetDataWriter one = new NetDataWriter();
        private const int MaxBatch = 900;
        private readonly EnemyStateTrim trim = new EnemyStateTrim();

        private class EnemyPuppetState
        {
            public Vector2 Position;
            public bool Flipped, Dead;
            public byte Facing;
            public float Health;
            public float AnimSpeed = 1f;
            public int[] Layers = new int[0];
            public float[] Times = new float[0];
            public float[] Floats = new float[0];
            public bool Fresh;
            public float Received;
        }

        public EnemySync(Session session, Presence presence)
        {
            Instance = this;
            this.session = session;
            this.presence = presence;
            session.On(Msg.EnemyStates, OnStates);
            session.On(Msg.EnemyHit, OnHit);
            session.On(Msg.EnemyDied, OnDied);
            session.On(Msg.EnemyAttack, OnAttack);
            session.On(Msg.EnemyAttackStart, OnAttackStart);
            session.On(Msg.EnemySpawn, OnSpawn);
            session.On(Msg.EnemyDamaged, OnDamaged);
            session.On(Msg.EnemyWake, OnWake);
            session.On(Msg.EnemyHandoff, OnHandoff);
            session.On(Msg.EnemyAttackCancel, OnAttackCancel);
            session.On(Msg.EnemyAttackAim, OnAttackAim);
            session.On(Msg.EnemyDeadList, OnDeadList);
            session.Ended += ReleaseAll;
        }

        // ---------------- registry ----------------

        private void EnsureRegistry()
        {
            int scene = SceneManager.GetActiveScene().handle;
            if (scene == registryScene) return;
            registryScene = scene;
            registeredAt = Time.realtimeSinceStartup;
            byId.Clear();
            idOf.Clear();
            areaOf.Clear();
            localDeathTime.Clear();
            killed.Clear();
            targets.Clear();
            puppets.Clear();
            authority = 0;
            iAmAuthority = true;
            authorityFresh = false;
            awaitingHandoff = false;
            ClearDeadLists();
            foreach (var e in Object.FindObjectsByType<Enemy>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Register(e);
            Plugin.Log.LogInfo($"[enemy] Registered {byId.Count} enemies in {Game.SceneName}");
        }

        private int Register(Enemy e)
        {
            if (idOf.TryGetValue(e, out int id)) return id;
            if (NetIds.IsRuntimeSpawned(e.transform)) return 0; // runtime spawns get ids from the host (later)
            id = NetIds.Hash(NetIds.ScenePath(e.transform));
            if (byId.TryGetValue(id, out var clash) && clash != e)
            {
                Plugin.Log.LogWarning($"[enemy] Id clash {id}: {e.name} vs {clash.name}");
                return 0;
            }
            byId[id] = e;
            idOf[e] = id;
            areaOf[e] = e.GetComponentInParent<GameArea>(true)?.name ?? "";
            return id;
        }

        public int IdOf(Enemy e) => e != null && idOf.TryGetValue(e, out int id) ? id : 0;
        public Enemy ById(int id) => byId.TryGetValue(id, out var e) ? e : null;
        public string AreaOf(Enemy e) => areaOf.TryGetValue(e, out var a) ? a : "";
        public bool IsPuppet(Enemy e) => puppets.Contains(e);

        // ---------------- per frame ----------------

        public void Update()
        {
            if (!session.Ready || !Game.InScene) return;   // (a dead runner keeps running its scene: Game.InScene)
            EnsureRegistry();
            EnemyAim.Record();
            byte now = SceneAuthority.Mine;
            if (now == 0) now = session.LocalId;
            // A guest that just arrived while another player may be on the way here (not heard from yet since our
            // syncs started, or between scenes, like a host still loading the same fresh world): nobody's scene yet,
            // for a few seconds. Taking it would mean handing it over moments later, before its enemies are set up.
            if (now == session.LocalId && authority == 0 && !session.IsHost && OthersUnsettled() &&
                Time.realtimeSinceStartup - registeredAt < ArrivalGrace)
            {
                iAmAuthority = false;
                return;
            }
            if (now != authority) OnAuthorityChanged(now);
            if (iAmAuthority)
            {
                if (awaitingHandoff && Time.realtimeSinceStartup < handoffDeadline) return;
                awaitingHandoff = false;
                HostUpdate();
                TellArrivalsWhoIsDead();
            }
            else
            {
                GuestUpdate();
                KeepDeadDead();
            }
        }

        public const float ArrivalGrace = 5f;

        private bool OthersUnsettled() => session.Players.Values.Any(p => p.Id != session.LocalId &&
            (!presence.Remotes.TryGetValue(p.Id, out var rp) || rp.Latest == null || string.IsNullOrEmpty(rp.Latest.Scene)));

        private void OnAuthorityChanged(byte now)
        {
            bool was = iAmAuthority && authority != 0;
            byte old = authority;
            authority = now;
            iAmAuthority = now == session.LocalId;
            AuthorityChanges++;
            Senders.RunnerChanged(old);
            Plugin.Log.LogInfo($"[enemy] {Game.SceneName} is now run by #{now}{(iAmAuthority ? " (us)" : "")}");
            // Someone above us arrived: give them the fight as it is. Not in our first seconds in the scene: nothing can
            // have happened yet, and its enemies aren't set up yet (they'd all look dead and be killed on the new runner).
            if (was && !iAmAuthority && Time.realtimeSinceStartup - registeredAt > 2f)
            {
                SendHandoff(now);
                EnemyFx.Instance?.SendHandoff(now);   // effects and boss stages
            }
            if (iAmAuthority)
            {
                ReleaseAll();
                // We just walked into a scene others were already playing: wait for its state before taking over.
                byte previous = SceneAuthority.For(Game.SceneName, session.LocalId);
                awaitingHandoff = previous != 0 && targets.Count == 0 && old == 0;
                handoffDeadline = Time.realtimeSinceStartup + 3f;
                if (awaitingHandoff) Plugin.Log.LogInfo($"[enemy] Waiting for #{previous}'s handoff");
            }
        }

        private void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextSend) return;
            nextSend = now + 1f / SendRate;
            string scene = Game.SceneName;
            var guestAreas = new HashSet<string>(presence.Remotes.Values
                .Where(r => r.Latest != null && r.Latest.Scene == scene).Select(r => r.Latest.Area));
            if (guestAreas.Count == 0) return;

            batch.Reset();
            int count = 0, countPos = 0;
            void Begin()
            {
                batch.Reset();
                batch.Put((byte)Msg.EnemyStates);
                batch.Put(session.LocalId);
                batch.Put(NetIds.SceneHash(scene));
                countPos = batch.Length;
                batch.Put((byte)0);
                count = 0;
            }
            void Flush()
            {
                if (count == 0) return;
                batch.Data[countPos] = (byte)count;
                session.SendRaw(batch, DeliveryMethod.Unreliable);
                StatesSent += count;
            }
            Begin();
            foreach (var e in EnemiesManager.AllEnemies)
            {
                if (e == null || !e.isActiveAndEnabled) continue;
                int id = IdOf(e);
                if (id == 0 || !guestAreas.Contains(AreaOf(e)) || !trim.Due(id, e, now)) continue;
                // Unreliable packets can't go over 1023 bytes (LiteNetLib throws), and states vary in size (layers,
                // floats): the batch goes out before a state that wouldn't fit, with room left for the relay's header.
                one.Reset();
                WriteState(one, id, e);
                if (count > 0 && batch.Length + one.Length > MaxBatch) { Flush(); Begin(); }
                batch.Put(one.Data, 0, one.Length);
                count++;
                if (count == 255) { Flush(); Begin(); }
            }
            Flush();
        }

        private static void WriteState(NetDataWriter w, int id, Enemy e)
        {
            var p = e.transform.position;
            var anim = e.Animator;
            w.Put(id);
            w.Put(p.x);
            w.Put(p.y);
            w.Put((byte)((e.IsFlipped ? 1 : 0) | (e.IsDead ? 2 : 0)));
            w.Put((byte)e.FacingDirection);
            w.Put(e.CurrentHealth);
            w.Put(anim != null ? anim.speed : 1f);
            int layers = anim != null && anim.isActiveAndEnabled ? anim.layerCount : 0;
            w.Put((byte)layers);
            for (int i = 0; i < layers; i++)
            {
                var info = anim.IsInTransition(i) ? anim.GetNextAnimatorStateInfo(i) : anim.GetCurrentAnimatorStateInfo(i);
                w.Put(info.fullPathHash);
                w.Put(info.normalizedTime);
            }
            var floats = layers > 0 ? FloatParams(anim) : new int[0];
            w.Put((byte)floats.Length);
            foreach (int f in floats) w.Put(anim.GetFloat(f));
        }

        private static readonly Dictionary<RuntimeAnimatorController, int[]> floatParamCache = new Dictionary<RuntimeAnimatorController, int[]>();

        private static int[] FloatParams(Animator anim)
        {
            var ctrl = anim.runtimeAnimatorController;
            if (ctrl == null) return new int[0];
            if (!floatParamCache.TryGetValue(ctrl, out var list))
            {
                list = anim.parameters.Where(p => p.type == AnimatorControllerParameterType.Float).Select(p => p.nameHash).ToArray();
                floatParamCache[ctrl] = list;
            }
            return list;
        }

        private void GuestUpdate()
        {
            float now = Time.realtimeSinceStartup;
            bool fresh = now - lastAuthorityStates < 2f;
            if (fresh != authorityFresh)
            {
                authorityFresh = fresh;
                Plugin.Log.LogInfo(fresh ? $"[enemy] Enemies follow #{authority}" : $"[enemy] No states from #{authority}: running enemies locally");
                if (!fresh) ReleaseAll();
            }
            if (!authorityFresh) return;

            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 12f);
            foreach (var kv in targets)
            {
                var e = ById(kv.Key);
                var s = kv.Value;
                if (e == null) continue;
                if (RunsHere(e)) { puppets.Remove(e); continue; }
                // The host's copy is alive but ours died (a reset on the host, or a lost message): bring it back.
                if (e.IsDead && !s.Dead && s.Fresh && s.Received > LocalDeathTime(kv.Key) + 1.5f && AreaActive(e))
                {
                    Revive(e, s);
                    continue;
                }
                if (!e.isActiveAndEnabled) continue;
                if (e.IsDead)
                {
                    // The corpse slides like the runner's (the killing blow's knockback), for a moment after the death.
                    if (s.Dead && Time.realtimeSinceStartup - LocalDeathTime(kv.Key) < CorpseFollow) Follow(e, s, k);
                    continue;
                }
                if (!puppets.Contains(e)) Puppetize(e);
                if (s.Dead) continue; // the EnemyDied message runs the death

                Follow(e, s, k);
                if (!s.Fresh) continue;
                s.Fresh = false;

                Passthrough = true;
                try
                {
                    if (HoldFacing == null || !HoldFacing(e))
                    {
                        if (e.IsFlipped != s.Flipped) e.IsFlipped = s.Flipped;
                        if ((byte)e.FacingDirection != s.Facing) e.FacingDirection = (Direction)s.Facing;
                    }
                    if (!Mathf.Approximately(e.CurrentHealth, s.Health))
                    {
                        float before = e.currentHealth / e.initHealth;
                        e.currentHealth = s.Health;
                        RaiseHealthChanged(e, e.currentHealth / e.initHealth, before);
                    }
                }
                finally { Passthrough = false; }

                var anim = e.Animator;
                if (anim == null || !anim.isActiveAndEnabled) continue;
                anim.speed = s.AnimSpeed;
                var floats = FloatParams(anim);
                for (int i = 0; i < floats.Length && i < s.Floats.Length; i++) anim.SetFloat(floats[i], s.Floats[i]);
                for (int i = 0; i < s.Layers.Length && i < anim.layerCount; i++)
                {
                    var cur = anim.GetCurrentAnimatorStateInfo(i);
                    if (cur.fullPathHash == s.Layers[i]) continue;
                    if (anim.IsInTransition(i) && anim.GetNextAnimatorStateInfo(i).fullPathHash == s.Layers[i]) continue;
                    float t = s.Times[i];
                    anim.Play(s.Layers[i], i, t > 1f ? t % 1f : Mathf.Max(0f, t));
                }
                StatesApplied++;
            }
        }

        public const float CorpseFollow = 1.5f;

        private static void Follow(Enemy e, EnemyPuppetState s, float k)
        {
            Vector3 pos = e.transform.position;
            Vector3 target = new Vector3(s.Position.x, s.Position.y, pos.z);
            e.transform.position = (target - pos).sqrMagnitude > 9f ? target : Vector3.Lerp(pos, target, k);
        }

        // Looked up on first use, not in a static initializer: if a game update renames the event, only enemy health
        // bars on guests stop updating (logged once) instead of the whole of EnemySync failing to initialise.
        private static AccessTools.FieldRef<Enemy, Enemy.EnemyHealthPercentChanged> healthChanged;
        private static bool healthChangedLooked;

        private static void RaiseHealthChanged(Enemy e, float now, float before)
        {
            if (!healthChangedLooked)
            {
                healthChangedLooked = true;
                try { healthChanged = AccessTools.FieldRefAccess<Enemy, Enemy.EnemyHealthPercentChanged>("HealthChanged"); }
                catch (System.Exception ex) { Plugin.Log.LogError("[enemy] Enemy.HealthChanged not found (game update?): " + ex.Message); }
            }
            if (healthChanged != null) healthChanged(e)?.Invoke(now, before);
        }

        private float LocalDeathTime(int id) => localDeathTime.TryGetValue(id, out float t) ? t : -100f;

        private static bool AreaActive(Enemy e)
        {
            var area = e.GetComponentInParent<GameArea>(true);
            return area == null || area.gameObject.activeInHierarchy;
        }

        private void Revive(Enemy e, EnemyPuppetState s)
        {
            Plugin.Log.LogInfo($"[enemy] Reviving {e.name} (alive on the host)");
            e.transform.position = new Vector3(s.Position.x, s.Position.y, e.transform.position.z);
            e.isDead = false;
            puppets.Remove(e);
            e.gameObject.SetActive(true);
            Revived++;
        }

        // Stops the enemy's own brain: the host's copy decides what it does.
        private void Puppetize(Enemy e)
        {
            puppets.Add(e);
            Passthrough = true;
            try
            {
                e.CancelAttack();
                e.DisableBehaviorGraph();
                e.navMeshAgent?.Disable();
                e.ChangeState(e.idleState);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemy] Puppetize {e.name}: {ex.Message}"); }
            finally { Passthrough = false; }
        }

        private void ReleaseAll()
        {
            foreach (var e in puppets)
            {
                if (e == null || !e.isActiveAndEnabled || e.IsDead) continue;
                try
                {
                    e.RestartGraph();
                    e.ChangeState(e.runBehaviorState);
                }
                catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemy] Release {e.name}: {ex.Message}"); }
            }
            puppets.Clear();
            targets.Clear();
        }

        // ---------------- messages ----------------

        private void OnStates(byte from, NetDataReader r)
        {
            if (iAmAuthority || from != authority) return;
            if (r.GetInt() != NetIds.SceneHash(Game.SceneName)) return;
            lastAuthorityStates = Time.realtimeSinceStartup;
            int n = r.GetByte();
            for (int i = 0; i < n; i++)
            {
                int id = r.GetInt();
                if (!targets.TryGetValue(id, out var s)) targets[id] = s = new EnemyPuppetState();
                s.Position = new Vector2(r.GetFloat(), r.GetFloat());
                byte flags = r.GetByte();
                s.Flipped = (flags & 1) != 0;
                s.Dead = (flags & 2) != 0;
                s.Facing = r.GetByte();
                s.Health = r.GetFloat();
                s.AnimSpeed = r.GetFloat();
                int layers = r.GetByte();
                if (s.Layers.Length != layers) { s.Layers = new int[layers]; s.Times = new float[layers]; }
                for (int l = 0; l < layers; l++) { s.Layers[l] = r.GetInt(); s.Times[l] = r.GetFloat(); }
                int floats = r.GetByte();
                if (s.Floats.Length != floats) s.Floats = new float[floats];
                for (int f = 0; f < floats; f++) s.Floats[f] = r.GetFloat();
                s.Fresh = true;
                s.Received = lastAuthorityStates;
            }
        }

        // Guest: our character hit an enemy puppet. A hit from our CombatSystem goes as it left our side (EnemyFx: the
        // runner resolves the target's side and we show its number); anything else is shown now and applied as it is.
        public void SendHit(Enemy e, DamageData d)
        {
            int id = IdOf(e);
            if (id == 0) return;
            HitsSent++;
            if (EnemyFx.Instance != null && EnemyFx.Instance.SendRawHit(e, id, d))
            {
                try { e.sfxComponent?.PlaySfx("Hit", d.HitType); } catch { }
                return;
            }
            session.Send(Msg.EnemyHit, w => { w.Put(id); DamageCodec.Write(w, d); });
            if (d.Result != DamageResult.Parry)
                Bus<UIDamageEvent>.Invoke(new UIDamageEvent(d.Amount, "", e.CenterOfMass.position, DamageResult.Success));
            try { e.sfxComponent?.PlaySfx("Hit", d.HitType); } catch { }
        }

        private void OnHit(byte from, NetDataReader r)
        {
            if (!iAmAuthority) return;
            int hitId = r.GetInt();
            var e = ById(hitId);
            var d = DamageCodec.Read(r);
            if (e == null || !e.isActiveAndEnabled || e.IsDead)
            {
                if (HitsIgnored++ < 10)
                    Plugin.Log.LogInfo($"[enemy] Hit from #{from} on #{hitId} ignored: {(e == null ? "no such enemy here" : e.IsDead ? e.name + " is dead here" : e.name + " is inactive here")}");
                return;
            }
            // Custom effects (skills' bleeds, burns...) are objects; rebuild them from the attack asset.
            if (d.Attack != null && d.Attack.customEffects != null)
                foreach (var c in d.Attack.customEffects)
                    try { d.CreatedEffects.Add(c.CreateEffect(new EffectCreationContext { Level = d.Level })); } catch { }
            ApplyRemoteHit(from, e, d, true);
        }

        // Runner: another player's hit, resolved. Credited to our character (the game only engages and gives XP for
        // PlayerMaster attackers); its knockback (and the Acolyte Protector's absorb) point away from that player's
        // puppet, not from us (EnemyFxPatches). shownByHitter: the hitter showed its number already.
        internal void ApplyRemoteHit(byte from, Enemy e, DamageData d, bool shownByHitter)
        {
            d.Aggressor = Game.LocalPlayer;
            d.Target = e;
            lastHitBy[e] = from;
            var source = EnemyFx.HitSource;
            bool showed = EnemyFx.HitterShowed;
            EnemyFx.HitSource = HitSourceOf(from) ?? source;
            EnemyFx.HitterShowed = shownByHitter;
            try { e.ApplyDamage(d); }
            finally
            {
                EnemyFx.HitSource = source;
                EnemyFx.HitterShowed = showed;
            }
            HitsApplied++;
        }

        internal IDamageable HitSourceOf(byte player) => presence.Remotes.TryGetValue(player, out var rp) && rp.Target != null ? rp.Target : null;

        // Runner: something that player did to this enemy (an effect) counts as their hit (death notices, the killer).
        internal void NoteHitBy(Enemy e, byte player) => lastHitBy[e] = player;

        private readonly Dictionary<Enemy, byte> lastHitBy = new Dictionary<Enemy, byte>();

        // Host: an enemy died (postfix on SetKilled).
        public void OnKilled(Enemy e)
        {
            int id = IdOf(e);
            if (id == 0) return;
            killed.Add(id);
            byte killer = lastHitBy.TryGetValue(e, out var k) ? k : session.LocalId;
            lastHitBy.Remove(e);
            int type = (int)e.enemyType;
            int scene = NetIds.SceneHash(Game.SceneName);
            byte cause = EnemyFx.Instance?.CauseOf(e) ?? EnemyFx.CausePlayer;   // only a player's hit gives XP
            byte gore = (byte)(e.DieState != null && e.DieState.IsGore ? 1 : 0);   // (rolled before SetKilled)
            session.Send(Msg.EnemyDied, w => { w.Put(id); w.Put(killer); w.Put(scene); w.Put(type); w.Put(cause); w.Put(gore); });
            DeathsSent++;
        }

        private void OnDied(byte from, NetDataReader r)
        {
            int deadId = r.GetInt();
            byte killerId = r.GetByte();
            int scene = r.GetInt();
            var type = (EnemyType)r.GetInt();
            bool byPlayer = r.AvailableBytes == 0 || r.GetByte() != EnemyFx.CauseNone;
            int gore = r.AvailableBytes > 0 ? r.GetByte() : -1;
            // A kill in a scene we're not in still counts for our "kill X" quest objectives.
            if (scene != NetIds.SceneHash(Game.SceneName) || !Game.InGame) { WorldState.Instance?.OnRemoteKill(type); return; }
            if (iAmAuthority || from != authority) return;
            localDeathTime[deadId] = Time.realtimeSinceStartup;
            var e = ById(deadId);
            if (e != null && e.IsDead) return;   // already dead here (its death ran, or a handoff said so)
            if (e == null || !e.isActiveAndEnabled)
            {
                // In our scene but out of sight (another room, a spawn we never got): it still counts for our quests,
                // our character gets its XP if a player killed it (we're in the scene), and it stays dead here (OnEnable
                // hides dead enemies).
                if (e != null)
                {
                    e.isDead = true;
                    if (byPlayer) KillCredit.GrantOutOfSightXp(e);
                }
                WorldState.Instance?.OnRemoteKill(type);
                return;
            }
            if (e == null || !e.isActiveAndEnabled || e.IsDead || RunsHere(e)) return;
            var me = Game.LocalPlayer;
            e.CanTakeDamage = true;   // dead on the runner even if it was invulnerable there (EnemyFx mirrors that)
            // The runner's gore roll (an overkill makes it likely there; our last hit is exactly the health left).
            bool useGore = e.useDeathGore;
            float goreChance = e.randomGoreDeathChance;
            if (gore >= 0) { e.useDeathGore = gore == 1; e.randomGoreDeathChance = 2f; }
            Passthrough = true;
            MutingNumbers = true;   // the killing blow's number shows already (EnemyDamaged)
            try
            {
                // Runs the game's own death: animation, our loot roll, and our XP when a player killed it (no attacker
                // for a self-destruct, a burn's tick, another enemy, a trap: the game gives no XP for those).
                e.ApplyDamage(new DamageData
                {
                    Aggressor = byPlayer ? me : null,
                    Target = e,
                    Amount = Mathf.Max(1, Mathf.CeilToInt(e.CurrentHealth)),
                    Result = DamageResult.Success,
                    NoStagger = true,
                    Effects = new List<DamageEffect>(),
                    CreatedEffects = new List<Effect>(),
                });
            }
            finally
            {
                Passthrough = false;
                MutingNumbers = false;
                e.useDeathGore = useGore;
                e.randomGoreDeathChance = goreChance;
            }
            DeathsApplied++;
        }

        // Host: an enemy starts an attack. Guests replay it on their copy: same animation, projectiles and
        // effects (their damage is blocked on the guest; hits are decided here).
        public void OnAttackStarted(Enemy e, Attack attack, GameObject target, Vector3? targetPos)
        {
            int id = IdOf(e);
            if (id == 0 || attack == null) return;
            byte targetPlayer = 0;
            if (target != null)
            {
                if (target == Game.LocalPlayer?.gameObject) targetPlayer = session.LocalId;
                else
                    foreach (var r in presence.Remotes.Values)
                        if (r.Puppet == target) targetPlayer = r.Info.Id;
            }
            if (Args.Test != null)
            {
                var at = target != null ? target.transform.position : targetPos ?? e.transform.position;
                RecentStarts.Enqueue($"{Time.realtimeSinceStartup:F1}s {e.name}#{id} {attack.name} -> {(targetPlayer != 0 ? "#" + targetPlayer : target != null ? target.name : "a spot")} at {Vector2.Distance(at, e.transform.position):F1}");
                while (RecentStarts.Count > 20) RecentStarts.Dequeue();
            }
            session.Send(Msg.EnemyAttackStart, w =>
            {
                w.Put(id);
                w.Put(attack.name);
                w.Put(targetPlayer);
                w.Put(targetPos.HasValue);
                if (targetPos.HasValue) { w.Put(targetPos.Value.x); w.Put(targetPos.Value.y); }
                EnemyFx.Instance?.WriteAttackGroup(w, e);   // a group skill: one projectile from the group's centre
            });
            AttackStartsSent++;
            // Aimed at another player's puppet: which moment of that player it shows (EnemyAim).
            if (targetPlayer != 0 && targetPlayer != session.LocalId && presence.Remotes.TryGetValue(targetPlayer, out var aimed) && aimed.RenderClock >= 0f)
            {
                float clock = aimed.RenderClock;
                session.Send(Msg.EnemyAttackAim, w => { w.Put(id); w.Put(targetPlayer); w.Put(clock); });
                AimsSent++;
            }
        }

        // Runner: an attack was interrupted (EnemyCancelAttackPatch).
        public void OnAttackCancelled(Enemy e)
        {
            int id = IdOf(e);
            if (id == 0) return;
            session.Send(Msg.EnemyAttackCancel, w => w.Put(id));
            AttackCancelsSent++;
        }

        private void OnAttackCancel(byte from, NetDataReader r)
        {
            if (iAmAuthority || from != authority) return;
            var e = ById(r.GetInt());
            if (e == null || !e.isAttacking || RunsHere(e)) return;
            Passthrough = true;
            try { e.CancelAttack(); }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemy] Cancel attack on {e.name}: {ex.Message}"); }
            finally { Passthrough = false; }
            AttackCancelsApplied++;
        }

        // Our replay of an attack on us aims where the runner's does: at us as its puppet of us shows us.
        private void OnAttackAim(byte from, NetDataReader r)
        {
            if (iAmAuthority || from != authority) return;
            var e = ById(r.GetInt());
            byte targetPlayer = r.GetByte();
            float clock = r.GetFloat();
            var me = Game.LocalPlayer;
            if (targetPlayer != session.LocalId || me == null || e == null || !e.isAttacking || RunsHere(e) || e.currentTarget != me.gameObject) return;
            if (float.IsNaN(clock) || Mathf.Abs(Time.realtimeSinceStartup - clock) > 5f) return;   // not a moment we still know
            var proxy = EnemyAim.For(e, clock);
            e.currentTarget = proxy.gameObject;
            LastAimLag = proxy.Lag;
            AimsApplied++;
        }

        private void OnAttackStart(byte from, NetDataReader r)
        {
            if (iAmAuthority || from != authority || !authorityFresh) return;
            var e = ById(r.GetInt());
            var attack = DamageCodec.FindAttackByName(r.GetString());
            byte targetPlayer = r.GetByte();
            Vector3? pos = null;
            if (r.GetBool()) pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
            var group = EnemyFx.Instance?.ReadAttackGroup(r, from, e, attack);
            if (e == null || attack == null || !e.isActiveAndEnabled || e.IsDead || RunsHere(e)) return;
            GameObject target = null;
            if (targetPlayer == session.LocalId) target = Game.LocalPlayer?.gameObject;
            else if (presence.Remotes.TryGetValue(targetPlayer, out var rp)) target = rp.Puppet;
            Passthrough = true;
            try
            {
                if (e.isAttacking) e.CancelAttack();
                e.PerformAttack(attack, target, pos, group);
                AttackStartsApplied++;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[enemy] Replay attack {attack.name} on {e.name}: {ex.Message}"); }
            finally { Passthrough = false; }
        }

        // ---- runtime spawns ----

        // Host: an enemy from a pool came alive (postfix on EnemiesManager.SetEnemyAlive).
        public void OnEnemyAlive(Enemy e)
        {
            if (!NetIds.IsRuntimeSpawned(e.transform)) return;
            if (idOf.TryGetValue(e, out int old)) byId.Remove(old);
            // top bit: runtime spawn; next 7 bits: who spawned it (ids stay unique when the runner changes)
            int id = unchecked((int)(0x80000000u | ((uint)(session.LocalId & 0x7F) << 24) | (nextSpawnId++ & 0xFFFFFF)));
            byId[id] = e;
            idOf[e] = id;
            areaOf[e] = AreaAt(e.transform.position);
            string prefab = PrefabName(e);
            var p = e.transform.position;
            session.Send(Msg.EnemySpawn, w => { w.Put(id); w.Put(prefab); w.Put(p.x); w.Put(p.y); });
            SpawnsSent++;
        }

        // Guest: our game woke a placed enemy that was dormant (a boss after its intro cinematic). The host's
        // copy may still be asleep if the host hasn't walked in yet.
        public void OnLocalPlacedEnemyAlive(Enemy e)
        {
            int id = IdOf(e);
            if (id == 0 || spawningFromNet) return;
            if (targets.TryGetValue(id, out var s) && Time.realtimeSinceStartup - s.Received < 1f) return; // the host's is running
            session.Send(Msg.EnemyWake, w => w.Put(id));
        }

        private void OnWake(byte from, NetDataReader r)
        {
            if (!iAmAuthority) return;
            int id = r.GetInt();
            var e = ById(id);
            if (e == null || killed.Contains(id) || e.gameObject.activeInHierarchy) return;
            var area = e.GetComponentInParent<GameArea>(true);
            if (area != null && !area.gameObject.activeInHierarchy) return;
            Plugin.Log.LogInfo($"[enemy] A guest woke {e.name}: waking ours");
            e.ForceAlive();
            e.gameObject.SetActive(true);
            Wakes++;
        }

        // Guest: a pool enemy came alive here on its own (our copy of an egg hatching, a wave): the host's
        // copy is the real one and arrives as EnemySpawn, so this one goes away.
        public void OnLocalEnemyAlive(Enemy e)
        {
            if (spawningFromNet || !NetIds.IsRuntimeSpawned(e.transform)) return;
            e.isDead = true;
            e.gameObject.SetActive(false);
            LocalSpawnsDropped++;
        }

        private void OnSpawn(byte from, NetDataReader r)
        {
            if (iAmAuthority || from != authority) return;
            int id = r.GetInt();
            string prefabName = r.GetString();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
            SpawnLocal(id, prefabName, pos);
        }

        private Enemy SpawnLocal(int id, string prefabName, Vector3 pos)
        {
            var prefab = PoolManager.pools.Keys.FirstOrDefault(k => k != null && k.name == prefabName);
            GameObject go;
            if (prefab != null) go = PoolManager.Get(prefab);
            else
            {
                // Not pooled: room events and enemy waves Instantiate copies of templates kept (inactive) in the scene.
                var template = Resources.FindObjectsOfTypeAll<Enemy>()
                    .Where(x => x != null && x.gameObject.name == prefabName && !NetIds.IsRuntimeSpawned(x.transform))
                    .OrderBy(x => x.gameObject.scene == SceneManager.GetActiveScene() ? 0 : 1).FirstOrDefault();
                if (template == null) { Plugin.Log.LogWarning($"[enemy] No pool or template for spawned {prefabName}"); return null; }
                spawningFromNet = true;   // the copy may wake up at once (active template)
                try { go = Object.Instantiate(template.gameObject, pos, Quaternion.identity); }
                finally { spawningFromNet = false; }
                go.SetActive(false);
            }
            if (go == null) return null;
            var e = go.GetComponent<Enemy>();
            if (e == null) return null;
            if (idOf.TryGetValue(e, out int old)) byId.Remove(old);
            byId[id] = e;
            idOf[e] = id;
            areaOf[e] = AreaAt(pos);
            go.transform.position = pos;
            spawningFromNet = true;
            try
            {
                e.ForceAlive();
                go.SetActive(true);
            }
            finally { spawningFromNet = false; }
            SpawnsApplied++;
            return e;
        }

        private static string PrefabName(Enemy e)
        {
            string n = e.gameObject.name;
            return n.EndsWith("(Clone)") ? n.Substring(0, n.Length - 7).Trim() : n;
        }

        private static string AreaAt(Vector3 pos)
        {
            foreach (var a in Object.FindObjectsByType<GameArea>(FindObjectsSortMode.None))
                if (a.CameraConfiner != null && a.CameraConfiner.OverlapPoint(pos)) return a.name;
            return Game.AreaName;
        }

        // ---- damage numbers for hits by other players ----

        // Host: an enemy took damage from someone (postfix on ApplyDamage).
        public void OnEnemyDamaged(Enemy e, int amount, DamageResult result, byte byPlayer)
        {
            int id = IdOf(e);
            if (id == 0 || amount <= 0 && result != DamageResult.Parry && result != DamageResult.Block) return;
            bool hitterShowed = EnemyFx.HitterShowed;
            session.Send(Msg.EnemyDamaged, w => { w.Put(id); w.Put(amount); w.Put((int)result); w.Put(byPlayer); w.Put(hitterShowed); });
        }

        private void OnDamaged(byte from, NetDataReader r)
        {
            if (iAmAuthority) return;
            var e = ById(r.GetInt());
            int amount = r.GetInt();
            var result = (DamageResult)r.GetInt();
            byte by = r.GetByte();
            bool hitterShowed = r.AvailableBytes == 0 || r.GetBool();
            if (e == null || (by == session.LocalId && hitterShowed) || !e.isActiveAndEnabled) return; // our own hit already showed
            if (EnemyFx.Instance != null) EnemyFx.Instance.ShowDamage(e, amount, result, by == session.LocalId);
            else if (amount > 0) Bus<UIDamageEvent>.Invoke(new UIDamageEvent(amount, "", e.CenterOfMass.position, result == DamageResult.Critical ? DamageResult.Critical : DamageResult.Success));
        }

        public byte CurrentHitter(Enemy e) => lastHitBy.TryGetValue(e, out var b) ? b : session.LocalId;

        // Host: an enemy's attack reached a guest's puppet. The guest resolves it against its real character
        // (its own defence, dodge, block, parry).
        public void ForwardAttack(byte playerId, IDamageable aggressor, DamageData d)
        {
            int enemyId = aggressor is Enemy e ? IdOf(e) : 0;
            if (aggressor is Enemy) EnemyAttacksForwarded++;
            if (d.Processors != null)
                foreach (var p in d.Processors.Where(p => p.ProcessOrder == DamageDataProcessor.Order.PreTargetDamage).ToList())
                    p.Process(ref d);
            session.Send(Msg.EnemyAttack, w => { w.Put(playerId); w.Put(enemyId); DamageCodec.Write(w, d); });
            AttacksForwarded++;
        }

        private void OnAttack(byte from, NetDataReader r)
        {
            if (r.GetByte() != session.LocalId || from != authority) return;
            var e = ById(r.GetInt());
            var d = DamageCodec.Read(r);
            var me = Game.LocalPlayer;
            if (me == null) return;
            d.Aggressor = e;
            d.Target = me;
            Passthrough = true;
            DamageData counter;
            try { counter = CombatSystem.Resolve(d); }
            finally { Passthrough = false; }
            AttacksTaken++;
            if (e != null && counter.Result == DamageResult.Success && counter.Amount > 0)
                SendHit(e, counter); // our counter-attack goes to the host like any hit
        }
            // ---- handoff: a higher-priority player (usually the host) walked into the scene we were running ----

        private void SendHandoff(byte to)
        {
            int count = 0;
            var w = new NetDataWriter();
            w.Put(to);
            w.Put(NetIds.SceneHash(Game.SceneName));
            var entries = new List<(int id, Enemy e)>();
            foreach (var kv in byId) if (kv.Value != null) entries.Add((kv.Key, kv.Value));
            w.Put(entries.Count);
            foreach (var (id, e) in entries)
            {
                // Dead in our fight: killed while we ran it, or a death we were told about. Not just IsDead: enemies of
                // rooms that haven't woken up yet look dead too, and the new runner would kill them all.
                bool dead = killed.Contains(id) || localDeathTime.ContainsKey(id);
                w.Put(id);
                w.Put((byte)((dead ? 1 : 0) | (e.gameObject.activeInHierarchy ? 2 : 0)));
                w.Put(e.CurrentHealth);
                w.Put(e.transform.position.x);
                w.Put(e.transform.position.y);
                w.Put(id < 0 ? PrefabName(e) : "");
                count++;
            }
            session.Send(Msg.EnemyHandoff, x => x.Put(w.Data, 0, w.Length));
            HandoffsSent++;
            Plugin.Log.LogInfo($"[enemy] Handed {Game.SceneName} over to #{to} ({count} enemies)");
        }

        private void OnHandoff(byte from, NetDataReader r)
        {
            if (r.GetByte() != session.LocalId) return;
            if (r.GetInt() != NetIds.SceneHash(Game.SceneName)) return;
            int n = r.GetInt();
            int dead = 0, moved = 0, spawned = 0;
            for (int i = 0; i < n; i++)
            {
                int id = r.GetInt();
                byte flags = r.GetByte();
                float hp = r.GetFloat();
                var pos = new Vector3(r.GetFloat(), r.GetFloat(), 0f);
                string prefab = r.GetString();
                bool isDead = (flags & 1) != 0, active = (flags & 2) != 0;
                var e = ById(id);
                // (each spawn searches the loaded assets: a junk handoff must not make thousands)
                if (e == null && id < 0 && !isDead && active && spawned++ < 64) e = SpawnLocal(id, prefab, pos);
                if (e == null) continue;
                if (isDead)
                {
                    killed.Add(id);
                    if (!e.IsDead || e.gameObject.activeSelf)
                    {
                        // Gone in their fight: gone here too, without a second death (no loot, no XP).
                        e.isDead = true;
                        e.gameObject.SetActive(false);
                        dead++;
                    }
                    continue;
                }
                if (!e.gameObject.activeInHierarchy) continue;
                e.transform.position = new Vector3(pos.x, pos.y, e.transform.position.z);
                e.currentHealth = hp;
                moved++;
            }
            awaitingHandoff = false;
            HandoffsApplied++;
            Plugin.Log.LogInfo($"[enemy] Took over {Game.SceneName} from #{from}: {dead} dead, {moved} placed");
        }
    }
}
