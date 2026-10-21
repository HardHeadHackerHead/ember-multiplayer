using System;
using System.Collections.Generic;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EmbervilleMP.Sync
{
    // Teammates' sounds. The game posts a player's sounds from its own code, on the real character only (PlayerAttack:
    // the attack's sfx, PlayerDodge, skills' start/end/release/charge steps, "Hurt" with its hit type, "GoreDeath",
    // potions, "Spawn"/"Teleport"...): puppets were silent. Every Wwise event the game posts on our character's
    // GameObject is caught where it's posted (AK.Wwise.Event.Post) and sent as its id (a few bytes per sound, a batch
    // per frame); the others post the same event on an emitter that follows the puppet, at the moment the puppet
    // shows it (the state's clock and interpolation delay), so Wwise's attenuation and the hit-type switch apply
    // like on a real player. Not sent: footsteps (each copy derives them from the puppet's own animation: the
    // walk/run clips' DoFootstep events, read from the clips, with the surface under the puppet), the low-health
    // heartbeat and tinnitus (our own feedback). Sounds of effect objects (SkillFx copies, projectiles) are posted
    // on those objects, never on the character: they're not sent, so nothing plays twice.
    internal class TeamSounds
    {
        private enum Op : byte { Post = 0, HitType = 1 }
        public const int MaxOps = 32;
        private const float StepGap = 0.15f;   // two step events closer than this (layers, blends) are one step
        private const string StepEventName = "DoFootstep";

        public static TeamSounds Instance { get; private set; }

        // what the self-tests read
        public int BatchesSent, PostsSent, BatchesReceived, Played, HitTypesApplied, Dropped, Errors, OwnFootsteps;
        public readonly List<uint> SentLog = new List<uint>();                                    // (test runs only)
        public readonly Dictionary<byte, List<uint>> PlayedLog = new Dictionary<byte, List<uint>>();
        public readonly Dictionary<byte, int> StepsBy = new Dictionary<byte, int>();
        public float LastPlayedToPuppet = -1f;   // emitter to the puppet when the last sound played (puppet shown)

        private readonly Session session;
        private readonly Presence presence;

        // ---- the sender's side ----
        private readonly List<(Op op, uint value)> pending = new List<(Op, uint)>();
        private float pendingTime;
        private int pendingSpace;
        private Vector2 pendingPos;
        private PlayerMaster excludedFor;
        private readonly HashSet<uint> excluded = new HashSet<uint>();
        internal static bool InFootstep;

        // ---- the receivers' side ----
        private class Batch { public byte From; public float Due; public int Space; public Vector2 Pos; public (Op op, uint value)[] Ops; }
        private readonly List<Batch> queue = new List<Batch>();

        private class Emitter
        {
            public GameObject Go;
            public SurfaceDetector Surface;
            public float LastStep = -100f;
            public Animator Anim;
            public int[] Hash;
            public float[] Time;
            public AnimationClip[] Clip;
        }
        private readonly Dictionary<byte, Emitter> emitters = new Dictionary<byte, Emitter>();
        private static readonly List<AnimatorClipInfo> clipInfos = new List<AnimatorClipInfo>();
        private static readonly Dictionary<AnimationClip, float[]> stepTimes = new Dictionary<AnimationClip, float[]>();

        public TeamSounds(Session session, Presence presence)
        {
            Instance = this;
            this.session = session;
            this.presence = presence;
            session.On(Msg.PlayerSounds, OnBatch);
            session.PlayerLeft += p => DropEmitter(p.Id);
            session.Ended += Clear;
        }

        // ---- capture (Harmony patches below) ----

        // The game posted an event on some GameObject: ours to send if it's our character's.
        internal void OnLocalPost(AK.Wwise.Event ev, GameObject on)
        {
            if (InFootstep || on == null || ev == null || !Capturing(on)) return;
            if (!ev.IsValid()) return;   // Post does nothing with it either
            uint id = ev.Id;
            if (excluded.Contains(id)) return;
            Add(Op.Post, id);
            PostsSent++;
            if (Args.Test != null)
            {
                SentLog.Add(id);
                Plugin.Log.LogInfo($"[sounds] sent {ev.Name} ({id})");
            }
        }

        // SfxComponent.PlaySfx(id, hitType) sets the hit-type switch before posting ("Hurt"): the switch goes first.
        internal void OnLocalHitType(GameObject on, DamageHitType type)
        {
            if (InFootstep || on == null || !Capturing(on)) return;
            Add(Op.HitType, (uint)type);
        }

        private bool Capturing(GameObject on)
        {
            if (Session.Current != session || !session.Ready) return false;
            var me = Game.LocalPlayer;
            if (me == null || on != me.gameObject) return false;
            if (excludedFor != me) Exclude(me);
            return true;
        }

        // Our own feedback, not what a teammate would hear from us: the low-health heartbeat and tinnitus.
        private void Exclude(PlayerMaster me)
        {
            excludedFor = me;
            excluded.Clear();
            try
            {
                var list = me.sfxComponent != null ? me.sfxComponent.sfxList : null;
                if (list != null)
                    foreach (var s in list)
                        if (s != null && s.sfxID == "HeartBeat" && s.sfxEvent != null && s.sfxEvent.IsValid()) excluded.Add(s.sfxEvent.Id);
                var hb = me.GetComponent<HeartBeatController>();
                if (hb != null)
                {
                    if (hb.sfxTinnitusPlay != null && hb.sfxTinnitusPlay.IsValid()) excluded.Add(hb.sfxTinnitusPlay.Id);
                    if (hb.sfxTinnitusStop != null && hb.sfxTinnitusStop.IsValid()) excluded.Add(hb.sfxTinnitusStop.Id);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[sounds] reading our own feedback sounds: " + e.Message); }
        }

        private void Add(Op op, uint value)
        {
            if (pending.Count >= MaxOps) return;
            if (pending.Count == 0)
            {
                var me = Game.LocalPlayer;
                pendingTime = Time.realtimeSinceStartup;
                pendingSpace = TeamSpace.Here;
                pendingPos = me != null ? (Vector2)me.transform.position : Vector2.zero;
            }
            pending.Add((op, value));
        }

        private void Flush()
        {
            if (pending.Count == 0) return;
            if (Session.Current == session && session.Ready)
            {
                session.Send(Msg.PlayerSounds, w =>
                {
                    w.Put(pendingTime);
                    w.Put(pendingSpace);
                    w.Put(pendingPos.x); w.Put(pendingPos.y);
                    w.Put((byte)pending.Count);
                    foreach (var (op, value) in pending)
                    {
                        w.Put((byte)op);
                        if (op == Op.Post) w.Put(value); else w.Put((byte)value);
                    }
                });
                BatchesSent++;
            }
            pending.Clear();
        }

        // ---- playing them ----

        private void OnBatch(byte from, NetDataReader r)
        {
            float time = TeamSpace.Finite(r.GetFloat(), 1e7f);
            int space = r.GetInt();
            var pos = new Vector2(TeamSpace.Finite(r.GetFloat(), 100000f), TeamSpace.Finite(r.GetFloat(), 100000f));
            int n = NetGuard.Count(r.GetByte(), r, 2);
            if (n > MaxOps) throw new FormatException("too many sounds in a batch");
            var ops = new (Op, uint)[n];
            for (int i = 0; i < n; i++)
            {
                var op = (Op)r.GetByte();
                if (op == Op.Post) ops[i] = (op, r.GetUInt());
                else if (op == Op.HitType) ops[i] = (op, r.GetByte());
                else throw new FormatException("unknown sound op " + (byte)op);
            }
            BatchesReceived++;
            queue.Add(new Batch { From = from, Due = TeamSpace.DueAt(presence, from, time), Space = space, Pos = pos, Ops = ops });
        }

        public void Update()
        {
            if (Session.Current != session) return;
            Flush();
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < queue.Count; i++)
            {
                var b = queue[i];
                if (b.Due > now) continue;
                queue.RemoveAt(i--);
                Play(b);
            }
            foreach (var kv in emitters)
            {
                if (!presence.Remotes.TryGetValue(kv.Key, out var rp) || !rp.Shown || kv.Value.Go == null) continue;
                kv.Value.Go.transform.position = rp.Puppet.transform.position;   // AkGameObj tells Wwise each frame
                TrackSteps(kv.Key, rp, kv.Value);
            }
            // A teammate whose puppet we see gets an emitter (for its footsteps) before it makes any other sound.
            foreach (var rp in presence.Remotes.Values)
                if (rp.Shown && (!emitters.TryGetValue(rp.Info.Id, out var e) || e.Go == null)) EmitterFor(rp.Info.Id, rp.Puppet.transform.position);
        }

        private void Play(Batch b)
        {
            if (!Game.InGame || b.Space != TeamSpace.Here) { Dropped++; return; }   // another room, or we're loading
            presence.Remotes.TryGetValue(b.From, out var rp);
            bool shown = rp != null && rp.Shown;
            var e = EmitterFor(b.From, shown ? rp.Puppet.transform.position : (Vector3)b.Pos);
            if (e == null) return;
            Place(e, shown ? rp.Puppet.transform.position : new Vector3(b.Pos.x, b.Pos.y, e.Go.transform.position.z));
            foreach (var (op, value) in b.Ops)
            {
                try
                {
                    if (op == Op.HitType)
                    {
                        AkUnitySoundEngine.SetSwitch("SWG_InflictedDamageType", HitTypeSwitch((DamageHitType)value), e.Go);
                        HitTypesApplied++;
                        continue;
                    }
                    AkUnitySoundEngine.PostEvent(value, e.Go);
                    Played++;
                    LastPlayedToPuppet = shown ? Vector2.Distance(e.Go.transform.position, rp.Puppet.transform.position) : -1f;
                    if (Args.Test != null)
                    {
                        if (!PlayedLog.TryGetValue(b.From, out var log)) PlayedLog[b.From] = log = new List<uint>();
                        log.Add(value);
                        Plugin.Log.LogInfo($"[sounds] played {value} for {b.From} ({(shown ? "on the puppet" : "puppet hidden")})");
                    }
                }
                catch (Exception ex)
                {
                    if (Errors++ < 5) Plugin.Log.LogWarning($"[sounds] playing {op} {value}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // The same mapping as the game's SfxComponent.PlaySfx(id, hitType).
        private static string HitTypeSwitch(DamageHitType t) => t switch
        {
            DamageHitType.Cut => "SW_Cut",
            DamageHitType.Magic => "SW_Magic",
            DamageHitType.Arrow => "SW_Arrow",
            DamageHitType.Pierce => "SW_Pierce",
            _ => "SW_Impact",
        };

        // One per teammate, a plain scene object (it goes with the scene): Wwise registers it on its first event.
        // Not the puppet itself, which is switched off while the teammate is hidden (respawning, teleporting in),
        // and those moments have sounds.
        private Emitter EmitterFor(byte id, Vector3 pos)
        {
            if (emitters.TryGetValue(id, out var e) && e.Go != null) return e;
            var me = Game.LocalPlayer;
            if (me == null) return null;
            e = new Emitter { Go = new GameObject("MP_Sounds_" + id) };
            e.Go.transform.position = pos;
            var src = me.GetComponent<SurfaceDetector>();
            if (src != null)
            {
                e.Surface = e.Go.AddComponent<SurfaceDetector>();
                e.Surface.detectionRadius = src.detectionRadius;
                e.Surface.surfaceLayer = src.surfaceLayer;
                e.Surface.fallbackSurface = src.fallbackSurface;
                e.Surface.updateEvery = 1u;
            }
            emitters[id] = e;
            return e;
        }

        // Moves the emitter and tells Wwise at once (AkGameObj only does at its next Update): a sound posted right
        // after a jump (a hidden teammate's respawn) starts where it should.
        private void Place(Emitter e, Vector3 pos)
        {
            e.Go.transform.position = pos;
            try
            {
                if (AkUnitySoundEngine.IsInitialized() && AkUnitySoundEngine.IsGameObjectRegistered(e.Go))
                    AkUnitySoundEngine.SetObjectPosition(e.Go, e.Go.transform);
            }
            catch (Exception) { }
        }

        private void DropEmitter(byte id)
        {
            if (!emitters.TryGetValue(id, out var e)) return;
            emitters.Remove(id);
            if (e.Go == null) return;
            try { if (AkUnitySoundEngine.IsInitialized()) AkUnitySoundEngine.StopAll(e.Go); } catch (Exception) { }
            Object.Destroy(e.Go);
        }

        public void Clear()
        {
            foreach (var id in new List<byte>(emitters.Keys)) DropEmitter(id);
            queue.Clear();
            pending.Clear();
        }

        // ---- footsteps, from the puppet's own animation ----

        // The game's footsteps are animation events (StepsComponent.DoFootstep) on the walk/run clips; the puppet's
        // animator doesn't fire events (they'd call PlayerMaster methods it hasn't got). Its clips are the same and
        // play at the same speed, so each step event the puppet's animation passes plays the step here.
        private void TrackSteps(byte id, RemotePlayer rp, Emitter e)
        {
            var anim = rp.PuppetAnimator;
            if (anim == null || !anim.isActiveAndEnabled) return;
            int layers = anim.layerCount;
            if (e.Anim != anim || e.Hash == null || e.Hash.Length != layers)
            {
                e.Anim = anim;
                e.Hash = new int[layers];
                e.Time = new float[layers];
                e.Clip = new AnimationClip[layers];
            }
            bool step = false;
            for (int l = 0; l < layers; l++)
            {
                if (l > 0 && anim.GetLayerWeight(l) <= 0.01f) { e.Hash[l] = 0; continue; }
                var st = anim.GetCurrentAnimatorStateInfo(l);
                anim.GetCurrentAnimatorClipInfo(l, clipInfos);
                AnimationClip clip = null;
                float weight = 0f;
                foreach (var ci in clipInfos)
                    if (ci.clip != null && ci.weight > weight) { weight = ci.weight; clip = ci.clip; }
                float t = st.normalizedTime;
                // Same state and clip, moving forward by less than half a cycle (a jump is a new state being put in place).
                if (clip != null && e.Hash[l] == st.fullPathHash && e.Clip[l] == clip && t > e.Time[l] && t - e.Time[l] < 0.5f &&
                    Crossed(StepTimes(clip), e.Time[l], t, st.loop))
                    step = true;
                e.Hash[l] = st.fullPathHash;
                e.Time[l] = t;
                e.Clip[l] = clip;
            }
            float now = Time.realtimeSinceStartup;
            if (!step || now - e.LastStep < StepGap) return;
            e.LastStep = now;
            StepsBy[id] = StepsBy.TryGetValue(id, out int c) ? c + 1 : 1;
            var steps = Game.LocalPlayer != null ? Game.LocalPlayer.GetComponent<StepsComponent>() : null;
            if (steps == null || steps.stepEvent == null || !steps.stepEvent.IsValid()) return;
            try
            {
                e.Surface?.SetWwiseSwitch(e.Go);
                AkUnitySoundEngine.PostEvent(steps.stepEvent.Id, e.Go);
            }
            catch (Exception ex)
            {
                if (Errors++ < 5) Plugin.Log.LogWarning($"[sounds] footstep: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Where in its cycle (0..1) a clip has its footsteps.
        public static float[] StepTimes(AnimationClip clip)
        {
            if (stepTimes.TryGetValue(clip, out var times)) return times;
            var list = new List<float>();
            if (clip.length > 0f)
                foreach (var ev in clip.events)
                    if (ev.functionName == StepEventName) list.Add(Mathf.Clamp01(ev.time / clip.length));
            return stepTimes[clip] = list.ToArray();
        }

        // Did the animation pass one of the steps between normalized times t0 and t1 (t1 > t0; looping: every cycle)?
        private static bool Crossed(float[] steps, float t0, float t1, bool loop)
        {
            if (steps.Length == 0) return false;
            int from = loop ? Mathf.FloorToInt(t0) : 0, to = loop ? Mathf.FloorToInt(t1) : 0;
            for (int k = from; k <= to; k++)
                foreach (float s in steps)
                {
                    float x = k + s;
                    if (x > t0 && x <= t1) return true;
                }
            return false;
        }
    }

    // Which room a moment happened in, as one number (sounds and effects only play for players in the same room).
    internal static class TeamSpace
    {
        public static int Key(string scene, string area)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char ch in scene ?? "") h = (h ^ ch) * 16777619;
                h = (h ^ '/') * 16777619;
                foreach (char ch in area ?? "") h = (h ^ ch) * 16777619;
                return (int)h;
            }
        }

        public static int Here => Key(Game.SceneName, Game.AreaName);

        // When something the player `from` did at their time `remoteTime` is due here: with their puppet's delay, never
        // in the past, at most a second ahead.
        public static float DueAt(Presence presence, byte from, float remoteTime)
        {
            float now = Time.realtimeSinceStartup;
            if (presence == null || !presence.Remotes.TryGetValue(from, out var rp)) return now;
            return Mathf.Clamp(rp.LocalTimeOf(remoteTime), now, now + 1f);
        }

        public static float Finite(float v, float limit)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || Math.Abs(v) > limit) throw new FormatException("value out of range");
            return v;
        }
    }

    [HarmonyPatch(typeof(AK.Wwise.Event), nameof(AK.Wwise.Event.Post), new[] { typeof(GameObject) })]
    internal static class PlayerSoundPostPatch
    {
        private static void Prefix(AK.Wwise.Event __instance, GameObject gameObject) => TeamSounds.Instance?.OnLocalPost(__instance, gameObject);
    }

    [HarmonyPatch(typeof(SfxComponent), nameof(SfxComponent.PlaySfx), new[] { typeof(string), typeof(DamageHitType) })]
    internal static class PlayerSoundHitTypePatch
    {
        private static void Prefix(SfxComponent __instance, DamageHitType hitType) => TeamSounds.Instance?.OnLocalHitType(__instance.gameObject, hitType);
    }

    // Our footsteps aren't sent (every copy derives them from the puppet's animation).
    [HarmonyPatch(typeof(StepsComponent), nameof(StepsComponent.DoFootstep))]
    internal static class PlayerFootstepPatch
    {
        private static void Prefix(StepsComponent __instance)
        {
            TeamSounds.InFootstep = true;
            var me = Game.LocalPlayer;
            if (TeamSounds.Instance != null && me != null && __instance.gameObject == me.gameObject) TeamSounds.Instance.OwnFootsteps++;
        }

        private static void Finalizer() => TeamSounds.InFootstep = false;
    }
}
