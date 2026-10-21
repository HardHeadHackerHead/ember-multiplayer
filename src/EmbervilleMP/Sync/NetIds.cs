using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Stable ids for objects placed in a scene: every copy of the game loads the same scene file,
    // so "scene + path from the root with sibling indices" names the same object everywhere.
    internal static class NetIds
    {
        private static readonly StringBuilder sb = new StringBuilder();
        private static readonly List<Transform> chain = new List<Transform>();

        public static string ScenePath(Transform t)
        {
            chain.Clear();
            for (var x = t; x != null; x = x.parent) chain.Add(x);
            sb.Clear();
            sb.Append(t.gameObject.scene.name);
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var x = chain[i];
                sb.Append('/').Append(x.name).Append('#').Append(SameNameIndex(x));
            }
            return sb.ToString();
        }

        // Index among siblings with the same name: unaffected by objects the game adds at runtime under other names.
        private static int SameNameIndex(Transform t)
        {
            int n = 0;
            if (t.parent == null)
            {
                foreach (var root in t.gameObject.scene.GetRootGameObjects())
                {
                    if (root.transform == t) return n;
                    if (root.name == t.name) n++;
                }
                return n;
            }
            for (int i = 0; i < t.parent.childCount; i++)
            {
                var c = t.parent.GetChild(i);
                if (c == t) return n;
                if (c.name == t.name) n++;
            }
            return n;
        }

        // FNV-1a; top bit cleared (ids with the top bit set are handed out by the host for runtime spawns).
        public static int Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in s) { h ^= c; h *= 16777619; }
                return (int)(h & 0x7FFFFFFF);
            }
        }

        public static bool IsRuntimeSpawned(Transform t)
        {
            for (var x = t; x != null; x = x.parent)
                if (x.name.EndsWith("(Clone)")) return true;
            return false;
        }

        public static int SceneHash(string sceneName) => Hash(sceneName);
    }
}
