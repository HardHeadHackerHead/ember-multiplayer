using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace EmbervilleMP.Sync
{
    // Copies the local player's GameObject without running any of its scripts, then strips everything
    // that isn't drawing: game logic, physics, audio, UI, triggers. The night light stays (its colour follows ours:
    // RemotePlayer.MirrorNightLight); sounds play from an emitter that follows the puppet (TeamSounds).
    internal static class PuppetBuilder
    {
        // Children of Player_Base that are gameplay, not looks.
        private static readonly string[] DropChildren =
        {
            "BodyCollider", "CenterTrigger", "DamageableCollider", "FarmingTriggers", "SpawnPoints",
            "AnimTrappedLevel", "TouchDamage", "PlayerPortrait", "Fishing", "MinimapCenter", "SchorchMarkSpawn",
            "GoreMarkSpawn", "OverHeadTextbox", "Graphics/AttackHitbox", "Graphics/Events",
        };

        public static GameObject Build(PlayerMaster template, string name, out Animator animator, out Transform[] flipPivots)
        {
            // Instantiating under an inactive parent means no Awake/OnEnable runs on the copy.
            var holder = new GameObject("MP_PuppetHolder");
            holder.SetActive(false);
            var copy = UnityEngine.Object.Instantiate(template.gameObject, holder.transform);
            copy.name = name;
            copy.tag = "Untagged"; // the game finds its player by the "Player" tag
            foreach (var t in copy.GetComponentsInChildren<Transform>(true))
                if (t.CompareTag("Player")) t.tag = "Untagged";

            foreach (string path in DropChildren)
            {
                var t = copy.transform.Find(path);
                if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);
            }
            StripComponents(copy);

            // Map the rotating pivots (facing left/right) from the template onto the copy.
            var pivots = new List<Transform>();
            var sb = new System.Text.StringBuilder();
            foreach (var p in template.charPivotFlip ?? new List<Transform>())
            {
                if (p == null) continue;
                string path = PlayerLook.PathOf(p, template.transform, sb);
                var found = path.Length == 0 ? copy.transform : copy.transform.Find(path);
                if (found != null) pivots.Add(found);
            }
            flipPivots = pivots.ToArray();

            animator = copy.GetComponent<Animator>();
            animator.fireEvents = false;      // its events call PlayerMaster methods, which the copy doesn't have
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.applyRootMotion = false;
            foreach (var extra in copy.GetComponentsInChildren<Animator>(true))
                if (extra != animator) extra.fireEvents = false;

            copy.transform.SetParent(null, true);
            UnityEngine.Object.Destroy(holder);
            copy.SetActive(true);
            return copy;
        }

        private static void StripComponents(GameObject go)
        {
            var keep = NightLightOf(go.transform);
            // Several passes: some scripts [RequireComponent] others and must go first.
            for (int pass = 0; pass < 6; pass++)
            {
                var doomed = go.GetComponentsInChildren<Component>(true).Where(c => Doomed(c, keep)).ToList();
                if (doomed.Count == 0) return;
                foreach (var c in doomed.OrderBy(Priority))
                {
                    if (c == null) continue;
                    try { UnityEngine.Object.DestroyImmediate(c); }
                    catch (Exception) { /* a dependency still holds it; next pass */ }
                }
            }
        }

        public const string NightLightPath = "NightLight";

        public static Light2D NightLightOf(Transform playerRoot)
        {
            var t = playerRoot != null ? playerRoot.Find(NightLightPath) : null;
            return t != null ? t.GetComponent<Light2D>() : null;
        }

        // Keep only what draws the character (and its night light, without the script that tints it).
        private static bool Doomed(Component c, Light2D keep)
        {
            if (c == null) return false;
            if (c is Light2D) return c != keep;
            return !(c is Transform || c is SpriteRenderer || c is Animator || c is UnityEngine.Rendering.SortingGroup);
        }

        // Scripts before physics, so RequireComponent links resolve.
        private static int Priority(Component c) => c is MonoBehaviour ? 0 : c is Collider2D ? 1 : c is Rigidbody2D ? 3 : 2;
    }
}
