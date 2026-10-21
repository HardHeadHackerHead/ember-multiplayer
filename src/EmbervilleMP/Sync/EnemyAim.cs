using System.Collections.Generic;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // A replayed enemy attack aimed at our character goes where the runner's real one goes: at our puppet on the
    // runner's screen, which shows us as we were a moment ago (its interpolation delay and the trip there).
    // EnemyAttackAim says which moment of our clock that puppet showed when the attack started; the replay's target
    // becomes a stand-in walking our own path from that moment on, so the projectile we see (or the spot its attack
    // object lands on) is the one that decides the hit.
    internal static class EnemyAim
    {
        private const float Keep = 3f;
        private struct Sample { public float T; public Vector3 Pos, Center; }
        private static readonly List<Sample> trail = new List<Sample>();
        private static readonly Dictionary<Enemy, AimProxy> proxies = new Dictionary<Enemy, AimProxy>();

        // Tests: a fresh world (our path so far was in the old one).
        public static void Forget() { trail.Clear(); proxies.Clear(); }

        // Every frame in game: where our character (its feet and its centre of mass) was, by our clock.
        public static void Record()
        {
            var me = Game.LocalPlayer;
            if (me == null) { trail.Clear(); return; }
            float now = Time.realtimeSinceStartup;
            var center = me.CenterOfMass != null ? me.CenterOfMass.position : me.transform.position;
            trail.Add(new Sample { T = now, Pos = me.transform.position, Center = center });
            int old = 0;
            while (old < trail.Count - 1 && trail[old].T < now - Keep) old++;
            if (old > 0) trail.RemoveRange(0, old);
        }

        public static bool At(float t, out Vector3 pos, out Vector3 center)
        {
            pos = center = Vector3.zero;
            if (trail.Count == 0) return false;
            int i = trail.Count - 1;
            while (i > 0 && trail[i].T > t) i--;
            var a = trail[i];
            if (i == trail.Count - 1 || t <= a.T) { pos = a.Pos; center = a.Center; return true; }
            var b = trail[i + 1];
            float k = Mathf.InverseLerp(a.T, b.T, t);
            pos = Vector3.Lerp(a.Pos, b.Pos, k);
            center = Vector3.Lerp(a.Center, b.Center, k);
            return true;
        }

        // The stand-in for this enemy's replayed attack, aiming at us as we were at `clock` when it started.
        public static AimProxy For(Enemy e, float clock)
        {
            if (!proxies.TryGetValue(e, out var p) || p == null)
            {
                if (proxies.Count > 64)
                    foreach (var dead in new List<Enemy>(proxies.Keys))
                        if (dead == null || proxies[dead] == null) proxies.Remove(dead);
                var go = new GameObject("MP_AimProxy");
                p = go.AddComponent<AimProxy>();
                p.Center = new GameObject("CenterOfMass").transform;
                p.Center.SetParent(go.transform, false);
                proxies[e] = p;
            }
            p.Clock = clock;
            p.Since = Time.realtimeSinceStartup;
            p.Place();
            return p;
        }
    }

    internal class AimProxy : MonoBehaviour, ICenterOfMass
    {
        public float Clock, Since;
        public Transform Center;
        public Transform CenterOfMass => Center != null ? Center : transform;
        // How far behind our character it aims (the runner's view of us).
        public float Lag => Since - Clock;

        public void Place()
        {
            if (!EnemyAim.At(Clock + (Time.realtimeSinceStartup - Since), out var pos, out var center)) return;
            transform.position = pos;
            if (Center != null) Center.position = center;
        }

        private void Update() => Place();
    }
}
