using System.Collections;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Shared world, part 1: the guest plays in the host's world and never writes it (or its own) to disk.
        //   worldjoin        : the guest comes from the main menu (character only) -> back to the menu after
        //   worldjoin-ingame : the guest was playing its own world (--mp-join-later) -> back to its own world after
        [Scenario("worldjoin", Timeout = 180, Start = "menu", Quick = true, Isolate = "worldjoin")]
        [Scenario("worldjoin-ingame", Timeout = 180, Start = "own-world")]
        private static IEnumerator WorldJoinTest()
        {
            HookPeerData();
            bool fromMenu = current == "worldjoin";
            var s0 = Session.Current;
            bool isHost = Args.Host;
            if (isHost)
            {
                // Mark our world as soon as it's loaded (before any snapshot can go out; later in a chain it is loaded,
                // and maybe marked by an earlier run of this scenario).
                yield return WaitFor(() => GameData.HasWorldData && (scenarioIndex > 0 || GameData.worldData.Name.Contains("Hosty")), 60f);
                GameData.worldData.Name = "Host world " + Random.Range(1000, 9999);
                GameData.gameBools[BoolID.MetAdeos] = true;
                yield return WaitInGame();
                yield return WaitFor(() => Session.Current.Players.Count >= 2, 60f);
                yield return WaitFor(() => GuestWorld.Instance.SnapshotsSent > 0, 30f);
                Check("host sent its world", GuestWorld.Instance.SnapshotsSent > 0);
                Check("host's character and world backed up before others played in it", Backups.Made > 0 && Directory.Exists(Backups.Folder), Backups.Folder);
                yield return Barrier("arrived", 90f);
                yield return WaitFor(() => Session.Current.Players.Count == 1, 60f);
                Check("guest left", Session.Current.Players.Count == 1);
                Check("host still in its own world", GameData.worldData.Name.StartsWith("Host world") && !GuestWorld.Active);
                yield return Finish("world join (host)");
                yield break;
            }

            // guest
            string saves = Path.GetDirectoryName(SaveLoad.GetPath("x"));
            string ownWorld = null;
            if (!fromMenu)
            {
                yield return WaitInGame();
                ownWorld = GameData.worldData.Name;
                Plugin.Log.LogInfo($"[test] Playing own world '{ownWorld}' before joining");
            }
            yield return WaitFor(() => GuestWorld.Active && Game.InGame && GuestWorld.Instance.SnapshotsLoaded > 0, 120f);
            yield return new WaitForSecondsRealtime(1f);
            Check("guest loaded the host's world", GuestWorld.Active && GuestWorld.Instance.SnapshotsLoaded > 0);
            Check("it is the host's world (name)", GameData.worldData.Name.StartsWith("Host world"), GameData.worldData?.Name);
            Check("host's story flag came along", GameData.gameBools.IsTrue(BoolID.MetAdeos));
            Check("guest's character backed up before joining", Backups.Made > 0 &&
                Directory.GetDirectories(Backups.Folder).Any(d => Directory.GetFiles(d).Any(f => Path.GetFileName(f).StartsWith(DevHarness.PlayerFile))), Backups.Folder);
            var presence = MPRunner.Instance.Presence;
            RemotePlayer host = null;
            yield return WaitFor(() => (host = presence.Remotes.Values.FirstOrDefault(r => r.Shown)) != null, 20f);
            float d = host != null ? Vector2.Distance(host.Latest.Position, Game.LocalPlayer.transform.position) : -1;
            Check("guest arrived next to the host", host != null && d < 4f, $"distance {d:F1}, scene {Game.SceneName}/{Game.AreaName}");
            yield return Barrier("arrived", 60f);

            // Leave by changing scene once (the game saves on every scene change) and check what reached the disk.
            Game.Manager.TeleportPlayerFade(Game.LocalPlayer.transform.position + Vector3.right, Direction.Right).Forget();
            yield return new WaitForSecondsRealtime(0.5f);
            yield return WaitFor(() => Game.Manager.gameState == GameState.Playing, 10f);
            GameData.SaveData();
            yield return new WaitForSecondsRealtime(0.5f);
            var guestFiles = Directory.GetFiles(saves).Select(Path.GetFileName).Where(f => f.StartsWith(GuestWorld.GuestPrefix)).ToList();
            Check("no file of the host's world was written by the guest", guestFiles.Count == 0, string.Join(",", guestFiles));
            Check("the guest's character file is saved", File.Exists(SaveLoad.GetPath(DevHarness.PlayerFile + ".evp")));

            int returns = GuestWorld.ReturnsHome;
            Session.Current.Stop();
            yield return WaitFor(() => GuestWorld.ReturnsHome > returns && !GuestWorld.Switching && (fromMenu ? Game.SceneName == "MainMenu" : Game.InGame), 60f);
            yield return new WaitForSecondsRealtime(1f);
            if (fromMenu) Check("back on the main menu after leaving", Game.SceneName == "MainMenu");
            else Check("back in its own world after leaving", Game.InGame && GameData.worldData.Name == ownWorld, $"{GameData.worldData?.Name} vs {ownWorld}");
            yield return Finish("world join (guest)");
        }
    }
}
