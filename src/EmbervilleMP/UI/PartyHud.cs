using System;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleMP
{
    // The party HUD: a card per teammate at the top left (name, level, health, Kymia; down, or somewhere else), arrows at
    // the screen's edge towards teammates out of sight, teammates on the minimap and the world map (M).
    // Built from the game's own HUD pieces (its health and Kymia bars, heart, font) and placed inside its HUD, so it
    // fades out whenever the game hides its HUD (cutscenes, dialogue, menus, the map).
    internal class PartyHud
    {
        public static PartyHud Instance { get; private set; }

        // One colour per player id, from the game's own palette (gold highlight, map teal, mini-boss lilac, coral).
        private static readonly Color[] Accents =
        {
            new Color32(250, 185, 84, 255), new Color32(52, 235, 193, 255), new Color32(176, 139, 255, 255), new Color32(255, 128, 112, 255),
        };
        public static Color AccentOf(byte id) => Accents[(Math.Max(1, (int)id) - 1) % Accents.Length];

        internal class Card
        {
            public byte Id;
            public RectTransform Root;
            public CanvasGroup Group, Bars;
            public Label Name, Tag;
            public SmoothSlideBar Health, Kymia;
            public GameObject HealthText, KymiaText;
            public TextMeshProUGUI HealthNumbers, KymiaNumbers;
            public Image Heart;
            public CanvasGroup NameGroup;
            public readonly List<KeyValuePair<Graphic, Material>> Greyable = new List<KeyValuePair<Graphic, Material>>();
            public bool Grey;
            public float ShownHealth = -1f, ShownKymia = -1f;
            public string State = "";     // here / area / elsewhere / away / down (tests read it)
        }

        internal class Arrow
        {
            public RectTransform Root, Pointer;
            public Image PointerImage;
            public Label Text;
            public CanvasGroup Group;
        }

        // A TextMeshPro line in the game's font with a 1-pixel drop shadow (readable over any background).
        internal class Label
        {
            public RectTransform Root;
            public TextMeshProUGUI Main, Shadow;
            public string Text
            {
                get => Main.text;
                set { if (Main.text != value) { Main.text = value; Shadow.text = StripColors(value); } }
            }
            public Color Color { set => Main.color = value; }
        }

        public bool Visible { get; private set; }
        public WorldMapParty WorldMap => worldMap;
        public float CardsX => cardsX;
        public readonly List<Card> Cards = new List<Card>();
        public readonly Dictionary<byte, Arrow> Arrows = new Dictionary<byte, Arrow>();
        public readonly Dictionary<byte, SpriteRenderer> MinimapMarkers = new Dictionary<byte, SpriteRenderer>();
        public readonly Dictionary<byte, GameObject> WorldMapMarkers = new Dictionary<byte, GameObject>();
        public readonly Dictionary<byte, Label> NameTags = new Dictionary<byte, Label>();

        private UIManager ui;
        private Canvas canvas;
        private CanvasGroup hudContent;
        private int builtForScene = -1;
        private RectTransform root, cardsHolder, arrowsHolder;
        private CanvasGroup rootGroup;
        private GameObject healthTemplate, kymiaTemplate, heartTemplate;
        private TextMeshProUGUI textTemplate;
        private Sprite arrowSprite;
        private Material greyMaterial;
        private Camera minimapCam;
        private SpriteRenderer minimapCenter;
        private WorldMapParty worldMap;

        public PartyHud() { Instance = this; }

        // ---------------- every frame ----------------

        public void Update()
        {
            var s = Session.Current;
            var presence = MPRunner.Instance.Presence;
            bool wanted = Plugin.Settings.PartyHud.Value && s != null && s.Ready && presence != null && Game.InGame && s.Players.Count > 1;
            if (wanted) EnsureBuilt();   // (a death or teleport isn't "in game" for a moment: same scene, same HUD)
            if (root == null) { Visible = false; return; }

            Visible = wanted && !GameHudHidden();
            float target = Visible ? 1f : 0f;
            rootGroup.alpha = Mathf.MoveTowards(rootGroup.alpha, target, Time.unscaledDeltaTime * 6f);
            if (!wanted)
            {
                foreach (var m in MinimapMarkers.Values) if (m != null) m.gameObject.SetActive(false);
                worldMap?.Update(null, null);
                return;
            }

            // Same order on every screen: by player id.
            var others = s.Others.OrderBy(p => p.Id).ToList();
            SyncCards(others);
            PlaceCards();
            bool numbers = ui.healthBarLabel != null && ui.healthBarLabel.gameObject.activeSelf;   // the game's "show numbers"
            for (int i = 0; i < others.Count; i++)
                UpdateCard(Cards[i], others[i], presence, numbers);
            worldMap?.Update(s, presence);
        }

        // Positions (arrows, minimap) after the camera has moved this frame.
        public void LateUpdate()
        {
            var s = Session.Current;
            var presence = MPRunner.Instance?.Presence;
            if (root == null || s == null || presence == null || !Game.InGame) return;
            var cam = Camera.main;
            var seen = new HashSet<byte>();
            foreach (var p in s.Others)
            {
                presence.Remotes.TryGetValue(p.Id, out var rp);
                var st = rp?.Latest;
                bool sameScene = st != null && st.Scene.Length > 0 && st.Scene == Game.SceneName;
                if (!sameScene || !Plugin.Settings.PartyHud.Value) continue;
                seen.Add(p.Id);
                Vector2 pos = rp.Shown ? (Vector2)rp.Puppet.transform.position : st.Position;
                PlaceArrow(p, pos, st.Area == Game.AreaName, cam);
                PlaceMinimapMarker(p.Id, pos);
            }
            foreach (var kv in Arrows) if (!seen.Contains(kv.Key) && kv.Value.Root.gameObject.activeSelf) kv.Value.Root.gameObject.SetActive(false);
            PlaceNameTags(s, presence, cam);
            foreach (var kv in MinimapMarkers) if (!seen.Contains(kv.Key) && kv.Value != null && kv.Value.gameObject.activeSelf) kv.Value.gameObject.SetActive(false);
        }

        // Name tags over the other players, in the game's font (Presence draws plain ones when the party HUD is off).
        public bool DrawsNameTags => root != null && Plugin.Settings.PartyHud.Value && Session.Current != null && Session.Current.Ready;

        private void PlaceNameTags(Session s, Presence presence, Camera cam)
        {
            foreach (var p in s.Others)
            {
                presence.Remotes.TryGetValue(p.Id, out var rp);
                if (!NameTags.TryGetValue(p.Id, out var tag))
                {
                    NameTags[p.Id] = tag = NewLabel("MP_NameTag_" + p.Id, arrowsHolder, 20f, AccentOf(p.Id), TextAlignmentOptions.Bottom);
                    tag.Root.anchorMin = tag.Root.anchorMax = new Vector2(0.5f, 0.5f);
                    tag.Root.pivot = new Vector2(0.5f, 0f);
                    tag.Root.sizeDelta = new Vector2(240f, 26f);
                }
                bool show = DrawsNameTags && rp != null && rp.Shown && cam != null && canvas != null;
                Vector2 local = default;
                if (show)
                {
                    Vector3 sp = cam.WorldToScreenPoint(rp.HeadPosition);
                    show = sp.z > 0f && RectTransformUtility.ScreenPointToLocalPointInRectangle(root, sp, canvas.worldCamera, out local);
                }
                if (tag.Root.gameObject.activeSelf != show) tag.Root.gameObject.SetActive(show);
                if (!show) continue;
                tag.Root.anchoredPosition = local;
                bool talking = Steam.Voice.Instance != null && Steam.Voice.Instance.IsSpeaking(p.Id);
                tag.Text = talking ? "(( " + p.Name + " ))" : p.Name;
            }
            foreach (var kv in NameTags)
                if (!s.Players.ContainsKey(kv.Key) && kv.Value.Root.gameObject.activeSelf) kv.Value.Root.gameObject.SetActive(false);
        }

        private bool GameHudHidden()
        {
            if (ui.hideHudStack.Count > 0 || !ui.panelPlayerHud.activeInHierarchy) return true;
            if (hudContent != null && hudContent.alpha < 0.05f) return true;
            var gm = Game.Manager;
            if (gm != null && (gm.gameState == GameState.Cutscene || gm.gameState == GameState.Gameover || gm.gameState == GameState.Loading)) return true;
            return Singleton<WorldMap>.HasMain && Singleton<WorldMap>.main.uiObject != null && Singleton<WorldMap>.main.uiObject.activeSelf;
        }

        // ---------------- cards ----------------

        private void SyncCards(List<PlayerInfo> others)
        {
            bool same = Cards.Count == others.Count;
            for (int i = 0; same && i < others.Count; i++) same = Cards[i].Id == others[i].Id;
            if (same) return;
            foreach (var c in Cards) UnityEngine.Object.Destroy(c.Root.gameObject);
            Cards.Clear();
            for (int i = 0; i < others.Count; i++)
            {
                var c = BuildCard(others[i].Id);
                c.Root.anchoredPosition = new Vector2(0f, -i * CardStep);
                Cards.Add(c);
            }
        }

        // Top left; to the right of the game's clock when it shows (towns, the hub).
        private float cardsX = 20f, nextClockCheck;
        private readonly Vector3[] corners = new Vector3[4];

        private void PlaceCards()
        {
            if (Time.unscaledTime < nextClockCheck) return;
            nextClockCheck = Time.unscaledTime + 0.5f;
            float x = 20f;
            if (Singleton<UIClock>.HasMain && Singleton<UIClock>.main.gameObject.activeInHierarchy)
            {
                // The clock's pictures at the top left (its own rect isn't its visible size): our cards go right of them.
                var clock = Singleton<UIClock>.main;
                var clockCanvas = clock.GetComponentInParent<Canvas>()?.rootCanvas;
                Camera cc = clockCanvas == null || clockCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : clockCanvas.worldCamera;
                float right = -1f;
                foreach (var g in clock.GetComponentsInChildren<Graphic>())
                {
                    if (!g.enabled || g.color.a < 0.05f) continue;
                    g.rectTransform.GetWorldCorners(corners);
                    Vector2 bl = RectTransformUtility.WorldToScreenPoint(cc, corners[0]), tr = RectTransformUtility.WorldToScreenPoint(cc, corners[2]);
                    if (bl.x < Screen.width * 0.3f && tr.x < Screen.width * 0.4f && tr.y > Screen.height * 0.6f) right = Mathf.Max(right, tr.x);
                }
                if (right > 0f && RectTransformUtility.ScreenPointToLocalPointInRectangle(root, new Vector2(right, Screen.height * 0.9f), canvas.worldCamera, out var local))
                    x = local.x - root.rect.xMin + 14f;
            }
            if (Mathf.Abs(x - cardsX) > 0.5f) { cardsX = x; cardsHolder.anchoredPosition = new Vector2(x, -20f); }
        }
        private void UpdateCard(Card c, PlayerInfo p, Presence presence, bool numbers)
        {
            presence.Remotes.TryGetValue(p.Id, out var rp);
            var st = rp?.Latest;
            PartyStatus.Vitals v = null;
            PartyStatus.Instance?.Players.TryGetValue(p.Id, out v);

            string level = v != null && v.Level > 0 ? $"  <size=16><color=#BDB6C8>Lv {v.Level}</color></size>" : "";
            c.Name.Text = p.Name + level;

            string state, tag;
            if (st == null || st.Scene.Length == 0 || rp.SecondsSinceState > 6f) { state = "away"; tag = "<color=#A9A3B5>travelling...</color>"; }
            else if (st.Dead) { state = "down"; tag = "<color=#FF5A4A>DOWN</color>"; }
            else if (st.Scene != Game.SceneName) { state = "elsewhere"; tag = "<color=#A9A3B5>in " + PlaceName(st.Scene) + "</color>"; }
            else if (st.Area != Game.AreaName) { state = "area"; tag = ""; }
            else { state = "here"; tag = ""; }
            c.State = state;
            c.Tag.Text = tag;
            // The tag gets what the name leaves of the line (a long place name shrinks rather than overlapping).
            float free = Mathf.Max(40f, BarWidth - 2f - c.Name.Main.preferredWidth - 10f);
            if (Mathf.Abs(c.Tag.Root.sizeDelta.x - free) > 0.5f)
            {
                c.Tag.Root.sizeDelta = new Vector2(free, 24f);
                c.Tag.Root.anchoredPosition = new Vector2(BarsX + BarWidth - free, 0f);
            }
            // Somewhere else or down: the game's own black-and-white look (as for characters not selected), name faded.
            // (Not transparency: the bars' layers would show through each other.)
            c.NameGroup.alpha = state == "elsewhere" || state == "away" ? 0.6f : 1f;
            SetGrey(c, state == "elsewhere" || state == "away" || state == "down");

            float hp = v != null ? v.HealthFraction : 1f, ky = v != null ? v.KymiaFraction : 1f;
            if (state == "down") hp = 0f;
            SetBar(c.Health, ref c.ShownHealth, hp);
            SetBar(c.Kymia, ref c.ShownKymia, ky);
            if (c.HealthText != null && c.HealthText.activeSelf != (numbers && v != null)) c.HealthText.SetActive(numbers && v != null);
            if (c.KymiaText != null && c.KymiaText.activeSelf != (numbers && v != null)) c.KymiaText.SetActive(numbers && v != null);
            if (numbers && v != null)
            {
                if (c.HealthNumbers != null) c.HealthNumbers.text = $"{v.Health} / {v.MaxHealth}";
                if (c.KymiaNumbers != null) c.KymiaNumbers.text = $"{v.Kymia} / {v.MaxKymia}";
            }
        }

        private void SetGrey(Card c, bool grey)
        {
            if (c.Grey == grey) return;
            c.Grey = grey;
            if (greyMaterial == null) greyMaterial = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m => m.name == "BlackAndWhite_Mat");
            foreach (var kv in c.Greyable)
            {
                if (kv.Key == null) continue;
                if (greyMaterial != null) kv.Key.material = grey ? greyMaterial : kv.Value;
                else kv.Key.color = grey ? new Color(0.45f, 0.45f, 0.5f, kv.Key.color.a) : Color.white;
            }
        }

        private static void SetBar(SmoothSlideBar bar, ref float shown, float value)
        {
            if (bar == null || Mathf.Abs(shown - value) < 0.001f) return;
            // The game's bar: the fill jumps, the lighter "damage" part behind it drains after (healing: both at once).
            if (shown < 0f || value > shown) bar.SetInitialState(value);
            bar.SetValue(value);
            shown = value;
        }

        // Card layout, in the HUD's 1920x1080 units.
        private const float CardStep = 76f, BarsX = 44f, BarWidth = 246f, BarHeight = 18f;

        private Card BuildCard(byte id)
        {
            var c = new Card { Id = id };
            c.Root = NewRect("MP_Card_" + id, cardsHolder);
            c.Root.anchorMin = c.Root.anchorMax = c.Root.pivot = new Vector2(0f, 1f);
            c.Root.sizeDelta = new Vector2(BarsX + BarWidth, CardStep - 10f);
            c.Group = c.Root.gameObject.AddComponent<CanvasGroup>();
            c.Group.blocksRaycasts = false;

            if (heartTemplate != null)
            {
                var heart = Clone(heartTemplate, c.Root);
                var rt = (RectTransform)heart.transform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(2f, -14f);
                rt.localScale = new Vector3(0.5f, 0.5f, 1f);
                foreach (var a in heart.GetComponentsInChildren<Animator>(true)) a.enabled = false;   // its beat follows *our* health
                c.Heart = heart.transform.Find("Heart")?.GetComponent<Image>() ?? heart.GetComponent<Image>();
            }

            c.Name = NewLabel("Name", c.Root, 22f, AccentOf(id), TextAlignmentOptions.BottomLeft);
            Place(c.Name.Root, BarsX + 2f, 2f, BarWidth - 70f, 26f);
            c.NameGroup = c.Name.Root.gameObject.AddComponent<CanvasGroup>();
            c.Tag = NewLabel("Tag", c.Root, 16f, Color.white, TextAlignmentOptions.BottomRight);
            foreach (var t in new[] { c.Tag.Main, c.Tag.Shadow }) { t.enableAutoSizing = true; t.fontSizeMin = 11f; t.fontSizeMax = 16f; t.overflowMode = TextOverflowModes.Ellipsis; }
            Place(c.Tag.Root, BarsX + BarWidth - 130f, 0f, 128f, 24f);

            var bars = NewRect("Bars", c.Root);
            bars.anchorMin = bars.anchorMax = bars.pivot = new Vector2(0f, 1f);
            bars.anchoredPosition = new Vector2(BarsX, -26f);
            bars.sizeDelta = new Vector2(BarWidth, BarHeight * 2f + 2f);
            c.Bars = bars.gameObject.AddComponent<CanvasGroup>();
            c.Health = BuildBar(healthTemplate, bars, 0f, out c.HealthText, out c.HealthNumbers);
            c.Kymia = BuildBar(kymiaTemplate, bars, -(BarHeight + 2f), out c.KymiaText, out c.KymiaNumbers);
            foreach (var g in c.Root.GetComponentsInChildren<Image>(true))
                c.Greyable.Add(new KeyValuePair<Graphic, Material>(g, g.material == g.defaultMaterial ? null : g.material));
            return c;
        }

        private SmoothSlideBar BuildBar(GameObject template, RectTransform parent, float y, out GameObject text, out TextMeshProUGUI numbers)
        {
            text = null; numbers = null;
            if (template == null) return null;
            var slot = NewRect(template.name + "Slot", parent);
            slot.anchorMin = slot.anchorMax = slot.pivot = new Vector2(0f, 1f);
            slot.anchoredPosition = new Vector2(0f, y);
            slot.sizeDelta = new Vector2(BarWidth, BarHeight);
            var bar = Clone(template, slot);
            var rt = (RectTransform)bar.transform;
            // Half size, centred in its slot (the Kymia bar is the health bar's mirror image: keep its own rotation).
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.localScale = new Vector3(0.5f, 0.5f, 1f);
            // The fill leaves room for the heart (health) or the gem (Kymia) at one end; a teammate's bar is full width.
            foreach (RectTransform child in bar.transform)
                if (child.anchorMin == Vector2.zero && child.anchorMax == Vector2.one && child.sizeDelta.x < -20f)
                {
                    child.offsetMin = new Vector2(8f, child.offsetMin.y);
                    child.offsetMax = new Vector2(-8f, child.offsetMax.y);
                }
            var smooth = bar.GetComponent<SmoothSlideBar>();
            // Its change event drives effects of *our* HUD (Kymia sparkle): not for a teammate's bar.
            if (smooth != null) smooth.onChangeEvent = new UnityEngine.Events.UnityEvent<float>();
            foreach (var t in bar.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                text = t.gameObject;
                numbers = t;
                t.fontSize = 30f;   // half-size bar: the game's 20 would be unreadable
                text.SetActive(false);
                break;
            }
            return smooth;
        }

        // ---------------- arrows ----------------

        private void PlaceArrow(PlayerInfo p, Vector2 world, bool sameArea, Camera cam)
        {
            if (!Arrows.TryGetValue(p.Id, out var a)) Arrows[p.Id] = a = BuildArrow(p.Id);
            if (cam == null || canvas == null) { a.Root.gameObject.SetActive(false); return; }
            Vector3 sp = cam.WorldToScreenPoint(new Vector3(world.x, world.y + 0.7f, 0f));
            bool behind = sp.z < 0f;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root, sp, canvas.worldCamera, out var local)) { a.Root.gameObject.SetActive(false); return; }
            if (behind) local = -local;
            Rect r = root.rect;
            bool onScreen = !behind && local.x > r.xMin + 8f && local.x < r.xMax - 8f && local.y > r.yMin + 8f && local.y < r.yMax - 8f;
            if (onScreen) { if (a.Root.gameObject.activeSelf) a.Root.gameObject.SetActive(false); return; }

            // Clamp onto a frame inside the screen: above the game's bottom HUD, clear of the minimap and the cards.
            var frame = Rect.MinMaxRect(r.xMin + 46f, r.yMin + 214f, r.xMax - 46f, r.yMax - 46f);
            Vector2 center = frame.center;
            Vector2 dir = local - center;
            if (dir.sqrMagnitude < 1f) dir = Vector2.up;
            float tx = dir.x > 0 ? (frame.xMax - center.x) / dir.x : dir.x < 0 ? (frame.xMin - center.x) / dir.x : float.MaxValue;
            float ty = dir.y > 0 ? (frame.yMax - center.y) / dir.y : dir.y < 0 ? (frame.yMin - center.y) / dir.y : float.MaxValue;
            Vector2 pos = center + dir * Mathf.Min(tx, ty);
            pos = AvoidCorner(pos, frame, new Rect(r.xMax - 350f, r.yMax - 330f, 350f, 330f));                           // minimap
            pos = AvoidCorner(pos, frame, new Rect(r.xMin, r.yMax - 30f - Math.Max(1, Cards.Count) * CardStep, cardsX + 320f, 30f + Math.Max(1, Cards.Count) * CardStep)); // cards

            a.Root.anchoredPosition = pos;
            a.Pointer.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
            // The label sits on the inner side of the arrow.
            Vector2 inward = (center - pos).normalized;
            a.Text.Root.anchoredPosition = inward * 34f;
            a.Text.Main.alignment = a.Text.Shadow.alignment = inward.x > 0.4f ? TextAlignmentOptions.Left : inward.x < -0.4f ? TextAlignmentOptions.Right : TextAlignmentOptions.Center;
            a.Text.Root.pivot = new Vector2(inward.x > 0.4f ? 0f : inward.x < -0.4f ? 1f : 0.5f, inward.y > 0.4f ? 0f : inward.y < -0.4f ? 1f : 0.5f);
            var me = Game.LocalPlayer;
            float dist = me != null ? Vector2.Distance(me.transform.position, world) : 0f;
            a.Text.Text = $"{p.Name}  <size=15><color=#D8D2E0>{dist:F0} m</color></size>";
            a.Group.alpha = sameArea ? 1f : 0.7f;
            if (!a.Root.gameObject.activeSelf) a.Root.gameObject.SetActive(true);
        }

        // An arrow that would land in a corner taken by other UI slides along its edge out of it.
        private static Vector2 AvoidCorner(Vector2 pos, Rect frame, Rect corner)
        {
            if (!corner.Contains(pos)) return pos;
            bool onSide = Mathf.Abs(pos.x - frame.xMin) < 1f || Mathf.Abs(pos.x - frame.xMax) < 1f;
            if (onSide) pos.y = corner.yMin - 20f;
            else pos.x = corner.center.x < frame.center.x ? corner.xMax + 20f : corner.xMin - 20f;
            return pos;
        }

        private Arrow BuildArrow(byte id)
        {
            var a = new Arrow();
            a.Root = NewRect("MP_Arrow_" + id, arrowsHolder);
            a.Root.anchorMin = a.Root.anchorMax = new Vector2(0.5f, 0.5f);
            a.Root.sizeDelta = new Vector2(40f, 40f);
            a.Group = a.Root.gameObject.AddComponent<CanvasGroup>();
            a.Group.blocksRaycasts = false;
            a.Pointer = NewRect("Pointer", a.Root);
            a.Pointer.sizeDelta = new Vector2(ArrowW * 2f, ArrowH * 2f);
            a.PointerImage = a.Pointer.gameObject.AddComponent<Image>();
            a.PointerImage.sprite = arrowSprite;
            a.PointerImage.color = AccentOf(id);
            a.PointerImage.raycastTarget = false;
            a.Text = NewLabel("Label", a.Root, 18f, AccentOf(id), TextAlignmentOptions.Center);
            a.Text.Root.anchorMin = a.Text.Root.anchorMax = new Vector2(0.5f, 0.5f);
            a.Text.Root.sizeDelta = new Vector2(220f, 24f);
            a.Root.gameObject.SetActive(false);
            return a;
        }

        // A pixel-art dart (pointing right, notched at the back) in the HUD's style: light fill, dark outline, a darker
        // lower half. Tinted with the player's colour.
        private const int ArrowW = 17, ArrowH = 13;
        private static Sprite MakeArrowSprite()
        {
            var tex = new Texture2D(ArrowW, ArrowH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[ArrowW * ArrowH];
            Func<int, int, bool> fill = (x, y) =>
            {
                int d = Math.Abs(y - 6);
                return d <= 5 && x >= 2 + (5 - d) && x <= 14 - 2 * d;
            };
            var outline = new Color32(38, 22, 30, 255);
            for (int y = 0; y < ArrowH; y++)
                for (int x = 0; x < ArrowW; x++)
                {
                    Color32 c = new Color32(0, 0, 0, 0);
                    if (fill(x, y)) c = y < 6 ? new Color32(200, 200, 200, 255) : new Color32(255, 255, 255, 255);
                    else
                    {
                        for (int dy = -1; dy <= 1 && c.a == 0; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                                if (fill(x + dx, y + dy)) { c = outline; break; }
                    }
                    px[y * ArrowW + x] = c;
                }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, ArrowW, ArrowH), new Vector2(0.5f, 0.5f), 1f);
        }
        // ---------------- minimap ----------------

        private void PlaceMinimapMarker(byte id, Vector2 world)
        {
            if (minimapCam == null)
            {
                var go = GameObject.FindWithTag("MinimapCam");
                minimapCam = go != null ? go.GetComponent<Camera>() : null;
            }
            if (minimapCenter == null)
                minimapCenter = Game.LocalPlayer != null ? Game.LocalPlayer.transform.Find("MinimapCenter")?.GetComponent<SpriteRenderer>() : null;
            if (minimapCam == null || minimapCenter == null) return;
            if (!MinimapMarkers.TryGetValue(id, out var sr) || sr == null)
            {
                var go = new GameObject("MP_MinimapMarker_" + id) { layer = minimapCenter.gameObject.layer };
                sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = minimapCenter.sprite;
                sr.sharedMaterial = minimapCenter.sharedMaterial;
                sr.sortingLayerID = minimapCenter.sortingLayerID;
                sr.sortingOrder = minimapCenter.sortingOrder + 1;
                sr.color = AccentOf(id);
                go.transform.localScale = minimapCenter.transform.lossyScale;
                go.transform.rotation = Quaternion.Euler(0f, 0f, 45f);   // a diamond: ours is the square
                MinimapMarkers[id] = sr;
            }
            // Out of the minimap's view: pinned to its edge, a little smaller.
            Vector2 c = minimapCam.transform.position;
            float h = minimapCam.orthographicSize, w = h * minimapCam.aspect;
            float edge = sr.bounds.extents.x + 0.3f;
            var clamped = new Vector2(Mathf.Clamp(world.x, c.x - w + edge, c.x + w - edge), Mathf.Clamp(world.y, c.y - h + edge, c.y + h - edge));
            bool pinned = (clamped - world).sqrMagnitude > 0.01f;
            sr.transform.position = new Vector3(clamped.x, clamped.y, minimapCenter.transform.position.z);
            sr.transform.localScale = minimapCenter.transform.lossyScale * (pinned ? 0.75f : 1f);
            if (!sr.gameObject.activeSelf) sr.gameObject.SetActive(true);
        }

        // ---------------- building ----------------

        private void EnsureBuilt()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (builtForScene == scene && root != null) return;
            ui = UnityEngine.Object.FindAnyObjectByType<UIManager>();
            if (ui == null || ui.panelPlayerHud == null || ui.healthBar == null) return;
            try { Build(); }
            catch (Exception e) { Plugin.Log.LogError("[hud] Couldn't build the party HUD: " + e); }
            builtForScene = scene;
        }

        private void Build()
        {
            Cards.Clear(); Arrows.Clear(); MinimapMarkers.Clear(); WorldMapMarkers.Clear(); NameTags.Clear();
            minimapCam = null; minimapCenter = null;
            var content = ui.panelPlayerHud.transform.Find("Content") ?? ui.panelPlayerHud.transform;
            hudContent = content.GetComponent<CanvasGroup>();
            canvas = content.GetComponentInParent<Canvas>();
            root = NewRect("MP_PartyHud", content);
            root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one;
            root.offsetMin = root.offsetMax = Vector2.zero;
            rootGroup = root.gameObject.AddComponent<CanvasGroup>();
            rootGroup.blocksRaycasts = false; rootGroup.interactable = false; rootGroup.alpha = 0f;
            cardsHolder = NewRect("Cards", root);
            cardsHolder.anchorMin = cardsHolder.anchorMax = cardsHolder.pivot = new Vector2(0f, 1f);
            cardsHolder.anchoredPosition = new Vector2(20f, -20f);
            cardsHolder.sizeDelta = new Vector2(320f, 320f);
            arrowsHolder = NewRect("Arrows", root);
            arrowsHolder.anchorMin = arrowsHolder.anchorMax = new Vector2(0.5f, 0.5f);
            arrowsHolder.sizeDelta = Vector2.zero;

            // Templates: the game's own bars, heart and font (kept inactive, cloned per card).
            var templates = NewRect("Templates", root);
            templates.gameObject.SetActive(false);
            healthTemplate = Clone(ui.healthBar.gameObject, templates);
            kymiaTemplate = ui.kymiaBar != null ? Clone(ui.kymiaBar.gameObject, templates) : null;
            var heart = ui.healthBar.transform.parent.Find("HeartHolder");
            heartTemplate = heart != null ? Clone(heart.gameObject, templates) : null;
            textTemplate = ui.healthBarLabel;
            if (arrowSprite == null) arrowSprite = MakeArrowSprite();
            worldMap = new WorldMapParty(this);
            Plugin.Log.LogInfo($"[hud] Party HUD built (bars {(healthTemplate != null)}/{(kymiaTemplate != null)}, heart {(heartTemplate != null)})");
        }

        internal Label NewLabel(string name, Transform parent, float size, Color color, TextAlignmentOptions align)
        {
            var l = new Label { Root = NewRect(name, parent) };
            l.Shadow = NewText("Shadow", l.Root, size, new Color(0.07f, 0.04f, 0.08f, 0.85f), align);
            ((RectTransform)l.Shadow.transform).offsetMin = new Vector2(2f, -2f);
            ((RectTransform)l.Shadow.transform).offsetMax = new Vector2(2f, -2f);
            l.Main = NewText("Text", l.Root, size, color, align);
            l.Main.richText = true;
            return l;
        }

        private TextMeshProUGUI NewText(string name, RectTransform parent, float size, Color color, TextAlignmentOptions align)
        {
            TextMeshProUGUI t;
            if (textTemplate != null)
            {
                var go = Clone(textTemplate.gameObject, parent);
                foreach (Transform child in go.transform) UnityEngine.Object.Destroy(child.gameObject);
                t = go.GetComponent<TextMeshProUGUI>();
            }
            else t = NewRect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
            t.gameObject.name = name;
            t.gameObject.SetActive(true);
            var rt = (RectTransform)t.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            t.fontSize = size;
            t.enableAutoSizing = false;
            t.color = color;
            t.alignment = align;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.raycastTarget = false;
            t.text = "";
            return t;
        }

        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }

        internal static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        // A copy of a piece of the game's UI that runs none of its scripts' Awake, without its localization hooks.
        internal static GameObject Clone(GameObject original, Transform parent)
        {
            var temp = new GameObject("MP_CloneHolder");
            temp.SetActive(false);
            var copy = UnityEngine.Object.Instantiate(original, temp.transform);
            foreach (var c in copy.GetComponentsInChildren<UnityEngine.Localization.Components.LocalizeStringEvent>(true))
                UnityEngine.Object.DestroyImmediate(c);
            copy.SetActive(true);
            copy.transform.SetParent(parent, false);
            UnityEngine.Object.Destroy(temp);
            return copy;
        }

        private static string StripColors(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s ?? "", "<color=[^>]*>|</color>", "");

        // Where a player is, for "in ...": the scene, in words.
        public static string PlaceName(string scene)
        {
            switch (scene)
            {
                case "VitromotusCrystal": return "Vitromotus Crystal";
                case "DungeonScene": return "the Vitromotus";
                case "CartographerInterior": return "the Cartographer's";
                case "": case null: return "a loading screen";
            }
            return System.Text.RegularExpressions.Regex.Replace(scene, "(?<=[a-z])(?=[A-Z])", " ").Replace('_', ' ');
        }
    }
}
