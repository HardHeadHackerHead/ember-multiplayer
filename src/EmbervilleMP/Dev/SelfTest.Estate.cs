using System.Collections;
using System.Linq;
using System.Text;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // What the hub has for phase D: farming, building, crafting, repairs, portals, the world map.
        [Scenario("estatesurvey")]
        private static IEnumerator EstateSurvey()
        {
            yield return WaitInGame();
            yield return new WaitForSecondsRealtime(3f);
            var sb = new StringBuilder();
            sb.AppendLine($"scene={Game.SceneName} area={Game.AreaName}");
            void Count<T>() where T : Component
            {
                var all = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                sb.AppendLine($"{typeof(T).Name}: {all.Length}" + (all.Length > 0 ? "  e.g. " + string.Join(", ", all.Take(4).Select(x => x.name)) : ""));
            }
            Count<SoilTile>(); Count<FarmingManager>(); Count<PlaceableController>(); Count<TilesetDrawerManager>();
            Count<CraftingStation>(); Count<RepairableBuilding>(); Count<DestructedPortal>(); Count<ChunkPortal>();
            Count<Portal>(); Count<QuickPortal>(); Count<StoreComponent>(); Count<Chest>(); Count<GatheringObject>();
            Count<GameObjectStateLoader>(); Count<DialogueTrigger>(); Count<GateDoor>(); Count<Teleporter>();
            sb.AppendLine($"WorldMap: {Singleton<WorldMap>.HasMain}  TimeManager: {Singleton<TimeManager>.HasMain}");
            foreach (var b in new[] { BoolID.PlaceablesActive, BoolID.RepairablesActive, BoolID.PortalRepairActive, BoolID.CutsceneCrystal })
                sb.AppendLine($"bool {b} = {GameData.gameBools.IsTrue(b)}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, $"estate-{Game.SceneName}.txt"), sb.ToString());
            Plugin.Log.LogInfo("[dev] estate survey:\n" + sb);
            yield return Finish("estate surveyed");
        }
    }
}
