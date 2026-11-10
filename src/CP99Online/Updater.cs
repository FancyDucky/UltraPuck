using System;
using System.Collections;
using System.IO;
using CP99Online.Net;
using UnityEngine;

namespace CP99Online
{
    /// <summary>
    /// Auto-update from GitHub Releases. Each release carries two extra assets next to the zip:
    ///   version.txt      the plugin version, e.g. "0.3.1"
    ///   CP99Online.dll   the plugin itself
    /// "releases/latest/download/&lt;name&gt;" always points at the newest release's asset. When a newer version is
    /// found the DLL is downloaded and swapped in on disk; it takes effect the next time the game starts.
    /// Only the plugin DLL updates this way; loader (BepInEx) changes still need the installer.
    /// </summary>
    internal class Updater : MonoBehaviour
    {
        internal const string Repo = "FancyDucky/ClusterPuck-99-Ultra";

        internal static string Status = "Checking for updates...";
        internal static string Latest;
        internal static bool RestartNeeded;
        private float _toastUntil;

        private static string BaseUrl
        {
            get
            {
                var overrideBase = Environment.GetEnvironmentVariable("CP99_UPDATE_BASE"); // testing
                return string.IsNullOrEmpty(overrideBase) ? "https://github.com/" + Repo + "/releases/latest/download/" : overrideBase;
            }
        }

        private IEnumerator Start()
        {
            var dll = Plugin.DllPath;
            TryDelete(dll + ".old");
            TryDelete(dll + ".new");

            if (!Plugin.AutoUpdate.Value) { Status = "Auto update is off"; yield break; }
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CP99_TEST")) &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CP99_UPDATE_BASE")))
            {
                Status = "Update check skipped (test mode)";
                yield break;
            }
            yield return new WaitForSeconds(4f);

            var vReq = new WWW(BaseUrl + "version.txt" + Bust());
            yield return vReq;
            if (!string.IsNullOrEmpty(vReq.error))
            {
                var err = vReq.error.Trim();
                NetLog.Info("Update check: " + BaseUrl + "version.txt -> " + err);
                Fail(err.StartsWith("404") ? "No update published yet" : "Couldn't check for updates (" + err + ")");
                yield break;
            }
            var text = vReq.text.Trim();
            Version latest, current;
            if (!TryVersion(text, out latest) || !TryVersion(Plugin.Version, out current)) { Fail("Couldn't read the latest version (\"" + text + "\")"); yield break; }
            Latest = latest.ToString();
            if (latest <= current) { Status = "Up to date (" + Plugin.Version + ")"; NetLog.Info("Update check: up to date (" + Plugin.Version + ", latest " + Latest + ")"); yield break; }

            Status = "Downloading " + Latest + "...";
            NetLog.Info("Update check: " + Latest + " available, downloading");
            var dReq = new WWW(BaseUrl + "CP99Online.dll" + Bust());
            yield return dReq;
            if (!string.IsNullOrEmpty(dReq.error)) { Fail("Update download failed (" + dReq.error.Trim() + ")"); yield break; }
            var bytes = dReq.bytes;
            if (bytes == null || bytes.Length < 16 * 1024 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            {
                Fail("Update download was not a valid mod file (" + (bytes == null ? 0 : bytes.Length) + " bytes)");
                yield break;
            }

            try
            {
                // The running DLL can be renamed but not overwritten, so move it aside and write the new one.
                File.WriteAllBytes(dll + ".new", bytes);
                File.Move(dll, dll + ".old");
                File.Move(dll + ".new", dll);
                RestartNeeded = true;
                Status = "Updated to " + Latest + " - restart the game to use it";
                _toastUntil = Time.realtimeSinceStartup + 15f;
                NetLog.Info("Updated plugin to " + Latest + " (takes effect after restart)");
            }
            catch (Exception ex)
            {
                if (!File.Exists(dll) && File.Exists(dll + ".old")) TryMove(dll + ".old", dll);
                Fail("Couldn't install the update: " + ex.Message);
            }
        }

        private void OnGUI()
        {
            if (!RestartNeeded || Time.realtimeSinceStartup > _toastUntil) return;
            var style = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            GUI.Box(new Rect(Screen.width - 380f, Screen.height - 90f, 360f, 70f),
                "CP99 Online " + Latest + " downloaded.\nRestart the game to use it.", style);
        }

        private static void Fail(string message)
        {
            Status = message;
            NetLog.Warn("Update check: " + message);
        }

        internal static bool TryVersion(string s, out Version v)
        {
            v = null;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim().TrimStart('v', 'V');
            try { v = new Version(s); return true; } catch { return false; }
        }

        /// <summary>Unique query string so Windows' web cache never serves an old answer (e.g. a 404 from before a release).</summary>
        private static string Bust() { return BaseUrl.StartsWith("http") ? "?t=" + DateTime.UtcNow.Ticks : string.Empty; }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        private static void TryMove(string a, string b) { try { File.Move(a, b); } catch { } }
    }
}
