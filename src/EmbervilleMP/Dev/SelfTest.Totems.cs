using System.Collections;
using System.Linq;
using System.Reflection;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using EmbervilleMP.Sync.Objects;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Totem puzzles (TotemFirstStage + TotemSecondStage in a DungeonRoomEvent). No demo scene has one (the
        // classes are in the game's code, no scene or prefab uses them), so this builds two, the same way on both
        // copies, in the start room. Puzzle 1: the guest starts it, both see the host's sequence, the guest, the
        // host and the guest again hit its totems in order, the count agrees after each hit, it completes once on
        // both. Puzzle 2: the host starts it, the guest hits a wrong totem, it fails and resets once on both; the
        // retry plays one new sequence for both, the host and the guest solve it.
        [Scenario("totems", Timeout = 200)]
        private static IEnumerator TotemsTest()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            yield return WaitFor(() => s.Ready && s.Players.Count >= 2, 90f);
            yield return WaitFor(() => Game.InGame && (s.IsHost || GuestWorld.Active) && !GuestWorld.Switching, 90f);
            yield return Barrier("ready", 120f);
            string role = s.IsHost ? "host" : "guest", other = s.IsHost ? "guest" : "host";
            // Counters are static: in a chain of scenarios they carry over, so compare with these.
            int intents0 = TotemSync.Intents, sent0 = TotemSync.HitsSent, shown0 = TotemSync.SequencesShown, adopted0 = TotemSync.SequencesAdopted;
            const string room = "001_Barrack";
            var spot = new Vector2(-104f, 70f);
            yield return TeleportTo(room, spot + new Vector2(s.IsHost ? -1.5f : 1.5f, -2f));
            yield return Barrier("placed", 60f);

            int done1 = 0, failed1 = 0, done2 = 0, failed2 = 0;
            var ev1 = BuildTotemPuzzle("MP_TestTotems1", spot + new Vector2(0f, 3f), () => done1++, () => failed1++);
            var ev2 = BuildTotemPuzzle("MP_TestTotems2", spot + new Vector2(0f, 6f), () => done2++, () => failed2++);
            // The object table was built when the scene loaded: look again.
            typeof(ObjectSync).GetField("liveScene", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(ObjectSync.Instance, -1);
            Check("the puzzles are totem events", TotemSync.IsTotemEvent(ev1) && TotemSync.IsTotemEvent(ev2));
            yield return Barrier("built");

            // ---- puzzle 1: started by the guest, solved together ----
            if (!s.IsHost)
            {
                ev1.StartEvent();
                Check("the guest didn't start it on its own", ev1.state == DungeonRoomEvent.EventState.ready, ev1.state.ToString());
            }
            int[] order = null;
            yield return SharedSequence(ev1, "p1", role, other, o => order = o);
            if (order == null) { yield return Finish(""); yield break; }
            if (!s.IsHost) Check("the guest played the host's sequence, not one of its own", TotemSync.SequencesShown - shown0 == 1 && TotemSync.SequencesAdopted > adopted0, $"{TotemSync.SequencesShown - shown0} shown, {TotemSync.SequencesAdopted - adopted0} adopted");
            // Read before the barrier: the guest may hit (and its intent arrive) before our side of it resumes.
            int intentsBefore = TotemSync.Intents;
            yield return Barrier("p1-hits");

            for (int step = 0; step < order.Length; step++)
            {
                bool guestHits = step % 2 == 0;
                string who = guestHits ? "guest" : "host";
                if (guestHits != s.IsHost) TotemsOf(ev1)[order[step]].OnHit();
                if (step < order.Length - 1)
                {
                    yield return WaitFor(() => TotemSync.SecondStageOf(ev1)?.totemsHit == step + 1, 5f);
                    var st2 = TotemSync.SecondStageOf(ev1);
                    Check($"after the {who}'s hit {step + 1} both count {step + 1}", st2 != null && st2.totemsHit == step + 1, $"{st2?.totemsHit}");
                    if (guestHits == s.IsHost)
                        Check($"the {who}'s hit lit totem {order[step]} here", TotemsOf(ev1)[order[step]].lightEffect.activeSelf);
                    if (s.IsHost && guestHits) Check("the guest's hit went to the host", TotemSync.Intents == intentsBefore + 1, $"{TotemSync.Intents - intentsBefore}");
                    intentsBefore = TotemSync.Intents;
                    yield return Barrier("p1-hit" + step, 30f);
                }
            }
            yield return WaitFor(() => ev1.state == DungeonRoomEvent.EventState.completed, 5f);
            Check("puzzle 1 is solved here", ev1.state == DungeonRoomEvent.EventState.completed, $"{ev1.state}, stage {ev1.currentStage}");
            yield return new WaitForSecondsRealtime(0.5f);
            Check("its success ran once here, no failure", done1 == 1 && failed1 == 0, $"{done1} done, {failed1} failed");
            Check("its totems are still standing", TotemSync.FirstStageOf(ev1) != null);
            yield return Barrier("p1-done");

            // ---- puzzle 2: the guest's wrong hit resets it for both, then the retry ----
            if (s.IsHost) ev2.StartEvent();
            order = null;
            yield return SharedSequence(ev2, "p2", role, other, o => order = o);
            if (order == null) { yield return Finish(""); yield break; }
            yield return Barrier("p2-hits");
            if (!s.IsHost)
            {
                int wrong = (order[0] + 1) % TotemsOf(ev2).Length;
                TotemsOf(ev2)[wrong].OnHit();
            }
            yield return WaitFor(() => failed2 > 0 && ev2.state == DungeonRoomEvent.EventState.ready, 5f);
            yield return new WaitForSecondsRealtime(0.5f);
            Check("the guest's wrong hit failed puzzle 2 once here", failed2 == 1 && done2 == 0, $"{failed2} failed, {done2} done");
            Check("and reset it here", ev2.state == DungeonRoomEvent.EventState.ready && ev2.currentStage == 0 && ev2.instantiatedStages.All(go => go == null),
                $"{ev2.state}, stage {ev2.currentStage}, {ev2.instantiatedStages.Count(go => go != null)} stages");
            yield return Barrier("p2-reset");

            if (!s.IsHost) ev2.StartEvent();
            int[] first = order;
            order = null;
            yield return SharedSequence(ev2, "p3", role, other, o => order = o);
            if (order == null) { yield return Finish(""); yield break; }
            Plugin.Log.LogInfo($"[test] retry sequence {string.Join(",", order)} (first try {string.Join(",", first)})");
            yield return Barrier("p3-hits");
            for (int step = 0; step < order.Length; step++)
            {
                bool hostHits = step % 2 == 0;
                if (hostHits == s.IsHost) TotemsOf(ev2)[order[step]].OnHit();
                if (step < order.Length - 1)
                {
                    yield return WaitFor(() => TotemSync.SecondStageOf(ev2)?.totemsHit == step + 1, 5f);
                    Check($"retry: after hit {step + 1} both count {step + 1}", TotemSync.SecondStageOf(ev2)?.totemsHit == step + 1, $"{TotemSync.SecondStageOf(ev2)?.totemsHit}");
                    yield return Barrier("p3-hit" + step, 30f);
                }
            }
            yield return WaitFor(() => ev2.state == DungeonRoomEvent.EventState.completed, 5f);
            yield return new WaitForSecondsRealtime(0.5f);
            Check("the retry solved puzzle 2 here, once", ev2.state == DungeonRoomEvent.EventState.completed && done2 == 1 && failed2 == 1, $"{ev2.state}, {done2} done, {failed2} failed");
            if (s.IsHost) Check("the host counted every guest hit it was sent", TotemSync.Intents - intents0 == 4, (TotemSync.Intents - intents0).ToString());
            else Check("the guest sent its hits to the host", TotemSync.HitsSent - sent0 == 4 && TotemSync.SequencesShown - shown0 == 3, $"{TotemSync.HitsSent - sent0} sent, {TotemSync.SequencesShown - shown0} sequences shown");

            Plugin.Log.LogInfo($"[totems] intents {TotemSync.Intents} sent {TotemSync.HitsSent} counted {TotemSync.HitsCounted} adopted {TotemSync.SequencesAdopted} shown {TotemSync.SequencesShown} late {TotemSync.LateTotems}");
            yield return Barrier("done");
            Object.Destroy(ev1.gameObject);
            Object.Destroy(ev2.gameObject);
            yield return Finish("totem puzzles shared: one sequence, every hit counted by the runner, one outcome");
        }

        private static Totem[] TotemsOf(DungeonRoomEvent ev) => TotemSync.FirstStageOf(ev)?.totems ?? new Totem[0];

        // Waits for the puzzle's second stage on this copy, then checks both copies play the same sequence.
        private static IEnumerator SharedSequence(DungeonRoomEvent ev, string key, string role, string other, System.Action<int[]> result)
        {
            yield return WaitFor(() => ev.currentStage == 1 && TotemSync.SecondStageOf(ev)?.state == DungeonRoomEventStage.StageState.active, 15f);
            var st1 = TotemSync.FirstStageOf(ev);
            int[] order = st1?.totemOrder;
            if (!Check($"{key}: the sequence played and the hits stage started here", TotemSync.SecondStageOf(ev)?.state == DungeonRoomEventStage.StageState.active && order != null,
                $"{ev.state}, stage {ev.currentStage}, sequence {(order != null ? string.Join(",", order) : "none")}"))
                yield break;
            string mine = string.Join(",", order);
            Chat.Send($"#{key}-{role}={mine}");
            yield return WaitFor(() => peerData.ContainsKey($"{key}-{other}"), 10f);
            peerData.TryGetValue($"{key}-{other}", out var theirs);
            Check($"{key}: both copies have the same sequence", theirs == mine, $"{role} {mine}, {other} {theirs}");
            Check($"{key}: the hits stage uses it", TotemSecondStage.totemOrder != null && string.Join(",", TotemSecondStage.totemOrder) == mine);
            result(order);
        }

        // A totem puzzle as the game's classes expect it: an event whose first stage holds the totems (lit in a
        // random sequence of 3), whose second stage counts the hits; it can be retried after a wrong hit.
        private static DungeonRoomEvent BuildTotemPuzzle(string name, Vector2 at, System.Action onDone, System.Action onFailed)
        {
            var evGo = new GameObject(name);
            evGo.SetActive(false);
            evGo.transform.position = at;
            var st1 = new GameObject("Stage1_Sequence");
            st1.SetActive(false);
            st1.transform.SetParent(evGo.transform, false);
            var first = st1.AddComponent<TotemFirstStage>();
            first.patternLength = 3;
            first.totemLightUpSpeed = 0.25f;
            for (int i = 0; i < 4; i++)
            {
                var t = new GameObject("Totem" + i);
                t.transform.SetParent(st1.transform, false);
                t.transform.localPosition = new Vector3(i * 1.5f - 2.25f, 0f, 0f);
                var light = new GameObject("Light");
                light.transform.SetParent(t.transform, false);
                t.AddComponent<Totem>().lightEffect = light;
            }
            var st2 = new GameObject("Stage2_Hits");
            st2.SetActive(false);
            st2.transform.SetParent(evGo.transform, false);
            first.secondStage = st2.AddComponent<TotemSecondStage>();
            var ev = evGo.AddComponent<DungeonRoomEvent>();
            ev.stageCooldown = 0.3f;
            ev.canRetry = true;
            ev.onEventCompleted.AddListener(() => onDone());
            ev.onEventFailed.AddListener(() => onFailed());
            evGo.SetActive(true);
            return ev;
        }
    }
}
