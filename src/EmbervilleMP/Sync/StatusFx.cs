using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Status effects and buffs (burning, poison, webbed, shields...) of other players, as seen here.
    // The effect itself runs on its owner's machine; its owner sends the list of active effects whenever it
    // changes, and the puppet here replays what the game's EffectsHandler wiring does on a real player:
    //   - burn/bleed/freeze/poison/shock: PlayerMaster.SetEffect plays a "Status/<x>" loop on the extra FX animator
    //   - trapped (the Matriarch's web): Graphics/Extras/Coccoon on, animator float TrappedLevel 3 -> 2 -> 1 -> 0,
    //     and a burst of the VFX_Coccoon particles on start, each stage and the end
    // Nothing of the effect's logic runs on the puppet (it never takes damage or changes speed).
    internal class StatusFx
    {
        public static StatusFx Instance { get; private set; }
        public int ListsSent, ListsReceived, Bursts;

        public struct NetEffect
        {
            public EffectType Type;
            public string Name;      // only for EffectType.Other
            public int Stacks;
            public float EndsAt;     // local realtime, or -1 without a duration
        }

        private class Remote
        {
            public readonly List<NetEffect> Effects = new List<NetEffect>();
            public float TrappedLevel;
            public bool Dirty;
            // what the puppet currently shows (and which puppet: a rebuilt one starts over)
            public GameObject AppliedTo;
            public bool ShownTrapped;
            public float ShownLevel;
            public string ShownStatus;
            public ParticleSystem Vfx;
        }

        private const float PollInterval = 0.2f;
        private static readonly int TrappedLevelHash = Animator.StringToHash("TrappedLevel");
        private static readonly int EmptyFxHash = Animator.StringToHash("Base Layer.Empty");
        private static readonly Dictionary<string, int> StatusStates = new[] { "Bleed", "Burn", "Freeze", "Poison", "Shock" }
            .ToDictionary(n => n, n => Animator.StringToHash("Base Layer.Status.Status/" + n));

        private readonly Session session;
        private readonly Presence presence;
        private readonly Dictionary<byte, Remote> remotes = new Dictionary<byte, Remote>();
        private float nextPoll;
        private string lastSignature = "";
        private bool forceSend = true;
        private Animator trappedParamFor;
        private bool hasTrappedParam;

        public StatusFx(Session session, Presence presence)
        {
            Instance = this;
            this.session = session;
            this.presence = presence;
            session.On(Msg.PlayerEffects, OnEffects);
            // Late arrivals: everyone re-sends its list when someone joins (and we do when we're welcomed).
            session.PlayerJoined += p => forceSend = true;
            session.Welcomed += () => forceSend = true;
            session.PlayerLeft += p => remotes.Remove(p.Id);
        }

        // ---------------- our own effects ----------------

        public void Update()
        {
            if (!session.Ready || Session.Current != session) return;
            float now = Time.realtimeSinceStartup;
            if (now >= nextPoll)
            {
                nextPoll = now + PollInterval;
                SendIfChanged();
            }
            foreach (var kv in remotes) ApplyToPuppet(kv.Key, kv.Value);
        }

        private void SendIfChanged()
        {
            var me = Game.LocalPlayer;
            var list = new List<NetEffect>();
            float level = 0f;
            if (me != null && Game.InGame && me.EffectsHandler != null)
            {
                foreach (var e in me.EffectsHandler.effects)
                {
                    if (e == null || e.IsRemoved) continue;
                    float remaining = e.Duration.HasValue ? Mathf.Max(0f, e.Duration.Value - (float)e.timeSinceApplied) : -1f;
                    list.Add(new NetEffect
                    {
                        Type = e.Type,
                        Name = e.Type == EffectType.Other ? e.Name ?? "" : "",
                        Stacks = e is ShieldEffect sh ? sh.Stacks : e.Stacks,
                        EndsAt = remaining,   // seconds left, on the wire
                    });
                }
                level = TrappedLevelOf(me.animator);
            }
            // Re-sent when an effect comes or goes, stacks change, the web weakens, or a duration is refreshed.
            var sig = new System.Text.StringBuilder();
            foreach (var e in list) sig.Append((int)e.Type).Append(e.Name).Append(':').Append(e.Stacks).Append(';');
            sig.Append(level.ToString("F1"));
            string signature = sig.ToString();
            bool refreshed = list.Any(e => e.EndsAt >= 0f && lastSent.TryGetValue(Key(e), out float prev) && e.EndsAt > prev + 0.5f);
            if (!forceSend && !refreshed && signature == lastSignature)
            {
                foreach (var e in list) lastSent[Key(e)] = e.EndsAt;
                return;
            }
            forceSend = false;
            lastSignature = signature;
            lastSent.Clear();
            foreach (var e in list) lastSent[Key(e)] = e.EndsAt;
            session.Send(Msg.PlayerEffects, w =>
            {
                w.Put((byte)Mathf.Min(list.Count, 255));
                foreach (var e in list.Take(255))
                {
                    w.Put((ushort)e.Type);
                    w.Put(e.Name);
                    w.Put((short)Mathf.Clamp(e.Stacks, short.MinValue, short.MaxValue));
                    w.Put(e.EndsAt);
                }
                w.Put(level);
            });
            ListsSent++;
            Plugin.Log.LogInfo($"[fx] Sent our effects: {Describe(list, level, false)}");
        }

        private readonly Dictionary<string, float> lastSent = new Dictionary<string, float>();
        private static string Key(NetEffect e) => (int)e.Type + e.Name;

        private float TrappedLevelOf(Animator a)
        {
            if (a == null) return 0f;
            if (trappedParamFor != a)
            {
                trappedParamFor = a;
                hasTrappedParam = a.parameters.Any(p => p.nameHash == TrappedLevelHash && p.type == AnimatorControllerParameterType.Float);
            }
            return hasTrappedParam ? a.GetFloat(TrappedLevelHash) : 0f;
        }

        // ---------------- other players' effects ----------------

        private void OnEffects(byte from, NetDataReader r)
        {
            if (from == session.LocalId) return;
            if (!remotes.TryGetValue(from, out var rem)) remotes[from] = rem = new Remote();
            rem.Effects.Clear();
            float now = Time.realtimeSinceStartup;
            int n = r.GetByte();
            for (int i = 0; i < n; i++)
            {
                var e = new NetEffect { Type = (EffectType)r.GetUShort(), Name = r.GetString(), Stacks = r.GetShort() };
                float remaining = r.GetFloat();
                e.EndsAt = remaining >= 0f ? now + remaining : -1f;
                rem.Effects.Add(e);
            }
            rem.TrappedLevel = r.GetFloat();
            rem.Dirty = true;
            ListsReceived++;
            Plugin.Log.LogInfo($"[fx] {NameOf(from)}: {Describe(rem.Effects, rem.TrappedLevel, true)}");
        }

        private string NameOf(byte id) => session.Players.TryGetValue(id, out var p) ? p.ToString() : "#" + id;

        private static string Describe(List<NetEffect> list, float level, bool endsAtIsClock)
        {
            if (list.Count == 0) return "none";
            float now = Time.realtimeSinceStartup;
            return string.Join(", ", list.Select(e =>
            {
                string t = e.Type == EffectType.Other ? e.Name : e.Type.ToString();
                float left = e.EndsAt < 0f ? -1f : endsAtIsClock ? e.EndsAt - now : e.EndsAt;
                return t + (e.Stacks != 1 ? " x" + e.Stacks : "") + (left >= 0f ? $" {left:F1}s" : "");
            })) + (level > 0f ? $" (web {level:F0})" : "");
        }

        private void ApplyToPuppet(byte id, Remote rem)
        {
            if (!presence.Remotes.TryGetValue(id, out var rp) || !rp.Shown) return;
            var puppet = rp.Puppet;
            bool fresh = rem.AppliedTo != puppet;
            if (!fresh && !rem.Dirty) return;
            if (fresh)
            {
                rem.AppliedTo = puppet;
                rem.Vfx = null;
                rem.ShownTrapped = false;
                rem.ShownLevel = 0f;
                rem.ShownStatus = null;
            }
            rem.Dirty = false;

            // The web cocoon: what the EffectsHandler's Trapped receiver does on a real player.
            bool trapped = rem.Effects.Any(e => e.Type == EffectType.Trapped);
            float level = trapped ? (rem.TrappedLevel > 0f ? rem.TrappedLevel : 3f) : 0f;
            var cocoon = puppet.transform.Find("Graphics/Extras/Coccoon");
            if (cocoon != null && cocoon.gameObject.activeSelf != trapped) cocoon.gameObject.SetActive(trapped);
            var anim = rp.PuppetAnimator;
            if (anim != null && anim.parameters.Any(p => p.nameHash == TrappedLevelHash)) anim.SetFloat(TrappedLevelHash, level);
            if (trapped != rem.ShownTrapped || (trapped && !Mathf.Approximately(level, rem.ShownLevel))) Burst(rp, rem);
            rem.ShownTrapped = trapped;
            rem.ShownLevel = level;

            // The status loop on the extra FX animator: PlayerMaster.SetEffect shows the latest effect's status
            // ("EndStatus" when that one isn't burn/bleed/freeze/poison/shock). Only played when it changes, so the
            // FX states streamed with the player's state (dash, spells) aren't fought over.
            string status = null;
            if (rem.Effects.Count > 0)
            {
                switch (rem.Effects[rem.Effects.Count - 1].Type)
                {
                    case EffectType.Bleed: status = "Bleed"; break;
                    case EffectType.Burn: status = "Burn"; break;
                    case EffectType.Freeeze: status = "Freeze"; break;
                    case EffectType.Kymia:
                    case EffectType.Poison: status = "Poison"; break;
                    case EffectType.Shock: status = "Shock"; break;
                }
            }
            if (status != rem.ShownStatus) PlayStatus(puppet, status);
            rem.ShownStatus = status;
        }

        private static void PlayStatus(GameObject puppet, string status)
        {
            var fx = puppet.transform.Find("Player_ExtraFxAnimations")?.GetComponent<Animator>();
            if (fx == null || !fx.isActiveAndEnabled) return;
            if (status != null && StatusStates.TryGetValue(status, out int h))
            {
                if (fx.HasState(0, h)) fx.Play(h, 0, 0f);
            }
            else if (InStatus(fx) && fx.HasState(0, EmptyFxHash)) fx.Play(EmptyFxHash, 0, 0f);
        }

        private static bool InStatus(Animator fx)
        {
            int cur = fx.GetCurrentAnimatorStateInfo(0).fullPathHash;
            return StatusStates.Values.Contains(cur);
        }

        // The cocoon's particles: the puppet builder strips particle systems, so the puppet gets its own copy of
        // the local player's (particles only: no scripts, no colliders).
        private void Burst(RemotePlayer rp, Remote rem)
        {
            if (rem.Vfx == null)
            {
                var src = Game.LocalPlayer != null ? Game.LocalPlayer.transform.Find("Graphics/Extras/VFX_Coccoon") : null;
                var parent = rp.Puppet.transform.Find("Graphics/Extras");
                if (src == null || parent == null) return;
                // Built under an inactive holder so nothing plays on awake before it's set up.
                var holder = new GameObject("MP_FxHolder");
                holder.SetActive(false);
                var go = Object.Instantiate(src.gameObject, holder.transform, false);
                go.name = "MP_VFX_Coccoon";
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                    if (!(c is Transform || c is ParticleSystem || c is ParticleSystemRenderer)) Object.DestroyImmediate(c);
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = ps.main;
                    main.playOnAwake = false;
                    main.stopAction = ParticleSystemStopAction.None;  // a "Destroy" stop action would take our copy away
                }
                go.transform.SetParent(parent, false);
                go.transform.localPosition = src.localPosition;
                go.transform.localRotation = src.localRotation;
                go.transform.localScale = src.localScale;
                go.SetActive(true);
                Object.Destroy(holder);
                rem.Vfx = go.GetComponentInChildren<ParticleSystem>(true);   // its own first
                if (rem.Vfx == null) { Object.Destroy(go); return; }
            }
            rem.Vfx.Play(true);
            Bursts++;
        }

        // ---------------- read by name tags and self-tests ----------------

        public IReadOnlyList<NetEffect> EffectsOf(byte id) => remotes.TryGetValue(id, out var r) ? r.Effects : (IReadOnlyList<NetEffect>)new NetEffect[0];
        public float TrappedLevelOf(byte id) => remotes.TryGetValue(id, out var r) ? r.TrappedLevel : 0f;
        public ParticleSystem CocoonVfxOf(byte id) => remotes.TryGetValue(id, out var r) && r.Vfx != null ? r.Vfx : null;
        public static int StatusStateHash(string status) => StatusStates.TryGetValue(status, out int h) ? h : 0;

        // Small effect icons under the other players' name tags (the game's own side-bar icons where it has one).
        private PlayerMaster iconsFor;
        private Dictionary<EffectType, Sprite> icons = new Dictionary<EffectType, Sprite>();
        private static GUIStyle chip, timer;

        public void OnGUI()
        {
            if (!session.Ready || Session.Current != session || remotes.Count == 0) return;
            var cam = Camera.main;
            if (cam == null) return;
            LoadIcons();
            if (chip == null)
            {
                chip = new GUIStyle(MPStyle.Hud) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                timer = new GUIStyle(MPStyle.Hud) { alignment = TextAnchor.LowerRight, fontSize = Mathf.Max(10, Screen.height / 90), fontStyle = FontStyle.Bold };
            }
            float now = Time.realtimeSinceStartup;
            float size = Mathf.Max(16f, Screen.height / 40f);
            foreach (var kv in remotes)
            {
                if (!presence.Remotes.TryGetValue(kv.Key, out var rp) || !rp.Shown) continue;
                var shown = kv.Value.Effects.Where(e => e.EndsAt < 0f || e.EndsAt > now - 0.5f).ToList();
                if (shown.Count == 0) continue;
                Vector3 sp = cam.WorldToScreenPoint(rp.HeadPosition);
                if (sp.z < 0) continue;
                float y = Screen.height - sp.y + 12f;   // just under the name tag (Presence.OnGUI)
                var widths = shown.Select(e => icons.ContainsKey(e.Type) ? size : chip.CalcSize(new GUIContent(Label(e))).x + 6f).ToList();
                float x = sp.x - (widths.Sum() + 3f * (shown.Count - 1)) / 2f;
                for (int i = 0; i < shown.Count; i++)
                {
                    var e = shown[i];
                    var rect = new Rect(x, y, widths[i], size);
                    if (!(icons.TryGetValue(e.Type, out var sprite) && DrawSprite(rect, sprite)))
                        MPStyle.Shadowed(rect, Label(e), chip);
                    if (e.EndsAt >= 0f)
                    {
                        int left = Mathf.CeilToInt(Mathf.Max(0f, e.EndsAt - now));
                        MPStyle.Shadowed(new Rect(rect.x, rect.y + 4f, rect.width + 4f, rect.height + 2f), left.ToString(), timer);
                    }
                    x += widths[i] + 3f;
                }
            }
        }

        private static string Label(NetEffect e)
        {
            switch (e.Type)
            {
                case EffectType.Trapped: return "Webbed";
                case EffectType.Shield: return "Shield " + e.Stacks;
                case EffectType.MoveSpeedBuff: return "Speed";
                case EffectType.Other: return string.IsNullOrEmpty(e.Name) ? "Buff" : e.Name;
                default: return e.Type == EffectType.Freeeze ? "Freeze" : e.Type.ToString();
            }
        }

        private void LoadIcons()
        {
            var me = Game.LocalPlayer;
            if (me == null || me == iconsFor) return;
            iconsFor = me;
            icons = new Dictionary<EffectType, Sprite>();
            var bar = me.GetComponent<EffectsSideBar>();
            if (bar == null || bar.icons == null) return;
            foreach (var i in bar.icons)
                if (i != null && i.icon != null && !icons.ContainsKey(i.effect)) icons[i.effect] = i.icon;
        }

        private static bool DrawSprite(Rect r, Sprite s)
        {
            try
            {
                var tex = s.texture;
                if (tex == null) return false;
                var tr = s.textureRect;
                GUI.DrawTextureWithTexCoords(r, tex, new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height));
                return true;
            }
            catch (System.Exception) { return false; }   // tightly packed atlas sprites have no texture rect
        }
    }
}
