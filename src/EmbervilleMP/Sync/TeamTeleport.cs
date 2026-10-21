using System.Linq;
using Cysharp.Threading.Tasks;
using EmbervilleMP.Net;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Teleport next to a teammate (pause menu > MULTIPLAYER, or /tp <name>): the game's own fade teleport in the same
    // scene, the game's scene change to another one. Only out of combat, not dead, with a short cooldown, and only if
    // the host allows it ([Coop] AllowTeleportToTeammates, sent to guests as CoopRules).
    internal class TeamTeleport
    {
        public const float Cooldown = 8f;
        public const float HurtSeconds = 5f;      // took damage this recently: in combat
        public const float EnemyRadius = 7f;      // an enemy fighting this close: in combat

        public static TeamTeleport Instance { get; private set; }
        // The host's rule (guests: as the host sent it; until then, on).
        public static bool Allowed { get; private set; } = true;
        private static Session rulesFor;

        public int Teleports, Refusals, RulesReceived;
        public string LastMessage { get; private set; } = "";
        public bool Busy => pending != 0 || travelling;
        public float CooldownLeft => Mathf.Max(0f, readyAt - Time.realtimeSinceStartup);

        private readonly Session session;
        private byte pending;
        private float pendingSince, readyAt, lastHurt = -100f;
        private bool travelling;
        private int lastHealth = -1;
        private PlayerMaster healthOf;

        public TeamTeleport(Session session)
        {
            Instance = this;
            this.session = session;
            // (Rebuilt on the same session after a world swap: guests keep the rule they had, the host sends it again.)
            bool sameSession = rulesFor == session;
            rulesFor = session;
            if (session.IsHost)
            {
                Allowed = Plugin.Settings.AllowTeleport.Value;
                session.PlayerJoined += p => SendRules(p.Id);
                if (sameSession) session.Send(Msg.CoopRules, WriteRules);
            }
            else
            {
                if (!sameSession) Allowed = true;
                session.On(Msg.CoopRules, OnRules);
            }
            session.Ended += () => { if (Instance == this) Allowed = true; };
        }

        // ---------------- the host's rule ----------------

        public static string SettingLabel => "Teleport to teammates: " + (Plugin.Settings.AllowTeleport.Value ? "allowed" : "off");

        // Menus: before hosting it's the setting; while hosting it changes the session's rule too.
        public static void CycleSetting()
        {
            var e = Plugin.Settings.AllowTeleport;
            e.Value = !e.Value;
            Plugin.Settings.Save();
            var s = Session.Current;
            if (s != null && s.IsHost && Instance != null && Instance.session == s) Instance.SetAllowed(e.Value);
        }

        // Host: change the rule for everyone now.
        public void SetAllowed(bool allowed)
        {
            if (!session.IsHost) return;
            Allowed = allowed;
            session.Send(Msg.CoopRules, WriteRules);
            Plugin.Log.LogInfo($"[tp] Teleport to teammates: {(allowed ? "allowed" : "off")}");
        }

        // Host: the rule to every guest again (tests: each chained scenario starts from the host's setting).
        public void ResendRules() { if (session.IsHost) { Allowed = Plugin.Settings.AllowTeleport.Value; session.Send(Msg.CoopRules, WriteRules); } }

        private void SendRules(byte id) => session.SendTo(id, Msg.CoopRules, WriteRules);

        private static void WriteRules(NetDataWriter w) => w.Put((byte)(Allowed ? 1 : 0));

        private void OnRules(byte from, NetDataReader r)
        {
            if (from != Protocol.HostId) return;
            bool was = Allowed;
            Allowed = (r.GetByte() & 1) != 0;
            RulesReceived++;
            Plugin.Log.LogInfo($"[tp] Host's rule: teleport to teammates {(Allowed ? "allowed" : "off")}");
            if (was != Allowed && Game.InGame) Chat.Notice(Allowed ? "The host allowed teleporting to teammates." : "The host turned teleporting to teammates off.");
        }

        // ---------------- requests ----------------

        // Asks to teleport to a player. True: on its way (it starts as soon as the game is unpaused). False: why not.
        public bool Request(byte id, out string why)
        {
            if (!CanGo(id, out var target, out why))
            {
                Refusals++;
                LastMessage = why;
                Plugin.Log.LogInfo("[tp] Refused: " + why);
                return false;
            }
            pending = id;
            pendingSince = Time.realtimeSinceStartup;
            why = $"Teleporting to {target.Info.Name}...";
            LastMessage = why;
            Plugin.Log.LogInfo("[tp] " + why);
            return true;
        }

        // Every rule, checked when asked and again when it happens.
        private bool CanGo(byte id, out RemotePlayer target, out string why)
        {
            target = null;
            why = null;
            var presence = MPRunner.Instance?.Presence;
            if (Session.Current != session || !session.Ready || presence == null) { why = "Not in a multiplayer game."; return false; }
            if (!Allowed) { why = "The host has turned teleporting to teammates off."; return false; }
            if (id == session.LocalId) { why = "That's you."; return false; }
            if (!presence.Remotes.TryGetValue(id, out target) || target.Latest == null) { why = "That player isn't here (yet)."; return false; }
            string name = target.Info.Name;
            var st = target.Latest;
            if (target.SecondsSinceState > 3f || string.IsNullOrEmpty(st.Scene) || st.Scene == "MainMenu") { why = $"{name} is between areas; try again in a moment."; return false; }
            if (st.Dead) { why = $"{name} is down; wait until they're back up."; return false; }
            if (!st.Visible) { why = $"{name} is busy (cutscene or doorway); try again in a moment."; return false; }
            var me = Game.LocalPlayer;
            if (!Game.InGame || me == null || travelling || IsLoading) { why = "Wait until you've arrived."; return false; }
            if (me.IsDead) { why = "You can't teleport while you're down."; return false; }
            if (CooldownLeft > 0f) { why = $"Teleport is recharging ({Mathf.CeilToInt(CooldownLeft)} s)."; return false; }
            if (InCombat(me)) { why = "You can't teleport in combat."; return false; }
            return true;
        }

        private static bool IsLoading => Singleton<SceneLoader>.HasMain && Singleton<SceneLoader>.main.isLoading || GuestWorld.Switching;

        // Hurt a moment ago, or an enemy is fighting us or close by (EnemyCombat: the runner's word on puppets).
        public bool InCombat(PlayerMaster me)
        {
            if (Time.realtimeSinceStartup - lastHurt < HurtSeconds) return true;
            return EnemyCombat.Fighting(me, skipInvulnerable: true);
        }

        public void Update()
        {
            if (Session.Current != session) return;
            TrackHurt();
            if (travelling)
            {
                // Arrived: the fade is over (same scene) or the new scene is up.
                var me = Game.LocalPlayer;
                if (Game.InGame && !IsLoading && Game.SceneName == travelScene && me != null && !me.Static
                    && Game.Manager.gameState == GameState.Playing && Time.realtimeSinceStartup - travelStarted > 0.5f)
                {
                    travelling = false;
                    readyAt = Time.realtimeSinceStartup + Cooldown;
                    Plugin.Log.LogInfo($"[tp] Arrived at {Game.SceneName}/{Game.AreaName} {(Vector2)Game.LocalPlayer.transform.position}");
                }
                else if (Time.realtimeSinceStartup - travelStarted > 120f) travelling = false;
                return;
            }
            if (pending == 0) return;
            if (Time.realtimeSinceStartup - pendingSince > 6f)
            {
                pending = 0;
                Plugin.Log.LogInfo($"[tp] Cancelled: the game didn't resume (state {Game.Manager?.gameState}, panel open: {UIPanels.HasOpenedPanel})");
                Chat.Notice("Teleport cancelled.");
                return;
            }
            // Out of the pause menu first (the fade and the scene change need the game running).
            if (!Game.InGame || Game.Manager.gameState != GameState.Playing || UIPanels.HasOpenedPanel) return;
            byte id = pending;
            pending = 0;
            if (!CanGo(id, out var target, out string why))
            {
                Refusals++;
                LastMessage = why;
                Plugin.Log.LogInfo("[tp] Refused on the way: " + why);
                Chat.Notice(why);
                return;
            }
            Go(target);
        }

        private float travelStarted;
        private string travelScene;

        private void Go(RemotePlayer target)
        {
            var st = target.Latest;
            Vector2 to = st.Position;
            travelling = true;
            travelStarted = Time.realtimeSinceStartup;
            travelScene = st.Scene;
            Teleports++;
            if (st.Scene == Game.SceneName)
            {
                var area = Object.FindObjectsByType<GameArea>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(a => a.name == st.Area);
                // (another room's walls aren't active yet: there, right on their spot)
                Vector2 spot = area == null || st.Area == Game.AreaName ? FreeSpotNear(to) : to;
                var dir = Mathf.Abs(to.x - spot.x) >= Mathf.Abs(to.y - spot.y) ? (to.x >= spot.x ? Direction.Right : Direction.Left) : (to.y >= spot.y ? Direction.Up : Direction.Down);
                Plugin.Log.LogInfo($"[tp] To {target.Info.Name} in {st.Area} at {spot}");
                Game.Manager.TeleportPlayerFade(new Vector3(spot.x, spot.y, Game.LocalPlayer.transform.position.z), dir, area, area != null ? area.CameraConfiner : null).Forget();
            }
            else
            {
                Plugin.Log.LogInfo($"[tp] To {target.Info.Name} in {st.Scene}/{st.Area} at {to}");
                GuestWorld.Travel(st.Scene, st.Area, new Vector3(to.x, to.y, 0f), newWorld: false);
            }
        }

        // Beside the teammate rather than inside them, unless a wall is in the way.
        private static Vector2 FreeSpotNear(Vector2 to)
        {
            var me = Game.LocalPlayer;
            foreach (var off in new[] { new Vector2(-0.9f, 0f), new Vector2(0.9f, 0f), new Vector2(0f, -0.9f), new Vector2(0f, 0.9f) })
            {
                var spot = to + off;
                bool blocked = Physics2D.LinecastAll(to, spot).Any(h => Solid(h.collider, me))
                    || Physics2D.OverlapCircleAll(spot, 0.3f).Any(c => Solid(c, me));
                if (!blocked) return spot;
            }
            return to;
        }

        private static bool Solid(Collider2D c, PlayerMaster me) =>
            c != null && !c.isTrigger && (c.attachedRigidbody == null || c.attachedRigidbody.bodyType == RigidbodyType2D.Static)
            && (me == null || !c.transform.IsChildOf(me.transform));

        private void TrackHurt()
        {
            var me = Game.LocalPlayer;
            if (me == null || !Game.InGame) { lastHealth = -1; return; }
            if (me != healthOf) { healthOf = me; lastHealth = -1; }
            int hp = me.CurrentHealthPoints;
            if (lastHealth >= 0 && hp < lastHealth) lastHurt = Time.realtimeSinceStartup;
            lastHealth = hp;
        }

        // Players by (part of a) name: exact first, then the start of a name, then anywhere in it.
        public PlayerInfo[] FindPlayers(string query)
        {
            var others = session.Others.ToArray();
            query = (query ?? "").Trim();
            if (query.Length == 0) return others;
            var exact = others.Where(p => string.Equals(p.Name, query, System.StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length > 0) return exact;
            var starts = others.Where(p => p.Name != null && p.Name.StartsWith(query, System.StringComparison.OrdinalIgnoreCase)).ToArray();
            if (starts.Length > 0) return starts;
            return others.Where(p => p.Name != null && p.Name.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        }
    }
}
