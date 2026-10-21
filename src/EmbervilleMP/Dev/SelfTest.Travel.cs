using System.Collections;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Independent travel (3 players: host #1, guest A #2, guest B #3).
        //   1. The host leaves for the crystal hub; A (lowest id left) runs the tutorial area, B follows A.
        //   2. B kills an enemy: A applies it.
        //   3. The host comes back: A hands the fight over; the enemy B killed stays dead on the host; everyone follows the host.
        [Scenario("travel", Timeout = 300, Players = 3, Quick = true)]
        private static IEnumerator TravelTest()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            yield return WaitFor(() => s.Ready && s.Players.Count >= 3, 90f);
            yield return WaitFor(() => Game.InGame && Game.SceneName == "TutorialArea", 90f);
            yield return Barrier("ready", 120f);
            var es = EnemySync.Instance;
            byte me = s.LocalId;
            const string hub = "VitromotusCrystal";

            if (s.IsHost)
            {
                // The hub's first visit plays a cutscene that moves you on to the Cartographer's house; skip it.
                GameData.gameBools[BoolID.CutsceneCrystal] = true;
                GuestWorld.Travel(hub, "", Vector3.zero, newWorld: false);
                yield return WaitFor(() => Game.InGame && Game.SceneName == hub, 90f);
                Check("host went to the hub", Game.SceneName == hub);
            }
            else
            {
                yield return TeleportTo(CombatArea, me == 2 ? new Vector2(51.8f, 76.3f) : new Vector2(53f, 77f));
            }
            yield return Barrier("split", 120f);

            if (!s.IsHost)
            {
                // The tutorial area is now A's.
                yield return WaitFor(() => es.Authority == 2 && (me == 2 ? EnemySync.IsAuthority : EnemySync.GuestPuppets), 20f);
                Check("guest A runs the tutorial area without the host", es.Authority == 2, $"authority #{es.Authority}");
                if (me == 3) Check("guest B follows A's enemies", EnemySync.GuestPuppets && es.StatesApplied > 0, $"{es.StatesApplied} states");
                yield return Barrier("authority", 40f);
                if (me == 3)
                {
                    var victim = NearestAlive(CombatArea, Game.LocalPlayer.transform.position);
                    if (Check("B found an enemy", victim != null))
                    {
                        Chat.Send("#victim=" + es.IdOf(victim));
                        CombatSystem.Resolve(Hit(Game.LocalPlayer, victim, 99999));
                        yield return WaitFor(() => victim.IsDead, 6f);
                        Check("B's kill went through A", victim.IsDead);
                    }
                }
                else
                {
                    yield return WaitFor(() => peerData.ContainsKey("victim"), 20f);
                    var victim = es.ById(int.Parse(peerData["victim"]));
                    yield return WaitFor(() => victim != null && victim.IsDead, 6f);
                    Check("A applied B's kill", victim != null && victim.IsDead, $"{es.HitsApplied} hits applied");
                }
            }
            else yield return Barrier("authority", 40f);
            yield return Barrier("killed", 40f);

            // The host comes back to the guests' room.
            if (s.IsHost)
            {
                GuestWorld.Travel("TutorialArea", CombatArea, new Vector3(52.5f, 76.6f, 0f), newWorld: false);
                yield return WaitFor(() => Game.InGame && Game.SceneName == "TutorialArea" && Game.AreaName == CombatArea, 90f);
                Check("host came back to the guests' room", Game.SceneName == "TutorialArea" && Game.AreaName == CombatArea, $"{Game.SceneName}/{Game.AreaName}");
                yield return WaitFor(() => es.HandoffsApplied > 0, 10f);
                Check("host got the fight handed over", es.HandoffsApplied > 0);
                yield return WaitFor(() => peerData.ContainsKey("victim"), 5f);
                var victim = peerData.TryGetValue("victim", out var v) ? es.ById(int.Parse(v)) : null;
                Check("the enemy B killed is dead on the host too", victim != null && (victim.IsDead || !victim.gameObject.activeInHierarchy));
                Check("host runs the scene again", EnemySync.IsAuthority);
            }
            else
            {
                yield return WaitFor(() => es.Authority == Protocol.HostId && EnemySync.GuestPuppets, 120f);
                Check("guests follow the host again", es.Authority == Protocol.HostId && EnemySync.GuestPuppets, $"authority #{es.Authority}");
                if (me == 2) Check("A handed the fight over", es.HandoffsSent > 0);
            }
            Plugin.Log.LogInfo($"[enemy] authority changes={es.AuthorityChanges} handoffs={es.HandoffsSent}/{es.HandoffsApplied} hits={es.HitsSent}/{es.HitsApplied}");
            yield return Barrier("done", 60f);
            yield return Finish("independent travel works");
        }
    }
}
