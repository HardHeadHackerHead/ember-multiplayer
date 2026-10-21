using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync
{
    // Teammates brush the world as our own character does: grass sways, critters run off, ghosts vanish. The game
    // reacts to the "PlayerCenter" trigger, which a puppet doesn't have (the same tag lets a player interact and pick
    // things up). So the puppet asks the physics engine what its center would touch (the same size and layers as the
    // real one) and calls those reactions itself: nothing else in the scene sees a new collider.
    internal class PuppetTouch : MonoBehaviour
    {
        public static int GrassSwings, FaunaMoves, GhostsVanished;
        public static GrassShaderComponent LastGrass;

        private Vector2 offset;
        private float radius;
        private ContactFilter2D filter;
        private float next;
        private HashSet<Collider2D> inside = new HashSet<Collider2D>(), now = new HashSet<Collider2D>();
        private readonly List<Collider2D> hits = new List<Collider2D>();

        // Ghosts listen through a TriggerForward that isn't always on their own object.
        private static readonly Dictionary<TriggerForward, Ghost> ghostOf = new Dictionary<TriggerForward, Ghost>();
        private static int ghostScene = -1;

        public static PuppetTouch Attach(GameObject puppet, PlayerMaster template)
        {
            var t = puppet.AddComponent<PuppetTouch>();
            var center = template.transform.Find("CenterTrigger")?.GetComponent<Collider2D>();
            int layer = center != null ? center.gameObject.layer : template.gameObject.layer;
            t.radius = 0.3f;
            if (center != null && center.enabled && center.bounds.extents.sqrMagnitude > 0.0001f)
            {
                var b = center.bounds;
                t.offset = (Vector2)(b.center - template.transform.position);
                t.radius = Mathf.Max(0.1f, Mathf.Min(b.extents.x, b.extents.y));
            }
            t.filter = new ContactFilter2D { useTriggers = true, useLayerMask = true, layerMask = Physics2D.GetLayerCollisionMask(layer) };
            return t;
        }

        private void OnDisable() => inside.Clear();

        private void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.05f;
            hits.Clear();
            Vector2 at = (Vector2)transform.position + offset;
            Physics2D.OverlapCircle(at, radius, filter, hits);
            now.Clear();
            foreach (var c in hits)
            {
                if (c == null || !now.Add(c) || inside.Contains(c)) continue;
                try { Touched(c, at); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"[touch] {c.name}: {e.Message}"); }
            }
            (inside, now) = (now, inside);
        }

        // c just came under the puppet's center: what our own center would set off there.
        private static void Touched(Collider2D c, Vector2 at)
        {
            var go = c.gameObject;
            var body = c.attachedRigidbody != null && c.attachedRigidbody.gameObject != go ? c.attachedRigidbody.gameObject : null;
            var grass = go.GetComponent<GrassShaderComponent>() ?? body?.GetComponent<GrassShaderComponent>();
            if (grass != null && grass.tagToCompare == "PlayerCenter") Swing(grass, at);
            var fauna = go.GetComponent<FaunaPath>() ?? (go.GetComponent<ChildTriggerHandler>()?._receiver as FaunaPath) ?? body?.GetComponent<FaunaPath>();
            if (fauna != null && !fauna.isMoving && fauna.path.Count > 0)
            {
                fauna.StartMove();
                FaunaMoves++;
            }
            var ghost = go.GetComponent<Ghost>() ?? GhostOf(go.GetComponent<TriggerForward>());
            if (ghost != null) Vanish(ghost);
        }

        // GrassShaderComponent.OnTriggerEnter2D, for a teammate.
        private static void Swing(GrassShaderComponent g, Vector2 from)
        {
            g.tokenSource?.Cancel();
            int direction = from.x > g.transform.position.x ? 1 : -1;
            g.tokenSource = new CancellationTokenSource();
            g.Swing(direction, g.tokenSource.Token).Forget();
            GrassSwings++;
            LastGrass = g;
        }

        // Ghost.OnTriggerEnter2D, for a teammate. Each copy sees the same teammates walk in, so a ghost scared off by
        // anyone is gone on every screen (none in the demo: its onVanishEvent runs here as on its own copy).
        private static void Vanish(Ghost g)
        {
            if (g.vanished) return;
            g.vanished = true;
            g.animator.SetTrigger("Vanish");
            g.sfxComponent.PlaySfx("Vanish");
            g.onVanishEvent.Invoke();
            GhostsVanished++;
        }

        private static Ghost GhostOf(TriggerForward tf)
        {
            if (tf == null) return null;
            int scene = SceneManager.GetActiveScene().handle;
            if (scene != ghostScene)
            {
                ghostScene = scene;
                ghostOf.Clear();
                foreach (var g in Object.FindObjectsByType<Ghost>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (g.triggerForward != null) ghostOf[g.triggerForward] = g;
            }
            return ghostOf.TryGetValue(tf, out var ghost) ? ghost : null;
        }
    }
}
