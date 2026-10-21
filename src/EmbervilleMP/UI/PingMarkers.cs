using System;
using System.Linq;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    // Draws pings: a pulsing ground ring with a bobbing pin and the player's name, in the player's colour; off screen,
    // a badge on the screen's edge pointing at it. Textures are made once, in code (soft, anti-aliased).
    internal static class PingMarkers
    {
        private static Texture2D ring, thinRing, disc, glow, arrow;
        private static GUIStyle label;
        private const float GroundSquash = 0.62f;   // rings lie on the ground (the game's 3/4 view)

        public static void OnGUI()
        {
            var pings = Pings.Instance;
            if (pings == null || pings.Active.Count == 0 || Event.current.type != EventType.Repaint) return;
            var cam = Camera.main;
            if (cam == null || !Game.InGame) return;
            EnsureAssets();
            float k = Screen.height / 1080f;
            var saved = GUI.color;
            foreach (var p in pings.Active.OrderBy(x => x.Time))
            {
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(p.Position.x, p.Position.y, Game.LocalPlayer.transform.position.z));
                var at = new Vector2(sp.x, Screen.height - sp.y);
                float margin = 40f * k;
                bool onScreen = sp.z > 0 && at.x > margin && at.x < Screen.width - margin && at.y > margin && at.y < Screen.height - margin;
                if (onScreen) DrawMarker(p, at, k);
                else DrawEdge(p, at, sp.z < 0, k);
            }
            GUI.color = saved;
        }

        public static bool OnScreen(Pings.Ping p)
        {
            var cam = Camera.main;
            if (cam == null || Game.LocalPlayer == null) return false;
            Vector3 sp = cam.WorldToScreenPoint(new Vector3(p.Position.x, p.Position.y, Game.LocalPlayer.transform.position.z));
            return sp.z > 0 && sp.x >= 0 && sp.x <= Screen.width && sp.y >= 0 && sp.y <= Screen.height;
        }

        private static float Fade(Pings.Ping p) => Mathf.Clamp01((Pings.Lifetime - p.Age) / 0.8f) * Mathf.Clamp01(p.Age / 0.08f);

        private static float EaseOutBack(float t)
        {
            t = Mathf.Clamp01(t) - 1f;
            return 1f + t * t * (2.7f * t + 1.7f);
        }

        private static void DrawMarker(Pings.Ping p, Vector2 at, float k)
        {
            float t = p.Age, a = Fade(p);
            var col = Pings.ColorOf(p.From);
            float pop = EaseOutBack(t / 0.3f);

            // soft light on the ground
            Draw(glow, at, 120f * k * pop, 120f * k * GroundSquash * pop, col, 0.45f * a);
            // three rings rolling outwards, a new one every 0.4 s
            for (int i = 0; i < 3; i++)
            {
                float start = i * 0.4f;
                if (t < start) continue;
                float phase = ((t - start) % 1.2f) / 1.2f;
                float r = Mathf.Lerp(18f, 78f, 1f - (1f - phase) * (1f - phase)) * k;
                Draw(thinRing, at, r * 2f, r * 2f * GroundSquash, col, (1f - phase) * 0.9f * a);
            }
            // the spot itself
            Draw(ring, at, 34f * k * pop, 34f * k * GroundSquash * pop, col, a);
            Draw(disc, at, 12f * k * pop, 12f * k * GroundSquash * pop, Color.white, a);

            // pin above the spot, bobbing, and the name above it
            float bob = Mathf.Sin(t * 5f) * 3f * k;
            var pin = new Vector2(at.x, at.y - (30f * k + bob) * pop);
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(90f, pin);   // the arrow points right; down it goes
            Draw(arrow, pin, 26f * k * pop, 22f * k * pop, Color.black, 0.45f * a, new Vector2(1.5f * k, -1.5f * k));
            Draw(arrow, pin, 22f * k * pop, 18f * k * pop, col, a);
            GUI.matrix = m;
            Label(new Rect(at.x - 150f * k, pin.y - 44f * k, 300f * k, 28f * k), p.Name, col, a, k);
        }

        // Off screen: a badge just inside the edge, its arrow pointing to the ping.
        private static void DrawEdge(Pings.Ping p, Vector2 at, bool behind, float k)
        {
            float a = Fade(p);
            var col = Pings.ColorOf(p.From);
            var center = new Vector2(Screen.width / 2f, Screen.height / 2f);
            var dir = at - center;
            if (behind) dir = -dir;
            if (dir.sqrMagnitude < 1f) dir = Vector2.up;
            float inset = 56f * k;
            float sx = (Screen.width / 2f - inset) / Mathf.Max(0.001f, Mathf.Abs(dir.x));
            float sy = (Screen.height / 2f - inset) / Mathf.Max(0.001f, Mathf.Abs(dir.y));
            var pos = center + dir * Mathf.Min(sx, sy);
            var n = dir.normalized;

            float phase = (p.Age % 1.2f) / 1.2f;
            Draw(glow, pos, 90f * k, 90f * k, col, 0.4f * a);
            Draw(thinRing, pos, Mathf.Lerp(30f, 76f, phase) * k, Mathf.Lerp(30f, 76f, phase) * k, col, (1f - phase) * 0.9f * a);
            Draw(disc, pos, 30f * k, 30f * k, new Color(0.08f, 0.07f, 0.1f), 0.85f * a);
            Draw(ring, pos, 32f * k, 32f * k, col, a);
            Draw(disc, pos, 9f * k, 9f * k, col, a);

            var tip = pos + n * 26f * k;
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(n.y, n.x) * Mathf.Rad2Deg, tip);
            Draw(arrow, tip, 18f * k, 18f * k, col, a);
            GUI.matrix = m;

            // the name on the side facing the middle of the screen
            float w = 300f * k;
            var r = new Rect(pos.x - w / 2f, pos.y - n.y * 40f * k - 14f * k, w, 28f * k);
            if (Mathf.Abs(n.x) > 0.7f) r = new Rect(n.x > 0 ? pos.x - w - 28f * k : pos.x + 28f * k, pos.y - 14f * k, w, 28f * k);
            Label(r, p.Name, col, a, k, Mathf.Abs(n.x) > 0.7f ? (n.x > 0 ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft) : TextAnchor.MiddleCenter);
        }

        private static void Draw(Texture2D tex, Vector2 center, float w, float h, Color c, float alpha, Vector2 offset = default)
        {
            if (alpha <= 0.001f) return;
            c.a = alpha;
            GUI.color = c;
            GUI.DrawTexture(new Rect(center.x - w / 2f + offset.x, center.y - h / 2f + offset.y, w, h), tex, ScaleMode.StretchToFill, true);
        }

        private static void Label(Rect r, string text, Color col, float alpha, float k, TextAnchor align = TextAnchor.MiddleCenter)
        {
            label.fontSize = Mathf.Max(13, Mathf.RoundToInt(20f * k));
            label.alignment = align;
            GUI.color = new Color(0f, 0f, 0f, 0.75f * alpha);
            foreach (var d in new[] { new Vector2(-1, 0), new Vector2(1, 0), new Vector2(0, -1), new Vector2(0, 1), new Vector2(1.5f, 1.5f) })
                GUI.Label(new Rect(r.x + d.x * k * 1.4f, r.y + d.y * k * 1.4f, r.width, r.height), text, label);
            GUI.color = new Color(Mathf.Lerp(col.r, 1f, 0.35f), Mathf.Lerp(col.g, 1f, 0.35f), Mathf.Lerp(col.b, 1f, 0.35f), alpha);
            GUI.Label(r, text, label);
        }

        // ---------------- assets ----------------

        private static void EnsureAssets()
        {
            if (ring != null && label != null) return;
            ring = Make(128, (x, y) => { float d = Mathf.Sqrt(x * x + y * y); return Mathf.Clamp01(1f - Mathf.Abs(d - 0.86f) / 0.1f); });
            thinRing = Make(256, (x, y) => { float d = Mathf.Sqrt(x * x + y * y); return Mathf.Clamp01(1f - Mathf.Abs(d - 0.93f) / 0.035f); }, supersample: 2);
            disc = Make(64, (x, y) => Mathf.Sqrt(x * x + y * y) <= 0.92f ? 1f : 0f);
            glow = Make(128, (x, y) => { float d = Mathf.Clamp01(Mathf.Sqrt(x * x + y * y)); return (1f - d) * (1f - d); }, supersample: 1);
            // A rounded arrowhead pointing right (+x).
            arrow = Make(64, (x, y) => x > -0.75f && x < 0.85f && Mathf.Abs(y) < (0.85f - x) * 0.62f && Mathf.Abs(y) < 0.95f ? 1f : 0f);
            label = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow, wordWrap = false };
            var font = GameFont();
            if (font != null) label.font = font;
        }

        // alpha(x, y) with x, y in -1..1; supersampled for smooth edges.
        private static Texture2D Make(int size, Func<float, float, float> alpha, int supersample = 4)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[size * size];
            for (int j = 0; j < size; j++)
                for (int i = 0; i < size; i++)
                {
                    float sum = 0f;
                    for (int sj = 0; sj < supersample; sj++)
                        for (int si = 0; si < supersample; si++)
                        {
                            float x = ((i + (si + 0.5f) / supersample) / size) * 2f - 1f;
                            float y = ((j + (sj + 0.5f) / supersample) / size) * 2f - 1f;
                            sum += alpha(x, y);
                        }
                    px[j * size + i] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(sum / (supersample * supersample)) * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        // The game's own UI font when it can draw IMGUI text (a dynamic font), else Unity's default.
        private static Font GameFont()
        {
            try
            {
                // the font most of the UI uses
                return UnityEngine.Object.FindObjectsByType<TMPro.TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Select(t => t.font != null ? t.font.sourceFontFile : null)
                    .Where(f => f != null && f.dynamic && f.HasCharacter('A'))
                    .GroupBy(f => f).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            }
            catch (Exception e) { Plugin.Log.LogInfo("[ping] Game font not usable: " + e.Message); }
            return null;
        }

        // ---------------- sound ----------------

        private static ButtonSoundsSO sounds;
        private static float lastSound;

        // The game's own UI click (Wwise), at most a few times a second.
        public static void PlaySound()
        {
            if (Time.realtimeSinceStartup - lastSound < 0.25f) return;
            lastSound = Time.realtimeSinceStartup;
            try
            {
                if (sounds == null) sounds = Resources.FindObjectsOfTypeAll<ButtonSoundsSO>().FirstOrDefault(s => s != null && s.onPress != null && s.onPress.IsValid());
                var me = Game.LocalPlayer;
                if (sounds != null && me != null) sounds.onPress.Post(me.gameObject);
            }
            catch (Exception e) { Plugin.Log.LogInfo("[ping] No sound: " + e.Message); sounds = null; }
        }
    }
}
