using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync.Objects
{
    // Gathering nodes (rocks, herbs, logs, trees and their stumps). The player who gathers runs the game's own code
    // (animation, hits, drops, job XP); everyone else gets the node's state (health, felled into a stump, depleted)
    // and shows it with the node's own effects, without drops or XP. Drops follow the loot mode (WorldDrops).
    //
    // The game resets scene nodes whenever their scene loads (only estate trees are saved), so we keep node states
    // only while someone is in the scene: a scene loaded with nobody in it starts with fresh nodes, as in single player.
    internal class GatheringSync : ObjectHandler
    {
        public const string KindName = "gather";
        public static GatheringSync Instance { get; private set; }
        public GatheringSync() { Instance = this; }

        public override string Kind => KindName;
        public int RemoteHits, RemoteDepleted;

        public override IEnumerable<Component> FindAll() =>
            UnityEngine.Object.FindObjectsByType<GatheringObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w)
        {
            var g = (GatheringObject)obj;
            var type = g.gatherType;
            int hp = g.currentHealth;
            bool active = g.isActive;
            // A felled tree becomes a stump after its falling animation: report where it ends up.
            if (g.isTree && type == GatheringObject.GatherType.Wood && hp <= 0)
            {
                type = GatheringObject.GatherType.Residual;
                hp = g.hitsToRemoveTreeResidual;
                active = true;
            }
            w.Put((byte)type);
            w.Put(hp);
            w.Put(active);
            w.Put(g.activeVariant);
        }

        public override void Apply(Component obj, NetDataReader r)
        {
            var g = (GatheringObject)obj;
            var type = (GatheringObject.GatherType)r.GetByte();
            int hp = r.GetInt();
            bool active = r.GetBool();
            int variant = r.GetInt();
            g.InitalSetup();   // no-op once started; otherwise Start would later reset what we set
            bool live = g.gameObject.activeInHierarchy && Time.timeSinceLevelLoad > 2f;
            if (g.isTree && variant >= 0 && variant < g.variations.Count && variant != g.activeVariant)
            {
                g.activeVariant = variant;
                if (g.treeRenderer != null) g.treeRenderer.sprite = g.variations[variant];
            }
            // Only ever forward (two players on one node, messages crossing): stump beats standing, less health beats more.
            int phase = Phase(type), localPhase = Phase(g.gatherType);
            if (phase < localPhase || (phase == localPhase && hp >= g.currentHealth)) return;
            RemoteHits++;
            if (live) HitEffects(g);
            if (phase > localPhase)
            {
                // Felled by someone else: the tree goes, the stump stays and can be gathered.
                Finish(g, g.gatherType, live);
                g.gatherType = type;
                g.currentHealth = hp;
                g.isActive = active;
                if (g.interactionDisplay != null) g.interactionDisplay.enabled = active;
                return;
            }
            g.currentHealth = hp;
            if (hp <= 0)
            {
                RemoteDepleted++;
                Finish(g, g.gatherType, live);
            }
        }

        private static int Phase(GatheringObject.GatherType t) => t == GatheringObject.GatherType.Residual ? 1 : 0;

        private static void HitEffects(GatheringObject g)
        {
            bool shake = g.doCameraShake;
            g.doCameraShake = false;   // the camera shakes for the one who hits
            try { g.HitEffects(); }
            finally { g.doCameraShake = shake; }
            g.sfxHit?.Post(g.gameObject);
        }

        // FinishObject's visuals, without its drops and XP (those are the gatherer's).
        private static void Finish(GatheringObject g, GatheringObject.GatherType type, bool live)
        {
            g.isActive = false;
            if (g.interactionDisplay != null) g.interactionDisplay.enabled = false;
            if (g.contextButton != null) g.contextButton.SetActive(false);
            foreach (var a in g.artToHide)
                if (a != null) a.SetActive(false);
            if (live)
            {
                if (g.vfxDestroy != null) g.vfxDestroy.Play();
                g.sfxDestroy?.Post(g.gameObject);
            }
            if (g.artResidual != null) g.artResidual.SetActive(type != GatheringObject.GatherType.Residual);
            if (type == GatheringObject.GatherType.Residual) g.OnRemoveResidualsEvent?.Invoke();
        }

        // ---------------- forgetting nodes of scenes nobody is in (called every frame) ----------------

        private static int lastSceneHandle = -1;
        private static float nextPrune;

        public static void Prune()
        {
            var os = ObjectSync.Instance;
            if (os == null || !ObjectSync.Active) return;
            int handle = SceneManager.GetActiveScene().handle;
            if (Game.InGame && handle != lastSceneHandle)
            {
                lastSceneHandle = handle;
                // We loaded this scene and nobody else is in it: its nodes are fresh.
                if (!SceneAuthority.OthersInMyScene()) os.Forget(Game.SceneName, KindName);
            }
            if (Time.realtimeSinceStartup < nextPrune) return;
            nextPrune = Time.realtimeSinceStartup + 1f;
            foreach (var scene in os.KnownScenes.ToList())
                if (SceneAuthority.For(scene) == 0) os.Forget(scene, KindName);
        }
    }

    // Our player's gathering hit (the animation's hit event; only the local player's).
    [HarmonyPatch(typeof(GatheringObject), nameof(GatheringObject.OnHitCallback))]
    internal static class GatherHitPatch
    {
        private static bool Prefix(GatheringObject __instance)
        {
            if (!ObjectSync.Active) return true;
            // Someone else finished it while our swing was on its way: no second final drop or XP.
            if (!__instance.isActive || __instance.currentHealth <= 0) return false;
            ObjectSync.LocalAction = true;
            return true;
        }

        private static void Postfix(GatheringObject __instance, bool __runOriginal)
        {
            if (!__runOriginal || !ObjectSync.Active) return;
            ObjectSync.Instance?.Changed(GatheringSync.Instance, __instance);
        }

        private static Exception Finalizer(Exception __exception)
        {
            ObjectSync.LocalAction = false;
            return __exception;
        }
    }
}
