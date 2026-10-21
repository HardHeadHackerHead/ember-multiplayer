using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EmbervilleMP.Net;
using EmbervilleMP.Sync.Objects;
using LiteNetLib.Utils;

namespace EmbervilleMP.Sync
{
    internal enum LootMode : byte { Instanced, Shared }

    // The host's loot setting (config [Coop] LootMode, or --mp-loot=shared|instanced), sent to every guest when it's
    // welcomed. What follows the mode:
    //   - Instanced (default): every player has their own loot. Enemy loot already is (each copy runs the death);
    //     chests roll per player (see ChestSync), drops stay local. The host remembers, per player, what each guest
    //     left in the chests it opened (side file next to its save), so rejoining doesn't refill them. Items a player
    //     throws away from its inventory are the exception: shared drops (WorldDrops), for a teammate to pick up.
    //   - Shared: one set of loot. Chests are the scene runner's (ChestSync); drops are spawned by the runner and
    //     picked up through it (WorldDrops).
    // Gathering nodes are shared in both modes (GatheringSync); their drops follow the mode. Storage chests (the world's
    // stash) and key items are the host's world's in both modes (StashSync, KeyItemSync).
    internal class LootRules
    {
        public static LootRules Instance { get; private set; }
        private static Session lastSession;
        public static LootMode Mode { get; private set; } = LootMode.Instanced;
        public static bool Shared => Mode == LootMode.Shared && Session.Current != null && Session.Current.Ready;
        // A guest playing in the host's world with per-player chests.
        public static bool InstancedGuest => Mode == LootMode.Instanced && Session.Current != null && !Session.Current.IsHost && GuestWorld.Active;

        public readonly WorldDrops Drops;
        public int RecordsSent, RecordsReceived;

        private readonly Session session;
        // host: player name -> chest key -> what that player left in it (item JSON)
        private readonly Dictionary<string, Dictionary<string, string>> records = new Dictionary<string, Dictionary<string, string>>();
        // guest: our own records (from the host at welcome, then kept up to date here)
        private readonly Dictionary<string, string> mine = new Dictionary<string, string>();

        public LootRules(Session session)
        {
            Instance = this;
            this.session = session;
            if (session.IsHost)
            {
                Mode = HostMode();
                Plugin.Log.LogInfo($"[loot] Loot mode: {Mode}");
                LoadRecords();
                session.PlayerJoined += SendRules;
                session.On(Msg.LootChestRecord, OnRecord);
                // Rebuilt on the same session (chained tests: a fresh world): tell the guests again.
                if (lastSession == session) foreach (var p in session.Others) SendRules(p);
            }
            else
            {
                // A new session starts instanced until the host says; rebuilt on the same one, the host's word stands.
                if (lastSession != session) Mode = LootMode.Instanced;
                session.On(Msg.LootRules, OnRules);
            }
            lastSession = session;
            session.Ended += () => { if (Instance == this) Mode = LootMode.Instanced; };
            Drops = new WorldDrops(session);
        }

        // Host settings (menus): the loot mode a new session starts with.
        public static string SettingLabel => "Loot: " + (Plugin.Settings.LootMode.Value == LootMode.Shared ? "shared (first to grab)" : "each player their own");

        public static void CycleSetting()
        {
            var m = Plugin.Settings.LootMode;
            m.Value = m.Value == LootMode.Shared ? LootMode.Instanced : LootMode.Shared;
            Plugin.Settings.Save();
        }

        private static LootMode HostMode()
        {
            string arg = Args.Get("loot");
            if (arg != null) return arg.StartsWith("s", StringComparison.OrdinalIgnoreCase) ? LootMode.Shared : LootMode.Instanced;
            return Plugin.Settings.LootMode.Value;
        }

        public void Update()
        {
            GatheringSync.Prune();
            Drops.Update();
        }

        // ---------------- host -> guest: the rules ----------------

        // Host: the rules to every guest again (tests: each chained scenario may run with other ones).
        public void ResendRules() { if (!session.IsHost) return; Mode = HostMode(); foreach (var p in session.Others) SendRules(p); }

        private void SendRules(PlayerInfo p)
        {
            records.TryGetValue(p.Name ?? "", out var theirs);
            session.SendTo(p.Id, Msg.LootRules, w =>
            {
                w.Put((byte)Mode);
                w.Put(theirs?.Count ?? 0);
                if (theirs != null)
                    foreach (var kv in theirs) { w.Put(kv.Key); w.Put(kv.Value); }
            });
        }

        private void OnRules(byte from, NetDataReader r)
        {
            if (from != Protocol.HostId) return;
            Mode = (LootMode)r.GetByte();
            int n = r.GetInt();
            mine.Clear();
            for (int i = 0; i < n; i++) mine[r.GetString()] = r.GetString();
            Plugin.Log.LogInfo($"[loot] Host's loot mode: {Mode} ({n} chest(s) already looted here)");
        }

        // ---------------- instanced chests: per player records ----------------

        public static string ChestKey(Chest c) => $"{c.gameObject.scene.name}:{NetIds.Hash(NetIds.ScenePath(c.transform))}";

        public bool TryGetRecord(Chest c, out string json) => mine.TryGetValue(ChestKey(c), out json);

        // Guest: our player opened or closed a chest; the host keeps what's left in it for us.
        public void RecordChest(Chest c, string json)
        {
            string key = ChestKey(c);
            mine[key] = json;
            session.Send(Msg.LootChestRecord, w => { w.Put(key); w.Put(json); });
            RecordsSent++;
        }

        private void OnRecord(byte from, NetDataReader r)
        {
            string key = r.GetString(), json = r.GetString();
            if (!session.Players.TryGetValue(from, out var p)) return;
            if (!records.TryGetValue(p.Name ?? "", out var theirs)) records[p.Name ?? ""] = theirs = new Dictionary<string, string>();
            theirs[key] = json;
            RecordsReceived++;
        }

        public int RecordCount(string playerName) => records.TryGetValue(playerName ?? "", out var t) ? t.Count : 0;

        public static string RecordsPath => SaveLoad.GetPath(GameData.GetWorldPrefix + "_mp_loot.txt");

        public void SaveRecords()
        {
            if (!session.IsHost || Session.Current != session || GuestWorld.Active || !GameData.HasWorldData || records.Count == 0) return;
            try
            {
                var lines = new List<string> { SideFiles.WorldHeader };   // (whose records: see SideFiles)
                foreach (var p in records)
                    foreach (var kv in p.Value)
                        lines.Add($"{B64(p.Key)}\t{kv.Key}\t{B64(kv.Value)}");
                Directory.CreateDirectory(Path.GetDirectoryName(RecordsPath));
                File.WriteAllLines(RecordsPath, lines);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[loot] Couldn't save the chest records: " + e.Message); }
        }

        private void LoadRecords()
        {
            try
            {
                if (!GameData.HasWorldData || !File.Exists(RecordsPath)) return;
                var lines = File.ReadAllLines(RecordsPath);
                if (lines.Length > 0 && !SideFiles.HeaderOk(lines[0], RecordsPath)) return;
                foreach (var line in lines)
                {
                    var p = line.Split('\t');
                    if (p.Length != 3) continue;
                    string name = UnB64(p[0]);
                    if (!records.TryGetValue(name, out var t)) records[name] = t = new Dictionary<string, string>();
                    t[p[1]] = UnB64(p[2]);
                }
                Plugin.Log.LogInfo($"[loot] Loaded chest records of {records.Count} player(s)");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[loot] Couldn't load the chest records: " + e.Message); }
        }

        private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        private static string UnB64(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    // The host saves its chest records whenever the game saves.
    [HarmonyLib.HarmonyPatch(typeof(GameData), nameof(GameData.SaveData))]
    internal static class LootRecordsSavePatch
    {
        private static void Postfix() => LootRules.Instance?.SaveRecords();
    }
}
