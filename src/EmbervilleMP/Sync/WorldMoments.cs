using System;
using System.Collections.Generic;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace EmbervilleMP.Sync
{
    // One-off reactions of world objects that only the acting player's copy sees, replayed for everyone in the scene:
    //   - a training dummy hit by our character (the others' copies of our attacks hit nothing): its hit animation,
    //     sound, particles, damage number and bar;
    //   - a recycler run (RecyclingStation.CheckOnClose after our recycling panel closed): its animation and sound.
    // Nothing here is state: an arrival sees the objects as they are.
    internal static class WorldMoments
    {
        public static int Sent, Received, Played;
        public static byte LastKind;
        public static int LastId;

        public const byte Dummy = 0, Recycler = 1;
        private static Session session;
        private static bool applying;
        private static readonly Dictionary<(Type, int), Component> found = new Dictionary<(Type, int), Component>();
        private static int foundScene = -1;

        public static void Hook(Session s)
        {
            session = s;
            s.On(Msg.WorldMoment, OnMoment);
        }

        private static bool Live => session != null && session.Ready && Session.Current == session && Game.InGame;

        // TestDummy.ApplyDamage, before it runs: our character's hit.
        internal static void OnDummyHit(TestDummy d, DamageData dd)
        {
            if (applying || !Live || d == null || NetIds.IsRuntimeSpawned(d.transform)) return;
            var me = Game.LocalPlayer;
            if (me == null || !ReferenceEquals(dd.Aggressor, me)) return;   // effect ticks and others' hits: theirs
            Send(Dummy, d.transform, w => { w.Put(dd.Amount); w.Put((byte)dd.Result); });
        }

        // RecyclingStation.CheckOnClose(true): our recycling panel closed with something recycled.
        internal static void OnRecycle(RecyclingStation st)
        {
            if (applying || !Live || st == null || NetIds.IsRuntimeSpawned(st.transform)) return;
            Send(Recycler, st.transform, null);
        }

        private static void Send(byte kind, Transform t, Action<NetDataWriter> extra)
        {
            string scene = Game.SceneName;
            int id = NetIds.Hash(NetIds.ScenePath(t));
            session.Send(Msg.WorldMoment, w => { w.Put(kind); w.Put(scene); w.Put(id); extra?.Invoke(w); });
            Sent++;
        }

        private static void OnMoment(byte from, NetDataReader r)
        {
            byte kind = r.GetByte();
            string scene = r.GetString();
            int id = r.GetInt();
            if (scene != Game.SceneName || !Game.InGame || !Senders.MaybeIn(from, scene)) return;
            Received++;
            LastKind = kind;
            LastId = id;
            applying = true;
            try
            {
                if (kind == Dummy)
                {
                    int amount = r.GetInt();
                    var result = (DamageResult)r.GetByte();
                    var d = Find<TestDummy>(id);
                    // (its Start must have run: a dummy in a room nobody has loaded here has no body or stats yet)
                    if (d == null || !d.isActiveAndEnabled || d.rb == null || d.characterStats == null) return;
                    d.ApplyDamage(new DamageData { Amount = Mathf.Clamp(amount, 0, 1000000), Result = result });
                    Played++;
                }
                else if (kind == Recycler)
                {
                    var st = Find<RecyclingStation>(id);
                    if (st == null || !st.isActiveAndEnabled) return;
                    if (st.animator != null) st.animator.SetTrigger("Play");
                    st.sfxOnRecycleStart?.Post(st.gameObject);
                    Played++;
                }
            }
            finally { applying = false; }
        }

        private static T Find<T>(int id) where T : Component
        {
            int scene = SceneManager.GetActiveScene().handle;
            if (scene != foundScene) { foundScene = scene; found.Clear(); }
            if (found.TryGetValue((typeof(T), id), out var c) && c != null) return (T)c;
            foreach (var x in Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (!NetIds.IsRuntimeSpawned(x.transform) && NetIds.Hash(NetIds.ScenePath(x.transform)) == id)
                {
                    found[(typeof(T), id)] = x;
                    return x;
                }
            return null;
        }
    }

    [HarmonyPatch(typeof(TestDummy), nameof(TestDummy.ApplyDamage))]
    internal static class DummyHitPatch
    {
        private static void Prefix(TestDummy __instance, DamageData damageData) => WorldMoments.OnDummyHit(__instance, damageData);
    }

    [HarmonyPatch(typeof(RecyclingStation), nameof(RecyclingStation.CheckOnClose))]
    internal static class RecyclerPlayPatch
    {
        private static void Postfix(RecyclingStation __instance, bool value)
        {
            if (value) WorldMoments.OnRecycle(__instance);
        }
    }
}
