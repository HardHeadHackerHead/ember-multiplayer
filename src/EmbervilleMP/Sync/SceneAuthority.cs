using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;

namespace EmbervilleMP.Sync
{
    // Players travel independently, so every scene with players in it needs exactly one machine running it
    // (its enemies, rooms, hits on the others): the host when it's there, otherwise the lowest-numbered player
    // there. Every copy works this out the same way from where everyone is.
    internal static class SceneAuthority
    {
        public const float PresenceTimeout = 3f;

        public static byte For(string scene, byte exclude = 0)
        {
            var s = Session.Current;
            if (s == null || !s.Ready || string.IsNullOrEmpty(scene)) return 0;
            var ids = new List<byte>();
            if (s.LocalId != exclude && Game.InScene && Game.SceneName == scene) ids.Add(s.LocalId);   // (dead here too)
            var presence = MPRunner.Instance?.Presence;
            if (presence != null)
                foreach (var r in presence.Remotes.Values)
                    if (r.Info.Id != exclude && r.Latest != null && r.Latest.Scene == scene && r.SecondsSinceState < PresenceTimeout)
                        ids.Add(r.Info.Id);
            if (ids.Count == 0) return 0;
            return ids.Contains(Protocol.HostId) ? Protocol.HostId : ids.Min();
        }

        public static byte Mine => For(Game.SceneName);

        // Others in my scene (any machine, not just the authority).
        public static bool OthersInMyScene()
        {
            var presence = MPRunner.Instance?.Presence;
            if (Session.Current == null || presence == null) return false;
            string scene = Game.SceneName;
            return presence.Remotes.Values.Any(r => r.Latest != null && r.Latest.Scene == scene && r.SecondsSinceState < PresenceTimeout);
        }
    }
}
