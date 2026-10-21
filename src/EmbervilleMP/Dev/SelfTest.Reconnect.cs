using System.Collections;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // The guest's network dies mid-game (no goodbye: the host only notices by the timeout). The guest keeps
        // playing in the host's world, reconnects by itself, gets the world again and stays where it was.
        [Scenario("reconnect", Timeout = 240, Start = "boot", Quick = true)]
        private static IEnumerator ReconnectTest()
        {
            yield return WaitInGame();
            if (Args.Host)
            {
                yield return WaitFor(() => Session.Current.Players.Count >= 2, 90f);
                yield return Barrier("arrived", 120f);
                int sent = GuestWorld.Instance.SnapshotsSent;
                // the guest goes silent, then comes back as a new connection
                yield return WaitFor(() => GuestWorld.Instance.SnapshotsSent > sent, 60f);
                Check("the guest came back and got the world again", GuestWorld.Instance.SnapshotsSent > sent);
                yield return Barrier("rejoined", 60f);
                Check("the old connection was replaced at once, same id (two players, not three)",
                    Session.Current.Players.Count == 2 && Session.Current.Players.ContainsKey(2), string.Join(",", Session.Current.Players.Values));
                RemotePlayer guest = null;
                yield return WaitFor(() => (guest = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault(r => r.Shown)) != null, 20f);
                Check("host sees the guest again", guest != null);
                yield return Barrier("done");
                yield return Finish("reconnect (host)");
                yield break;
            }

            yield return WaitFor(() => GuestWorld.Active && Game.InGame && GuestWorld.Instance.SnapshotsLoaded > 0 && !GuestWorld.Switching, 120f);
            yield return new WaitForSecondsRealtime(1f);
            yield return Barrier("arrived", 60f);

            // walk away from the host a little so "stays where it was" means something
            var me = Game.LocalPlayer;
            Vector3 spot = me.transform.position + Vector3.left * 2f;
            me.rb.position = spot;
            me.transform.position = spot;
            yield return new WaitForSecondsRealtime(0.5f);
            spot = me.transform.position;
            string scene = Game.SceneName;
            int returns = GuestWorld.ReturnsHome;

            Session.Current.SimulateDrop();
            yield return null;
            Check("reconnecting after the drop", MPRunner.Instance.Reconnecting && GuestWorld.Active);
            yield return WaitFor(() => Session.Current != null && Session.Current.Ready && !MPRunner.Instance.Reconnecting, 60f);
            Check("reconnected", Session.Current != null && Session.Current.Ready, Session.Current?.Status ?? "no session");
            Check("no ghost of ourselves", Session.Current != null && Session.Current.Players.Count == 2 && Session.Current.LocalId == 2,
                string.Join(",", Session.Current?.Players.Values.Select(p => p.ToString()) ?? new string[0]));
            yield return WaitFor(() => GuestWorld.Rejoins > 0 && Game.InGame && !GuestWorld.Switching, 60f);
            yield return new WaitForSecondsRealtime(1f);
            Check("still in the host's world, never sent home", GuestWorld.Active && GuestWorld.ReturnsHome == returns && GameData.worldData.Name != null);
            float d = Game.LocalPlayer != null ? Vector2.Distance(Game.LocalPlayer.transform.position, spot) : -1f;
            Check("stayed where it was", Game.SceneName == scene && d >= 0f && d < 2f, $"{Game.SceneName}, {d:F1} away");
            yield return Barrier("rejoined", 60f);
            RemotePlayer host = null;
            yield return WaitFor(() => (host = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault(r => r.Shown)) != null, 20f);
            Check("guest sees the host again", host != null);
            yield return Barrier("done");
            yield return Finish("reconnect (guest)");
        }

        // The host is gone for good (run with --mp-reconnect=8): the guest gives up and goes home as after leaving.
        [Scenario("reconnect-giveup", Timeout = 240, Start = "menu", Switches = "reconnect=8")]
        private static IEnumerator ReconnectGiveUpTest()
        {
            yield return WaitInGame();
            if (Args.Host)
            {
                yield return WaitFor(() => Session.Current.Players.Count >= 2, 90f);
                yield return Barrier("arrived", 120f);
                yield return new WaitForSecondsRealtime(1f);
                Session.Current.SimulateDrop();   // gone without a word: the guest only notices by the timeout
                // (nothing more to do here: our socket is closed now, whether this copy stays up or quits)
                yield return new WaitForSecondsRealtime(1f);
                yield return Finish("reconnect-giveup (host)");
                yield break;
            }
            // (later in a chain, each copy plays a fresh world of its own: no snapshot of the host's then)
            yield return WaitFor(() => GuestWorld.Active && Game.InGame && (GuestWorld.Instance.SnapshotsLoaded > 0 || scenarioIndex > 0) && !GuestWorld.Switching, 120f);
            yield return Barrier("arrived", 60f);
            int returns = GuestWorld.ReturnsHome;
            yield return WaitFor(() => MPRunner.Instance.Reconnecting, 30f);
            Check("noticed the lost host and tries to reconnect", MPRunner.Instance.Reconnecting);
            yield return WaitFor(() => !MPRunner.Instance.Reconnecting, 40f);
            Check("gave up", !MPRunner.Instance.Reconnecting && Session.Current == null);
            yield return WaitFor(() => GuestWorld.ReturnsHome > returns && !GuestWorld.Switching && Game.SceneName == "MainMenu", 60f);
            Check("back on the main menu", Game.SceneName == "MainMenu" && !GuestWorld.Active, Game.SceneName);
            yield return Finish("reconnect-giveup (guest)");
        }
    }
}
