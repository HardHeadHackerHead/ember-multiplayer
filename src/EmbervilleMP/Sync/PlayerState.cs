using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Where a player is and what their animator is doing, sent ~20 times a second (less while idle: see StateTrim).
    internal class PlayerState
    {
        public float Time;          // sender's realtime clock
        public string Scene = "";
        public string Area = "";
        public Vector2 Position;
        public bool Flipped;
        public bool Visible = true;
        public bool Dead;
        public int[] LayerStates = new int[0];
        public float[] LayerTimes = new float[0];
        public float[] Floats = new float[0];
        public int FxState;          // the extra effects animator (dash dust, spell flashes), layer 0
        public float FxTime;

        // How it travels (StateTrim decides): the scene/area names only now and then (receivers remember the last
        // ones per player), and animation times / parameters in 16 bits instead of 32.
        public bool WithNames = true;
        public bool Packed;

        private const byte FlipBit = 1, VisibleBit = 2, DeadBit = 4, NamesBit = 8, PackedBit = 16;
        private const int MaxNameLength = 128;
        private const float MaxCoord = 100000f, MaxClock = 1e7f;   // world units; seconds since the sender started

        public void Write(NetDataWriter w)
        {
            w.Put(Time);
            w.Put((byte)((Flipped ? FlipBit : 0) | (Visible ? VisibleBit : 0) | (Dead ? DeadBit : 0) | (WithNames ? NamesBit : 0) | (Packed ? PackedBit : 0)));
            if (WithNames)
            {
                w.Put(Scene);
                w.Put(Area);
            }
            w.Put(Position.x);
            w.Put(Position.y);
            w.Put((byte)LayerStates.Length);
            for (int i = 0; i < LayerStates.Length; i++)
            {
                w.Put(LayerStates[i]);
                PutTime(w, LayerTimes[i]);
            }
            w.Put((byte)Floats.Length);
            foreach (float f in Floats)
                if (Packed) w.Put(Mathf.FloatToHalf(Mathf.Clamp(f, -65000f, 65000f))); else w.Put(f);
            w.Put(FxState);
            PutTime(w, FxTime);
        }

        // Animation times are only used as "where in the loop" (t > 1 wraps), so 0..1 in 1/65535 steps is exact enough.
        private void PutTime(NetDataWriter w, float t)
        {
            if (!Packed) { w.Put(t); return; }
            float x = t > 1f ? t % 1f : Mathf.Max(0f, t);
            w.Put((ushort)Mathf.RoundToInt(Mathf.Clamp01(x) * 65535f));
        }

        private static float GetTime(NetDataReader r, bool packed) => packed ? r.GetUShort() / 65535f : Finite(r.GetFloat(), MaxClock);

        // Bad values (NaN, a position a billion units away) would break the puppet or its clock: the packet is junk.
        private static float Finite(float v, float limit)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || Math.Abs(v) > limit) throw new FormatException("value out of range in a player state");
            return v;
        }

        // Last names heard from each player (states without names use them). Per session.
        private static readonly Dictionary<byte, (float time, string scene, string area)> names = new Dictionary<byte, (float, string, string)>();
        private static object namesSession;

        // Null: a state without names from a player we have no names for yet (one with names comes within a second).
        public static PlayerState Read(NetDataReader r, byte from = 0)
        {
            if (namesSession != Net.Session.Current) { names.Clear(); namesSession = Net.Session.Current; }
            var s = new PlayerState { Time = r.GetFloat() };
            if (s.Time < 0f) throw new FormatException("negative clock in a player state");
            Finite(s.Time, MaxClock);
            byte flags = r.GetByte();
            s.Flipped = (flags & FlipBit) != 0;
            s.Visible = (flags & VisibleBit) != 0;
            s.Dead = (flags & DeadBit) != 0;
            s.WithNames = (flags & NamesBit) != 0;
            s.Packed = (flags & PackedBit) != 0;
            if (s.WithNames)
            {
                s.Scene = r.GetString(MaxNameLength);
                s.Area = r.GetString(MaxNameLength);
            }
            s.Position = new Vector2(Finite(r.GetFloat(), MaxCoord), Finite(r.GetFloat(), MaxCoord));
            int layers = r.GetByte();
            s.LayerStates = new int[layers];
            s.LayerTimes = new float[layers];
            for (int i = 0; i < layers; i++) { s.LayerStates[i] = r.GetInt(); s.LayerTimes[i] = GetTime(r, s.Packed); }
            int floats = r.GetByte();
            s.Floats = new float[floats];
            for (int i = 0; i < floats; i++) s.Floats[i] = s.Packed ? Mathf.HalfToFloat(r.GetUShort()) : Finite(r.GetFloat(), float.MaxValue);
            s.FxState = r.GetInt();
            s.FxTime = GetTime(r, s.Packed);

            // Out of order by a little: keep the newer names. Far older: that player's clock started over (or a new
            // player got the id), take them.
            if (s.WithNames)
            {
                if (!names.TryGetValue(from, out var known) || s.Time >= known.time || known.time - s.Time > 5f)
                    names[from] = (s.Time, s.Scene, s.Area);
            }
            else
            {
                if (!names.TryGetValue(from, out var known)) return null;
                s.Scene = known.scene;
                s.Area = known.area;
            }
            return s;
        }

        // Same pose as another state (times aside): nothing for the others to update.
        public bool SameAs(PlayerState o)
        {
            if (o == null || o.Scene != Scene || o.Area != Area || o.Flipped != Flipped || o.Visible != Visible || o.Dead != Dead || o.FxState != FxState) return false;
            if ((o.Position - Position).sqrMagnitude > 1e-6f) return false;
            if (o.LayerStates.Length != LayerStates.Length || o.Floats.Length != Floats.Length) return false;
            for (int i = 0; i < LayerStates.Length; i++) if (o.LayerStates[i] != LayerStates[i]) return false;
            for (int i = 0; i < Floats.Length; i++) if (Math.Abs(o.Floats[i] - Floats[i]) > 0.01f) return false;
            return true;
        }

        // Reads the local player.
        public static PlayerState Capture(PlayerMaster p, List<int> floatParams)
        {
            var anim = p.animator;
            var s = new PlayerState
            {
                Time = UnityEngine.Time.realtimeSinceStartup,
                Scene = Game.SceneName,
                Area = Game.AreaName,
                Position = p.transform.position,
                Flipped = p.isFliped,
                Dead = p.IsDead,
                Visible = p.artToHide == null || p.artToHide.Count == 0 || p.artToHide[0] == null || p.artToHide[0].activeSelf,
            };
            int layers = anim.layerCount;
            s.LayerStates = new int[layers];
            s.LayerTimes = new float[layers];
            for (int i = 0; i < layers; i++)
            {
                // During a transition report where it's heading, so the copy starts the new move straight away.
                var info = anim.IsInTransition(i) ? anim.GetNextAnimatorStateInfo(i) : anim.GetCurrentAnimatorStateInfo(i);
                s.LayerStates[i] = info.fullPathHash;
                s.LayerTimes[i] = info.normalizedTime;
            }
            s.Floats = new float[floatParams.Count];
            for (int i = 0; i < floatParams.Count; i++) s.Floats[i] = anim.GetFloat(floatParams[i]);
            var fx = p.extraFxAnimator;
            if (fx != null && fx.isActiveAndEnabled)
            {
                var info = fx.IsInTransition(0) ? fx.GetNextAnimatorStateInfo(0) : fx.GetCurrentAnimatorStateInfo(0);
                s.FxState = info.fullPathHash;
                s.FxTime = info.normalizedTime;
            }
            return s;
        }
    }

    // Sender side of the state trims (NetStats.Trim): a player standing still sends 4 states a second instead of 20
    // (each one still complete: a lost one costs nothing), names ride along once a second and for a second after a
    // change, and animation values go packed. Anything that changes (a step, a new animation, a flip) goes at once,
    // and keeps going for a few more states after the last change (so one lost packet doesn't leave the copy behind).
    internal class StateTrim
    {
        public const float IdleInterval = 0.25f, NamesInterval = 1f, Linger = 0.15f;

        private PlayerState lastSent;
        private float lastSentAt = -100f, namesSentAt = -100f, namesChangedAt = -100f, changedAt = -100f;
        private string scene, area;

        // The others heard other names meanwhile (a "between scenes" state): the next states carry ours again.
        public void NamesChanged()
        {
            scene = area = null;
            lastSent = null;
        }

        public bool Due(PlayerState s, float now)
        {
            if (!Net.NetStats.Trim) { s.WithNames = true; s.Packed = false; lastSent = null; scene = area = null; return true; }
            if (!s.SameAs(lastSent)) changedAt = now;
            else if (now - changedAt >= Linger && now - lastSentAt < IdleInterval) return false;
            if (s.Scene != scene || s.Area != area) { scene = s.Scene; area = s.Area; namesChangedAt = now; }
            s.Packed = true;
            s.WithNames = now - namesChangedAt < NamesInterval || now - namesSentAt >= NamesInterval;
            if (s.WithNames) namesSentAt = now;
            lastSent = s;
            lastSentAt = now;
            return true;
        }
    }
}
