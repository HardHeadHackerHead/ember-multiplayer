using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace EmbervilleMP
{
    // Notices when the main thread stops ticking and asks Mono to print every thread's stack to the log,
    // so a hang can be traced without a debugger.
    internal static class Watchdog
    {
        [DllImport("mono-2.0-bdwgc.dll")]
        private static extern void mono_threads_request_thread_dump();

        private static long lastBeat = DateTime.UtcNow.Ticks;
        private static Thread thread;

        public static float StallSeconds = 8f;

        public static void Beat() => Interlocked.Exchange(ref lastBeat, DateTime.UtcNow.Ticks);

        public static void Start()
        {
            if (thread != null) return;
            thread = new Thread(Loop) { IsBackground = true, Name = "EmbervilleMP watchdog" };
            thread.Start();
        }

        private static void Loop()
        {
            int dumps = 0;
            while (true)
            {
                Thread.Sleep(1000);
                double stalled = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref lastBeat)).TotalSeconds;
                if (stalled < StallSeconds || dumps >= 3) continue;
                dumps++;
                Plugin.Log.LogWarning($"[watchdog] main thread stalled for {stalled:F0}s, dumping thread stacks ({dumps}/3)");
                try { mono_threads_request_thread_dump(); }
                catch (Exception e) { Plugin.Log.LogWarning("[watchdog] dump failed: " + e.Message); }
                Thread.Sleep(5000);
            }
        }
    }
}
