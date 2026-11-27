using System;
using System.Diagnostics;

namespace OneClickFilm
{
    internal sealed class DevEvent { }

    internal static class DevMonitor
    {
        internal static string Context { get { return null; } set { } }
        internal static string Operation { get { return null; } set { } }
        internal static Action<string,string> Route { set { } }
        internal static string DirectoryName { get { return null; } }
        internal static long Version { get { return 0; } }
        internal static DevEvent[] Snapshot(bool timeline) { return null; }
        internal static string Level(string msg) { return null; }

        [Conditional("LACUNA_DEV_MONITOR")]
        internal static void Action(string project, string message) { }
        [Conditional("LACUNA_DEV_MONITOR")]
        internal static void Progress(string project, string message) { }
        [Conditional("LACUNA_DEV_MONITOR")]
        internal static void Log(string project, string category, string message) { }
        [Conditional("LACUNA_DEV_MONITOR")]
        internal static void CodexSend(string project, string purpose, string transport, string model, string effort,
            string skill, string prompt, string schema, int images) { }
        [Conditional("LACUNA_DEV_MONITOR")]
        internal static void ClearContext() { }
        [Conditional("LACUNA_DEV_MONITOR")]
        internal static void FlushOnClose() { }
    }
}
