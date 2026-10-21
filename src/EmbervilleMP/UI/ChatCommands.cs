using System;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;

namespace EmbervilleMP
{
    // Chat lines starting with "/" are commands for this player; replies show in the chat as notices.
    internal static class ChatCommands
    {
        private class Command
        {
            public string[] Names;
            public string Usage, Help;
            public Action<string> Run;
        }

        private static readonly List<Command> commands = new List<Command>
        {
            new Command { Names = new[] { "help", "?" }, Usage = "/help", Help = "this list", Run = _ => Help() },
            new Command { Names = new[] { "tp", "teleport" }, Usage = "/tp <name>", Help = "teleport to a teammate (out of combat)", Run = Teleport },
            new Command { Names = new[] { "players", "who" }, Usage = "/players", Help = "who's here, and where", Run = _ => Players() },
        };

        public static void Run(string line)
        {
            string body = line.Trim().TrimStart('/');
            int space = body.IndexOf(' ');
            string name = (space < 0 ? body : body.Substring(0, space)).ToLowerInvariant();
            string args = space < 0 ? "" : body.Substring(space + 1).Trim();
            var cmd = commands.FirstOrDefault(c => c.Names.Contains(name));
            if (cmd == null) { Chat.Notice($"Unknown command /{name}. Type /help for the list."); return; }
            Plugin.Log.LogInfo($"[chat] command /{name} {args}");
            try { cmd.Run(args); }
            catch (Exception e) { Plugin.Log.LogError($"[chat] /{name} failed: {e}"); Chat.Notice($"/{name} didn't work."); }
        }

        private static void Help()
        {
            Chat.Notice("Commands:");
            foreach (var c in commands) Chat.Notice($"  {c.Usage}  {c.Help}");
            var key = Plugin.Settings.PingKey.Value;
            if (key != UnityEngine.KeyCode.None) Chat.Notice($"  {(key == UnityEngine.KeyCode.Mouse2 ? "Middle mouse" : key.ToString())}  ping a spot for your team");
        }

        private static void Teleport(string args)
        {
            var tp = TeamTeleport.Instance;
            if (tp == null || Session.Current == null) { Chat.Notice("Not in a multiplayer game."); return; }
            var found = tp.FindPlayers(args);
            if (found.Length == 0) { Chat.Notice(args.Length == 0 ? "Nobody else is here." : $"No player called \"{args}\"."); return; }
            if (found.Length > 1) { Chat.Notice($"Who? /tp {string.Join(", ", found.Select(p => p.Name))}"); return; }
            tp.Request(found[0].Id, out string message);
            Chat.Notice(message);
        }

        private static void Players()
        {
            var s = Session.Current;
            var presence = MPRunner.Instance?.Presence;
            if (s == null) { Chat.Notice("Not in a multiplayer game."); return; }
            foreach (var p in s.Players.Values.OrderBy(p => p.Id))
            {
                if (p.Id == s.LocalId) { Chat.Notice($"  {p.Name} (you): {Pings.PrettyPlace(Game.AreaName)}, {Pings.PrettyPlace(Game.SceneName)}"); continue; }
                var st = presence != null && presence.Remotes.TryGetValue(p.Id, out var rp) ? rp.Latest : null;
                string where = st == null || string.IsNullOrEmpty(st.Scene) ? "travelling" : $"{Pings.PrettyPlace(st.Area)}, {Pings.PrettyPlace(st.Scene)}";
                Chat.Notice($"  {p.Name}: {where}{(st != null && st.Dead ? " (down)" : "")}, {p.Ping} ms");
            }
        }
    }
}
