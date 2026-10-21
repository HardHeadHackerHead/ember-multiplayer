using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using R3;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Tinetia's map unlocks, bought in the cartographer's panel (CartographerUnlockButton): the game takes the gold and
    // sets the unlock's flag on the buyer's copy, and the flags merge as a union (WorldState.OnTinetia), so two players
    // buying the same unlock at once both paid. In co-op the host's world decides who buys it: a guest's click asks the
    // host; the first request for an unlock gets it (the host sets the flag, which reaches everyone, and tells that
    // guest to pay), later ones are told someone was first and pay nothing. The host's own click is decided there and
    // then, the same way (and so is the game's second click handler, which each reopening of the panel adds).
    internal static class CartographerSync
    {
        public static int Requests, Granted, Refused, Paid;
        private static Session session;
        private static readonly Dictionary<int, (int coin, float at)> waiting = new Dictionary<int, (int, float)>();

        public static void Hook(Session s)
        {
            session = s;
            waiting.Clear();
            s.On(Msg.CartographerBuy, OnBuy);
            s.On(Msg.CartographerResult, OnResult);
        }

        private static bool Coop => session != null && session == Session.Current && session.Ready && WorldState.InSharedWorld && GameData.HasWorldData;

        // A click on an unlock whose price (gold, portals) we can pay. true: the game's own purchase runs here.
        public static bool Buy(TinetiaUnlocks unlock, int coin)
        {
            if (!Coop) return true;
            var wd = GameData.worldData;
            if (unlock == 0 || wd.TinetiaUnlock.Value.HasFlag(unlock)) { Refused++; return false; }   // ours already
            if (session.IsHost) { Granted++; Paid++; return true; }
            if (waiting.TryGetValue((int)unlock, out var w) && Time.realtimeSinceStartup - w.at < 10f) return false;   // asked already
            waiting[(int)unlock] = (coin, Time.realtimeSinceStartup);
            Requests++;
            session.Send(Msg.CartographerBuy, x => x.Put((int)unlock));
            Plugin.Log.LogInfo($"[carto] Asking the host for {unlock} ({coin} gold)");
            return false;
        }

        public static void Update()
        {
            if (waiting.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            foreach (var k in waiting.Where(kv => now - kv.Value.at > 30f).Select(kv => kv.Key).ToList()) waiting.Remove(k);
        }

        // Host: a guest wants one. First come, first served.
        private static void OnBuy(byte from, NetDataReader r)
        {
            int unlock = r.GetInt();
            if (session == null || !session.IsHost) return;
            bool known = unlock > 0 && (unlock & (unlock - 1)) == 0 && System.Enum.IsDefined(typeof(TinetiaUnlocks), unlock);
            bool granted = known && GameData.HasWorldData && !GameData.worldData.TinetiaUnlock.Value.HasFlag((TinetiaUnlocks)unlock);
            if (granted)
            {
                GameData.worldData.TinetiaUnlock.Value |= (TinetiaUnlocks)unlock;   // reaches everyone (WorldState)
                Granted++;
            }
            else Refused++;
            Plugin.Log.LogInfo($"[carto] #{from} buys {(TinetiaUnlocks)unlock}: {(granted ? "theirs" : "someone was first")}");
            session.SendTo(from, Msg.CartographerResult, w => { w.Put(unlock); w.Put(granted); });
        }

        // Guest: the host's answer.
        private static void OnResult(byte from, NetDataReader r)
        {
            int unlock = r.GetInt();
            bool granted = r.GetBool();
            if (from != Protocol.HostId || !waiting.TryGetValue(unlock, out var w)) return;
            waiting.Remove(unlock);
            if (!granted)
            {
                Refused++;
                try { PlayerTextReaction.ShowText("Someone else bought that already."); } catch { }
                return;
            }
            if (GameData.HasWorldData) GameData.worldData.TinetiaUnlock.Value |= (TinetiaUnlocks)unlock;
            var inv = Singleton<Inventory>.HasMain ? Singleton<Inventory>.main : null;
            if (inv != null) inv.AddGold(-Mathf.Min(w.coin, Mathf.Max(0, inv.TotalGold.CurrentValue)));
            Paid++;
            Plugin.Log.LogInfo($"[carto] Bought {(TinetiaUnlocks)unlock} for {w.coin} gold");
        }
    }

    // The unlock button's click handler (a closure of CartographerUnlockButton.Setup: the unlock, the inventory, the
    // unlocked portals). Same requirements as the game's, then CartographerSync decides.
    [HarmonyPatch]
    internal static class CartographerClickPatch
    {
        private static readonly Dictionary<System.Type, (FieldInfo unlock, FieldInfo inventory, FieldInfo portals)> fields =
            new Dictionary<System.Type, (FieldInfo, FieldInfo, FieldInfo)>();

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var t in typeof(CartographerUnlockButton).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                var unlock = t.GetField("cartographerUnlock");
                var inventory = t.GetField("inventory");
                var portals = t.GetField("unlockedPortals");
                if (unlock == null || inventory == null || portals == null) continue;
                fields[t] = (unlock, inventory, portals);
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var p = m.GetParameters();
                    if (p.Length == 1 && p[0].ParameterType == typeof(Unit)) yield return m;
                }
            }
        }

        private static bool Prefix(object __instance)
        {
            if (!fields.TryGetValue(__instance.GetType(), out var f)) return true;
            var unlock = f.unlock.GetValue(__instance) as CartographerUnlock;
            var inv = f.inventory.GetValue(__instance) as Inventory;
            var portals = f.portals.GetValue(__instance) as ReactiveProperty<int>;
            if (unlock == null || inv == null || portals == null) return true;
            if (inv.TotalGold.CurrentValue < unlock.Coin || portals.Value < unlock.Portal) return true;   // the game does nothing either
            return CartographerSync.Buy(unlock.ToUnlock, unlock.Coin);
        }
    }
}
