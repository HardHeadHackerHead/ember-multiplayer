using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Api;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP
{
    // TEST ONLY (--mp-test): a tiny "mod" inside our own DLL that uses the public API the way another mod would, so the
    // api-* scenarios (SelfTest.Api.cs) don't need Party Tricks. Registered on every copy before any session. Its id is
    // "mptest"; its fake declared mods (ApiMods.TestMods) are what api-modcheck hides on the guest.
    internal static class ApiTestMod
    {
        public const string Mod = "mptest";
        public static MpChannel Ch, HostOnly, Limited, Throwing;
        public static readonly List<(byte from, string text)> Got = new List<(byte, string)>();
        public static int GotLimited, GotThrowing;
        public static BoxKind Boxes;
        public static OrbState Orbs;
        public static BadKind Bad;
        public static bool BoxesWanted;
        private static bool done;

        public static void Register()
        {
            if (done) return;
            done = true;
            Ch = ApiChannels.Register("mptest.ch", Mod, new ChannelOptions { MaxSize = 2048, Rate = 50f, Burst = 100f });
            HostOnly = ApiChannels.Register("mptest.hostonly", Mod, new ChannelOptions { WhoMaySend = WhoMaySend.HostOnly });
            Limited = ApiChannels.Register("mptest.limited", Mod, new ChannelOptions { MaxSize = 64, Rate = 2f, Burst = 3f });
            Throwing = ApiChannels.Register("mptest.throw", Mod, new ChannelOptions());
            Ch.Received += (from, r) => Got.Add((from.Id, r.GetString()));
            HostOnly.Received += (from, r) => Got.Add((from.Id, "hostonly:" + r.GetString()));
            Limited.Received += (from, r) => GotLimited++;
            // (the first handler throws, the second still hears every message)
            Throwing.Received += (from, r) => throw new System.InvalidOperationException("mptest: a handler that throws");
            Throwing.Received += (from, r) => GotThrowing++;

            Boxes = new BoxKind();
            ApiObjects.Register(Boxes, Mod);
            Orbs = new OrbState();
            ApiSpawns.Register("mptest.orb", Mod, MakeOrb, Orbs, saved: false);
            ApiSpawns.Register("mptest.relic", Mod, MakeOrb, Orbs, saved: true);
            ApiSpawns.Register("mptest.badorb", Mod, r => throw new System.InvalidOperationException("mptest: a spawn factory that throws"), null, saved: false);
            Bad = new BadKind();
            ApiObjects.Register(Bad, Mod);

            ApiMods.TestMods.Add(new MpMod("mptest.everyone", "MP Test Everyone", "1.0.0", Requirement.Everyone));
            ApiMods.TestMods.Add(new MpMod("mptest.hostonly", "MP Test HostOnly", "1.0.0", Requirement.HostOnly));
            ApiMods.TestMods.Add(new MpMod("mptest.clientonly", "MP Test ClientOnly", "1.0.0", Requirement.ClientOnly));
            ApiMods.TestMods.Add(new MpMod("mptest.incompatible", "MP Test Incompatible", "1.0.0", Requirement.Incompatible));
        }

        private static GameObject MakeOrb(NetDataReader r)
        {
            var go = new GameObject("MPTestOrb_" + r.GetString());
            go.AddComponent<OrbTag>().Value = 0;
            return go;
        }

        internal class OrbTag : MonoBehaviour { public int Value; }

        // A spawned orb's state: one number.
        internal class OrbState : MpObjectHandler
        {
            public override string Kind => "mptest.orbstate";
            public override void Capture(GameObject obj, NetDataWriter w) => w.Put(obj.GetComponent<OrbTag>().Value);
            public override void Apply(GameObject obj, NetDataReader r) => obj.GetComponent<OrbTag>().Value = r.GetInt();
            public override void OnIntent(GameObject obj, MpPlayer from, NetDataReader r)
            {
                obj.GetComponent<OrbTag>().Value += r.GetInt();
                Multiplayer.ObjectChanged(this, obj);
            }
        }

        // Two boxes placed in the scene (same path on every copy), with a number and who last asked for a change.
        internal class BoxKind : MpObjectHandler
        {
            public override string Kind => "mptest.box";
            public readonly Dictionary<string, int> Values = new Dictionary<string, int>();
            public byte LastIntentFrom;

            public override IEnumerable<GameObject> FindAll()
            {
                if (!BoxesWanted) yield break;
                var root = GameObject.Find("MPTestBoxes");
                if (root == null)
                {
                    root = new GameObject("MPTestBoxes");
                    for (int i = 1; i <= 2; i++) new GameObject("Box" + i).transform.SetParent(root.transform, false);
                }
                foreach (Transform t in root.transform) yield return t.gameObject;
            }

            public int Get(string box) => Values.TryGetValue(box, out int v) ? v : 0;
            public GameObject Box(string box) => FindAll().FirstOrDefault(g => g.name == box);

            public override void Capture(GameObject obj, NetDataWriter w) => w.Put(Get(obj.name));
            public override void Apply(GameObject obj, NetDataReader r) => Values[obj.name] = r.GetInt();
            public override void OnIntent(GameObject obj, MpPlayer from, NetDataReader r)
            {
                LastIntentFrom = from.Id;
                Values[obj.name] = Get(obj.name) + r.GetInt();
                Multiplayer.ObjectChanged(this, obj);
            }
        }

        // A kind whose Apply throws (api-isolation).
        internal class BadKind : MpObjectHandler
        {
            public override string Kind => "mptest.bad";
            public override IEnumerable<GameObject> FindAll()
            {
                if (!BoxesWanted) yield break;
                var go = GameObject.Find("MPTestBad") ?? new GameObject("MPTestBad");
                yield return go;
            }
            public override void Capture(GameObject obj, NetDataWriter w) => w.Put(1);
            public override void Apply(GameObject obj, NetDataReader r) => throw new System.InvalidOperationException("mptest: an Apply that throws");
        }
    }
}
