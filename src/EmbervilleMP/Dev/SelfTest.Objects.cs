using System.Collections;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using EmbervilleMP.Sync.Objects;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Doors (the object framework's reference handler): the guest opens a door, the host sees it open;
        // the host closes it, the guest sees it closed.
        [Scenario("doors", Timeout = 200, Quick = true)]
        private static IEnumerator DoorsTest()
        {
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            yield return WaitFor(() => s.Ready && s.Players.Count >= 2, 90f);
            yield return WaitFor(() => Game.InGame && (s.IsHost || GuestWorld.Active) && !GuestWorld.Switching, 90f);
            yield return Barrier("ready", 120f);

            // Same choice on both copies: the first closed, interactable door by scene path.
            var doors = Object.FindObjectsByType<GateDoor>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(d => d.state == DoorState.Closed && d.GetComponentInParent<GameArea>(true) != null)
                .OrderBy(d => NetIds.ScenePath(d.transform)).ToList();
            Plugin.Log.LogInfo($"[test] {doors.Count} closed doors in {Game.SceneName}");
            var door = doors.FirstOrDefault();
            if (!Check("found a closed door", door != null)) { yield return Finish(""); yield break; }
            Plugin.Log.LogInfo($"[test] door id {ObjectSync.Instance.IdOf(door)} path {NetIds.ScenePath(door.transform)}");
            var area = door.GetComponentInParent<GameArea>(true);
            yield return TeleportTo(area.name, (Vector2)door.transform.position + Vector2.down * (s.IsHost ? 2f : 1.5f));
            yield return Barrier("at-door", 60f);

            if (!s.IsHost)
            {
                door.OnInteract(Game.LocalPlayer.gameObject);
                yield return WaitFor(() => door.state == DoorState.Opened, 5f);
                Check("guest opened the door here", door.state == DoorState.Opened);
            }
            yield return Barrier("opened");
            if (s.IsHost)
            {
                yield return WaitFor(() => door.state == DoorState.Opened, 5f);
                Check("the guest's door opened on the host", door.state == DoorState.Opened, door.state.ToString());
                yield return new WaitForSecondsRealtime(1f);
                door.OnInteract(Game.LocalPlayer.gameObject);
                yield return WaitFor(() => door.state == DoorState.Closed, 5f);
            }
            yield return Barrier("closed");
            if (!s.IsHost)
            {
                yield return WaitFor(() => door.state == DoorState.Closed, 5f);
                Check("the host's door closed on the guest", door.state == DoorState.Closed, door.state.ToString());
            }
            var os = ObjectSync.Instance;
            Plugin.Log.LogInfo($"[objects] sent {os.StatesSent} applied {os.StatesApplied} snapshots {os.SnapshotsSent}/{os.SnapshotsApplied}");
            yield return Barrier("done");
            yield return Finish("doors in sync");
        }
    }
}
