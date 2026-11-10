using System;
using System.Diagnostics;
using System.IO;

namespace CP99Online.Net
{
    /// <summary>
    /// Logs to BepInEx and to a per-process file, so two game instances on one PC (local testing)
    /// don't fight over LogOutput.log.
    /// </summary>
    internal static class NetLog
    {
        private static StreamWriter _file;

        public static void Init(string pluginDir)
        {
            try
            {
                var path = Path.Combine(pluginDir, "net-" + Process.GetCurrentProcess().Id + ".log");
                _file = new StreamWriter(path, false) { AutoFlush = true };
                // Keep only the few newest logs.
                var old = Directory.GetFiles(pluginDir, "net-*.log");
                Array.Sort(old, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                for (var i = 6; i < old.Length; i++) { try { File.Delete(old[i]); } catch { } }
            }
            catch (Exception e)
            {
                Plugin.Logger_.LogWarning("Net log file unavailable: " + e.Message);
            }
        }

        private static void Write(string level, string msg)
        {
            if (_file == null) return;
            try { _file.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + msg); } catch { }
        }

        public static void Info(string msg) { Plugin.Logger_.LogInfo(msg); Write("INFO", msg); }
        public static void Warn(string msg) { Plugin.Logger_.LogWarning(msg); Write("WARN", msg); }
        public static void Debug(string msg) { Write("DBG ", msg); }
    }
}
