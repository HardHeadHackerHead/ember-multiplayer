using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace EmbervilleMP.Sync
{
    // Boss arenas, for everyone in them. The game has no boss base class, but its boss cinematics share one shape
    // (MatriarchBossCinematic, HeartwoodBossCinematic): the boss, the door the intro shuts behind the player, the
    // arena's GameArea and fight confiner, where the intro puts the player, "done" once beaten, ResetToLastState (a
    // resettable) and HandleEvent(string) on the boss's event channel. Every type of that shape is found at startup
    // and patched (BossEventPatch, BossResetPatch): an EA boss of the same make gets all of this for free.
    //   - Her graph raises its events (the Matriarch: "Spawn", her egg roar: everyone in the arena held still for a
    //     second, the spiders paused, a small shake; "ScreenShake") on its own copy only: the runner's. The runner
    //     relays them (BossEvent) and every player whose own copy has the fight going in the arena gets what the
    //     boss's table says (Kinds). A runner that isn't in the arena itself (another room of the scene) only does the
    //     boss-side part (the Matriarch: pausing her spiders).
    //   - A player who dies in a boss fight (short of a party wipe, which rewinds it: PartyWipe) respawns at their
    //     room's save point, outside the door their own intro closed behind them: they're put back into the arena.
    //   - Each player's intro is their own (their copy's trigger). A copy with the fight on says so ("FightOn", every
    //     few seconds); a player in the arena's room whose own intro hasn't played (they came late, or their trigger
    //     never fired) while the boss is out on their copy joins that fight: in the arena, the door shut behind them,
    //     the fight's music and bar, as at the end of an intro (Join).
    internal static class BossArena
    {
        public static int EventsSent, EventsHeard, Shakes, Holds, PausedOnly, PullIns, Resets, Joins, FightNotices;
        public static bool IntroReadyAfterReset;
        // The boss as the last rewind left it (tests: a player whose save point lies on the trigger replays the intro
        // at once, which wakes the boss again): out or not, its health, and what the game had saved for it.
        public static bool BossOutAfterReset;
        public static float BossHealthAfterReset, BossSavedHealth;
        public static bool? BossSavedDead;
        public const float HoldSeconds = 1f, SpiderPause = 1.6f;

        // ---------------- what each boss's events do ----------------

        // The events on the wire (BossEvent's code is the index: append only). "FightOn" is ours: a copy with the fight
        // going says so, for players whose own intro hasn't played (see Join).
        private static readonly string[] Wire = { "ScreenShake", "Spawn", FightOnEvent };
        private const string FightOnEvent = "FightOn";
        public const float FightOnInterval = 3f, FightOnFresh = 8f, AfterResetQuiet = 10f;

        // What an event does to a player in the arena (what the game's HandleEvent does to its local player), and,
        // on a runner out of the arena, the boss-side part that still has to happen.
        public class Effect
        {
            public ShakeSize? Shake;
            public bool Hold;
            public Func<IEnumerator> RunnerAway;
        }

        public class Kind
        {
            public Type Type;
            public string Name = "the boss", Pronoun = "its";   // for the log: "<Name>'s arena", "<Pronoun> fight"
            public FieldInfo Boss, Door, Area, ToTeleport, Confiner, Done, Music;
            public FieldInfo[] AfterIntro = new FieldInfo[0];   // objects its intro switches on at its end
            public bool MiniBoss;                               // its health bar's kind (UIBossEvent.IsMiniBoss)
            public MethodInfo HandleEvent, Reset;
            public Dictionary<string, Effect> Events;
        }

        private class Boss
        {
            public string Name, Pronoun;
            public bool MiniBoss;
            public string[] AfterIntro = new string[0];
            public Dictionary<string, Effect> Events;
        }

        // Per boss, from its cinematic (decompiled): MatriarchBossCinematic: HandleEvent "ScreenShake" shakes (medium),
        // "Spawn" holds the player 1 s, stops and restarts the "Spiders", shakes (small); its intro ends switching the
        // nest door on and showing a mini-boss bar. HeartwoodBossCinematic: "ScreenShake" shakes (medium); a boss bar.
        // A boss we don't know: "ScreenShake" as both do; its other events stay the runner's own.
        private static readonly Dictionary<string, Boss> Known = new Dictionary<string, Boss>
        {
            ["MatriarchBossCinematic"] = new Boss
            {
                Name = "the Matriarch", Pronoun = "her", MiniBoss = true, AfterIntro = new[] { "nestDoor" },
                Events = new Dictionary<string, Effect>
                {
                    ["ScreenShake"] = new Effect { Shake = ShakeSize.Medium },
                    ["Spawn"] = new Effect { Shake = ShakeSize.Small, Hold = true, RunnerAway = () => PauseTagged("Spiders") },
                },
            },
            ["HeartwoodBossCinematic"] = new Boss
            {
                Name = "the Heartwood", Pronoun = "its",
                Events = new Dictionary<string, Effect> { ["ScreenShake"] = new Effect { Shake = ShakeSize.Medium } },
            },
        };

        private static Dictionary<string, Effect> DefaultEvents() =>
            new Dictionary<string, Effect> { ["ScreenShake"] = new Effect { Shake = ShakeSize.Medium } };

        // ---------------- finding the kinds of boss ----------------

        public static readonly List<Kind> Kinds = Discover();
        private static readonly Dictionary<Type, Kind> byType = Kinds.ToDictionary(k => k.Type);

        private static List<Kind> Discover()
        {
            var list = new List<Kind>();
            Type[] types;
            try { types = typeof(MatriarchBossCinematic).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            foreach (var t in types)
            {
                if (t.IsAbstract || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                FieldInfo F(string name, Type type)
                {
                    var f = FindField(t, name);
                    return f != null && type.IsAssignableFrom(f.FieldType) ? f : null;
                }
                var k = new Kind
                {
                    Type = t,
                    Boss = F("boss", typeof(GameObject)), Door = F("door", typeof(GameObject)), Area = F("gameArea", typeof(GameArea)),
                    ToTeleport = F("toTeleportPlayer", typeof(Transform)), Confiner = F("fightConfiner", typeof(Collider2D)),
                    Done = F("hasBeenDone", typeof(bool)),
                    HandleEvent = t.GetMethod("HandleEvent", Flags | BindingFlags.DeclaredOnly, null, new[] { typeof(string) }, null),
                    Reset = t.GetMethod("ResetToLastState", Flags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null),
                    Music = F("bossBattleMusicPlayer", typeof(MusicPlayer)),
                };
                if (k.Boss == null || k.Door == null || k.Area == null || k.ToTeleport == null || k.Done == null || k.HandleEvent == null || k.Reset == null) continue;
                if (Known.TryGetValue(t.Name, out var known))
                {
                    k.Name = known.Name; k.Pronoun = known.Pronoun; k.Events = known.Events; k.MiniBoss = known.MiniBoss;
                    k.AfterIntro = known.AfterIntro.Select(n => F(n, typeof(GameObject))).Where(f => f != null).ToArray();
                }
                else { k.Name = "the " + t.Name.Replace("BossCinematic", "").Replace("Cinematic", ""); k.Events = DefaultEvents(); }
                list.Add(k);
            }
            Plugin.Log.LogInfo($"[boss] Boss arenas: {string.Join(", ", list.Select(k => k.Type.Name))}");
            return list;
        }

        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static FieldInfo FindField(Type t, string name)
        {
            for (var x = t; x != null && x != typeof(MonoBehaviour); x = x.BaseType)
            {
                var f = x.GetField(name, Flags | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        public static Kind KindOf(Component c) => c != null && byType.TryGetValue(c.GetType(), out var k) ? k : null;

        private static GameObject Obj(FieldInfo f, Component c) => f?.GetValue(c) as GameObject;

        // The arenas of the scene (every kind), found once per scene.
        private static readonly List<Component> arenas = new List<Component>();
        private static int arenasScene;
        private static bool arenasFound;

        public static IReadOnlyList<Component> Arenas
        {
            get
            {
                int h = SceneManager.GetActiveScene().handle;
                if (!arenasFound || h != arenasScene || arenas.Any(a => a == null))
                {
                    arenasFound = true;
                    arenasScene = h;
                    arenas.Clear();
                    foreach (var k in Kinds)
                        arenas.AddRange(Object.FindObjectsByType(k.Type, FindObjectsInactive.Include, FindObjectsSortMode.None).OfType<Component>());
                }
                return arenas;
            }
        }

        public static MatriarchBossCinematic Matriarch => Arenas.OfType<MatriarchBossCinematic>().FirstOrDefault();

        private static int IdOf(Component c) => NetIds.Hash(NetIds.ScenePath(c.transform));

        // Its room: the area its intro confines the camera to (or the one it's placed in).
        private static bool InItsRoom(Component c)
        {
            var k = KindOf(c);
            string area = Game.AreaName;
            var ga = k?.Area?.GetValue(c) as GameArea;
            return area.Length > 0 && ((ga != null && ga.name == area) || PartyWipe.AreaOf(c) == area);
        }

        // This copy has the fight going: its intro played (the trigger is used up, the door shut) and the boss isn't beaten.
        public static bool FightOn(Component c)
        {
            var k = KindOf(c);
            if (k == null || (bool)k.Done.GetValue(c) || c.gameObject.activeSelf) return false;
            var door = Obj(k.Door, c);
            var boss = Obj(k.Boss, c);
            return door != null && door.activeSelf && boss != null && boss.activeSelf;
        }

        // For checks and logs: why FightOn says what it says.
        public static string Describe(Component c)
        {
            var k = KindOf(c);
            if (k == null) return "no arena";
            return $"intro {(c.gameObject.activeSelf ? "ready" : "used")}, door {(Obj(k.Door, c)?.activeSelf == true ? "shut" : "open")}, " +
                   $"boss {(Obj(k.Boss, c)?.activeSelf == true ? "out" : "away")}, done {k.Done.GetValue(c)}, room {(InItsRoom(c) ? "ours" : Game.AreaName)}";
        }

        // Our player is in that fight: in its room, with the fight going on our copy.
        public static bool LocalInArena(Component c)
        {
            var me = Game.LocalPlayer;
            return me != null && !me.IsDead && FightOn(c) && InItsRoom(c);
        }

        // ---------------- the boss's events ----------------

        private static Session session;

        public static void Hook(Session s)
        {
            session = s;
            s.On(Msg.BossEvent, OnEvent);
        }

        // Prefix on HandleEvent. false: we did what's ours here instead of the game's handler.
        public static bool OnLocalEvent(Component c, string message)
        {
            var s = Session.Current;
            var k = KindOf(c);
            if (s == null || !s.Ready || session != s || k == null || message == null) return true;
            int code = Array.IndexOf(Wire, message);
            if (code < 0 || !k.Events.TryGetValue(message, out var effect)) return true;
            // Not the runner: our boss is a puppet, the runner's graph raises its events (and relays them).
            if (EnemySync.GuestPuppets) return false;
            if (s.Players.Count > 1)
            {
                int scene = NetIds.SceneHash(Game.SceneName), id = IdOf(c);
                s.Send(Msg.BossEvent, w => { w.Put(scene); w.Put((byte)code); w.Put(id); });
                EventsSent++;
            }
            if (LocalInArena(c)) return true;
            // We run the fight from elsewhere: the boss-side part still happens, our player isn't held or shaken.
            if (effect.RunnerAway != null && MPRunner.Instance != null) MPRunner.Instance.StartCoroutine(effect.RunnerAway());
            PausedOnly++;
            Plugin.Log.LogInfo($"[boss] {Cap(k.Pronoun)} {message} while we're out of the arena: not holding our player");
            return false;
        }

        private static void OnEvent(byte from, NetDataReader r)
        {
            int scene = r.GetInt();
            int code = r.GetByte();
            int id = r.AvailableBytes >= 4 ? r.GetInt() : 0;
            if (from == session.LocalId || !Game.InGame || scene != NetIds.SceneHash(Game.SceneName) || code >= Wire.Length) return;
            string message = Wire[code];
            if (message == FightOnEvent)
            {
                // (whoever's copy has the fight on: not only the runner)
                if (!Senders.MaybeIn(from, Game.SceneName)) return;
                FightNotices++;
                othersFight[id] = Time.realtimeSinceStartup;
                return;
            }
            if (!Senders.IsRunner(from)) return;
            EventsHeard++;
            var c = Arenas.FirstOrDefault(a => a != null && (id == 0 || IdOf(a) == id));
            var k = KindOf(c);
            if (k == null || !k.Events.TryGetValue(message, out var effect) || !LocalInArena(c)) return;
            if (effect.Shake != null)
            {
                CameraShake.Play(effect.Shake.Value);
                Shakes++;
            }
            if (effect.Hold && MPRunner.Instance != null) MPRunner.Instance.StartCoroutine(Hold(Game.LocalPlayer));
        }

        // The Matriarch's Spawn on the player: still for a second.
        private static IEnumerator Hold(PlayerMaster me)
        {
            if (me == null || me.IsDead || me.Static) yield break;   // (already held: a cutscene, her intro)
            me.Static = true;
            Holds++;
            yield return new WaitForSeconds(HoldSeconds);
            if (me != null && me.Static) me.Static = false;
        }

        // The Matriarch's Spawn on her spiders (the runner's copy).
        // (only those it stopped, still alive in this scene: a scene change meanwhile leaves them be)
        private static IEnumerator PauseTagged(string tag)
        {
            int scene = SceneManager.GetActiveScene().handle;
            var stopped = EnemiesManager.AliveEnemies.Where(e => e != null && e.EnemyTag != null && e.EnemyTag.Name == tag).ToList();
            foreach (var e in stopped) e.StopBehavior();
            yield return new WaitForSeconds(SpiderPause);
            if (SceneManager.GetActiveScene().handle != scene) yield break;
            foreach (var e in stopped)
                if (e != null && e.isActiveAndEnabled && !e.IsDead) e.RestartBehavior();
        }

        private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // ---------------- joining a fight someone else started ----------------

        private static readonly Dictionary<int, float> othersFight = new Dictionary<int, float>();   // arena -> last heard
        private static readonly Dictionary<int, float> nextNotice = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> resetAt = new Dictionary<int, float>();
        private static float nextPoll;
        private static int noticesScene;

        // PartyWipe.Update, every frame (also on our death screen; it does nothing then).
        public static void Update()
        {
            var s = Session.Current;
            if (s == null || !s.Ready || session != s || !Game.InGame) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + 0.25f;
            int h = SceneManager.GetActiveScene().handle;
            if (h != noticesScene) { noticesScene = h; othersFight.Clear(); nextNotice.Clear(); resetAt.Clear(); }
            foreach (var c in Arenas)
            {
                if (c == null) continue;
                int id = IdOf(c);
                if (FightOn(c))
                {
                    if (s.Players.Count > 1 && (!nextNotice.TryGetValue(id, out var due) || now >= due))
                    {
                        nextNotice[id] = now + FightOnInterval;
                        int scene = NetIds.SceneHash(Game.SceneName), code = Array.IndexOf(Wire, FightOnEvent);
                        s.Send(Msg.BossEvent, w => { w.Put(scene); w.Put((byte)code); w.Put(id); });
                    }
                    continue;
                }
                if (othersFight.TryGetValue(id, out var heard) && now - heard < FightOnFresh &&
                    !(resetAt.TryGetValue(id, out var reset) && now - reset < AfterResetQuiet) && MayJoin(c))
                    Join(c);
            }
        }

        // Our own intro hasn't played (the trigger is ready), the boss is out on our copy (woken by whoever started the
        // fight), and we're in its room, free to move.
        private static bool MayJoin(Component c)
        {
            var k = KindOf(c);
            var me = Game.LocalPlayer;
            if (k == null || me == null || me.IsDead || me.Static || !c.gameObject.activeSelf || (bool)k.Done.GetValue(c)) return false;
            var boss = Obj(k.Boss, c);
            var enemy = boss != null ? boss.GetComponent<Enemy>() : null;
            return boss != null && boss.activeSelf && enemy != null && !enemy.IsDead && InItsRoom(c) &&
                   Game.Manager != null && Game.Manager.gameState == GameState.Playing;
        }

        // What the end of its intro leaves (the trigger used, us in the arena, the door shut behind us, the fight's
        // confiner, music and bar), without the intro itself: the fight is on already.
        private static void Join(Component c)
        {
            var k = KindOf(c);
            var me = Game.LocalPlayer;
            try
            {
                c.gameObject.SetActive(false);
                var spot = k.ToTeleport.GetValue(c) as Transform;
                if (spot != null)
                {
                    me.rb.position = spot.position;   // (no save here: our save point stays the one outside)
                    me.transform.position = spot.position;
                    me.ForceDirection = Direction.Up;
                }
                Obj(k.Door, c)?.SetActive(true);
                var area = k.Area.GetValue(c) as GameArea;
                var confiner = k.Confiner?.GetValue(c) as Collider2D;
                if (area != null && confiner != null) area.ChangeConfiner(confiner);
                foreach (var f in k.AfterIntro) Obj(f, c)?.SetActive(true);
                (k.Music?.GetValue(c) as MusicPlayer)?.Play();
                Bus<UIBossEvent>.Invoke(new UIBossEvent { IsMiniBoss = k.MiniBoss, EventType = UIBossEvent.Event_Type.Show });
                Joins++;
                Plugin.Log.LogInfo($"[boss] {Cap(k.Name)}'s fight is on (someone else's intro started it): joining it");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[boss] Joining {k.Name}'s fight: {e.Message}"); }
        }

        // ---------------- back into the fight ----------------

        // The respawn saves where it put us (StartGameplay): the save point stays the one outside the arena, so that
        // after a party wipe (the intro ready again) the player comes back where the game had them, not on the trigger.
        private static Vector2? keepRespawnAt;

        public static void OnSaved(PlayerMaster p)
        {
            if (keepRespawnAt == null || p != Game.LocalPlayer) return;
            var st = p.lastSavedState;
            st.Position = keepRespawnAt.Value;
            p.lastSavedState = st;
            keepRespawnAt = null;
        }

        // CoopResetScenePatch, after our player was put back at their save point (not a party wipe).
        public static void PullIn(PlayerMaster me)
        {
            if (me == null) return;
            foreach (var c in Arenas)
            {
                var k = KindOf(c);
                var spot = k?.ToTeleport.GetValue(c) as Transform;
                if (spot == null || !FightOn(c) || !InItsRoom(c)) continue;
                Vector2 to = spot.position;
                if (Vector2.Distance(me.rb.position, to) < 1f) return;
                keepRespawnAt = me.rb.position;
                me.rb.position = to;
                me.transform.position = to;
                me.ForceDirection = Direction.Up;
                var area = k.Area.GetValue(c) as GameArea;
                var confiner = k.Confiner?.GetValue(c) as Collider2D;
                if (area != null && confiner != null) area.ChangeConfiner(confiner);
                PullIns++;
                Plugin.Log.LogInfo($"[boss] Respawned outside {k.Name}'s arena while {k.Pronoun} fight goes on: back in");
                return;
            }
        }

        // BossResetPatch: the fight as a rewind leaves it, before anyone walks into its intro again (tests: where a
        // player respawns after a wipe is the game's business; their save point may lie on the trigger).
        public static void OnReset(Component c)
        {
            var k = KindOf(c);
            if (k == null) return;
            var door = Obj(k.Door, c);
            int id = IdOf(c);
            othersFight.Remove(id);   // (the others' copies rewind too; what they said before is over)
            resetAt[id] = Time.realtimeSinceStartup;
            Resets++;
            IntroReadyAfterReset = c.gameObject.activeSelf && door != null && !door.activeSelf;
            var boss = Obj(k.Boss, c);
            var enemy = boss != null ? boss.GetComponent<Enemy>() : null;
            BossOutAfterReset = boss != null && boss.activeSelf;
            BossHealthAfterReset = enemy != null ? enemy.currentHealth : 0f;
            var saved = enemy?.lastSavedState;
            BossSavedHealth = saved?.HP ?? 0f;
            BossSavedDead = saved?.IsDead;
            Plugin.Log.LogInfo($"[boss] {Cap(k.Pronoun)} fight rewound: intro {(c.gameObject.activeSelf ? "ready" : "off")}, door {(door != null && door.activeSelf ? "shut" : "open")}");
        }
    }

    [HarmonyPatch]
    internal static class BossEventPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => BossArena.Kinds.Select(k => (MethodBase)k.HandleEvent);
        private static bool Prefix(Component __instance, string __0) => BossArena.OnLocalEvent(__instance, __0);
    }

    [HarmonyPatch]
    internal static class BossResetPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => BossArena.Kinds.Select(k => (MethodBase)k.Reset);
        private static void Postfix(Component __instance) => BossArena.OnReset(__instance);
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.SaveState))]
    internal static class PulledInSavePatch
    {
        private static void Postfix(PlayerMaster __instance) => BossArena.OnSaved(__instance);
    }
}
