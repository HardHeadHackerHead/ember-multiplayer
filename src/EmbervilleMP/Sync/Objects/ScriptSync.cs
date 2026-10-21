using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace EmbervilleMP.Sync.Objects
{
    // Story and world scripts that run on one copy because of that copy's player: a dialogue's events and triggers
    // (DialogueTrigger: start/end events, its trigger receivers, Unity events inside them), a lore page's
    // (LoreContentTrigger), a trigger zone our player walks into (SimpleTrigger), a timed trigger (TimedTrigger: a room
    // our player entered switched it on), an entrance (InteractableEntrance), a tutorial window's close events, an
    // item's dialogue (ItemActionTriggerDialogue: the Minds Journal). What
    // they do to synced objects (doors, levers, portals...) is that player's doing: it's sent like a lever pulled by
    // hand (ObjectSync.LocalAction), also from a copy that doesn't run the scene (before, only the runner's copy sent
    // what its scripts changed, so a guest's dialogue opening a gate opened it on the guest's screen alone).
    //
    // Each run is a scope (ScriptScope):
    //   - LocalAction on (unless we're applying someone else's change), the old value back at the end;
    //   - the source (what CharacterRewards keys a reward on: which dialogue, page, trigger, tutorial gave it);
    //   - plain SetActive calls on scene objects in the script's events (persistent UnityEvent calls): compared
    //     before/after the run and sent as that object's state (the "scripted" kind below). Cameras, UI, rooms,
    //     cutscene graphs and players are left alone: each copy shows its own.
    // Teleporters' leave / arrive events and rooms' enter / exit events (GameArea) run on our copy because our player
    // went through or walked in: only their one-way SetActive calls are watched (ScriptedActiveSync.OneWay); the rest
    // (moving our player, the camera, music, a dialogue or a cutscene they start: CutsceneSync's) stays ours.
    internal static class ScriptScope
    {
        private struct Frame
        {
            public bool Local, Touched, Ours;
            public string Source;
            public List<(GameObject go, bool was)> Watched;
        }

        private static readonly List<Frame> stack = new List<Frame>();
        public static int Runs;

        public static string Source => stack.Count > 0 ? stack[stack.Count - 1].Source : null;
        public static bool Inside => stack.Count > 0;

        // Every Begin (true) is followed by one End (Harmony finalizers). touchLocal false: another patch of the same
        // method sets LocalAction already (two patches' finalizers don't run in reverse order: only one may restore it).
        public static bool Begin(Component owner, string source = null, bool touchLocal = true)
        {
            var f = new Frame { Local = ObjectSync.LocalAction, Source = Source };
            stack.Add(f);
            if (!ObjectSync.Active || owner == null) return true;
            try
            {
                // Playing along with a cutscene step another player set off: what its scripts change is theirs (their
                // copy sends it; copies elsewhere get the step's end state), never pushed into the others' cutscenes.
                f.Ours = !ObjectSync.Applying && !CutsceneSync.PlayingTheirs;
                if (f.Ours && touchLocal) { ObjectSync.LocalAction = true; f.Touched = true; }
                f.Source = source ?? NetIds.ScenePath(owner.transform);
                var targets = ScriptedActiveSync.Instance?.TargetsOf(owner);
                if (targets != null && targets.Count > 0) f.Watched = targets.Where(g => g != null).Select(g => (g, g.activeSelf)).ToList();
                Runs++;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[script] " + e.Message); }
            finally { stack[stack.Count - 1] = f; }
            return true;
        }

        // A source alone (what gave a reward: a cutscene graph), nothing else.
        public static void BeginSource(string source) => stack.Add(new Frame { Local = ObjectSync.LocalAction, Source = source });

        // Our player's doing, with no scene object of its own (an item's dialogue: its events live in the item's data).
        public static void BeginPlayer(string source)
        {
            var f = new Frame { Local = ObjectSync.LocalAction, Source = source };
            if (ObjectSync.Active && !ObjectSync.Applying && !CutsceneSync.PlayingTheirs)
            {
                ObjectSync.LocalAction = true;
                f.Touched = f.Ours = true;
                Runs++;
            }
            stack.Add(f);
        }

        public static void End()
        {
            if (stack.Count == 0) return;
            var f = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            bool now = ObjectSync.LocalAction;
            try
            {
                if (f.Watched != null && f.Ours && ScriptedActiveSync.Instance != null)
                {
                    // (something in the run, or another patch's finalizer, may have reset LocalAction already)
                    ObjectSync.LocalAction = true;
                    foreach (var (go, was) in f.Watched)
                        if (go != null && go.activeSelf != was) Interactables.Changed(ScriptedActiveSync.Instance, go.transform);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[script] " + e.Message); }
            finally { ObjectSync.LocalAction = f.Touched ? f.Local : now; }
        }

        // The persistent calls of a component's events, nested event lists included (custom event lists, dialogue
        // trigger receivers, "Unity Event" custom events, lore checks' true/false lists).
        public static IEnumerable<(Object target, string method)> Calls(object o, int depth = 0)
        {
            if (o == null || depth > 5) yield break;
            if (o is UnityEventBase ev)
            {
                for (int i = 0; i < ev.GetPersistentEventCount(); i++) yield return (ev.GetPersistentTarget(i), ev.GetPersistentMethodName(i));
                yield break;
            }
            if (o is IList list && !(o is Array a && a.Length > 256))
            {
                foreach (var x in list) foreach (var c in Calls(x, depth + 1)) yield return c;
                yield break;
            }
            for (var t = o.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (Walks(f.FieldType))
                        foreach (var c in Calls(f.GetValue(o), depth + 1)) yield return c;
        }

        private static bool Walks(Type t) =>
            typeof(UnityEventBase).IsAssignableFrom(t) || t == typeof(CustomEventList) || typeof(CustomEvent).IsAssignableFrom(t) ||
            t == typeof(DialogueTriggerReceiver) || t.IsGenericType && typeof(IList).IsAssignableFrom(t) && Walks(t.GetGenericArguments()[0]);
    }

    // Scene objects that the scripts above switch on or off (GameObject.SetActive in their events): on / off. Only
    // while the scene is loaded (the game puts them back as authored when it loads a scene; what must last is gated
    // by story flags, which are shared).
    internal class ScriptedActiveSync : ObjectHandler
    {
        public static ScriptedActiveSync Instance { get; private set; }
        public ScriptedActiveSync() { Instance = this; }

        public override string Kind => "scripted";
        public override bool Persistent => false;
        public int Applied;

        // The script components whose events we watch.
        private static readonly Type[] owners =
        {
            typeof(DialogueTrigger), typeof(LoreContentTrigger), typeof(SimpleTrigger), typeof(TimedTrigger),
            typeof(InteractableEntrance), typeof(SimpleInteractable), typeof(Teleporter), typeof(GameArea),
        };

        private readonly Dictionary<Component, List<GameObject>> targetsOf = new Dictionary<Component, List<GameObject>>();
        private readonly Dictionary<GameObject, bool> allowed = new Dictionary<GameObject, bool>();
        private Dictionary<GameObject, int> passCalls;
        private int sceneHandle = -1;

        public override IEnumerable<Component> FindAll()
        {
            EnsureScene();
            var all = new HashSet<Transform>();
            foreach (var t in owners)
                foreach (Component c in Object.FindObjectsByType(t, FindObjectsInactive.Include, FindObjectsSortMode.None))
                    foreach (var go in TargetsOf(c)) all.Add(go.transform);
            return all.Cast<Component>();
        }

        private void EnsureScene()
        {
            int h = SceneManager.GetActiveScene().handle;
            if (h == sceneHandle) return;
            sceneHandle = h;
            targetsOf.Clear();
            allowed.Clear();
            passCalls = null;
        }

        public List<GameObject> TargetsOf(Component owner)
        {
            EnsureScene();
            if (owner == null) return null;
            if (targetsOf.TryGetValue(owner, out var list)) return list;
            list = new List<GameObject>();
            try
            {
                foreach (var (target, method) in ScriptScope.Calls(owner))
                    if (method == nameof(GameObject.SetActive) && target is GameObject go && Allowed(go) && !list.Contains(go)) list.Add(go);
                if (owner is Teleporter || owner is GameArea) list.RemoveAll(go => !OneWay(owner, go));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[script] Reading {owner.name}'s events: {e.Message}"); }
            targetsOf[owner] = list;
            return list;
        }

        private static readonly string[] ownThings = { "Camera", "Cinemachine", "Canvas", "BehaviorGraphAgent", "PlayableDirector", "GameArea", "PlayerMaster", "AudioListener" };

        // Scene objects only, and nothing that's each player's own: cameras, UI, rooms (each copy shows its player's),
        // cutscene graphs (CutsceneSync's business), players.
        private bool Allowed(GameObject go)
        {
            if (allowed.TryGetValue(go, out bool ok)) return ok;
            ok = go.scene.IsValid() && go.scene.name != "DontDestroyOnLoad" && go.GetComponentInParent<PlayerMaster>(true) == null &&
                 !go.GetComponentsInChildren<Component>(true).Any(c => c != null && ownThings.Any(n => c.GetType().Name.Contains(n)));
            allowed[go] = ok;
            return ok;
        }

        // Teleporters and rooms switch things for the player going through: a room's art or lights on entering, off
        // on leaving (the room itself is each copy's own player's). What one of them switches once, that nothing
        // else of theirs switches back, and (a room) outside the room, is the world's: a teleporter showing its art once
        // someone has used it (the trap room's secret one is wired so, though the demo gives it no destination).
        private bool OneWay(Component owner, GameObject go)
        {
            if (owner is GameArea && go.transform.IsChildOf(owner.transform)) return false;
            if (passCalls == null)
            {
                passCalls = new Dictionary<GameObject, int>();
                var all = Object.FindObjectsByType<Teleporter>(FindObjectsInactive.Include, FindObjectsSortMode.None).Cast<Component>()
                    .Concat(Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None));
                foreach (var c in all)
                    foreach (var (target, method) in ScriptScope.Calls(c))
                        if (method == nameof(GameObject.SetActive) && target is GameObject g)
                            passCalls[g] = (passCalls.TryGetValue(g, out int n) ? n : 0) + 1;
            }
            return passCalls.TryGetValue(go, out int calls) && calls == 1;
        }

        // What the scene's scripts switch, by script (tests).
        public IEnumerable<(Component owner, GameObject target)> Wiring() =>
            targetsOf.Where(kv => kv.Key != null).SelectMany(kv => kv.Value.Where(g => g != null).Select(g => (kv.Key, g)));

        public override void Capture(Component obj, NetDataWriter w) => w.Put(obj.gameObject.activeSelf);

        // While a cutscene step plays here, its objects are its own (the same step on another copy, a bit ahead, would
        // switch them under it): what arrives waits for it to end.
        private readonly Dictionary<Component, bool> waiting = new Dictionary<Component, bool>();

        public override void Apply(Component obj, NetDataReader r)
        {
            bool on = r.GetBool();
            if (CutsceneSync.Instance != null && CutsceneSync.Instance.HasActive) { waiting[obj] = on; return; }
            waiting.Remove(obj);
            Set(obj, on);
        }

        private void Set(Component obj, bool on)
        {
            if (obj == null || obj.gameObject.activeSelf == on) return;
            obj.gameObject.SetActive(on);
            Applied++;
        }

        public override void Update()
        {
            if (waiting.Count == 0 || CutsceneSync.Instance != null && CutsceneSync.Instance.HasActive) return;
            var list = waiting.ToList();
            waiting.Clear();
            foreach (var kv in list) Set(kv.Key, kv.Value);
        }
    }

    // Portals switched on by scripts and cutscenes (Portal.SetPortalActive: the animation, its events, interactable).
    internal class PortalSync : ObjectHandler
    {
        public static PortalSync Instance { get; private set; }
        public PortalSync() { Instance = this; }

        public override string Kind => "portal";
        public override bool Persistent => false;   // the game doesn't save it either: scripts and flags set it on load

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<Portal>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w) => w.Put(((Portal)obj).isActive);

        public override void Apply(Component obj, NetDataReader r)
        {
            var p = (Portal)obj;
            bool on = r.GetBool();
            if (p.isActive != on) p.SetPortalActive(on);
        }
    }

    [HarmonyPatch(typeof(Portal), nameof(Portal.SetPortalActive))]
    internal static class PortalActivePatch
    {
        private static void Prefix(Portal __instance, out bool __state) => __state = __instance.isActive;

        private static void Postfix(Portal __instance, bool __state)
        {
            if (__instance.isActive != __state) Interactables.Changed(PortalSync.Instance, __instance);
        }
    }

    // ---------------- the scopes ----------------

    [HarmonyPatch]
    internal static class DialogueScriptPatch
    {
        // TriggerDialogue: its start events (also when a script or graph starts the dialogue, not E); OnTrigger: a
        // line's trigger; OnEnded: the end events (they run when our player closes the dialogue).
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DialogueTrigger), nameof(DialogueTrigger.TriggerDialogue), new[] { typeof(TextAsset) });
            yield return AccessTools.Method(typeof(DialogueTrigger), "OnTrigger");
            yield return AccessTools.Method(typeof(DialogueTrigger), "OnEnded");
        }

        private static void Prefix(DialogueTrigger __instance, out bool __state) =>
            __state = ScriptScope.Begin(__instance, "dialogue:" + (string.IsNullOrEmpty(__instance.uniqueID) ? NetIds.ScenePath(__instance.transform) : __instance.uniqueID));

        private static void Finalizer(bool __state) { if (__state) ScriptScope.End(); }
    }

    [HarmonyPatch]
    internal static class LoreScriptPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(LoreContentTrigger), nameof(LoreContentTrigger.Show));
            yield return AccessTools.Method(typeof(LoreContentTrigger), "OnTrigger");
            yield return AccessTools.Method(typeof(LoreContentTrigger), "OnEnd");
        }

        private static void Prefix(LoreContentTrigger __instance, out bool __state) =>
            __state = ScriptScope.Begin(__instance, "lore:" + (__instance.jsonFile != null ? __instance.jsonFile.name : NetIds.ScenePath(__instance.transform)));

        private static void Finalizer(bool __state) { if (__state) ScriptScope.End(); }
    }

    // A trigger zone: only when it's our own player walking in or out (on the scene's runner, an enemy setting it
    // off is sent anyway; other players' puppets have no colliders).
    [HarmonyPatch]
    internal static class SimpleTriggerScriptPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SimpleTrigger), "OnTriggerEnter2D");
            yield return AccessTools.Method(typeof(SimpleTrigger), "OnTriggerExit2D");
        }

        public static bool Ours(Collider2D other)
        {
            var me = Game.LocalPlayer;
            return me != null && other != null && other.GetComponentInParent<PlayerMaster>() == me;
        }

        private static void Prefix(SimpleTrigger __instance, Collider2D other, out bool __state)
        {
            __state = Ours(other);
            if (__state) ScriptScope.Begin(__instance);
        }

        private static void Finalizer(bool __state)
        {
            if (__state) ScriptScope.End();
        }
    }

    // TimedTrigger.Trigger is async: its two event calls run from the state machine (a struct) after the waits. They
    // go through Run below instead, with the trigger itself (the state machine's <>4__this).
    [HarmonyPatch]
    internal static class TimedTriggerScriptPatch
    {
        private static FieldInfo owner;
        public static int Wrapped;

        private static MethodBase TargetMethod()
        {
            var sm = typeof(TimedTrigger).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(t => t.Name.StartsWith("<" + nameof(TimedTrigger.Trigger) + ">") && typeof(System.Runtime.CompilerServices.IAsyncStateMachine).IsAssignableFrom(t));
            owner = sm?.GetField("<>4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return sm == null || owner == null ? null : AccessTools.Method(sm, "MoveNext");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var unityInvoke = AccessTools.Method(typeof(UnityEvent), nameof(UnityEvent.Invoke), Type.EmptyTypes);
            var listInvoke = AccessTools.Method(typeof(CustomEventList), nameof(CustomEventList.Invoke));
            foreach (var ci in instructions)
            {
                bool unity = ci.Calls(unityInvoke), custom = ci.Calls(listInvoke);
                if (!unity && !custom) { yield return ci; continue; }
                // [event] -> [event, trigger] -> Run(event, trigger); the call becomes the first of these (its labels stay)
                ci.opcode = System.Reflection.Emit.OpCodes.Ldarg_0;
                ci.operand = null;
                yield return ci;
                yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Ldfld, owner);
                yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(TimedTriggerScriptPatch), unity ? nameof(RunUnity) : nameof(RunList)));
                Wrapped++;
            }
            if (Wrapped < 2) Plugin.Log.LogWarning($"[script] TimedTrigger: {Wrapped} of its 2 event calls found (game update?)");
        }

        public static void RunUnity(UnityEvent ev, TimedTrigger t)
        {
            ScriptScope.Begin(t);
            try { ev.Invoke(); }
            finally { ScriptScope.End(); }
        }

        public static void RunList(CustomEventList list, TimedTrigger t)
        {
            ScriptScope.Begin(t);
            try { list.Invoke(); }
            finally { ScriptScope.End(); }
        }
    }

    [HarmonyPatch]
    internal static class EntranceScriptPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(InteractableEntrance), nameof(InteractableEntrance.OnInteract));
            yield return AccessTools.Method(typeof(InteractableEntrance), nameof(InteractableEntrance.Enter));
        }

        private static void Prefix(Component __instance, out bool __state) => __state = ScriptScope.Begin(__instance);
        private static void Finalizer(bool __state) { if (__state) ScriptScope.End(); }
    }

    // E on a SimpleInteractable is our player's doing already (InteractLocalActionPatch): only its SetActive calls.
    [HarmonyPatch(typeof(SimpleInteractable), nameof(SimpleInteractable.OnInteract))]
    internal static class SimpleInteractableScriptPatch
    {
        private static void Prefix(SimpleInteractable __instance, out bool __state) => __state = ScriptScope.Begin(__instance, touchLocal: false);
        private static void Finalizer(bool __state) { if (__state) ScriptScope.End(); }
    }

    // A tutorial window's close events (TutorialDataWindow.OnCloseEvents run when our player closes it).
    [HarmonyPatch(typeof(TutorialManager), "OnClosePanel")]
    internal static class TutorialScriptPatch
    {
        public static string Last = "";

        private static void Prefix(TutorialManager __instance, out bool __state) => __state = ScriptScope.Begin(__instance, "tutorial:" + Last);
        private static void Finalizer(bool __state) { if (__state) ScriptScope.End(); }
    }

    // A teleporter's leave and arrive events: TeleportPlayerFade runs them after the fade, from inside its async
    // callback; each goes through a scope with its teleporter (the events are passed in: wrapped here).
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.TeleportPlayerFade))]
    internal static class TeleportEventsPatch
    {
        public static int Wrapped;

        private static void Prefix(ref UnityEvent onLeaveEvent, ref UnityEvent onArriveEvent)
        {
            if (!ObjectSync.Active || onLeaveEvent == null && onArriveEvent == null) return;
            var all = Object.FindObjectsByType<Teleporter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            UnityEvent leave = onLeaveEvent, arrive = onArriveEvent;
            onLeaveEvent = Scoped(leave, all.FirstOrDefault(t => t.onLeaveEvent == leave));
            onArriveEvent = Scoped(arrive, all.FirstOrDefault(t => t.onAriveEvent == arrive));
        }

        private static UnityEvent Scoped(UnityEvent ev, Teleporter owner)
        {
            if (ev == null || owner == null) return ev;
            var wrapper = new UnityEvent();
            wrapper.AddListener(() =>
            {
                ScriptScope.Begin(owner, touchLocal: false);
                try { ev.Invoke(); }
                finally { ScriptScope.End(); }
            });
            Wrapped++;
            return wrapper;
        }
    }

    // Arriving from another scene: the destination's arrive event, as the scene starts.
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.PlacePlayerAtTeleporter))]
    internal static class TeleportArrivePatch
    {
        private static void Prefix(Teleporter teleporter, out bool __state) => __state = ScriptScope.Begin(teleporter, touchLocal: false);
        private static void Finalizer(bool __state) { if (__state) ScriptScope.End(); }
    }

    // Our player walking into a room (or out of one).
    [HarmonyPatch]
    internal static class AreaEventsPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GameArea), nameof(GameArea.EnterArea));
            yield return AccessTools.Method(typeof(GameArea), nameof(GameArea.ExitArea));
            yield return AccessTools.Method(typeof(GameArea), nameof(GameArea.OnFinishFadeAnimation));
        }

        private static void Prefix(GameArea __instance, out bool __state) => __state = ScriptScope.Begin(__instance, touchLocal: false);
        private static void Finalizer(bool __state) { if (__state) ScriptScope.End(); }
    }

    // An item's dialogue (using the Minds Journal): its line triggers are our player's doing.
    [HarmonyPatch(typeof(ItemActionTriggerDialogue), "OnTrigger")]
    internal static class ItemDialogueScriptPatch
    {
        private static void Prefix(ItemActionTriggerDialogue __instance) =>
            ScriptScope.BeginPlayer("itemdialogue:" + (__instance.dialogue != null ? __instance.dialogue.name : "?"));

        private static void Finalizer() => ScriptScope.End();
    }

    // Which tutorial window is up (for the close events' source).
    [HarmonyPatch(typeof(TutorialManager), "ShowModularTutorial")]
    internal static class TutorialShownPatch
    {
        private static void Prefix(TutorialDataWindow data)
        {
            if (data != null) TutorialScriptPatch.Last = string.IsNullOrEmpty(data.id) ? data.name : data.id;
        }
    }
}
