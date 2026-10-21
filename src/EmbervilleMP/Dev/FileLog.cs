using System;
using System.IO;
using BepInEx.Logging;

namespace EmbervilleMP
{
    // Writes every BepInEx log line (ours, the game's Unity log, exceptions) to <--mp-out>/mp.log, flushed per line.
    // Needed headless: in -batchmode Unity's own log file doesn't get BepInEx's output.
    internal class FileLog : ILogListener
    {
        private readonly StreamWriter writer;

        private FileLog(string path)
        {
            writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }

        public static void Start()
        {
            if (Args.Out == null) return;
            try
            {
                Directory.CreateDirectory(Args.Out);
                BepInEx.Logging.Logger.Listeners.Add(new FileLog(Path.Combine(Args.Out, "mp.log")));
            }
            catch (Exception e) { Plugin.Log.LogWarning("mp.log: " + e.Message); }
        }

        public void LogEvent(object sender, LogEventArgs e)
        {
            lock (writer) writer.WriteLine($"[{e.Level,-7}:{e.Source.SourceName}] {e.Data}");
        }

        public void Dispose() => writer.Dispose();
    }
}
