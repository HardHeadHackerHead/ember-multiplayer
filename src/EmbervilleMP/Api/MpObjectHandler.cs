using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync.Objects;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Api
{
    // A kind of world object a mod shares (Multiplayer.RegisterObjectKind), the same framework as the mod's own doors
    // and chests (ObjectSync): a change is sent as the object's new state, decisions are intents to the scene's
    // runner, late arrivals and a new runner get the states, and the host keeps them with its world (scenes nobody is
    // in, next session). Objects are told apart by their scene path (the same on every copy: give them unique names),
    // or, for objects made by Multiplayer.Spawn, by their net id.
    public abstract class MpObjectHandler
    {
        // Short and unique ("mymod.lamp"): it's the object's type on the wire.
        public abstract string Kind { get; }
        // Every object of this kind in the loaded scene (inactive ones too). Spawned objects registered with this
        // handler (RegisterSpawnable) are added by the framework: no need to return them.
        public virtual IEnumerable<GameObject> FindAll() => Enumerable.Empty<GameObject>();
        // Write the object's state.
        public abstract void Capture(GameObject obj, NetDataWriter w);
        // Put the object in that state (Multiplayer.ObjectChanged calls made from here are ignored: no echo).
        public abstract void Apply(GameObject obj, NetDataReader r);
        // Runs on the scene's runner: a player asked for something (Multiplayer.SendIntent). Decide, change the
        // object, call Multiplayer.ObjectChanged.
        public virtual void OnIntent(GameObject obj, MpPlayer from, NetDataReader r) { }
        // false: the state only lasts while the scene is loaded (not kept for scenes nobody is in, nor saved).
        public virtual bool Persistent => true;

        internal ApiObjectKind Adapter;
        internal string Mod;
    }

    // The framework's side of a mod's object kind.
    internal class ApiObjectKind : ObjectHandler
    {
        public readonly MpObjectHandler Handler;
        private readonly string kind;

        public ApiObjectKind(MpObjectHandler h, string kind)
        {
            Handler = h;
            this.kind = kind;
        }

        public override string Kind => kind;
        public override bool Persistent => ApiGuard.Run(Handler.Mod, $"{kind}.Persistent", () => Handler.Persistent, true);

        public override IEnumerable<Component> FindAll()
        {
            var found = ApiGuard.Run(Handler.Mod, $"{kind}.FindAll", () => (Handler.FindAll() ?? Enumerable.Empty<GameObject>()).ToList(), new List<GameObject>());
            foreach (var go in found.Concat(ApiSpawns.LiveOf(Handler)).Distinct())
                if (go != null) yield return go.transform;
        }

        public override void Capture(Component obj, NetDataWriter w) =>
            ApiGuard.Run(Handler.Mod, $"{kind}.Capture", () => Handler.Capture(obj.gameObject, w));

        public override void Apply(Component obj, NetDataReader r) =>
            ApiGuard.Run(Handler.Mod, $"{kind}.Apply", () => Handler.Apply(obj.gameObject, r));

        public override void OnIntent(Component obj, byte from, NetDataReader r)
        {
            var p = Multiplayer.GetPlayer(from);
            if (p == null) return;
            ApiGuard.Run(Handler.Mod, $"{kind}.OnIntent", () => Handler.OnIntent(obj.gameObject, p, r));
        }
    }

    internal static class ApiObjects
    {
        private static readonly Dictionary<string, ApiObjectKind> kinds = new Dictionary<string, ApiObjectKind>();
        public static IEnumerable<ObjectHandler> All => kinds.Values;

        public static void Register(MpObjectHandler h, string mod)
        {
            string kind = h.Kind;
            if (string.IsNullOrEmpty(kind) || kind.Length > 64) throw new System.ArgumentException("an object kind needs a short Kind (64 characters at most)");
            if (h.Adapter != null) return;
            if (kinds.ContainsKey(kind) || ObjectSync.BuiltInKinds.Contains(kind)) throw new System.InvalidOperationException($"object kind {kind} is taken: pick another name");
            h.Mod = mod;
            h.Adapter = new ApiObjectKind(h, kind);
            kinds[kind] = h.Adapter;
            ObjectSync.Instance?.Register(h.Adapter, rescan: true);   // (a session up already)
        }

        public static void Changed(MpObjectHandler h, GameObject obj, bool byLocalPlayer)
        {
            var os = ObjectSync.Instance;
            if (h?.Adapter == null || obj == null || os == null || ObjectSync.Applying) return;
            bool was = ObjectSync.LocalAction;
            ObjectSync.LocalAction = was || byLocalPlayer;
            try { os.Changed(h.Adapter, obj.transform); }
            finally { ObjectSync.LocalAction = was; }
        }

        public static void Intent(MpObjectHandler h, GameObject obj, System.Action<NetDataWriter> write)
        {
            var os = ObjectSync.Instance;
            if (h?.Adapter == null || obj == null || os == null || Session.Current == null) return;
            os.SendIntent(h.Adapter, obj.transform, w => ApiGuard.Run(h.Mod, $"{h.Kind} intent", () => write?.Invoke(w)));
        }
    }
}
