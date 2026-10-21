using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Cheap plausibility checks on who sent a message, for handlers whose decisions belong to one machine. They
    // lean towards accepting: a check that can't tell (no state from that player yet, a runner handoff a moment
    // ago) lets the message through, so honest play never loses a message to them.
    internal static class Senders
    {
        public const float RunnerGrace = 5f;
        private static readonly Dictionary<byte, float> formerRunners = new Dictionary<byte, float>();

        // EnemySync: our scene's runner changed (the old one's last decisions may still be on their way).
        public static void RunnerChanged(byte old)
        {
            if (old != 0) formerRunners[old] = Time.realtimeSinceStartup;
        }

        // Does `from` run the scene we're in (or did, a moment ago)? Loot decisions (who got a drop, new drops) are its.
        public static bool IsRunner(byte from)
        {
            var es = EnemySync.Instance;
            if (Session.Current == null || es == null || es.Authority == 0) return true;
            if (from == es.Authority || from == SceneAuthority.Mine) return true;
            return formerRunners.TryGetValue(from, out float t) && Time.realtimeSinceStartup - t < RunnerGrace;
        }

        // False only when that player's latest state (under a second old) puts it in another scene: it can't be
        // acting on an object of this one.
        public static bool MaybeIn(byte from, string scene)
        {
            var presence = MPRunner.Instance?.Presence;
            if (presence == null || !presence.Remotes.TryGetValue(from, out var rp) || rp.Latest == null) return true;
            if (rp.SecondsSinceState > 1f || string.IsNullOrEmpty(rp.Latest.Scene)) return true;
            return rp.Latest.Scene == scene;
        }

        // A scene name as the game has them (kept in tables and side files: no junk, no tabs or line breaks).
        public static bool SaneName(string s) => !string.IsNullOrEmpty(s) && s.Length <= 64 && !s.Any(char.IsControl);
    }
}
