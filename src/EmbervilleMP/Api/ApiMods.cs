using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using EmbervilleMP.Net;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Api
{
    // Which mods each player has, for the join handshake: every BepInEx plugin with [MultiplayerMod] (GUID, name,
    // version, requirement). Mods without the attribute are only warned about (log, F8 window), never a reason to refuse.
    //   --mp-hide-mod=<guid>[,<guid>]       this copy pretends not to have those mods (tests: both copies run from one
    //   --mp-guest-hide-mod=<guid>[,...]    game folder); the second form only on a copy that isn't the host
    internal static class ApiMods
    {
        public const int MaxMods = 64, MaxField = 128;

        // Tests (Dev/ApiTestMod.cs): mods that aren't BepInEx plugins, and what this copy hides right now.
        public static readonly List<MpMod> TestMods = new List<MpMod>();
        public static readonly HashSet<string> TestHidden = new HashSet<string>();
        public static int Refusals;   // joins refused for their mods (host)

        private static List<MpMod> declared;
        private static List<string> undeclared;
        private static bool warned;

        // This copy's declared mods, as it shows them to others (hidden ones left out).
        public static List<MpMod> Local()
        {
            Scan();
            var hidden = Hidden();
            return declared.Concat(TestMods).Where(m => !hidden.Contains(m.Guid)).ToList();
        }

        public static IReadOnlyList<string> Undeclared { get { Scan(); return undeclared; } }

        private static HashSet<string> Hidden()
        {
            var set = new HashSet<string>(TestHidden, StringComparer.OrdinalIgnoreCase);
            void Add(string list) { if (list != null) foreach (var g in list.Split(',')) if (g.Trim().Length > 0) set.Add(g.Trim()); }
            Add(Args.Get("hide-mod"));
            if (!Args.Host) Add(Args.Get("guest-hide-mod"));
            return set;
        }

        // Plugins are all loaded before anyone can host or join; read once.
        private static void Scan()
        {
            if (declared != null) return;
            declared = new List<MpMod>();
            undeclared = new List<string>();
            try
            {
                foreach (var info in Chainloader.PluginInfos.Values)
                {
                    if (info == null || info.Metadata == null || info.Metadata.GUID == Plugin.Guid) continue;
                    MultiplayerModAttribute attr = null;
                    try
                    {
                        var type = info.Instance != null ? info.Instance.GetType() : null;
                        attr = type?.GetCustomAttributes(typeof(MultiplayerModAttribute), false).FirstOrDefault() as MultiplayerModAttribute;
                    }
                    catch (Exception e) { Plugin.Log.LogWarning($"[mods] Couldn't read {info.Metadata.GUID}'s attributes: {e.Message}"); }
                    if (attr != null) declared.Add(new MpMod(info.Metadata.GUID, info.Metadata.Name, info.Metadata.Version?.ToString(), attr.Requirement));
                    else undeclared.Add($"{info.Metadata.Name} {info.Metadata.Version}");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[mods] Couldn't list the plugins: " + e.Message); }
        }

        // Once, when the first session starts: what the other players will be told, and what may not work in co-op.
        public static void LogOnce()
        {
            if (warned) return;
            warned = true;
            var local = Local();
            if (local.Count > 0) Plugin.Log.LogInfo("[mods] Multiplayer mods: " + string.Join(", ", local.Select(m => $"{m} ({m.Requirement})")));
            foreach (var m in local.Where(m => m.Requirement == Requirement.Incompatible))
                Plugin.Log.LogWarning($"[mods] {m} says it doesn't work in co-op: it's told to turn off while a session is up");
            foreach (var u in Undeclared)
                Plugin.Log.LogWarning($"[mods] {u} has no [MultiplayerMod] declaration: it may not work in co-op (it never blocks a join)");
        }

        public static void Write(NetDataWriter w, List<MpMod> mods)
        {
            int n = Math.Min(mods.Count, MaxMods);
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
            {
                w.Put(Clip(mods[i].Guid));
                w.Put(Clip(mods[i].Name));
                w.Put(Clip(mods[i].Version));
                w.Put((byte)mods[i].Requirement);
            }
        }

        public static List<MpMod> Read(NetDataReader r)
        {
            int n = NetGuard.Count(r.GetByte(), r, 4);
            if (n > MaxMods) throw new FormatException("too many mods");
            var list = new List<MpMod>(n);
            for (int i = 0; i < n; i++)
            {
                string guid = Clip(r.GetString()), name = Clip(r.GetString()), version = Clip(r.GetString());
                byte req = r.GetByte();
                if (req > (byte)Requirement.Incompatible) throw new FormatException("bad requirement");
                list.Add(new MpMod(guid, name, version, (Requirement)req));
            }
            return list;
        }

        private static string Clip(string s)
        {
            s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray());
            return s.Length > MaxField ? s.Substring(0, MaxField) : s;
        }

        // Host: why this guest can't join (null: it can). Only Everyone mods count, both ways, same version.
        public static string Refusal(List<MpMod> host, List<MpMod> guest)
        {
            var problems = new List<string>();
            foreach (var h in host.Where(m => m.Requirement == Requirement.Everyone))
            {
                var g = guest.FirstOrDefault(m => string.Equals(m.Guid, h.Guid, StringComparison.OrdinalIgnoreCase));
                if (g == null) problems.Add($"The host uses {h}: install it to join.");
                else if (g.Version != h.Version) problems.Add($"The host uses {h}, you have {g.Version}: use the same version to join.");
            }
            foreach (var g in guest.Where(m => m.Requirement == Requirement.Everyone))
                if (!host.Any(m => string.Equals(m.Guid, g.Guid, StringComparison.OrdinalIgnoreCase)))
                    problems.Add($"You use {g} and the host doesn't: turn it off to join (or the host installs it).");
            if (problems.Count > 0) Refusals++;
            return problems.Count == 0 ? null : string.Join(" ", problems.Take(4)) + (problems.Count > 4 ? $" (and {problems.Count - 4} more)" : "");
        }

        // F8 window: what this copy declares, and what it doesn't.
        public static void DrawGui()
        {
            var local = Local();
            if (local.Count == 0 && Undeclared.Count == 0) return;
            GUILayout.Space(4);
            if (local.Count > 0) GUILayout.Label("Mods: " + string.Join(", ", local.Select(m => $"{m} ({m.Requirement})")));
            if (Undeclared.Count > 0) GUILayout.Label("Without a multiplayer declaration (may not work in co-op): " + string.Join(", ", Undeclared));
        }
    }
}
