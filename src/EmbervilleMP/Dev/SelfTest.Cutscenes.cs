using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.Behavior;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using EmbervilleMP.Sync.Objects;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Story cutscenes, 3 players (host #1 and guest A #2 together, guest B #3 elsewhere in the scene):
        //   1. A sets off the tutorial's intro in the cavern: the host plays it too, once, at the same time; both end in
        //      the barracks with the acolyte gone. B (other room) isn't pulled in, gets the end state, and coming into
        //      the cavern later doesn't replay it.
        //   2. The host frees Tinetia (A plays it too, B gets her out of the cage); then host and A set off her portal
        //      cutscene at the same instant: it plays once on each. A gets stuck in it (held still on purpose):
        //      released once the host has finished. B gets the open portal.
        //   3. B reads the humming sphere's journal alone: the gate scene plays for B only; the others get the gate
        //      open, and their own read of the journal later doesn't replay it.
        //   4. Adeos (cutscene, fight, his thanks): A sets it off, the host plays it too; A keeps reading the thanks long
        //      after the host is done and isn't pulled out; B gets the end state and walking up later doesn't replay.
        // cutscenes-adeos: only 4.
        [Scenario("cutscenes", Timeout = 400, Players = 3, Switches = "cutscene-grace=6")]
        [Scenario("cutscenes-adeos")]   // (by hand, with 3 players: the suite plays it as the end of "cutscenes")
        private static IEnumerator CutscenesTest()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            yield return WaitFor(() => s.Ready && s.Players.Count >= 3, 90f);
            yield return WaitFor(() => Game.InGame && (s.IsHost || GuestWorld.Active) && !GuestWorld.Switching, 90f);
            yield return Barrier("ready", 120f);
            var cs = CutsceneSync.Instance;
            byte me = s.LocalId;
            bool watcher = me <= 2;
            if (current != "cutscenes-adeos") yield return TutorialCutscenes(cs, me, watcher);
            yield return AdeosCutscene(cs, me, watcher);
            Plugin.Log.LogInfo($"[cutscene] plays={string.Join(",", cs.Plays.Select(kv => kv.Key + ":" + kv.Value))} skipped={string.Join(",", cs.Skipped.Select(kv => kv.Key + ":" + kv.Value))} received={cs.PlaysReceived} blocked={cs.Blocked} released={cs.ForcedReleases}");
            yield return Barrier("done", 120f);
            yield return Finish("story cutscenes shared");
        }

        private static IEnumerator TutorialCutscenes(CutsceneSync cs, byte me, bool watcher)
        {
            const string cavern = "002_Cavern";

            // ---- 1. the intro ----
            var intro = Object.FindObjectsByType<IntroCutscene>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            if (!Check("intro cutscene found", intro != null) | !Check("fresh world: intro not seen", !GameData.gameBools.IsTrue(BoolID.CutsceneIntroAcolyte)))
            { yield return Finish(""); yield break; }
            var acolyte = intro.GetComponentsInChildren<Enemy>(true).FirstOrDefault(e => e.name.StartsWith("Mob_Acolyte"));
            if (watcher) yield return TeleportTo(cavern, me == 1 ? new Vector2(-56f, 72f) : new Vector2(-54.5f, 72f));
            else yield return TeleportTo(CombatArea, new Vector2(51.8f, 76.3f));
            yield return Barrier("placed", 60f);

            if (me == 2) intro.Play();   // what coming out of a house into the cavern does
            if (watcher)
            {
                yield return WaitFor(() => cs.Watching("intro"), 10f);
                Check("the intro plays here", cs.Watching("intro"), $"plays {Count(cs.Plays, "intro")}, received {cs.PlaysReceived}");
                if (cs.StartedAt.TryGetValue("intro", out var at)) Chat.Send($"#introstart{me}={at.Ticks}");
            }
            else
            {
                // while the others watch it (both tell when theirs started), and a moment more
                yield return WaitFor(() => peerData.ContainsKey("introstart1") && peerData.ContainsKey("introstart2"), 10f);
                yield return new WaitForSecondsRealtime(1.5f);
                Check("B in another room isn't pulled into the intro", !cs.Watching("intro") && Count(cs.Plays, "intro") == 0 && !cs.Busy());
            }
            yield return Barrier("intro-started");
            if (me == 1)
            {
                yield return WaitFor(() => peerData.ContainsKey("introstart2"), 5f);
                long ours = cs.StartedAt.TryGetValue("intro", out var a1) ? a1.Ticks : 0, theirs = peerData.TryGetValue("introstart2", out var t2) ? long.Parse(t2) : 0;
                double apart = Math.Abs(ours - theirs) / (double)TimeSpan.TicksPerSecond;
                Check("host and guest started the intro together", ours != 0 && theirs != 0 && apart < 2.5, $"{apart:F2}s apart");
            }
            if (watcher)
            {
                yield return WaitFor(() => GameData.gameBools.IsTrue(BoolID.CutsceneIntroAcolyte) && !cs.Watching("intro"), 120f);
                Check("the intro played to its end here", !cs.Watching("intro") && GameData.gameBools.IsTrue(BoolID.CutsceneIntroAcolyte), $"busy={cs.Busy()} area={Game.AreaName}");
                Check("played once", Count(cs.Plays, "intro") == 1, $"{Count(cs.Plays, "intro")} plays");
                yield return WaitFor(() => Game.AreaName == "001_Barrack" && !cs.Busy(), 15f);
                Check("woke up in the barracks, free to move", Game.AreaName == "001_Barrack" && !cs.Busy(), $"{Game.AreaName} busy={cs.Busy()}");
                Check("the acolyte is gone", acolyte == null || acolyte.IsDead || !acolyte.gameObject.activeInHierarchy);
            }
            else
            {
                yield return WaitFor(() => Count(cs.Skipped, "intro") > 0, 120f);
                Check("B got the intro's end state without watching", Count(cs.Skipped, "intro") > 0 && Count(cs.Plays, "intro") == 0);
                Check("B: the acolyte is gone", acolyte == null || !acolyte.gameObject.activeSelf || acolyte.IsDead);
                Check("B: story flag shared", GameData.gameBools.IsTrue(BoolID.CutsceneIntroAcolyte));
            }
            yield return Barrier("intro-done", 120f);
            if (!watcher)
            {
                // B comes into the cavern afterwards (through a house door, which plays the intro if it isn't done).
                yield return TeleportTo(cavern, new Vector2(-55f, 72f));
                intro.Play();
                yield return new WaitForSecondsRealtime(3f);
                Check("late arrival: the intro doesn't replay", Count(cs.Plays, "intro") == 0 && !cs.Watching("intro") && !cs.Busy(), cs.BusyWhy());
                Check("late arrival: no acolyte in the cavern", acolyte == null || !acolyte.gameObject.activeInHierarchy || acolyte.IsDead);
            }
            yield return Barrier("late-done", 60f);

            // ---- 2. Tinetia: the host frees her (A watches, B gets her out of the cage), then host and A set off the
            // portal at the same instant ----
            var cage = Object.FindObjectsByType<CutsceneTinetiaCage>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            if (!Check("Tinetia's cutscene found", cage != null)) { yield return Finish(""); yield break; }
            var portal = cage.GetComponentsInChildren<Portal>(true).FirstOrDefault();
            if (watcher) yield return TeleportTo("09_Tinetia", me == 1 ? new Vector2(372f, 163f) : new Vector2(375f, 163f));
            yield return Barrier("at-tinetia", 60f);
            if (me == 1) cage.ReleaseTinetia();   // what her dialogue's first trigger does
            if (watcher)
            {
                yield return WaitFor(() => cs.Watching("tinetia-release"), 5f);
                Check("Tinetia's release plays here", cs.Watching("tinetia-release"));
                yield return WaitFor(() => !cs.Watching("tinetia-release"), 30f);
                Check("release over, player next to her cage door", !cs.Watching("tinetia-release") && Vector2.Distance(Game.LocalPlayer.transform.position, cage.playerReleasePosition.position) < 1.5f,
                    $"{Vector2.Distance(Game.LocalPlayer.transform.position, cage.playerReleasePosition.position):F2} from the release point");
            }
            else
            {
                yield return WaitFor(() => Count(cs.Skipped, "tinetia-release") > 0, 60f);
                Check("B got Tinetia out of the cage without watching", Count(cs.Skipped, "tinetia-release") > 0 && Count(cs.Plays, "tinetia-release") == 0);
            }
            Check("Tinetia is out of her cage", Vector2.Distance(cage.tinetiaNpc.transform.position, cage.tinetiaReleasePosition.position) < 1.5f,
                $"{Vector2.Distance(cage.tinetiaNpc.transform.position, cage.tinetiaReleasePosition.position):F2} from her release point");
            yield return Barrier("released", 60f);
            var stuck = new object();
            if (watcher)
            {
                cage.StartActivePortalCutscene();
                yield return WaitFor(() => cs.Watching("tinetia-portal"), 5f);
                Check("the portal cutscene plays here", cs.Watching("tinetia-portal"));
                if (me == 2)
                {
                    // Something only on our copy keeps us in the cutscene.
                    yield return new WaitForSecondsRealtime(1f);
                    Game.LocalPlayer.AddStaticStack(stuck);
                }
                yield return WaitFor(() => !cs.Watching("tinetia-portal"), 90f);
                Check("the portal cutscene is over here", !cs.Watching("tinetia-portal"));
                Check("played once though both set it off", Count(cs.Plays, "tinetia-portal") == 1, $"{Count(cs.Plays, "tinetia-portal")} plays, {cs.Blocked} blocked");
                if (me == 2) Check("stuck copy released after the host finished", cs.ForcedReleases > 0 && !cs.Busy(), $"{cs.ForcedReleases} releases busy={cs.Busy()}");
                else Check("host finished on its own", cs.ForcedReleases == 0);
            }
            else
            {
                yield return WaitFor(() => Count(cs.Skipped, "tinetia-portal") > 0, 120f);
                Check("B got the portal's end state without watching", Count(cs.Skipped, "tinetia-portal") > 0 && Count(cs.Plays, "tinetia-portal") == 0);
            }
            yield return WaitFor(() => portal != null && portal.isActive, 10f);
            Check("the way out (portal) is open", portal != null && portal.isActive);
            Check("Tinetia went through it", !cage.tinetiaNpc.activeSelf);
            yield return Barrier("portal-done", 120f);

            // ---- 3. the humming sphere's gate: B reads the journal (sets the gate scene off) while the others are
            // elsewhere; then they arrive ----
            var sphere = Object.FindObjectsByType<HummingSphereCutscene>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            if (!Check("humming sphere cutscene found", sphere != null)) { yield return Finish(""); yield break; }
            // The gate scene starts the acolyte battle (its sorcerer wakes and shoots whoever walks in: a guest arriving
            // with the intro's hits on it died there and respawned out of Adeos's room). The cutscenes are tested here,
            // not that fight.
            Game.LocalPlayer.Invincible = true;
            if (!watcher)
            {
                yield return TeleportTo("004_Gather", new Vector2(177.5f, 141f));
                sphere.FinishCutscene();   // what the journal's dialogue does at its end
                yield return WaitFor(() => cs.Watching("humming-gate"), 5f);
                Check("B: the gate scene plays", cs.Watching("humming-gate"));
                yield return WaitFor(() => !cs.Watching("humming-gate"), 30f);
                Check("B: gate scene over, gate open", !cs.Watching("humming-gate") && sphere.gate.state == DoorState.Opened, sphere.gate.state.ToString());
            }
            else
            {
                yield return WaitFor(() => Count(cs.Skipped, "humming-gate") > 0, 60f);
                Check("gate scene not watched from another room", Count(cs.Plays, "humming-gate") == 0 && Count(cs.Skipped, "humming-gate") > 0);
            }
            yield return Barrier("gate-done", 60f);
            if (watcher)
            {
                yield return TeleportTo("004_Gather", me == 1 ? new Vector2(176f, 141f) : new Vector2(179f, 141f));
                sphere.FinishCutscene();   // our own copy of the journal read: must not replay
                yield return new WaitForSecondsRealtime(3f);
                Check("arriving later: no replay", Count(cs.Plays, "humming-gate") == 0 && !cs.Busy(), cs.BusyWhy());
                Check("arriving later: the gate is open", sphere.gate.state == DoorState.Opened, sphere.gate.state.ToString());
            }
            Check("acolyte battle flag shared", GameData.gameBools.IsTrue(BoolID.CutsceneAcolyteBattle));
            yield return Barrier("humming-done", 60f);
        }

        private static IEnumerator AdeosCutscene(CutsceneSync cs, byte me, bool watcher)
        {
            // ---- 4. Adeos: A walks up to him (the host plays it too), both fight the spiderlings; A then keeps
            // reading Adeos's thanks long after the host is done and must not be pulled out of it; B, in another
            // room, gets the end state and then walks in ----
            var adeos = Object.FindObjectsByType<AdeosCutscene>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            if (!Check("Adeos's cutscene found", adeos != null)) { yield return Finish(""); yield break; }
            var adeosTrigger = adeos.triggerForward.GetComponent<Collider2D>();
            var mine = Game.LocalPlayer;
            // Tutorial popups lock the UI until closed (a conversation waits for that): close them as a player would.
            bool closing = true;
            MPRunner.Instance.StartCoroutine(ClosePopups(() => closing));
            if (watcher)
            {
                mine.Invincible = true;
                yield return TeleportTo("06_Adeos", me == 1 ? new Vector2(259f, 157.6f) : new Vector2(260.8f, 157.6f));
            }
            yield return Barrier("at-adeos", 60f);
            if (watcher)
            {
                if (me == 2)
                {
                    DevHarness.HoldDialogue = true;
                    yield return MoveTo(mine, adeosTrigger.bounds.center, 1f);
                }
                yield return WaitFor(() => cs.Watching("adeos"), 10f);
                Check("Adeos's cutscene plays here", cs.Watching("adeos"), $"hasPlayed={adeos.hasPlayed}");
                // The spiderlings show off first; then the fight.
                yield return new WaitForSecondsRealtime(2f);
                yield return WaitFor(() => !cs.Busy(), 20f);
                yield return new WaitForSecondsRealtime(2f);   // they turn on the players
                float f0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - f0 < 60f && !GameData.gameBools.IsTrue(BoolID.CutsceneAdeosSpank))
                {
                    foreach (var e in adeos.enemies)
                        if (e != null && e.isActiveAndEnabled && !e.IsDead) CombatSystem.Resolve(Hit(mine, e, 99999));
                    yield return new WaitForSecondsRealtime(1.5f);
                }
                Check("spiderlings beaten", GameData.gameBools.IsTrue(BoolID.CutsceneAdeosSpank), string.Join(", ", adeos.enemies.Select(e => $"{e.name} dead={e.IsDead} active={e.isActiveAndEnabled}")));
                if (me == 1)
                {
                    yield return WaitFor(() => !cs.Watching("adeos"), 40f);
                    Check("host read through and is done", !cs.Watching("adeos") && !cs.Busy(), cs.BusyWhy() + " graph: " + GraphNow(adeos.behaviorGraphAgent));
                    Chat.Send("#adeosdone=1");
                }
                else
                {
                    yield return WaitFor(() => Singleton<DialogueManager>.main.IsPlaying, 30f);
                    Check("A is reading Adeos's thanks", Singleton<DialogueManager>.main.IsPlaying, $"in {Game.AreaName}, dead={mine.IsDead}");
                    yield return WaitFor(() => peerData.ContainsKey("adeosdone"), 40f);
                    int released = cs.ForcedReleases;
                    yield return new WaitForSecondsRealtime(CutsceneSync.StuckGrace + 4f);
                    Check("A, still reading after the host finished, isn't pulled out of it", Singleton<DialogueManager>.main.IsPlaying && cs.Watching("adeos") && cs.ForcedReleases == released);
                    DevHarness.HoldDialogue = false;   // A presses through
                    yield return WaitFor(() => !cs.Watching("adeos"), 30f);
                    Check("A done once the conversation is closed", !cs.Watching("adeos") && !cs.Busy(), cs.BusyWhy() + " graph: " + GraphNow(adeos.behaviorGraphAgent));
                }
                Check("Adeos's cutscene played once", Count(cs.Plays, "adeos") == 1, $"{Count(cs.Plays, "adeos")} plays");
            }
            else
            {
                yield return WaitFor(() => Count(cs.Skipped, "adeos") > 0, 150f);
                Check("B got Adeos's end state without watching", Count(cs.Skipped, "adeos") > 0 && Count(cs.Plays, "adeos") == 0);
                Check("B: Adeos is safe", Vector2.Distance(adeos.npcAdeos.transform.position, adeos.npcAdeosPosition.position) < 0.5f);
                Check("B: the spiderlings are gone", adeos.enemies.All(e => e == null || e.IsDead || !e.gameObject.activeSelf));
            }
            yield return Barrier("adeos-done", 180f);
            if (!watcher)
            {
                yield return TeleportTo("06_Adeos", new Vector2(260f, 157.6f));
                yield return MoveTo(mine, adeosTrigger.bounds.center, 1f);
                yield return new WaitForSecondsRealtime(3f);
                Check("walking up to Adeos later: no replay", Count(cs.Plays, "adeos") == 0 && !cs.Busy(), cs.BusyWhy());
            }
            closing = false;
        }

        private static IEnumerator ClosePopups(Func<bool> keepGoing)
        {
            while (keepGoing())
            {
                if (Singleton<TutorialManager>.HasMain)
                {
                    var tm = Singleton<TutorialManager>.main;
                    if (tm.panelPopup != null && tm.panelPopup.activeInHierarchy)
                    {
                        Plugin.Log.LogInfo("[test] Closing a tutorial popup");
                        tm.ClosePopupWindow();
                    }
                    else if (UIPanels.isLocked && (tm.panelPopup == null || !tm.panelPopup.activeInHierarchy)) UIPanels.SetLocked(false);
                }
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        // What a behaviour graph is busy with (for failed checks).
        private static string GraphNow(BehaviorGraphAgent a)
        {
            try
            {
                var root = typeof(BehaviorGraph).GetField("RootGraph", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(a.Graph);
                var nodes = (System.Collections.IList)root.GetType().GetField("m_ActiveNodes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(root);
                return $"running={a.Graph.IsRunning} active=" + string.Join(",", nodes.Cast<object>().Select(n => n.GetType().Name));
            }
            catch (Exception e) { return e.Message; }
        }

        private static int Count(Dictionary<string, int> d, string key) => d.TryGetValue(key, out int v) ? v : 0;

        // One copy: lists the story cutscenes of the scene it's in (cutscene components, behaviour graphs that aren't
        // enemies, triggers and what they call) to cutscenes-<scene>.txt.
        [Scenario("cutsurvey")]
        private static IEnumerator CutsceneSurvey()
        {
            yield return WaitInGame();
            yield return new WaitForSecondsRealtime(3f);
            var sb = new StringBuilder();
            sb.AppendLine($"scene={Game.SceneName} area={Game.AreaName} player={Game.LocalPlayer.transform.position}");
            sb.AppendLine("bools: " + string.Join(" ", Enum.GetValues(typeof(BoolID)).Cast<BoolID>().Select(b => $"{b}={GameData.gameBools.IsTrue(b)}")));
            foreach (var c in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(c => FullPath(c.transform)))
            {
                if (c == null) continue;
                string tn = c.GetType().Name;
                bool graph = c is BehaviorGraphAgent && c.GetComponent<Enemy>() == null;
                bool interesting = graph || tn.Contains("Cutscene") || tn.Contains("Cinematic") || tn == "SimpleTrigger" || tn == "CheckGameBool"
                    || tn == "SetGameBool" || tn == "EventStateEvents";
                if (!interesting) continue;
                sb.AppendLine($"{tn} '{FullPath(c.transform)}' active={c.gameObject.activeInHierarchy} enabled={c.enabled} area={(c.GetComponentInParent<GameArea>(true)?.name ?? "-")} pos={c.transform.position}");
                DumpFields(sb, c, "    ");
                if (c is BehaviorGraphAgent agent) DumpGraph(sb, agent);
            }
            // Who calls into cutscene objects (events on any component, nested in lists and serialized classes).
            sb.AppendLine("== callers");
            foreach (var c in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(c => FullPath(c.transform)))
                if (c != null) WalkEvents(sb, c, c.GetType().Name + " '" + FullPath(c.transform) + "'", "", 0);
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, $"cutscenes-{Game.SceneName}.txt"), sb.ToString());
            Report(true, "surveyed");
            DevHarness.Quit("done");
        }

        private static bool CutsceneTarget(Object target)
        {
            if (target == null) return false;
            var t = target is Component tc ? tc.transform : target is GameObject g ? g.transform : null;
            if (t == null) return false;
            string p = FullPath(t);
            return p.IndexOf("cutscene", StringComparison.OrdinalIgnoreCase) >= 0 || p.Contains("Cinematic") || p.Contains("IntroAnimation")
                || target.GetType().Name.Contains("Cutscene") || target is BehaviorGraphAgent;
        }

        private static void WalkEvents(StringBuilder sb, object o, string owner, string path, int depth)
        {
            if (o == null || depth > 4) return;
            for (var t = o.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(object) && t.Namespace?.StartsWith("Unity") != true; t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType.IsPrimitive || f.FieldType == typeof(string) || typeof(Object).IsAssignableFrom(f.FieldType)) continue;
                    object v;
                    try { v = f.GetValue(o); } catch { continue; }
                    if (v == null) continue;
                    if (v is UnityEventBase ev)
                    {
                        for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                            if (CutsceneTarget(ev.GetPersistentTarget(i)))
                                sb.AppendLine($"  {owner} {path}{f.Name} -> {Describe(ev.GetPersistentTarget(i), "")}.{ev.GetPersistentMethodName(i)}");
                    }
                    else if (v is System.Collections.IList list)
                    {
                        int k = 0;
                        foreach (var x in list) { if (x != null && !(x is Object) && !x.GetType().IsPrimitive) WalkEvents(sb, x, owner, $"{path}{f.Name}[{k}].", depth + 1); k++; }
                    }
                    else if (!f.FieldType.IsValueType && (f.FieldType.IsSerializable || v is CustomEvent))
                        WalkEvents(sb, v, owner, path + f.Name + ".", depth + 1);
                }
        }

        private static void DumpFields(StringBuilder sb, object o, string indent, bool eventsOnly = false)
        {
            for (var t = o.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    object v;
                    try { v = f.GetValue(o); } catch { continue; }
                    string s = Describe(v, indent);
                    if (s == null) continue;
                    if (eventsOnly && !(v is UnityEventBase || v is CustomEventList)) continue;
                    sb.AppendLine($"{indent}{f.Name} = {s}");
                }
        }

        private static string Describe(object v, string indent)
        {
            switch (v)
            {
                case null: return null;
                case UnityEventBase ev:
                    int n = ev.GetPersistentEventCount();
                    if (n == 0) return null;
                    return string.Join("; ", Enumerable.Range(0, n).Select(i =>
                    {
                        var target = ev.GetPersistentTarget(i);
                        string where = target is Component tc ? FullPath(tc.transform) + ":" + tc.GetType().Name : target is GameObject g ? FullPath(g.transform) : target?.name ?? "null";
                        return $"{where}.{ev.GetPersistentMethodName(i)}";
                    }));
                case CustomEventList list:
                    if (list.events == null || list.events.Count == 0) return null;
                    return string.Join(" | ", list.events.Select(e =>
                    {
                        if (e == null) return "null";
                        var inner = new StringBuilder();
                        DumpFields(inner, e, "");
                        return e.GetType().Name + "{" + inner.ToString().Replace(Environment.NewLine, ", ") + "}";
                    }));
                case System.Collections.IList l when !(v is string):
                    if (l.Count == 0) return null;
                    return "[" + string.Join(", ", l.Cast<object>().Take(12).Select(x => Describe(x, indent) ?? "null")) + (l.Count > 12 ? ", ..." : "") + "]";
                case GameBool gb: return $"{gb.id}={gb.value}";
                case Component c: return c ? FullPath(c.transform) + ":" + c.GetType().Name : "missing";
                case GameObject g: return g ? FullPath(g.transform) + (g.activeSelf ? "" : "(off)") : "missing";
                case Object uo: return uo ? uo.name + ":" + uo.GetType().Name : "missing";
                case string s: return s.Length == 0 ? null : "\"" + s + "\"";
                case Enum _: case bool _: case int _: case float _: case Vector2 _: case Vector3 _: return v.ToString();
                default: return null;
            }
        }

        private static void DumpGraph(StringBuilder sb, BehaviorGraphAgent agent)
        {
            try
            {
                var graph = agent.Graph;
                if (graph == null) { sb.AppendLine("    graph: none"); return; }
                sb.AppendLine($"    graph {graph.name} running={graph.IsRunning}");
                foreach (var v in agent.BlackboardReference.Blackboard.Variables)
                    sb.AppendLine($"      var {v.Name}: {v.Type?.Name} = {Describe(v.ObjectValue, "") ?? v.ObjectValue}");
                var root = typeof(BehaviorGraph).GetField("RootGraph", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(graph);
                var nodes = (IEnumerable<Node>)root.GetType().GetMethod("Nodes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(root, null);
                foreach (var node in nodes)
                {
                    var parts = new List<string>();
                    foreach (var f in node.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                    {
                        var bv = f.GetValue(node) as BlackboardVariable;
                        if (bv == null) continue;
                        parts.Add($"{f.Name}={(string.IsNullOrEmpty(bv.Name) ? "" : "$" + bv.Name + ":")}{Describe(bv.ObjectValue, "") ?? bv.ObjectValue}");
                    }
                    sb.AppendLine($"      node {node.GetType().Name} {string.Join(" ", parts)}");
                }
            }
            catch (Exception e) { sb.AppendLine("    graph dump failed: " + e.Message); }
        }
    }
}
