using System.Collections;
using System.Globalization;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        private static PlayerState LatestOf(byte id) =>
            MPRunner.Instance.Presence != null && MPRunner.Instance.Presence.Remotes.TryGetValue(id, out var r) ? r.Latest : null;

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static float ParseF(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        // Pings: the guest pings a spot, the host shows it (position, name, colour); off screen it's an edge badge;
        // the rate limit holds; the host pings back.
        [Scenario("pings", Timeout = 200)]
        private static IEnumerator PingsTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            var pings = Pings.Instance;
            if (!Check("pings attached to the session", pings != null && s.Others.Any())) { yield return Finish(""); yield break; }
            byte other = s.Others.First().Id;
            string otherName = s.Players[other].Name;
            yield return WaitFor(() => LatestOf(other)?.Scene == Game.SceneName && LatestOf(other)?.Area == Game.AreaName, 20f);
            Check("both in the same room", LatestOf(other)?.Area == Game.AreaName, $"{LatestOf(other)?.Area} / {Game.AreaName}");
            yield return new WaitForSecondsRealtime(1f);
            var me = Game.LocalPlayer;

            // The cursor -> ground mapping: a world point's screen position maps back to it.
            var cam = Camera.main;
            if (Check("camera found", cam != null))
            {
                Vector2 probe = (Vector2)me.transform.position + new Vector2(2f, 1f);
                var sp = cam.WorldToScreenPoint(new Vector3(probe.x, probe.y, me.transform.position.z));
                bool mapped = Pings.CursorToWorld(sp, out var back);
                Check("cursor maps to the ground under it", mapped && (back - probe).magnitude < 0.05f, $"{probe} -> {sp} -> {back}");
            }

            // 1. The guest pings a spot near both players.
            if (!s.IsHost)
            {
                Vector2 spot = (Vector2)me.transform.position + new Vector2(2.5f, -1.5f);
                Check("guest pinged", pings.TryPing(spot));
                Chat.Send($"#pingspot={F(spot.x)},{F(spot.y)}");
                Check("guest shows its own ping", pings.Active.Any(p => p.Local && p.Position == spot));
                yield return DevHarness.Screenshot("ping-guest", 0.7f);
            }
            else
            {
                yield return WaitFor(() => pings.Received >= 1 && peerData.ContainsKey("pingspot"), 15f);
                var last = pings.LastReceived;
                if (Check("host received the guest's ping", last != null, $"{pings.Received} received"))
                {
                    var parts = peerData.TryGetValue("pingspot", out var ps) ? ps.Split(',') : new[] { "0", "0" };
                    var spot = new Vector2(ParseF(parts[0]), ParseF(parts[1]));
                    Check("ping is the guest's", last.From == other && last.Name == otherName, $"#{last.From} {last.Name}");
                    Check("ping is at the spot the guest pinged", (last.Position - spot).magnitude < 0.01f, $"{last.Position} vs {spot}");
                    Check("ping is in our room", last.Scene == Game.SceneName && last.Area == Game.AreaName, $"{last.Scene}/{last.Area}");
                    Check("ping marker shown", pings.Active.Contains(last));
                    if (!DevHarness.Headless) Check("ping marker on screen", PingMarkers.OnScreen(last));
                    Plugin.Log.LogInfo($"[ping] colour of #{last.From}: {Pings.ColorOf(last.From)}");
                }
                yield return DevHarness.Screenshot("ping-host", 0.7f);   // let the rings roll out
            }
            yield return Barrier("pinged");

            // 2. Off screen: a ping far to the side shows as a badge on the screen's edge.
            if (!s.IsHost)
            {
                // (pings go at least 0.35 s apart: headless, nothing waits for a screenshot in between any more)
                yield return new WaitForSecondsRealtime(0.4f);
                Check("guest pinged far away", pings.TryPing((Vector2)me.transform.position + new Vector2(30f, 6f)));
            }
            else
            {
                yield return WaitFor(() => pings.Received >= 2, 10f);
                var far = pings.LastReceived;
                Check("host received the far ping", pings.Received >= 2);
                if (!DevHarness.Headless && far != null) Check("far ping is off screen (edge badge)", !PingMarkers.OnScreen(far));
                yield return DevHarness.Screenshot("ping-offscreen-host", 0.5f);
            }
            yield return Barrier("offscreen");

            // 3. Rate limit: a fresh burst of six tries lets three through, and the host gets exactly those.
            int receivedBefore = pings.Received;
            yield return new WaitForSecondsRealtime(4.5f);   // (the earlier pings leave the window)
            if (!s.IsHost)
            {
                int accepted = 0;
                for (int i = 0; i < 6; i++)
                {
                    if (pings.TryPing((Vector2)me.transform.position + new Vector2(-2f + i * 0.8f, 2f))) accepted++;
                    yield return new WaitForSecondsRealtime(0.4f);
                }
                Check("rate limit: 3 of 6 quick pings sent", accepted == 3, $"{accepted} sent, {pings.Limited} limited");
                Chat.Send("#burst=" + accepted);
            }
            yield return Barrier("burst");
            if (s.IsHost)
            {
                int accepted = peerData.TryGetValue("burst", out var b) ? int.Parse(b) : -1;
                yield return WaitFor(() => pings.Received - receivedBefore >= accepted, 5f);
                yield return new WaitForSecondsRealtime(0.5f);
                Check("host got exactly the pings the guest sent", pings.Received - receivedBefore == accepted, $"{pings.Received - receivedBefore} vs {accepted}");
                Check("each player shows 3 markers at most", pings.Active.Count(p => p.From == other) <= 3);
                // Pings fade out after a few seconds.
                yield return WaitFor(() => pings.Active.Count == 0, Pings.Lifetime + 0.5f);
                Check("pings fade out", pings.Active.Count == 0, $"{pings.Active.Count} left");
                // 4. The host pings back.
                Check("host pinged", pings.TryPing((Vector2)me.transform.position + new Vector2(-2f, 1f)));
            }
            else
            {
                yield return WaitFor(() => pings.Received >= 1, 25f);
                var last = pings.LastReceived;
                Check("guest received the host's ping", last != null && last.From == Protocol.HostId && last.Name == otherName, last == null ? "none" : $"#{last.From} {last.Name}");
                yield return DevHarness.Screenshot("ping-from-host-guest", 0.5f);
            }
            Plugin.Log.LogInfo($"[ping] sent={pings.Sent} received={pings.Received} limited={pings.Limited} dropped={pings.Dropped} elsewhere={pings.Elsewhere}");
            yield return Barrier("done", 40f);
            yield return Finish("pings work");
        }

        // A teleport waits for the game to run: close anything the game opened on its own (tutorial windows), as a
        // player would.
        private static IEnumerator NoOpenPanels()
        {
            yield return WaitFor(() => !UIPanels.HasOpenedPanel, 3f);
            if (!UIPanels.HasOpenedPanel) yield break;
            Plugin.Log.LogInfo($"[tp] Closing open game panels (state {Game.Manager.gameState})");
            // tutorial windows are locked until their "Got it!"
            var tut = Singleton<TutorialManager>.HasMain ? Singleton<TutorialManager>.main : null;
            if (tut != null && tut.panelPopup != null && tut.panelPopup.activeSelf) tut.ClosePopupWindow();
            yield return WaitFor(() => !UIPanels.HasOpenedPanel, 1f);
            if (UIPanels.HasOpenedPanel) Object.FindAnyObjectByType<UIManager>()?.panelsManager?.CloseAll();
            yield return WaitFor(() => !UIPanels.HasOpenedPanel && Game.Manager.gameState == GameState.Playing, 5f);
            Check("no game panel left open", !UIPanels.HasOpenedPanel, Game.Manager.gameState.ToString());
        }

        // Where a player lands when using one of this room's doors: a walkable spot inside it.
        private static Vector2? SpawnPointIn(string area)
        {
            foreach (var t in Object.FindObjectsByType<Teleporter>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(t => NetIds.ScenePath(t.transform)))
            {
                var a = t.GetComponentInParent<GameArea>(true);
                if (a == null || a.name != area || t.spawnPoint == null) continue;
                return t.SpawnPosition;
            }
            return null;
        }

        // Teleport to a teammate: refused when hurt or when the host turns it off; to the host's room in the same
        // scene (/tp also through the chat command), then to another scene.
        [Scenario("tptoteammate", Timeout = 240)]
        private static IEnumerator TeleportToTeammateTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            var tp = TeamTeleport.Instance;
            var pings = Pings.Instance;
            if (!Check("teleport attached to the session", tp != null && s.Others.Any())) { yield return Finish(""); yield break; }
            byte other = s.Others.First().Id;
            string otherName = s.Players[other].Name;
            const string house = "02_House", hub = "VitromotusCrystal";
            var me = Game.LocalPlayer;

            // The host goes into a house (no enemies); the guest stays at the start.
            if (s.IsHost)
            {
                var spot = SpawnPointIn(house);
                if (Check($"found a spot in {house}", spot != null)) yield return TeleportTo(house, spot.Value);
            }
            yield return Barrier("placed", 40f);

            if (!s.IsHost)
            {
                yield return WaitFor(() => LatestOf(other)?.Area == house, 10f);
                var host = LatestOf(other);
                Check("guest sees the host in the house", host?.Area == house, host?.Area);
                Check("guest starts away from the host", host != null && (host.Position - (Vector2)me.transform.position).magnitude > 3f);
                // Just hurt: that's combat. (Health taken directly: real damage opens the game's first-hit tutorial
                // window, which pauses the game until someone closes it.)
                me.currentHealth -= 1;
                yield return null;
                yield return null;
                bool hurtOk = tp.Request(other, out string hurtWhy);
                Check("no teleport right after being hurt", !hurtOk && hurtWhy.Contains("combat"), hurtWhy);
                me.ResetToFullHealth();
                yield return new WaitForSecondsRealtime(TeamTeleport.HurtSeconds + 0.5f);
            }
            yield return Barrier("hurt-checked");

            // The host turns it off: refused.
            if (s.IsHost) tp.SetAllowed(false);
            else
            {
                yield return WaitFor(() => !TeamTeleport.Allowed, 10f);
                Check("guest got the host's rule (off)", !TeamTeleport.Allowed, $"{tp.RulesReceived} rule messages");
                bool offOk = tp.Request(other, out string offWhy);
                Check("refused while the host doesn't allow it", !offOk, offWhy);
            }
            yield return Barrier("rule-off");

            // Back on: the guest teleports to the host (same scene, another room).
            if (s.IsHost) tp.SetAllowed(true);
            else
            {
                yield return WaitFor(() => TeamTeleport.Allowed, 10f);
                Check("guest got the host's rule (on)", TeamTeleport.Allowed);
                yield return NoOpenPanels();
                bool ok = tp.Request(other, out string msg);
                Check("teleport to the host accepted", ok, msg);
                yield return WaitFor(() => tp.Teleports >= 1 && !tp.Busy, 20f);
                yield return new WaitForSecondsRealtime(0.5f);
                var host = LatestOf(other);
                float d = host != null ? (host.Position - (Vector2)me.transform.position).magnitude : -1f;
                Check("guest arrived in the host's room", Game.AreaName == house, Game.AreaName);
                Check("guest is next to the host", host != null && d < 2f, $"{d:F2} units");
                yield return DevHarness.Screenshot("tp-same-scene-guest");
            }
            yield return Barrier("tp1", 40f);
            if (s.IsHost)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                var guest = LatestOf(other);
                float d = guest != null ? (guest.Position - (Vector2)me.transform.position).magnitude : -1f;
                Check("host sees the guest beside it", guest != null && guest.Area == Game.AreaName && d < 2.5f, $"{guest?.Area} {d:F2} units");
                yield return DevHarness.Screenshot("tp-same-scene-host");
            }

            // The host goes to another scene (the crystal hub; its first-visit cutscene skipped).
            GameData.gameBools[BoolID.CutsceneCrystal] = true;
            if (s.IsHost)
            {
                GuestWorld.Travel(hub, "", Vector3.zero, newWorld: false);
                yield return WaitFor(() => Game.InGame && Game.SceneName == hub && Game.Manager.gameState == GameState.Playing, 90f);
                Check("host went to the hub", Game.SceneName == hub);
                yield return new WaitForSecondsRealtime(1f);
            }
            else
            {
                yield return WaitFor(() => LatestOf(other)?.Scene == hub, 120f);
                Check("guest sees the host in the hub", LatestOf(other)?.Scene == hub);
                // A ping here, while the host is elsewhere: a chat notice there instead of a marker.
                Check("guest pinged (host elsewhere)", pings.TryPing((Vector2)me.transform.position + Vector2.right));
            }
            yield return Barrier("host-in-hub", 120f);

            if (s.IsHost)
            {
                yield return WaitFor(() => pings.Elsewhere >= 1, 10f);
                Check("a ping from another scene became a chat notice", pings.Elsewhere >= 1 && pings.Active.Count == 0
                    && Chat.Lines.Any(l => l.from == null && l.text.Contains("pinged in")), $"{pings.Elsewhere} elsewhere");
            }
            else
            {
                Chat.Send("/help");
                Check("/help lists /tp", Chat.Lines.Any(l => l.from == null && l.text.Contains("/tp <name>")));
                yield return WaitFor(() => tp.CooldownLeft <= 0f && !tp.Busy, 15f);
                yield return NoOpenPanels();
                // Through the chat command this time, with the start of the name.
                Chat.Send("/tp " + otherName.Substring(0, Mathf.Min(3, otherName.Length)).ToLowerInvariant());
                Check("/tp accepted", Chat.Lines.Any(l => l.from == null && l.text.Contains("Teleporting to " + otherName)), tp.LastMessage);
                yield return WaitFor(() => tp.Teleports >= 2 && !tp.Busy, 120f);
                yield return new WaitForSecondsRealtime(1f);
                Check("guest arrived in the hub", Game.SceneName == hub, Game.SceneName);
                var host = LatestOf(other);
                float d = host != null ? (host.Position - (Vector2)Game.LocalPlayer.transform.position).magnitude : -1f;
                Check("guest is next to the host in the hub", host != null && host.Scene == hub && d < 2.5f, $"{d:F2} units");
                yield return DevHarness.Screenshot("tp-other-scene-guest");
            }
            yield return Barrier("tp2", 150f);
            if (s.IsHost)
            {
                var guest = LatestOf(other);
                Check("host sees the guest in the hub", guest != null && guest.Scene == hub, guest?.Scene);
            }
            Plugin.Log.LogInfo($"[tp] teleports={tp.Teleports} refusals={tp.Refusals} rules={tp.RulesReceived}");
            yield return Barrier("done", 40f);
            yield return Finish("teleport to teammates works");
        }
    }
}
