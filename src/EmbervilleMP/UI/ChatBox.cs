using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    // The chat key (Y) opens a text field; Enter sends, Escape cancels. Recent lines fade out after a while.
    internal class ChatBox
    {
        private const float ShowSeconds = 12f;
        private const float SystemShowSeconds = 40f;
        private bool typing;
        private string draft = "";
        private bool focusNext;

        public bool Typing => typing;

        // Above the game's HUD (health/Kymia bars and the quick bar fill the bottom ~18% of the screen).
        private static float Bottom => Screen.height * 0.80f - 30f;

        public void Update()
        {
            if (Session.Current == null) { typing = false; InputBlock.Set("chat", false); return; }
            if (!typing && Input.GetKeyDown(Plugin.Settings.ChatKey.Value) && GUIUtility.keyboardControl == 0)
            {
                typing = true;
                draft = "";
                focusNext = true;
            }
            InputBlock.Set("chat", typing);
        }

        // Tests: as if the chat key, then Escape, were pressed.
        public void Open()
        {
            typing = true;
            draft = "";
            focusNext = true;
            InputBlock.Set("chat", true);
        }

        public void Cancel()
        {
            typing = false;
            InputBlock.Set("chat", false);
        }

        public void OnGUI()
        {
            if (Session.Current == null) return;
            float now = Time.realtimeSinceStartup;
            float y = Bottom;
            for (int i = Chat.Lines.Count - 1, shown = 0; i >= 0 && shown < 6; i--)
            {
                var line = Chat.Lines[i];
                bool system = line.from == null;   // Chat.AddSystem
                if (!typing && now - line.time > (system ? SystemShowSeconds : ShowSeconds)) continue;
                var rect = new Rect(12, y - 22, Screen.width * 0.6f, 22);
                if (system) SystemLine.Draw(rect, line.text);
                else MPStyle.Shadowed(rect, $"{line.from}: {line.text}", MPStyle.Hud);
                y -= 22;
                shown++;
            }
            if (!typing) return;

            var e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
            {
                Chat.Send(draft.Trim());
                typing = false;
                e.Use();
                return;
            }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                typing = false;
                e.Use();
                return;
            }
            GUI.SetNextControlName("mp-chat");
            draft = GUI.TextField(new Rect(12, Bottom + 4, Screen.width * 0.5f, 24), draft, 160);
            if (focusNext)
            {
                GUI.FocusControl("mp-chat");
                focusNext = false;
            }
        }
    }
}
