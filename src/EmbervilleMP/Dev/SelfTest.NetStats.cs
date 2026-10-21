using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Bandwidth before/after the trims (NetStats.Trim), measured in one run: both players idle, then walking,
        // then idle among enemies (the host runs them), each with the trims off and on. Bytes/s per message type go
        // to the log ([netstats] lines); the checks make sure the trims save what they should and sync still holds.
        [Scenario("netstats", Timeout = 200, Switches = "netstats")]
        private static IEnumerator NetStatsTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            if (s == null || s.Players.Count < 2) { yield return Finish(""); yield break; }
            var presence = MPRunner.Instance.Presence;
            var me = Game.LocalPlayer;
            RemotePlayer other = null;
            yield return WaitFor(() => (other = presence.Remotes.Values.FirstOrDefault(r => r.Shown)) != null, 30f);
            Check("other player is shown", other != null);
            var es = EnemySync.Instance;
            var rates = new Dictionary<string, List<NetStats.Rate>>();

            IEnumerator Measure(string name, float seconds, IEnumerator during = null)
            {
                var a = NetStats.Take();
                if (during != null) yield return during;
                while (Time.realtimeSinceStartup - a.Time < seconds) yield return null;
                var b = NetStats.Take();
                rates[name] = NetStats.Rates(a, b);
                Plugin.Log.LogInfo(NetStats.Report(a, b, name));
            }

            // (3 s windows: idle players send 4 states a second with the trims, 20 without, so the ratios the checks
            // look at hold with room to spare; 6 s windows only made the numbers smoother)
            const float window = 3f;
            IEnumerator Walk(Vector2 home, float dir)
            {
                for (int k = 0; k < 2; k++)
                {
                    yield return MoveTo(me, home + Vector2.right * dir, 0.75f);
                    yield return MoveTo(me, home, 0.75f);
                }
            }

            foreach (bool trim in new[] { false, true })
            {
                string mode = trim ? "trims on" : "trims off";
                NetStats.Trim = trim;
                yield return Barrier("idle-" + trim, 30f);
                yield return new WaitForSecondsRealtime(1f);
                yield return Measure("players idle, " + mode, window);
                yield return Barrier("walk-" + trim, 30f);
                Vector2 home = me.rb.position;
                yield return Measure("players walking, " + mode, window, Walk(home, s.IsHost ? 1.5f : -1.5f));
                // Sync as precise as ever: where the puppet stands vs where the other one really is.
                yield return new WaitForSecondsRealtime(0.7f);
                string key = "nsfinal" + (trim ? 1 : 0);
                var p = me.transform.position;
                Chat.Send($"#{key}=" + p.x.ToString("F3", CultureInfo.InvariantCulture) + ":" + p.y.ToString("F3", CultureInfo.InvariantCulture));
                yield return WaitFor(() => peerData.ContainsKey(key), 10f);
                yield return new WaitForSecondsRealtime(0.3f);
                if (peerData.TryGetValue(key, out var fin) && other.Puppet != null)
                {
                    var parts = fin.Split(':');
                    var real = new Vector2(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture));
                    float off = Vector2.Distance(real, other.Puppet.transform.position);
                    Check($"puppet is where the other player really is ({mode})", off < 0.1f, $"off by {off:F3}");
                }
            }

            // Among enemies: the host runs them, the guest shows them. Both stand at the room's entrance.
            yield return TeleportTo(CombatArea, s.IsHost ? new Vector2(51.8f, 76.3f) : new Vector2(52.6f, 76.3f));
            me = Game.LocalPlayer;
            me.Invincible = true;
            yield return Barrier("enemies", 40f);
            yield return new WaitForSecondsRealtime(2f);
            foreach (bool trim in new[] { false, true })
            {
                string mode = trim ? "trims on" : "trims off";
                NetStats.Trim = trim;
                yield return Barrier("enemies-" + trim, 30f);
                yield return new WaitForSecondsRealtime(1f);
                int applied = es.StatesApplied;
                yield return Measure("among enemies, " + mode, window);
                if (!s.IsHost)
                    Check($"enemies follow the host ({mode})", EnemySync.GuestPuppets && es.StatesApplied > applied, $"{es.StatesApplied - applied} states applied");
            }
            NetStats.Trim = true;

            float Bytes(string phase, Msg t, bool sent) =>
                rates.TryGetValue(phase, out var l) ? l.Where(r => r.Type == t).Select(r => sent ? r.OutBytes : r.InBytes).DefaultIfEmpty(0f).First() : 0f;
            float Total(string phase) => rates.TryGetValue(phase, out var l) ? l.Sum(r => r.OutBytes + r.InBytes) : 0f;
            string Cmp(float before, float after) => $"{NetStats.Size(before)} -> {NetStats.Size(after)} ({(before > 0 ? (after / before - 1f) * 100f : 0f):+0;-0}%)";

            float idleOff = Bytes("players idle, trims off", Msg.State, true), idleOn = Bytes("players idle, trims on", Msg.State, true);
            float walkOff = Bytes("players walking, trims off", Msg.State, true), walkOn = Bytes("players walking, trims on", Msg.State, true);
            var enemyDir = s.IsHost;   // the host sends enemy states, the guest receives them
            float enemyOff = Bytes("among enemies, trims off", Msg.EnemyStates, enemyDir), enemyOn = Bytes("among enemies, trims on", Msg.EnemyStates, enemyDir);
            Plugin.Log.LogInfo("[netstats] SUMMARY (" + (s.IsHost ? "host" : "guest") + ")" +
                $"\n[netstats]   State sent, idle:        {Cmp(idleOff, idleOn)}" +
                $"\n[netstats]   State sent, walking:     {Cmp(walkOff, walkOn)}" +
                $"\n[netstats]   EnemyStates {(enemyDir ? "sent" : "received")}, idle room: {Cmp(enemyOff, enemyOn)}" +
                $"\n[netstats]   all traffic, idle:       {Cmp(Total("players idle, trims off"), Total("players idle, trims on"))}" +
                $"\n[netstats]   all traffic, walking:    {Cmp(Total("players walking, trims off"), Total("players walking, trims on"))}" +
                $"\n[netstats]   all traffic, enemies:    {Cmp(Total("among enemies, trims off"), Total("among enemies, trims on"))}");
            Check("idle players send far less with the trims", idleOff > 0 && idleOn < idleOff * 0.5f, Cmp(idleOff, idleOn));
            Check("walking players send less with the trims", walkOff > 0 && walkOn < walkOff * 0.9f, Cmp(walkOff, walkOn));
            Check("enemy states still flow with the trims", enemyOn > 0, Cmp(enemyOff, enemyOn));
            yield return Barrier("done");
            yield return Finish("bandwidth measured");
        }
    }
}
