using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;

namespace EmbervilleMP.Api
{
    // Every call into another mod's code (handlers, events, object kinds, spawn factories) goes through here: an
    // exception is logged under that mod's id (a few full reports, then counts) and never reaches the session.
    internal static class ApiGuard
    {
        public const string OurId = Plugin.Guid;
        public static readonly Dictionary<string, int> Errors = new Dictionary<string, int>();
        public static int TotalErrors;
        private static readonly Dictionary<string, (float since, int logged, int suppressed)> windows = new Dictionary<string, (float, int, int)>();
        private static readonly Dictionary<Assembly, string> idOf = new Dictionary<Assembly, string>();
        private static int mainThread = -1;

        public static void NoteMainThread() => mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;

        // The API is main-thread only: a call from elsewhere is a bug in that mod (logged, ignored).
        public static bool OnMainThread(string what)
        {
            if (mainThread < 0 || System.Threading.Thread.CurrentThread.ManagedThreadId == mainThread) return true;
            Plugin.Log.LogError($"[api] {what} called off the main thread: ignored (the multiplayer API is main-thread only)");
            return false;
        }

        // The BepInEx plugin GUID of the mod an assembly belongs to (its assembly name if it has no plugin).
        public static string ModIdOf(Assembly a)
        {
            if (a == null) return "?";
            if (a == typeof(ApiGuard).Assembly) return OurId;
            if (idOf.TryGetValue(a, out var id)) return id;
            id = null;
            try
            {
                // (by its file: a plugin registering from its constructor or static fields has no Instance yet)
                string location = a.Location;
                foreach (var info in Chainloader.PluginInfos.Values)
                    if (info?.Metadata != null && (info.Instance != null && info.Instance.GetType().Assembly == a
                        || !string.IsNullOrEmpty(location) && string.Equals(info.Location, location, StringComparison.OrdinalIgnoreCase))) { id = info.Metadata.GUID; break; }
            }
            catch { }
            if (id == null) return a.GetName().Name;   // (not cached: it may be a plugin still loading)
            idOf[a] = id;
            return id;
        }

        public static string ModIdOf(Delegate d) => d?.Method?.DeclaringType != null ? ModIdOf(d.Method.DeclaringType.Assembly) : "?";

        public static void Run(string mod, string what, Action call)
        {
            if (call == null) return;
            try { call(); }
            catch (Exception e) { Report(mod, what, e); }
        }

        public static T Run<T>(string mod, string what, Func<T> call, T fallback)
        {
            if (call == null) return fallback;
            try { return call(); }
            catch (Exception e) { Report(mod, what, e); return fallback; }
        }

        // Each subscriber on its own: one that throws doesn't keep the others from hearing it.
        public static void Raise(Delegate handlers, string what, Action<Delegate> invoke)
        {
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { invoke(d); }
                catch (Exception e) { Report(ModIdOf(d), what, (e as TargetInvocationException)?.InnerException ?? e); }
            }
        }

        public static void Report(string mod, string what, Exception e)
        {
            Errors.TryGetValue(mod, out int n);
            Errors[mod] = n + 1;
            TotalErrors++;
            float now = UnityEngine.Time.realtimeSinceStartup;
            string key = mod + "|" + what;
            windows.TryGetValue(key, out var w);
            if (now - w.since >= 10f)
            {
                if (w.suppressed > 0) Plugin.Log.LogError($"[api] {mod}: ({w.suppressed} more errors in {what} in the last 10 s not shown)");
                w = (now, 0, 0);
            }
            if (w.logged < 3)
            {
                w.logged++;
                Plugin.Log.LogError($"[api] {mod}: {what} failed (the session goes on): {e}");
            }
            else w.suppressed++;
            windows[key] = w;
        }

        public static int ErrorsOf(string mod) => Errors.TryGetValue(mod, out int n) ? n : 0;

        // Logged once per key (an unknown channel, a mistake a mod repeats every frame).
        private static readonly HashSet<string> once = new HashSet<string>();
        public static bool Once(string key) => once.Add(key);


        public static string Describe() => string.Join(", ", Errors.Select(kv => $"{kv.Key}: {kv.Value}"));
    }
}
