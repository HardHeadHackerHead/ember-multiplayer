using System.Collections.Generic;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // The phases of the game's fishing (FishingManager.FishingAction): cast and wait (FishStart), the bite and reel
    // (FishEnd), the catch (the extra-effects animator plays FishRare, moved one unit towards the water).
    internal enum FishPhase : byte { None = 0, Cast = 1, Reel = 2, Catch = 3 }

    // Others see you fish. The rod, line and pose are the player animator's states and the catch flash is the
    // extra-effects animator's: both already reach puppets through PlayerState. What doesn't is the one thing the game
    // does outside an animator: it moves the effects animator towards the water for the catch (and back after).
    // This sends the phase and the direction, and puppets mirror that offset.
    //
    // Deliberately not synced: fishing spots (FishingTrigger has no state: no depletion, no cooldown, no owner, so
    // there is nothing to share through ObjectSync), catches (the game only adds Fishing XP to the fisher's own
    // character, no item or world drop, so loot rules don't apply), sounds, and the "fish here" indicator (the
    // player's "Fishing" child: a local prompt, PuppetBuilder strips it). Fishing has no minigame or UI and never
    // touches Time.timeScale.
    internal class FishingSync
    {
        public static FishingSync Instance { get; private set; }

        public int PhasesSent, PhasesReceived, OffsetsApplied;
        public FishPhase LocalPhase { get; private set; }
        public Direction LocalDirection { get; private set; }

        private class RemoteFishing
        {
            public FishPhase Phase;
            public Direction Dir;
            public readonly List<FishPhase> History = new List<FishPhase>();
            public GameObject AppliedTo;
            public FishPhase AppliedPhase;
        }

        private readonly Session session;
        private readonly Presence presence;
        private readonly Dictionary<byte, RemoteFishing> remotes = new Dictionary<byte, RemoteFishing>();
        private float localStart = -1f;     // game time (scaled, as the game's own delays) when we started fishing
        private float nextResend;
        private string fxPath;

        public FishingSync(Session session, Presence presence)
        {
            Instance = this;
            this.session = session;
            this.presence = presence;
            session.On(Msg.Fishing, OnFishing);
            session.PlayerLeft += p => remotes.Remove(p.Id);
        }

        // From the StartFishing hook: the local player started fishing, facing dir.
        public void OnLocalStart(Direction dir)
        {
            if (Session.Current != session) return;
            localStart = Time.time;
            LocalDirection = dir;
            SetLocal(FishPhase.Cast);
        }

        public void Update()
        {
            if (Session.Current != session || MPRunner.Instance.Presence != presence) return;   // ended, or detached
            SetLocal(ReadLocalPhase());
            // Repeat while fishing, for players who arrive (or rebuild our puppet) mid-cast.
            if (LocalPhase != FishPhase.None && Time.realtimeSinceStartup >= nextResend) SendLocal();
            ApplyRemotes();
        }

        private FishPhase ReadLocalPhase()
        {
            var me = Game.LocalPlayer;
            if (me == null || !Singleton<FishingManager>.HasMain) return FishPhase.None;   // never .main: it creates one
            var fm = Singleton<FishingManager>.main;
            if (!fm.isInteracting || fm.player != me) return FishPhase.None;
            if (localStart < 0f) { localStart = Time.time; LocalDirection = me.direction; }   // started without our hook
            var fx = me.extraFxAnimator;
            if (fx != null && fx.transform.localPosition.sqrMagnitude > 0.01f) return FishPhase.Catch;
            return Time.time - localStart >= fm.idleDuration ? FishPhase.Reel : FishPhase.Cast;
        }

        private void SetLocal(FishPhase phase)
        {
            if (phase == LocalPhase) return;
            if (phase == FishPhase.None) localStart = -1f;
            Plugin.Log.LogInfo($"[fish] local {LocalPhase} -> {phase} ({LocalDirection})");
            LocalPhase = phase;
            SendLocal();
        }

        private void SendLocal()
        {
            nextResend = Time.realtimeSinceStartup + 1f;
            session.Send(Msg.Fishing, w => { w.Put((byte)LocalPhase); w.Put((byte)LocalDirection); });
            PhasesSent++;
        }

        private void OnFishing(byte from, NetDataReader r)
        {
            var phase = (FishPhase)r.GetByte();
            var dir = (Direction)r.GetByte();
            if (from == session.LocalId) return;
            PhasesReceived++;
            if (!remotes.TryGetValue(from, out var f)) remotes[from] = f = new RemoteFishing();
            if (f.Phase == phase && f.Dir == dir) return;   // a repeat
            f.Phase = phase;
            f.Dir = dir;
            f.History.Add(phase);
            if (f.History.Count > 32) f.History.RemoveAt(0);
            Plugin.Log.LogInfo($"[fish] player {from}: {phase} ({dir})");
        }

        private void ApplyRemotes()
        {
            foreach (var kv in remotes)
            {
                var f = kv.Value;
                if (!presence.Remotes.TryGetValue(kv.Key, out var rp) || rp.Puppet == null) continue;
                if (f.AppliedTo == rp.Puppet && f.AppliedPhase == f.Phase) continue;
                var fx = PuppetFx(rp.Puppet);
                if (fx == null) continue;
                // A new puppet copies our own rig, offset included if we're mid-catch: always set it.
                fx.localPosition = f.Phase == FishPhase.Catch ? f.Dir.ToVector() : Vector3.zero;
                f.AppliedTo = rp.Puppet;
                f.AppliedPhase = f.Phase;
                OffsetsApplied++;
            }
        }

        // The puppet's copy of the player's extra-effects animator (same path as on our own rig).
        private Transform PuppetFx(GameObject puppet)
        {
            if (fxPath == null)
            {
                var me = Game.LocalPlayer;
                if (me == null || me.extraFxAnimator == null) return puppet.transform.Find("Player_ExtraFxAnimations");
                fxPath = PlayerLook.PathOf(me.extraFxAnimator.transform, me.transform, new System.Text.StringBuilder());
            }
            return fxPath.Length == 0 ? null : puppet.transform.Find(fxPath);
        }

        // ---- for the self-test ----

        public FishPhase PhaseOf(byte player) => remotes.TryGetValue(player, out var f) ? f.Phase : FishPhase.None;
        public List<FishPhase> HistoryOf(byte player) => remotes.TryGetValue(player, out var f) ? f.History : new List<FishPhase>();

        public Vector3? PuppetFxOffset(byte player)
        {
            if (!presence.Remotes.TryGetValue(player, out var rp) || rp.Puppet == null) return null;
            return PuppetFx(rp.Puppet)?.localPosition;
        }
    }

    [HarmonyPatch(typeof(FishingManager), nameof(FishingManager.StartFishing))]
    internal static class FishingStartPatch
    {
        private static void Prefix(FishingManager __instance, out bool __state) => __state = __instance.isInteracting;

        private static void Postfix(FishingManager __instance, Direction directionToLook, bool __state)
        {
            // Only a fresh start (the game ignores StartFishing while already fishing), by our own player.
            if (__state || !__instance.isInteracting || __instance.player == null || __instance.player != Game.LocalPlayer) return;
            FishingSync.Instance?.OnLocalStart(directionToLook);
        }
    }
}
