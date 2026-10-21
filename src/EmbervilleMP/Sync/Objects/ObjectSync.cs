using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbervilleMP.Net;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync.Objects
{
    // Phase C: shared world objects (doors, switches, chests, gathering nodes...). One framework, one handler per
    // kind of object (see DoorSync for the reference implementation).
    //
    //   - A change made here (by our player, or by the scene's scripts when we run the scene) is sent as the
    //     object's new state: ObjState(scene, kind, id, state). Everyone applies it with the game's own code.
    //   - Every copy remembers the last state of every changed object, per scene ("known" table).
    //   - Someone arriving in a scene gets the known states from whoever runs it (and a new runner from the old).
    //   - The host keeps the table with its world (side file next to the save), so changes made in scenes it
    //     wasn't in are there when it goes there, and next session.
    //
    // Object ids: scene path hash (NetIds), the same on every copy.
    internal abstract class ObjectHandler
    {
        public abstract string Kind { get; }
        // All objects of this kind in the loaded scene (including inactive ones).
        public abstract IEnumerable<Component> FindAll();
        // Writes the object's current state.
        public abstract void Capture(Component obj, NetDataWriter w);
        // Applies a state (ObjectSync.Applying is set: the handler's own hooks must not echo it).
        public abstract void Apply(Component obj, NetDataReader r);
        // Whoever runs the scene: another player asked for something done to this object (see ObjectSync.SendIntent).
        public virtual void OnIntent(Component obj, byte from, NetDataReader r) { }
        // false: the state only lasts while the scene stays loaded (the game resets these objects when a scene
        // loads: breakables, levers, traps...), so it isn't kept for scenes we leave nor saved with the world.
        public virtual bool Persistent => true;
        // Once per session, and every frame in game (for handlers with their own messages or timers).
        public virtual void Init(Session session) { }
        public virtual void Update() { }
    }

    internal class ObjectSync
    {
        public static ObjectSync Instance { get; private set; }
        public static bool Applying;
        // We're the one whose player is acting right now (set by handlers around local interactions).
        public static bool LocalAction;
        public int StatesSent, StatesApplied, SnapshotsSent, SnapshotsApplied;

        private readonly Session session;
        private readonly Dictionary<string, ObjectHandler> handlers = new Dictionary<string, ObjectHandler>();
        // scene -> (kind, id) -> state bytes
        private readonly Dictionary<string, Dictionary<(string kind, int id), byte[]>> known = new Dictionary<string, Dictionary<(string, int), byte[]>>();
        private readonly Dictionary<(string kind, int id), Component> live = new Dictionary<(string, int), Component>();
        private readonly Dictionary<Component, int> idOf = new Dictionary<Component, int>();
        private int liveScene = -1;
        private bool applyKnown;
        private readonly Dictionary<byte, string> lastSceneOf = new Dictionary<byte, string>();
        private byte lastAuthority;

        public ObjectSync(Session session)
        {
            Instance = this;
            this.session = session;
            session.On(Msg.ObjState, OnState);
            session.On(Msg.ObjSnapshot, OnSnapshot);
            // Every ObjectHandler in the mod registers itself (one file per kind of object, nothing else to edit).
            foreach (var t in typeof(ObjectSync).Assembly.GetTypes())
                if (!t.IsAbstract && typeof(ObjectHandler).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
                    Register((ObjectHandler)Activator.CreateInstance(t));
            builtIn = new HashSet<string>(handlers.Keys);
            // Other mods' kinds (Api/MpObjectHandler.cs: registered explicitly, they aren't in our assembly).
            foreach (var h in Api.ApiObjects.All)
            {
                if (!handlers.ContainsKey(h.Kind)) Register(h);
                else if (Api.ApiGuard.Once("kind:" + h.Kind)) Plugin.Log.LogError($"[api] Object kind {h.Kind} is the multiplayer mod's own: the other mod's isn't used");
            }
            foreach (var h in handlers.Values) h.Init(session);
            session.On(Msg.ObjIntent, OnIntent);
            if (session.IsHost) LoadTable();
        }

        public void Register(ObjectHandler h) => handlers[h.Kind] = h;

        // A kind added while a session is up (another mod's, late): the scene's objects are looked up again.
        public void Register(ObjectHandler h, bool rescan)
        {
            if (handlers.ContainsKey(h.Kind)) return;
            Register(h);
            h.Init(session);
            if (rescan) Rescan();
        }

        public void Rescan() => liveScene = -1;

        // Our own kinds' names (other mods can't take them), as the first session registered them. (Not by making
        // handlers here: their constructors take over the static Instance.)
        private static HashSet<string> builtIn;
        public static HashSet<string> BuiltInKinds => builtIn ?? new HashSet<string>();

        public static bool Active => Instance != null && Session.Current != null && Session.Current.Ready;

        // ---------------- local changes ----------------

        // A handler calls this when one of its objects changed here. It's sent if our player did it, or if we run
        // this scene (then the scene's own scripts did it).
        public void Changed(ObjectHandler h, Component obj)
        {
            if (Applying || !session.Ready || obj == null) return;
            if (!LocalAction && !EnemySync.IsAuthority) return;
            EnsureLive();
            int id = IdOf(obj);
            var w = new NetDataWriter();
            h.Capture(obj, w);
            byte[] state = w.CopyData();
            string scene = Game.SceneName;
            Table(scene)[(h.Kind, id)] = state;
            session.Send(Msg.ObjState, x => { x.Put(scene); x.Put(h.Kind); x.Put(id); x.PutBytesWithLength(state); });
            StatesSent++;
        }

        // Asks whoever runs this scene to do something to an object (when the result must be decided in one place:
        // who gets the last item of a chest, a shop's stock...). The handler's OnIntent runs there.
        public void SendIntent(ObjectHandler h, Component obj, Action<NetDataWriter> payload)
        {
            if (!session.Ready || obj == null) return;
            EnsureLive();
            int id = IdOf(obj);
            string scene = Game.SceneName;
            var w = new NetDataWriter();
            payload?.Invoke(w);
            byte[] data = w.CopyData();
            if (EnemySync.IsAuthority) { RunIntent(h.Kind, id, session.LocalId, data); return; }
            session.Send(Msg.ObjIntent, x => { x.Put(scene); x.Put(h.Kind); x.Put(id); x.PutBytesWithLength(data); });
        }

        private void OnIntent(byte from, NetDataReader r)
        {
            string scene = r.GetString(), kind = r.GetString();
            int id = r.GetInt();
            byte[] data = r.GetBytesWithLength();
            if (scene == Game.SceneName && Game.InScene && EnemySync.IsAuthority && Senders.MaybeIn(from, scene)) RunIntent(kind, id, from, data);
        }

        private void RunIntent(string kind, int id, byte from, byte[] data)
        {
            if (!handlers.TryGetValue(kind, out var h)) return;
            EnsureLive();
            if (!live.TryGetValue((kind, id), out var obj) || obj == null) return;
            try { h.OnIntent(obj, from, new NetDataReader(data)); }
            catch (Exception e) { Plugin.Log.LogWarning($"[objects] Intent {kind}#{id} from #{from}: {e.Message}"); }
            SaveSync.Changed(kind, from);   // (a guest's purchase, loot or order: in our world now)
        }

        // The machine running this scene (true when alone too).
        public static bool RunsScene => EnemySync.IsAuthority || Session.Current == null;

        // Our own object lookup, for handlers.
        public Component Find(string kind, int id)
        {
            EnsureLive();
            return live.TryGetValue((kind, id), out var c) ? c : null;
        }

        private void OnState(byte from, NetDataReader r)
        {
            string scene = r.GetString(), kind = r.GetString();
            int id = r.GetInt();
            byte[] state = r.GetBytesWithLength();
            if (!Senders.MaybeIn(from, scene)) return;   // a player elsewhere can't have changed this scene's objects
            if (scene == Game.SceneName && Game.InScene) EnsureLive();
            Remember(scene, kind, id, state);
            if (scene == Game.SceneName && Game.InScene) ApplyOne(kind, id, state);
            SaveSync.Changed(kind, from);
        }

        // Transient kinds are only remembered for the scene we're in. Unknown kinds and junk scene names never are
        // (the host saves this table with its world).
        private bool Keep(string scene, string kind) =>
            handlers.TryGetValue(kind, out var h) && (scene == Game.SceneName || h.Persistent) && Senders.SaneName(scene)
            && (known.ContainsKey(scene) || known.Count < 512);

        private void Remember(string scene, string kind, int id, byte[] state)
        {
            if (!Keep(scene, kind)) return;
            var t = Table(scene);
            if (t.Count < 20000 || t.ContainsKey((kind, id))) t[(kind, id)] = state;   // (a scene has far fewer objects)
        }

        private void ApplyOne(string kind, int id, byte[] state)
        {
            if (!handlers.TryGetValue(kind, out var h)) return;
            EnsureLive();
            if (!live.TryGetValue((kind, id), out var obj) || obj == null)
            {
                Plugin.Log.LogInfo($"[objects] No {kind} #{id} here ({live.Count(k => k.Key.kind == kind)} known)");
                return;
            }
            Applying = true;
            try { h.Apply(obj, new NetDataReader(state)); StatesApplied++; }
            catch (Exception e) { Plugin.Log.LogWarning($"[objects] Apply {kind}#{id}: {e.Message}"); }
            finally { Applying = false; }
        }

        // Spawned objects (Api/ApiSpawns.cs): ids are their net ids, not scene paths, and they come and go mid-scene.
        public void Track(ObjectHandler h, Component c, int id)
        {
            if (c == null || !handlers.ContainsKey(h.Kind)) return;
            EnsureLive();
            live[(h.Kind, id)] = c;
            idOf[c] = id;
            if (known.TryGetValue(Game.SceneName, out var t) && t.TryGetValue((h.Kind, id), out var state)) ApplyOne(h.Kind, id, state);
        }

        // A state heard some other way (a spawned object's, from the host's records): kept, and applied if it's here.
        public void Learn(string scene, string kind, int id, byte[] state)
        {
            Remember(scene, kind, id, state);
            if (scene == Game.SceneName && Game.InScene && live.ContainsKey((kind, id))) ApplyOne(kind, id, state);
        }

        public bool TryGetKnown(string scene, string kind, int id, out byte[] state)
        {
            state = null;
            return known.TryGetValue(scene, out var t) && t.TryGetValue((kind, id), out state);
        }

        // One object gone for good (a despawned one): nothing kept, nothing saved.
        public void ForgetObject(string scene, string kind, int id)
        {
            if (known.TryGetValue(scene, out var t)) t.Remove((kind, id));
            if (live.TryGetValue((kind, id), out var c))
            {
                live.Remove((kind, id));
                if (c != null) idOf.Remove(c);
            }
        }

        // Drops what we remember of one kind of object in a scene (objects the game itself resets on a scene load).
        public void Forget(string scene, string kind)
        {
            if (!known.TryGetValue(scene, out var t)) return;
            foreach (var key in t.Keys.Where(k => k.kind == kind).ToList()) t.Remove(key);
        }

        public IEnumerable<string> KnownScenes => known.Keys;

        // ---------------- per frame ----------------

        public void Update()
        {
            if (!session.Ready || !Game.InScene) return;   // (dead in it, we still run / follow its objects: Game.InScene)
            EnsureLive();
            if (applyKnown)
            {
                // Scene just loaded: put every object we know a state for into that state.
                applyKnown = false;
                if (known.TryGetValue(Game.SceneName, out var table))
                    foreach (var kv in table.ToList()) ApplyOne(kv.Key.kind, kv.Key.id, kv.Value);
            }
            foreach (var h in handlers.Values)
                try { h.Update(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[objects] {h.Kind} update: {e.Message}"); }
            var es = EnemySync.Instance;
            byte authority = es != null ? es.Authority : (byte)0;
            // We run this scene: send what we know to players who just arrived.
            if (EnemySync.IsAuthority && MPRunner.Instance?.Presence != null)
                foreach (var rp in MPRunner.Instance.Presence.Remotes.Values)
                {
                    string scene = rp.Latest?.Scene ?? "";
                    lastSceneOf.TryGetValue(rp.Info.Id, out var before);
                    if (scene != before)
                    {
                        lastSceneOf[rp.Info.Id] = scene;
                        if (scene == Game.SceneName) SendSnapshot(Game.SceneName);
                    }
                }
            // We just handed the scene over: the new runner gets what we know.
            if (lastAuthority == session.LocalId && authority != session.LocalId && authority != 0) SendSnapshot(Game.SceneName);
            lastAuthority = authority;
        }

        private void SendSnapshot(string scene)
        {
            if (scene == Game.SceneName) RefreshModStates();
            if (!known.TryGetValue(scene, out var table) || table.Count == 0) return;
            session.Send(Msg.ObjSnapshot, w =>
            {
                w.Put(scene);
                w.Put(table.Count);
                foreach (var kv in table) { w.Put(kv.Key.kind); w.Put(kv.Key.id); w.PutBytesWithLength(kv.Value); }
            });
            SnapshotsSent++;
        }

        private void OnSnapshot(byte from, NetDataReader r)
        {
            string scene = r.GetString();
            int n = r.GetInt();
            if (scene == Game.SceneName && Game.InScene) EnsureLive();
            for (int i = 0; i < n; i++)
            {
                string kind = r.GetString();
                int id = r.GetInt();
                byte[] state = r.GetBytesWithLength();
                Remember(scene, kind, id, state);
                if (scene == Game.SceneName && Game.InScene) ApplyOne(kind, id, state);
            }
            SnapshotsApplied++;
        }

        // ---------------- ids ----------------

        private Dictionary<(string, int), byte[]> Table(string scene)
        {
            if (!known.TryGetValue(scene, out var t)) known[scene] = t = new Dictionary<(string, int), byte[]>();
            return t;
        }

        // Rebuilds the id -> object map when the scene changed; true if it did. The known states are applied by the
        // next Update (whoever noticed the change first: a handler's hook can run before it).
        private bool EnsureLive()
        {
            int scene = SceneManager.GetActiveScene().handle;
            if (scene == liveScene) return false;
            liveScene = scene;
            applyKnown = true;
            live.Clear();
            idOf.Clear();
            // Transient states of scenes we left are gone (the game resets those objects when it loads them).
            foreach (var t in known)
                if (t.Key != Game.SceneName)
                    foreach (var k in t.Value.Keys.Where(k => !Keep(t.Key, k.kind)).ToList()) t.Value.Remove(k);
            foreach (var h in handlers.Values)
                foreach (var c in h.FindAll())
                {
                    if (c == null) continue;
                    int id = Api.ApiSpawns.NetIdOf(c.gameObject);   // (a mod's spawned object: its net id)
                    if (id == 0)
                    {
                        if (NetIds.IsRuntimeSpawned(c.transform)) continue;
                        id = NetIds.Hash(NetIds.ScenePath(c.transform));
                    }
                    live[(h.Kind, id)] = c;
                    idOf[c] = id;
                }
            return true;
        }

        public int IdOf(Component c)
        {
            if (idOf.TryGetValue(c, out int id)) return id;
            id = Api.ApiSpawns.NetIdOf(c.gameObject);
            if (id == 0) id = NetIds.Hash(NetIds.ScenePath(c.transform));
            idOf[c] = id;
            return id;
        }

        // ---------------- the host keeps the table with its world ----------------

        private static string TablePath => SaveLoad.GetPath(GameData.GetWorldPrefix + "_mp_objects.txt");

        public void SaveTable()
        {
            if (!session.IsHost || GuestWorld.Active || !GameData.HasWorldData) return;
            RefreshModStates();
            try
            {
                var lines = new List<string> { SideFiles.WorldHeader };   // (whose table: see SideFiles)
                foreach (var scene in known)
                    foreach (var kv in scene.Value)
                        if (!handlers.TryGetValue(kv.Key.kind, out var h) || h.Persistent)
                            lines.Add($"{scene.Key}\t{kv.Key.kind}\t{kv.Key.id}\t{Convert.ToBase64String(kv.Value)}");
                Directory.CreateDirectory(Path.GetDirectoryName(TablePath));
                File.WriteAllLines(TablePath, lines);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[objects] Couldn't save the object table: " + e.Message); }
        }

        // Other mods' objects (Api/) in the scene we run: their current states go into the table before it's saved or
        // sent to a player arriving (mods send changes now and then, not every frame: a campfire's time left).
        public void RefreshModStates()
        {
            if (!EnemySync.IsAuthority || !Game.InScene || liveScene != SceneManager.GetActiveScene().handle) return;
            string scene = Game.SceneName;
            foreach (var kv in live.ToList())
                if (kv.Value != null && handlers.TryGetValue(kv.Key.kind, out var h) && h is Api.ApiObjectKind && h.Persistent)
                {
                    var w = new NetDataWriter();
                    h.Capture(kv.Value, w);
                    Table(scene)[kv.Key] = w.CopyData();
                }
        }

        public void LoadTable()
        {
            try
            {
                if (!GameData.HasWorldData || !File.Exists(TablePath)) return;
                var lines = File.ReadAllLines(TablePath);
                if (lines.Length > 0 && !SideFiles.HeaderOk(lines[0], TablePath)) return;
                foreach (var line in lines)
                {
                    var p = line.Split('\t');
                    if (p.Length == 4) Table(p[0])[(p[1], int.Parse(p[2]))] = Convert.FromBase64String(p[3]);
                }
                Plugin.Log.LogInfo($"[objects] Loaded {known.Sum(k => k.Value.Count)} object states");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[objects] Couldn't load the object table: " + e.Message); }
        }
    }

    // The host saves its object table whenever the game saves.
    [HarmonyLib.HarmonyPatch(typeof(GameData), nameof(GameData.SaveData))]
    internal static class ObjectTableSavePatch
    {
        private static void Postfix() => ObjectSync.Instance?.SaveTable();
    }
}
