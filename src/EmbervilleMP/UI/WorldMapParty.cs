using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbervilleMP
{
    // The party on the world map (M): a "Party" box in the map's own style (who, level, where), and, in the generated
    // dungeon (the only place the map shows players), each teammate's marker next to ours.
    internal class WorldMapParty
    {
        public readonly List<string> Lines = new List<string>();   // what the box says (tests)
        public GameObject Box => box != null ? box.gameObject : null;

        private const string DungeonScene = "DungeonScene";
        private readonly PartyHud hud;
        private WorldMap map;
        private RectTransform box;
        private readonly List<PartyHud.Label> labels = new List<PartyHud.Label>();
        private float nextRefresh;

        public WorldMapParty(PartyHud hud) { this.hud = hud; }

        public void Update(Session s, Presence presence)
        {
            if (!Singleton<WorldMap>.HasMain) return;
            var wm = Singleton<WorldMap>.main;
            if (wm != map)
            {
                map = wm;
                try { Build(); }
                catch (System.Exception e) { Plugin.Log.LogError("[hud] Couldn't add the party to the world map: " + e); box = null; }
            }
            if (box == null) return;
            bool show = s != null && presence != null && Plugin.Settings.PartyHud.Value && s.Others.Any();
            if (box.gameObject.activeSelf != show) box.gameObject.SetActive(show);
            if (!show) { HideMarkers(null); return; }
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.25f;

            var others = s.Others.OrderBy(p => p.Id).ToList();
            while (labels.Count < others.Count)
            {
                var l = hud.NewLabel("MP_PartyLine", box, 20f, Color.white, TextAlignmentOptions.Left);
                l.Root.anchorMin = l.Root.anchorMax = l.Root.pivot = new Vector2(0f, 1f);
                l.Root.sizeDelta = new Vector2(310f, 26f);
                labels.Add(l);
            }
            Lines.Clear();
            for (int i = 0; i < labels.Count; i++)
            {
                bool used = i < others.Count;
                if (labels[i].Root.gameObject.activeSelf != used) labels[i].Root.gameObject.SetActive(used);
                if (!used) continue;
                var p = others[i];
                presence.Remotes.TryGetValue(p.Id, out var rp);
                var st = rp?.Latest;
                PartyStatus.Vitals v = null;
                PartyStatus.Instance?.Players.TryGetValue(p.Id, out v);
                string where = st == null || st.Scene.Length == 0 ? "travelling" : st.Dead ? "down" : st.Scene == Game.SceneName ? "with you" : PartyHud.PlaceName(st.Scene);
                string line = $"<color=#{ColorUtility.ToHtmlStringRGB(PartyHud.AccentOf(p.Id))}>{p.Name}</color>" +
                              $"<size=16><color=#BDB6C8>{(v != null && v.Level > 0 ? $"  Lv {v.Level}" : "")} · {where}</color></size>";
                labels[i].Text = line;
                labels[i].Root.anchoredPosition = new Vector2(24f, -46f - i * 30f);
                Lines.Add($"{p.Name}|{where}");
            }
            box.sizeDelta = new Vector2(box.sizeDelta.x, 58f + others.Count * 30f);
            UpdateMarkers(others, presence);
        }

        private void Build()
        {
            box = null;
            labels.Clear();
            var container = map.worldMapContainer;
            var canvas = container != null ? container.transform.Find("Canvas") : null;
            if (canvas == null) return;
            var regionInfo = canvas.Find("RegionInfo");
            box = PartyHud.NewRect("MP_PartyBox", canvas);
            box.anchorMin = box.anchorMax = box.pivot = Vector2.zero;
            box.anchoredPosition = new Vector2(7f, 112f);   // above the map's bottom-left buttons, under the zone info
            box.sizeDelta = new Vector2(352f, 88f);
            var bgSource = regionInfo != null ? regionInfo.GetComponent<Image>() : null;
            var bg = box.gameObject.AddComponent<Image>();
            if (bgSource != null) { bg.sprite = bgSource.sprite; bg.type = bgSource.type; bg.color = bgSource.color; bg.pixelsPerUnitMultiplier = bgSource.pixelsPerUnitMultiplier; }
            else bg.color = new Color(0.12f, 0.08f, 0.13f, 0.9f);
            bg.raycastTarget = false;
            // The section header of the zone info ("Common Enemies"), saying "Party".
            var headerSource = regionInfo != null ? regionInfo.Find("Enemies/Header") : null;
            if (headerSource != null)
            {
                var header = PartyHud.Clone(headerSource.gameObject, box);
                var rt = (RectTransform)header.transform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -10f);
                foreach (var t in header.GetComponentsInChildren<TextMeshProUGUI>(true)) t.text = "Party";
            }
            box.SetAsLastSibling();
            Plugin.Log.LogInfo("[hud] Party added to the world map");
        }

        // In the generated dungeon the map shows where we are; teammates there get a copy of that marker in their colour.
        private void UpdateMarkers(List<PlayerInfo> others, Presence presence)
        {
            var container = map.worldMapContainer;
            bool mapOpen = map.uiObject != null && map.uiObject.activeSelf;
            var preview = container != null ? container.playerPreview : null;
            var keep = new HashSet<byte>();
            if (mapOpen && preview != null && map.WorldMapState.IsInDungeon.Value)
                foreach (var p in others)
                {
                    presence.Remotes.TryGetValue(p.Id, out var rp);
                    var st = rp?.Latest;
                    if (st == null || st.Scene != DungeonScene) continue;
                    keep.Add(p.Id);
                    if (!hud.WorldMapMarkers.TryGetValue(p.Id, out var m) || m == null)
                    {
                        m = Object.Instantiate(preview, preview.transform.parent);
                        m.name = "MP_MapMarker_" + p.Id;
                        foreach (var g in m.GetComponentsInChildren<Graphic>(true)) g.color = PartyHud.AccentOf(p.Id);
                        foreach (var sr in m.GetComponentsInChildren<SpriteRenderer>(true)) sr.color = PartyHud.AccentOf(p.Id);
                        hud.WorldMapMarkers[p.Id] = m;
                    }
                    m.SetActive(true);
                    m.transform.localPosition = (Vector3)(st.Position / 10f * 6f);   // the map's own world-to-map scale
                    preview.transform.SetAsLastSibling();                             // ours on top
                }
            HideMarkers(keep);
        }

        private void HideMarkers(HashSet<byte> keep)
        {
            foreach (var kv in hud.WorldMapMarkers)
                if (kv.Value != null && (keep == null || !keep.Contains(kv.Key)) && kv.Value.activeSelf) kv.Value.SetActive(false);
        }
    }
}
