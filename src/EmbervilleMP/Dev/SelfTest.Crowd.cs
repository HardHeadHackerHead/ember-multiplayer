using System.Collections;
using System.Linq;
using EmbervilleMP.Net;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // A full session (--mp-players, up to 4): everyone sees everyone else, and a fifth player would be refused.
        [Scenario("crowd", Timeout = 240, Players = 4)]
        private static IEnumerator CrowdTest()
        {
            yield return WaitInGame();
            var s = Session.Current;
            int players = Args.Int("players", 4);
            yield return WaitFor(() => s.Ready && s.Players.Count >= players, 120f);
            Check($"{players} players in the session", s.Players.Count >= players, $"{s.Players.Count}");
            yield return Barrier("all-here", 120f);
            var presence = MPRunner.Instance.Presence;
            yield return WaitFor(() => presence.Remotes.Values.Count(r => r.Shown) >= players - 1, 30f);
            Check("everyone else is visible", presence.Remotes.Values.Count(r => r.Shown) >= players - 1,
                string.Join(", ", presence.Remotes.Values.Select(r => $"{r.Info.Name}:{(r.Shown ? "shown" : "hidden")}")));
            yield return Barrier("done", 60f);
            yield return Finish("full session works");
        }
    }
}
