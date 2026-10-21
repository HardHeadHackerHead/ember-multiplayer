using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync
{
    // Scene runner, with NetStats.Trim: an enemy goes into the 15 Hz EnemyStates batches when something the puppets
    // use changed (position, flip, facing, health, animation state or speed, float parameters; not the time inside a
    // looping animation), for a few ticks after that (a lost batch is covered), and twice a second anyway (newcomers,
    // freshness). A sleeping or idle enemy costs ~2 entries a second instead of 15. Same layout as before.
    internal class EnemyStateTrim
    {
        public const float Refresh = 0.5f, Linger = 0.2f;

        private readonly Dictionary<int, (int sig, float changed, float sent)> last = new Dictionary<int, (int, float, float)>();
        private int scene = -1;
        private static readonly Dictionary<RuntimeAnimatorController, int[]> floatParams = new Dictionary<RuntimeAnimatorController, int[]>();

        public bool Due(int id, Enemy e, float now)
        {
            if (!NetStats.Trim) return true;
            int handle = SceneManager.GetActiveScene().handle;
            if (handle != scene) { scene = handle; last.Clear(); }
            int sig = Signature(e);
            if (last.TryGetValue(id, out var l))
            {
                if (sig != l.sig) l.changed = now;
                else if (now - l.changed >= Linger && now - l.sent < Refresh) return false;
            }
            else l.changed = now;
            last[id] = (sig, l.changed, now);
            return true;
        }

        private static int Signature(Enemy e)
        {
            unchecked
            {
                var p = e.transform.position;
                int h = Mathf.RoundToInt(p.x * 200f);
                h = h * 31 + Mathf.RoundToInt(p.y * 200f);
                h = h * 31 + (e.IsFlipped ? 1 : 0) + (e.IsDead ? 2 : 0) + ((int)e.FacingDirection << 2);
                h = h * 31 + e.CurrentHealth.GetHashCode();
                var anim = e.Animator;
                if (anim == null) return h;
                h = h * 31 + anim.speed.GetHashCode();
                if (!anim.isActiveAndEnabled) return h;
                for (int i = 0; i < anim.layerCount; i++)
                {
                    var info = anim.IsInTransition(i) ? anim.GetNextAnimatorStateInfo(i) : anim.GetCurrentAnimatorStateInfo(i);
                    h = h * 31 + info.fullPathHash;
                }
                foreach (int f in FloatParams(anim)) h = h * 31 + anim.GetFloat(f).GetHashCode();
                return h;
            }
        }

        private static int[] FloatParams(Animator anim)
        {
            var ctrl = anim.runtimeAnimatorController;
            if (ctrl == null) return new int[0];
            if (!floatParams.TryGetValue(ctrl, out var list))
                floatParams[ctrl] = list = anim.parameters.Where(p => p.type == AnimatorControllerParameterType.Float).Select(p => p.nameHash).ToArray();
            return list;
        }
    }
}
