using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EmbervilleMP.Net;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Pings: the ping key (middle mouse by default) marks the spot under the cursor for everyone in the same area.
    // Pings elsewhere become a chat notice. Markers are drawn by PingMarkers.
    internal class Pings
    {
        public const float Lifetime = 4.2f;
        private const int PerPlayer = 3;            // markers shown per player (a new one replaces the oldest)
        private const float BurstWindow = 4f;       // rate limit: Burst pings per BurstWindow seconds,
        private const int Burst = 3;                // at least MinGap apart
        private const float MinGap = 0.35f;

        public class Ping
        {
            public byte From;
            public string Name;
            public string Scene, Area;
            public Vector2 Position;
            public float Time;      // realtime it arrived
            public bool Local;
            public float Age => UnityEngine.Time.realtimeSinceStartup - Time;
        }

        public static Pings Instance { get; private set; }

        public readonly List<Ping> Active = new List<Ping>();
        public Ping LastReceived { get; private set; }
        public int Sent, Received, Elsewhere, Dropped, Limited;

        private readonly Session session;
        private readonly Queue<float> mine = new Queue<float>();
        private readonly Dictionary<byte, Queue<float>> theirs = new Dictionary<byte, Queue<float>>();
        private readonly Dictionary<byte, float> lastNotice = new Dictionary<byte, float>();

        public Pings(Session session)
        {
            Instance = this;
            this.session = session;
            session.On(Msg.Ping, OnPing);
            session.PlayerLeft += p => Active.RemoveAll(x => x.From == p.Id);
        }

        // Player colours, by player id (the host is ember orange). Shared with anything that colours players.
        private static readonly Color[] palette =
        {
            new Color(1.00f, 0.62f, 0.22f),   // #1 ember
            new Color(0.36f, 0.92f, 0.82f),   // #2 teal (the menus' accent)
            new Color(0.74f, 0.56f, 1.00f),   // #3 violet
            new Color(1.00f, 0.47f, 0.62f),   // #4 rose
        };

        public static Color ColorOf(byte id) => palette[(Mathf.Max(1, (int)id) - 1) % palette.Length];

        public void Update()
        {
            if (Session.Current != session) return;
            // Markers belong to the area they were made in.
            Active.RemoveAll(p => p.Age > Lifetime || p.Scene != Game.SceneName || p.Area != Game.AreaName);

            var key = Plugin.Settings.PingKey.Value;
            if (key == KeyCode.None || !Input.GetKeyDown(key) || !CanPingNow()) return;
            if (CursorToWorld(Input.mousePosition, out var at)) TryPing(at);
        }

        // Not in menus, dialogue, cutscenes, our chat line or the F8 window.
        private static bool CanPingNow()
        {
            var gm = Game.Manager;
            if (!Game.InGame || gm.gameState != GameState.Playing) return false;
            if (UIPanels.HasOpenedPanel || GUIUtility.hotControl != 0) return false;
            var runner = MPRunner.Instance;
            return runner == null || (!runner.ChatBox.Typing && !runner.Window.Open);
        }

        // The point on the ground (the player's plane) under a screen position.
        public static bool CursorToWorld(Vector3 screen, out Vector2 world)
        {
            world = default;
            var cam = Camera.main;
            var me = Game.LocalPlayer;
            if (cam == null || me == null) return false;
            var plane = new Plane(Vector3.forward, new Vector3(0f, 0f, me.transform.position.z));
            var ray = cam.ScreenPointToRay(screen);
            if (!plane.Raycast(ray, out float d)) return false;
            world = ray.GetPoint(d);
            return true;
        }

        // Pings a spot in our area: shown here and sent to everyone. False when rate-limited.
        public bool TryPing(Vector2 at)
        {
            if (!session.Ready || !Game.InGame) return false;
            float now = Time.realtimeSinceStartup;
            while (mine.Count > 0 && now - mine.Peek() > BurstWindow) mine.Dequeue();
            if (mine.Count >= Burst || (mine.Count > 0 && now - mine.Last() < MinGap)) { Limited++; return false; }
            mine.Enqueue(now);
            string scene = Game.SceneName, area = Game.AreaName;
            session.Send(Msg.Ping, w => { w.Put(scene); w.Put(area); w.Put(at.x); w.Put(at.y); });
            Sent++;
            Show(new Ping { From = session.LocalId, Name = session.LocalName, Scene = scene, Area = area, Position = at, Time = now, Local = true });
            return true;
        }

        private void OnPing(byte from, NetDataReader r)
        {
            string scene = r.GetString(), area = r.GetString();
            var pos = new Vector2(r.GetFloat(), r.GetFloat());
            float now = Time.realtimeSinceStartup;
            // A modified or runaway sender can't flood the screen.
            if (!theirs.TryGetValue(from, out var q)) theirs[from] = q = new Queue<float>();
            while (q.Count > 0 && now - q.Peek() > BurstWindow) q.Dequeue();
            if (q.Count >= Burst + 2) { Dropped++; return; }
            q.Enqueue(now);
            string name = session.Players.TryGetValue(from, out var p) ? p.Name : "#" + from;
            var ping = new Ping { From = from, Name = name, Scene = scene, Area = area, Position = pos, Time = now };
            Received++;
            LastReceived = ping;
            Plugin.Log.LogInfo($"[ping] {name} pinged {scene}/{area} at ({pos.x:F2}, {pos.y:F2})");
            if (Game.InGame && scene == Game.SceneName && area == Game.AreaName) { Show(ping); return; }
            Elsewhere++;
            // Elsewhere: one notice per player every few seconds.
            if (lastNotice.TryGetValue(from, out float last) && now - last < 3f) return;
            lastNotice[from] = now;
            Chat.Notice(scene == Game.SceneName ? $"{name} pinged in {PrettyPlace(area)}" : $"{name} pinged in {PrettyPlace(scene)}");
        }

        private void Show(Ping ping)
        {
            var same = Active.Where(x => x.From == ping.From).ToList();
            if (same.Count >= PerPlayer) Active.Remove(same[0]);
            Active.Add(ping);
            PingMarkers.PlaySound();
        }

        // "003_Vitromotus" -> "Vitromotus", "TutorialArea" -> "Tutorial Area", "=== GameArea_Vitromotus " -> "Vitromotus".
        public static string PrettyPlace(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "another area";
            string s = raw.Replace("GameArea_", "").Trim(' ', '=', '_');
            s = Regex.Replace(s, @"^\d+_", "");
            s = s.Replace('_', ' ');
            s = Regex.Replace(s, @"(?<=[a-z])(?=[A-Z])", " ");
            s = Regex.Replace(s, @"\s+\d+$", "");
            return s.Trim().Length > 0 ? s.Trim() : raw;
        }
    }
}
