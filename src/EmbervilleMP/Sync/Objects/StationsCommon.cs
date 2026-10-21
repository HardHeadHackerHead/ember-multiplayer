using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace EmbervilleMP.Sync.Objects
{
    // Items on the wire: the game's own identity (addressable key) + its rolled values (as the save files keep them).
    internal static class StationItemCodec
    {
        private static readonly Dictionary<string, ItemData> byKey = new Dictionary<string, ItemData>();
        private static bool scanned;

        public static void Write(NetDataWriter w, ItemObject item, int amount)
        {
            w.Put(item.data.addressKey ?? "");
            w.Put(amount);
            w.Put(JsonUtility.ToJson(item.randomValues ?? new ItemRandomValues()));
        }

        // Null if the item's data can't be found here.
        public static ItemObject Read(NetDataReader r)
        {
            string key = r.GetString();
            int amount = r.GetInt();
            string json = r.GetString();
            var data = Data(key);
            if (data == null) { Plugin.Log.LogWarning($"[objects] Unknown item {key}"); return null; }
            var values = JsonUtility.FromJson<ItemRandomValues>(json) ?? new ItemRandomValues();
            var o = new ItemObject(data, amount) { randomValues = values };
            values.SetData(data);
            return o;
        }

        // Same item (data and rolled values), whatever the amount.
        public static string Key(ItemObject o) => (o.data?.addressKey ?? "") + "|" + JsonUtility.ToJson(o.randomValues);

        public static ItemData Data(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (byKey.TryGetValue(key, out var d) && d != null) return d;
            // Loaded assets first (everything the scene references), then the game's own addressables (as its loader does).
            if (!scanned || d == null)
            {
                scanned = true;
                foreach (var x in Resources.FindObjectsOfTypeAll<ItemData>())
                    if (!string.IsNullOrEmpty(x.addressKey)) byKey[x.addressKey] = x;
                if (byKey.TryGetValue(key, out d) && d != null) return d;
            }
            try { d = Addressables.LoadAssetAsync<ItemData>(key).WaitForCompletion(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[objects] Load item {key}: {e.Message}"); }
            if (d != null) byKey[key] = d;
            return d;
        }

        // Gives an item to our own character (what the game does when you collect one): bag first, the ground if full.
        public static void Give(ItemObject item, Vector3 dropAt)
        {
            var inv = Singleton<Inventory>.main;
            int left = inv.AddItem(item);
            if (left > 0) item.DropToWorld(dropAt, left, 1.2f);
            inv.UpdateEquippedList();
            inv.SetInventoryChanged();
        }
    }

    // Per-frame work for the station handlers (the object framework has no tick for handlers).
    internal class StationsTicker : MonoBehaviour
    {
        private static StationsTicker instance;
        public static event Action Tick;

        public static void Ensure()
        {
            if (instance != null) return;
            var go = new GameObject("EmbervilleMP.StationsTicker");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            instance = go.AddComponent<StationsTicker>();
        }

        private void Update()
        {
            if (!ObjectSync.Active || !Game.InGame) return;
            try { Tick?.Invoke(); }
            catch (Exception e) { Plugin.Log.LogWarning("[objects] Station tick: " + e); }
        }
    }

    // Results decided by the scene's runner travel inside the object's state ("player P's request N: here you go").
    // Only the machine that asked, and only once, acts on it (states are replayed to late arrivals).
    internal static class Requests
    {
        private static int next = UnityEngine.Random.Range(1, int.MaxValue / 2);
        public static int NewNonce() => next++;
        public static byte Me => Net.Session.Current != null ? Net.Session.Current.LocalId : (byte)0;
    }
}
