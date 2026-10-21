using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Lists what's in the current scene: areas, enemies, teleporters (for planning the sync).
        [Scenario("survey")]
        private static IEnumerator Survey()
        {
            yield return WaitInGame();
            yield return new WaitForSecondsRealtime(3f);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"scene={Game.SceneName} area={Game.AreaName} player={Game.LocalPlayer.transform.position}");
            foreach (var a in Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"area '{a.name}' active={a.gameObject.activeSelf}");
            foreach (var e in Object.FindObjectsByType<Enemy>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"placed-enemy '{e.name}' id={e.ID} pos={e.transform.position} area={(e.GetComponentInParent<GameArea>(true)?.name ?? "-")} graph={(e.behaviorGraphAgent != null && e.behaviorGraphAgent.Graph != null ? e.behaviorGraphAgent.Graph.name : "-")}");
            foreach (var c in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string tn = c.GetType().Name;
                if (tn.Contains("Spawn") || tn.Contains("Wave") || tn.Contains("Egg") || tn == "ItemDropper" || tn == "Chest")
                    sb.AppendLine($"component {tn} on '{FullPath(c.transform)}' area={(c.GetComponentInParent<GameArea>(true)?.name ?? "-")}");
            }
            // Shops, stations, repairables, saved story objects, NPC dialogue (phase C7-C9, D3-D4).
            foreach (var st in Object.FindObjectsByType<StoreComponent>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var col = Singleton<StoreManager>.HasMain && st.ID != null && Singleton<StoreManager>.main.allStores.TryGetValue(st.ID, out var c) ? c : null;
                sb.AppendLine($"store '{FullPath(st.transform)}' id={st.ID} save={st.saveStore} active={st.gameObject.activeInHierarchy} stock={(col != null ? string.Join(",", col.mainItems.Select(i => i.itemObject.data.name + "x" + i.itemObject.amount)) : "-")}");
            }
            foreach (var g in Object.FindObjectsByType<GameObjectStateLoader>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"saved-object '{FullPath(g.transform)}' guid={g.guid} pos={g.savePosition} act={g.saveActiveState} active={g.gameObject.activeSelf} enemy={(g.GetComponent<Enemy>() != null)} at={g.transform.position}");
            foreach (var cs in Object.FindObjectsByType<CraftingStation>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"crafting '{FullPath(cs.transform)}' save={cs.saveID} type={cs.StationType} recipes={cs.availableRecipes.Count} active={cs.gameObject.activeInHierarchy}");
            foreach (var rb in Object.FindObjectsByType<RepairableBuilding>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"repairable '{FullPath(rb.transform)}' save={rb.saveID} repaired={rb.isRepaired} costs={string.Join(",", rb.GetItemCosts.Select(x => x.itemData?.name + "x" + x.Amount))} active={rb.gameObject.activeInHierarchy}");
            foreach (var dp in Object.FindObjectsByType<DestructedPortal>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"portal-ruin '{FullPath(dp.transform)}' repaired={dp.isRepaired} active={dp.gameObject.activeInHierarchy}");
            foreach (var dt in Object.FindObjectsByType<DialogueTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"dialogue '{FullPath(dt.transform)}' uid={dt.uniqueID} npc={dt.npcName} enemy={(dt.GetComponent<Enemy>() != null)} active={dt.gameObject.activeInHierarchy}");
            sb.AppendLine($"bools RepairablesActive={GameData.gameBools.IsTrue(BoolID.RepairablesActive)} PortalRepairActive={GameData.gameBools.IsTrue(BoolID.PortalRepairActive)} CutsceneCrystal={GameData.gameBools.IsTrue(BoolID.CutsceneCrystal)}");
            sb.AppendLine($"GameManager.dungeonPrefab={(Game.Manager.dungeonPrefab != null)} worldGenerator={(Game.Manager.worldGenerator != null)}");
            foreach (var e in EnemiesManager.AllEnemies)
                sb.AppendLine($"enemy '{e.name}' id={e.ID} pos={e.transform.position} active={e.gameObject.activeInHierarchy} area={(e.GetComponentInParent<GameArea>(true)?.name ?? "-")} path={FullPath(e.transform)}");
            foreach (var t in Object.FindObjectsByType<Teleporter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"teleporter '{t.name}' id={t.teleporterID} scene={t.sceneToLoad} dest={t.destinationID} area={(t.GetComponentInParent<GameArea>(true)?.name ?? "-")} pos={t.transform.position}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, $"survey-{Game.SceneName}.txt"), sb.ToString());
            Plugin.Log.LogInfo($"[dev] survey: {EnemiesManager.AllEnemies.Count} enemies");
            yield return DevHarness.Screenshot("survey-" + Game.SceneName);
            Report(true, "surveyed");
            DevHarness.Quit("done");
        }

        public static string FullPath(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
