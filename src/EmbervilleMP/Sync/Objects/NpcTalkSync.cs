using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync.Objects
{
    // "Someone else is talking to this NPC": while a player is in a conversation they started (E on a DialogueTrigger),
    // the others can't start one with the same NPC. Conversations stay local (each player's own dialogue, choices and
    // their effects on the story go through the world sync). The lock only counts while the talker is in our scene,
    // so a player who left or dropped never blocks anyone.
    // NPCs themselves (Enemy-based, empty behaviour graph) move with the enemy sync: on a guest they're the runner's
    // puppets, but their DialogueTrigger and its trigger collider are untouched, so talking to them works there too.
    // While someone talks to an NPC, every copy shows it as the talker's does: turned to the talker (facePlayer: the
    // game turns it on the talker's copy, we do on the others, towards the talker's puppet) and its bubble on the
    // talking sprite. The runner's states don't turn it back meanwhile (EnemySync.HoldFacing). Afterwards it stays
    // turned, as in the game, and the bubble is again what our own player sees (the game shows it when we're near).
    internal class NpcTalkSync : ObjectHandler
    {
        public static NpcTalkSync Instance { get; private set; }
        public NpcTalkSync()
        {
            Instance = this;
            EnemySync.HoldFacing = e => Instance != null && Instance.Holds(e);
        }

        public override string Kind => "npctalk";
        public int Blocked, Shown, Turned, Restored;

        private readonly Dictionary<DialogueTrigger, byte> talker = new Dictionary<DialogueTrigger, byte>();
        private readonly HashSet<DialogueTrigger> mine = new HashSet<DialogueTrigger>();
        // Someone else's conversations shown here, and their bubbles (held on the talking sprite).
        private readonly HashSet<DialogueTrigger> shown = new HashSet<DialogueTrigger>();
        private readonly Dictionary<NPCTalkBubble, DialogueTrigger> held = new Dictionary<NPCTalkBubble, DialogueTrigger>();
        private bool showing;

        public override IEnumerable<Component> FindAll() =>
            Object.FindObjectsByType<DialogueTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        public override void Capture(Component obj, NetDataWriter w) =>
            w.Put(talker.TryGetValue((DialogueTrigger)obj, out var t) ? t : (byte)0);

        public override void Apply(Component obj, NetDataReader r) => talker[(DialogueTrigger)obj] = r.GetByte();

        // Another player (here, in this scene) is talking to it.
        public byte BusyWith(DialogueTrigger d)
        {
            if (!talker.TryGetValue(d, out var t) || t == 0 || t == Requests.Me) return 0;
            var presence = MPRunner.Instance?.Presence;
            if (presence == null || !presence.Remotes.TryGetValue(t, out var rp)) return 0;
            return rp.Latest != null && rp.Latest.Scene == Game.SceneName && rp.SecondsSinceState < SceneAuthority.PresenceTimeout ? t : (byte)0;
        }

        public void OnStarted(DialogueTrigger d)
        {
            mine.Add(d);
            Set(d, Requests.Me);
        }

        public void OnEnded(DialogueTrigger d)
        {
            if (mine.Remove(d)) Set(d, 0);
        }

        private void Set(DialogueTrigger d, byte who)
        {
            talker[d] = who;
            ObjectSync.LocalAction = true;
            try { ObjectSync.Instance.Changed(this, d); }
            finally { ObjectSync.LocalAction = false; }
        }

        public string Name(byte id) =>
            Net.Session.Current != null && Net.Session.Current.Players.TryGetValue(id, out var p) ? p.Name : "Someone";

        // Who talks to it, here: us, someone in our scene, or nobody (0).
        public byte TalkingWith(DialogueTrigger d) => d == null ? (byte)0 : mine.Contains(d) ? Requests.Me : BusyWith(d);

        // An NPC that turns to whoever talks to it, in a conversation now.
        public bool Holds(Enemy e)
        {
            if (e == null || mine.Count == 0 && shown.Count == 0) return false;
            return e.TryGetComponent<DialogueTrigger>(out var d) && d.facePlayer && (mine.Contains(d) || shown.Contains(d));
        }

        // The bubble shows the talking sprite while someone else talks (the interaction prompt's show / hide wait).
        public bool HeldBubble(NPCTalkBubble b) => !showing && held.Count > 0 && b != null && held.ContainsKey(b);

        public override void Update()
        {
            if (talker.Count == 0 && shown.Count == 0) return;
            List<DialogueTrigger> start = null, stop = null;
            foreach (var kv in talker)
                if (kv.Key != null && kv.Value != 0 && !shown.Contains(kv.Key) && !mine.Contains(kv.Key) && BusyWith(kv.Key) != 0)
                    (start ??= new List<DialogueTrigger>()).Add(kv.Key);
            foreach (var d in shown)
                if (d == null || BusyWith(d) == 0 || mine.Contains(d)) (stop ??= new List<DialogueTrigger>()).Add(d);
            if (stop != null) foreach (var d in stop) Restore(d);
            if (start != null) foreach (var d in start) Show(d, talker[d]);
        }

        private void Show(DialogueTrigger d, byte who)
        {
            shown.Add(d);
            Shown++;
            try
            {
                if (d.facePlayer && d.TryGetComponent<Enemy>(out var e) && TalkerPosition(who) is Vector3 at)
                {
                    // (what the game's OnInteract does on the talker's copy)
                    e.CurrentDirection = (at - d.transform.position).normalized;
                    Turned++;
                }
                if (d.selectArrow != null)
                {
                    held[d.selectArrow] = d;
                    SetBubble(d.selectArrow, true, false);
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[npc] Showing {d.name} talking: {ex.Message}"); }
        }

        private void Restore(DialogueTrigger d)
        {
            shown.Remove(d);
            if (d == null) { held.Keys.Where(b => b == null).ToList().ForEach(b => held.Remove(b)); return; }
            Restored++;
            if (d.selectArrow == null) return;
            held.Remove(d.selectArrow);
            // Hidden: the game's own prompt brings it back (normal) when our player is the closest to it.
            try { SetBubble(d.selectArrow, false, true); }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"[npc] Bubble of {d.name}: {ex.Message}"); }
        }

        private void SetBubble(NPCTalkBubble b, bool on, bool normal)
        {
            showing = true;
            try { b.Show(on, normal); }
            finally { showing = false; }
        }

        private static Vector3? TalkerPosition(byte who)
        {
            if (who == Requests.Me) return Game.LocalPlayer != null ? Game.LocalPlayer.transform.position : (Vector3?)null;
            var presence = MPRunner.Instance?.Presence;
            if (presence == null || !presence.Remotes.TryGetValue(who, out var rp)) return null;
            if (rp.Puppet != null) return rp.Puppet.transform.position;
            return rp.Latest != null ? (Vector3)rp.Latest.Position : (Vector3?)null;
        }
    }

    [HarmonyPatch(typeof(NPCTalkBubble), nameof(NPCTalkBubble.Show))]
    internal static class NpcBubblePatch
    {
        private static void Prefix(NPCTalkBubble __instance, ref bool isShowing, ref bool isNormal)
        {
            if (NpcTalkSync.Instance == null || !NpcTalkSync.Instance.HeldBubble(__instance)) return;
            isShowing = true;
            isNormal = false;
        }
    }

    [HarmonyPatch(typeof(DialogueTrigger), nameof(DialogueTrigger.OnInteract))]
    internal static class NpcInteractPatch
    {
        private static bool Prefix(DialogueTrigger __instance, out bool __state)
        {
            __state = __instance.isOpened;
            if (!ObjectSync.Active || NpcTalkSync.Instance == null) return true;
            byte busy = NpcTalkSync.Instance.BusyWith(__instance);
            if (busy == 0) return true;
            NpcTalkSync.Instance.Blocked++;
            try { PlayerTextReaction.ShowText($"{NpcTalkSync.Instance.Name(busy)} is talking to them."); } catch { }
            return false;
        }

        private static void Postfix(DialogueTrigger __instance, bool __state)
        {
            if (!__state && __instance.isOpened && ObjectSync.Active) NpcTalkSync.Instance?.OnStarted(__instance);
        }
    }

    [HarmonyPatch(typeof(DialogueTrigger), "OnEnded")]
    internal static class NpcEndedPatch
    {
        private static void Postfix(DialogueTrigger __instance)
        {
            if (ObjectSync.Active) NpcTalkSync.Instance?.OnEnded(__instance);
        }
    }

    // The interaction prompt doesn't come up while someone else talks to them.
    [HarmonyPatch(typeof(DialogueTrigger), nameof(DialogueTrigger.CanInteract))]
    internal static class NpcCanInteractPatch
    {
        private static void Postfix(DialogueTrigger __instance, ref bool __result)
        {
            if (__result && ObjectSync.Active && NpcTalkSync.Instance != null && NpcTalkSync.Instance.BusyWith(__instance) != 0) __result = false;
        }
    }
}
