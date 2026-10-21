using System;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using Unity.Behavior;
using UnityEngine;
using UnityEngine.SceneManagement;
using Action = System.Action;
using Object = UnityEngine.Object;

namespace EmbervilleMP.Sync.Objects
{
    // B7: story cutscenes that a player sets off inside a room (the tutorial's intro, Adeos, the humming sphere,
    // Tinetia's cage/guide/portal). Whoever sets one off, everyone in that room plays it too (each copy runs the
    // game's own cutscene with its own player: camera, letterbox, NPC moves, dialogue); players elsewhere aren't
    // pulled in, they get the world as the cutscene left it once it's over.
    //
    //   - CutscenePlay: "step S of cutscene X started in room R"; copies in R play it now, unless they already are
    //     (two players hitting the trigger at once play it once).
    //   - The object state (played / ended steps) goes through the object framework: someone arriving later, or
    //     a copy elsewhere in the scene, puts the world in the end state (NPC positions, gates, portal) without
    //     watching, and never replays a step someone else played. The host keeps it with its world.
    //   - While a step plays here, its NPCs are moved by our copy (not puppets of the scene's runner, who may be in
    //     another room), and doors it moves are reported as ours.
    //   - Nobody waits for anyone: a copy still stuck in a step (not reading a dialogue) well after another player
    //     finished it is released (player free, letterbox and HUD back, the step's script stopped).
    //   - A step that can end without being over for good (Tinetia's first talk when the player doesn't commit: she can
    //     be talked to again) is "reopened" instead of ended: anyone may set it off again.
    // Arrival cutscenes (crystal, cartographer, Gumu) start when a player loads the scene and are gated by story
    // flags (shared by WorldState); boss intros stay per player (each player gets the intro when walking in).
    internal class CutsceneSync : ObjectHandler
    {
        public static CutsceneSync Instance { get; private set; }
        public CutsceneSync() { Instance = this; }

        public override string Kind => "cutscene";

        // Set while we play a step another player set off (our entry-point hooks must not echo it).
        public static bool Replaying;
        public static float StuckGrace => Args.Float("cutscene-grace", 20f);

        private static readonly CutsceneKind[] kinds = { new IntroKind(), new AdeosKind(), new HummingKind(), new TinetiaKind(), new TinetiaTalkKind() };

        private class Record
        {
            public byte Played, Ended, Here, PendingEnd, Reopened;
            public float NextTry;
            public readonly float[] PlayedAt = new float[8];   // when we heard each step was set off
        }

        // A step someone set off but never finished (they left half-way) stops blocking the others after this long.
        private const float PlayingWindow = 180f;

        private static void MarkPlayed(Record rec, int bits)
        {
            for (int i = 0; i < 8; i++)
                if ((bits & (1 << i)) != 0 && (rec.Played & (1 << i)) == 0) rec.PlayedAt[i] = Time.realtimeSinceStartup;
            rec.Played |= (byte)bits;
        }

        private static bool PlayingElsewhere(Record rec, int step)
        {
            for (int i = 0; i < 8; i++)
                if ((step & (1 << i)) != 0 && (rec.Played & (1 << i)) != 0 && Time.realtimeSinceStartup - rec.PlayedAt[i] < PlayingWindow) return true;
            return false;
        }

        internal class Tracker
        {
            public Component C;
            public CutsceneKind K;
            public int Step;
            public float T0, LastBusy = -1f, OthersEnded = -1f, Stuck = -1f;
            public bool SawBusy, Busy, Theirs;   // Theirs: another player set it off, we play along
            // The step showed (player held, letterbox...) and has been over for a while.
            public bool Calm(float now, float seconds = 2f) => SawBusy && !Busy && now - LastBusy > seconds;
        }

        private Session session;
        private readonly Dictionary<Component, Record> records = new Dictionary<Component, Record>();
        private readonly List<Tracker> active = new List<Tracker>();
        private readonly HashSet<Enemy> actors = new HashSet<Enemy>();
        private readonly List<(int frame, Action run)> later = new List<(int, Action)>();
        private int sceneHandle = -1;
        private CameraManager cameraManager;

        // Test counters: local plays per step name, when the last one started, forced releases, end states applied.
        public readonly Dictionary<string, int> Plays = new Dictionary<string, int>();
        public readonly Dictionary<string, DateTime> StartedAt = new Dictionary<string, DateTime>();
        public readonly Dictionary<string, int> Skipped = new Dictionary<string, int>();
        public int ForcedReleases, Blocked, PlaysReceived;

        public override void Init(Session s)
        {
            session = s;
            s.On(Msg.CutscenePlay, OnPlay);
            EnemySync.LocalActor = e => Instance != null && Instance.actors.Contains(e);
        }

        public override IEnumerable<Component> FindAll() =>
            kinds.SelectMany(k => Object.FindObjectsByType(k.Type, FindObjectsInactive.Include, FindObjectsSortMode.None)).Cast<Component>();

        private static CutsceneKind KindOf(Component c) => c == null ? null : kinds.FirstOrDefault(k => k.Type.IsInstanceOfType(c));

        public override void Capture(Component obj, NetDataWriter w)
        {
            var rec = Rec(obj);
            w.Put(rec.Played);
            w.Put(rec.Ended);
            w.Put(rec.Reopened);
        }

        public override void Apply(Component obj, NetDataReader r)
        {
            EnsureScene();
            byte played = r.GetByte(), ended = r.GetByte();
            byte reopened = r.AvailableBytes > 0 ? r.GetByte() : (byte)0;   // (tables saved before it had one)
            var rec = Rec(obj);
            byte newlyEnded = (byte)(ended & ~rec.Ended);
            // Steps the sender let go without ending them: nobody is playing them any more.
            rec.Played &= (byte)~(reopened & ~played & ~ended & ~rec.Ended);
            MarkPlayed(rec, played);
            rec.Ended |= ended;
            // Steps that are over and that we didn't watch here: the world as they left it.
            rec.PendingEnd |= (byte)(newlyEnded & ~rec.Here);
            rec.NextTry = 0f;
        }

        public bool HasActive => active.Count > 0;
        // We play along with a step another player set off: its scripts are theirs, not our player's doing.
        public static bool PlayingTheirs => Instance != null && Instance.active.Any(t => t.Theirs);
        public bool Watching(string name) => active.Any(t => t.K.Name(t.Step) == name);
        public static bool IsActor(Enemy e) => Instance != null && Instance.actors.Contains(e);

        private Record Rec(Component c)
        {
            if (!records.TryGetValue(c, out var rec)) records[c] = rec = new Record();
            return rec;
        }

        // Everything here is per scene visit (the object framework re-applies what's known when a scene loads).
        private void EnsureScene()
        {
            int h = SceneManager.GetActiveScene().handle;
            if (h == sceneHandle) return;
            sceneHandle = h;
            records.Clear();
            active.Clear();
            actors.Clear();
            later.Clear();
            cameraManager = null;
        }

        private static string AreaOf(Component c) => c.GetComponentInParent<GameArea>(true)?.name ?? "";

        // ---------------- our player set a step off ----------------

        // An entry point is about to start a step here because of our own player. false: don't play it (another
        // player already did, or it's already playing here); the end state comes from them.
        public static bool AllowLocal(Component c, int step)
        {
            var me = Instance;
            if (me == null || Replaying || !ObjectSync.Active || !Game.InGame) return true;
            me.EnsureScene();
            var rec = me.Rec(c);
            if ((rec.Here & step) != 0 || (rec.Ended & step) != 0 || PlayingElsewhere(rec, step))
            {
                me.Blocked++;
                string why = (rec.Here & step) != 0 ? "playing here" : (rec.Ended & step) != 0 ? "played by someone else" : "someone else is playing it";
                Plugin.Log.LogInfo($"[cutscene] Not replaying {KindOf(c)?.Name(step)} ({why})");
                return false;
            }
            return true;
        }

        // A step started here because of our own player: everyone in the room plays it too.
        public static void Started(Component c, int step, Action<NetDataWriter> extra = null)
        {
            var me = Instance;
            if (me == null || Replaying || !ObjectSync.Active || !Game.InGame || c == null) return;
            me.EnsureScene();
            var rec = me.Rec(c);
            if ((rec.Here & step) != 0) return;
            var kind = KindOf(c);
            rec.Here |= (byte)step;
            rec.Reopened &= (byte)~step;
            MarkPlayed(rec, step);
            me.Track(c, kind, step);
            int id = ObjectSync.Instance.IdOf(c);
            string scene = Game.SceneName, area = AreaOf(c);
            var x = new NetDataWriter();
            extra?.Invoke(x);
            byte[] data = x.CopyData();
            me.session.Send(Msg.CutscenePlay, w => { w.Put(scene); w.Put(area); w.Put(id); w.Put((byte)step); w.PutBytesWithLength(data); });
            Plugin.Log.LogInfo($"[cutscene] We set off {kind.Name(step)} in {area}");
            me.SendState(c);
        }

        private void SendState(Component c)
        {
            // (a script's run may have set it already: a dialogue line that sets a step off goes on after it)
            bool was = ObjectSync.LocalAction;
            ObjectSync.LocalAction = true;
            try { ObjectSync.Instance.Changed(this, c); }
            finally { ObjectSync.LocalAction = was; }
        }

        // ---------------- another player set a step off ----------------

        private void OnPlay(byte from, NetDataReader r)
        {
            string scene = r.GetString(), area = r.GetString();
            int id = r.GetInt();
            int step = r.GetByte();
            byte[] extra = r.GetBytesWithLength();
            PlaysReceived++;
            if (scene != Game.SceneName || !Game.InGame) return;
            EnsureScene();
            var c = ObjectSync.Instance.Find(Kind, id);
            var kind = KindOf(c);
            if (kind == null) { Plugin.Log.LogInfo($"[cutscene] #{from} set off an unknown cutscene {id}"); return; }
            var rec = Rec(c);
            MarkPlayed(rec, step);
            rec.Reopened &= (byte)~step;
            if ((rec.Here & step) != 0) return;   // already playing it (we set it off at the same time)
            if (area.Length > 0 && Game.AreaName != area)
            {
                Plugin.Log.LogInfo($"[cutscene] #{from} set off {kind.Name(step)} in {area}; we're in {Game.AreaName}: not watching");
                return;
            }
            rec.Here |= (byte)step;
            Plugin.Log.LogInfo($"[cutscene] #{from} set off {kind.Name(step)}: playing it here");
            Track(c, kind, step, theirs: true);
            Replaying = true;
            try { kind.Play(c, step, new NetDataReader(extra)); }
            catch (Exception e) { Plugin.Log.LogWarning($"[cutscene] Playing {kind.Name(step)}: {e}"); }
            finally { Replaying = false; }
        }

        private void Track(Component c, CutsceneKind kind, int step, bool theirs = false)
        {
            string name = kind.Name(step);
            Plays[name] = (Plays.TryGetValue(name, out int n) ? n : 0) + 1;
            StartedAt[name] = DateTime.UtcNow;
            foreach (var e in kind.Actors(c, step))
                if (e != null && actors.Add(e)) Plugin.Log.LogInfo($"[cutscene] {e.name} is moved by our copy now");
            active.Add(new Tracker { C = c, K = kind, Step = step, T0 = Time.realtimeSinceStartup, Theirs = theirs });
        }

        // Runs an action a few frames later (the game's own scripts need a frame to start).
        public void Later(int frames, Action run) => later.Add((Time.frameCount + frames, run));

        // ---------------- per frame ----------------

        public override void Update()
        {
            if (!Game.InGame) return;
            EnsureScene();
            float now = Time.realtimeSinceStartup;
            for (int i = later.Count - 1; i >= 0; i--)
                if (Time.frameCount >= later[i].frame)
                {
                    var run = later[i].run;
                    later.RemoveAt(i);
                    try { Replaying = true; run(); }
                    catch (Exception e) { Plugin.Log.LogWarning("[cutscene] " + e.Message); }
                    finally { Replaying = false; }
                }

            bool busy = Busy();
            foreach (var t in active.ToList())
            {
                if (t.C == null) { active.Remove(t); continue; }
                t.Busy = busy;
                if (busy) { t.SawBusy = true; t.LastBusy = now; }
                var rec = Rec(t.C);
                if (t.OthersEnded < 0 && (rec.Ended & t.Step) != 0) t.OthersEnded = now;
                bool finished;
                try { finished = t.K.Finished(t.C, t.Step, t, now); }
                catch (Exception e) { Plugin.Log.LogWarning($"[cutscene] {t.K.Name(t.Step)}: {e.Message}"); finished = true; }
                if (finished) { End(t, "finished"); continue; }
                // Another player finished this step and we're still held by it (not just reading a dialogue box).
                if (t.OthersEnded >= 0 && busy && !DialoguePlaying())
                {
                    if (t.Stuck < 0) t.Stuck = now;
                    else if (now - t.Stuck > StuckGrace) { ForceRelease(t); End(t, "stuck: released"); continue; }
                }
                else t.Stuck = -1f;
                if (now - t.T0 > 900f) { active.Remove(t); Plugin.Log.LogInfo($"[cutscene] Stopped following {t.K.Name(t.Step)}"); }
            }

            foreach (var kv in records)
            {
                var rec = kv.Value;
                if (rec.PendingEnd == 0 || now < rec.NextTry || kv.Key == null) continue;
                rec.NextTry = now + 1f;
                var kind = KindOf(kv.Key);
                for (int bit = 1; bit < 256; bit <<= 1)
                {
                    if ((rec.PendingEnd & bit) == 0) continue;
                    bool done;
                    try { done = kind.SkipToEnd(kv.Key, bit); }
                    catch (Exception e) { Plugin.Log.LogWarning($"[cutscene] End state of {kind.Name(bit)}: {e.Message}"); done = true; }
                    if (!done) continue;
                    rec.PendingEnd &= (byte)~bit;
                    string name = kind.Name(bit);
                    Skipped[name] = (Skipped.TryGetValue(name, out int n) ? n : 0) + 1;
                    Plugin.Log.LogInfo($"[cutscene] {name} was played by someone else: end state applied");
                }
            }
        }

        private void End(Tracker t, string why)
        {
            active.Remove(t);
            var rec = Rec(t.C);
            bool over = (rec.Ended & t.Step) != 0 || t.K.Over(t.C, t.Step);
            if (over)
            {
                rec.Ended |= (byte)t.Step;
                rec.Reopened &= (byte)~t.Step;
            }
            else
            {
                // Not over for good: free to be set off again, here and by anyone.
                rec.Reopened |= (byte)t.Step;
                rec.Played &= (byte)~t.Step;
                rec.Here &= (byte)~t.Step;
            }
            Plugin.Log.LogInfo($"[cutscene] {t.K.Name(t.Step)} over here ({why}, {Time.realtimeSinceStartup - t.T0:F1}s){(over ? "" : ", can be set off again")}");
            SendState(t.C);
        }

        // Our player is held by something cutscene-like.
        public bool Busy()
        {
            var p = Game.LocalPlayer;
            if (p == null) return false;
            if (p.Static || p.staticOwners.Count > 0) return true;
            if (cameraManager == null) cameraManager = Object.FindAnyObjectByType<CameraManager>();
            if (cameraManager != null && cameraManager.letterboxOwners.Count > 0) return true;
            if (Game.Manager.gameState == GameState.Cutscene) return true;
            return DialoguePlaying();
        }

        public string BusyWhy()
        {
            var p = Game.LocalPlayer;
            if (p == null) return "no player";
            var parts = new List<string>();
            if (p.Static) parts.Add("static(" + string.Join(",", p.staticOwners.Select(o => o is Object uo && uo ? uo.name : o?.ToString())) + ")");
            if (cameraManager != null && cameraManager.letterboxOwners.Count > 0) parts.Add("letterbox(" + string.Join(",", cameraManager.letterboxOwners) + ")");
            if (Game.Manager.gameState != GameState.Playing) parts.Add("state " + Game.Manager.gameState);
            if (DialoguePlaying()) parts.Add("dialogue");
            return parts.Count == 0 ? "free" : string.Join(" ", parts);
        }

        private static bool DialoguePlaying() => Singleton<DialogueManager>.HasMain && Singleton<DialogueManager>.main.IsPlaying;

        // Lets our player go: whatever the step was waiting for won't come here.
        private void ForceRelease(Tracker t)
        {
            ForcedReleases++;
            Plugin.Log.LogWarning($"[cutscene] Still in {t.K.Name(t.Step)} {StuckGrace:F0}s after the others finished it: releasing our player");
            try { t.K.Abort(t.C, t.Step); } catch (Exception e) { Plugin.Log.LogWarning("[cutscene] Abort: " + e.Message); }
            var p = Game.LocalPlayer;
            if (p != null)
            {
                p.staticOwners.Clear();
                if (p.Static) p.Static = false;
            }
            if (cameraManager != null)
            {
                cameraManager.letterboxOwners.Clear();
                CameraManager.SetLetterBox?.Invoke(typeof(CutsceneSync), false);
            }
            var ui = Object.FindAnyObjectByType<UIManager>();
            if (ui != null)
            {
                ui.hideHudStack.Clear();
                ui.hideHudStack.Add(typeof(CutsceneSync));
                ui.ShowPlayerHUDStack(typeof(CutsceneSync));
            }
            MainInput.Actions.Player.Enable();
            if (Game.Manager.gameState == GameState.Cutscene) Game.Manager.gameState = GameState.Playing;
        }

        // Doors a step moves here count as our player's (the scene's runner may be in another room).
        public bool DoorInActiveStep(GateDoor d) => d != null && active.Any(t => t.C != null && d.transform.IsChildOf(t.C.transform));
    }

    // One kind of story cutscene: how to play a step on another player's say-so, what it leaves behind, when it's over.
    internal abstract class CutsceneKind
    {
        public abstract Type Type { get; }
        public abstract string Name(int step);
        public override string ToString() => Type.Name;
        // Plays the step here as if our player had set it off.
        public abstract void Play(Component c, int step, NetDataReader extra);
        // Puts the world in the state the step leaves it in, without watching. false: not yet, try again later.
        public virtual bool SkipToEnd(Component c, int step) => true;
        public virtual bool Finished(Component c, int step, CutsceneSync.Tracker t, float now) => t.Calm(now) || (!t.SawBusy && now - t.T0 > 15f);
        // Enemies (NPCs) the step moves: run by our copy from now on.
        public virtual IEnumerable<Enemy> Actors(Component c, int step) => Enumerable.Empty<Enemy>();
        // Stops the step's own scripts (a copy that got stuck in it).
        public virtual void Abort(Component c, int step) { }
        // The step, finished here, is over for good (false: it can be set off again).
        public virtual bool Over(Component c, int step) => true;

        protected static bool Flag(BoolID b) => GameData.HasWorldData && GameData.gameBools.IsTrue(b);

        private static readonly System.Reflection.FieldInfo rootGraph = typeof(BehaviorGraph).GetField("RootGraph", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        private static System.Reflection.FieldInfo activeNodes;

        // What a graph is doing now (node type names).
        protected static IEnumerable<string> ActiveNodes(BehaviorGraphAgent a)
        {
            var root = a != null && a.Graph != null ? rootGraph?.GetValue(a.Graph) : null;
            if (root == null) return Enumerable.Empty<string>();
            if (activeNodes == null) activeNodes = root.GetType().GetField("m_ActiveNodes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            return activeNodes?.GetValue(root) is System.Collections.IList list ? list.Cast<object>().Select(n => n.GetType().Name).ToList() : Enumerable.Empty<string>();
        }

        protected static void StopAgent(BehaviorGraphAgent a)
        {
            if (a == null) return;
            try { a.End(); } catch { }
            a.enabled = false;
        }
    }

    // The tutorial's opening (cavern by the barracks): an acolyte kills the player, who wakes up in the barracks.
    // Set off by coming out of a house into the cavern.
    internal class IntroKind : CutsceneKind
    {
        public override Type Type => typeof(IntroCutscene);
        public override string Name(int step) => "intro";

        public static bool WouldPlay(IntroCutscene c) => c.gameObject.activeSelf && c.playedTimes == 0 && !Flag(BoolID.CutsceneIntroAcolyte);

        public override void Play(Component c, int step, NetDataReader extra) => ((IntroCutscene)c).Play();

        private static IEnumerable<Enemy> Acolytes(Component c) =>
            c.GetComponentsInChildren<Enemy>(true).Where(e => e.name.StartsWith("Mob_Acolyte"));

        public override IEnumerable<Enemy> Actors(Component c, int step) => Acolytes(c);

        public override bool Finished(Component c, int step, CutsceneSync.Tracker t, float now) =>
            Flag(BoolID.CutsceneIntroAcolyte) && t.Calm(now);

        public override bool SkipToEnd(Component c, int step)
        {
            // What the cutscene's last steps and the scene's "acolyte disabler" leave: the acolyte gone.
            foreach (var e in Acolytes(c))
            {
                if (e.gameObject.activeInHierarchy && !e.IsDead) e.Disables();
                e.gameObject.SetActive(false);
            }
            return true;
        }

        public override void Abort(Component c, int step) => StopAgent(((IntroCutscene)c).behaviorGraphAgent);
    }

    // Adeos attacked by spiderlings: set off by walking up to him; a fight, then he thanks the player.
    internal class AdeosKind : CutsceneKind
    {
        public override Type Type => typeof(AdeosCutscene);
        public override string Name(int step) => "adeos";

        public override void Play(Component c, int step, NetDataReader extra)
        {
            var a = (AdeosCutscene)c;
            if (Flag(a.cutsceneDone)) return;
            if (!a.hasPlayed) a.Play();   // normally done when the room is entered: the graph waits for the trigger
            // The graph listens from its first tick: send the trigger a few frames later.
            CutsceneSync.Instance.Later(3, () =>
            {
                var center = Game.LocalPlayer?.GetComponentsInChildren<Collider2D>(true).FirstOrDefault(x => x.CompareTag("PlayerCenter"));
                if (center != null) a.GetTriggerEnter(center);
                else a.triggerPlayer.SendEventMessage(true);
            });
        }

        public override IEnumerable<Enemy> Actors(Component c, int step) => new[] { ((AdeosCutscene)c).npcAdeos };

        // The flag comes from whoever beat the spiderlings first; our own copy is over when its graph is back to
        // waiting for the trigger (after the thanks).
        public override bool Finished(Component c, int step, CutsceneSync.Tracker t, float now)
        {
            var a = (AdeosCutscene)c;
            return Flag(a.cutsceneDone) && t.Calm(now) && ActiveNodes(a.behaviorGraphAgent).All(n => n == "ParallelAllComposite" || n == "StartOnEvent");
        }

        public override bool SkipToEnd(Component c, int step)
        {
            var a = (AdeosCutscene)c;
            if (!Flag(a.cutsceneDone)) return false;   // the fight isn't over yet
            // The game's own "already done" path (AdeosCutscene.Play).
            a.npcAdeos.transform.position = a.npcAdeosPosition.position;
            a.hasPlayed = true;
            StopAgent(a.behaviorGraphAgent);
            foreach (var e in a.enemies)
            {
                if (e == null) continue;
                e.isDead = true;   // not SetKilled: that would count as a fresh kill for everyone's quests
                e.gameObject.SetActive(false);
            }
            return true;
        }

        public override void Abort(Component c, int step) => StopAgent(((AdeosCutscene)c).behaviorGraphAgent);
    }

    // The humming sphere room: the acolyte drops the Minds Journal, the player picks it up (step 1), reads it and
    // the gate opens (step 2).
    internal class HummingKind : CutsceneKind
    {
        public const int Collect = 1, Gate = 2;
        public override Type Type => typeof(HummingSphereCutscene);
        public override string Name(int step) => step == Collect ? "humming" : "humming-gate";

        public override void Play(Component c, int step, NetDataReader extra)
        {
            var h = (HummingSphereCutscene)c;
            if (step == Collect)
            {
                if (extra.AvailableBytes >= 12)
                {
                    var p = new Vector3(extra.GetFloat(), extra.GetFloat(), extra.GetFloat());
                    h.collectPoint.position = p;
                    h.focusCamera.position = p;
                }
                var agent = h.GetComponent<BehaviorGraphAgent>();
                if (agent != null) agent.enabled = true;   // what the acolyte's drop does
            }
            else h.FinishCutscene();
        }

        public override bool Finished(Component c, int step, CutsceneSync.Tracker t, float now) =>
            step == Collect ? base.Finished(c, step, t, now) : Flag(BoolID.CutsceneAcolyteBattle) && t.Calm(now);

        public override bool SkipToEnd(Component c, int step)
        {
            if (step != Gate) return true;   // the journal is the player's own: nothing in the world
            var h = (HummingSphereCutscene)c;
            h.focusCamera.gameObject.SetActive(false);
            h.gateCamera.SetActive(false);
            if (h.gate.state != DoorState.Opened) h.gate.ForceOpen();
            // What the gate scene ends with, but the quest step: quests are shared already, and replaying it after
            // a reload could set a finished quest back.
            foreach (var ev in h.OnFinishEvents.events)
                if (ev != null && !(ev is CustomEvent.SetQuestState)) ev.Invoke();
            Game.Manager.RemoveResettable(h);
            return true;
        }

        public override void Abort(Component c, int step)
        {
            if (step == Collect) StopAgent(c.GetComponent<BehaviorGraphAgent>());
        }
    }

    // Tinetia in her cage: freed (step 1), she guides the player (2), she opens the portal out (4). Each is set off
    // by a line of her dialogue.
    internal class TinetiaKind : CutsceneKind
    {
        public const int Release = 1, Guide = 2, Portal = 4;
        public override Type Type => typeof(CutsceneTinetiaCage);
        public override string Name(int step) => step == Release ? "tinetia-release" : step == Guide ? "tinetia-guide" : "tinetia-portal";

        public override void Play(Component c, int step, NetDataReader extra)
        {
            var t = (CutsceneTinetiaCage)c;
            if (step == Release) t.ReleaseTinetia();
            else if (step == Guide) t.StartGuideCutscene();
            else t.StartActivePortalCutscene();
        }

        public override IEnumerable<Enemy> Actors(Component c, int step)
        {
            var e = ((CutsceneTinetiaCage)c).tinetiaNpc?.GetComponent<Enemy>();
            return e != null ? new[] { e } : Enumerable.Empty<Enemy>();
        }

        private static GameObject AgentOf(CutsceneTinetiaCage t, int step) => step == Guide ? t.agentGuideToPortal : step == Portal ? t.agentActivatePortal : null;

        public override bool Finished(Component c, int step, CutsceneSync.Tracker tr, float now)
        {
            var agent = AgentOf((CutsceneTinetiaCage)c, step);
            if (agent == null) return base.Finished(c, step, tr, now);
            // The graph switches its own object off at the very end.
            return !agent.activeSelf && !tr.Busy && now - tr.T0 > 1f;
        }

        public override bool SkipToEnd(Component c, int step)
        {
            var t = (CutsceneTinetiaCage)c;
            if (step == Release)
            {
                t.tinetiaNpc.transform.position = t.tinetiaReleasePosition.position;
                var nav = t.tinetiaNpc.GetComponents<Behaviour>().FirstOrDefault(b => b.GetType().Name == "NavMeshAgent");
                if (nav != null && t.tinetiaNpc.activeInHierarchy) nav.enabled = true;
            }
            else if (step == Portal)
            {
                // She leaves through the portal, which stays open: the way out of the tutorial.
                t.agentActivatePortal.SetActive(false);
                t.tinetiaNpc.SetActive(false);
                foreach (var p in t.GetComponentsInChildren<Portal>(true)) p.SetPortalActive(true);
            }
            return true;
        }

        public override void Abort(Component c, int step)
        {
            var agent = AgentOf((CutsceneTinetiaCage)c, step);
            if (agent != null) agent.SetActive(false);
        }
    }

    // Talking to Tinetia the first time (TinetiaInitialCutscene): E on her starts a behaviour graph (her moves, the
    // dialogue). She's an NPC the scene's runner moves, so on a guest she'd be a puppet; and nothing stopped two
    // players starting it at once. It plays for everyone in the room; it's over for good once her dialogue reached
    // TRIGGER_1 (the game then stops her being interactable), otherwise she can be talked to again (reopened).
    internal class TinetiaTalkKind : CutsceneKind
    {
        public override Type Type => typeof(TinetiaInitialCutscene);
        public override string Name(int step) => "tinetia-talk";

        public override void Play(Component c, int step, NetDataReader extra)
        {
            var t = (TinetiaInitialCutscene)c;
            var me = Game.LocalPlayer;
            if (!t.isOpened && t.isInteractable && me != null) t.OnInteract(me.gameObject);
        }

        public override IEnumerable<Enemy> Actors(Component c, int step)
        {
            var t = (TinetiaInitialCutscene)c;
            var found = new HashSet<Enemy>();
            var own = t.GetComponentInParent<Enemy>(true);
            if (own != null) found.Add(own);
            // NPCs the graph moves (its blackboard's objects).
            try
            {
                var vars = t.cutsceneAgent?.BlackboardReference?.Blackboard?.Variables;
                if (vars != null)
                    foreach (var v in vars)
                    {
                        var go = v.ObjectValue is Component vc ? vc.gameObject : v.ObjectValue as GameObject;
                        var e = go != null ? go.GetComponentInParent<Enemy>(true) : null;
                        if (e != null && e.GetComponent<PlayerMaster>() == null) found.Add(e);
                    }
            }
            catch { }
            return found;
        }

        public override bool Finished(Component c, int step, CutsceneSync.Tracker t, float now) =>
            !((TinetiaInitialCutscene)c).isOpened && base.Finished(c, step, t, now);

        public override bool Over(Component c, int step) => !((TinetiaInitialCutscene)c).isInteractable;

        // Someone else's talk ended it for good: she isn't to be talked to again here either.
        public override bool SkipToEnd(Component c, int step)
        {
            var t = (TinetiaInitialCutscene)c;
            t.isInteractable = false;
            t.isOpened = false;
            if (t.titlePivot != null) t.titlePivot.SetActive(false);
            try { t.bubbleImage?.Show(false); } catch { }
            return true;
        }

        public override void Abort(Component c, int step)
        {
            var t = (TinetiaInitialCutscene)c;
            StopAgent(t.cutsceneAgent);
            t.isOpened = false;
        }
    }

    // ---------------- entry points: our own player setting a step off ----------------

    [HarmonyPatch(typeof(TinetiaInitialCutscene), nameof(TinetiaInitialCutscene.OnInteract))]
    internal static class TinetiaTalkPatch
    {
        private static bool Prefix(TinetiaInitialCutscene __instance, out bool __state)
        {
            __state = false;
            if (CutsceneSync.Replaying || __instance.isOpened) return true;
            if (!CutsceneSync.AllowLocal(__instance, 1))
            {
                try { PlayerTextReaction.ShowText("Someone else is talking to her."); } catch { }
                return false;
            }
            __state = true;
            return true;
        }

        private static void Postfix(TinetiaInitialCutscene __instance, bool __state)
        {
            if (__state && __instance.isOpened) CutsceneSync.Started(__instance, 1);
        }
    }

    [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.Play))]
    internal static class IntroPlayPatch
    {
        private static bool Prefix(IntroCutscene __instance, out bool __state)
        {
            __state = false;
            if (CutsceneSync.Replaying || !IntroKind.WouldPlay(__instance)) return true;
            if (!CutsceneSync.AllowLocal(__instance, 1))
            {
                __instance.SetupSmoke();   // the cutscene's own "already seen" path
                return false;
            }
            __state = true;
            return true;
        }

        private static void Postfix(IntroCutscene __instance, bool __state)
        {
            if (__state) CutsceneSync.Started(__instance, 1);
        }
    }

    [HarmonyPatch(typeof(AdeosCutscene), nameof(AdeosCutscene.GetTriggerEnter))]
    internal static class AdeosTriggerPatch
    {
        private static bool Prefix(AdeosCutscene __instance, Collider2D other, out bool __state)
        {
            __state = false;
            if (CutsceneSync.Replaying || other == null || !other.CompareTag("PlayerCenter")) return true;
            if (!CutsceneSync.AllowLocal(__instance, 1))
            {
                __instance.triggerForward.OnTriggerEntered -= __instance.GetTriggerEnter;
                return false;
            }
            __state = true;
            return true;
        }

        private static void Postfix(AdeosCutscene __instance, bool __state)
        {
            if (__state) CutsceneSync.Started(__instance, 1);
        }
    }

    // The journal dropped: the room's graph starts (enabled by the drop) and this places the camera.
    [HarmonyPatch(typeof(HummingSphereCutscene), nameof(HummingSphereCutscene.SetCollectingPoint))]
    internal static class HummingCollectPatch
    {
        private static void Postfix(HummingSphereCutscene __instance)
        {
            var p = __instance.collectPoint.position;
            CutsceneSync.Started(__instance, HummingKind.Collect, w => { w.Put(p.x); w.Put(p.y); w.Put(p.z); });
        }
    }

    [HarmonyPatch(typeof(HummingSphereCutscene), nameof(HummingSphereCutscene.FinishCutscene))]
    internal static class HummingFinishPatch
    {
        private static bool Prefix(HummingSphereCutscene __instance, out bool __state)
        {
            __state = false;
            if (CutsceneSync.Replaying) return true;
            if (!CutsceneSync.AllowLocal(__instance, HummingKind.Gate)) return false;
            __state = true;
            return true;
        }

        private static void Postfix(HummingSphereCutscene __instance, bool __state)
        {
            if (__state) CutsceneSync.Started(__instance, HummingKind.Gate);
        }
    }

    [HarmonyPatch]
    internal static class TinetiaStepPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(CutsceneTinetiaCage), nameof(CutsceneTinetiaCage.ReleaseTinetia)),
            AccessTools.Method(typeof(CutsceneTinetiaCage), nameof(CutsceneTinetiaCage.StartGuideCutscene)),
            AccessTools.Method(typeof(CutsceneTinetiaCage), nameof(CutsceneTinetiaCage.StartActivePortalCutscene)),
        };

        private static int StepOf(System.Reflection.MethodBase m) =>
            m.Name == nameof(CutsceneTinetiaCage.ReleaseTinetia) ? TinetiaKind.Release : m.Name == nameof(CutsceneTinetiaCage.StartGuideCutscene) ? TinetiaKind.Guide : TinetiaKind.Portal;

        private static bool Prefix(CutsceneTinetiaCage __instance, System.Reflection.MethodBase __originalMethod, out bool __state)
        {
            __state = false;
            if (CutsceneSync.Replaying) return true;
            if (!CutsceneSync.AllowLocal(__instance, StepOf(__originalMethod))) return false;
            __state = true;
            return true;
        }

        private static void Postfix(CutsceneTinetiaCage __instance, System.Reflection.MethodBase __originalMethod, bool __state)
        {
            if (__state) CutsceneSync.Started(__instance, StepOf(__originalMethod));
        }
    }

    // ---------------- doors moved by a step playing here ----------------

    [HarmonyPatch]
    internal static class CutsceneDoorStatePatch
    {
        private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(SetDoorStateAction), "OnStart");

        private static void Prefix(out bool __state)
        {
            __state = CutsceneSync.Instance != null && CutsceneSync.Instance.HasActive && !ObjectSync.LocalAction;
            if (__state) ObjectSync.LocalAction = true;
        }

        private static void Postfix(bool __state)
        {
            if (__state) ObjectSync.LocalAction = false;
        }
    }

    [HarmonyPatch]
    internal static class CutsceneGatePatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(GateDoor), nameof(GateDoor.Open)),
            AccessTools.Method(typeof(GateDoor), nameof(GateDoor.Close)),
        };

        private static void Prefix(GateDoor __instance, out bool __state)
        {
            __state = CutsceneSync.Instance != null && !ObjectSync.LocalAction && CutsceneSync.Instance.DoorInActiveStep(__instance);
            if (__state) ObjectSync.LocalAction = true;
        }

        // After DoorSync's postfix has sent the door's new state.
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(bool __state)
        {
            if (__state) ObjectSync.LocalAction = false;
        }
    }
}