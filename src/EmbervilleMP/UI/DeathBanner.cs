using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EmbervilleMP.Sync;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleMP
{
    // A short ribbon near the top of the screen ("Guesty has fallen!" / what killed them), in the game's own font
    // (copied from its boss bar). Falls back to IMGUI if no game font can be found. Over everything, death screen included.
    internal static class DeathBanner
    {
        public static string LastTitle = "", LastSubtitle = "";
        public static int Shown;

        private class Item { public string Title, Subtitle; public Color Colour; public float Seconds; }
        private static readonly Queue<Item> queue = new Queue<Item>();
        private static Item current;
        private static float started;

        private static GameObject root;
        private static CanvasGroup group;
        private static RectTransform panel;
        private static TextMeshProUGUI title, subtitle;
        private static RawImage lineTop, lineBottom, skullLeft, skullRight;
        private static bool tmpTried, tmpOk;

        public static void Show(string titleText, string subtitleText, Color colour, float seconds)
        {
            LastTitle = titleText ?? "";
            LastSubtitle = subtitleText ?? "";
            queue.Enqueue(new Item { Title = LastTitle, Subtitle = LastSubtitle, Colour = colour, Seconds = seconds });
            while (queue.Count > 3) queue.Dequeue();
            // A newer one cuts a long one short.
            if (current != null && Time.realtimeSinceStartup - started > 1.5f) current.Seconds = Mathf.Min(current.Seconds, Time.realtimeSinceStartup - started + 0.4f);
        }

        private static float Alpha(float t, float seconds) => Mathf.Clamp01(t / 0.2f) * Mathf.Clamp01((seconds - t) / 0.6f);

        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (current != null && now - started > current.Seconds) current = null;
            if (current == null && queue.Count > 0)
            {
                current = queue.Dequeue();
                started = now;
                Shown++;
                if (!tmpTried) Build();
                if (tmpOk) Apply(current);
            }
            if (!tmpOk || root == null) return;
            root.SetActive(current != null);
            if (current == null) return;
            float t = now - started;
            group.alpha = Alpha(t, current.Seconds);
            float punch = 1f + 0.15f * Mathf.Clamp01(1f - t / 0.25f);   // lands with a little thump
            title.rectTransform.localScale = new Vector3(punch, punch, 1f);
        }

        private static void Build()
        {
            tmpTried = true;
            if (DevHarness.Headless) return;
            try
            {
                var src = FindGameText();
                if (src == null || src.font == null) { Plugin.Log.LogInfo("[death] No game font found; banner drawn with IMGUI"); return; }
                root = new GameObject("MP_DeathBanner");
                Object.DontDestroyOnLoad(root);
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32000;
                var scaler = root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 1f;
                group = root.AddComponent<CanvasGroup>();
                group.blocksRaycasts = false;
                group.interactable = false;

                panel = NewRect("Ribbon", root.transform);
                panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 1f);
                panel.pivot = new Vector2(0.5f, 0.5f);
                panel.anchoredPosition = new Vector2(0, -215);
                panel.sizeDelta = new Vector2(1200, 150);
                var back = panel.gameObject.AddComponent<RawImage>();
                back.texture = SystemLine.Ribbon;
                back.color = new Color(0.05f, 0.02f, 0.02f, 0.78f);
                back.raycastTarget = false;
                lineTop = Line(panel, 1f);
                lineBottom = Line(panel, 0f);

                title = Text("Title", panel, src, 60, new Vector2(0, 22));
                subtitle = Text("Subtitle", panel, src, 30, new Vector2(0, -36));
                subtitle.color = new Color(0.96f, 0.91f, 0.86f);
                skullLeft = Skull(panel);
                skullRight = Skull(panel);
                root.SetActive(false);
                tmpOk = true;
                Plugin.Log.LogInfo($"[death] Banner uses the game's font {src.font.name}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[death] Banner (TextMeshPro) failed, using IMGUI: " + e.Message);
                if (root != null) Object.Destroy(root);
                root = null;
                tmpOk = false;
            }
        }

        private static TextMeshProUGUI FindGameText()
        {
            var ui = Object.FindAnyObjectByType<UIManager>(FindObjectsInactive.Include);
            if (ui != null)
                foreach (var t in new[] { ui.bossName, ui.levelUpLabel, ui.miniBossName, ui.healthBarLabel })
                    if (t != null && t.font != null) return t;
            return Object.FindObjectsByType<TextMeshProUGUI>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => t.font != null && !t.font.name.StartsWith("LiberationSans")).OrderByDescending(t => t.fontSize).FirstOrDefault();
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static RawImage Line(RectTransform parent, float y)
        {
            var r = NewRect("Line", parent);
            r.anchorMin = new Vector2(0f, y);
            r.anchorMax = new Vector2(1f, y);
            r.sizeDelta = new Vector2(0, 3);
            r.anchoredPosition = Vector2.zero;
            var img = r.gameObject.AddComponent<RawImage>();
            img.texture = SystemLine.Ribbon;
            img.raycastTarget = false;
            return img;
        }

        private static TextMeshProUGUI Text(string name, RectTransform parent, TextMeshProUGUI src, float size, Vector2 pos)
        {
            var r = NewRect(name, parent);
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(1600, size * 1.4f);
            r.anchoredPosition = pos;
            var t = r.gameObject.AddComponent<TextMeshProUGUI>();
            t.font = src.font;
            t.fontSharedMaterial = src.fontSharedMaterial;
            t.fontSize = size;
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.richText = true;
            t.raycastTarget = false;
            return t;
        }

        private static RawImage Skull(RectTransform parent)
        {
            var r = NewRect("Skull", parent);
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(46, 46);
            var img = r.gameObject.AddComponent<RawImage>();
            img.texture = SystemLine.SkullTexture;
            img.raycastTarget = false;
            return img;
        }

        private static void Apply(Item item)
        {
            title.text = item.Title;
            title.color = item.Colour;
            subtitle.text = item.Subtitle;
            subtitle.gameObject.SetActive(!string.IsNullOrEmpty(item.Subtitle));
            title.rectTransform.anchoredPosition = new Vector2(0, string.IsNullOrEmpty(item.Subtitle) ? 0 : 22);
            var faded = new Color(item.Colour.r, item.Colour.g, item.Colour.b, 0.9f);
            lineTop.color = lineBottom.color = faded;
            float half = Mathf.Min(title.GetPreferredValues(item.Title).x * 0.5f, 520f);
            float y = title.rectTransform.anchoredPosition.y;
            skullLeft.rectTransform.anchoredPosition = new Vector2(-half - 44, y);
            skullRight.rectTransform.anchoredPosition = new Vector2(half + 44, y);
            skullLeft.color = skullRight.color = item.Colour;
        }

        private static GUIStyle titleStyle, subStyle;

        // IMGUI fallback (no game font found).
        public static void OnGUI()
        {
            if (current == null || tmpOk || DevHarness.Headless) return;
            if (titleStyle == null)
            {
                titleStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, richText = true };
                subStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, richText = true };
            }
            titleStyle.fontSize = Mathf.Max(22, Screen.height / 20);
            subStyle.fontSize = Mathf.Max(14, Screen.height / 38);
            float a = Alpha(Time.realtimeSinceStartup - started, current.Seconds);
            float w = Mathf.Min(Screen.width * 0.7f, Screen.height * 1.15f), h = Screen.height * 0.14f;
            var r = new Rect((Screen.width - w) / 2f, Screen.height * 0.2f - h / 2f, w, h);
            var old = GUI.color;
            GUI.color = new Color(0.05f, 0.02f, 0.02f, 0.78f * a);
            GUI.DrawTexture(r, SystemLine.Ribbon);
            GUI.color = new Color(current.Colour.r, current.Colour.g, current.Colour.b, 0.9f * a);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 3), SystemLine.Ribbon);
            GUI.DrawTexture(new Rect(r.x, r.yMax - 3, r.width, 3), SystemLine.Ribbon);
            bool sub = !string.IsNullOrEmpty(current.Subtitle);
            var tr = new Rect(r.x, r.y + (sub ? h * 0.08f : 0f), r.width, sub ? h * 0.5f : h);
            float tw = titleStyle.CalcSize(new GUIContent(current.Title)).x;
            float icon = titleStyle.fontSize * 0.9f;
            GUI.DrawTexture(new Rect(r.center.x - tw / 2f - icon - 16, tr.center.y - icon / 2f, icon, icon), SystemLine.SkullTexture);
            GUI.DrawTexture(new Rect(r.center.x + tw / 2f + 16, tr.center.y - icon / 2f, icon, icon), SystemLine.SkullTexture);
            GUI.color = new Color(1, 1, 1, a);
            SystemLine.ShadowLabel(tr, $"<color=#{ColorUtility.ToHtmlStringRGB(current.Colour)}>{current.Title}</color>", titleStyle);
            if (sub) SystemLine.ShadowLabel(new Rect(r.x, r.y + h * 0.55f, r.width, h * 0.35f), current.Subtitle, subStyle);
            GUI.color = old;
        }
    }

    // Chat lines from the game itself (Chat.AddSystem): no sender, a dark backing strip, a coloured edge, and a skull
    // for deaths (text starting with Skull).
    internal static class SystemLine
    {
        public const string Skull = "☠";
        private static Texture2D skull, ribbon, strip;
        private static GUIStyle style, shadow;
        private static readonly Regex colourTag = new Regex("</?color[^>]*>");
        private static readonly Regex firstColour = new Regex("<color=#([0-9A-Fa-f]{6})");

        // 9x10 pixel skull, drawn point-filtered.
        public static Texture2D SkullTexture
        {
            get
            {
                if (skull != null) return skull;
                string[] rows =
                {
                    "..#####..",
                    ".#######.",
                    "#########",
                    "#..###..#",
                    "#..###..#",
                    "#########",
                    ".###.###.",
                    "..#####..",
                    "..#.#.#..",
                    "..#####..",
                };
                skull = new Texture2D(9, 10, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                for (int y = 0; y < 10; y++)
                    for (int x = 0; x < 9; x++)
                        skull.SetPixel(x, 9 - y, rows[y][x] == '#' ? Color.white : Color.clear);
                skull.Apply();
                return skull;
            }
        }

        // White, fading out at both ends (tinted by whoever draws it).
        public static Texture2D Ribbon => ribbon != null ? ribbon : ribbon = Gradient(64, x => Mathf.Clamp01(Mathf.Min(x, 1f - x) * 6f));
        // White, fading out to the right.
        private static Texture2D Strip => strip != null ? strip : strip = Gradient(64, x => Mathf.Clamp01((1f - x) * 1.6f));

        private static Texture2D Gradient(int width, System.Func<float, float> alpha)
        {
            var t = new Texture2D(width, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            for (int x = 0; x < width; x++) t.SetPixel(x, 0, new Color(1, 1, 1, alpha(x / (width - 1f))));
            t.Apply();
            return t;
        }

        // A label with a dark drop shadow (colour tags stripped from the shadow so it stays dark).
        public static void ShadowLabel(Rect r, string text, GUIStyle s)
        {
            if (shadow == null || shadow.fontSize != s.fontSize || shadow.alignment != s.alignment)
                shadow = new GUIStyle(s) { normal = { textColor = new Color(0, 0, 0, 0.85f) } };
            shadow.fontStyle = s.fontStyle;
            string plain = colourTag.Replace(text, "");
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), plain, shadow);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), plain, shadow);
            GUI.Label(r, text, s);
        }

        public static void Draw(Rect r, string text)
        {
            if (style == null) style = new GUIStyle(MPStyle.Hud) { richText = true, normal = { textColor = Color.white } };
            style.fontSize = MPStyle.Hud.fontSize + 1;
            var m = firstColour.Match(text);
            Color tint = Color.white;
            if (m.Success) ColorUtility.TryParseHtmlString("#" + m.Groups[1].Value, out tint);
            bool skullMark = Deaths.Plain(text).StartsWith(Skull);
            if (skullMark)
            {
                int k = text.IndexOf(Skull);
                text = text.Remove(k, text.Length > k + 1 && text[k + 1] == ' ' ? 2 : 1);
            }
            float iconSize = style.fontSize + 2;
            float textX = r.x + (skullMark ? iconSize + 8 : 4);
            float w = style.CalcSize(new GUIContent(Deaths.Plain(text))).x + (textX - r.x) + 50;
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f * old.a);
            GUI.DrawTexture(new Rect(r.x - 6, r.y + 1, w, r.height - 2), Strip);
            GUI.color = new Color(tint.r, tint.g, tint.b, old.a);
            GUI.DrawTexture(new Rect(r.x - 6, r.y + 1, 3, r.height - 2), Texture2D.whiteTexture);
            if (skullMark)
            {
                var ir = new Rect(r.x + 2, r.y + (r.height - iconSize) / 2f, iconSize, iconSize);
                GUI.color = new Color(0, 0, 0, 0.8f * old.a);
                GUI.DrawTexture(new Rect(ir.x + 1, ir.y + 1, ir.width, ir.height), SkullTexture);
                GUI.color = new Color(tint.r, tint.g, tint.b, old.a);
                GUI.DrawTexture(ir, SkullTexture);
            }
            GUI.color = old;
            ShadowLabel(new Rect(textX, r.y, r.width - (textX - r.x), r.height), text, style);
        }
    }
}
