using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace EmbervilleMP.Net
{
    // Bytes and messages per message type, sent and received (payload as the mod sees it: add ~30 bytes of UDP/IP
    // and transport headers per packet for the wire). Sent counts every copy: a host relaying one guest's state to
    // two others sends it twice. --mp-netstats logs a summary every 30 s; the F8 window shows the last second.
    internal static class NetStats
    {
        // Bandwidth trims (idle players send less, compact states, enemy states only on change). --mp-notrim: off.
        public static bool Trim = !Args.Has("notrim");

        public class Counters
        {
            public readonly long[] OutBytes = new long[256], OutMsgs = new long[256], InBytes = new long[256], InMsgs = new long[256], Dropped = new long[256];
            public float Time;

            public Counters Copy()
            {
                var c = new Counters { Time = Time };
                Array.Copy(OutBytes, c.OutBytes, 256);
                Array.Copy(OutMsgs, c.OutMsgs, 256);
                Array.Copy(InBytes, c.InBytes, 256);
                Array.Copy(InMsgs, c.InMsgs, 256);
                Array.Copy(Dropped, c.Dropped, 256);
                return c;
            }
        }

        private static readonly Counters total = new Counters();
        private static Counters secondStart, lastSecond, logStart;
        private static Counters lastSecondEnd;
        private const float LogInterval = 30f;

        public static void Out(byte[] data, int offset, int length)
        {
            if (length < 1) return;
            byte t = data[offset];
            total.OutBytes[t] += length;
            total.OutMsgs[t]++;
        }

        public static void In(Msg type, int length)
        {
            total.InBytes[(byte)type] += length;
            total.InMsgs[(byte)type]++;
        }

        public static void Drop(Msg type) => total.Dropped[(byte)type]++;

        public static Counters Take()
        {
            total.Time = UnityEngine.Time.realtimeSinceStartup;
            return total.Copy();
        }

        public static void Tick()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (secondStart == null) { secondStart = logStart = Take(); return; }
            if (now - secondStart.Time >= 1f)
            {
                lastSecond = secondStart;
                lastSecondEnd = Take();
                secondStart = lastSecondEnd;
            }
            if (now - logStart.Time >= LogInterval)
            {
                var end = Take();
                if (Args.Has("netstats")) Plugin.Log.LogInfo(Report(logStart, end, $"last {end.Time - logStart.Time:F0} s"));
                logStart = end;
            }
        }

        public struct Rate
        {
            public Msg Type;
            public float OutBytes, OutMsgs, InBytes, InMsgs, Dropped;
            public float Bytes => OutBytes + InBytes;
        }

        // Per-second rates between two snapshots, busiest first.
        public static List<Rate> Rates(Counters a, Counters b)
        {
            float dt = Mathf.Max(0.001f, b.Time - a.Time);
            var list = new List<Rate>();
            for (int t = 0; t < 256; t++)
            {
                long ob = b.OutBytes[t] - a.OutBytes[t], ib = b.InBytes[t] - a.InBytes[t], dr = b.Dropped[t] - a.Dropped[t];
                if (ob == 0 && ib == 0 && dr == 0) continue;
                list.Add(new Rate
                {
                    Type = (Msg)t,
                    OutBytes = ob / dt, OutMsgs = (b.OutMsgs[t] - a.OutMsgs[t]) / dt,
                    InBytes = ib / dt, InMsgs = (b.InMsgs[t] - a.InMsgs[t]) / dt,
                    Dropped = dr / dt,
                });
            }
            return list.OrderByDescending(r => r.Bytes).ToList();
        }

        public static string Name(Msg t) => NetGuard.Known(t) ? t.ToString() : "#" + (byte)t;

        public static string Size(float bytesPerSecond) =>
            bytesPerSecond >= 1024f ? (bytesPerSecond / 1024f).ToString("F1", CultureInfo.InvariantCulture) + " KB/s"
                                    : bytesPerSecond.ToString("F0", CultureInfo.InvariantCulture) + " B/s";

        public static string Report(Counters a, Counters b, string title)
        {
            var rates = Rates(a, b);
            var sb = new StringBuilder();
            string who = Session.Current != null ? (Session.Current.IsHost ? "host" : "guest #" + Session.Current.LocalId) : "no session";
            sb.Append($"[netstats] {title} ({who}, trims {(Trim ? "on" : "off")}): out {Size(rates.Sum(r => r.OutBytes))}, in {Size(rates.Sum(r => r.InBytes))}");
            foreach (var r in rates)
            {
                sb.Append($"\n[netstats]   {Name(r.Type),-17} out {Size(r.OutBytes),11} {r.OutMsgs,6:F1}/s   in {Size(r.InBytes),11} {r.InMsgs,6:F1}/s");
                if (r.Dropped > 0) sb.Append($"   dropped {r.Dropped:F1}/s");
            }
            return sb.ToString();
        }

        // ---- F8 window ----

        private static bool showTable;

        public static void DrawGui()
        {
            showTable = GUILayout.Toggle(showTable, "Network traffic");
            if (!showTable) return;
            if (lastSecond == null || lastSecondEnd == null) { GUILayout.Label("  (measuring...)"); return; }
            var rates = Rates(lastSecond, lastSecondEnd);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Message", GUILayout.Width(120));
            GUILayout.Label("Out", GUILayout.Width(80));
            GUILayout.Label("In", GUILayout.Width(80));
            GUILayout.Label("Msg/s", GUILayout.Width(70));
            GUILayout.EndHorizontal();
            foreach (var r in rates.Take(12))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Name(r.Type) + (r.Dropped > 0 ? " !" : ""), GUILayout.Width(120));
                GUILayout.Label(Size(r.OutBytes), GUILayout.Width(80));
                GUILayout.Label(Size(r.InBytes), GUILayout.Width(80));
                GUILayout.Label($"{r.OutMsgs:F0}/{r.InMsgs:F0}", GUILayout.Width(70));
                GUILayout.EndHorizontal();
            }
            GUILayout.Label($"Total: out {Size(rates.Sum(r => r.OutBytes))}, in {Size(rates.Sum(r => r.InBytes))}" +
                            (PeerGuard.TotalDropped > 0 ? $"; {PeerGuard.TotalDropped} bad packets dropped" : ""));
        }
    }
}
