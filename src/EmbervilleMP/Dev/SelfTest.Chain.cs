using System.Collections;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    // Chained tests: what makes nearly every scenario able to follow any other in one boot.
    //   - each scenario's switches (loot mode, estate, keep playing...) are the host's rules for it: sent to the
    //     guests again before it starts (SyncRules);
    //   - a scenario that starts with guests coming from their own world or from the main menu gets them there first,
    //     and they join again from there (GuestsLeave), as a boot with --mp-join-later / --mp-join would have them;
    //   - a scenario that ended the session for someone (a guest left or was kicked, gave up on a host that dropped)
    //     is followed by everyone coming back the way a player would: the host hosts again, a guest joins from where
    //     it is (EnsureSession).
    internal static partial class SelfTest
    {
        private static byte lastLocalId;   // our player id in the session (a guest joins again as it)

        private static bool Loading() => Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading;

        private static void NoteSession()
        {
            var s = Session.Current;
            if (s != null && s.Ready && !s.IsHost) lastLocalId = s.LocalId;
        }

        // The host's address as this guest was given it (--mp-join or --mp-join-later).
        private static bool HostTarget(out string ip, out int port)
        {
            string t = Args.Join ?? Args.Get("join-later");
            ip = null;
            port = 0;
            if (t == null || t == "lan" || t == "lobby") return false;
            var parts = t.Split(':');
            ip = parts[0];
            port = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : Plugin.Settings.Port.Value;
            return true;
        }

        private static IEnumerator JoinAgain(string ip, int port, float delay)
        {
            if (delay > 0f) yield return new WaitForSecondsRealtime(delay);   // (as --mp-join-later: a moment in game first)
            Plugin.Log.LogInfo($"[dev] Joining {ip}:{port} again as #{lastLocalId}");
            if (MPRunner.Instance.StartJoin(ip, port, MPWindow.DefaultName(), MPRunner.ArgTransport) && Session.Current != null && lastLocalId > Protocol.HostId)
                Session.Current.RejoinAs = lastLocalId;   // the same player id as before: scenarios tell players apart by it
        }

        // A scenario without a fresh world ([Scenario(Fresh = false)]): only if every copy can go on where it is (all in
        // the scenario's scene, in the session); otherwise a fresh world for all, never half and half.
        private static bool carryAgreed;
        // Host: the guests as they were when the scenario started (before the rules barrier: one that leaves right after it
        // may be back before we look).
        private static System.Collections.Generic.List<PlayerInfo> othersAtStart = new System.Collections.Generic.List<PlayerInfo>();

        private static IEnumerator AgreeToCarryOn()
        {
            bool mine = DevHarness.CanCarryOn(Args.AutoStartScene ?? "TutorialArea");
            Chat.Send("#carry=" + (mine ? 1 : 0));
            int others = Args.Int("players", 2) - 1;
            var b = Bucket(scenarioIndex);
            System.Func<string, int> n = key => b.signals.TryGetValue(key, out var set) ? set.Count : 0;
            yield return WaitFor(() => n("#carry=1") + n("#carry=0") >= others, 10f);
            carryAgreed = mine && n("#carry=1") >= others;
            Plugin.Log.LogInfo($"[dev] Same world as the last scenario: {(carryAgreed ? "yes" : "no, a fresh one")}");
        }

        // The host's rules for this scenario, at every guest before it starts (rules and control lines take the same
        // ordered channel: past the barrier, the guests have them).
        private static IEnumerator SyncRules()
        {
            MPRunner.Instance.Presence?.SendLookAgain();   // (everyone is attached again now: our look reaches them)
            NoteSession();
            var s = Session.Current;
            othersAtStart = s != null ? s.Others.ToList() : new System.Collections.Generic.List<PlayerInfo>();
            if (s != null && s.IsHost)
            {
                LootRules.Instance?.ResendRules();
                EstateRules.Instance?.ResendRules();
                TeamTeleport.Instance?.ResendRules();
            }
            yield return Barrier("rules", 30f, sync: true);
        }

        // The guests leave and join again during the scenario: from their own world (toMenu false) or the main menu.
        private static IEnumerator GuestsLeave(bool toMenu)
        {
            var s = Session.Current;
            if (s == null) yield break;
            string how = toMenu ? "the main menu" : "their own world";
            if (s.IsHost)
            {
                // (these very players gone: one that left and is back already is another PlayerInfo)
                var before = othersAtStart;
                System.Func<bool> gone = () => Session.Current != null && before.All(p => !Session.Current.Players.Values.Contains(p));
                yield return WaitFor(() => Session.Current == null || gone(), 30f);
                if (!Check($"the guests left, to come back from {how}", gone())) Abort("the guests didn't leave");
                yield break;
            }
            if (!Check("the host's address is known (to join again)", HostTarget(out var ip, out int port))) yield break;
            NoteSession();
            int returns = GuestWorld.ReturnsHome;
            yield return ConfirmBeforeLeaving();   // (the host has our "rules")
            GuestWorld.ReturnToMenu = toMenu;
            s.Stop();
            yield return WaitFor(() => GuestWorld.ReturnsHome > returns && !GuestWorld.Switching && !Loading() && (Game.InGame || Game.SceneName == Game.MainMenuScene), 60f);
            GuestWorld.ReturnToMenu = false;
            if (!toMenu && !Game.InGame)
            {
                // (this copy came from the main menu at the start: into its own world first)
                DevHarness.LoadOwnWorld("TutorialArea");
                yield return WaitFor(() => Game.InGame && !Loading(), 90f);
            }
            bool there = toMenu ? Game.SceneName == Game.MainMenuScene : Game.InGame && !GuestWorld.Active;
            Check($"back in {how}, to join from there", there, Game.SceneName);
            if (toMenu) DevHarness.CharacterForJoining();
            MPRunner.Instance.StartCoroutine(JoinAgain(ip, port, toMenu ? 0f : 2f));
        }

        // The end of a scenario, in step: everyone back in the session (EnsureSession), past the "scenario-end" barrier,
        // and the guests move on only when the host says so ("#go", or the host's signals from the next scenario). A
        // copy whose session went away meanwhile (a kick noticed late) comes back and tries again: one bad scenario
        // costs that scenario, never the rest of the chain out of step.
        private static bool inStep;

        private static IEnumerator InStep()
        {
            inStep = false;
            var bucket = Bucket(scenarioIndex);
            System.Func<bool> go = () => (bucket.signals.TryGetValue("#go", out var set) && set.Contains(Protocol.HostId))
                || (peerAt.TryGetValue(Protocol.HostId, out int at) && at > scenarioIndex);
            for (int attempt = 0; attempt < 3 && !inStep; attempt++)
            {
                yield return EnsureSession();
                if (!EveryoneHere()) continue;
                yield return Barrier("scenario-end", 120f, sync: true);
                if (!lastBarrierOk) continue;
                if (Args.Host)
                {
                    Chat.Send("#go");
                    inStep = true;
                    yield break;
                }
                yield return WaitFor(() => go() || !EveryoneHere(), 30f);
                inStep = go();
            }
            if (!inStep) Plugin.Log.LogWarning($"[dev] Couldn't get back in step after {current}");
        }

        // Everyone in the session again after a scenario that ended it for someone.
        private static IEnumerator EnsureSession()
        {
            if (EveryoneHere() && !MPRunner.Instance.Reconnecting) yield break;
            Plugin.Log.LogInfo("[dev] Putting the session back together");
            yield return WaitFor(() => !MPRunner.Instance.Reconnecting, 90f);
            if (Args.Host)
            {
                if (Session.Current == null)
                {
                    // A guest still trying to reconnect must give up first, as the scenario expects: the time it takes to
                    // notice (the connection's 8 s timeout) and its reconnect window.
                    yield return new WaitForSecondsRealtime(Mathf.Min(8f + Args.Int("reconnect", 60) + 5f, 80f));
                    Plugin.Log.LogInfo("[dev] Hosting again");
                    MPRunner.Instance.StartHost(Args.HostPort, MPWindow.DefaultName(), MPRunner.ArgTransport);
                }
                yield return WaitFor(EveryoneHere, 150f);
            }
            else
            {
                if (Session.Current == null && HostTarget(out var ip, out int port))
                {
                    yield return WaitFor(() => !GuestWorld.Switching && !Loading() && (Game.InGame || Game.SceneName == Game.MainMenuScene), 60f);
                    float until = Time.realtimeSinceStartup + 150f;
                    while (Time.realtimeSinceStartup < until && !(Session.Current != null && Session.Current.Ready))
                    {
                        if (Session.Current == null)
                        {
                            if (Game.SceneName == Game.MainMenuScene) DevHarness.CharacterForJoining();
                            yield return JoinAgain(ip, port, 0f);
                        }
                        // (the host may not be hosting again yet: then try once more)
                        float t = Time.realtimeSinceStartup + 12f;
                        while (Time.realtimeSinceStartup < t && Session.Current != null && !Session.Current.Ready) yield return null;
                        if (Session.Current != null && !Session.Current.Ready) Session.Current.Stop();
                        if (Session.Current == null) yield return new WaitForSecondsRealtime(2f);
                    }
                }
                // in the host's world again
                yield return WaitFor(() => EveryoneHere() && GuestWorld.Active && Game.InGame && !GuestWorld.Switching && !Loading(), 90f);
            }
            Plugin.Log.LogInfo($"[dev] Session back together: {EveryoneHere()}");
        }
    }
}
