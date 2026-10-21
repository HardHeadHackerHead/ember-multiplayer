using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // Story props and NPCs the game saves by GUID (GameObjectStateLoader: active state and/or position, written into
    // worldData.savedGameObjects on every save). Nothing in the game announces these changes (cutscenes, dialogue
    // events and room scripts just SetActive/move them), so every copy watches its own and reports a change, wherever
    // it happened: the runner's scripts, or a cutscene/dialogue that only ran on a guest. The host applies it, so its
    // next save stores it; arrivals get it from the object table.
    // Enemy-based NPCs: only their active state (their position is the enemy sync's).
    internal class SavedObjectSync : ObjectHandler
    {
        public static SavedObjectSync Instance { get; private set; }
        public SavedObjectSync()
        {
            Instance = this;
            StationsTicker.Ensure();
            StationsTicker.Tick -= TickStatic;
            StationsTicker.Tick += TickStatic;
        }

        public override string Kind => "savedobj";
        public int ChangesSent, ChangesApplied;

        private struct Seen { public bool Active; public Vector3 Pos; }

        private int sceneHandle = -1;
        private float sceneSince, nextPoll;
        private readonly List<GameObjectStateLoader> all = new List<GameObjectStateLoader>();
        private readonly Dictionary<GameObjectStateLoader, Seen> seen = new Dictionary<GameObjectStateLoader, Seen>();

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<GameObjectStateLoader>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        private static bool SyncsPosition(GameObjectStateLoader g) => g.savePosition && g.GetComponent<Enemy>() == null;

        public override void Capture(Component obj, NetDataWriter w)
        {
            var g = (GameObjectStateLoader)obj;
            w.Put(g.gameObject.activeSelf);
            var p = g.transform.position;
            w.Put(p.x); w.Put(p.y); w.Put(p.z);
        }

        public override void Apply(Component obj, NetDataReader r)
        {
            var g = (GameObjectStateLoader)obj;
            bool active = r.GetBool();
            var p = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            if (SyncsPosition(g) && (g.transform.position - p).sqrMagnitude > 0.0001f) g.transform.position = p;
            if (g.saveActiveState && g.gameObject.activeSelf != active) g.gameObject.SetActive(active);
            seen[g] = new Seen { Active = g.gameObject.activeSelf, Pos = g.transform.position };
            ChangesApplied++;
        }

        private static void TickStatic() => Instance?.Tick();

        private void Tick()
        {
            float now = Time.realtimeSinceStartup;
            int h = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (h != sceneHandle)
            {
                sceneHandle = h;
                sceneSince = now;
                all.Clear();
                seen.Clear();
                all.AddRange(Object.FindObjectsByType<GameObjectStateLoader>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            }
            // Let the game's own load (saved states) settle before watching.
            if (now - sceneSince < 1f || now < nextPoll || all.Count == 0) return;
            nextPoll = now + 0.25f;
            foreach (var g in all)
            {
                if (g == null) continue;
                var cur = new Seen { Active = g.gameObject.activeSelf, Pos = g.transform.position };
                if (!seen.TryGetValue(g, out var was)) { seen[g] = cur; continue; }
                bool changed = (g.saveActiveState && cur.Active != was.Active)
                               || (SyncsPosition(g) && (cur.Pos - was.Pos).sqrMagnitude > 0.09f);
                if (!changed) continue;
                seen[g] = cur;
                // Whatever moved it here (our player's cutscene/dialogue, or our scripts as the runner): tell everyone.
                ObjectSync.LocalAction = true;
                try { ObjectSync.Instance.Changed(this, g); }
                finally { ObjectSync.LocalAction = false; }
                ChangesSent++;
                Plugin.Log.LogInfo($"[objects] Saved object {g.name} changed here: active={cur.Active} pos={cur.Pos}");
            }
        }
    }
}
