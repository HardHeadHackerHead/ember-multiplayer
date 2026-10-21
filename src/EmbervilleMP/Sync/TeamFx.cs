using System;
using System.Collections.Generic;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EmbervilleMP.Sync
{
    // What else a teammate's own game shows on their character, replayed on the puppet:
    //   - one-off effects the state can't carry (PlayerMoment): GameManager's teleport flash (PlayExtraVFXAsync
    //     plays it on a second, temporary effects animator) and the respawn flash (played while the character is
    //     hidden and between "in game" states: the puppet is off). Played here as the game does: a temporary copy of
    //     the effects animator, at the spot, with the same trigger. Death marks (the scorch / gore mark the game's
    //     ObjectSpawners leave where a player died) the same way.
    //   - floating text over the character (PlayerFloatText): damage taken, heals and potions, Blocked / Dodged /
    //     Counter / Critical, status names, "Health Full", and the floating health bar. The game raises them on the
    //     owner's machine only (Bus<UIDamageEvent>, Bus<UIFloatingBarEvent>); the owner knows the outcome (block,
    //     parry, the amount after its defences), so it sends what it showed and the others show it over the puppet.
    //     Nothing else shows numbers over a puppet (RemotePlayerTarget takes no damage), so nothing shows twice.
    //   - the animator's speed (PlayerAnimSpeed): attacks with a speed multiplier play faster/slower; sent on change.
    // Everything carries the sender's clock and plays when the puppet gets there (TeamSpace.DueAt).
    internal class TeamFx
    {
        public enum Moment : byte { ExtraFx = 0, ScorchMark = 1, GoreMark = 2 }
        private enum TextKind : byte { Damage = 0, Bar = 1 }
        private const byte BarHide = 1;
        private const int MaxTrigger = 32, MaxMessage = 48;
        // The owner's own notices, not something that happens to the character.
        private static readonly HashSet<string> PersonalMessages = new HashSet<string> { "Inventory Full", "Can't use it here", "No Kymia" };

        public static TeamFx Instance { get; private set; }

        // what the self-tests read (the logs only fill in test runs)
        public int MomentsSent, MomentsReceived, FxShown, MarksShown, TextsSent, BarsSent, TextsShown, BarsShown, SpeedsSent, SpeedsApplied;
        public int LastBarInUi = -1;   // the game's UI had a bar for the puppet right after the last one: 1, no: 0, no UI: -1
        public readonly List<string> MomentLog = new List<string>();                 // "from:kind:trigger" as received
        public readonly List<(byte from, string what, Vector2 at)> ShownLog = new List<(byte, string, Vector2)>();   // fx and marks shown
        public readonly List<(byte from, float amount, string message, DamageResult result, Vector3 at)> TextLog = new List<(byte, float, string, DamageResult, Vector3)>();

        private readonly Session session;
        private readonly Presence presence;
        private readonly List<(float due, byte from, Action act)> queue = new List<(float, byte, Action)>();
        private float lastSpeed = float.NaN;
        private PlayerMaster speedOf;
        private static bool hooked, replaying;

        public TeamFx(Session session, Presence presence)
        {
            Instance = this;
            this.session = session;
            this.presence = presence;
            session.On(Msg.PlayerMoment, OnMoment);
            session.On(Msg.PlayerFloatText, OnFloatText);
            session.On(Msg.PlayerAnimSpeed, OnAnimSpeed);
            session.PlayerJoined += _ => lastSpeed = float.NaN;   // the newcomer hears our speed at once
            session.Welcomed += () => lastSpeed = float.NaN;
            session.Ended += () => queue.Clear();
            if (!hooked)
            {
                hooked = true;
                Bus<UIDamageEvent>.Listeners += e => Instance?.OnLocalDamageText(e);
                Bus<UIFloatingBarEvent>.Listeners += e => Instance?.OnLocalBar(e);
            }
        }

        private bool Sending => Session.Current == session && session.Ready;

        public void Update()
        {
            if (Session.Current != session) return;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < queue.Count; i++)
            {
                var q = queue[i];
                if (q.due > now) continue;
                queue.RemoveAt(i--);
                try { q.act(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[teamfx] {e.GetType().Name}: {e.Message}"); }
            }
        }

        private void Schedule(byte from, float remoteTime, Action act) => queue.Add((TeamSpace.DueAt(presence, from, remoteTime), from, act));

        // ---- one-off effects ----

        // PlayerMaster.PlayExtraVFXAsync (a second effects animator: never in the state), or PlayExtraVFX of the
        // teleport / respawn flash while the character is hidden or between "in game" states (the puppet is off).
        internal void OnLocalExtraFx(PlayerMaster p, string trigger, bool second)
        {
            if (!Sending || p == null || p != Game.LocalPlayer || string.IsNullOrEmpty(trigger)) return;
            if (!second && !((trigger == "Spawn" || trigger == "Teleport") && (!Game.InGame || !ArtShown(p)))) return;
            SendMoment(p, Moment.ExtraFx, trigger);
        }

        internal void OnLocalSpawner(ObjectSpawner sp)
        {
            var p = Game.LocalPlayer;
            if (!Sending || p == null || sp == null) return;
            if (sp == p.scorchMarkSpawner) SendMoment(p, Moment.ScorchMark, null);
            else if (sp == p.goreMarkSpawner) SendMoment(p, Moment.GoreMark, null);
        }

        private static bool ArtShown(PlayerMaster p) => p.artToHide == null || p.artToHide.Count == 0 || p.artToHide[0] == null || p.artToHide[0].activeSelf;

        private void SendMoment(PlayerMaster p, Moment kind, string trigger)
        {
            Vector2 pos = p.transform.position;
            int space = TeamSpace.Here;
            float time = Time.realtimeSinceStartup;
            session.Send(Msg.PlayerMoment, w =>
            {
                w.Put(time);
                w.Put(space);
                w.Put(pos.x); w.Put(pos.y);
                w.Put((byte)kind);
                if (kind == Moment.ExtraFx) w.Put(trigger.Length > MaxTrigger ? trigger.Substring(0, MaxTrigger) : trigger);
            });
            MomentsSent++;
            if (Args.Test != null) Plugin.Log.LogInfo($"[teamfx] sent {kind} {trigger} at {pos}");
        }

        private void OnMoment(byte from, NetDataReader r)
        {
            float time = TeamSpace.Finite(r.GetFloat(), 1e7f);
            int space = r.GetInt();
            var pos = new Vector2(TeamSpace.Finite(r.GetFloat(), 100000f), TeamSpace.Finite(r.GetFloat(), 100000f));
            var kind = (Moment)r.GetByte();
            if (kind > Moment.GoreMark) throw new FormatException("unknown moment " + (byte)kind);
            string trigger = kind == Moment.ExtraFx ? r.GetString(MaxTrigger) : "";
            MomentsReceived++;
            if (Args.Test != null) MomentLog.Add($"{from}:{kind}:{trigger}");
            Schedule(from, time, () =>
            {
                if (!Game.InGame || space != TeamSpace.Here) return;   // another room
                if (kind == Moment.ExtraFx) ShowExtraFx(from, trigger, pos);
                else ShowMark(from, kind, pos);
            });
        }

        // What PlayExtraVFXAsync does: a temporary copy of the effects animator, the trigger, gone when it's played.
        private void ShowExtraFx(byte from, string trigger, Vector2 pos)
        {
            var me = Game.LocalPlayer;
            var src = me != null ? me.extraFxAnimator : null;
            if (src == null) return;
            var holder = new GameObject("MP_FxHolder");
            holder.SetActive(false);
            var copy = Object.Instantiate(src.gameObject, holder.transform);
            foreach (var c in copy.GetComponentsInChildren<Component>(true))
                if (!(c is Transform || c is SpriteRenderer || c is Animator)) Object.DestroyImmediate(c);
            Vector3 offset = src.transform.position - me.transform.position;
            copy.transform.SetParent(null, false);
            Object.Destroy(holder);
            copy.name = "MP_ExtraFx_" + trigger;
            copy.transform.position = new Vector3(pos.x + offset.x, pos.y + offset.y, src.transform.position.z);
            copy.transform.rotation = Quaternion.identity;
            copy.transform.localScale = src.transform.lossyScale;
            copy.SetActive(true);
            var anim = copy.GetComponent<Animator>();
            anim.fireEvents = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            anim.SetTrigger(trigger);
            copy.AddComponent<OneShotFx>().Anim = anim;
            FxShown++;
            if (Args.Test != null) ShownLog.Add((from, trigger, pos));
            if (Args.Test != null) Plugin.Log.LogInfo($"[teamfx] showing {trigger} of {from} at {pos}");
        }

        // What PlayerDead does with the scorch / gore mark spawners, with our own copy of the mark.
        private void ShowMark(byte from, Moment kind, Vector2 pos)
        {
            var me = Game.LocalPlayer;
            var sp = me == null ? null : kind == Moment.GoreMark ? me.goreMarkSpawner : me.scorchMarkSpawner;
            if (sp == null || sp.objectPrefab == null) return;
            var go = FxCopies.Get(sp.objectPrefab, keepColliders: false);
            if (go == null) return;
            var at = sp.spawnPoints != null && sp.spawnPoints.Count > 0 && sp.spawnPoints[0] != null ? sp.spawnPoints[UnityEngine.Random.Range(0, sp.spawnPoints.Count)] : sp.transform;
            if (at == null) at = sp.transform;
            Vector3 offset = at.position - me.transform.position;
            go.transform.position = new Vector3(pos.x + offset.x, pos.y + offset.y, at.position.z);
            if (sp.keepRotation) go.transform.rotation = sp.transform.rotation;
            if (sp.replaceAnim != null)
            {
                var a = go.GetComponentInChildren<Animator>();
                if (a != null) a.runtimeAnimatorController = sp.replaceAnim;
            }
            go.SetActive(true);
            MarksShown++;
            if (Args.Test != null) ShownLog.Add((from, kind.ToString(), pos));
            if (Args.Test != null) Plugin.Log.LogInfo($"[teamfx] {kind} of {from} at {pos}");
        }

        // ---- floating text and the health bar ----

        // Raised for our character: at its position (the game uses the character's own transform for these).
        private bool OursAt(Vector3 origin)
        {
            var me = Game.LocalPlayer;
            return me != null && ((Vector2)(origin - me.transform.position)).sqrMagnitude < 1e-4f;
        }

        private void OnLocalDamageText(UIDamageEvent e)
        {
            if (replaying || !Sending || e.Result == DamageResult.GainXP || !OursAt(e.origin)) return;
            string msg = e.Message ?? "";
            if (PersonalMessages.Contains(msg)) return;
            if (msg.Length > MaxMessage) msg = msg.Substring(0, MaxMessage);
            float time = Time.realtimeSinceStartup;
            float amount = float.IsNaN(e.DamageAmount) || float.IsInfinity(e.DamageAmount) ? 0f : e.DamageAmount;
            var result = e.Result;
            session.Send(Msg.PlayerFloatText, w =>
            {
                w.Put(time);
                w.Put((byte)TextKind.Damage);
                w.Put(amount);
                w.Put((byte)result);
                w.Put(msg);
            });
            TextsSent++;
        }

        private void OnLocalBar(UIFloatingBarEvent e)
        {
            var me = Game.LocalPlayer;
            if (replaying || !Sending || me == null || e.Owner != me.transform) return;
            float time = Time.realtimeSinceStartup;
            session.Send(Msg.PlayerFloatText, w =>
            {
                w.Put(time);
                w.Put((byte)TextKind.Bar);
                w.Put(Mathf.FloatToHalf(Mathf.Clamp(e.InitialFill, -1f, 2f)));
                w.Put(Mathf.FloatToHalf(Mathf.Clamp(e.CurrentFill, -1f, 2f)));
                w.Put(e.Hide ? BarHide : (byte)0);
            });
            BarsSent++;
        }

        private void OnFloatText(byte from, NetDataReader r)
        {
            float time = TeamSpace.Finite(r.GetFloat(), 1e7f);
            var kind = (TextKind)r.GetByte();
            if (kind == TextKind.Damage)
            {
                float amount = TeamSpace.Finite(r.GetFloat(), 1e7f);
                var result = (DamageResult)r.GetByte();
                string msg = r.GetString(MaxMessage);
                Schedule(from, time, () => ShowText(from, amount, msg, result));
            }
            else if (kind == TextKind.Bar)
            {
                float initial = Mathf.HalfToFloat(r.GetUShort()), current = Mathf.HalfToFloat(r.GetUShort());
                bool hide = (r.GetByte() & BarHide) != 0;
                if (float.IsNaN(initial) || float.IsNaN(current)) throw new FormatException("bad bar fill");
                Schedule(from, time, () => ShowBar(from, initial, current, hide));
            }
            else throw new FormatException("unknown float text kind " + (byte)kind);
        }

        private void ShowText(byte from, float amount, string msg, DamageResult result)
        {
            if (!presence.Remotes.TryGetValue(from, out var rp) || !rp.Shown) return;
            var at = rp.Puppet.transform.position;
            replaying = true;
            try { Bus<UIDamageEvent>.Invoke(new UIDamageEvent(amount, msg, at, result)); }
            finally { replaying = false; }
            TextsShown++;
            if (Args.Test != null) TextLog.Add((from, amount, msg, result, at));
        }

        // Always fading on its own: a teammate's bar left on would sit over the party HUD's.
        private void ShowBar(byte from, float initial, float current, bool hide)
        {
            if (!presence.Remotes.TryGetValue(from, out var rp) || rp.Puppet == null) return;
            if (!hide && (!rp.Shown || Game.LocalPlayer != null && Game.LocalPlayer.HPBarShow == ShowType.AlwaysOff)) return;
            replaying = true;
            try
            {
                Bus<UIFloatingBarEvent>.Invoke(hide
                    ? new UIFloatingBarEvent(rp.Puppet.transform, 0f, 0f, hide: true)
                    : new UIFloatingBarEvent(rp.Puppet.transform, initial, current, hide: false, autoHide: true));
            }
            finally { replaying = false; }
            if (hide) return;
            BarsShown++;
            if (Args.Test != null)
            {
                var ui = Object.FindFirstObjectByType<UIManager>();
                LastBarInUi = ui == null ? -1 : ui.floatingBars.ContainsKey(rp.Puppet.transform) ? 1 : 0;
            }
        }

        // ---- animator speed ----

        // Each state tick (Presence): our animator's speed, when it changed (PlayerAttack multiplies it for the attack).
        internal void OnLocalState(float time, PlayerMaster me)
        {
            if (!Sending || me == null || me.animator == null) return;
            if (speedOf != me) { speedOf = me; lastSpeed = float.NaN; }
            float speed = me.animator.speed;
            if (!float.IsNaN(lastSpeed) && Mathf.Abs(speed - lastSpeed) < 0.001f) return;
            lastSpeed = speed;
            session.Send(Msg.PlayerAnimSpeed, w => { w.Put(time); w.Put(speed); });
            SpeedsSent++;
        }

        private void OnAnimSpeed(byte from, NetDataReader r)
        {
            float time = TeamSpace.Finite(r.GetFloat(), 1e7f);
            float speed = Mathf.Clamp(TeamSpace.Finite(r.GetFloat(), 1000f), 0f, 10f);
            Schedule(from, time, () =>
            {
                if (!presence.Remotes.TryGetValue(from, out var rp)) return;
                rp.AnimSpeed = speed;
                SpeedsApplied++;
            });
        }
    }

    // A temporary effects animator (PlayExtraVFXAsync's way): gone once its effect has played (or after a while).
    internal class OneShotFx : MonoBehaviour
    {
        public static int Played;
        public Animator Anim;
        private int startState;
        private bool started, left;
        private float age;

        private void Update()
        {
            age += Time.deltaTime;
            if (Anim == null || age > 5f) { Destroy(gameObject); return; }
            var st = Anim.GetCurrentAnimatorStateInfo(0);
            if (!started) { started = true; startState = st.fullPathHash; return; }
            if (!left && st.fullPathHash != startState) { left = true; Played++; }
            if (left && !Anim.IsInTransition(0) && st.normalizedTime >= 0.8f) Destroy(gameObject);
        }
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.PlayExtraVFX))]
    internal static class PlayerExtraFxPatch
    {
        private static void Prefix(PlayerMaster __instance, string triggerName) => TeamFx.Instance?.OnLocalExtraFx(__instance, triggerName, second: false);
    }

    [HarmonyPatch(typeof(PlayerMaster), nameof(PlayerMaster.PlayExtraVFXAsync))]
    internal static class PlayerExtraFxAsyncPatch
    {
        private static void Prefix(PlayerMaster __instance, string triggerName) => TeamFx.Instance?.OnLocalExtraFx(__instance, triggerName, second: true);
    }

    [HarmonyPatch(typeof(ObjectSpawner), nameof(ObjectSpawner.SpawnObject))]
    internal static class PlayerDeathMarkPatch
    {
        private static void Prefix(ObjectSpawner __instance) => TeamFx.Instance?.OnLocalSpawner(__instance);
    }
}
