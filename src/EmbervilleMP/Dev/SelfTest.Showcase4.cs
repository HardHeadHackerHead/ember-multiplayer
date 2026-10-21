using System.Collections;
using System.Linq;
using Cysharp.Threading.Tasks;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // README pictures, not in the suite. "spotsurvey" (one copy, windowed): a screenshot at every room's arrival
        // point of the loaded scene, to pick where the four-player shot goes.
        [Scenario("spotsurvey")]
        private static IEnumerator SpotSurveyTest()
        {
            yield return WaitInGame();
            var me = Game.LocalPlayer;
            me.Invincible = true;
            HideTutorials();
            var rooms = Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(a => a.name).Distinct().OrderBy(n => n).ToList();
            foreach (var room in rooms)
            {
                var spot = SpawnPointIn(room);
                if (spot == null) continue;
                yield return TeleportTo(room, spot.Value);
                yield return new WaitForSecondsRealtime(2.5f);   // the room's lights and the camera settle
                HideTutorials();
                yield return DevHarness.Screenshot($"spot-{Game.SceneName}-{room}");
            }
            yield return Finish("spotsurvey");
        }

        // Where the crystal is (its renderers' names and bounds), with a screenshot from its arrival point and below it.
        [Scenario("crystalscan")]
        private static IEnumerator CrystalScanTest()
        {
            yield return WaitInGame();
            var me = Game.LocalPlayer;
            me.Invincible = true;
            yield return TravelAndWait(CrystalHub, "", Vector3.zero);
            HideTutorials();
            yield return new WaitForSecondsRealtime(2f);
            var sb = new System.Text.StringBuilder();
            foreach (var r in Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None)
                         .Where(r => r.name.IndexOf("rystal", System.StringComparison.OrdinalIgnoreCase) >= 0 || r.bounds.size.x > 4f)
                         .OrderByDescending(r => r.bounds.size.x * r.bounds.size.y).Take(25))
                sb.AppendLine($"{NetIds.ScenePath(r.transform)}  center {(Vector2)r.bounds.center}  size {(Vector2)r.bounds.size}");
            sb.AppendLine($"player at {(Vector2)Game.LocalPlayer.transform.position} in {Game.AreaName}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, "crystal.txt"), sb.ToString());
            yield return DevHarness.Screenshot("crystal-arrival");
            yield return Finish("crystalscan");
        }

        // The README's first picture: four players by the campfire outside the cabin (002_Cavern), name tags and the
        // party HUD up. Run windowed (run-pair -Guests 3 -Width 1280 -Height 720 -Names ... -Extra --mp-clean): every
        // copy takes one, centred on its own player.
        [Scenario("showcase4")]
        private static IEnumerator Showcase4Test()
        {
            const string room = "002_Cavern";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            int players = Args.Int("players", 4);
            HookPeerData();
            yield return WaitInGame();
            var s = Session.Current;
            if (!Check("session exists", s != null)) { yield return Finish("showcase4"); yield break; }
            yield return WaitFor(() => s.Ready && s.Players.Count >= players, 90f);
            yield return Barrier("ingame", 90f);
            Game.LocalPlayer.Invincible = true;
            HideTutorials();
            Equip(new[] {
                new[] { "wpn_irnSwrdSup", "item_shld_irnShld", "item_hvy_irnHlmt", "item_hvy_irnBrstplt", "item_hvy_irnGrvs" },   // a knight
                new[] { "wpn_shrtBw", "item_mdm_rngHlm", "item_mdm_rngCrs", "item_mdm_rngLgs" },                                  // a ranger
                new[] { "wpn_wdnStfSpdr", "item_lght_apprtcHat", "item_lght_apprtcChst", "item_lght_apprtcTrsrs" },               // a mage
                new[] { "wpn_irnAxe", "item_mdm_lthrHlm", "item_mdm_lthrCrs", "item_mdm_lthrLgs", "item_clk_Cttm" } }             // a brawler
                [Mathf.Clamp(s.LocalId - 1, 0, 3)]);
            yield return new WaitForSecondsRealtime(0.5f);
            Game.LocalPlayer.ResetToFullHealth();                // (the armour raised everyone's max health)
            Game.LocalPlayer.RestoreKymia(10000f, showFeedback: false);

            // The host picks the spots: between the cabin's door (the room's arrival point) and its campfire.
            if (s.IsHost)
            {
                Vector2 door = SpawnPointIn(room) ?? Vector2.zero;
                var area = Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).First(a => a.name == room);
                var fire = area.GetComponentsInChildren<Transform>(true)
                    .Where(t => t.name.IndexOf("fire", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(t => ((Vector2)t.position - door).sqrMagnitude).FirstOrDefault();
                Vector2 c = fire != null ? (Vector2)fire.position : door + new Vector2(8f, 0.5f);
                Plugin.Log.LogInfo($"[shot] door {door}, fire {(fire != null ? NetIds.ScenePath(fire) : "none")} at {c}, camera size {(Camera.main != null ? Camera.main.orthographicSize : 0f)}");
                Vector2 mid = door + (c - door) * 0.5f + new Vector2(0f, -0.1f);
                // a shallow arc facing us, above the fence, the host second from the left: (position, direction)
                // (the direction is the one we come from: Up faces the camera)
                var spots = new[] { (mid + new Vector2(-0.55f, -0.15f), Direction.Up), (mid + new Vector2(-1.65f, 0.25f), Direction.Up),
                                    (mid + new Vector2(0.55f, -0.15f), Direction.Up), (mid + new Vector2(1.65f, 0.25f), Direction.Up) };
                string list = string.Join(";", spots.Select(p => $"{p.Item1.x.ToString(inv)},{p.Item1.y.ToString(inv)},{(int)p.Item2}"));
                Chat.Send("#spots=" + list);
                peerData["spots"] = list;   // (our own control lines don't come back to us)
            }
            yield return WaitFor(() => peerData.ContainsKey("spots"), 20f);
            var all = peerData["spots"].Split(';').Select(t => t.Split(',')).ToArray();
            var mine = all[Mathf.Clamp(s.LocalId - 1, 0, all.Length - 1)];
            var at = new Vector2(float.Parse(mine[0], inv), float.Parse(mine[1], inv));
            var here = Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).First(a => a.name == room);
            // (a teleport asked for while the game still fades in from joining is dropped: wait, and try again)
            System.Func<bool> inPlace = () => Game.AreaName == room && ((Vector2)Game.LocalPlayer.transform.position - at).sqrMagnitude <= 0.05f
                                               && Game.Manager.gameState == GameState.Playing;
            yield return new WaitForSecondsRealtime(3f);   // (just joined: the game's own arrival fades first)
            for (int tries = 0; tries < 4 && !inPlace(); tries++)
            {
                yield return WaitFor(() => Game.Manager.gameState == GameState.Playing && !GuestWorld.Switching, 20f);
                yield return new WaitForSecondsRealtime(1f);
                Game.Manager.TeleportPlayerFade(at, (Direction)int.Parse(mine[2]), here).Forget();
                yield return WaitFor(inPlace, 6f);
            }
            Check("in place for the picture", inPlace(), $"{(Vector2)Game.LocalPlayer.transform.position} in {Game.AreaName} vs {at} in {room}");
            yield return Barrier("posed", 40f);

            // A few words in the chat, then the picture (puppets settle, the party HUD fills in, the fades end).
            yield return new WaitForSecondsRealtime(1.5f + 0.4f * s.LocalId);
            string[] lines = { "Everyone here? Into the caves next.", "Right behind you!", "Wait, I'm still warming my hands.", "Last one in fights the spider." };
            Chat.Send(lines[Mathf.Clamp(s.LocalId - 1, 0, lines.Length - 1)]);
            yield return Barrier("talked", 30f);
            yield return new WaitForSecondsRealtime(6f);         // the teleport's arrival marks fade, the bars settle
            HideTutorials();
            yield return DevHarness.Screenshot($"showcase4-{s.LocalId}");
            yield return Barrier("done", 30f);
            yield return Finish("showcase4");
        }

        [Scenario("itemkeys")]
        private static IEnumerator ItemKeysTest()
        {
            yield return WaitInGame();
            Equip(new string[0]);
            var sb = new System.Text.StringBuilder();
            foreach (var d in Resources.FindObjectsOfTypeAll<ItemData>().OrderBy(x => x.name))
                sb.AppendLine($"{d.GetType().Name}	{d.name}	{d.addressKey}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, "loaded-items.txt"), sb.ToString());
            yield return Finish("itemkeys");
        }

        // Equips the first weapon or gear whose item key contains each word (the game's own EquipItem), and writes
        // every item key to items.txt to pick from.
        private static void Equip(string[] words)
        {
            var keys = UnityEngine.AddressableAssets.Addressables.ResourceLocators.SelectMany(l => l.Keys).OfType<string>()
                .Distinct().OrderBy(k => k).ToList();
            System.IO.File.WriteAllLines(System.IO.Path.Combine(DevHarness.OutDir, "items.txt"), keys);
            var me = Game.LocalPlayer;
            foreach (var w in words)
                foreach (var k in keys.Where(k => k == w).Concat(keys.Where(k => k != w && k.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0)))
                {
                    var d = ItemCodec.Find(k);
                    if (!(d is CollectableWeapon) && !(d is CollectableGear)) continue;
                    try { new ItemObject(d).OnUse(me.characterStats); Plugin.Log.LogInfo($"[shot] equipped {k}"); }
                    catch (System.Exception ex) { Plugin.Log.LogWarning($"[shot] {k}: {ex.Message}"); continue; }
                    break;
                }
            if (Singleton<Inventory>.HasMain) Singleton<Inventory>.main.UpdateEquippedList();
        }

        private static void HideTutorials()
        {
            if (!Singleton<TutorialManager>.HasMain) return;
            Singleton<TutorialManager>.main.SetAllDone();
            Singleton<TutorialManager>.main.ClosePopupWindow();
        }
    }
}
