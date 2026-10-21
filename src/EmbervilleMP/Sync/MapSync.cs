using System;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync.Objects;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Phase D5: the world map, shared (WorldMapState: what the map screen shows and the .evm file saves).
    //   - fog of war: the union of everyone's explored cells, sent as deltas once a second;
    //   - region portals (PortalState): unlocked by anyone, unlocked for everyone, never locked again;
    //   - map markers (story-placed: the game has no player-made ones): added and removed for everyone;
    //   - quick portals (placed with the portal item): the host gives them their ids, so a guest's portal and
    //     the host's never share one (the game numbers them per machine; it even gives every one "QuickPortal0").
    // Every copy keeps a record of what was shared this session and merges it into the live map whenever the game
    // (re)loads it: the map is only built when it's first opened or a dungeon loads, from the saved copy
    // (GameData.worldMapData), which would otherwise drop what arrived before. The host's save gets everything;
    // guests only hold it in memory (their world files are never written, see GuestWorld). A guest that has loaded
    // the host's world asks for the host's whole map once, so changes made after the world snapshot left reach it.
    // None of this is reachable by play in the demo (no DungeonScene); the data paths are the same in EA.
    internal class MapSync
    {
        public static MapSync Instance { get; private set; }
        public static bool Applying;   // we're changing the live map ourselves (the hooks below must not echo it)
        private static int loading;    // inside WorldMapState.LoadSavedData
        public int FogSent, FogApplied, PortalsSent, PortalsApplied, MarkersSent, MarkersApplied, QuickPlaced, QuickApplied, QuickRemoved, FullSent, FullApplied;

        private enum Op : byte { Fog = 1, Portal = 2, MarkerAdd = 3, MarkerRemove = 4, FullEnd = 5 }
        private enum QOp : byte { Request = 1, Placed = 2, Removed = 3 }
        private const int FogChunk = 4000;   // cells per message (8 bytes each)

        private readonly Session session;
        private readonly HashSet<Vector2Int> fog = new HashSet<Vector2Int>();
        private readonly HashSet<(int region, Vector2Int pos)> portals = new HashSet<(int, Vector2Int)>();
        private readonly Dictionary<string, WorldMapMarker> markers = new Dictionary<string, WorldMapMarker>();
        private readonly HashSet<string> removedMarkers = new HashSet<string>();
        private readonly Dictionary<string, QuickPortalModel> quick = new Dictionary<string, QuickPortalModel>();
        private readonly HashSet<string> removedQuick = new HashSet<string>();
        private readonly List<Vector2Int> pendingFog = new List<Vector2Int>();
        private float nextFlush, nextScan, nextPrefabLookup;
        private WorldData worldRef;        // the world this record belongs to
        private WorldMapState lastLive;
        private bool requested;
        private float requestedAt;
        private int fullAtRequest;
        private int provisionalCount;

        public MapSync(Session session)
        {
            Instance = this;
            this.session = session;
            session.On(Msg.MapOps, OnOps);
            session.On(Msg.MapQuickPortal, OnQuick);
            session.On(Msg.MapRequest, OnRequest);
        }

        public static WorldMapState Live => Singleton<WorldMap>.HasMain ? Singleton<WorldMap>.main.WorldMapState : null;

        // The host shares its own world; a guest only once it plays in the host's.
        private bool Sharing => Session.Current == session && session.Ready && (session.IsHost ? !GuestWorld.Active : GuestWorld.Active);
        private static MapSync Current => Instance != null && Instance.Sharing ? Instance : null;

        public string Stats => $"fog {FogSent}/{FogApplied} portals {PortalsSent}/{PortalsApplied} markers {MarkersSent}/{MarkersApplied} " +
                               $"quick {QuickPlaced}/{QuickApplied}/{QuickRemoved} full {FullSent}/{FullApplied}";

        private void ClearShared()
        {
            fog.Clear(); portals.Clear(); markers.Clear(); removedMarkers.Clear(); quick.Clear(); removedQuick.Clear(); pendingFog.Clear();
        }

        // ---------------- per frame (in game) ----------------

        public void Update()
        {
            if (!Sharing || !GameData.HasWorldData) return;
            if (GameData.worldData != worldRef)
            {
                if (worldRef != null) ClearShared();
                worldRef = GameData.worldData;
                requested = false;
            }
            var live = Live;
            if (live != null && live != lastLive) { lastLive = live; MergeInto(live); }   // a new WorldMap (scene)
            // No answer after a while (the host's syncs weren't up yet: it was still loading the same fresh world, and
            // a message nobody listens to is gone): ask again.
            if (requested && !session.IsHost && FullApplied == fullAtRequest && Time.realtimeSinceStartup - requestedAt > 5f) requested = false;
            if (!session.IsHost && !requested && !GuestWorld.Switching && Game.InGame)
            {
                requested = true;
                requestedAt = Time.realtimeSinceStartup;
                fullAtRequest = FullApplied;
                session.Send(Msg.MapRequest, null);
                Plugin.Log.LogInfo("[map] Asking the host for its whole map");
            }
            float now = Time.realtimeSinceStartup;
            if (now >= nextFlush) { nextFlush = now + 1f; FlushFog(); }
            if (now >= nextScan && live != null) { nextScan = now + 0.5f; ScanPortals(live); FixPrefabs(live); }
        }

        // Portals change through a reactive property (ChunkPortal.Activate and the game's other paths): a light scan
        // of the list (a few dozen) both ways: our unlocks out, everyone's unlocks into a map built late.
        private void ScanPortals(WorldMapState live)
        {
            foreach (var ps in live.PortalStates)
            {
                var key = (ps.Region, ps.Position);
                if (ps.CurrentState.Value == PortalState.State.Active)
                {
                    if (!portals.Add(key)) continue;
                    session.Send(Msg.MapOps, w => PutPortal(w, key));
                    PortalsSent++;
                }
                else if (portals.Contains(key)) Unlock(ps);
            }
        }

        private void Unlock(PortalState ps)
        {
            Applying = true;
            try { ps.CurrentState.Value = PortalState.State.Active; }   // the chunk's portal and the map button follow it
            finally { Applying = false; }
            PortalsApplied++;
        }

        private void FlushFog()
        {
            if (pendingFog.Count == 0) return;
            for (int i = 0; i < pendingFog.Count; i += FogChunk)
            {
                int start = i, n = Math.Min(FogChunk, pendingFog.Count - i);
                session.Send(Msg.MapOps, w => PutFog(w, pendingFog, start, n));
            }
            FogSent += pendingFog.Count;
            pendingFog.Clear();
        }

        // Everything shared so far, into a live map that may not have it (just built or loaded, or a new scene's).
        private void MergeInto(WorldMapState live)
        {
            Applying = true;
            try
            {
                var added = new List<Vector2Int>();
                foreach (var c in fog) if (live.ClearedFogOfWar.Add(c)) added.Add(c);
                RaiseFog(live, added);
                foreach (var ps in live.PortalStates)
                    if (ps.CurrentState.Value != PortalState.State.Active && portals.Contains((ps.Region, ps.Position))) ps.CurrentState.Value = PortalState.State.Active;
                foreach (var m in markers.Values) live.AddMarker(m);
                foreach (var name in removedMarkers) live.RemoveMarker(name);
                foreach (var q in quick.Values)
                    if (!live.placedPortals.Any(x => x.ID == q.ID)) live.placedPortals.Add(q);
            }
            finally { Applying = false; }
            foreach (var id in removedQuick) RemoveQuickHere(live, id);
        }

        // The map screen's fog tiles clear through this event (WorldMapFogRenderer).
        private static readonly System.Reflection.FieldInfo FogEvent = AccessTools.Field(typeof(WorldMapState), "OnUpdatedFog");

        private static void RaiseFog(WorldMapState live, List<Vector2Int> cells)
        {
            if (cells.Count == 0) return;
            try { (FogEvent?.GetValue(live) as Action<List<Vector2Int>>)?.Invoke(cells); }
            catch (Exception e) { Plugin.Log.LogWarning("[map] Fog redraw: " + e.Message); }
        }

        // ---------------- local changes (from the hooks) ----------------

        public void OnLocalFog(WorldMapState st, Vector2 pos, int range)
        {
            if (Applying || st != Live) return;
            foreach (var c in FogCells(pos, range)) if (fog.Add(c)) pendingFog.Add(c);
        }

        // The cells WorldMapState.ClearFog clears (same formula).
        public static IEnumerable<Vector2Int> FogCells(Vector2 pos, int range)
        {
            Vector2 v = pos / 10f * 6f;
            for (int i = -range; i <= range; i++)
                for (int j = -range; j <= range; j++)
                    if (!(new Vector2Int(i, j).magnitude > range)) yield return new Vector2Int((int)v.x + i, (int)v.y + j);
        }

        public void OnLocalMarker(WorldMapState st, WorldMapMarker m, bool added)
        {
            if (Applying || loading > 0 || st != Live || m == null) return;
            if (added) { markers[m.Name] = m; removedMarkers.Remove(m.Name); }
            else { markers.Remove(m.Name); removedMarkers.Add(m.Name); }
            session.Send(Msg.MapOps, w => { if (added) PutMarker(w, m); else { w.Put((byte)Op.MarkerRemove); w.Put(m.Name); } });
            MarkersSent++;
        }

        public void OnLocalQuickPortal(WorldMapState st, QuickPortalModel model)
        {
            if (Applying || st != Live || model == null) return;
            if (session.IsHost)
            {
                model.ID = UniqueQuickId(model);
                quick[model.ID] = model;
                session.Send(Msg.MapQuickPortal, w => PutPlaced(w, model, session.LocalId, ""));
                QuickPlaced++;
                Plugin.Log.LogInfo($"[map] Placed quick portal {model.ID}");
                return;
            }
            // A guest's id is provisional (unique, usable at once) until the host names it.
            string provisional = $"QuickPortal_{session.LocalId}_{++provisionalCount}_{(int)(Time.realtimeSinceStartup * 1000f) % 100000}";
            model.ID = provisional;
            session.Send(Msg.MapQuickPortal, w => { w.Put((byte)QOp.Request); w.Put(provisional); PutModel(w, model); });
            QuickPlaced++;
            Plugin.Log.LogInfo($"[map] Placed quick portal {provisional}; asking the host for its id");
        }

        public void OnLocalQuickRemoved(WorldMapState st, QuickPortalModel model)
        {
            if (Applying || st != Live || model == null || string.IsNullOrEmpty(model.ID)) return;
            quick.Remove(model.ID);
            if (!removedQuick.Add(model.ID)) return;
            string id = model.ID;
            session.Send(Msg.MapQuickPortal, w => { w.Put((byte)QOp.Removed); w.Put(id); });
            QuickRemoved++;
        }

        public void OnLoaded(WorldMapState st)
        {
            if (st == Live) MergeInto(st);
        }

        // ---------------- messages ----------------

        private static void PutFog(NetDataWriter w, IList<Vector2Int> cells, int start, int n)
        {
            w.Put((byte)Op.Fog);
            w.Put(n);
            for (int i = start; i < start + n; i++) { w.Put(cells[i].x); w.Put(cells[i].y); }
        }

        private static void PutPortal(NetDataWriter w, (int region, Vector2Int pos) key)
        {
            w.Put((byte)Op.Portal);
            w.Put(key.region);
            w.Put(key.pos.x);
            w.Put(key.pos.y);
        }

        private static void PutMarker(NetDataWriter w, WorldMapMarker m)
        {
            w.Put((byte)Op.MarkerAdd);
            w.Put(m.Name ?? "");
            w.Put(m.Position.x);
            w.Put(m.Position.y);
            w.Put(m.Radius);
        }

        private void OnOps(byte from, NetDataReader r)
        {
            if (!Sharing) return;   // a guest not yet in the host's world: it asks for the whole map once there
            var live = Live;
            try
            {
                while (r.AvailableBytes > 0)
                {
                    switch ((Op)r.GetByte())
                    {
                        case Op.Fog:
                        {
                            int n = r.GetInt();
                            var added = new List<Vector2Int>();
                            for (int i = 0; i < n; i++)
                            {
                                var c = new Vector2Int(r.GetInt(), r.GetInt());
                                fog.Add(c);
                                if (live != null && live.ClearedFogOfWar.Add(c)) added.Add(c);
                            }
                            FogApplied += added.Count;
                            RaiseFog(live, added);
                            break;
                        }
                        case Op.Portal:
                        {
                            var key = (r.GetInt(), new Vector2Int(r.GetInt(), r.GetInt()));
                            portals.Add(key);
                            var ps = live?.PortalStates.FirstOrDefault(x => x.Region == key.Item1 && x.Position == key.Item2);
                            if (ps != null && ps.CurrentState.Value != PortalState.State.Active) Unlock(ps);
                            break;
                        }
                        case Op.MarkerAdd:
                        {
                            var m = new WorldMapMarker { Name = r.GetString(), Position = new Vector2(r.GetFloat(), r.GetFloat()), Radius = r.GetFloat() };
                            markers[m.Name] = m;
                            removedMarkers.Remove(m.Name);
                            if (live != null && !live.markers.ContainsKey(m.Name))
                            {
                                Applying = true;
                                try { live.AddMarker(m); } finally { Applying = false; }
                                MarkersApplied++;
                            }
                            break;
                        }
                        case Op.MarkerRemove:
                        {
                            string name = r.GetString();
                            markers.Remove(name);
                            removedMarkers.Add(name);
                            if (live != null && live.markers.ContainsKey(name))
                            {
                                Applying = true;
                                try { live.RemoveMarker(name); } finally { Applying = false; }
                                MarkersApplied++;
                            }
                            break;
                        }
                        case Op.FullEnd:
                            FullApplied++;
                            Plugin.Log.LogInfo($"[map] Got the host's whole map: {fog.Count} explored cells, {portals.Count} portals, {markers.Count} markers, {quick.Count} quick portals");
                            break;
                        default:
                            return;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[map] Bad map message: " + e.Message); }
        }

        // Host: a guest that has loaded our world wants the whole map (its snapshot may have missed later changes).
        private void OnRequest(byte from, NetDataReader r)
        {
            if (!session.IsHost || !Sharing) return;
            var live = Live;
            var saved = GameData.worldMapData;
            var cells = new HashSet<Vector2Int>(fog);
            if (live != null) cells.UnionWith(live.ClearedFogOfWar);
            if (saved?.ClearedFog != null) cells.UnionWith(saved.ClearedFog);
            var list = cells.ToList();
            for (int i = 0; i < list.Count; i += FogChunk)
            {
                int start = i, n = Math.Min(FogChunk, list.Count - i);
                session.SendTo(from, Msg.MapOps, w => PutFog(w, list, start, n));
            }
            var unlocked = new HashSet<(int, Vector2Int)>(portals);
            foreach (var ps in (live?.PortalStates ?? Enumerable.Empty<PortalState>()).Concat(saved?.PortalStates ?? Enumerable.Empty<PortalState>()))
                if (ps.CurrentState.Value == PortalState.State.Active) unlocked.Add((ps.Region, ps.Position));
            var allMarkers = new Dictionary<string, WorldMapMarker>();
            if (saved?.Markers != null) foreach (var m in saved.Markers) allMarkers[m.Name] = m;
            if (live != null) foreach (var kv in live.markers) allMarkers[kv.Key] = kv.Value;
            foreach (var kv in markers) allMarkers[kv.Key] = kv.Value;
            session.SendTo(from, Msg.MapOps, w =>
            {
                foreach (var key in unlocked) PutPortal(w, key);
                foreach (var m in allMarkers.Values) if (!removedMarkers.Contains(m.Name)) PutMarker(w, m);
                foreach (var name in removedMarkers) { w.Put((byte)Op.MarkerRemove); w.Put(name); }
                w.Put((byte)Op.FullEnd);
            });
            var placed = new Dictionary<string, QuickPortalModel>();
            if (live != null) foreach (var m in live.PlacedPortals) placed[m.ID] = m;
            foreach (var kv in quick) placed[kv.Key] = kv.Value;
            foreach (var m in placed.Values)
                if (!removedQuick.Contains(m.ID)) session.SendTo(from, Msg.MapQuickPortal, w => PutPlaced(w, m, 0, ""));
            foreach (var id in removedQuick) session.SendTo(from, Msg.MapQuickPortal, w => { w.Put((byte)QOp.Removed); w.Put(id); });
            FullSent++;
            Plugin.Log.LogInfo($"[map] Sent the whole map to #{from}: {list.Count} explored cells, {unlocked.Count} portals, {allMarkers.Count} markers, {placed.Count} quick portals");
        }

        // ---------------- quick portals ----------------

        private static string TypeStr(QuickPortalType t) => t is QuickPortalType.MainMap m ? $"M_{m.Biome}_{m.Region}" : "";

        private static QuickPortalType ParseType(string s)
        {
            var p = (s ?? "").Split('_');
            return p.Length == 3 && p[0] == "M" && int.TryParse(p[1], out int b) && int.TryParse(p[2], out int reg) ? new QuickPortalType.MainMap(b, reg) : null;
        }

        private static void PutModel(NetDataWriter w, QuickPortalModel m)
        {
            w.Put(m.ActualPosition.x);
            w.Put(m.ActualPosition.y);
            w.Put(TypeStr(m.Type));
            w.Put(m.Uses.Value);
        }

        private static void PutPlaced(NetDataWriter w, QuickPortalModel m, byte owner, string provisional)
        {
            w.Put((byte)QOp.Placed);
            w.Put(m.ID);
            w.Put(owner);
            w.Put(provisional);
            PutModel(w, m);
        }

        private QuickPortalModel ReadModel(string id, NetDataReader r)
        {
            var m = new QuickPortalModel { ID = id, ActualPosition = new Vector2(r.GetFloat(), r.GetFloat()), Type = ParseType(r.GetString()), Prefab = LocalPrefab() };
            m.Uses.Value = r.GetInt();
            return m;
        }

        // The host's numbering: the game's own "QuickPortal<n>", n the lowest one nobody uses.
        private string UniqueQuickId(QuickPortalModel except)
        {
            var used = new HashSet<string>(quick.Where(kv => kv.Value != except).Select(kv => kv.Key));
            var live = Live;
            if (live != null) foreach (var m in live.PlacedPortals) if (m != except) used.Add(m.ID);
            var saved = GameData.worldMapData?.PlaceablePortal;
            if (saved != null) foreach (var m in saved) if (m != except) used.Add(m.ID);
            for (int n = 0; ; n++)
                if (!used.Contains("QuickPortal" + n)) return "QuickPortal" + n;
        }

        private void OnQuick(byte from, NetDataReader r)
        {
            if (!Sharing) return;
            var live = Live;
            try
            {
                switch ((QOp)r.GetByte())
                {
                    case QOp.Request:
                    {
                        if (!session.IsHost) return;   // (relayed to the other guests too; only the host names portals)
                        string provisional = r.GetString();
                        var model = ReadModel(UniqueQuickId(null), r);
                        AddQuickHere(live, model);
                        session.Send(Msg.MapQuickPortal, w => PutPlaced(w, model, from, provisional));
                        Plugin.Log.LogInfo($"[map] #{from}'s quick portal {provisional} is {model.ID}");
                        break;
                    }
                    case QOp.Placed:
                    {
                        if (session.IsHost) return;
                        string id = r.GetString();
                        byte owner = r.GetByte();
                        string provisional = r.GetString();
                        if (removedQuick.Contains(id)) return;
                        QuickPortalModel mine = owner == session.LocalId && provisional.Length > 0 ? live?.PlacedPortals.FirstOrDefault(x => x.ID == provisional) : null;
                        if (mine != null)
                        {
                            RenameQuick(mine, provisional, id);
                            Plugin.Log.LogInfo($"[map] Our quick portal {provisional} is {id}");
                            break;
                        }
                        var model = ReadModel(id, r);
                        var existing = live?.PlacedPortals.FirstOrDefault(x => x.ID == id);
                        if (existing != null) { quick[id] = existing; break; }
                        AddQuickHere(live, model);
                        break;
                    }
                    case QOp.Removed:
                    {
                        string id = r.GetString();
                        quick.Remove(id);
                        removedQuick.Add(id);
                        if (live != null) RemoveQuickHere(live, id);
                        break;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[map] Bad quick portal message: " + e.Message); }
        }

        private void AddQuickHere(WorldMapState live, QuickPortalModel model)
        {
            quick[model.ID] = model;
            QuickApplied++;
            if (live == null || live.placedPortals.Any(x => x.ID == model.ID)) return;
            Applying = true;
            try { live.placedPortals.Add(model); }   // the map screen adds its button
            finally { Applying = false; }
            SpawnHere(model);
        }

        private void RenameQuick(QuickPortalModel model, string from, string to)
        {
            model.ID = to;
            quick[to] = model;
            if (Teleporter.GlobalTeleporters.TryGetValue(from, out var tp))
            {
                Teleporter.GlobalTeleporters.Remove(from);
                if (tp != null) { tp.SetID(to); Teleporter.GlobalTeleporters[to] = tp; }
            }
            if (GameData.teleporterID == from) GameData.teleporterID = to;
        }

        // Someone placed it in the dungeon region we're in: put it up here too, as the game does when a region loads.
        private static void SpawnHere(QuickPortalModel model)
        {
            try
            {
                if (!Singleton<WorldMap>.HasMain || model.Prefab == null) return;
                var wm = Singleton<WorldMap>.main;
                if (!wm.WorldMapState.IsInDungeon.Value || !(wm.CurrentMap is MapInfo.MainMap map) || !(model.Type is QuickPortalType.MainMap t) || t.Region != map.Region) return;
                var q = UnityEngine.Object.Instantiate(model.Prefab);
                q.transform.position = model.ActualPosition;
                q.SetupId(model);
                var tp = q.GetComponent<Teleporter>();
                if (tp != null) { tp.SetID(model.ID); Teleporter.GlobalTeleporters[model.ID] = tp; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[map] Quick portal spawn: " + e.Message); }
        }

        // Used up (or removed) elsewhere: gone here too, with the game's own "consumed" effect if it stands here.
        private void RemoveQuickHere(WorldMapState live, string id)
        {
            Applying = true;
            try
            {
                foreach (var q in UnityEngine.Object.FindObjectsByType<QuickPortal>(FindObjectsSortMode.None))
                    if (q.model != null && q.model.ID == id)
                    {
                        q.onConsume?.Invoke();
                        UnityEngine.Object.Destroy(q.gameObject, 3f);   // (the consume animation destroys it sooner)
                    }
                foreach (var m in live.placedPortals.Where(x => x.ID == id).ToList()) live.placedPortals.Remove(m);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[map] Quick portal removal: " + e.Message); }
            finally { Applying = false; }
        }

        // A quick portal's prefab doesn't survive JSON across machines (it's an instance id): use ours.
        private static QuickPortal prefab;

        public static QuickPortal LocalPrefab()
        {
            if (prefab != null) return prefab;
            var live = Live;
            if (live != null) foreach (var m in live.PlacedPortals) if (m.Prefab != null) return prefab = m.Prefab;
            foreach (var c in Resources.FindObjectsOfTypeAll<CollectablePortal>()) if (c.prefab != null) return prefab = c.prefab;
            return null;
        }

        private void FixPrefabs(WorldMapState live)
        {
            if (Time.realtimeSinceStartup < nextPrefabLookup || !live.PlacedPortals.Any(m => m.Prefab == null)) return;
            nextPrefabLookup = Time.realtimeSinceStartup + 10f;
            var p = LocalPrefab();
            if (p == null) return;
            foreach (var m in live.PlacedPortals) if (m.Prefab == null) m.Prefab = p;
        }

        // ---------------- worlds and saves ----------------

        // A world was loaded under the live map (a guest joining the host's world, or going back to its own): the
        // live map becomes that world's (the game itself only reads the saved map when the map is first built, so
        // it would keep showing, and next save, the world we left).
        public static void LoadWorldMap(WorldMapState.SavedData saved)
        {
            if (Instance != null)
            {
                Instance.ClearShared();
                Instance.worldRef = GameData.worldData;
                Instance.requested = false;
            }
            if (!Singleton<WorldMap>.HasMain) return;
            var wm = Singleton<WorldMap>.main;
            var st = wm.WorldMapState;
            Applying = true;
            try
            {
                // A map drawn for the world we leave: drawn again from the new world's data when next opened.
                if (wm.render != null && wm.RenderCompletionSource == null)
                {
                    wm.render = null;
                    wm.isDone = false;
                    wm.CreationCompletionSource = null;
                    st.IsMapLoaded = false;
                    wm.worldMapContainer?.Cleanup();
                }
                // One by one: the map screen drops its buttons on removals (a Clear is a reset it doesn't listen to).
                foreach (var name in st.markers.Select(kv => kv.Key).ToList()) st.RemoveMarker(name);
                foreach (var m in st.placedPortals.ToList()) st.placedPortals.Remove(m);
                if (saved != null && saved.PortalStates != null)
                {
                    if (saved.ClearedFog == null) saved.ClearedFog = new List<Vector2Int>();
                    st.LoadSavedData(saved);
                }
                else
                {
                    st.PortalStates.Clear();
                    st.ClearedFogOfWar = new HashSet<Vector2Int>();
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[map] Loading the world's map: " + e.Message); }
            finally { Applying = false; }
        }

        // Host: the game saves the map only once it's built (opened, or a dungeon loaded). If it isn't, what the
        // others shared goes into the saved copy and that copy is written, so it's in the save all the same.
        public static void SaveIntoUnbuiltMap()
        {
            var ms = Instance;
            if (ms == null || ms.session.IsHost == false || GuestWorld.Active || GuestWorld.Switching || ms.worldRef != GameData.worldData) return;
            if (GameData.playerData == null || GameData.worldData == null || !GameData.CanSave) return;
            var live = Live;
            if (live != null && live.PortalStates.Count > 0) return;   // the game saved the live map
            var saved = GameData.worldMapData;
            if (saved == null || saved.PortalStates == null || saved.PortalStates.Count == 0) return;   // no map yet: nothing valid to write
            if (!ms.MergeIntoSaved(saved)) return;
            SaveLoad.Save(GameData.GetWorldPrefix + ".evm", JsonUtility.ToJson(saved));
            SaveLoad.Save(GameData.GetWorldPrefix + "_mapFriendly.json", JsonUtility.ToJson(saved, true), obfuscate: false);
            Plugin.Log.LogInfo("[map] Saved the shared map into the (unopened) world map");
        }

        private bool MergeIntoSaved(WorldMapState.SavedData saved)
        {
            bool changed = false;
            var cells = new HashSet<Vector2Int>(saved.ClearedFog ?? new List<Vector2Int>());
            int before = cells.Count;
            cells.UnionWith(fog);
            if (cells.Count != before || saved.ClearedFog == null) { saved.ClearedFog = cells.ToList(); changed = true; }
            foreach (var ps in saved.PortalStates)
                if (ps.CurrentState.Value != PortalState.State.Active && portals.Contains((ps.Region, ps.Position))) { ps.CurrentState.Value = PortalState.State.Active; changed = true; }
            if (saved.Markers == null) saved.Markers = new List<WorldMapMarker>();
            foreach (var m in markers.Values) if (!saved.Markers.Any(x => x.Name == m.Name)) { saved.Markers.Add(m); changed = true; }
            changed |= saved.Markers.RemoveAll(x => removedMarkers.Contains(x.Name)) > 0;
            if (saved.PlaceablePortal == null) saved.PlaceablePortal = new List<QuickPortalModel>();
            foreach (var q in quick.Values) if (!saved.PlaceablePortal.Any(x => x.ID == q.ID)) { saved.PlaceablePortal.Add(q); changed = true; }
            changed |= saved.PlaceablePortal.RemoveAll(x => removedQuick.Contains(x.ID)) > 0;
            return changed;
        }

        // ---------------- hooks ----------------

        [HarmonyPatch(typeof(WorldMapState), nameof(WorldMapState.ClearFog))]
        internal static class ClearFogPatch
        {
            private static void Postfix(WorldMapState __instance, Vector2 pos, int range) => Current?.OnLocalFog(__instance, pos, range);
        }

        [HarmonyPatch(typeof(WorldMapState), nameof(WorldMapState.AddMarker))]
        internal static class AddMarkerPatch
        {
            private static void Prefix(WorldMapState __instance, WorldMapMarker mapMarker, out bool __state) =>
                __state = mapMarker != null && mapMarker.Name != null && !__instance.markers.ContainsKey(mapMarker.Name);

            private static void Postfix(WorldMapState __instance, WorldMapMarker mapMarker, bool __state)
            {
                if (__state) Current?.OnLocalMarker(__instance, mapMarker, true);
            }
        }

        [HarmonyPatch(typeof(WorldMapState), nameof(WorldMapState.RemoveMarker))]
        internal static class RemoveMarkerPatch
        {
            private static void Prefix(WorldMapState __instance, string name, out WorldMapMarker __state) =>
                __state = name != null && __instance.markers.TryGetValue(name, out var m) ? m : null;

            private static void Postfix(WorldMapState __instance, WorldMapMarker __state)
            {
                if (__state != null) Current?.OnLocalMarker(__instance, __state, false);
            }
        }

        [HarmonyPatch(typeof(WorldMapState), nameof(WorldMapState.AddQuickPortal))]
        internal static class AddQuickPortalPatch
        {
            private static void Postfix(WorldMapState __instance, QuickPortalModel __result) => Current?.OnLocalQuickPortal(__instance, __result);
        }

        [HarmonyPatch(typeof(WorldMapState), nameof(WorldMapState.RemoveQuickPortal))]
        internal static class RemoveQuickPortalPatch
        {
            private static void Postfix(WorldMapState __instance, QuickPortalModel portal) => Current?.OnLocalQuickRemoved(__instance, portal);
        }

        // The game loads the saved map into the live one when the map is built: what was shared goes back on top.
        [HarmonyPatch(typeof(WorldMapState), nameof(WorldMapState.LoadSavedData))]
        internal static class LoadSavedDataPatch
        {
            private static void Prefix() => loading++;
            private static void Postfix(WorldMapState __instance) => Current?.OnLoaded(__instance);
            private static Exception Finalizer(Exception __exception) { loading--; return __exception; }
        }

        [HarmonyPatch(typeof(GameData), nameof(GameData.SaveData))]
        internal static class SaveMapPatch
        {
            private static void Postfix()
            {
                try { SaveIntoUnbuiltMap(); }
                catch (Exception e) { Plugin.Log.LogWarning("[map] Saving the shared map: " + e.Message); }
            }
        }
    }
}

namespace EmbervilleMP.Sync.Objects
{
    // Hooks MapSync onto the session through the object framework (its per-session Init and in-game Update);
    // the world map isn't a scene object, so there's nothing to find, capture or apply.
    internal class WorldMapHandler : ObjectHandler
    {
        public override string Kind => "worldmap";
        public override IEnumerable<Component> FindAll() => Enumerable.Empty<Component>();
        public override void Capture(Component obj, NetDataWriter w) { }
        public override void Apply(Component obj, NetDataReader r) { }
        public override void Init(Session session) => new MapSync(session);
        public override void Update() => MapSync.Instance?.Update();
    }
}
