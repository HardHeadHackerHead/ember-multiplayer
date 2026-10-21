using System.Collections.Generic;
using EmbervilleMP.Net;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // What the party HUD shows about a player besides where they are (that's PlayerState): health, Kymia, level.
    // 9 bytes, sent when they change (at most 5 times a second) and every 2 s, so a lost or late player catches up.
    internal class PartyStatus
    {
        public class Vitals
        {
            public int Health, MaxHealth, Kymia, MaxKymia, Level;

            public float HealthFraction => MaxHealth > 0 ? Mathf.Clamp01((float)Health / MaxHealth) : 0f;
            public float KymiaFraction => MaxKymia > 0 ? Mathf.Clamp01((float)Kymia / MaxKymia) : 0f;

            public bool Same(Vitals o) =>
                o != null && o.Health == Health && o.MaxHealth == MaxHealth && o.Kymia == Kymia && o.MaxKymia == MaxKymia && o.Level == Level;

            public void Write(NetDataWriter w)
            {
                w.Put((ushort)Mathf.Clamp(Health, 0, ushort.MaxValue));
                w.Put((ushort)Mathf.Clamp(MaxHealth, 0, ushort.MaxValue));
                w.Put((ushort)Mathf.Clamp(Kymia, 0, ushort.MaxValue));
                w.Put((ushort)Mathf.Clamp(MaxKymia, 0, ushort.MaxValue));
                w.Put((byte)Mathf.Clamp(Level, 0, 255));
            }

            public static Vitals Read(NetDataReader r) => new Vitals
            {
                Health = r.GetUShort(), MaxHealth = r.GetUShort(), Kymia = r.GetUShort(), MaxKymia = r.GetUShort(), Level = r.GetByte(),
            };

            public override string ToString() => $"hp {Health}/{MaxHealth} kymia {Kymia}/{MaxKymia} lv {Level}";
        }

        public static PartyStatus Instance { get; private set; }

        public readonly Dictionary<byte, Vitals> Players = new Dictionary<byte, Vitals>();
        public int Sent, Received;

        private const float MinInterval = 0.2f, Heartbeat = 2f;
        private readonly Session session;
        private Vitals last;
        private float nextAllowed, nextBeat;
        private bool force = true;

        public PartyStatus(Session session)
        {
            Instance = this;
            this.session = session;
            session.On(Msg.PartyStatus, (from, r) => { Players[from] = Vitals.Read(r); Received++; });
            session.PlayerJoined += _ => force = true;   // the newcomer hears from us at once
            session.Welcomed += () => force = true;
            session.PlayerLeft += p => Players.Remove(p.Id);
        }

        public static Vitals Capture(PlayerMaster p)
        {
            try
            {
                return new Vitals
                {
                    Health = p.HP_Clamped, MaxHealth = p.MaxHealthPoints,
                    Kymia = Mathf.RoundToInt(p.CurrentKymiaPoints), MaxKymia = Mathf.RoundToInt(p.MaxKymiaPoints),
                    Level = p.characterStats != null && p.characterStats.baseClassXP != null ? p.characterStats.baseClassXP.CurrentLevel : 0,
                };
            }
            catch (System.Exception) { return null; }   // stats not set up yet (scene loading)
        }

        public void Update()
        {
            if (!session.Ready || !Game.InGame) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextAllowed) return;
            var v = Capture(Game.LocalPlayer);
            if (v == null) return;
            if (!force && v.Same(last) && now < nextBeat) return;
            session.Send(Msg.PartyStatus, v.Write);
            Sent++;
            last = v;
            force = false;
            nextAllowed = now + MinInterval;
            nextBeat = now + Heartbeat;
        }
    }
}
