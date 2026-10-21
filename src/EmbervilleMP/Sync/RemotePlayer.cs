using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace EmbervilleMP.Sync
{
    // Another player, shown as a "puppet": a copy of the local player's rig with all game logic stripped,
    // moved and animated from the states that player sends.
    internal class RemotePlayer
    {
        public readonly PlayerInfo Info;
        public GameObject Puppet { get; private set; }
        public PlayerState Latest { get; private set; }
        public PlayerLook Look { get; private set; }
        public RemotePlayerTarget Target { get; private set; }   // host only: lets enemies chase and hit this player
        public bool Shown => Puppet != null && Puppet.activeSelf;
        public Vector3 HeadPosition => Puppet != null ? Puppet.transform.position + Vector3.up * 1.35f : Vector3.zero;

        private const float InterpDelay = 0.1f;
        private readonly List<PlayerState> buffer = new List<PlayerState>();
        private float clockOffset;            // remote clock - local clock (smoothed)
        private bool hasClock;
        private PlayerState lastApplied;
        private Animator animator;
        private Animator fxAnimator;
        private List<int> floatParams;
        private Transform[] flipPivots;
        private Dictionary<string, Transform> nodes;
        private readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
        private int builtForScene = -1;
        private Light2D nightLight;

        // The animator speed that player's character has (attacks with a speed multiplier), from TeamFx.
        public float AnimSpeed = 1f;

        public RemotePlayer(PlayerInfo info) { Info = info; }

        public float LastStateAt { get; private set; } = -100f;

        // Local realtime at which something that happened at this player's remoteTime shows on the puppet (the same
        // clock and interpolation delay as its states): its sounds and effects line up with its animations.
        public float LocalTimeOf(float remoteTime) =>
            hasClock ? remoteTime - clockOffset + InterpDelay : Time.realtimeSinceStartup;
        public float SecondsSinceState => Time.realtimeSinceStartup - LastStateAt;
        // The moment of this player's clock the puppet shows now (EnemyAim: where an enemy aiming at it aims), or -1.
        public float RenderClock => hasClock ? Time.realtimeSinceStartup + clockOffset - InterpDelay : -1f;

        public void OnState(PlayerState s)
        {
            LastStateAt = Time.realtimeSinceStartup;
            // Far behind what we have: that clock started over, or what we had was junk. Start again from this one.
            if (buffer.Count > 0 && s.Time < buffer[buffer.Count - 1].Time - 5f) { buffer.Clear(); hasClock = false; }
            // offset = (their clock - ours) - this packet's delay. The fastest packet (largest offset) is the best
            // estimate; a stall (scene load, hitch) makes packets late, and must not leave the puppet trailing.
            float offset = s.Time - Time.realtimeSinceStartup;
            if (!hasClock || offset > clockOffset) clockOffset = offset;
            else clockOffset = Mathf.Lerp(clockOffset, offset, 0.002f); // follow slow clock drift
            hasClock = true;
            if (buffer.Count > 0 && s.Time <= buffer[buffer.Count - 1].Time) return; // stale
            buffer.Add(s);
            if (buffer.Count > 40) buffer.RemoveAt(0);
            Latest = s;
        }

        public void OnLook(PlayerLook look)
        {
            Look = look;
            if (Puppet != null) ApplyLook();
        }

        public void Update()
        {
            if (Latest == null || !Game.InScene) { SetShown(false); return; }   // (dead, we still run the enemies chasing them)
            bool sameSpace = Latest.Scene == Game.SceneName && Latest.Area == Game.AreaName;
            if (!sameSpace || !Latest.Visible) { SetShown(false); return; }

            EnsurePuppet();
            if (Puppet == null) return;
            SetShown(true);
            if (animator != null && animator.speed != AnimSpeed) animator.speed = AnimSpeed;
            MirrorNightLight();

            float renderTime = Time.realtimeSinceStartup + clockOffset - InterpDelay;
            PlayerState a = null, b = null;
            for (int i = 0; i < buffer.Count; i++)
            {
                if (buffer[i].Time <= renderTime) a = buffer[i];
                else { b = buffer[i]; break; }
            }
            Vector2 pos;
            PlayerState current;
            if (a != null && b != null && (b.Position - a.Position).sqrMagnitude < 25f)
            {
                float t = Mathf.InverseLerp(a.Time, b.Time, renderTime);
                pos = Vector2.Lerp(a.Position, b.Position, t);
                current = a;
            }
            else
            {
                current = a ?? buffer[0];
                pos = current.Position;
            }
            Puppet.transform.position = new Vector3(pos.x, pos.y, Puppet.transform.position.z);
            if (Target != null) Target.Dead = Latest.Dead;
            if (current != lastApplied)
            {
                ApplyAnimation(current);
                lastApplied = current;
            }
        }

        public Animator PuppetAnimator => animator;
        public PlayerState Applied => lastApplied;

        // Self-test helper: how many look entries the puppet doesn't match (parts, tints, colour materials).
        public int LookMismatches(out int total)
        {
            total = 0;
            int bad = 0;
            if (Look == null || Puppet == null) return -1;
            foreach (var n in Look.Nodes)
            {
                if (!nodes.TryGetValue(n.Path, out var t)) { bad++; total++; continue; }
                total++;
                if (t.gameObject.activeSelf != n.Active) bad++;
                else if (n.HasRenderer)
                {
                    var sr = t.GetComponent<SpriteRenderer>();
                    if (sr == null || sr.enabled != n.RendererEnabled || (Color32)sr.color is var c && (c.r != n.Color.r || c.g != n.Color.g || c.b != n.Color.b || c.a != n.Color.a)) bad++;
                }
            }
            foreach (var kv in Look.Materials)
            {
                total++;
                if (!materials.TryGetValue(kv.Key, out var m)) { bad++; continue; }
                foreach (var p in kv.Value)
                    if (p.Type == UnityEngine.Rendering.ShaderPropertyType.Color && m.HasProperty(p.Name) && (Vector4)m.GetColor(p.Name) != p.Value) { bad++; break; }
            }
            return bad;
        }

        // Raised just before a puppet goes away, so enemies chasing it can be pointed elsewhere first.
        public static event System.Action<GameObject> PuppetDestroying;

        public void Destroy()
        {
            if (Puppet != null)
            {
                PuppetDestroying?.Invoke(Puppet);
                Object.Destroy(Puppet);
            }
            foreach (var m in materials.Values) Object.Destroy(m);
            materials.Clear();
            Puppet = null;
        }

        private void SetShown(bool shown)
        {
            if (Puppet != null && Puppet.activeSelf != shown) Puppet.SetActive(shown);
        }

        private void EnsurePuppet()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (Puppet != null && builtForScene == scene) return;
            var template = Game.LocalPlayer;
            if (template == null) return;
            Destroy();
            Puppet = PuppetBuilder.Build(template, "MP_" + Info.Name, out animator, out flipPivots);
            builtForScene = scene;
            fxAnimator = Puppet.transform.Find("Player_ExtraFxAnimations")?.GetComponent<Animator>();
            floatParams = animator.parameters.Where(p => p.type == AnimatorControllerParameterType.Float).Select(p => p.nameHash).ToList();
            nodes = new Dictionary<string, Transform>();
            var graphics = Puppet.transform.Find("Graphics");
            if (graphics != null)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var t in graphics.GetComponentsInChildren<Transform>(true))
                    if (t != graphics) nodes[PlayerLook.PathOf(t, Puppet.transform, sb)] = t;
            }
            // Give the puppet its own copies of the colour materials, so its colours don't change ours.
            foreach (var sr in Puppet.GetComponentsInChildren<SpriteRenderer>(true))
            {
                var m = sr.sharedMaterial;
                if (PlayerLook.IsSharedDefault(m)) continue;
                if (!materials.TryGetValue(m.name, out var copy))
                {
                    copy = new Material(m) { name = m.name };
                    materials[m.name] = copy;
                }
                sr.sharedMaterial = copy;
            }
            lastApplied = null;
            nightLight = PuppetBuilder.NightLightOf(Puppet.transform);
            // Whoever runs this scene lets its enemies chase and hit this player (any machine can be that).
            Target = RemotePlayerTarget.Attach(Puppet, template, Info.Id);
            PuppetTouch.Attach(Puppet, template);   // grass, critters, ghosts react to this player too
            if (Look != null) ApplyLook();
            Plugin.Log.LogInfo($"[sync] Built puppet for {Info} ({nodes.Count} parts)");
        }

        // The night light (Player_Base/NightLight) is tinted by the time of day, the same for everyone (the clock is
        // synced): the puppet's shows what ours shows, so teammates aren't dark shapes at night.
        private static PlayerMaster lightOwner;
        private static Light2D localLight;

        private void MirrorNightLight()
        {
            if (nightLight == null) return;
            var me = Game.LocalPlayer;
            if (me != lightOwner) { lightOwner = me; localLight = me != null ? PuppetBuilder.NightLightOf(me.transform) : null; }
            if (localLight == null) return;
            bool on = localLight.enabled && localLight.gameObject.activeInHierarchy;
            if (nightLight.enabled != on) nightLight.enabled = on;
            nightLight.color = localLight.color;
            nightLight.intensity = localLight.intensity;
        }

        public Light2D NightLight => nightLight;

        private void ApplyLook()
        {
            foreach (var n in Look.Nodes)
            {
                if (!nodes.TryGetValue(n.Path, out var t)) continue;
                if (t.gameObject.activeSelf != n.Active) t.gameObject.SetActive(n.Active);
                if (n.HasRenderer)
                {
                    var sr = t.GetComponent<SpriteRenderer>();
                    if (sr != null) { sr.enabled = n.RendererEnabled; sr.color = n.Color; }
                }
            }
            foreach (var kv in Look.Materials)
                if (materials.TryGetValue(kv.Key, out var m)) PlayerLook.ApplyMaterial(m, kv.Value);
        }

        private void ApplyAnimation(PlayerState s)
        {
            if (animator == null) return;
            foreach (var pivot in flipPivots)
                if (pivot != null) pivot.rotation = Quaternion.Euler(0f, s.Flipped ? 180f : 0f, 0f);
            for (int i = 0; i < floatParams.Count && i < s.Floats.Length; i++)
                animator.SetFloat(floatParams[i], s.Floats[i]);
            if (fxAnimator != null && fxAnimator.isActiveAndEnabled && s.FxState != 0 &&
                fxAnimator.GetCurrentAnimatorStateInfo(0).fullPathHash != s.FxState)
                fxAnimator.Play(s.FxState, 0, s.FxTime > 1f ? s.FxTime % 1f : Mathf.Max(0f, s.FxTime));
            for (int i = 0; i < s.LayerStates.Length && i < animator.layerCount; i++)
            {
                var cur = animator.GetCurrentAnimatorStateInfo(i);
                if (cur.fullPathHash == s.LayerStates[i]) continue;
                if (animator.IsInTransition(i) && animator.GetNextAnimatorStateInfo(i).fullPathHash == s.LayerStates[i]) continue;
                float t = s.LayerTimes[i];
                animator.Play(s.LayerStates[i], i, t > 1f ? t % 1f : Mathf.Max(0f, t));
            }
        }
    }
}
