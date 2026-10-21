using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbervilleMP.Net;
using EmbervilleMP.Sync.Objects;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EmbervilleMP.Sync
{
    // Character rewards outside quests: items, known items and classes given by dialogue, lore pages, triggers and
    // tutorial windows (custom events "Add to Inventory", "Unlock Player Class", "Set Item as Known"), and by cutscene
    // graphs (actions "Give Player item", "Give Player Class", "Add XP to PlayerClass"). The game gives them to whoever's
    // copy runs the script; the story flags that gate them are shared, so nobody else could ever get them. In co-op they
    // go like quest rewards (QuestRewards): when one fires on a copy that plays the session's world, every player in
    // that world gets it, once per character and world.
    //   - A reward is keyed on where it came from (the dialogue, page, trigger, tutorial or graph: ScriptScope's source,
    //     else the scene) and what it gives. What this character already got is in QuestRewards' record (saved with it).
    //   - Fired here: the game's own code runs if the key is new to us, nothing if we had it (a cutscene that plays on
    //     several copies runs the same action on each); then it goes to the others.
    //   - Received: granted to our character if the key is new. A copy not in the session's world yet (a guest still in
    //     its own world, or loading) keeps it for a minute.
    // A reward a script means to give again and again from the same place is given once per character and world.
    internal static class CharacterRewards
    {
        public static int Fired, Skipped, Received, Granted;
        public static int Suppress;   // > 0: the game's own grant runs untouched (quest rewards, our own apply)
        private static Session session;
        private static readonly List<(string key, byte kind, byte[] data, float at)> pending = new List<(string, byte, byte[], float)>();

        public const byte Items = 1, KnownItems = 2, Classes = 3, GiveClass = 4, Xp = 5;

        public static void Hook(Session s)
        {
            session = s;
            pending.Clear();
            s.On(Msg.CharReward, OnReward);
        }

        public static bool Active =>
            Suppress == 0 && session != null && session == Session.Current && session.Ready && WorldState.InSharedWorld &&
            !GuestWorld.Switching && GameData.HasPlayerData && GameData.HasWorldData;

        private static bool CanGrant => Active && Game.InGame && Singleton<Inventory>.HasMain;

        // A reward of ours is about to be given here. false: we had it already (the game's code doesn't run).
        public static bool Fire(string what, byte kind, Action<NetDataWriter> payload, CustomEvent ev = null)
        {
            string key = Key(what, ev);
            if (!QuestRewards.Claim(key))
            {
                Skipped++;
                Plugin.Log.LogInfo($"[reward] {key}: already got it in this world");
                return false;
            }
            Fired++;
            Plugin.Log.LogInfo($"[reward] {key}: ours, and everyone's");
            session.Send(Msg.CharReward, w => { w.Put(key); w.Put(kind); payload(w); });
            return true;
        }

        // "~<source>|<what>": control characters out (the record is a text file, one key per line). The source: the
        // scene object holding the reward's event (the same whichever way it's run: a dialogue's end, a cutscene's
        // end state applied elsewhere...), else what the running script scope names, else the scene.
        internal static string Key(string what, CustomEvent ev = null)
        {
            string source = (ev != null ? EventOwners.Of(ev) : null) ?? ScriptScope.Source ?? Game.SceneName;
            var s = "~" + source + "|" + what;
            if (s.Length > 400) s = s.Substring(0, 400);
            return new string(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        }

        private static void OnReward(byte from, NetDataReader r)
        {
            string key = r.GetString(512);
            byte kind = r.GetByte();
            byte[] data = r.GetRemainingBytes();
            Parse(kind, new NetDataReader(data));   // junk throws here: the sender's fault
            Received++;
            if (CanGrant) Grant(key, kind, data);
            else if (pending.Count < 64) pending.Add((key, kind, data, Time.realtimeSinceStartup));
        }

        public static void Update()
        {
            if (pending.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            pending.RemoveAll(p => now - p.at > 60f);
            if (!CanGrant || pending.Count == 0) return;
            var list = pending.ToList();
            pending.Clear();
            foreach (var p in list) Grant(p.key, p.kind, p.data);
        }

        private static void Grant(string key, byte kind, byte[] data)
        {
            if (string.IsNullOrEmpty(key) || !key.StartsWith("~")) return;
            List<Action> grant;
            try { grant = Parse(kind, new NetDataReader(data)); }
            catch (Exception e) { Plugin.Log.LogWarning($"[reward] {key}: {e.Message}"); return; }
            if (grant == null || !QuestRewards.Claim(key)) return;
            Suppress++;
            try { foreach (var g in grant) g(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[reward] Granting {key}: {e.Message}"); }
            finally { Suppress--; }
            Granted++;
            Plugin.Log.LogInfo($"[reward] {key}: another player's, granted to us too");
        }

        // What a reward does to our character (null: nothing we can give).
        private static List<Action> Parse(byte kind, NetDataReader r)
        {
            var list = new List<Action>();
            var inv = Singleton<Inventory>.HasMain ? Singleton<Inventory>.main : null;
            switch (kind)
            {
                case Items:
                {
                    int n = NetGuard.Count(r.GetShort(), r, 5);
                    for (int i = 0; i < n; i++)
                    {
                        string item = r.GetString(256);
                        int amount = Mathf.Clamp(r.GetInt(), 1, 9999);
                        list.Add(() =>
                        {
                            var d = ItemCodec.Find(item);
                            if (d != null) Singleton<Inventory>.main.AddItem(d.CreateItem(amount));
                        });
                    }
                    break;
                }
                case KnownItems:
                {
                    bool badge = r.GetBool();
                    int n = NetGuard.Count(r.GetShort(), r, 2);
                    for (int i = 0; i < n; i++)
                    {
                        string item = r.GetString(256);
                        list.Add(() =>
                        {
                            var d = ItemCodec.Find(item);
                            if (d == null) return;
                            Singleton<Inventory>.main.SetItemKnown(d);
                            if (badge) Singleton<Inventory>.main.RaiseBadge(d);
                        });
                    }
                    break;
                }
                case Classes:
                {
                    bool learned = r.GetBool();
                    int n = NetGuard.Count(r.GetShort(), r, 2);
                    for (int i = 0; i < n; i++)
                    {
                        string id = r.GetString(64);
                        list.Add(() =>
                        {
                            GameData.playerData.KnownClasses.Add(id);
                            if (learned) GameData.playerData.LearnedClasses.Add(id);
                        });
                    }
                    break;
                }
                case GiveClass:
                {
                    var type = (PlayerClassType)r.GetInt();
                    list.Add(() =>
                    {
                        // Their graph equipped it on them; we get it equipped too if we have none, else just known.
                        var stats = Game.LocalPlayer?.GetCharacterStats();
                        var cls = Resources.FindObjectsOfTypeAll<PlayerCombatClass>().FirstOrDefault(c => c != null && c.playerClassType == type);
                        if (stats != null && cls != null && stats.equippedClass == null) stats.EquipClass(cls);
                        else GameData.playerData.KnownClasses.Add(type.GetEmbervilleID());
                    });
                    break;
                }
                case Xp:
                {
                    var type = (PlayerClassType)r.GetInt();
                    int xp = Mathf.Clamp(r.GetInt(), 0, 1000000);
                    list.Add(() => GameData.playerData.AddXP(type, xp));
                    break;
                }
                default:
                    return null;
            }
            return list;
        }

        // What an "Unlock Player Class" gives, in its key (ClassLessons gives late joiners the same).
        internal static string ClassesWhat(List<string> ids, bool learned) => "classes:" + string.Join(",", ids) + (learned ? "+learned" : "");

        // ---- what each reward gives, on the wire ----

        public static void PutItems(NetDataWriter w, List<(string key, int amount)> items)
        {
            w.Put((short)items.Count);
            foreach (var (key, amount) in items) { w.Put(key); w.Put(amount); }
        }

        public static void PutKeys(NetDataWriter w, List<string> keys)
        {
            w.Put((short)keys.Count);
            foreach (var k in keys) w.Put(k);
        }

        // A graph action's source: the object running the graph.
        public static string GraphSource(Unity.Behavior.Node n)
        {
            try { var go = n.GameObject; return go != null ? NetIds.ScenePath(go.transform) : null; }
            catch { return null; }
        }
    }

    // ---- hooks: the rewards, where the game gives them ----

    [HarmonyPatch(typeof(CustomEventAddToInventory), nameof(CustomEventAddToInventory.Invoke))]
    internal static class AddToInventoryRewardPatch
    {
        private static bool Prefix(CustomEventAddToInventory __instance)
        {
            if (!CharacterRewards.Active || __instance.items == null) return true;
            var items = __instance.items.Where(i => i?.itemData != null && !string.IsNullOrEmpty(i.itemData.addressKey))
                .Select(i => (i.itemData.addressKey, i.Amount)).ToList();
            if (items.Count == 0) return true;
            return CharacterRewards.Fire("items:" + string.Join(",", items.Select(i => i.addressKey + "x" + i.Amount)), CharacterRewards.Items,
                w => CharacterRewards.PutItems(w, items), __instance);
        }
    }

    [HarmonyPatch(typeof(CustomEvent.SetItemKnown), nameof(CustomEvent.SetItemKnown.Invoke))]
    internal static class SetItemKnownRewardPatch
    {
        private static bool Prefix(CustomEvent.SetItemKnown __instance)
        {
            if (!CharacterRewards.Active || __instance.items == null) return true;
            var keys = __instance.items.Where(i => i != null && !string.IsNullOrEmpty(i.addressKey)).Select(i => i.addressKey).ToList();
            if (keys.Count == 0) return true;
            bool badge = __instance.raiseNotificationBadge;
            return CharacterRewards.Fire("known:" + string.Join(",", keys), CharacterRewards.KnownItems, w => { w.Put(badge); CharacterRewards.PutKeys(w, keys); }, __instance);
        }
    }

    [HarmonyPatch(typeof(CustomEvent.UnlockPlayerClass), nameof(CustomEvent.UnlockPlayerClass.Invoke))]
    internal static class UnlockClassRewardPatch
    {
        private static bool Prefix(CustomEvent.UnlockPlayerClass __instance)
        {
            if (!CharacterRewards.Active || __instance.classesToUnlock == null) return true;
            var ids = __instance.classesToUnlock.Where(c => c != null).Select(c => c.playerClassType.GetEmbervilleID()).Where(id => !string.IsNullOrEmpty(id)).ToList();
            if (ids.Count == 0) return true;
            bool learned = __instance.setAsLearned;
            return CharacterRewards.Fire(CharacterRewards.ClassesWhat(ids, learned), CharacterRewards.Classes,
                w => { w.Put(learned); CharacterRewards.PutKeys(w, ids); }, __instance);
        }
    }

    [HarmonyPatch(typeof(GivePlayerItemAction), "OnStart")]
    internal static class GiveItemActionRewardPatch
    {
        private static bool Prefix(GivePlayerItemAction __instance, ref Unity.Behavior.Node.Status __result)
        {
            if (!CharacterRewards.Active) return true;
            var item = __instance.Item?.Value;
            int amount = __instance.Stacks?.Value ?? 1;
            if (item == null || string.IsNullOrEmpty(item.addressKey) || !Singleton<Inventory>.HasMain) return true;
            var items = new List<(string, int)> { (item.addressKey, amount) };
            using (new GraphSource(__instance))
                if (CharacterRewards.Fire($"item:{item.addressKey}x{amount}", CharacterRewards.Items, w => CharacterRewards.PutItems(w, items))) return true;
            __result = Unity.Behavior.Node.Status.Success;
            return false;
        }
    }

    [HarmonyPatch(typeof(GivePlayerClassAction), "OnStart")]
    internal static class GiveClassActionRewardPatch
    {
        private static bool Prefix(GivePlayerClassAction __instance, ref Unity.Behavior.Node.Status __result)
        {
            if (!CharacterRewards.Active) return true;
            var cls = __instance.Class?.Value;
            if (cls == null) return true;
            int type = (int)cls.playerClassType;
            using (new GraphSource(__instance))
                if (CharacterRewards.Fire($"class:{cls.playerClassType.GetEmbervilleID()}", CharacterRewards.GiveClass, w => w.Put(type))) return true;
            __result = Unity.Behavior.Node.Status.Success;
            return false;
        }
    }

    [HarmonyPatch(typeof(AddXpToPlayerClassAction), "OnStart")]
    internal static class AddXpActionRewardPatch
    {
        private static bool Prefix(AddXpToPlayerClassAction __instance, ref Unity.Behavior.Node.Status __result)
        {
            if (!CharacterRewards.Active) return true;
            var cls = __instance.PlayerClass?.Value;
            int xp = __instance.Value?.Value ?? 0;
            if (cls == null || xp <= 0) return true;
            int type = (int)cls.playerClassType;
            using (new GraphSource(__instance))
                if (CharacterRewards.Fire($"xp:{cls.playerClassType.GetEmbervilleID()}+{xp}", CharacterRewards.Xp, w => { w.Put(type); w.Put(xp); })) return true;
            __result = Unity.Behavior.Node.Status.Success;
            return false;
        }
    }

    // Which scene object holds a custom event (in its event lists, trigger receivers...): looked up the first time a
    // reward fires in a scene, by walking the scene's own scripts once.
    internal static class EventOwners
    {
        private static readonly Dictionary<CustomEvent, string> owners = new Dictionary<CustomEvent, string>();
        private static readonly Dictionary<Type, FieldInfo[]> walked = new Dictionary<Type, FieldInfo[]>();
        private static int sceneHandle = -1;

        public static string Of(CustomEvent ev)
        {
            int h = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (h != sceneHandle)
            {
                sceneHandle = h;
                owners.Clear();
                try
                {
                    var game = typeof(CustomEvent).Assembly;
                    foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                        if (mb != null && mb.GetType().Assembly == game && mb.gameObject.scene.IsValid())
                        {
                            string path = null;
                            Walk(mb, e => { if (!owners.ContainsKey(e)) owners[e] = path ?? (path = NetIds.ScenePath(mb.transform)); }, 0);
                        }
                }
                catch (Exception e) { Plugin.Log.LogWarning("[reward] Looking for events: " + e.Message); }
            }
            return owners.TryGetValue(ev, out var p) ? p : null;
        }

        private static void Walk(object o, Action<CustomEvent> found, int depth)
        {
            if (o == null || depth > 5) return;
            if (o is CustomEvent ce) found(ce);
            if (o is System.Collections.IList list) { foreach (var x in list) Walk(x, found, depth + 1); return; }
            if (o is Object && depth > 0) return;   // another object: walked on its own
            foreach (var f in Fields(o.GetType())) Walk(f.GetValue(o), found, depth + 1);
        }

        // Fields that can hold custom events: event lists, events (nested lists), receivers, lists of those.
        private static FieldInfo[] Fields(Type t)
        {
            if (walked.TryGetValue(t, out var fs)) return fs;
            var list = new List<FieldInfo>();
            for (var x = t; x != null && x != typeof(MonoBehaviour) && x != typeof(object); x = x.BaseType)
                foreach (var f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (Holds(f.FieldType)) list.Add(f);
            return walked[t] = list.ToArray();
        }

        private static bool Holds(Type t) =>
            t == typeof(CustomEventList) || typeof(CustomEvent).IsAssignableFrom(t) || t == typeof(DialogueTriggerReceiver) ||
            t.IsGenericType && typeof(System.Collections.IList).IsAssignableFrom(t) && Holds(t.GetGenericArguments()[0]) ||
            t.IsArray && Holds(t.GetElementType());
    }

    // A graph action's rewards are keyed on the object running the graph (the same on every copy that plays it);
    // without one, on what the running script names.
    internal struct GraphSource : IDisposable
    {
        private readonly bool pushed;

        public GraphSource(Unity.Behavior.Node n)
        {
            string src = CharacterRewards.GraphSource(n);
            pushed = src != null || !ScriptScope.Inside;
            if (pushed) ScriptScope.BeginSource("graph:" + (src ?? Game.SceneName));
        }

        public void Dispose()
        {
            if (pushed) ScriptScope.End();
        }
    }
}
