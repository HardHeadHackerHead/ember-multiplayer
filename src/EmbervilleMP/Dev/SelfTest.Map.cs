using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // The world map, shared (MapSync). The demo has no dungeon (DungeonScene), so its map is never built in play:
        // both copies put up the same portal list the map would build from the shared seed, then play the game's own
        // calls on it (ChunkPortal.Activate's state change, GameManager's ClearFog, story markers, the portal item).
        //   mapshare      : the guest explores and unlocks a portal -> on the host and in its save; the host's unlock
        //                   and marker -> on the guest; quick portals placed at the same moment get different ids,
        //                   the same on both; the guest writes nothing of the host's world.
        //   mapshare-late : (--mp-join-later) the host changes its map before and while the guest joins; the guest ends
        //                   up with all of it; back in its own world, the guest's map isn't the host's.
        [Scenario("mapshare", Timeout = 200)]
        [Scenario("mapshare-late", Timeout = 240, Start = "own-world")]
        private static IEnumerator MapShareTest()
        {
            HookPeerData();
            if (current == "mapshare-late") { yield return MapShareLate(); yield break; }
            yield return WaitInGame();
            var s = Session.Current;
            yield return WaitFor(() => s.Ready && s.Players.Count >= 2, 90f);
            yield return WaitFor(() => Game.InGame && (s.IsHost || (GuestWorld.Active && MapSync.Instance != null && MapSync.Instance.FullApplied > 0)) && !GuestWorld.Switching, 90f);
            var map = MapSync.Live;
            if (!Check("the world map exists here", map != null && MapSync.Instance != null)) { yield return Finish(""); yield break; }
            yield return Barrier("ready", 120f);

            // (picked before the barrier: nobody unlocks anything until both have picked the same two)
            bool seeded = SeedTestPortals(map);
            var ordered = map.PortalStates.Where(p => p.CurrentState.Value == PortalState.State.Inactive)
                .OrderBy(p => p.Region).ThenBy(p => p.Position.x).ThenBy(p => p.Position.y).ToList();
            var fogBefore = new HashSet<Vector2Int>(map.ClearedFogOfWar);
            yield return Barrier("seeded");
            if (!Check("two locked portals to unlock", ordered.Count >= 2, $"{map.PortalStates.Count} portals, {ordered.Count} locked")) { yield return Finish(""); yield break; }
            PortalState guestPortal = ordered[0], hostPortal = ordered[ordered.Count - 1];

            if (!s.IsHost)
            {
                // Explore a room away from the host, as the game does in a dungeon (ClearFog around the player, once a second).
                var area = Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(a => a.name == "004_Gather");
                Vector2 to = area != null && area.CameraConfiner != null ? (Vector2)area.CameraConfiner.bounds.center : (Vector2)Game.LocalPlayer.transform.position + Vector2.right * 40f;
                yield return TeleportTo("004_Gather", to);
                Vector2 a1 = Game.LocalPlayer.transform.position, a2 = a1 + new Vector2(6f, 2f);
                map.ClearFog(a1, 4);
                yield return new WaitForSecondsRealtime(1f);
                map.ClearFog(a2, 4);
                Chat.Send("#fogat=" + V(a1) + ";" + V(a2));
                guestPortal.CurrentState.Value = PortalState.State.Active;   // what ChunkPortal.Activate does
                map.AddMarker(new WorldMapMarker { Name = "mp_test_marker_guest", Position = new Vector2(12f, 34f), Radius = 30f });
            }
            else
            {
                hostPortal.CurrentState.Value = PortalState.State.Active;
                map.AddMarker(new WorldMapMarker { Name = "mp_test_marker_host", Position = new Vector2(-8f, 20f), Radius = 30f });
            }
            yield return Barrier("changed");

            if (s.IsHost)
            {
                yield return WaitFor(() => peerData.ContainsKey("fogat"), 10f);
                var cells = peerData.TryGetValue("fogat", out var fa) ? fa.Split(';').SelectMany(p => MapSync.FogCells(P(p), 4)).Distinct().ToList() : new List<Vector2Int>();
                yield return WaitFor(() => cells.Count > 0 && cells.All(c => map.ClearedFogOfWar.Contains(c)), 10f);
                Check("the guest's explored cells are explored on the host", cells.Count > 0 && cells.All(c => map.ClearedFogOfWar.Contains(c)),
                    $"{cells.Count(c => map.ClearedFogOfWar.Contains(c))}/{cells.Count}");
                Check("...and were new to the host", cells.Any(c => !fogBefore.Contains(c)), $"{cells.Count(c => !fogBefore.Contains(c))} new");
                yield return WaitFor(() => guestPortal.CurrentState.Value == PortalState.State.Active, 10f);
                Check("the guest's unlocked portal is unlocked on the host", guestPortal.CurrentState.Value == PortalState.State.Active, $"region {guestPortal.Region} at {guestPortal.Position}");
                yield return WaitFor(() => map.markers.ContainsKey("mp_test_marker_guest"), 10f);
                Check("the guest's map marker is on the host's map", map.markers.ContainsKey("mp_test_marker_guest"));
                Chat.Send("#hostcells=" + cells.Count);
            }
            else
            {
                yield return WaitFor(() => hostPortal.CurrentState.Value == PortalState.State.Active, 10f);
                Check("the host's unlocked portal is unlocked on the guest", hostPortal.CurrentState.Value == PortalState.State.Active, $"region {hostPortal.Region} at {hostPortal.Position}");
                yield return WaitFor(() => map.markers.ContainsKey("mp_test_marker_host"), 10f);
                Check("the host's map marker is on the guest's map", map.markers.ContainsKey("mp_test_marker_host"));
            }
            // (counted before the barrier: past it, the other copy's portal can arrive before we look)
            int before = map.PlacedPortals.Count;
            yield return Barrier("checked");

            // Quick portals placed at the same moment: the game would call both "QuickPortal0".
            map.AddQuickPortal(Game.LocalPlayer.transform, MapSync.LocalPrefab(), new QuickPortalType.MainMap(0, 0));
            System.Func<bool> settled = () => map.PlacedPortals.Count >= before + 2 && map.PlacedPortals.All(p => !p.ID.Contains("_"));
            yield return WaitFor(settled, 15f);
            string ids = string.Join(",", map.PlacedPortals.Select(p => p.ID).OrderBy(x => x));
            Check("both quick portals are on our map with the host's ids", settled(), ids);
            Check("the two quick portals have different ids", map.PlacedPortals.Select(p => p.ID).Distinct().Count() == map.PlacedPortals.Count, ids);
            Chat.Send("#qp=" + ids);
            yield return WaitFor(() => peerData.ContainsKey("qp"), 15f);
            Check("quick portal ids agree on both", peerData.TryGetValue("qp", out var theirs) && theirs == ids, $"{ids} vs {theirs}");
            yield return Barrier("quick");

            if (s.IsHost)
            {
                // Everything is in the host's save.
                var cells = peerData.TryGetValue("fogat", out var fa) ? fa.Split(';').SelectMany(p => MapSync.FogCells(P(p), 4)).Distinct().ToList() : new List<Vector2Int>();
                GameData.SaveData();
                WorldMapState.SavedData saved = null;
                try { saved = JsonUtility.FromJson<WorldMapState.SavedData>(SaveLoad.Load(GameData.GetWorldPrefix + ".evm")); }
                catch (System.Exception e) { Plugin.Log.LogWarning("[test] map save: " + e.Message); }
                Check("the host saved its map", saved != null && saved.PortalStates != null);
                if (saved != null && saved.PortalStates != null)
                {
                    var sp = saved.PortalStates.FirstOrDefault(p => p.Region == guestPortal.Region && p.Position == guestPortal.Position);
                    Check("the guest's portal is unlocked in the host's save", sp != null && sp.CurrentState.Value == PortalState.State.Active);
                    var sf = new HashSet<Vector2Int>(saved.ClearedFog ?? new List<Vector2Int>());
                    Check("the guest's explored cells are in the host's save", cells.Count > 0 && cells.All(sf.Contains), $"{cells.Count(sf.Contains)}/{cells.Count}");
                    Check("the guest's marker is in the host's save", saved.Markers != null && saved.Markers.Any(m => m.Name == "mp_test_marker_guest"));
                    string savedIds = string.Join(",", (saved.PlaceablePortal ?? new List<QuickPortalModel>()).Select(p => p.ID).OrderBy(x => x));
                    Check("the quick portals are in the host's save under the agreed ids", savedIds == ids, savedIds);
                }
                Check("the host sent the guest its whole map", MapSync.Instance.FullSent > 0);
            }
            else
            {
                // A guest never writes the host's world, its map included.
                string saves = Path.GetDirectoryName(SaveLoad.GetPath("x"));
                GameData.SaveData();
                yield return new WaitForSecondsRealtime(0.5f);
                var written = Directory.GetFiles(saves).Select(Path.GetFileName).Where(f => f.StartsWith(GuestWorld.GuestPrefix)).ToList();
                Check("the guest wrote no file of the host's world", written.Count == 0, string.Join(",", written));
            }
            Plugin.Log.LogInfo("[map] " + MapSync.Instance.Stats);
            yield return Barrier("done");
            // The test's stand-in map goes away again. (The guest stays in 004_Gather, and the host keeps that room
            // running for it: a chain's next world swap unloads it, which UnloadGuardPatches makes safe; see mapfresh.)
            if (seeded) MapSync.LoadWorldMap(null);
            yield return Finish("world map shared");
        }

        private static IEnumerator MapShareLate()
        {
            if (Args.Host)
            {
                yield return WaitInGame();
                var map = MapSync.Live;
                if (!Check("the world map exists here", map != null)) { yield return Finish(""); yield break; }
                // Before anyone joins (the world snapshot carries these).
                bool seeded = SeedTestPortals(map);
                var unlock =map.PortalStates.Where(p => p.CurrentState.Value == PortalState.State.Inactive).OrderBy(p => p.Region).ThenBy(p => p.Position.x).First();
                unlock.CurrentState.Value = PortalState.State.Active;
                Vector2 basePos = Game.LocalPlayer.transform.position;
                map.ClearFog(basePos, 6);
                map.AddMarker(new WorldMapMarker { Name = "mp_late_marker", Position = new Vector2(5f, 5f), Radius = 30f });
                yield return WaitFor(() => Session.Current != null && Session.Current.Players.Count >= 2, 90f);
                // While the guest gets and loads our world: changes it can't take yet (it asks for the whole map once in).
                map.AddQuickPortal(Game.LocalPlayer.transform, MapSync.LocalPrefab(), new QuickPortalType.MainMap(0, 1));
                float until = Time.realtimeSinceStartup + 90f;
                for (int i = 1; !peerData.ContainsKey("loaded") && Time.realtimeSinceStartup < until; i++)
                {
                    map.ClearFog(basePos + new Vector2(i * 3f, (i % 3) * 2f), 3);
                    yield return new WaitForSecondsRealtime(0.3f);
                }
                Check("the guest loaded our world", peerData.ContainsKey("loaded"));
                yield return new WaitForSecondsRealtime(2.5f);   // the last deltas go out (once a second)
                Chat.Send("#hostmap=" + FogSignature(map) + "|" + unlock.Region + ":" + unlock.Position.x + ":" + unlock.Position.y
                          + "|" + string.Join(",", map.PlacedPortals.Select(p => p.ID).OrderBy(x => x)));
                yield return Barrier("compared", 60f);
                yield return WaitFor(() => Session.Current.Players.Count == 1, 60f);
                Check("the guest left", Session.Current.Players.Count == 1);
                Plugin.Log.LogInfo("[map] " + MapSync.Instance.Stats);
                if (seeded) MapSync.LoadWorldMap(null);
                yield return Finish("late join gets the whole map (host)");
                yield break;
            }

            // guest: plays its own world first (--mp-join-later)
            yield return WaitInGame();
            yield return WaitFor(() => GuestWorld.Active && Game.InGame && !GuestWorld.Switching && GuestWorld.Instance.SnapshotsLoaded > 0, 120f);
            yield return WaitFor(() => MapSync.Instance != null && MapSync.Instance.FullApplied > 0, 30f);
            Check("got the host's whole map after loading its world", MapSync.Instance != null && MapSync.Instance.FullApplied > 0);
            Chat.Send("#loaded=1");
            yield return WaitFor(() => peerData.ContainsKey("hostmap"), 30f);
            yield return new WaitForSecondsRealtime(2f);
            var gm = MapSync.Live;
            var parts = peerData.TryGetValue("hostmap", out var hm) ? hm.Split('|') : new string[0];
            if (Check("the world map exists here", gm != null) && Check("the host sent its map summary", parts.Length == 3))
            {
                Check("every cell the host explored is explored here", FogSignature(gm) == parts[0], $"{FogSignature(gm)} vs {parts[0]}");
                var pp = parts[1].Split(':').Select(int.Parse).ToArray();
                var ps = gm.PortalStates.FirstOrDefault(p => p.Region == pp[0] && p.Position == new Vector2Int(pp[1], pp[2]));
                Check("the host's unlocked portal is unlocked here", ps != null && ps.CurrentState.Value == PortalState.State.Active, parts[1]);
                Check("the host's marker is here", gm.markers.ContainsKey("mp_late_marker"));
                string ids = string.Join(",", gm.PlacedPortals.Select(p => p.ID).OrderBy(x => x));
                Check("the host's quick portal is here, same id", ids == parts[2] && ids.Length > 0, $"{ids} vs {parts[2]}");
            }
            Plugin.Log.LogInfo("[map] " + MapSync.Instance.Stats);
            yield return Barrier("compared", 60f);

            // Home again: our own world's map, not the host's.
            int returns = GuestWorld.ReturnsHome;
            yield return ConfirmBeforeLeaving();   // (the host has our "compared" before we go)
            Session.Current.Stop();
            yield return WaitFor(() => GuestWorld.ReturnsHome > returns && !GuestWorld.Switching && Game.InGame, 60f);
            yield return new WaitForSecondsRealtime(1f);
            var own = MapSync.Live;
            Check("back in our own world", Game.InGame && !GuestWorld.Active);
            Check("our own map doesn't keep the host's", own != null && !own.markers.ContainsKey("mp_late_marker") && !own.PortalStates.Any() && !own.PlacedPortals.Any(),
                own == null ? "no map" : $"{own.markers.Count} markers, {own.PortalStates.Count} portals, {own.PlacedPortals.Count} quick portals, {own.ClearedFogOfWar.Count} cells");
            yield return Finish("late join gets the whole map (guest)");
        }

        // The portal list the map builds from the world's seed (SetupInitialState: region 0 open, the others locked).
        // true: they were put up (the test takes them down again at its end).
        private static bool SeedTestPortals(WorldMapState map)
        {
            if (map.PortalStates.Count > 0) return false;   // (a real map: use it)
            for (int region = 0; region < 3; region++)
                for (int k = 0; k < 2; k++)
                    map.PortalStates.Add(new PortalState(region == 0 ? PortalState.State.Active : PortalState.State.Inactive)
                    { Region = region, Position = new Vector2Int(region * 10 + k, 5) });
            return true;
        }

        // A chain's next world after mapshare (mapshare,mapfresh): the scene starts for real (no bare singletons
        // left from the unload) and the world map is the new world's, not the last one's. Valid anywhere in a chain.
        [Scenario("mapfresh", Timeout = 120)]
        private static IEnumerator MapFreshTest()
        {
            yield return WaitInGame();
            var s = Session.Current;
            yield return WaitFor(() => s != null && s.Ready && s.Players.Count >= 2, 90f);
            yield return WaitFor(() => Game.InGame && (s.IsHost || GuestWorld.Active) && !GuestWorld.Switching, 90f);
            var managers = Object.FindObjectsByType<GameManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Check("one real GameManager", managers.Length == 1 && managers[0].settings != null, $"{managers.Length}, settings {(managers.Length > 0 && managers[0].settings != null)}");
            var drops = Object.FindObjectsByType<ItemDropHandler>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Check("one real ItemDropHandler", drops.Length == 1 && drops[0].itemDropObjectPrefab != null, $"{drops.Length}");
            var map = MapSync.Live;
            Check("the world map is the new world's (empty)", map != null && map.PortalStates.Count == 0 && map.markers.Count == 0 && map.PlacedPortals.Count == 0,
                map == null ? "no map" : $"{map.PortalStates.Count} portals, {map.markers.Count} markers, {map.PlacedPortals.Count} quick portals, {map.ClearedFogOfWar.Count} cells");
            yield return Barrier("done");
            yield return Finish("fresh world after the map test");
        }

        private static string FogSignature(WorldMapState map)
        {
            long h = 0;
            foreach (var c in map.ClearedFogOfWar) h += ((long)c.x * 73856093L) ^ ((long)c.y * 19349663L);
            return map.ClearedFogOfWar.Count + ":" + h;
        }

        private static string V(Vector2 v) => v.x.ToString("R", CultureInfo.InvariantCulture) + ":" + v.y.ToString("R", CultureInfo.InvariantCulture);

        private static Vector2 P(string s)
        {
            var p = s.Split(':');
            return new Vector2(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture));
        }
    }
}
