using System.Collections;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;
using UnityEngine.Events;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // The guest gets effects on its own character (burning, a shield, the Matriarch's web, through the game's
        // own EffectsHandler); the host sees them on the guest's puppet, and sees them go.
        [Scenario("statusfx", Timeout = 200)]
        private static IEnumerator StatusFxTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) { yield return Finish(""); yield break; }
            var me = Game.LocalPlayer;
            var fx = StatusFx.Instance;
            string tag = s.IsHost ? "host" : "guest";
            Check("status effect sync is running", fx != null);
            LogEffectWiring(me);
            // Side by side at the mushroom cavern's entrance (no enemy reaches it in the time this takes).
            yield return TeleportTo(CombatArea, s.IsHost ? new Vector2(51.8f, 76.3f) : new Vector2(53.4f, 76.3f));
            yield return Barrier("placed", 40f);

            RemotePlayer guest = null;
            if (s.IsHost)
            {
                yield return WaitFor(() => (guest = MPRunner.Instance.Presence.Remotes.Values.FirstOrDefault(r => r.Shown)) != null, 20f);
                Check("guest's puppet is shown", guest != null);
            }
            byte gid = guest != null ? guest.Info.Id : (byte)0;
            System.Func<EffectType, bool> has = t => fx.EffectsOf(gid).Any(e => e.Type == t);

            // 1. Burning: the status loop on the puppet's extra FX animator.
            int burnHash = StatusFx.StatusStateHash("Burn");
            if (!s.IsHost)
            {
                bool ok = me.EffectsHandler.HandleEffect(new DamageEffect { Effect = EffectType.Burn, DamagePerSec = 1, Duration = 30f, HideUI = true });
                Check("guest is burning", ok && me.EffectsHandler.HasEffect(EffectType.Burn));
            }
            yield return Barrier("burn-on");
            if (s.IsHost)
            {
                yield return WaitFor(() => has(EffectType.Burn) && FxStateIs(guest, burnHash), 5f);
                var burn = fx.EffectsOf(gid).FirstOrDefault(e => e.Type == EffectType.Burn);
                Check("guest's burn reached the host", has(EffectType.Burn), Effects(fx, gid));
                float left = burn.EndsAt - Time.realtimeSinceStartup;
                Check("with its time left", has(EffectType.Burn) && left > 20f && left <= 30.5f, $"{left:F1}s");
                Check("guest's puppet burns (Status/Burn on its FX animator)", FxStateIs(guest, burnHash), FxStateName(guest));
            }
            yield return DevHarness.Screenshot("statusfx-burn-" + tag);
            yield return Barrier("burn-seen");
            if (!s.IsHost)
            {
                me.EffectsHandler.RemoveEffect(EffectType.Burn);
                yield return WaitFor(() => !me.EffectsHandler.HasEffect(EffectType.Burn), 3f);
                me.ResetToFullHealth();
            }
            yield return Barrier("burn-off");
            if (s.IsHost)
            {
                yield return WaitFor(() => !has(EffectType.Burn) && !FxStateIs(guest, burnHash), 5f);
                Check("burn gone from the host's list", !has(EffectType.Burn), Effects(fx, gid));
                Check("guest's puppet stopped burning", !FxStateIs(guest, burnHash), FxStateName(guest));
            }
            yield return Barrier("burn-cleared");

            // 2. A shield buff: stacks, no duration.
            if (!s.IsHost)
            {
                bool ok = me.EffectsHandler.HandleEffect(new DamageEffect { Effect = EffectType.Shield, DamagePerSec = 3, HideUI = true });
                Check("guest has a shield", ok && me.EffectsHandler.HasEffect(EffectType.Shield));
            }
            yield return Barrier("shield-on");
            if (s.IsHost)
            {
                yield return WaitFor(() => has(EffectType.Shield), 5f);
                var sh = fx.EffectsOf(gid).FirstOrDefault(e => e.Type == EffectType.Shield);
                Check("guest's shield reached the host, 3 stacks, no time limit", has(EffectType.Shield) && sh.Stacks == 3 && sh.EndsAt < 0f, Effects(fx, gid));
            }
            yield return Barrier("shield-seen");
            if (!s.IsHost)
            {
                me.EffectsHandler.RemoveEffect(EffectType.Shield);
                yield return WaitFor(() => !me.EffectsHandler.HasEffect(EffectType.Shield), 3f);
            }
            yield return Barrier("shield-off");
            if (s.IsHost)
            {
                yield return WaitFor(() => !has(EffectType.Shield), 5f);
                Check("shield gone from the host's list", !has(EffectType.Shield), Effects(fx, gid));
            }
            yield return Barrier("shield-cleared");

            // 3. The Matriarch's web: the cocoon, its stages, and a rebuilt puppet (scene re-entry) shows it again.
            if (!s.IsHost)
            {
                bool ok = me.EffectsHandler.HandleEffect(new DamageEffect { Effect = EffectType.Trapped, HideUI = true });
                yield return null;
                Check("guest is webbed", ok && me.EffectsHandler.HasEffect(EffectType.Trapped) && me.currentState == me.stateTrapped,
                    $"state {me.currentState?.GetType().Name}, TrappedLevel {me.animator.GetFloat("TrappedLevel")}");
            }
            yield return Barrier("web-on");
            int burstsAtWeb = 0;
            if (s.IsHost)
            {
                yield return WaitFor(() => has(EffectType.Trapped) && CocoonShown(guest) && Mathf.Abs(TrappedLevel(guest) - 3f) < 0.01f && fx.CocoonVfxOf(gid) != null, 5f);
                Check("guest's web reached the host", has(EffectType.Trapped), Effects(fx, gid));
                Check("guest's puppet is in the cocoon", CocoonShown(guest));
                Check("puppet's TrappedLevel is 3 (whole cocoon)", Mathf.Abs(TrappedLevel(guest) - 3f) < 0.01f, TrappedLevel(guest).ToString("F2"));
                var vfx = fx.CocoonVfxOf(gid);
                Check("cocoon particles burst on the puppet", vfx != null && fx.Bursts > 0 && vfx.transform.IsChildOf(guest.Puppet.transform), $"{fx.Bursts} bursts");
                burstsAtWeb = fx.Bursts;
            }
            yield return DevHarness.Screenshot("statusfx-webbed-" + tag, 0.3f);
            yield return Barrier("web-seen");

            if (!s.IsHost)
            {
                // What struggling left and right does, at 10 lives left.
                me.EffectsHandler.GetEffect(EffectType.Trapped)?.TriggerEvent("LittleDamage");
                Check("guest's cocoon weakens (TrappedLevel 2)", Mathf.Abs(me.animator.GetFloat("TrappedLevel") - 2f) < 0.01f, me.animator.GetFloat("TrappedLevel").ToString("F2"));
            }
            yield return Barrier("web-weaker");
            if (s.IsHost)
            {
                yield return WaitFor(() => Mathf.Abs(fx.TrappedLevelOf(gid) - 2f) < 0.01f && Mathf.Abs(TrappedLevel(guest) - 2f) < 0.01f && fx.Bursts > burstsAtWeb, 5f);
                Check("puppet's cocoon weakened too (TrappedLevel 2)", Mathf.Abs(TrappedLevel(guest) - 2f) < 0.01f, TrappedLevel(guest).ToString("F2"));
                Check("with a burst of web", fx.Bursts > burstsAtWeb, $"{burstsAtWeb} -> {fx.Bursts} bursts");

                var old = guest.Puppet;
                guest.Destroy();
                yield return WaitFor(() => guest.Puppet != null && guest.Puppet != old && guest.Shown && CocoonShown(guest) &&
                    fx.CocoonVfxOf(gid) != null && fx.CocoonVfxOf(gid).transform.IsChildOf(guest.Puppet.transform), 5f);
                Check("a rebuilt puppet (scene re-entry) is in the cocoon again, with its particles",
                    guest.Puppet != null && guest.Puppet != old && CocoonShown(guest) && fx.CocoonVfxOf(gid) != null && fx.CocoonVfxOf(gid).transform.IsChildOf(guest.Puppet.transform));
            }
            yield return Barrier("web-weaker-seen");

            if (!s.IsHost)
            {
                me.EffectsHandler.RemoveEffect(EffectType.Trapped);
                yield return WaitFor(() => !me.EffectsHandler.HasEffect(EffectType.Trapped) && me.currentState != me.stateTrapped, 3f);
                Check("guest broke free", !me.EffectsHandler.HasEffect(EffectType.Trapped) && me.currentState != me.stateTrapped);
            }
            yield return Barrier("web-off");
            if (s.IsHost)
            {
                yield return WaitFor(() => !has(EffectType.Trapped) && !CocoonShown(guest) && TrappedLevel(guest) < 0.01f, 5f);
                Check("web gone from the host's list", !has(EffectType.Trapped), Effects(fx, gid));
                Check("guest's puppet is out of the cocoon", !CocoonShown(guest));
                Check("puppet's TrappedLevel back to 0", TrappedLevel(guest) < 0.01f, TrappedLevel(guest).ToString("F2"));
                Check("host's own character untouched", !me.EffectsHandler.HasEffect(EffectType.Trapped) && !me.EffectsHandler.HasEffect(EffectType.Burn) && me.currentState != me.stateTrapped);
                Check("guest's effects arrived as messages", fx.ListsReceived >= 6, $"{fx.ListsReceived} lists");
            }
            else Check("guest sent its effects on each change", fx.ListsSent >= 6, $"{fx.ListsSent} lists");
            yield return DevHarness.Screenshot("statusfx-free-" + tag, 0.5f);
            me.ResetToFullHealth();
            yield return Barrier("done");
            yield return Finish("other players' status effects show on their puppets");
        }

        private static Animator PuppetFx(RemotePlayer rp)
        {
            if (rp == null || rp.Puppet == null) return null;
            var t = rp.Puppet.transform.Find("Player_ExtraFxAnimations");
            return t != null ? t.GetComponent<Animator>() : null;
        }

        private static bool FxStateIs(RemotePlayer rp, int hash)
        {
            var a = PuppetFx(rp);
            if (a == null || !a.isActiveAndEnabled) return false;
            return a.GetCurrentAnimatorStateInfo(0).fullPathHash == hash || (a.IsInTransition(0) && a.GetNextAnimatorStateInfo(0).fullPathHash == hash);
        }

        private static string FxStateName(RemotePlayer rp)
        {
            var a = PuppetFx(rp);
            if (a == null) return "no FX animator";
            int h = a.GetCurrentAnimatorStateInfo(0).fullPathHash;
            foreach (var n in new[] { "Bleed", "Burn", "Freeze", "Poison", "Shock" })
                if (StatusFx.StatusStateHash(n) == h) return "Status/" + n;
            return h == Animator.StringToHash("Base Layer.Empty") ? "Empty" : "state " + h;
        }

        private static bool CocoonShown(RemotePlayer rp)
        {
            if (rp == null || rp.Puppet == null) return false;
            var t = rp.Puppet.transform.Find("Graphics/Extras/Coccoon");
            return t != null && t.gameObject.activeInHierarchy;
        }

        private static float TrappedLevel(RemotePlayer rp)
        {
            var a = rp != null && rp.Puppet != null ? rp.PuppetAnimator : null;
            return a != null ? a.GetFloat("TrappedLevel") : -1f;
        }

        private static string Effects(StatusFx fx, byte id) =>
            string.Join(", ", fx.EffectsOf(id).Select(e => $"{e.Type} x{e.Stacks}")) is var s && s.Length > 0 ? s : "none";

        // What the game does on a real player when an effect starts/ends (in the log, for when a game update changes it).
        private static void LogEffectWiring(PlayerMaster me)
        {
            var h = me != null ? me.EffectsHandler : null;
            if (h == null || h.eventsReceiver == null) { Plugin.Log.LogInfo("[fx] no EffectsHandler wiring found"); return; }
            string Calls(UnityEventBase ev)
            {
                if (ev == null) return "";
                return string.Join("; ", Enumerable.Range(0, ev.GetPersistentEventCount()).Select(i =>
                {
                    var t = ev.GetPersistentTarget(i);
                    string who = t is Component c ? PlayerLook.PathOf(c.transform, me.transform) + "<" + c.GetType().Name + ">" : t is GameObject g ? PlayerLook.PathOf(g.transform, me.transform) : t != null ? t.name : "null";
                    return who + "." + ev.GetPersistentMethodName(i);
                }));
            }
            foreach (var r in h.eventsReceiver)
            {
                if (r == null) continue;
                Plugin.Log.LogInfo($"[fx] wiring {r.Effect}: enter [{Calls(r.OnEnterEffect)}] update [{Calls(r.OnUpdateEffect)}] exit [{Calls(r.OnExitEffect)}]" +
                    string.Concat((r.GenericEvents ?? new NamedEvent[0]).Select(n => $" {n.Name} [{Calls(n.Event)}]")));
            }
            Plugin.Log.LogInfo($"[fx] wiring setEffectType [{Calls(h.setEffectType)}] broadcast [{Calls(h.broadcastEffectEvent)}]");
        }
    }
}
