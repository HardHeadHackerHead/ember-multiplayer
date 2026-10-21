using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using EmbervilleMP.Sync.Objects;
using Unity.Behavior;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Story and world scripts (Sync/Objects/ScriptSync.cs and friends). The scripts are built at runtime, the
        // way the scene's own are wired (event lists, trigger receivers, Unity events), so the checks don't depend on
        // which dialogue the demo has where.

        private static IEnumerator ScriptsReady()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            yield return WaitFor(() => s.Ready && s.Players.Count >= 2, 90f);
            yield return WaitFor(() => Game.InGame && (s.IsHost || GuestWorld.Active) && !GuestWorld.Switching, 90f);
            yield return Barrier("ready", 120f);
        }

        // A dialogue trigger like an NPC's: a line's trigger runs these events.
        private static DialogueTrigger TestDialogue(string name, string trigger, params CustomEvent[] events)
        {
            var go = new GameObject(name);
            go.SetActive(false);   // nothing of its own runs (no dialogue file, no UI)
            var dt = go.AddComponent<DialogueTrigger>();
            dt.triggers = new List<DialogueTriggerReceiver>
            {
                new DialogueTriggerReceiver { Trigger = trigger, OnTriggerEvents = new CustomEventList { events = events.ToList() } },
            };
            return dt;
        }

        // How many of an item our character carries (bag, belt, key items).
        private static int Held(Inventory inv, ItemData item) =>
            inv.allItems.Where(x => x?.itemObject?.data != null && (x.section & (InventorySection.MainOrKey | InventorySection.Belt)) != 0 && x.itemObject.data.IsSame(item))
                .Sum(x => x.itemObject.amount);

        private static CustomEvent UnityCall(UnityAction a)
        {
            var ev = new UnityEvent();
            ev.AddListener(a);
            return new CustomEvent.CustomEventUnityEvent { unityEvent = ev };
        }

        // scripttriggers: what a guest's own scripts do to synced objects reaches the host (the host runs the scene,
        // so before, only the guest's screen changed): a dialogue line's trigger opens a gate, a trigger zone the
        // guest walks into opens it again (after the host closed it), a lore page's trigger opens a portal, a timed
        // trigger closes the gate, and a plain SetActive in one of the scene's own scripts' events.
        [Scenario("scripttriggers", Timeout = 220)]
        private static IEnumerator ScriptTriggersTest()
        {
            yield return ScriptsReady();
            var s = Session.Current;
            var me = Game.LocalPlayer;
            var door = Object.FindObjectsByType<GateDoor>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(d => d.state == DoorState.Closed && d.GetComponentInParent<GameArea>(true) != null)
                .OrderBy(d => NetIds.ScenePath(d.transform)).FirstOrDefault();
            var portal = Object.FindObjectsByType<Portal>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(p => !p.isActive).OrderBy(p => NetIds.ScenePath(p.transform)).FirstOrDefault();
            if (!Check("found a closed door", door != null) | !Check("found an inactive portal", portal != null)) { yield return Finish(""); yield break; }
            Plugin.Log.LogInfo($"[test] door {NetIds.ScenePath(door.transform)}, portal {NetIds.ScenePath(portal.transform)}");
            var area = door.GetComponentInParent<GameArea>(true);
            yield return TeleportTo(area.name, (Vector2)door.transform.position + Vector2.down * (s.IsHost ? 2f : 1.5f));
            yield return Barrier("at-door", 60f);
            Check("the host runs the scene", EnemySync.IsAuthority == s.IsHost);

            // 1. a dialogue line's trigger -> a "Unity Event" custom event -> the gate opens
            if (!s.IsHost)
            {
                var dt = TestDialogue("MP_TestDialogue", "MP_OPEN", UnityCall(() => door.Open()));
                dt.OnTrigger("MP_OPEN");
                yield return WaitFor(() => door.state == DoorState.Opened, 5f);
                Check("guest's dialogue opened the gate here", door.state == DoorState.Opened, door.state.ToString());
            }
            yield return Barrier("dialogue");
            if (s.IsHost)
            {
                yield return WaitFor(() => door.state == DoorState.Opened, 5f);
                Check("the guest's dialogue opened the gate on the host", door.state == DoorState.Opened, door.state.ToString());
                yield return new WaitForSecondsRealtime(1f);
                door.OnInteract(me.gameObject);
            }
            yield return Barrier("host-closed");
            if (!s.IsHost)
            {
                yield return WaitFor(() => door.state == DoorState.Closed, 6f);
                Check("the host closed it again on the guest", door.state == DoorState.Closed, door.state.ToString());
            }
            yield return Barrier("closed-1");

            // 2. a trigger zone our own player walks into
            if (!s.IsHost)
            {
                yield return new WaitForSecondsRealtime(1f);
                var go = new GameObject("MP_TestZone");
                go.SetActive(false);
                var zone = go.AddComponent<SimpleTrigger>();
                zone.tagToCompare = "";
                zone.onEnableEvent = new UnityEvent();
                zone.onDisableEvent = new UnityEvent();
                zone.onTriggerEnterEvent = new UnityEvent<Collider2D>();
                zone.onTriggerExitEvent = new UnityEvent<Collider2D>();
                zone.OnTriggerEnterCustom = new CustomEventList { events = new List<CustomEvent> { UnityCall(() => door.Open()) } };
                zone.OnTriggerExitCustom = new CustomEventList { events = new List<CustomEvent>() };
                var center = me.GetComponentsInChildren<Collider2D>(true).FirstOrDefault(c => c.CompareTag("PlayerCenter")) ?? me.GetComponentInChildren<Collider2D>(true);
                Check("found our player's collider", center != null);
                if (center != null) zone.OnTriggerEnter2D(center);
                yield return WaitFor(() => door.state == DoorState.Opened, 5f);
                Check("guest's trigger zone opened the gate here", door.state == DoorState.Opened, door.state.ToString());
            }
            yield return Barrier("zone");
            if (s.IsHost)
            {
                yield return WaitFor(() => door.state == DoorState.Opened, 5f);
                Check("the guest's trigger zone opened the gate on the host", door.state == DoorState.Opened, door.state.ToString());
            }
            yield return Barrier("zone-checked");

            // 3. a lore page's trigger switches a portal on
            if (!s.IsHost)
            {
                var go = new GameObject("MP_TestLore");
                go.SetActive(false);
                var lore = go.AddComponent<LoreContentTrigger>();
                lore.triggers = new List<DialogueTriggerReceiver>
                {
                    new DialogueTriggerReceiver { Trigger = "MP_PORTAL", OnTriggerEvents = new CustomEventList { events = new List<CustomEvent> { UnityCall(() => portal.SetPortalActive(true)) } } },
                };
                lore.OnTrigger("MP_PORTAL");
                Check("guest's lore page switched the portal on here", portal.isActive);
            }
            yield return Barrier("lore");
            if (s.IsHost)
            {
                yield return WaitFor(() => portal.isActive, 5f);
                Check("the guest's lore page switched the portal on on the host", portal.isActive);
            }
            yield return Barrier("lore-checked");

            // 4. a timed trigger (its events run after an await) closes the gate
            if (!s.IsHost)
            {
                var go = new GameObject("MP_TestTimer");
                go.SetActive(false);
                var timer = go.AddComponent<TimedTrigger>();
                timer.triggerWhen = UnityCalls.OnEnable;
                timer.waitStartFadeIn = false;
                timer.delaySeconds = 0.3f;
                timer.OnTriggerEvent = new UnityEvent();
                timer.OnTriggerEvent.AddListener(() => door.Close());
                timer.customEvents = new CustomEventList { events = new List<CustomEvent>() };
                go.SetActive(true);
                yield return WaitFor(() => door.state == DoorState.Closed, 6f);
                Check("guest's timed trigger closed the gate here", door.state == DoorState.Closed, door.state.ToString());
                Check("its events went through our scope", TimedTriggerScriptPatch.Wrapped >= 2, $"{TimedTriggerScriptPatch.Wrapped} calls wrapped");
            }
            yield return Barrier("timer");
            if (s.IsHost)
            {
                yield return WaitFor(() => door.state == DoorState.Closed, 6f);
                Check("the guest's timed trigger closed the gate on the host", door.state == DoorState.Closed, door.state.ToString());
            }
            yield return Barrier("timer-checked");

            // 5. a plain SetActive wired in one of the scene's own scripts (if the scene has one)
            // (preferably in a room neither of us is in: nothing of the game's own switches it there meanwhile)
            var wiring = ScriptedActiveSync.Instance.Wiring()
                .OrderBy(w => AreaOf(w.target.transform) == "" || AreaOf(w.target.transform) == Game.AreaName ? 1 : 0)
                .ThenBy(w => NetIds.ScenePath(w.target.transform)).ThenBy(w => NetIds.ScenePath(w.owner.transform)).ToList();
            Plugin.Log.LogInfo($"[test] {wiring.Count} scripted SetActive calls in this scene, {wiring.Select(w => w.target).Distinct().Count()} objects");
            if (wiring.Count > 0)
            {
                var (owner, target) = wiring[0];
                bool was = target.activeSelf;
                Plugin.Log.LogInfo($"[test] scripted object {NetIds.ScenePath(target.transform)} (by {owner.GetType().Name} {NetIds.ScenePath(owner.transform)}), active={was}");
                Chat.Send("#active" + s.LocalId + "=" + was);
                yield return WaitFor(() => peerData.ContainsKey("active" + (s.IsHost ? 2 : 1)), 10f);
                Check("both see the scripted object the same", peerData.TryGetValue("active" + (s.IsHost ? 2 : 1), out var theirs) && theirs == was.ToString(), theirs);
                if (!s.IsHost)
                {
                    // What the script's event does, inside the script's run.
                    ScriptScope.Begin(owner);
                    try { target.SetActive(!was); }
                    finally { ScriptScope.End(); }
                }
                yield return Barrier("scripted");
                if (s.IsHost)
                {
                    yield return WaitFor(() => target.activeSelf != was, 5f);
                    Check("the guest's script switched the object on the host", target.activeSelf != was, $"active={target.activeSelf}");
                }
                yield return Barrier("scripted-checked");
            }
            else Plugin.Log.LogInfo("[test] no scripted SetActive in this scene: that part skipped");

            Plugin.Log.LogInfo($"[script] runs {ScriptScope.Runs}, objects sent {ObjectSync.Instance.StatesSent} applied {ObjectSync.Instance.StatesApplied}");
            yield return Barrier("done");
            yield return Finish("guests' scripts change the shared world");
        }

        // liveflags: a story flag from another player re-runs the checks that depend on it in the scene we're in (no
        // re-entry), and flags only grow (a guest setting one back to false doesn't take it from the host).
        [Scenario("liveflags", Timeout = 150)]
        private static IEnumerator LiveFlagsTest()
        {
            yield return ScriptsReady();
            var s = Session.Current;
            const BoolID flag = BoolID.AskedTinetiaAboutMordaxius;
            if (!Check("fresh world: the flag isn't set", !GameData.gameBools.IsTrue(flag))) { yield return Finish(""); yield break; }

            int onEnableMatch = 0, onEnableMismatch = 0, startMatch = 0, remoteMatch = 0;
            if (!s.IsHost)
            {
                CheckGameBool Make(string name, UnityCalls when, UnityAction match, UnityAction mismatch)
                {
                    var go = new GameObject(name);
                    go.SetActive(false);
                    var c = go.AddComponent<CheckGameBool>();
                    c.checkWhen = when;
                    c.checkBools = new List<GameBool> { new GameBool(flag, true) };
                    c.onMatchEvent = new UnityEvent();
                    c.onMismatchEvent = new UnityEvent();
                    c.onMatchEvent.AddListener(match);
                    c.onMismatchEvent.AddListener(mismatch);
                    go.SetActive(true);
                    return c;
                }
                Make("MP_TestCheckOnEnable", UnityCalls.OnEnable, () => onEnableMatch++, () => onEnableMismatch++);
                Make("MP_TestCheckStart", UnityCalls.Start, () => startMatch++, () => { });
                Make("MP_TestCheckRemote", UnityCalls.Remote, () => remoteMatch++, () => { });
                yield return null;
                yield return null;
                Check("the checks ran as the scene loaded (flag not set)", onEnableMismatch == 1 && onEnableMatch == 0, $"{onEnableMatch}/{onEnableMismatch}");
            }
            yield return Barrier("checks-ready");
            if (s.IsHost) GameData.gameBools[flag] = true;
            yield return Barrier("flag-set");
            if (!s.IsHost)
            {
                yield return WaitFor(() => GameData.gameBools.IsTrue(flag) && onEnableMatch > 0, 6f);
                Check("the host's flag reached us", GameData.gameBools.IsTrue(flag));
                Check("the check here ran again without leaving the room", onEnableMatch == 1, $"match {onEnableMatch}, rechecked {LiveFlags.Rechecked}");
                Check("so did the one checked on start", startMatch == 1, startMatch.ToString());
                Check("a check other scripts call wasn't run", remoteMatch == 0, remoteMatch.ToString());
                // Setting it back to false here (a scene re-initialising itself) stays here.
                GameData.gameBools[flag] = false;
            }
            yield return Barrier("guest-false");
            yield return new WaitForSecondsRealtime(2f);
            if (s.IsHost) Check("a guest's false doesn't take the flag back", GameData.gameBools.IsTrue(flag));
            yield return Barrier("done");
            yield return Finish("flags reach the loaded scene and only grow");
        }

        // charrewards: rewards given outside quests on the guest's copy (an item from a dialogue's event, class XP from
        // a cutscene graph's action) reach the host too, once; firing them again gives nobody more.
        [Scenario("charrewards", Timeout = 150)]
        private static IEnumerator CharRewardsTest()
        {
            yield return ScriptsReady();
            var s = Session.Current;
            var inv = Singleton<Inventory>.main;
            // A material (stackable, in the bag).
            var item = Resources.FindObjectsOfTypeAll<ItemData>()
                .Where(i => i != null && i.IsStackable && (i.itemCategory & ItemCategory.Material) != 0 && !string.IsNullOrEmpty(i.addressKey) && !(i is CollectableCoin))
                .OrderBy(i => i.addressKey, StringComparer.Ordinal).FirstOrDefault();
            // A class the guest has XP in (the game's AddXP stumbles on a class's first XP), its choice.
            var classes = Resources.FindObjectsOfTypeAll<PlayerCombatClass>().Where(c => c != null && c.playerClassType != PlayerClassType.None)
                .OrderBy(c => (int)c.playerClassType).ToList();
            if (!s.IsHost)
            {
                var pick = classes.FirstOrDefault(c => GameData.playerData.playerXPDictionary.ContainsKey(c.playerClassType.GetEmbervilleID())) ?? classes.FirstOrDefault();
                Chat.Send("#xpclass=" + (pick != null ? (int)pick.playerClassType : -1));
            }
            yield return WaitFor(() => peerData.ContainsKey("xpclass") || !s.IsHost, 10f);
            int clsType = peerData.TryGetValue("xpclass", out var ct) ? int.Parse(ct) : -1;
            var cls = s.IsHost ? classes.FirstOrDefault(c => (int)c.playerClassType == clsType)
                : classes.FirstOrDefault(c => GameData.playerData.playerXPDictionary.ContainsKey(c.playerClassType.GetEmbervilleID())) ?? classes.FirstOrDefault();
            if (!Check("found an item and a class", item != null && cls != null)) { yield return Finish(""); yield break; }
            string clsId = cls.playerClassType.GetEmbervilleID();
            int Xp() => GameData.playerData.playerXPDictionary.TryGetValue(clsId, out var x) ? x.TotalXP : 0;
            int items0 = Held(inv, item), xp0 = Xp(), granted0 = CharacterRewards.Granted;
            Plugin.Log.LogInfo($"[test] reward item {item.addressKey} (have {items0}), class {clsId} (xp {xp0})");
            yield return Barrier("counted");

            if (!s.IsHost)
            {
                var gift = new CustomEventAddToInventory { items = new List<ItemDataQuantified> { new ItemDataQuantified { itemData = item, amount = 2 } } };
                var dt = TestDialogue("MP_TestGift", "MP_GIFT", gift);
                dt.OnTrigger("MP_GIFT");
                Check("guest got the dialogue's item", Held(inv, item) == items0 + 2, $"{items0} -> {Held(inv, item)}");
                var action = new AddXpToPlayerClassAction { Value = new BlackboardVariable<int>(37), PlayerClass = new BlackboardVariable<PlayerCombatClass>(cls) };
                var onStart = HarmonyLib.AccessTools.Method(typeof(AddXpToPlayerClassAction), "OnStart");
                void RunAction()
                {
                    try { onStart.Invoke(action, null); }
                    catch (Exception e) { Plugin.Log.LogWarning("[test] XP action: " + (e.InnerException ?? e).Message); }
                }
                RunAction();
                Check("guest got the graph's XP", Xp() == xp0 + 37, $"{xp0} -> {Xp()}");
                yield return new WaitForSecondsRealtime(1f);
                // The same dialogue line and the same graph action again (a cutscene played on several copies).
                dt.OnTrigger("MP_GIFT");
                RunAction();
                Check("the same rewards again give nothing more", Held(inv, item) == items0 + 2 && Xp() == xp0 + 37, $"items {Held(inv, item) - items0}, xp {Xp() - xp0}");
            }
            yield return Barrier("fired");
            if (s.IsHost)
            {
                yield return WaitFor(() => Held(inv, item) >= items0 + 2 && Xp() >= xp0 + 37, 6f);
                yield return new WaitForSecondsRealtime(2f);
                Check("the host got the guest's dialogue item, once", Held(inv, item) == items0 + 2, $"{items0} -> {Held(inv, item)}");
                Check("the host got the guest's graph XP, once", Xp() == xp0 + 37, $"{xp0} -> {Xp()}");
                Check("granted from the network", CharacterRewards.Granted - granted0 == 2, $"granted {CharacterRewards.Granted - granted0}, received {CharacterRewards.Received}");
            }
            yield return Barrier("done");
            yield return Finish("character rewards outside quests reach everyone once");
        }

        // cartographer: both players buy the same Tinetia unlock at the same moment (what the panel's button does):
        // it's bought once, by one of them; buying it again costs nobody anything.
        [Scenario("cartographer", Timeout = 150)]
        private static IEnumerator CartographerTest()
        {
            yield return ScriptsReady();
            var s = Session.Current;
            var inv = Singleton<Inventory>.main;
            var wd = GameData.worldData;
            const TinetiaUnlocks unlock = TinetiaUnlocks.ShowEnemies;
            const int coin = 7;
            if (!Check("fresh world: not unlocked", !wd.TinetiaUnlock.Value.HasFlag(unlock))) { yield return Finish(""); yield break; }
            inv.AddGold(50);
            int gold0 = inv.TotalGold.CurrentValue;
            // The button's click: the game's purchase runs where CartographerSync lets it.
            void Click()
            {
                if (CartographerSync.Buy(unlock, coin))
                {
                    wd.TinetiaUnlock.Value |= unlock;
                    inv.AddGold(-coin);
                }
            }
            yield return Barrier("buy");
            Click();
            Click();   // the panel's extra click handler (one more each time it's set up)
            yield return WaitFor(() => wd.TinetiaUnlock.Value.HasFlag(unlock), 6f);
            yield return new WaitForSecondsRealtime(2f);
            Check("unlocked here", wd.TinetiaUnlock.Value.HasFlag(unlock));
            int paid = gold0 - inv.TotalGold.CurrentValue;
            Check("paid the price or nothing", paid == 0 || paid == coin, paid.ToString());
            Chat.Send("#paid" + s.LocalId + "=" + paid);
            yield return WaitFor(() => peerData.ContainsKey("paid" + (s.IsHost ? 2 : 1)), 10f);
            int theirs = peerData.TryGetValue("paid" + (s.IsHost ? 2 : 1), out var t) ? int.Parse(t) : -1;
            Check("bought once between us", paid + theirs == coin, $"we paid {paid}, they paid {theirs}");
            yield return Barrier("bought");
            int gold1 = inv.TotalGold.CurrentValue;
            Click();
            yield return new WaitForSecondsRealtime(2f);
            Check("buying it again costs nothing", inv.TotalGold.CurrentValue == gold1, $"{gold1} -> {inv.TotalGold.CurrentValue}");
            yield return Barrier("done");
            yield return Finish("Tinetia's unlocks are bought once");
        }

        // worldclock: a guest's script setting the time of day moves everyone's clock (the host's isn't undoing it),
        // and a host clock that's a day ahead runs the game's new day on the guest.
        [Scenario("worldclock", Timeout = 150)]
        private static IEnumerator WorldClockTest()
        {
            yield return ScriptsReady();
            var s = Session.Current;
            var tm = Singleton<TimeManager>.main;
            if (!Check("time manager", tm != null)) { yield return Finish(""); yield break; }
            var inv = CultureInfo.InvariantCulture;

            // 1. the guest's script sets the time
            if (s.IsHost) { tm.dayTime = 15.5f; WorldState.Instance.SendTimeSoon(); }
            else yield return WaitFor(() => Mathf.Abs(tm.dayTime - 15.5f) < 0.6f, 3f);   // the host's afternoon is here first
            yield return Barrier("afternoon");
            if (!s.IsHost) tm.SetDayTime(7.25f);   // what "Set DayTime" / "Set Time of Day" do
            yield return new WaitForSecondsRealtime(5f);
            Check("the clock is at the script's time", tm.dayTime >= 7.2f && tm.dayTime < 8.5f, tm.dayTime.ToString("F2", inv));
            if (s.IsHost) Check("the host took the guest's setting", WorldClock.Received >= 1, WorldClock.Received.ToString());
            // (listening before the barrier: the host's new day may come in with its signal)
            int newDays = 0;
            Action<NewDayEvent> onNewDay = e => newDays++;
            Bus<NewDayEvent>.Listeners += onNewDay;
            yield return Barrier("morning");

            // 2. the host's clock is a day ahead: the guest's new day comes through the game
            if (s.IsHost)
            {
                tm.dayCount += 1;
                tm.dayTime = 3f;
                WorldState.Instance.SendTimeSoon();
                Chat.Send("#day=" + tm.dayCount);
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("day"), 10f);
                int day = peerData.TryGetValue("day", out var d) ? int.Parse(d) : -1;
                yield return WaitFor(() => tm.dayCount == day, 6f);
                Check("the guest is on the host's day", tm.dayCount == day, $"{tm.dayCount} vs {day}");
                Check("through the game's new day", newDays == 1 && WorldClock.NewDays >= 1, $"{newDays} new day events");
                Check("at the host's hour", tm.dayTime >= 2.9f && tm.dayTime < 4f, tm.dayTime.ToString("F2", inv));
            }
            yield return Barrier("next-day");
            Bus<NewDayEvent>.Listeners -= onNewDay;
            yield return Finish("one clock for everyone");
        }

        // spiderdoor: the host opens the spider nest's door; the guest, in another room, gets it open without being
        // held still or shaken.
        [Scenario("spiderdoor", Timeout = 180, Quick = true)]
        private static IEnumerator SpiderDoorTest()
        {
            yield return ScriptsReady();
            var s = Session.Current;
            SpiderDoorCutscene Find() => Resources.FindObjectsOfTypeAll<SpiderDoorCutscene>()
                .Where(d => d != null && d.gameObject.scene.IsValid() && d.gameObject.scene.name != "DontDestroyOnLoad")
                .OrderBy(d => NetIds.ScenePath(d.transform)).FirstOrDefault();
            var door = Find();
            if (door == null)
            {
                // Maybe its room brings it in: go there and look again.
                Plugin.Log.LogInfo($"[test] no spider door in the loaded scene ({Resources.FindObjectsOfTypeAll<SpiderDoorCutscene>().Length} loaded anywhere, assets included); looking in 10_Spidernest");
                if (Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(a => a.name == "10_Spidernest"))
                    yield return TeleportTo("10_Spidernest", new Vector2(s.IsHost ? 414f : 415.5f, 292f));
                yield return new WaitForSecondsRealtime(2f);
                door = Find();
            }
            Chat.Send("#door" + s.LocalId + "=" + (door != null));
            yield return WaitFor(() => peerData.ContainsKey("door" + (s.IsHost ? 2 : 1)), 15f);
            bool theirs = peerData.TryGetValue("door" + (s.IsHost ? 2 : 1), out var td) && td == "True";
            if (door == null && !theirs)
            {
                // The demo build doesn't have it where we can reach it: nothing to test (not a failure of the sync).
                Plugin.Log.LogWarning($"[test] SpiderDoorCutscene isn't in {Game.SceneName} (nor brought in by 10_Spidernest): spider door not tested");
                yield return Barrier("done");
                yield return Finish("no spider door in this scene: skipped");
                yield break;
            }
            if (!Check("both copies have the spider nest's door", door != null && theirs && door.doorStatic != null && door.doorAnimator != null)) { yield return Finish(""); yield break; }
            string room = door.GetComponentInParent<GameArea>(true)?.name ?? "";
            Plugin.Log.LogInfo($"[test] spider door {NetIds.ScenePath(door.transform)} in {room}, static={door.doorStatic.activeSelf} animated={door.doorAnimator.activeSelf}");
            if (s.IsHost) { if (room.Length > 0) yield return TeleportTo(room, (Vector2)door.transform.position + Vector2.down * 2f); }
            else yield return TeleportTo("001_Barrack", new Vector2(-90.2f, 81.8f));   // (a quiet room)
            yield return Barrier("placed", 60f);
            var sd = SpiderDoorSync.Instance;
            if (s.IsHost)
            {
                ObjectSync.LocalAction = true;   // our player opened it (an item lock, a lever...)
                try { door.Play(); }
                finally { ObjectSync.LocalAction = false; }
                yield return new WaitForSecondsRealtime(0.5f);
                Check("it plays here: the door swaps, we're held", !door.doorStatic.activeSelf && door.doorAnimator.activeSelf && Game.LocalPlayer.staticOwners.Contains(door));
                yield return WaitFor(() => !Game.LocalPlayer.staticOwners.Contains(door), 6f);
                Check("and we're free again", !Game.LocalPlayer.staticOwners.Contains(door));
            }
            else
            {
                bool held = false;
                float until = Time.realtimeSinceStartup + 6f;
                while (Time.realtimeSinceStartup < until)
                {
                    if (Game.LocalPlayer.staticOwners.Contains(door)) held = true;
                    yield return null;
                }
                Check("the door is open here too", !door.doorStatic.activeSelf && door.doorAnimator.activeSelf, $"static={door.doorStatic.activeSelf} animated={door.doorAnimator.activeSelf}");
                Check("elsewhere we were never held", !held);
                Check("we got its end state, no playing", sd.EndStates == 1 && sd.Plays == 0, $"end states {sd.EndStates}, plays {sd.Plays}");
            }
            yield return Barrier("done");
            yield return Finish("the spider door plays in its room only");
        }
    }
}
