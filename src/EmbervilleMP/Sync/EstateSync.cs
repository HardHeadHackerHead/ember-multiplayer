using System;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace EmbervilleMP.Sync
{
    // The estate (phase D): building, tiles and farming. There is one estate per world (GameData.playerEstateData:
    // placeables, tiles, soil states), so it's synced as a list of changes to that data rather than per scene object:
    //
    //   - A player places or removes something through the game's own PlaceableController (it pays or gets the
    //     refund, plays the effects). We note the estate before and after and send the difference (EstateOps).
    //   - Farming actions (plow, plant, water, fertilize, harvest) send the soil's new state; growth runs on every
    //     copy on the shared clock, and the runner of the estate scene sends a soil's state whenever its crop moves
    //     a step, gets ready or dries out, so everyone ends up with the runner's (the host's clock's) result.
    //   - Copies in the estate scene apply changes live with the game's own code (Place, RemoveSavedData, tiles,
    //     the soil's fields as its own load does); copies elsewhere edit their estate data, which is what the
    //     estate scene builds itself from when they go there. The host's copy is the one that's saved.
    //   - Ids: a placed object's save id is the GUID the placer's game made; everyone uses that one.
    //   - Harvesting is asked of the estate scene's runner (EstateClaim/EstateGrant): one player gets each crop.
    //     The yield drops like any drop, so it follows the loot mode (WorldDrops).
    //   - Players arriving in the estate get the runner's soil states.
    internal class EstateSync
    {
        public static EstateSync Instance { get; private set; }
        public static bool Applying;
        public int OpsSent, ChangesSent, ChangesLive, ChangesData, SoilsSent, ClaimsSent, ClaimsGranted, ClaimsRefused, SoilSnapshots;

        private readonly Session session;
        private readonly Dictionary<string, (byte who, float at)> claims = new Dictionary<string, (byte, float)>();
        private readonly Dictionary<byte, string> lastSceneOf = new Dictionary<byte, string>();
        private readonly HashSet<string> pendingClaims = new HashSet<string>();

        public EstateSync(Session session)
        {
            Instance = this;
            this.session = session;
            session.On(Msg.EstateOps, OnOps);
            session.On(Msg.EstateClaim, OnClaim);
            session.On(Msg.EstateGrant, OnGrant);
        }

        public static bool Active => Instance != null && Session.Current != null && Session.Current.Ready;

        // The estate scene is the one we're in when its controllers exist (no scene names: the full game may move it).
        public static bool PlaceablesLive => Game.InGame && Singleton<PlaceableController>.HasMain;
        public static bool TilesLive => Game.InGame && Singleton<TilesetDrawerManager>.HasMain;
        public static bool FarmLive => Game.InGame && Singleton<FarmingManager>.HasMain;
        private static bool Runner => EnemySync.IsAuthority;

        public void Update()
        {
            if (!session.Ready || !Game.InGame) return;
            float now = Time.realtimeSinceStartup;
            foreach (var k in claims.Where(kv => now - kv.Value.at > 10f).Select(kv => kv.Key).ToList()) claims.Remove(k);
            // We run the estate scene: players arriving get our soil states (crops grew on our clock).
            if (Runner && FarmLive && MPRunner.Instance?.Presence != null)
                foreach (var rp in MPRunner.Instance.Presence.Remotes.Values)
                {
                    string scene = rp.Latest?.Scene ?? "";
                    lastSceneOf.TryGetValue(rp.Info.Id, out var before);
                    if (scene == before) continue;
                    lastSceneOf[rp.Info.Id] = scene;
                    if (scene == Game.SceneName) SendSoilSnapshot();
                }
        }

        // ---------------- changes ----------------

        internal enum Kind : byte { PlaceableAdd = 1, PlaceableRemove = 2, TileSet = 3, TileRemove = 4, Soil = 5, SoilRemove = 6 }

        internal class Change
        {
            public Kind Kind;
            public string Id = "", Instance = "", Tile = "";
            public Vector3 Pos;
            public byte Dir;
            public int Layer;
            public Vector3Int Cell;
            public string Json = "";
            public bool Ready;

            public void Write(NetDataWriter w)
            {
                w.Put((byte)Kind);
                switch (Kind)
                {
                    case Kind.PlaceableAdd:
                    case Kind.PlaceableRemove:
                        w.Put(Id); w.Put(Instance ?? ""); w.Put(Pos.x); w.Put(Pos.y); w.Put(Pos.z); w.Put(Dir);
                        break;
                    case Kind.TileSet:
                    case Kind.TileRemove:
                        w.Put(Layer); w.Put(Cell.x); w.Put(Cell.y); w.Put(Tile ?? ""); w.Put(Id ?? "");
                        break;
                    case Kind.Soil:
                        w.Put(Id); w.Put(Json); w.Put(Ready);
                        break;
                    case Kind.SoilRemove:
                        w.Put(Id);
                        break;
                }
            }

            public static Change Read(NetDataReader r)
            {
                var c = new Change { Kind = (Kind)r.GetByte() };
                switch (c.Kind)
                {
                    case Kind.PlaceableAdd:
                    case Kind.PlaceableRemove:
                        c.Id = r.GetString(); c.Instance = r.GetString(); c.Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()); c.Dir = r.GetByte();
                        break;
                    case Kind.TileSet:
                    case Kind.TileRemove:
                        c.Layer = r.GetInt(); c.Cell = new Vector3Int(r.GetInt(), r.GetInt(), 0); c.Tile = r.GetString(); c.Id = r.GetString();
                        break;
                    case Kind.Soil:
                        c.Id = r.GetString(); c.Json = r.GetString(); c.Ready = r.GetBool();
                        break;
                    case Kind.SoilRemove:
                        c.Id = r.GetString();
                        break;
                    default: throw new FormatException("unknown estate change " + c.Kind);
                }
                return c;
            }

            public override string ToString() =>
                Kind == Kind.PlaceableAdd || Kind == Kind.PlaceableRemove ? $"{Kind} {Id} at {Pos} ({(string.IsNullOrEmpty(Instance) ? "no id" : Instance)})"
                : Kind == Kind.TileSet || Kind == Kind.TileRemove ? $"{Kind} {(TilemapLayer)Layer} {Cell.x},{Cell.y} {Tile}"
                : $"{Kind} {Id}";
        }

        public void Send(List<Change> changes)
        {
            if (!session.Ready || changes == null || changes.Count == 0) return;
            session.Send(Msg.EstateOps, w =>
            {
                w.Put(changes.Count);
                foreach (var c in changes) c.Write(w);
            });
            OpsSent++;
            ChangesSent += changes.Count;
            Plugin.Log.LogInfo($"[estate] Sent {string.Join("; ", changes)}");
        }

        private void OnOps(byte from, NetDataReader r)
        {
            int n = Net.NetGuard.Count(r.GetInt(), r);   // (a junk count must not allocate gigabytes)
            var list = new List<Change>(n);
            for (int i = 0; i < n; i++) list.Add(Change.Read(r));
            Apply(list);
        }

        // Puts changes into our estate: live when we're in the estate, else into the estate data.
        public void Apply(List<Change> changes)
        {
            Applying = true;
            try
            {
                // Objects first: a farm tile's own Place sets its tile, the tile change after it is then a no-op.
                foreach (var c in changes.OrderBy(c => c.Kind == Kind.PlaceableAdd || c.Kind == Kind.PlaceableRemove ? 0 : 1))
                {
                    try { ApplyOne(c); }
                    catch (Exception e) { Plugin.Log.LogWarning($"[estate] Couldn't apply {c}: {e}"); }
                    if (c.Kind == Kind.Soil && !c.Ready) claims.Remove(c.Id);
                }
            }
            finally { Applying = false; }
        }

        private void ApplyOne(Change c)
        {
            var est = GameData.playerEstateData ?? (GameData.playerEstateData = new PlayerEstateData());
            switch (c.Kind)
            {
                case Kind.PlaceableAdd:
                    if (PlaceablesLive && LivePlace(c)) { ChangesLive++; return; }
                    if (!est.savedPlaceables.Any(p => SamePlaceable(p, c)))
                        est.savedPlaceables.Add(new SavedPlaceableData { placeableID = c.Id, instanceSaveID = NullIfEmpty(c.Instance), worldPosition = c.Pos, direction = (Direction)c.Dir });
                    ChangesData++;
                    return;
                case Kind.PlaceableRemove:
                    if (PlaceablesLive && LiveRemove(c)) { ChangesLive++; return; }
                    est.savedPlaceables.RemoveAll(p => SamePlaceable(p, c));
                    if (!string.IsNullOrEmpty(c.Instance))
                    {
                        // what the object's own RemoveFromSave would drop
                        est.savedSoilStates.RemoveAll(s => s.saveID == c.Instance);
                        est.savedTrees.RemoveAll(t => t.saveID == c.Instance);
                    }
                    ChangesData++;
                    return;
                case Kind.TileSet:
                case Kind.TileRemove:
                    if (TilesLive && LiveTile(c)) { ChangesLive++; return; }
                    DataTile(est, c);
                    ChangesData++;
                    return;
                case Kind.Soil:
                {
                    var soil = FarmLive ? FindSoil(c.Id) : null;
                    var state = JsonUtility.FromJson<SavedSoilState>(c.Json);
                    if (soil != null) { SoilCodec.ApplyLive(soil, state, c.Ready, fx: true); ChangesLive++; return; }
                    est.savedSoilStates.RemoveAll(s => s.saveID == c.Id);
                    est.savedSoilStates.Add(state);
                    ChangesData++;
                    return;
                }
                case Kind.SoilRemove:
                    est.savedSoilStates.RemoveAll(s => s.saveID == c.Id);
                    ChangesData++;
                    return;
            }
        }

        private static bool SamePlaceable(SavedPlaceableData p, Change c) =>
            !string.IsNullOrEmpty(c.Instance) ? p.instanceSaveID == c.Instance
                : p.placeableID == c.Id && string.IsNullOrEmpty(p.instanceSaveID) && (p.worldPosition - c.Pos).sqrMagnitude < 1e-4f;

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        // ---- live: placeables ----

        private static bool LivePlace(Change c)
        {
            var ctrl = Singleton<PlaceableController>.main;
            var data = ctrl.dataBase != null ? ctrl.dataBase.Get(c.Id) : null;
            if (data == null) { Plugin.Log.LogWarning($"[estate] Unknown placeable {c.Id}"); return false; }
            if (ctrl.saved.Any(p => SamePlaceable(p, c))) return true;   // already here
            var tiles = Singleton<TilesetDrawerManager>.HasMain ? Singleton<TilesetDrawerManager>.main : null;
            var keepItem = tiles?.currentItem;
            var keepMap = tiles?.currentTileMap;
            try
            {
                var (_, instance) = data.Place(c.Pos, (Direction)c.Dir, NullIfEmpty(c.Instance), ctrl.placeablesParent);
                data.SaveData(c.Pos, (Direction)c.Dir, instance);
                Fx.Post(data, "sfxOnPlace", ctrl.gameObject);
            }
            finally
            {
                // the local player may be placing something of its own (its ghost checks the current tile)
                if (tiles != null && keepItem != null) { tiles.currentItem = keepItem; tiles.currentTileMap = keepMap; }
            }
            return true;
        }

        private static bool LiveRemove(Change c)
        {
            var ctrl = Singleton<PlaceableController>.main;
            var data = ctrl.dataBase != null ? ctrl.dataBase.Get(c.Id) : null;
            var obj = UnityEngine.Object.FindObjectsByType<PlacementObjectComponent>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(o => o.RelatedPlaceableDataID == c.Id && (o.transform.position - c.Pos).sqrMagnitude < 1e-3f
                    && (string.IsNullOrEmpty(c.Instance) || (o.GetComponent<ISavableID>()?.GetSaveID() ?? c.Instance) == c.Instance));
            if (data == null && obj == null) return false;
            Vector3 pos = obj != null ? obj.transform.position : c.Pos;
            // The game's removal minus the refund (the remover got it).
            if (data != null) data.RemoveSavedData(pos);
            else ctrl.saved.RemoveAll(p => SamePlaceable(p, c));
            if (obj != null)
            {
                if (data != null) Fx.Post(data, "sfxOnRemove", ctrl.gameObject);
                obj.OnRemove();
                UnityEngine.Object.Destroy(obj.gameObject);
            }
            return true;
        }

        // ---- tiles ----

        private static bool LiveTile(Change c)
        {
            var mgr = Singleton<TilesetDrawerManager>.main;
            var layer = (TilemapLayer)c.Layer;
            if (!mgr.tilemapsDict.TryGetValue(layer, out var map) || map == null) return false;
            if (!mgr.currentTilemapData.TryGetValue(layer, out var data)) mgr.currentTilemapData[layer] = data = new Dictionary<Vector3, (string, string)>();
            if (c.Kind == Kind.TileRemove)
            {
                map.DeleteTile(c.Cell);
                data.Remove(c.Cell);
                return true;
            }
            var item = mgr.tilesets.Find(t => t != null && t.saveID == c.Tile);
            if (item == null) { Plugin.Log.LogWarning($"[estate] Unknown tile {c.Tile}"); return false; }
            if (map.GetTile(c.Cell) != item.tile) map.SetTile(c.Cell, item.tile);
            data[c.Cell] = (c.Tile, c.Id);
            return true;
        }

        private static void DataTile(PlayerEstateData est, Change c)
        {
            var layer = (TilemapLayer)c.Layer;
            var l = est.tilemapLayers.Find(x => x.layer == layer);
            if (l == null) est.tilemapLayers.Add(l = new SavedTilesetLayer { layer = layer });
            l.savedTiles.RemoveAll(t => t.pos.x == c.Cell.x && t.pos.y == c.Cell.y);
            if (c.Kind == Kind.TileSet) l.savedTiles.Add(new SavedTilesetItem(new Vector3(c.Cell.x, c.Cell.y, 0f), c.Tile, c.Id));
        }

        // ---------------- local changes: before/after the game's own code ----------------

        internal class Mark
        {
            public List<SavedPlaceableData> Placeables;
            public Dictionary<(int, Vector3Int), (string tile, string id)> Tiles;
            public HashSet<string> Soils;
        }

        public static Mark Take()
        {
            var m = new Mark();
            if (PlaceablesLive) m.Placeables = Singleton<PlaceableController>.main.saved.ToList();
            if (TilesLive) m.Tiles = Tiles();
            m.Soils = new HashSet<string>((GameData.playerEstateData?.savedSoilStates ?? new List<SavedSoilState>()).Select(s => s.saveID));
            return m;
        }

        private static Dictionary<(int, Vector3Int), (string, string)> Tiles()
        {
            var d = new Dictionary<(int, Vector3Int), (string, string)>();
            foreach (var layer in Singleton<TilesetDrawerManager>.main.currentTilemapData)
                foreach (var kv in layer.Value)
                    d[((int)layer.Key, Vector3Int.RoundToInt(kv.Key))] = kv.Value;
            return d;
        }

        public static List<Change> Diff(Mark before)
        {
            var list = new List<Change>();
            if (before.Placeables != null && PlaceablesLive)
            {
                var now = Singleton<PlaceableController>.main.saved;
                foreach (var p in now.Where(p => !before.Placeables.Contains(p)))
                    list.Add(new Change { Kind = Kind.PlaceableAdd, Id = p.placeableID, Instance = p.instanceSaveID ?? "", Pos = p.worldPosition, Dir = (byte)p.direction });
                foreach (var p in before.Placeables.Where(p => !now.Contains(p)))
                    list.Add(new Change { Kind = Kind.PlaceableRemove, Id = p.placeableID, Instance = p.instanceSaveID ?? "", Pos = p.worldPosition, Dir = (byte)p.direction });
            }
            if (before.Tiles != null && TilesLive)
            {
                var now = Tiles();
                foreach (var kv in now)
                    if (!before.Tiles.TryGetValue(kv.Key, out var was) || was != kv.Value)
                        list.Add(new Change { Kind = Kind.TileSet, Layer = kv.Key.Item1, Cell = kv.Key.Item2, Tile = kv.Value.Item1 ?? "", Id = kv.Value.Item2 ?? "" });
                foreach (var k in before.Tiles.Keys.Where(k => !now.ContainsKey(k)))
                    list.Add(new Change { Kind = Kind.TileRemove, Layer = k.Item1, Cell = k.Item2 });
            }
            var soils = (GameData.playerEstateData?.savedSoilStates ?? new List<SavedSoilState>()).Select(s => s.saveID);
            foreach (var id in before.Soils.Except(soils)) list.Add(new Change { Kind = Kind.SoilRemove, Id = id });
            return list;
        }

        // ---------------- soil ----------------

        public static SoilTile FindSoil(string saveID) =>
            string.IsNullOrEmpty(saveID) ? null
                : UnityEngine.Object.FindObjectsByType<SoilTile>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(s => s.saveID == saveID);

        private readonly HashSet<SoilTile> warnedNoId = new HashSet<SoilTile>();

        public void SendSoil(SoilTile t)
        {
            if (!session.Ready || t == null) return;
            if (string.IsNullOrEmpty(t.saveID))
            {
                if (warnedNoId.Add(t)) Plugin.Log.LogWarning($"[estate] Soil {t.name} has no save id: not synced");
                return;
            }
            Send(new List<Change> { SoilChange(t) });
            SoilsSent++;
        }

        private static Change SoilChange(SoilTile t) =>
            new Change { Kind = Kind.Soil, Id = t.saveID, Json = JsonUtility.ToJson(SoilCodec.Capture(t)), Ready = t.canHarvest };

        private void SendSoilSnapshot()
        {
            var all = UnityEngine.Object.FindObjectsByType<SoilTile>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(s => !string.IsNullOrEmpty(s.saveID)).Select(SoilChange).ToList();
            if (all.Count == 0) return;
            session.Send(Msg.EstateOps, w => { w.Put(all.Count); foreach (var c in all) c.Write(w); });
            SoilSnapshots++;
        }

        // ---------------- harvest: one player per crop ----------------

        internal static bool Granted;   // re-running a harvest the runner granted us

        // FarmingManager.OnHarvest is about to run here. False: wait for the runner.
        public bool BeforeHarvest(FarmingManager fm, SoilTile soil)
        {
            if (Granted || fm.isBusy || soil == null || !soil.canHarvest || string.IsNullOrEmpty(soil.saveID)) return true;
            if (Runner)
            {
                if (claims.TryGetValue(soil.saveID, out var c) && c.who != session.LocalId) return false;   // someone else's
                claims[soil.saveID] = (session.LocalId, Time.realtimeSinceStartup);
                return true;
            }
            if (pendingClaims.Add(soil.saveID))
            {
                string scene = Game.SceneName, id = soil.saveID;
                session.Send(Msg.EstateClaim, w => { w.Put(scene); w.Put(id); });
                ClaimsSent++;
            }
            return false;
        }

        private void OnClaim(byte from, NetDataReader r)
        {
            string scene = r.GetString(), id = r.GetString();
            if (scene != Game.SceneName || !Game.InGame || !Runner) return;
            var soil = FindSoil(id);
            bool ok = soil != null && soil.canHarvest && (!claims.TryGetValue(id, out var c) || c.who == from);
            if (ok) { claims[id] = (from, Time.realtimeSinceStartup); ClaimsGranted++; }
            else ClaimsRefused++;
            Plugin.Log.LogInfo($"[estate] Harvest of {id} by #{from}: {(ok ? "granted" : "refused")}");
            session.Send(Msg.EstateGrant, w => { w.Put(scene); w.Put(id); w.Put(from); w.Put(ok); });
        }

        private void OnGrant(byte from, NetDataReader r)
        {
            string scene = r.GetString(), id = r.GetString();
            byte who = r.GetByte();
            bool ok = r.GetBool();
            if (ok) claims[id] = (who, Time.realtimeSinceStartup);
            if (who != session.LocalId) return;
            pendingClaims.Remove(id);
            if (!ok || scene != Game.SceneName || !FarmLive) return;
            var soil = FindSoil(id);
            if (soil == null) return;
            Granted = true;
            try { Singleton<FarmingManager>.main.OnHarvest(soil); }
            finally { Granted = false; }
        }

        public bool ClaimedByOther(string saveID) =>
            claims.TryGetValue(saveID, out var c) && c.who != session.LocalId;
    }

    // A soil's state as the game saves it, and putting it back on a live soil the way its own load does.
    internal static class SoilCodec
    {
        public static SavedSoilState Capture(SoilTile t) => new SavedSoilState
        {
            saveID = t.saveID,
            state = t.state,
            isFertilized = t.isFertilized,
            isWatered = t.isWatered,
            growingProgress = t.growingProgress,
            currentCropType = t.activeCrop?.cropType ?? CropType.None,
            currentFertilizer = t.activeFertilizerType,
            lastCropStep = t.lastCropStep,
            resetSoilDate = t.resetSoilEvent.GetScheduledDate(),
            resetWaterDate = t.resetWaterEvent.GetScheduledDate(),
            lastUpdateTime = t.lastUpdateTime,
            growingTime = t.growingTime,
        };

        public static void ApplyLive(SoilTile t, SavedSoilState s, bool ready, bool fx)
        {
            t.Initialize();
            var manager = t.manager != null ? t.manager : Singleton<FarmingManager>.main;
            if (t.fadeCo != null) { t.StopCoroutine(t.fadeCo); t.fadeCo = null; }
            var col = t.rend.color; col.a = 1f; t.rend.color = col;
            bool changed = t.state != s.state || t.isWatered != s.isWatered || t.isFertilized != s.isFertilized || t.lastCropStep != s.lastCropStep || t.canHarvest != ready;
            t.state = s.state;
            t.isWatered = s.isWatered;
            t.growingProgress = s.growingProgress;
            t.lastCropStep = s.lastCropStep;
            t.lastUpdateTime = s.lastUpdateTime;
            t.growingTime = s.growingTime;
            t.activeCrop = s.state == SoilState.Planted ? manager.GetCrop(s.currentCropType) : null;
            t.canHarvest = ready && t.activeCrop != null;
            // fertilizer (the game's load forgets its multiplier and sprite)
            var fert = s.isFertilized ? manager.GetFertilizer(s.currentFertilizer) : null;
            t.isFertilized = s.isFertilized;
            t.activeFertilizerType = s.isFertilized ? s.currentFertilizer : FertilizerType.None;
            t.fertilizerMultiplier = fert != null ? fert.multiplier : 1f;
            t.fertRenderer.sprite = fert != null ? fert.tileSprite : null;
            // its clock events
            TimeManager.RemoveScheduledEvent(t.resetWaterEvent);
            t.resetWaterEvent = default;
            if (s.isWatered) t.ScheduleWaterReset(s.resetWaterDate);
            TimeManager.RemoveScheduledEvent(t.resetSoilEvent);
            t.resetSoilEvent = default;
            if (s.state == SoilState.Plowed) t.ScheduleResetSoil(s.resetSoilDate);
            if (s.state == SoilState.Planted && !t.canHarvest) manager.AddGrowingSoil(t);
            else manager.RemoveGrowingSoil(t);
            // looks
            if (t.activeCrop == null) t.cropRenderer.sprite = null;
            else if (t.canHarvest && t.activeCrop.growthSteps.Count > 0) t.cropRenderer.sprite = t.activeCrop.growthSteps[t.activeCrop.growthSteps.Count - 1];
            else t.UpdateCropSpriteStep();
            t.cropRenderer.color = t.isWatered || t.canHarvest ? Color.white : t.dryColor;
            t.UpdateVisuals();
            if (!t.canHarvest)
            {
                if (t.interactionIcon != null) t.interactionIcon.SetActive(false);
                if (t.buttonActionIcon != null) t.buttonActionIcon.SetActive(false);
            }
            if (fx && changed && t.vfxChange != null && t.isActiveAndEnabled) t.vfxChange.Play();
        }
    }

    // Sound/particle fields typed from assemblies the mod doesn't reference (Wwise).
    internal static class Fx
    {
        public static void Post(object owner, string field, GameObject on)
        {
            try
            {
                var e = Traverse.Create(owner).Field(field).GetValue();
                e?.GetType().GetMethod("Post", new[] { typeof(GameObject) })?.Invoke(e, new object[] { on });
            }
            catch (Exception ex) { Plugin.Log.LogInfo($"[estate] {field}: {ex.Message}"); }
        }
    }

    // ---------------- hooks ----------------

    // Placing and removing go through the game's own code (costs, refunds, effects); we send what it changed.
    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.PlaceObject))]
    internal static class PlaceObjectPatch
    {
        private static void Prefix(out EstateSync.Mark __state) =>
            __state = EstateSync.Active && !EstateSync.Applying ? EstateSync.Take() : null;

        private static void Postfix(EstateSync.Mark __state)
        {
            if (__state != null) EstateSync.Instance.Send(EstateSync.Diff(__state));
        }
    }

    [HarmonyPatch(typeof(PlaceableController), nameof(PlaceableController.RemoveSelectedObject))]
    internal static class RemovePlaceablePatch
    {
        private static void Prefix(out EstateSync.Mark __state) =>
            __state = EstateSync.Active && !EstateSync.Applying ? EstateSync.Take() : null;

        private static void Postfix(EstateSync.Mark __state)
        {
            if (__state != null) EstateSync.Instance.Send(EstateSync.Diff(__state));
        }
    }

    // Farming actions (FarmingManager runs them for our own player only); Harvest plows or replants inside, so only
    // the outermost call sends.
    [HarmonyPatch]
    internal static class SoilActionPatch
    {
        private static int depth;

        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(SoilTile), nameof(SoilTile.Plow)),
            AccessTools.Method(typeof(SoilTile), nameof(SoilTile.Plant)),
            AccessTools.Method(typeof(SoilTile), nameof(SoilTile.Water)),
            AccessTools.Method(typeof(SoilTile), nameof(SoilTile.Fertilize)),
            AccessTools.Method(typeof(SoilTile), nameof(SoilTile.Harvest)),
        };

        internal static bool Inside => depth > 0;

        private static void Prefix() => depth++;

        private static void Postfix(SoilTile __instance)
        {
            if (--depth > 0 || EstateSync.Applying || !EstateSync.Active) return;
            EstateSync.Instance.SendSoil(__instance);
        }

        private static Exception Finalizer(Exception __exception)
        {
            if (__exception != null && depth > 0) depth--;
            return __exception;
        }
    }

    // Growth, drying and fallow soil follow the clock; the estate scene's runner sends what its clock made of them.
    [HarmonyPatch(typeof(SoilTile), nameof(SoilTile.UpdateSoil))]
    internal static class SoilGrowthPatch
    {
        private static void Prefix(SoilTile __instance, out (int step, bool ready) __state) =>
            __state = (__instance.lastCropStep, __instance.canHarvest);

        private static void Postfix(SoilTile __instance, (int step, bool ready) __state)
        {
            if (!EstateSync.Active || EstateSync.Applying || !EnemySync.IsAuthority) return;
            if (__instance.lastCropStep != __state.step || __instance.canHarvest != __state.ready) EstateSync.Instance.SendSoil(__instance);
        }
    }

    [HarmonyPatch]
    internal static class SoilClockPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(SoilTile), nameof(SoilTile.ResetWater)),
            AccessTools.Method(typeof(SoilTile), nameof(SoilTile.SetDirty)),
        };

        private static void Postfix(SoilTile __instance)
        {
            if (SoilActionPatch.Inside || !EstateSync.Active || EstateSync.Applying || !EnemySync.IsAuthority) return;
            EstateSync.Instance.SendSoil(__instance);
        }
    }

    [HarmonyPatch(typeof(FarmingManager), nameof(FarmingManager.OnHarvest))]
    internal static class HarvestClaimPatch
    {
        private static bool Prefix(FarmingManager __instance, SoilTile soil) =>
            !EstateSync.Active || EstateSync.Instance.BeforeHarvest(__instance, soil);
    }
}
