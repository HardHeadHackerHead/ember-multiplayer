using System.Linq;
using EmbervilleMP.Net;
using HarmonyLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // The world's clock is the host's (WorldState sends it every 2 s). Around that:
    //  - A script on a guest that sets the time of day (the custom event "Set DayTime", the cutscene action "Set Time
    //    of Day"; both call TimeManager.SetDayTime) is forwarded to the host: otherwise the host's next correction
    //    undoes it. The guest keeps its own setting meanwhile (corrections wait a moment), the host sends its clock
    //    at once.
    //  - A correction that puts our clock into a later day goes through the game's own new-day path (NewDayEvent, the
    //    day's state schedule) instead of overwriting the day count, as if our clock had run past midnight.
    //  - The host's clock keeps running while the host is paused (pause menu, death screen: timeScale 0) as long as
    //    anyone else is in the session: theirs follow it.
    internal static class WorldClock
    {
        public static int Forwarded, Received, NewDays, PausedTicks;
        private static Session session;
        private static bool applying;
        private static float holdUntil;

        public static void Hook(Session s)
        {
            session = s;
            holdUntil = 0f;
            s.On(Msg.TimeSet, OnTimeSet);
        }

        // Our own setting was just forwarded: the host's clock as it was before it saw it mustn't undo it.
        public static bool Holding => Time.realtimeSinceStartup < holdUntil;

        // Guest: TimeManager.SetDayTime ran here (not from the network).
        public static void OnLocalSet(float dayTime)
        {
            var s = Session.Current;
            if (applying || s == null || !s.Ready || s.IsHost || !WorldState.InSharedWorld || GuestWorld.Switching) return;
            if (Objects.CutsceneSync.PlayingTheirs) return;   // a step someone else set off: their copy asks
            s.Send(Msg.TimeSet, w => w.Put(dayTime));
            holdUntil = Time.realtimeSinceStartup + 3f;
            Forwarded++;
            Plugin.Log.LogInfo($"[time] A script set the time of day to {dayTime:F2}: asking the host");
        }

        private static void OnTimeSet(byte from, NetDataReader r)
        {
            float t = r.GetFloat();
            if (session == null || !session.IsHost || !Singleton<TimeManager>.HasMain) return;
            if (float.IsNaN(t) || t < 0f || t >= 24f) return;
            // A cutscene of ours sets the clock as it goes (often the same step, a bit behind theirs): it has the say.
            if (Objects.CutsceneSync.Instance != null && Objects.CutsceneSync.Instance.HasActive) { Plugin.Log.LogInfo($"[time] #{from}'s script set the time of day to {t:F2}: we're in a cutscene, ours sets it"); return; }
            applying = true;
            try { Singleton<TimeManager>.main.SetDayTime(t); }
            finally { applying = false; }
            Received++;
            WorldState.Instance?.SendTimeSoon();
            Plugin.Log.LogInfo($"[time] #{from}'s script set the time of day to {t:F2}");
        }

        // Guest: the host's clock (day, hour) differs from ours.
        public static void Correct(TimeManager tm, int day, float t)
        {
            int days = day - tm.dayCount;
            if (days > 0 && days <= 30)
            {
                // Past midnight: the game's own rollover (new day event, the new day's states).
                tm.dayTime = t + 24f * days;
                tm.SetupNewDay();
                NewDays++;
                return;
            }
            // We went past midnight a little before the host: going back would run the new day twice; the host's
            // clock catches up in a moment and the next correction is within the day.
            if (days == -1 && tm.dayTime < 2f && t > 22f) return;
            tm.dayCount = day;
            tm.dayTime = t;
        }

        // Host, every frame: paused here, but others play on.
        public static void Update()
        {
            var s = session;
            if (s == null || !s.IsHost || Time.timeScale != 0f || !s.Others.Any() || !Game.InGame || !Singleton<TimeManager>.HasMain) return;
            var tm = Singleton<TimeManager>.main;
            if (tm.IsForcedToCurrentState || tm.timeMultiplier <= 0f) return;
            tm.dayTime += Mathf.Min(Time.unscaledDeltaTime, 0.5f) * tm.timeMultiplier;
            if (tm.dayTime >= 24f) tm.SetupNewDay();
            PausedTicks++;
        }
    }

    [HarmonyPatch(typeof(TimeManager), nameof(TimeManager.SetDayTime))]
    internal static class SetDayTimePatch
    {
        private static void Postfix(float dayTime) => WorldClock.OnLocalSet(dayTime);
    }
}
