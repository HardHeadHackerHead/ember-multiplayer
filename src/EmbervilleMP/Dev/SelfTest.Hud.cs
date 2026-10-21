using System.Collections;
using System.Globalization;
using System.Linq;
using System.Text;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // The party HUD (2 to 4 players; player #2 is the one who moves around):
        //   1. together: a card per teammate in player-id order, nobody off screen, everyone on the minimap and the map's party box
        //   2. everyone takes a different hit: each screen's cards show the others' health
        //   3. the game hides its HUD: ours goes too
        //   4. #2 dies: the others' cards say DOWN
        //   5. #2 goes to another room: arrows at the screen's edge (both ways), #2 pinned to the minimap's edge
        //   6. #2 goes to another scene: dimmed card "in ...", no arrow, off the minimap
        [Scenario("hud", Timeout = 300, Players = 3)]
        private static IEnumerator HudTest()
        {
            DevHarness.FastDeath = true;   // the cards while #2 is down, not the death animation
            HookPeerData();
            float loadWait = DevHarness.Headless ? 90f : 200f;   // windowed loads can render the dungeon map first
            yield return WaitInGame(loadWait);
            var s = Session.Current;
            if (!Check("session exists", s != null)) { yield return Finish(""); yield break; }
            int players = Args.Int("players", 2);
            yield return WaitFor(() => s.Ready && s.Players.Count >= players, loadWait);
            yield return Barrier("ingame", loadWait);
            var hud = PartyHud.Instance;
            var presence = MPRunner.Instance.Presence;
            var me = Game.LocalPlayer;
            byte myId = s.LocalId;
            const byte mover = 2;
            string tag = s.IsHost ? "host" : "guest" + (myId > 2 ? (myId - 1).ToString() : "");
            System.Action noTutorials = () =>
            {
                if (!Singleton<TutorialManager>.HasMain) return;
                Singleton<TutorialManager>.main.SetAllDone();
                Singleton<TutorialManager>.main.ClosePopupWindow();
            };
            noTutorials();
            GameData.gameBools[BoolID.CanShowMiniMap] = true;   // the minimap opens later in the story

            // 1. Together at the lit entrance of the mushroom cavern.
            yield return TeleportExactly(CombatArea, new Vector2(54.6f + myId * 1.5f, 77f + (myId % 2) * 0.5f));
            yield return Barrier("together", 40f);
            var others = s.Others.OrderBy(p => p.Id).ToList();
            yield return WaitFor(() => hud.Visible && hud.Cards.Count == others.Count && others.All(p => PartyStatus.Instance.Players.ContainsKey(p.Id))
                && others.All(p => presence.Remotes.TryGetValue(p.Id, out var r) && r.Shown), 20f);
            noTutorials();
            Check("party HUD visible with a card per teammate", hud.Visible && hud.Cards.Count == others.Count, $"visible={hud.Visible} cards={hud.Cards.Count}/{others.Count}");
            string order = string.Join(",", hud.Cards.Select(c => c.Id));
            Check("cards in player-id order", order == string.Join(",", others.Select(p => p.Id)), order);
            foreach (var p in others)
            {
                var card = hud.Cards.FirstOrDefault(c => c.Id == p.Id);
                Check($"{p.Name}'s card has their name and level", card != null && card.Name.Main.text.StartsWith(p.Name) && card.Name.Main.text.Contains("Lv "), card?.Name.Main.text ?? "-");
                Check($"{p.Name} is here", card != null && card.State == "here", card?.State ?? "-");
            }
            yield return new WaitForSecondsRealtime(1f);
            Plugin.Log.LogInfo($"[hud] me at {me.transform.position} in {Game.AreaName}; " + string.Join("; ", others.Select(p => presence.Remotes.TryGetValue(p.Id, out var r) && r.Latest != null
                ? $"{p.Name} says {r.Latest.Position} in {r.Latest.Area}, puppet at {(r.Puppet != null ? r.Puppet.transform.position.ToString() : "-")}, {r.SecondsSinceState:F2}s ago" : p.Name + " -")));
            foreach (var p in others)
            {
                Check($"no arrow for {p.Name} (on screen)", !ArrowShown(hud, p.Id));
                var rp = presence.Remotes[p.Id];
                var marker = hud.MinimapMarkers.TryGetValue(p.Id, out var m) ? m : null;
                Check($"{p.Name} on the minimap", marker != null && marker.gameObject.activeInHierarchy && marker.gameObject.layer == LayerMask.NameToLayer("Minimap")
                    && Vector2.Distance(marker.transform.position, rp.Puppet.transform.position) < 0.6f,
                    marker == null ? "no marker" : $"at {marker.transform.position}, player at {rp.Puppet.transform.position}");
            }
            foreach (var p in others)
            {
                var nt = hud.NameTags.TryGetValue(p.Id, out var l) ? l : null;
                Check($"{p.Name}'s name tag in the game's font", nt != null && nt.Root.gameObject.activeInHierarchy && nt.Main.text.Contains(p.Name), nt?.Main.text ?? "none");
            }
            yield return CheckWorldMapParty(hud, others, p => "with you");
            yield return DevHarness.Screenshot("hud-together-" + tag);
            yield return Barrier("shot-together");

            // 2. Everyone takes a different hit (host 10%, #2 20%, ...): every screen follows everyone's health.
            int hit = Mathf.Max(1, me.MaxHealthPoints * 10 * myId / 100);
            me.ApplyDamage(Hit(null, me, hit));
            yield return new WaitForSecondsRealtime(0.3f);
            me.Invincible = true;   // the cavern's enemies mustn't change the numbers now
            Check("own health went down", me.CurrentHealthPoints < me.MaxHealthPoints, $"{me.CurrentHealthPoints}/{me.MaxHealthPoints}");
            Chat.Send($"#hp{myId}={me.HP_Clamped}/{me.MaxHealthPoints}");
            yield return WaitFor(() => others.All(p => peerData.ContainsKey("hp" + p.Id)), 15f);
            foreach (var p in others)
            {
                if (!Check($"{p.Name} reported their health", peerData.TryGetValue("hp" + p.Id, out var hpText))) continue;
                var parts = hpText.Split('/');
                int hp = int.Parse(parts[0]), max = int.Parse(parts[1]);
                PartyStatus.Vitals v = null;
                yield return WaitFor(() => PartyStatus.Instance.Players.TryGetValue(p.Id, out v) && Mathf.Abs(v.Health - hp) <= max / 50 + 1, 6f);
                Check($"{p.Name}'s health arrived", v != null && Mathf.Abs(v.Health - hp) <= max / 50 + 1, $"got {v}, they said {hp}/{max}");
                var card = hud.Cards.First(c => c.Id == p.Id);
                yield return null;
                Check($"{p.Name}'s card shows it", Mathf.Abs(card.ShownHealth - (float)hp / max) < 0.03f && card.ShownHealth < 0.99f, $"bar {card.ShownHealth:F3}, health {(float)hp / max:F3}");
            }
            yield return DevHarness.Screenshot("hud-health-" + tag, 1.2f);   // the bars' drain animation
            yield return Barrier("hp");

            // 3. The game hides its HUD (dialogue, cutscenes): ours goes with it.
            var ui = Object.FindAnyObjectByType<UIManager>();
            object hider = new object();
            ui.HidePlayerHUDStack(hider);
            yield return WaitFor(() => !hud.Visible, 3f);
            Check("party HUD hides with the game's HUD", !hud.Visible);
            ui.ShowPlayerHUDStack(hider);
            yield return WaitFor(() => hud.Visible, 3f);
            Check("party HUD comes back with it", hud.Visible);
            yield return Barrier("hidden");

            // 4. #2 goes down.
            if (myId == mover)
            {
                me.Invincible = false;
                me.ApplyDamage(Hit(null, me, 99999));
                yield return WaitFor(() => me.IsDead, 5f);
                Check("#2 died", me.IsDead);
            }
            else
            {
                var card = hud.Cards.First(c => c.Id == mover);
                yield return WaitFor(() => card.State == "down", 10f);
                Check("#2's card says DOWN", card.State == "down" && card.Grey && card.Tag.Main.text.Contains("DOWN"), $"{card.State} '{card.Tag.Main.text}'");
                yield return DevHarness.Screenshot("hud-down-" + tag, 0.8f);
            }
            yield return Barrier("down");
            if (myId == mover)
            {
                yield return WaitFor(() => !me.IsDead && Game.InGame && Game.Manager.gameState == GameState.Playing, 40f);
                Check("#2 is back on their feet", !me.IsDead);
                me.ResetToFullHealth();
            }
            yield return Barrier("respawned", 60f);

            // 5. #2 goes to another room of the same scene: arrows at the screen's edge, both ways.
            if (myId == mover) yield return TeleportExactly("004_Gather", new Vector2(150f, 130f));
            else yield return TeleportExactly(CombatArea, new Vector2(54.6f + myId * 1.5f, 77f + (myId % 2) * 0.5f));
            yield return Barrier("apart", 40f);
            noTutorials();
            foreach (var p in others)
            {
                bool apart = myId == mover || p.Id == mover;
                yield return WaitFor(() => ArrowShown(hud, p.Id) == apart, 6f);
                Check(apart ? $"arrow to {p.Name} in the other room" : $"no arrow for {p.Name} (still here)", ArrowShown(hud, p.Id) == apart);
                if (!apart) continue;
                var a = hud.Arrows[p.Id];
                Rect r = ((RectTransform)a.Root.parent.parent).rect;
                Vector2 ap = a.Root.anchoredPosition;
                Plugin.Log.LogInfo($"[hud] arrow to {p.Name} at {ap}, pointing {a.Pointer.localEulerAngles.z:F0} deg, world rot {a.Pointer.rotation.eulerAngles}, root rot {a.Root.parent.parent.rotation.eulerAngles} scale {a.Root.parent.parent.lossyScale}");
                bool atEdge = Mathf.Abs(ap.x) > r.width / 2f - 80f || ap.y > r.height / 2f - 80f || ap.y < -r.height / 2f + 240f;
                Check($"the arrow sits at the screen's edge, labelled", atEdge && a.Text.Main.text.Contains(p.Name) && a.Text.Main.text.Contains(" m"), $"at {ap} in {r.size}, '{a.Text.Main.text}'");
                var card = hud.Cards.First(c => c.Id == p.Id);
                Check($"{p.Name}'s card: same scene, other room", card.State == "area", card.State);
                var marker = hud.MinimapMarkers.TryGetValue(p.Id, out var m) ? m : null;
                var cam = GameObject.FindWithTag("MinimapCam")?.GetComponent<Camera>();
                bool inView = marker != null && cam != null && Mathf.Abs(marker.transform.position.x - cam.transform.position.x) <= cam.orthographicSize * cam.aspect
                    && Mathf.Abs(marker.transform.position.y - cam.transform.position.y) <= cam.orthographicSize;
                Check($"{p.Name} pinned to the minimap's edge", marker != null && marker.gameObject.activeInHierarchy && inView, marker == null ? "no marker" : $"{marker.transform.position}");
            }
            yield return DevHarness.Screenshot("hud-arrow-" + tag, 0.5f);
            yield return Barrier("arrows");

            // 6. #2 goes to the crystal hub (another scene).
            const string away = "VitromotusCrystal";
            float travelWait = DevHarness.Headless ? 90f : 240f;   // windowed, the hub's first visit renders the whole dungeon map
            if (myId == mover)
            {
                // (Leaving for the hub from the far room fails in 3-player sessions, HUD or not: from the cavern then.)
                yield return TeleportExactly(CombatArea, new Vector2(54.6f + myId * 1.5f, 77f + (myId % 2) * 0.5f));
                GameData.gameBools[BoolID.CutsceneCrystal] = true;   // its first-visit cutscene would move us on
                GuestWorld.Travel(away, "", Vector3.zero, newWorld: false);
                yield return WaitFor(() => Game.InGame && Game.SceneName == away, travelWait);
                Check("#2 went to the crystal hub", Game.InGame && Game.SceneName == away, Game.SceneName);
                yield return new WaitForSecondsRealtime(1f);
                noTutorials();
                yield return WaitFor(() => hud.Visible && hud.Cards.All(c => c.State == "elsewhere"), 15f);
                foreach (var c in hud.Cards)
                    Check($"#{c.Id}'s card: in the tutorial area", c.State == "elsewhere" && c.Tag.Main.text.Contains("Tutorial"), $"{c.State} '{c.Tag.Main.text}'");
                if (Singleton<UIClock>.HasMain && Singleton<UIClock>.main.gameObject.activeInHierarchy)
                    Check("cards make room for the hub's clock", hud.CardsX > 150f, $"cards at x={hud.CardsX:F0}");
                yield return DevHarness.Screenshot("hud-elsewhere-" + tag);
            }
            else
            {
                var card = hud.Cards.First(c => c.Id == mover);
                yield return WaitFor(() => card.State == "elsewhere", travelWait);
                Check("#2's card: in another scene", card.State == "elsewhere" && card.Tag.Main.text.Contains("Vitromotus") && card.Grey && card.NameGroup.alpha < 0.9f, $"{card.State} '{card.Tag.Main.text}' grey {card.Grey}");
                yield return null;
                Check("no arrow to another scene", !ArrowShown(hud, mover));
                Check("#2 off our minimap", !(hud.MinimapMarkers.TryGetValue(mover, out var m) && m != null && m.gameObject.activeInHierarchy));
                yield return CheckWorldMapParty(hud, others, p => p.Id == mover ? PartyHud.PlaceName(away) : "with you");
                yield return DevHarness.Screenshot("hud-elsewhere-" + tag);
                // The world map, with its party box (windowed runs: building the map's images takes a while).
                if (!DevHarness.Headless && myId == Protocol.HostId && Singleton<WorldMap>.HasMain)
                {
                    Singleton<WorldMap>.main.OpenMap();
                    yield return DevHarness.Screenshot("hud-map-" + tag, 2.5f);
                    Singleton<WorldMap>.main.CloseMap();
                }
            }
            me.Invincible = false;
            Plugin.Log.LogInfo($"[hud] party status sent={PartyStatus.Instance.Sent} received={PartyStatus.Instance.Received}");
            yield return Barrier("done", travelWait + 30f);
            yield return Finish("party HUD follows the party");
        }

        // TeleportTo returns once the room changed; the game moves the player a little later (after its background work).
        private static IEnumerator TeleportExactly(string area, Vector2 pos)
        {
            for (int i = 0; i < 2; i++)
            {
                yield return TeleportTo(area, pos);
                yield return WaitFor(() => Vector2.Distance(Game.LocalPlayer.transform.position, pos) < 1.5f, 10f);
                if (Vector2.Distance(Game.LocalPlayer.transform.position, pos) < 1.5f)
                {
                    // The fade-in ends with the game saving our state (ResetToFullHealth goes back to it): wait for it.
                    yield return new WaitForSecondsRealtime(1.5f);
                    yield break;
                }
                Plugin.Log.LogInfo($"[hud] teleport to {pos} left us at {Game.LocalPlayer.transform.position}; again");
            }
            Check($"standing at {pos} in {area}", Vector2.Distance(Game.LocalPlayer.transform.position, pos) < 1.5f, Game.LocalPlayer.transform.position.ToString());
        }

        private static bool ArrowShown(PartyHud hud, byte id) => hud.Arrows.TryGetValue(id, out var a) && a.Root.gameObject.activeInHierarchy;

        private static IEnumerator CheckWorldMapParty(PartyHud hud, System.Collections.Generic.List<PlayerInfo> others, System.Func<PlayerInfo, string> where)
        {
            var wm = hud.WorldMap;
            System.Func<bool> ok = () => wm != null && others.All(p => wm.Lines.Contains(p.Name + "|" + where(p)));
            yield return WaitFor(ok, 3f);
            Check("world map's party box lists everyone and where they are", ok(), wm == null ? "no box" : string.Join("; ", wm.Lines));
        }

        // Dumps the game's HUD, minimap and world map (to plan the party HUD).
        [Scenario("hudsurvey")]
        private static IEnumerator HudSurvey()
        {
            yield return WaitInGame();
            yield return new WaitForSecondsRealtime(3f);
            GameData.gameBools[BoolID.CanShowMiniMap] = true;
            yield return new WaitForSecondsRealtime(1f);
            var ui = Object.FindAnyObjectByType<UIManager>();
            var sb = new StringBuilder();
            sb.AppendLine($"screen {Screen.width}x{Screen.height} hideStack={ui.hideHudStack.Count}");
            var root = ui.transform;
            while (root.parent != null) root = root.parent;
            DumpUi(root, 0, sb);
            var mmCam = GameObject.FindWithTag("MinimapCam");
            if (mmCam != null)
            {
                var cam = mmCam.GetComponent<Camera>();
                sb.AppendLine($"MinimapCam {FullPath(mmCam.transform)} mask={cam.cullingMask:X} layers={string.Join(",", Enumerable.Range(0, 32).Where(i => (cam.cullingMask & (1 << i)) != 0).Select(i => i + ":" + LayerMask.LayerToName(i)))} rt={(cam.targetTexture ? cam.targetTexture.name + " " + cam.targetTexture.width + "x" + cam.targetTexture.height : "-")} ortho={cam.orthographicSize} pos={cam.transform.position}");
            }
            var main = Camera.main;
            sb.AppendLine($"MainCam mask={string.Join(",", Enumerable.Range(0, 32).Where(i => (main.cullingMask & (1 << i)) != 0).Select(i => i + ":" + LayerMask.LayerToName(i)))}");
            var center = Game.LocalPlayer.transform.Find("MinimapCenter");
            if (center != null) sb.AppendLine($"MinimapCenter layer={center.gameObject.layer}:{LayerMask.LayerToName(center.gameObject.layer)} sortLayer={center.GetComponent<SpriteRenderer>().sortingLayerName} order={center.GetComponent<SpriteRenderer>().sortingOrder} col={center.GetComponent<SpriteRenderer>().color}");
            if (Singleton<WorldMap>.HasMain)
            {
                sb.AppendLine("---- WorldMap");
                DumpUi(Singleton<WorldMap>.main.transform, 0, sb);
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, "hud-survey.txt"), sb.ToString());
            yield return DevHarness.Screenshot("hudsurvey");
            if (Singleton<WorldMap>.HasMain)
            {
                Singleton<WorldMap>.main.OpenMap();
                yield return DevHarness.Screenshot("hudsurvey-map", 3f);
                Singleton<WorldMap>.main.CloseMap();
            }
            Report(true, "surveyed");
            DevHarness.Quit("done");
        }

        private static void DumpUi(Transform t, int depth, StringBuilder sb)
        {
            if (depth > 14) return;
            sb.Append(' ', depth * 2).Append(t.gameObject.activeSelf ? "+ " : "- ").Append(t.name).Append(" L").Append(t.gameObject.layer).Append(" [");
            sb.Append(string.Join(", ", t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c =>
            {
                string s = c.GetType().Name;
                if (c is Image im) s += $"(sprite={(im.sprite ? im.sprite.name : "null")} type={im.type} col={(Color32)im.color} fill={im.fillAmount:F2} mat={(im.material ? im.material.name : "-")})";
                if (c is TMPro.TextMeshProUGUI tm) s += $"(font={(tm.font ? tm.font.name : "-")} size={tm.fontSize} col={(Color32)tm.color} text='{tm.text?.Replace("\n", " ")}')";
                if (c is SpriteRenderer sr) s += $"(sprite={(sr.sprite ? sr.sprite.name : "null")} col={(Color32)sr.color} sort={sr.sortingLayerName}/{sr.sortingOrder})";
                if (c is Canvas cv) s += $"(mode={cv.renderMode} order={cv.sortingOrder} scale={cv.scaleFactor:F2} cam={(cv.worldCamera ? cv.worldCamera.name : "-")})";
                if (c is RawImage ri) s += $"(tex={(ri.texture ? ri.texture.name : "-")})";
                if (c is CanvasGroup cg) s += $"(a={cg.alpha:F2})";
                if (c is Behaviour b && !b.enabled) s += "(off)";
                return s;
            })));
            sb.Append("]");
            if (t is RectTransform rt) sb.Append($" rect=({rt.anchorMin.x:F2},{rt.anchorMin.y:F2})-({rt.anchorMax.x:F2},{rt.anchorMax.y:F2}) pos={rt.anchoredPosition} size={rt.sizeDelta} piv={rt.pivot}");
            else sb.Append(" pos=").Append(t.localPosition);
            if (t.localScale != Vector3.one) sb.Append(" scale=").Append(t.localScale);
            sb.AppendLine();
            foreach (Transform c in t) DumpUi(c, depth + 1, sb);
        }
    }
}
