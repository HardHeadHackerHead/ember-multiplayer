using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace EmbervilleMP
{
    // Tests only. The demo has no estate scene (the "CreativeArea" its teleporters lead to isn't in the build) and
    // none of the estate's objects or databases, so the estate tests build a small one with the game's own classes:
    // a TilesetDrawerManager with real tilemaps, a PlaceableController (its UI left out), a FarmingManager with one
    // quick crop, and three placeables (a farm tile with a real SoilTile, a box that costs gold, a fence tile).
    // Every copy builds the same one, then loads the world's estate data into it the way a scene load does.
    internal static class EstateFixture
    {
        public const string SoilID = "mp_test_soil", BoxID = "mp_test_box", FenceID = "mp_test_fence";
        public const string SoilTileID = "mp_test_soil_tile", FenceTileID = "mp_test_fence_tile";
        public const int BoxCost = 10;

        public static PlaceableController Ctrl;
        public static FarmingManager Farm;
        public static TilesetDrawerManager Tiles;
        public static PlaceableData SoilData, BoxData, FenceData;
        public static Crop Crop;

        private static GameObject root;

        public static bool Built => root != null && Ctrl != null;

        public static IEnumerator Build(ItemData seed, ItemData result)
        {
            root = new GameObject("MP_TestEstate");
            var templates = new GameObject("templates");
            templates.transform.SetParent(root.transform);
            templates.SetActive(false);

            // ---- tiles ----
            var tilesGo = new GameObject("TestTilesetDrawer");
            tilesGo.SetActive(false);
            tilesGo.transform.SetParent(root.transform);
            tilesGo.AddComponent<Grid>();
            Tiles = tilesGo.AddComponent<TilesetDrawerManager>();
            Tiles.tilemaps = new List<TilemapLayerObject>();
            foreach (TilemapLayer layer in System.Enum.GetValues(typeof(TilemapLayer)))
            {
                var m = new GameObject(layer.ToString());
                m.transform.SetParent(tilesGo.transform);
                Tiles.tilemaps.Add(new TilemapLayerObject { tilemapLayer = layer, tilemap = m.AddComponent<Tilemap>() });
                m.AddComponent<TilemapRenderer>();
            }
            var soilItem = TileItem(SoilTileID, TilemapLayer.Farm);
            var fenceItem = TileItem(FenceTileID, TilemapLayer.Fences);
            Tiles.tilesets = new List<TilesetItem> { soilItem, fenceItem };
            tilesGo.SetActive(true);

            // ---- farming ----
            var farmGo = new GameObject("TestFarmingManager");
            farmGo.SetActive(false);
            farmGo.transform.SetParent(root.transform);
            Farm = farmGo.AddComponent<FarmingManager>();
            Farm.enabled = false;   // its Start builds the farming HUD, which isn't here
            Crop = ScriptableObject.CreateInstance<Crop>();
            Crop.name = "TestCrop";
            Crop.cropType = CropType.Carrot;
            Crop.seedItemData = seed;
            Crop.growingTime = new Vector2(0.25f, 0.25f);   // a quarter of a game hour: ~3 s
            Crop.recycleStep = -1;
            Crop.resultItemData = result;
            Crop.resultAmount = 1;
            Crop.growthSteps = new List<Sprite> { null, null, null };
            Farm.database = ScriptableObject.CreateInstance<FarmingDatabase>();
            Farm.database.crops = new List<Crop> { Crop };
            Farm.database.fertilizers = new List<Fertilizer>();
            Farm.player = Game.LocalPlayer;
            Farm.farmController = Game.LocalPlayer.GetComponent<FarmingController>();
            farmGo.SetActive(true);
            Bus<DayTimeEvent>.Listeners += Farm.UpdateSoils;   // what its OnEnable does

            // ---- placeables ----
            var soilTemplate = SoilTemplate(templates.transform);
            var boxTemplate = new GameObject("TestBox");
            boxTemplate.transform.SetParent(templates.transform);
            Placement(boxTemplate);
            Collider(boxTemplate);

            var soilData = ScriptableObject.CreateInstance<PlaceableFarmTileData>();
            soilData.tilesetItem = soilItem;
            SoilData = World(soilData, SoilID, soilTemplate);
            BoxData = World(ScriptableObject.CreateInstance<PlaceableWorldObjectData>(), BoxID, boxTemplate);
            BoxData.neededResources.resources.Add(new NeededResourcesEvent.Gold { goldAmount = BoxCost });
            var fence = ScriptableObject.CreateInstance<PlaceableTilesetData>();
            fence.tilesetItem = fenceItem;
            fence.OnPlaceEvents = new CustomEventList { events = new List<CustomEvent>() };
            FenceData = Common(fence, FenceID);

            var ctrlGo = new GameObject("TestPlaceableController");
            ctrlGo.SetActive(false);
            ctrlGo.transform.SetParent(root.transform);
            var trig = new GameObject("trigger");
            trig.transform.SetParent(ctrlGo.transform);
            Ctrl = ctrlGo.AddComponent<PlaceableController>();
            Ctrl.enabled = false;   // its Start builds the building HUD
            Ctrl.triggerForward = trig.AddComponent<TriggerForward>();
            Ctrl.dataBase = ScriptableObject.CreateInstance<PlaceablesDataBase>();
            Ctrl.dataBase.placeables = new List<PlaceableData> { SoilData, BoxData, FenceData };
            Ctrl.placeablesParent = new GameObject("=== Placeables Runtime").transform;
            Ctrl.placeablesParent.SetParent(root.transform);
            Ctrl.tilesetManager = Tiles;
            Ctrl.cam = Camera.main;
            Ctrl.gridLens = Child(ctrlGo, "gridLens");
            Ctrl.worldGrid = Child(ctrlGo, "worldGrid");
            Ctrl.objectGrid = Child(ctrlGo, "objectGrid").AddComponent<SpriteRenderer>();
            Ctrl.amountObject = Child(ctrlGo, "amount");
            Ctrl.amountDisplay = Child(ctrlGo, "amountText").AddComponent<TMPro.TextMeshProUGUI>();
            ctrlGo.SetActive(true);
            GameData.Register(GameData.Call.OnLoad, Ctrl.OnLoad);
            GameData.Register(GameData.Call.OnSave, Ctrl.OnSave);
            root.AddComponent<Teardown>();

            yield return null;   // the tile manager's Start (its tables, its load/save hooks)
            yield return null;
            // What a scene load does with the world's estate data.
            Tiles.OnLoad(GameData.playerData, GameData.worldData);
            Ctrl.OnLoad(GameData.playerData, GameData.worldData);
            Plugin.Log.LogInfo($"[test] Estate fixture built: {Ctrl.saved.Count} placeable(s), {Tiles.currentTilemapData.Sum(l => l.Value.Count)} tile(s), {Soils().Count()} soil(s)");
        }

        private class Teardown : MonoBehaviour
        {
            private void OnDestroy()
            {
                if (Farm != null) Bus<DayTimeEvent>.Listeners -= Farm.UpdateSoils;
                if (Ctrl != null) { GameData.Unregister(Ctrl.OnLoad); GameData.Unregister(Ctrl.OnSave); }
            }
        }

        private static GameObject Child(GameObject parent, string name)
        {
            var g = new GameObject(name);
            g.transform.SetParent(parent.transform);
            return g;
        }

        private static TilesetItem TileItem(string id, TilemapLayer layer)
        {
            var item = ScriptableObject.CreateInstance<TilesetItem>();
            item.saveID = id;
            item.tilemapLayer = layer;
            item.tile = ScriptableObject.CreateInstance<Tile>();
            return item;
        }

        private static T Common<T>(T data, string id) where T : PlaceableData
        {
            data.name = id;
            data.saveID = id;
            data.type = PlaceableType.Crafted;
            data.continuousPlacing = true;   // keeps PlaceObject from closing the (missing) placing HUD
            data.neededResources = new NeededResourcesEventList { resources = new List<NeededResourcesEvent>() };
            data.sfxOnPlace = new AK.Wwise.Event();
            data.sfxOnRemove = new AK.Wwise.Event();
            return data;
        }

        private static PlaceableData World(PlaceableWorldObjectData data, string id, GameObject prefab)
        {
            data.placeObject = prefab;
            data.OnPlaceEvents = new CustomEventList { events = new List<CustomEvent>() };
            return Common(data, id);
        }

        private static void Placement(GameObject go)
        {
            var p = go.AddComponent<PlacementObjectComponent>();
            p.onPlaceEvents = new CustomEventList { events = new List<CustomEvent>() };
            p.onRemoveEvents = new CustomEventList { events = new List<CustomEvent>() };
        }

        // the collider the destroy tool finds (its parent is the placed object)
        private static void Collider(GameObject go)
        {
            var c = Child(go, "collider");
            var box = c.AddComponent<BoxCollider2D>();
            box.isTrigger = true;
            box.size = Vector2.one * 0.8f;
        }

        private static GameObject SoilTemplate(Transform parent)
        {
            var go = new GameObject("TestSoil");
            go.transform.SetParent(parent);
            var rend = go.AddComponent<SpriteRenderer>();
            go.AddComponent<Animator>();
            var soil = go.AddComponent<SoilTile>();
            Placement(go);
            Collider(go);
            soil.rend = rend;
            soil.fertRenderer = Child(go, "fert").AddComponent<SpriteRenderer>();
            soil.cropRenderer = Child(go, "crop").AddComponent<SpriteRenderer>();
            soil.overlayWatered = Child(go, "watered");
            soil.overlayFertilized = Child(go, "fertilized");
            soil.overlayHighlight = Child(go, "highlight");
            soil.dryIndicator = Child(go, "dry");
            soil.readyIndicator = Child(go, "ready");
            soil.center = Child(go, "center");
            soil.interactionIcon = Child(go, "icon");
            soil.buttonActionIcon = Child(go, "button");
            soil.mediumRangeTrigger = Child(go, "medium").AddComponent<TriggerForward>();
            soil.soilTrigger = Child(go, "soilTrigger").AddComponent<TriggerForward>();
            soil.clickable = Child(go, "click").AddComponent<SoilClickable>();
            soil.vfxPlow = Child(go, "vfxPlow").AddComponent<ParticleSystem>();
            soil.vfxChange = Child(go, "vfxChange").AddComponent<ParticleSystem>();
            soil.sfxOnPlow = new AK.Wwise.Event();
            soil.sfxOnHavest = new AK.Wwise.Event();
            return go;
        }

        // ---- driving it like a player ----

        // What the controller does when the player confirms a placement at this spot.
        public static void Place(PlaceableData data, Vector3 pos)
        {
            Ctrl.isPlacing = true;
            Ctrl.canPlace = true;
            Ctrl.currentPlaceableData = data;
            Ctrl.currentPlaceableComponent = null;
            Ctrl.turnable = null;
            Ctrl.lastSnapPosition = pos - data.anchor;
            try { Ctrl.PlaceObject(); }
            finally { Ctrl.isPlacing = false; Ctrl.currentPlaceableData = null; }
        }

        // What the destroy tool does when the player clicks the cell at this spot.
        public static void Remove(Vector3 pos)
        {
            Vector3 corner = new Vector3(Mathf.Floor(pos.x), Mathf.Floor(pos.y), 0f);
            Ctrl.objectResults = Ctrl.placeablesParent.GetComponentsInChildren<Collider2D>()
                .Where(c => c.transform.parent != null && (c.transform.parent.position - pos).sqrMagnitude < 0.01f).ToList();
            Ctrl.tilemapResults = Tiles.GetTilemapsWithObject(corner + new Vector3(0.5f, 0.5f, 0f));
            Ctrl.lastSnapPosition = corner;
            Ctrl.RemoveSelectedObject(default);
        }

        public static IEnumerable<SoilTile> Soils() =>
            Ctrl == null ? Enumerable.Empty<SoilTile>() : Ctrl.placeablesParent.GetComponentsInChildren<SoilTile>();

        public static SoilTile SoilAt(Vector3 pos) => Soils().FirstOrDefault(s => (s.transform.position - pos).sqrMagnitude < 0.01f);

        public static bool HasObject(string id, Vector3 pos) =>
            Ctrl.placeablesParent.GetComponentsInChildren<PlacementObjectComponent>().Any(p => p.RelatedPlaceableDataID == id && (p.transform.position - pos).sqrMagnitude < 0.01f);

        public static bool HasSaved(string id, Vector3 pos) =>
            Ctrl.saved.Any(p => p.placeableID == id && (p.worldPosition - pos).sqrMagnitude < 0.01f);

        public static bool HasTile(TilemapLayer layer, Vector3 pos)
        {
            var map = Tiles.tilemapsDict[layer];
            var cell = map.WorldToCell(pos);
            cell.z = 0;
            return map.GetTile(cell) != null && Tiles.currentTilemapData[layer].ContainsKey(cell);
        }
    }
}
