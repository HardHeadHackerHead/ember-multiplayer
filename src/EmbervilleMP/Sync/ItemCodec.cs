using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace EmbervilleMP.Sync
{
    // Items on the wire: the game's own save format (SavedItem: address key, amount, rolled stats) as JSON,
    // so a chest's contents or a drop look the same on every copy.
    internal static class ItemCodec
    {
        [Serializable]
        private class Wrapper { public List<SavedItem> items = new List<SavedItem>(); }

        private static readonly Dictionary<string, ItemData> byKey = new Dictionary<string, ItemData>();

        public static string Encode(IEnumerable<InventoryItem> items) =>
            JsonUtility.ToJson(new Wrapper { items = (items ?? Enumerable.Empty<InventoryItem>()).Where(i => i?.itemObject?.data != null).Select(i => i.ToSavedItem()).ToList() });

        public static string EncodeOne(ItemObject item) => Encode(new[] { new InventoryItem(item) });

        // Items as a save file keeps them (WorldData's saved chests, key items).
        public static string EncodeSaved(List<SavedItem> items) => JsonUtility.ToJson(new Wrapper { items = items ?? new List<SavedItem>() });

        public static List<InventoryItem> Decode(string json)
        {
            var result = new List<InventoryItem>();
            if (string.IsNullOrEmpty(json)) return result;
            Wrapper w;
            try { w = JsonUtility.FromJson<Wrapper>(json); }
            catch (Exception e) { Plugin.Log.LogWarning("[loot] Bad item list: " + e.Message); return result; }
            foreach (var s in w?.items ?? new List<SavedItem>())
            {
                var data = Find(s.Key);
                if (data == null) { Plugin.Log.LogWarning($"[loot] Unknown item '{s.Key}'"); continue; }
                var o = new ItemObject(data, s.Amount);
                if (s.Values != null) o.randomValues = s.Values;
                o.randomValues.SetData(data);
                result.Add(new InventoryItem(o, s.SlotIndex, s.Section));
            }
            return result;
        }

        public static ItemObject DecodeOne(string json) => Decode(json).FirstOrDefault()?.itemObject;

        // The item asset for an address key: usually already loaded (same scene, same loot tables on every copy).
        public static ItemData Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (byKey.TryGetValue(key, out var d) && d != null) return d;
            foreach (var x in Resources.FindObjectsOfTypeAll<ItemData>())
                if (x != null && !string.IsNullOrEmpty(x.addressKey)) byKey[x.addressKey] = x;
            if (byKey.TryGetValue(key, out d) && d != null) return d;
            try { d = Addressables.LoadAssetAsync<ItemData>(key).WaitForCompletion(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[loot] Couldn't load item '{key}': {e.Message}"); }
            if (d != null) byKey[key] = d;
            return d;
        }
    }
}
