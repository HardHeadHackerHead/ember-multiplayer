using System;
using System.Collections.Generic;
using EmbervilleMP.Net;

namespace EmbervilleMP.Sync
{
    // Text lines between players. Lines starting with "#" are control messages (used by the self-tests) and aren't shown.
    internal static class Chat
    {
        public static readonly List<(string from, string text, float time)> Lines = new List<(string, string, float)>();
        public static event Action<byte, string> Received;

        public static void Hook(Session s)
        {
            s.On(Msg.Chat, (from, r) =>
            {
                string text = r.GetString();
                Received?.Invoke(from, text);
                if (text.StartsWith("#")) return;
                string name = s.Players.TryGetValue(from, out var p) ? p.Name : "#" + from;
                Add(name, text);
            });
        }

        // Tests: rewrites outgoing control lines ("#...") so each chained scenario's signals stay apart.
        public static Func<string, string> ControlFilter;

        public static void Send(string text)
        {
            var s = Session.Current;
            if (s == null || string.IsNullOrEmpty(text)) return;
            if (text.StartsWith("#") && ControlFilter != null) text = ControlFilter(text);
            if (text.StartsWith("/")) { ChatCommands.Run(text); return; }   // commands stay local
            s.Send(Msg.Chat, w => w.Put(text));
            if (!text.StartsWith("#")) Add(s.LocalName, text);
        }

        // A line from the mod itself (deaths...): no sender (from == null), drawn in its colour and kept on screen
        // longer by ChatBox. The text may hold rich-text tags (<b>, <color>).
        public static void AddSystem(string text, UnityEngine.Color colour)
        {
            Lines.Add((null, $"<color=#{UnityEngine.ColorUtility.ToHtmlStringRGB(colour)}>{text}</color>", UnityEngine.Time.realtimeSinceStartup));
            Plugin.Log.LogInfo("[chat] " + System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", ""));   // also in the log (tests, bug reports)
            if (Lines.Count > 50) Lines.RemoveAt(0);
        }
        // A line for this player only (command replies, notices); shown without a sender.
        public static void Notice(string text) => AddSystem(text, new UnityEngine.Color(1f, 0.86f, 0.55f));   // the game's warm gold

        private static void Add(string from, string text)
        {
            Lines.Add((from, text, UnityEngine.Time.realtimeSinceStartup));
            if (Lines.Count > 50) Lines.RemoveAt(0);
            Plugin.Log.LogInfo(from == null ? $"[chat] * {text}" : $"[chat] {from}: {text}");
        }
    }
}
