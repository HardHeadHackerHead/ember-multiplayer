using System;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using LiteNetLib.Utils;

namespace EmbervilleMP.Api
{
    // The stores' values on this copy. Msg.ModState carries them (see ApiNet):
    //   Set [world?][mod][key][value]: guest -> host (a request), host -> all (applied, in the host's order)
    //   Dump: host -> one guest, every value (joining, and asked again after a world swap)
    internal static class ApiStores
    {
        public const int MaxKey = 64, MaxString = 4096, MaxKeysPerMod = 512, MaxMods = 64;

        internal class Table
        {
            public readonly Dictionary<string, StoreValue> Values = new Dictionary<string, StoreValue>();
        }

        private static readonly Dictionary<string, Table> world = new Dictionary<string, Table>();
        private static readonly Dictionary<string, Table> session = new Dictionary<string, Table>();
        private static readonly Dictionary<(bool, string), MpStore> stores = new Dictionary<(bool, string), MpStore>();
        // Host: which world the world tables are of (kept in memory across sessions in the same world: newer than the file).
        public static string WorldKey;
        public static int SetsApplied, SetsRefused;

        public static MpStore Get(bool worldStore, string mod)
        {
            if (!stores.TryGetValue((worldStore, mod), out var s)) stores[(worldStore, mod)] = s = new MpStore(mod, worldStore);
            return s;
        }

        public static Table TableOf(bool worldStore, string mod, bool create)
        {
            var d = worldStore ? world : session;
            if (d.TryGetValue(mod, out var t) || !create) return t;
            if (d.Count >= MaxMods) return null;
            return d[mod] = new Table();
        }

        public static void Set(MpStore store, string key, StoreValue v)
        {
            if (!ApiGuard.OnMainThread("MpStore.Set")) return;
            var s = Session.Current;
            if (s == null || !s.Ready) return;
            if (!Valid(key, v))
            {
                if (ApiGuard.Once("store:" + store.ModId + ":" + key)) Plugin.Log.LogError($"[api] {store.ModId}: {store} key '{key}' or its value is too long (keys {MaxKey}, strings {MaxString}): not set");
                return;
            }
            if (s.IsHost) Apply(s, store.SavedWithWorld, store.ModId, key, v, broadcast: true);
            else s.Send(Msg.ModState, w => WriteSet(w, store.SavedWithWorld, store.ModId, key, v));
        }

        private static bool Valid(string key, StoreValue v) =>
            !string.IsNullOrEmpty(key) && key.Length <= MaxKey && !key.Any(char.IsControl) && (v.Type != StoreValue.Str || (v.S ?? "").Length <= MaxString);

        public static void WriteSet(NetDataWriter w, bool worldStore, string mod, string key, StoreValue v)
        {
            w.Put(ApiNet.OpStoreSet);
            w.Put(worldStore);
            w.Put(mod);
            w.Put(key);
            v.Write(w);
        }

        // Host: a guest's Set (checked), or the host's own. Everyone hears the result.
        public static void OnSet(Session s, byte from, NetDataReader r)
        {
            bool worldStore = r.GetBool();
            string mod = r.GetString(), key = r.GetString();
            var v = StoreValue.Read(r);
            if (s.IsHost)
            {
                if (mod.Length == 0 || mod.Length > 128 || mod.Any(char.IsControl) || !Valid(key, v)) { SetsRefused++; return; }
                Apply(s, worldStore, mod, key, v, broadcast: true);
            }
            else if (from == Protocol.HostId) Apply(s, worldStore, mod, key, v, broadcast: false);
        }

        private static void Apply(Session s, bool worldStore, string mod, string key, StoreValue v, bool broadcast)
        {
            var t = TableOf(worldStore, mod, create: v.Type != StoreValue.None);
            if (t == null) { SetsRefused++; return; }
            bool had = t.Values.TryGetValue(key, out var old);
            if (v.Type == StoreValue.None)
            {
                if (!had) return;
                t.Values.Remove(key);
            }
            else
            {
                if (had && old.Same(v)) return;
                if (!had && t.Values.Count >= MaxKeysPerMod) { SetsRefused++; return; }
                t.Values[key] = v;
            }
            SetsApplied++;
            if (broadcast && s.IsHost) s.Send(Msg.ModState, w => WriteSet(w, worldStore, mod, key, v));
            if (stores.TryGetValue((worldStore, mod), out var store)) store.RaiseChanged(key);
        }

        // ---- dumps ----

        public static void WriteDump(NetDataWriter w)
        {
            foreach (var d in new[] { world, session })
            {
                w.Put(d.Count);
                foreach (var kv in d)
                {
                    w.Put(kv.Key);
                    w.Put(kv.Value.Values.Count);
                    foreach (var e in kv.Value.Values) { w.Put(e.Key); e.Value.Write(w); }
                }
            }
        }

        // Guest: the host's values replace ours (each changed key is announced).
        public static void ReadDump(NetDataReader r)
        {
            foreach (bool worldStore in new[] { true, false })
            {
                var d = worldStore ? world : session;
                int mods = NetGuard.Count(r.GetInt(), r, 8);
                var got = new Dictionary<string, Table>();
                for (int i = 0; i < mods; i++)
                {
                    string mod = r.GetString();
                    int n = NetGuard.Count(r.GetInt(), r, 3);
                    var t = new Table();
                    for (int k = 0; k < n; k++) { string key = r.GetString(); t.Values[key] = StoreValue.Read(r); }
                    got[mod] = t;
                }
                var changed = new List<(string mod, string key)>();
                foreach (var kv in got)
                    foreach (var e in kv.Value.Values)
                        if (!d.TryGetValue(kv.Key, out var mine) || !mine.Values.TryGetValue(e.Key, out var v) || !v.Same(e.Value)) changed.Add((kv.Key, e.Key));
                foreach (var kv in d)
                    foreach (var key in kv.Value.Values.Keys)
                        if (!got.TryGetValue(kv.Key, out var theirs) || !theirs.Values.ContainsKey(key)) changed.Add((kv.Key, key));
                d.Clear();
                foreach (var kv in got) d[kv.Key] = kv.Value;
                foreach (var (mod, key) in changed)
                    if (stores.TryGetValue((worldStore, mod), out var store)) store.RaiseChanged(key);
            }
        }

        // ---- the host's world file (ApiSideFile) ----

        public static IEnumerable<(string mod, string key, StoreValue v)> WorldEntries() =>
            world.SelectMany(t => t.Value.Values.Select(e => (t.Key, e.Key, e.Value)));

        public static void LoadWorld(IEnumerable<(string mod, string key, StoreValue v)> entries)
        {
            world.Clear();
            foreach (var (mod, key, v) in entries)
            {
                var t = TableOf(true, mod, create: true);
                if (t != null && t.Values.Count < MaxKeysPerMod) t.Values[key] = v;
            }
        }

        // A session's end: its values go; a guest's world values too (they were the host's).
        public static void OnSessionEnded(bool wasHost)
        {
            var gone = new List<(bool, string, string)>();
            foreach (var kv in session) foreach (var key in kv.Value.Values.Keys) gone.Add((false, kv.Key, key));
            session.Clear();
            if (!wasHost)
            {
                foreach (var kv in world) foreach (var key in kv.Value.Values.Keys) gone.Add((true, kv.Key, key));
                world.Clear();
                WorldKey = null;
            }
            foreach (var (w, mod, key) in gone)
                if (stores.TryGetValue((w, mod), out var store)) store.RaiseChanged(key);
        }


        public static int Count(bool worldStore) => (worldStore ? world : session).Sum(t => t.Value.Values.Count);
    }
}
