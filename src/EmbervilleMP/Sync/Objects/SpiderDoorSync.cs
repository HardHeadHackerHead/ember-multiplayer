using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EmbervilleMP.Sync.Objects
{
    // The spider nest's door (SpiderDoorCutscene, TutorialArea's 10_Spidernest): the static door swaps for the
    // animated one, a sound, the camera shakes and the player is held for 3 s. The game finds "the" player with
    // FindAnyObjectByType, so wherever it runs it holds that copy's player, wherever they are (and when the door hangs
    // off an item lock, ItemRequesterSync runs the lock's unlock events on every copy). In co-op it plays like a room
    // cutscene (CutsceneSync's rule): whoever opens it and everyone in its room see and feel it; everyone else gets the
    // door as it ends (opened: no hold, no shake, no sound), and so does someone arriving in the scene later.
    internal class SpiderDoorSync : ObjectHandler
    {
        public static SpiderDoorSync Instance { get; private set; }
        public SpiderDoorSync() { Instance = this; }

        public override string Kind => "spiderdoor";
        public override bool Persistent => false;   // like the item lock it hangs off (ItemRequesterSync)

        public int Plays, EndStates, Blocked;
        public readonly HashSet<SpiderDoorCutscene> Opened = new HashSet<SpiderDoorCutscene>();
        private int sceneHandle = -1;
        private float sceneSince;

        public override IEnumerable<Component> FindAll()
        {
            EnsureScene();
            return Object.FindObjectsByType<SpiderDoorCutscene>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        private void EnsureScene()
        {
            int h = SceneManager.GetActiveScene().handle;
            if (h == sceneHandle) return;
            sceneHandle = h;
            sceneSince = Time.realtimeSinceStartup;
            Opened.Clear();
        }

        public override void Capture(Component obj, NetDataWriter w) => w.Put(Opened.Contains((SpiderDoorCutscene)obj));

        public override void Apply(Component obj, NetDataReader r)
        {
            var door = (SpiderDoorCutscene)obj;
            if (r.GetBool() && !Opened.Contains(door)) door.Play();   // through OnPlay below: watch it or get its end
        }

        private static string AreaOf(Component c) => c.GetComponentInParent<GameArea>(true)?.name ?? "";

        // SpiderDoorCutscene.Play is about to run here. false: it doesn't (already open here, or the end state instead).
        public bool OnPlay(SpiderDoorCutscene door)
        {
            EnsureScene();
            if (!Opened.Add(door)) { Blocked++; return false; }
            bool ours = ObjectSync.LocalAction && !ObjectSync.Applying;
            // Just arrived (a known state applied on load): nothing to watch.
            bool here = Game.LocalPlayer != null && Time.realtimeSinceStartup - sceneSince > 3f && Game.AreaName == AreaOf(door);
            if (!ObjectSync.Applying) Interactables.Changed(this, door);
            if (ours || here)
            {
                Plays++;
                Plugin.Log.LogInfo($"[script] Spider door opens {(ours ? "(ours)" : "in our room")}: playing it");
                return true;
            }
            EndStates++;
            Plugin.Log.LogInfo($"[script] Spider door opened while we're in {Game.AreaName}: end state only");
            if (door.doorStatic != null) door.doorStatic.SetActive(false);
            if (door.doorAnimator != null) door.doorAnimator.SetActive(true);
            return false;
        }
    }

    [HarmonyPatch(typeof(SpiderDoorCutscene), nameof(SpiderDoorCutscene.Play))]
    internal static class SpiderDoorPlayPatch
    {
        private static bool Prefix(SpiderDoorCutscene __instance) =>
            !ObjectSync.Active || SpiderDoorSync.Instance == null || SpiderDoorSync.Instance.OnPlay(__instance);
    }
}
