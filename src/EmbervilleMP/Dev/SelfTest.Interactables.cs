using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using EmbervilleMP.Sync.Objects;
using UnityEngine;
using UnityEngine.Events;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        private static readonly string[] interactableTypes =
        {
            "InteractableSwitch", "InteractableLever", "InteractableGroup", "InteractableLight", "SimpleInteractable",
            "ItemRequester", "DungeonRoomEvent", "DungeonRoomEventStage", "EnemyWaveHandler", "EnemyWaveStage",
            "DestroyBreakablesStage", "EventStateEvents", "GroundTrap", "ShooterTrap", "RoomHider", "GroundButton",
            "EventObjectCallbackHandler", "Totem", "SimpleTrigger", "TimedTrigger", "GraphTrigger", "CheckTriggerCondition",
            "ChildTriggerHandler", "ItemDropper", "TriggerExplosion", "RandomExplosionTrigger",
        };

        private static bool IsSurveyed(System.Type t)
        {
            for (var x = t; x != null && x != typeof(MonoBehaviour); x = x.BaseType)
                if (interactableTypes.Contains(x.Name)) return true;
            return false;
        }

        // Lists the world objects of phase C3 in this scene with their wiring (UnityEvent listeners).
        [Scenario("isurvey")]
        private static IEnumerator InteractablesSurvey()
        {
            yield return WaitInGame();
            yield return new WaitForSecondsRealtime(3f);
            var sb = new StringBuilder();
            sb.AppendLine($"scene={Game.SceneName}");
            var damageables = new Dictionary<string, int>();
            foreach (var c in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(c => FullPath(c.transform)))
            {
                if (c is DamageableObject d)
                {
                    string key = $"{d.name} area={(d.GetComponentInParent<GameArea>(true)?.name ?? "-")}";
                    damageables.TryGetValue(key, out int n);
                    damageables[key] = n + 1;
                    if (n > 0) continue;
                }
                else if (!IsSurveyed(c.GetType())) continue;
                sb.AppendLine($"{c.GetType().Name} '{FullPath(c.transform)}' area={(c.GetComponentInParent<GameArea>(true)?.name ?? "-")} active={c.gameObject.activeInHierarchy} pos={c.transform.position}{Extra(c)}");
                foreach (var f in AllFields(c.GetType()))
                {
                    if (!typeof(UnityEventBase).IsAssignableFrom(f.FieldType)) continue;
                    var ev = f.GetValue(c) as UnityEventBase;
                    if (ev == null) continue;
                    for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                    {
                        var target = ev.GetPersistentTarget(i);
                        string tn = target is Component tc ? $"{tc.GetType().Name}@{FullPath(tc.transform)}" : target is GameObject go ? $"GameObject@{FullPath(go.transform)}" : target?.ToString() ?? "null";
                        sb.AppendLine($"    {f.Name} -> {tn}.{ev.GetPersistentMethodName(i)}");
                    }
                }
            }
            foreach (var kv in damageables.OrderBy(k => k.Key)) sb.AppendLine($"damageables {kv.Key} x{kv.Value}");
            // Trap rooms and arenas (05_, 07_): everything with event wiring, doors, traps, graphs not on enemies.
            foreach (var c in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(c => FullPath(c.transform)))
            {
                string area = c.GetComponentInParent<GameArea>(true)?.name ?? "-";
                if (!area.StartsWith("07_") && !area.StartsWith("05_")) continue;
                if (c is DamageableObject || c is ItemDropper || c is InteractableLight) continue;
                bool graph = c is Unity.Behavior.BehaviorGraphAgent && c.GetComponent<Enemy>() == null;
                var lines = new List<string>();
                foreach (var f in AllFields(c.GetType()))
                {
                    if (!typeof(UnityEventBase).IsAssignableFrom(f.FieldType)) continue;
                    var ev = f.GetValue(c) as UnityEventBase;
                    if (ev == null) continue;
                    for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                    {
                        var target = ev.GetPersistentTarget(i);
                        string tn = target is Component tc ? $"{tc.GetType().Name}@{FullPath(tc.transform)}" : target is GameObject go ? $"GameObject@{FullPath(go.transform)}" : target?.ToString() ?? "null";
                        lines.Add($"    {f.Name} -> {tn}.{ev.GetPersistentMethodName(i)}");
                    }
                }
                if (lines.Count == 0 && !graph && !(c is GateDoor) && !(c is GroundTrap) && !(c is ShooterTrap)) continue;
                string extra = "";
                if (c is Unity.Behavior.BehaviorGraphAgent ag && ag.Graph != null) extra = " graph=" + ag.Graph.name;
                if (c is GroundTrap g && g.damageCollider != null) extra = " dmg=" + string.Join(",", g.damageCollider.GetComponents<Component>().Select(x => x.GetType().Name));
                if (c is ShooterTrap st) extra = $" attack={st.attack?.name} projectile={st.attack?.projectile?.name}";
                if (c is GateDoor gd) extra = $" state={gd.state}";
                sb.AppendLine($"wired {c.GetType().Name} '{FullPath(c.transform)}' area={area}{extra}");
                foreach (var l in lines) sb.AppendLine(l);
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, $"isurvey-{Game.SceneName}.txt"), sb.ToString());
            Report(true, "surveyed");
            DevHarness.Quit("done");
        }

        private static string AreaOf(Component c) => c.GetComponentInParent<GameArea>(true)?.name ?? "";

        private static bool Wired(UnityEventBase ev, System.Func<Object, string, bool> match)
        {
            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                if (match(ev.GetPersistentTarget(i), ev.GetPersistentMethodName(i))) return true;
            return false;
        }

        // Phase C3: a lever flipped by the guest (and its arrow traps), breakables broken by either player, the
        // shared trap clock, trap damage staying with its own player, a secret room revealed by breaking a wall,
        // and a pressure plate opening a door.
        [Scenario("interactables", Timeout = 240)]
        private static IEnumerator InteractablesTest()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            yield return WaitFor(() => s.Ready && s.Players.Count >= 2, 90f);
            yield return WaitFor(() => Game.InGame && (s.IsHost || GuestWorld.Active) && !GuestWorld.Switching, 90f);
            yield return Barrier("ready", 120f);
            var me = Game.LocalPlayer;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Plugin.Log.LogInfo($"[test] room events here: {Object.FindObjectsByType<DungeonRoomEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length}, wave handlers: {Object.FindObjectsByType<EnemyWaveHandler>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length}");

            // ---- the trap clock ----
            if (!s.IsHost)
            {
                yield return WaitFor(() => TrapClock.Shared, 10f);
                Check("guest has the host's trap clock", TrapClock.Shared, $"offset {TrapClock.Offset:F3}");
                Chat.Send("#clock=" + TrapClock.Now.ToString("F3", inv));
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("clock"), 15f);
                float theirs = peerData.TryGetValue("clock", out var c) ? float.Parse(c, inv) : -1f;
                Check("trap clocks agree", Mathf.Abs(theirs - TrapClock.Now) < 0.15f, $"guest {theirs:F3}, host {TrapClock.Now:F3}");
            }

            // ---- a lever and the arrow traps it drives ----
            const string trapRoom = "05_Trap_Room2";
            var lever = Object.FindObjectsByType<InteractableLever>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(l => AreaOf(l) == trapRoom).OrderBy(l => NetIds.ScenePath(l.transform)).FirstOrDefault();
            if (!Check("found the trap room's lever", lever != null)) { yield return Finish(""); yield break; }
            var shooters = Object.FindObjectsByType<ShooterTrap>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => Wired(lever.OnTurnOffEvent, (o, m) => o == t && m == "TurnOff")).ToList();
            yield return TeleportTo(trapRoom, (Vector2)lever.transform.position + new Vector2(s.IsHost ? 1.2f : 0f, -1.2f));
            yield return Barrier("at-lever", 60f);
            Check("lever starts on", lever.state == SwitchState.On, lever.state.ToString());
            if (!s.IsHost)
            {
                lever.OnInteract(me.gameObject);
                Check("guest turned the lever off", lever.state == SwitchState.Off);
            }
            yield return Barrier("lever-off");
            if (s.IsHost)
            {
                yield return WaitFor(() => lever.state == SwitchState.Off, 5f);
                Check("the guest's lever is off on the host", lever.state == SwitchState.Off, lever.state.ToString());
                Check("its arrow traps stopped on the host", shooters.Count > 0 && shooters.All(t => t.state == SwitchState.Off), $"{shooters.Count(t => t.state == SwitchState.Off)}/{shooters.Count}");
                yield return new WaitForSecondsRealtime(0.5f);
                lever.OnInteract(me.gameObject);
            }
            yield return Barrier("lever-on");
            if (!s.IsHost)
            {
                yield return WaitFor(() => lever.state == SwitchState.On, 5f);
                Check("the host's lever is on on the guest", lever.state == SwitchState.On, lever.state.ToString());
                Check("its arrow traps run again on the guest", shooters.Count > 0 && shooters.All(t => t.state == SwitchState.On));
            }

            // ---- breakables: the host breaks one, the guest another and chips a third ----
            var breakables = Object.FindObjectsByType<DamageableObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(d => d.health > 0f && d.receiveDamage && !d.ignorePlayer && d.damageBehaviour == DamageableObject.DamageBehaviour.Hits)
                .OrderBy(d => AreaOf(d) == trapRoom ? 0 : 1).ThenBy(d => d.GetComponent<ItemDropper>() != null ? 0 : 1)
                .ThenBy(d => NetIds.ScenePath(d.transform)).ToList();
            var b1 = breakables.FirstOrDefault();
            var b2 = breakables.Skip(1).FirstOrDefault();
            var b3 = breakables.Skip(2).FirstOrDefault(d => d.health >= 2f);
            if (!Check("found breakables", b1 != null && b2 != null && b3 != null)) { yield return Finish(""); yield break; }
            float b3Health = b3.health;
            Plugin.Log.LogInfo($"[test] breakables {NetIds.ScenePath(b1.transform)} / {NetIds.ScenePath(b2.transform)} / {NetIds.ScenePath(b3.transform)} ({b3Health})");
            int suppressedBefore = DestructibleSync.DropsSuppressed;
            yield return Barrier("breakables");
            if (s.IsHost)
            {
                for (int i = 0; i < 10 && b1.health > 0f; i++) b1.ApplyDamage(Hit(me, b1, 5));
                Check("host broke one", b1.health <= 0f);
            }
            yield return Barrier("b1");
            if (!s.IsHost)
            {
                yield return WaitFor(() => b1.health <= 0f, 5f);
                Check("the host's breakable is broken on the guest", b1.health <= 0f, b1.health.ToString(inv));
                if (b1.GetComponent<ItemDropper>() != null)
                    Check("and it dropped nothing here (the breaker got the drop)", DestructibleSync.DropsSuppressed > suppressedBefore);
                for (int i = 0; i < 10 && b2.health > 0f; i++) b2.ApplyDamage(Hit(me, b2, 5));
                b3.ApplyDamage(Hit(me, b3, 5));
                Check("guest broke one and chipped another", b2.health <= 0f && b3.health == b3Health - 1f, $"{b2.health}, {b3.health} of {b3Health}");
            }
            yield return Barrier("b2");
            if (s.IsHost)
            {
                yield return WaitFor(() => b2.health <= 0f && b3.health < b3Health, 5f);
                Check("the guest's breakable is broken on the host", b2.health <= 0f, b2.health.ToString(inv));
                Check("the guest's hit on another shows on the host", b3.health == b3Health - 1f, $"{b3.health} of {b3Health}");
            }

            // ---- trap damage: the guest stands on a ground trap; only the guest's copy hurts the guest ----
            var groundTrap = Object.FindObjectsByType<GroundTrap>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => AreaOf(t) == trapRoom).OrderBy(t => NetIds.ScenePath(t.transform)).FirstOrDefault();
            int hazardsBefore = TrapSync.HazardHitsDropped, forwardedBefore = EnemySync.Instance.AttacksForwarded;
            yield return Barrier("traps");
            if (!s.IsHost && Check("found a ground trap", groundTrap != null))
            {
                me.ResetToFullHealth();
                float full = me.CurrentHealthPoints;
                yield return MoveTo(me, groundTrap.transform.position, 0.3f);
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 4f && me.CurrentHealthPoints >= full)
                {
                    me.rb.position = groundTrap.transform.position;
                    yield return null;
                }
                Check("the guest's own trap hurt the guest", me.CurrentHealthPoints < full, $"{me.CurrentHealthPoints}/{full}");
                me.Invincible = true;
                me.ResetToFullHealth();
                yield return MoveTo(me, (Vector2)lever.transform.position + Vector2.down * 1.2f, 0.3f);
                yield return new WaitForSecondsRealtime(0.5f);
                me.Invincible = false;
                me.ResetToFullHealth();
            }
            yield return Barrier("traps-done");
            if (s.IsHost)
                Check("the host's traps didn't hit the guest a second time", TrapSync.HazardHitsDropped > hazardsBefore || EnemySync.Instance.AttacksForwarded == forwardedBefore,
                    $"{TrapSync.HazardHitsDropped - hazardsBefore} trap hits on the puppet dropped, {EnemySync.Instance.AttacksForwarded - forwardedBefore} attacks forwarded");

            // ---- secret room: the guest breaks a wooden door; its hidden room is revealed for the host ----
            DamageableObject wall = null;
            RoomHider hider = null;
            foreach (var d in Object.FindObjectsByType<DamageableObject>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(d => NetIds.ScenePath(d.transform)))
            {
                for (int i = 0; i < d.onDestroyEvent.GetPersistentEventCount() && hider == null; i++)
                    if (d.onDestroyEvent.GetPersistentTarget(i) is RoomHider h && d.onDestroyEvent.GetPersistentMethodName(i) == "Reveal" && h.isHiding != false && d.health > 0f)
                    { hider = h; wall = d; }
                if (hider != null) break;
            }
            if (!Check("found a wall hiding a room", wall != null)) { yield return Finish(""); yield break; }
            yield return TeleportTo(AreaOf(wall), (Vector2)wall.transform.position + new Vector2(s.IsHost ? 1.5f : 0f, -1.5f));
            yield return Barrier("at-wall", 60f);
            if (!s.IsHost)
            {
                for (int i = 0; i < 10 && wall.health > 0f; i++) wall.ApplyDamage(Hit(me, wall, 999));
                yield return WaitFor(() => hider.isHiding == false, 3f);
                Check("guest broke the wall and found the room", wall.health <= 0f && hider.isHiding == false);
            }
            yield return Barrier("wall");
            if (s.IsHost)
            {
                yield return WaitFor(() => wall.health <= 0f && hider.isHiding == false, 5f);
                Check("the wall is broken on the host", wall.health <= 0f);
                Check("the secret room is revealed on the host", hider.isHiding == false, $"{hider.isHiding}");
            }

            // ---- pressure plate: the guest steps on it, the door it drives opens for the host ----
            var button = Object.FindObjectsByType<GroundButton>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(b => NetIds.ScenePath(b.transform)).FirstOrDefault();
            GateDoor plateDoor = null;
            if (button != null)
                for (int i = 0; i < button.onTurnOnEvent.GetPersistentEventCount(); i++)
                    if (button.onTurnOnEvent.GetPersistentTarget(i) is GateDoor gd) plateDoor = gd;
            if (Check("found a pressure plate and its door", button != null && plateDoor != null))
            {
                yield return Barrier("plate");
                if (!s.IsHost)
                {
                    yield return TeleportTo(AreaOf(button), (Vector2)button.transform.position + Vector2.down * 1.5f);
                    yield return MoveTo(me, button.transform.position, 0.5f);
                    yield return WaitFor(() => button.state == SwitchState.On, 3f);
                    if (button.state != SwitchState.On) { Plugin.Log.LogInfo("[test] plate trigger didn't fire: pressing it directly"); button.TurnOn(); }
                    Check("guest is on the plate", button.state == SwitchState.On);
                }
                yield return Barrier("pressed");
                if (s.IsHost)
                {
                    yield return WaitFor(() => button.state == SwitchState.On && plateDoor.state == DoorState.Opened, 5f);
                    Check("the plate is down on the host", button.state == SwitchState.On, button.state.ToString());
                    Check("its door opened on the host", plateDoor.state == DoorState.Opened, plateDoor.state.ToString());
                }
            }

            var os = ObjectSync.Instance;
            Plugin.Log.LogInfo($"[objects] sent {os.StatesSent} applied {os.StatesApplied} snapshots {os.SnapshotsSent}/{os.SnapshotsApplied}; broken {DestructibleSync.Broken}/{DestructibleSync.BrokenApplied}, drops suppressed {DestructibleSync.DropsSuppressed}, clock syncs {TrapClock.Syncs}");
            yield return Barrier("done");
            yield return Finish("interactables in sync");
        }

        // The demo's scenes have no room events or enemy waves (DungeonRoomEvent, EnemyWaveHandler), so this builds
        // one of each, the same way on both copies, in the start room: the guest asks for them to start, the host
        // runs them, the guest follows (stages, completion, the wave's enemy spawned from a scene template).
        [Scenario("roomevents", Timeout = 200)]
        private static IEnumerator RoomEventsTest()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            yield return WaitFor(() => s.Ready && s.Players.Count >= 2, 90f);
            yield return WaitFor(() => Game.InGame && (s.IsHost || GuestWorld.Active) && !GuestWorld.Switching, 90f);
            yield return Barrier("ready", 120f);
            var me = Game.LocalPlayer;
            const string room = "001_Barrack";
            var spot = new Vector2(-104f, 70f);
            yield return TeleportTo(room, spot + new Vector2(s.IsHost ? -1.5f : 1.5f, 0f));
            yield return Barrier("placed", 60f);

            // A two-stage event; its stages (waves with no enemies) are over as soon as they start.
            var evGo = new GameObject("MP_TestRoomEvent");
            evGo.SetActive(false);
            for (int i = 0; i < 2; i++)
            {
                var st = new GameObject("Stage" + (i + 1));
                st.transform.SetParent(evGo.transform, false);
                st.AddComponent<EnemyWaveStage>();
            }
            var ev = evGo.AddComponent<DungeonRoomEvent>();
            ev.stageCooldown = 0.5f;
            int evDone = 0;
            ev.onEventCompleted.AddListener(() => evDone++);
            evGo.SetActive(true);

            // Enemy waves: one wave holding a copy of a placed enemy (kept inactive, as the game's templates are).
            var src = Object.FindObjectsByType<Enemy>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(e => e.name.StartsWith("Mob_Glowbat") && !NetIds.IsRuntimeSpawned(e.transform)).OrderBy(e => NetIds.ScenePath(e.transform)).FirstOrDefault();
            if (!Check("found an enemy to copy", src != null)) { yield return Finish(""); yield break; }
            var wvGo = new GameObject("MP_TestWaves");
            wvGo.SetActive(false);
            wvGo.transform.position = spot;
            var wave = new GameObject("Wave1");
            wave.transform.SetParent(wvGo.transform, false);
            var mob = Object.Instantiate(src.gameObject, wave.transform);
            mob.name = "MP_TestMob";
            mob.transform.position = spot + Vector2.up * 3f;
            var waves = wvGo.AddComponent<EnemyWaveHandler>();
            waves.timeBetweenWaves = 0.5f;
            waves.onWaveEventStart = new UnityEvent();
            waves.onWaveEventCompleted = new UnityEvent();
            waves.onWaveEventFailed = new UnityEvent();
            int wavesStarted = 0, wavesDone = 0;
            waves.onWaveEventStart.AddListener(() => wavesStarted++);
            waves.onWaveEventCompleted.AddListener(() => wavesDone++);
            wvGo.SetActive(true);

            // The object table was built when the scene loaded: look again.
            typeof(ObjectSync).GetField("liveScene", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(ObjectSync.Instance, -1);
            yield return Barrier("built");

            // ---- the room event ----
            if (!s.IsHost)
            {
                ev.StartEvent();
                Check("the guest didn't start it on its own", ev.state == DungeonRoomEvent.EventState.ready, ev.state.ToString());
            }
            yield return WaitFor(() => ev.state == DungeonRoomEvent.EventState.completed, 10f);
            Check("the room event ran to the end", ev.state == DungeonRoomEvent.EventState.completed, $"{ev.state}, stage {ev.currentStage}");
            yield return new WaitForSecondsRealtime(0.5f);
            Check("its completion ran once here", evDone == 1, evDone.ToString());
            if (s.IsHost) Check("the guest asked the host to start it", RoomEventSync.Intents == 1, RoomEventSync.Intents.ToString());
            else Check("the guest followed the host's stages", RoomEventSync.Applied >= 2, RoomEventSync.Applied.ToString());
            yield return Barrier("event-done");

            // ---- enemy waves ----
            int spawnsBefore = EnemySync.Instance.SpawnsApplied;
            if (!s.IsHost)
            {
                waves.StartWaveEvent();
                Check("the guest didn't spawn the wave itself", waves.state == EnemyWaveHandler.waveState.ready);
            }
            Enemy spawned = null;
            yield return WaitFor(() => (spawned = Object.FindObjectsByType<Enemy>(FindObjectsSortMode.None).FirstOrDefault(e => e.name.StartsWith("MP_TestMob") && e.isActiveAndEnabled && !e.IsDead)) != null && wavesStarted > 0, 10f);
            Check("the wave's enemy is here", spawned != null, s.IsHost ? "" : $"{EnemySync.Instance.SpawnsApplied - spawnsBefore} spawns applied");
            Check("the wave started here", wavesStarted == 1 && waves.state != EnemyWaveHandler.waveState.ready, $"{wavesStarted}, {waves.state}");
            yield return Barrier("wave-up");
            if (s.IsHost && spawned != null)
            {
                for (int i = 0; i < 5 && !spawned.IsDead; i++) { CombatSystem.Resolve(Hit(me, spawned, 99999)); yield return new WaitForSecondsRealtime(0.3f); }
                Check("host killed the wave's enemy", spawned.IsDead);
            }
            yield return WaitFor(() => waves.state == EnemyWaveHandler.waveState.completed, 10f);
            Check("the waves are over", waves.state == EnemyWaveHandler.waveState.completed, waves.state.ToString());
            yield return new WaitForSecondsRealtime(0.5f);
            Check("their completion ran once here", wavesDone == 1, wavesDone.ToString());
            if (!s.IsHost) Check("the guest's copy of the enemy died too", spawned == null || spawned.IsDead || !spawned.isActiveAndEnabled);

            yield return Barrier("done");
            Object.Destroy(evGo);
            Object.Destroy(wvGo);
            yield return Finish("room events and waves follow the runner");
        }

        private static string Extra(Component c)
        {
            switch (c)
            {
                case InteractableSwitch s: return $" state={s.state} interactable={s.isInteractable}";
                case DungeonRoomEvent e: return $" state={e.state} startActive={e.startActive} retry={e.canRetry} stages={e.stagesContainer?.GetComponentsInChildren<DungeonRoomEventStage>(true).Length}";
                case EnemyWaveHandler w: return $" state={w.state} waves={w.wavesContainer?.transform.childCount}";
                case GroundTrap g: return $" interval={g.hitInterval} delay={g.startDelay} active={g.isActive}";
                case ShooterTrap t: return $" state={t.state} cycle={t.cycleTime} dephase={t.dephase} points={t.shooterPoints.Count}";
                case RoomHider h: return $" guid={h.guid} hiding={h.isHiding}";
                case DamageableObject d: return $" hp={d.health} behaviour={d.damageBehaviour}";
                default: return "";
            }
        }

        private static IEnumerable<FieldInfo> AllFields(System.Type t)
        {
            for (var x = t; x != null && x != typeof(MonoBehaviour); x = x.BaseType)
                foreach (var f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    yield return f;
        }
    }
}
