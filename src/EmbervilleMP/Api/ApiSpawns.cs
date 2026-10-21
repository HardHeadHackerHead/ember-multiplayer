using System;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using EmbervilleMP.Sync.Objects;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Api
{
    // Objects mods create at runtime (Multiplayer.Spawn), one copy on every machine with the same net id.
    // Msg.ModSpawn, through the host (it checks, keeps the records, relays):
    //   Request  [scene][key][pos][init]  player -> host -> the scene's runner: please spawn this
    //   Spawn    [scene][id][key][pos][init]  runner -> host -> all: it exists (everyone in that scene makes it)
    //   Despawn  [scene][id]               runner -> host -> all
    //   Unspawn  [scene][id]               player -> host -> the scene's runner: please remove it
    //   ListAsk  [scene]                   player -> host: what's spawned in the scene I just arrived in
    //   List     [scene][n][id key pos init state?]  host -> that player (states of spawned objects with a kind)
    // Every copy keeps the records it has heard of (all scenes) and makes a scene's objects when it loads it; the host's
    // records are the reference (arrivals get them, saved ones go into its world's side file).
    // Ids: top bit set, the spawning player's id in bits 24-30, a counter below (unique when the runner changes).
    internal static class ApiSpawns
    {
        public const byte OpRequest = 1, OpSpawn = 2, OpDespawn = 3, OpUnspawn = 4, OpListAsk = 5, OpList = 6;
        public const int MaxRecords = 4096, MaxInit = 8 * 1024;

        internal class Spawnable
        {
            public string Key, Mod;
            public Func<NetDataReader, GameObject> Create;
            public MpObjectHandler State;
            public bool Saved;
        }

        internal class Record
        {
            public int Id;
            public string Scene, Key;
            public Vector2 Pos;
            public byte[] Init;
        }

        private static readonly Dictionary<string, Spawnable> spawnables = new Dictionary<string, Spawnable>();
        private static readonly Dictionary<int, Record> records = new Dictionary<int, Record>();
        private static readonly Dictionary<int, GameObject> live = new Dictionary<int, GameObject>();
        private static readonly Dictionary<GameObject, int> idOf = new Dictionary<GameObject, int>();
        private static int liveScene = -1;
        private static int counter = new System.Random().Next(0, 0xFFFFFF);
        private static string listedScene;
        private static float listAskedAt;

        public static int Created, Destroyed, RequestsSent, RequestsRun, ListsSent, ListsApplied, Refused;

        public static void Register(string key, string mod, Func<NetDataReader, GameObject> create, MpObjectHandler state, bool saved)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 64) throw new ArgumentException("a spawnable needs a key (64 characters at most)");
            if (create == null) throw new ArgumentNullException(nameof(create));
            if (spawnables.TryGetValue(key, out var old) && old.Mod != mod) throw new InvalidOperationException($"spawnable {key} already belongs to {old.Mod}");
            if (state != null && state.Adapter == null) ApiObjects.Register(state, mod);
            spawnables[key] = new Spawnable { Key = key, Mod = mod, Create = create, State = state, Saved = saved };
        }

        public static int NetIdOf(GameObject go) => go != null && idOf.TryGetValue(go, out int id) ? id : 0;
        public static GameObject Find(int id) => live.TryGetValue(id, out var go) && go != null ? go : null;

        public static IEnumerable<GameObject> LiveOf(MpObjectHandler h)
        {
            foreach (var kv in live)
                if (kv.Value != null && records.TryGetValue(kv.Key, out var rec) && spawnables.TryGetValue(rec.Key, out var sp) && sp.State == h)
                    yield return kv.Value;
        }

        public static IEnumerable<(int id, string scene, string key)> Records => records.Values.Select(r => (r.Id, r.Scene, r.Key));

        // ---------------- the API's calls ----------------

        public static GameObject Spawn(string key, Vector2 pos, Action<NetDataWriter> init)
        {
            if (!ApiGuard.OnMainThread("Multiplayer.Spawn")) return null;
            var s = Session.Current;
            if (s == null || !s.Ready || !Game.InScene || !s.IsHost && !GuestWorld.Active) return null;
            if (!spawnables.TryGetValue(key, out var sp))
            {
                if (ApiGuard.Once("spawn:" + key)) Plugin.Log.LogError($"[api] Spawn: nothing registered as '{key}' (RegisterSpawnable first)");
                return null;
            }
            var w = new NetDataWriter();
            if (init != null && !ApiGuard.Run(sp.Mod, $"spawn {key} init", () => { init(w); return true; }, false)) return null;
            byte[] data = w.CopyData();
            if (data.Length > MaxInit) { Plugin.Log.LogError($"[api] {sp.Mod}: spawn {key}'s init is {data.Length} bytes (at most {MaxInit})"); return null; }
            string scene = Game.SceneName;
            if (EnemySync.IsAuthority) return SpawnHere(s, scene, key, pos, data);
            RequestsSent++;
            s.Send(Msg.ModSpawn, x => { x.Put(OpRequest); x.Put(scene); x.Put(key); x.Put(pos.x); x.Put(pos.y); x.PutBytesWithLength(data); });
            return null;
        }

        // We run the scene: a new id, the object, and everyone hears of it.
        private static GameObject SpawnHere(Session s, string scene, string key, Vector2 pos, byte[] init)
        {
            int id;
            do id = unchecked((int)(0x80000000u | ((uint)(s.LocalId & 0x7F) << 24) | (uint)(counter++ & 0xFFFFFF)));
            while (records.ContainsKey(id));
            var rec = new Record { Id = id, Scene = scene, Key = key, Pos = pos, Init = init };
            if (!Add(rec)) return null;
            Send(s, OpSpawn, rec);
            return Make(rec);
        }

        public static void Despawn(GameObject go)
        {
            if (!ApiGuard.OnMainThread("Multiplayer.Despawn")) return;
            int id = NetIdOf(go);
            var s = Session.Current;
            if (id == 0 || !records.TryGetValue(id, out var rec)) return;
            if (s == null || !s.Ready) return;
            if (EnemySync.IsAuthority && rec.Scene == Game.SceneName) { Remove(rec); Send(s, OpDespawn, rec); }
            else s.Send(Msg.ModSpawn, x => { x.Put(OpUnspawn); x.Put(rec.Scene); x.Put(rec.Id); });
        }

        private static void Send(Session s, byte op, Record rec)
        {
            if (op == OpSpawn) s.Send(Msg.ModSpawn, x => WriteSpawn(x, rec));
            else s.Send(Msg.ModSpawn, x => { x.Put(op); x.Put(rec.Scene); x.Put(rec.Id); });
        }

        private static void WriteSpawn(NetDataWriter x, Record rec)
        {
            x.Put(OpSpawn);
            x.Put(rec.Scene);
            x.Put(rec.Id);
            x.Put(rec.Key);
            x.Put(rec.Pos.x);
            x.Put(rec.Pos.y);
            x.PutBytesWithLength(rec.Init);
        }

        // ---------------- messages ----------------

        public static void OnMessage(Session s, byte from, NetDataReader r)
        {
            byte op = r.GetByte();
            string scene = r.GetString();
            if (!Senders.SaneName(scene)) throw new FormatException("bad scene name");
            switch (op)
            {
                case OpRequest:
                {
                    string key = r.GetString();
                    var pos = new Vector2(Finite(r.GetFloat()), Finite(r.GetFloat()));
                    byte[] init = r.GetBytesWithLength();
                    if (init.Length > MaxInit) throw new FormatException("spawn init too big");
                    if (scene == Game.SceneName && Game.InScene && EnemySync.IsAuthority)
                    {
                        if (!spawnables.ContainsKey(key)) { Refused++; return; }
                        RequestsRun++;
                        SpawnHere(s, scene, key, pos, init);
                    }
                    else if (s.IsHost) ToRunner(s, from, scene, x => { x.Put(OpRequest); x.Put(scene); x.Put(key); x.Put(pos.x); x.Put(pos.y); x.PutBytesWithLength(init); });
                    return;
                }
                case OpUnspawn:
                {
                    int id = r.GetInt();
                    if (scene == Game.SceneName && Game.InScene && EnemySync.IsAuthority)
                    {
                        if (records.TryGetValue(id, out var rec) && rec.Scene == scene) { RequestsRun++; Remove(rec); Send(s, OpDespawn, rec); }
                    }
                    else if (s.IsHost) ToRunner(s, from, scene, x => { x.Put(OpUnspawn); x.Put(scene); x.Put(id); });
                    return;
                }
                case OpSpawn:
                {
                    int id = r.GetInt();
                    var rec = new Record { Id = id, Scene = scene, Key = r.GetString(), Pos = new Vector2(Finite(r.GetFloat()), Finite(r.GetFloat())), Init = r.GetBytesWithLength() };
                    if (rec.Init.Length > MaxInit) throw new FormatException("spawn init too big");
                    // Only the scene's runner spawns, under its own id.
                    if (s.IsHost)
                    {
                        if ((id & 0x80000000) == 0 || ((id >> 24) & 0x7F) != from || !RunnerOf(scene, from)) { Refused++; return; }
                        Relay(s, from, x => WriteSpawn(x, rec));
                    }
                    if (!Add(rec)) return;
                    if (scene == Game.SceneName && Game.InScene && liveScene == SceneManager.GetActiveScene().handle) Make(rec);
                    return;
                }
                case OpDespawn:
                {
                    int id = r.GetInt();
                    if (s.IsHost)
                    {
                        if (!RunnerOf(scene, from)) { Refused++; return; }
                        Relay(s, from, x => { x.Put(OpDespawn); x.Put(scene); x.Put(id); });
                    }
                    if (records.TryGetValue(id, out var rec) && rec.Scene == scene) Remove(rec);
                    return;
                }
                case OpListAsk:
                    if (s.IsHost) SendList(s, from, scene);
                    return;
                case OpList:
                    if (from == Protocol.HostId) ApplyList(r, scene);
                    return;
                default:
                    throw new FormatException("bad spawn op " + op);
            }
        }

        private static float Finite(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || Math.Abs(v) > 100000f) throw new FormatException("bad position");
            return v;
        }

        // Host: is `from` running that scene (or can't we tell)? Lenient, as Senders is: an honest runner never loses.
        private static bool RunnerOf(string scene, byte from)
        {
            byte runner = SceneAuthority.For(scene);
            if (runner == 0 || runner == from) return true;
            var p = Multiplayer.GetPlayer(from);
            return p == null || p.Scene == "" || p.Scene == scene && Senders.IsRunner(from);
        }

        private static void ToRunner(Session s, byte from, string scene, Action<NetDataWriter> body)
        {
            byte runner = SceneAuthority.For(scene);
            if (runner == 0 || runner == s.LocalId || runner == from) return;   // (nobody there: nothing to spawn into)
            s.RelayTo(runner, from, Msg.ModSpawn, body, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        private static void Relay(Session s, byte from, Action<NetDataWriter> body)
        {
            foreach (var id in s.Players.Keys.ToList())
                if (id != s.LocalId && id != from) s.RelayTo(id, from, Msg.ModSpawn, body, LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        private static void SendList(Session s, byte to, string scene)
        {
            var list = records.Values.Where(r => r.Scene == scene).ToList();
            var os = ObjectSync.Instance;
            if (scene == Game.SceneName && EnemySync.IsAuthority) os?.RefreshModStates();   // (their states as they are now)
            s.SendTo(to, Msg.ModSpawn, x =>
            {
                x.Put(OpList);
                x.Put(scene);
                x.Put(list.Count);
                foreach (var rec in list)
                {
                    x.Put(rec.Id);
                    x.Put(rec.Key);
                    x.Put(rec.Pos.x);
                    x.Put(rec.Pos.y);
                    x.PutBytesWithLength(rec.Init);
                    byte[] state = null;
                    var h = spawnables.TryGetValue(rec.Key, out var sp) ? sp.State : null;
                    if (h != null && os != null) os.TryGetKnown(scene, h.Kind, rec.Id, out state);
                    x.Put(state != null);
                    if (state != null) x.PutBytesWithLength(state);
                }
            });
            ListsSent++;
        }

        private static void ApplyList(NetDataReader r, string scene)
        {
            int n = NetGuard.Count(r.GetInt(), r, 18);
            var os = ObjectSync.Instance;
            for (int i = 0; i < n; i++)
            {
                var rec = new Record { Id = r.GetInt(), Scene = scene, Key = r.GetString(), Pos = new Vector2(Finite(r.GetFloat()), Finite(r.GetFloat())), Init = r.GetBytesWithLength() };
                byte[] state = r.GetBool() ? r.GetBytesWithLength() : null;
                var h = spawnables.TryGetValue(rec.Key, out var sp) ? sp.State : null;
                if (state != null && h != null && os != null) os.Learn(scene, h.Kind, rec.Id, state);
                if (records.ContainsKey(rec.Id)) continue;
                if (!Add(rec)) continue;
                if (scene == Game.SceneName && Game.InScene && liveScene == SceneManager.GetActiveScene().handle) Make(rec);
            }
            if (scene == listedScene) listAskedAt = float.MaxValue;   // (answered)
            ListsApplied++;
        }

        // ---------------- this copy ----------------

        private static bool Add(Record rec)
        {
            if (records.Count >= MaxRecords && !records.ContainsKey(rec.Id))
            {
                if (ApiGuard.Once("spawn-full")) Plugin.Log.LogWarning($"[api] {MaxRecords} spawned objects: no more are kept");
                return false;
            }
            records[rec.Id] = rec;
            return true;
        }

        private static void Remove(Record rec)
        {
            records.Remove(rec.Id);
            if (live.TryGetValue(rec.Id, out var go))
            {
                live.Remove(rec.Id);
                if (go != null)
                {
                    idOf.Remove(go);
                    UnityEngine.Object.Destroy(go);
                    Destroyed++;
                }
            }
            var h = spawnables.TryGetValue(rec.Key, out var sp) ? sp.State : null;
            if (h != null) ObjectSync.Instance?.ForgetObject(rec.Scene, h.Kind, rec.Id);
        }

        private static GameObject Make(Record rec)
        {
            if (live.TryGetValue(rec.Id, out var existing) && existing != null) return existing;
            if (!spawnables.TryGetValue(rec.Key, out var sp))
            {
                if (ApiGuard.Once("spawn-unknown:" + rec.Key)) Plugin.Log.LogWarning($"[api] A spawned '{rec.Key}' this copy has no mod for: not shown (logged once)");
                return null;
            }
            var go = ApiGuard.Run(sp.Mod, $"spawn {rec.Key}", () => sp.Create(new NetDataReader(rec.Init)), null);
            if (go == null) return null;
            if (go.transform.parent == null && go.scene != SceneManager.GetActiveScene()) SceneManager.MoveGameObjectToScene(go, SceneManager.GetActiveScene());
            go.transform.position = new Vector3(rec.Pos.x, rec.Pos.y, go.transform.position.z);
            live[rec.Id] = go;
            idOf[go] = rec.Id;
            Created++;
            if (sp.State?.Adapter != null) ObjectSync.Instance?.Track(sp.State.Adapter, go.transform, rec.Id);
            return go;
        }

        // Every frame: a scene loaded makes its spawned objects (and asks the host for any we haven't heard of).
        public static void Update(Session s)
        {
            if (!s.Ready || !Game.InScene) return;
            if (!s.IsHost && (!GuestWorld.Active || GuestWorld.Switching)) return;   // (a guest still in its own world: nothing of the host's here)
            int handle = SceneManager.GetActiveScene().handle;
            if (handle != liveScene)
            {
                liveScene = handle;
                live.Clear();
                foreach (var go in idOf.Keys.ToList()) if (go == null) idOf.Remove(go);
                string scene = Game.SceneName;
                foreach (var rec in records.Values.Where(r => r.Scene == scene).ToList()) Make(rec);
                listedScene = scene;
                listAskedAt = -100f;
            }
            // (asked again until the host answers: a request sent while it was rebuilding its syncs is lost)
            if (!s.IsHost && listAskedAt != float.MaxValue && Time.realtimeSinceStartup - listAskedAt > 4f)
            {
                listAskedAt = Time.realtimeSinceStartup;
                string scene = listedScene;
                s.Send(Msg.ModSpawn, x => { x.Put(OpListAsk); x.Put(scene); });
            }
            foreach (var kv in live.Where(kv => kv.Value == null).ToList()) live.Remove(kv.Key);   // destroyed by its mod
        }

        // ---- the host's world file ----

        public static IEnumerable<Record> Saved() => records.Values.Where(r => spawnables.TryGetValue(r.Key, out var sp) ? sp.Saved : savedUnknown.Contains(r.Id));
        // Records of a mod not loaded right now stay in the file (kept as they were).
        private static readonly HashSet<int> savedUnknown = new HashSet<int>();

        public static void LoadSaved(IEnumerable<Record> saved)
        {
            foreach (var rec in saved)
            {
                if (records.ContainsKey(rec.Id) || !Add(rec)) continue;
                if (!spawnables.ContainsKey(rec.Key)) savedUnknown.Add(rec.Id);
            }
        }

        // The scene's objects are made again from the records (a session on this world again).
        public static void Rescan() => liveScene = -1;

        // The host's session ended: its objects go from the scene; the saved ones' records stay with the world (next
        // session in it), the others end with the session.
        public static void Unshow()
        {
            foreach (var go in live.Values) if (go != null) UnityEngine.Object.Destroy(go);
            live.Clear();
            idOf.Clear();
            liveScene = -1;
            var keep = new HashSet<int>(Saved().Select(r => r.Id));
            foreach (var id in records.Keys.Where(id => !keep.Contains(id)).ToList()) records.Remove(id);
        }

        // A new session, or a guest's world swap: our records are of another world (the host's arrive with the scenes).
        public static void Forget()
        {
            foreach (var go in live.Values) if (go != null) UnityEngine.Object.Destroy(go);
            live.Clear();
            idOf.Clear();
            records.Clear();
            savedUnknown.Clear();
            liveScene = -1;
        }
    }
}
