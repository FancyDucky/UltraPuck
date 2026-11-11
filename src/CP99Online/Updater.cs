using System;
using System.Collections;
using System.Diagnostics;
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
    /// found a popup says so, the DLL is downloaded and swapped in on disk, and the game restarts itself through
    /// Steam (after a short countdown, once the player is on the main menu and not in an online session).
    /// Only the plugin DLL updates this way; loader (BepInEx) changes still need the installer.
    /// </summary>
    internal class Updater : MonoBehaviour
    {
        internal const string Repo = "FancyDucky/UltraPuck";
        private const string SteamAppId = "337960";
        private const float RestartCountdown = 5f;

        internal static string Status = "Checking for updates...";
        internal static string Latest;
        internal static bool RestartNeeded;

        private enum Popup { None, Downloading, Installed, Failed }
        private Popup _popup;
        private string _popupText;
        private float _popupUntil;     // for messages that go away by themselves
        private float _restartAt = -1f; // countdown end (realtime); -1 = not counting
        private bool _restartCancelled, _restarting;
        private Texture2D _bg;
        private GUIStyle _title, _body;

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
                // No popup when offline or before the first release: that isn't something the player needs to act on.
                Fail(err.StartsWith("404") ? "No update published yet" : "Couldn't check for updates (" + err + ")", false);
                yield break;
            }
            var text = vReq.text.Trim();
            Version latest, current;
            if (!TryVersion(text, out latest) || !TryVersion(Plugin.Version, out current)) { Fail("Couldn't read the latest version (\"" + text + "\")", false); yield break; }
            Latest = latest.ToString();
            if (latest <= current) { Status = "Up to date (" + Plugin.Version + ")"; NetLog.Info("Update check: up to date (" + Plugin.Version + ", latest " + Latest + ")"); yield break; }

            Status = "Downloading " + Latest + "...";
            NetLog.Info("Update check: " + Latest + " available, downloading");
            Show(Popup.Downloading, "UltraPuck " + Latest + " is available (you have " + Plugin.Version + ").\nDownloading...", 0f);
            var dReq = new WWW(BaseUrl + "CP99Online.dll" + Bust());
            yield return dReq;
            if (!string.IsNullOrEmpty(dReq.error)) { Fail("Update download failed (" + dReq.error.Trim() + ")", true); yield break; }
            var bytes = dReq.bytes;
            if (bytes == null || bytes.Length < 16 * 1024 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            {
                Fail("Update download was not a valid mod file (" + (bytes == null ? 0 : bytes.Length) + " bytes)", true);
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
                NetLog.Info("Updated plugin to " + Latest + " (takes effect after restart)");
                Show(Popup.Installed, null, 0f);
            }
            catch (Exception ex)
            {
                if (!File.Exists(dll) && File.Exists(dll + ".old")) TryMove(dll + ".old", dll);
                Fail("Couldn't install the update: " + ex.Message, true);
            }
        }

        // ------------------------------------------------------------------ restart

        /// <summary>Restarting is safe now: on the main menu, not in an online session, menus closed.</summary>
        private static bool CanRestartNow
        {
            get
            {
                var s = NetSession.Instance;
                return Application.loadedLevelName == "MainMenu" && (s == null || s.Role == Role.None) && !ModMenu.IsOpen;
            }
        }

        /// <summary>We can relaunch through Steam on Windows and Mac; not from inside Proton/Wine (Linux, Steam Deck).</summary>
        private static bool CanRelaunch
        {
            get
            {
                if (Application.platform == RuntimePlatform.OSXPlayer) return true;
                if (Application.platform != RuntimePlatform.WindowsPlayer) return false;
                try { return Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Wine") == null; }
                catch { return true; }
            }
        }

        private void Update()
        {
            if (_popup != Popup.Installed || _restarting) return;
            if (!CanRelaunch || _restartCancelled) return;

            if (!CanRestartNow) { _restartAt = -1f; return; } // wait until the player is back on the main menu
            if (_restartAt < 0f) { _restartAt = Time.realtimeSinceStartup + RestartCountdown; NetLog.Info("Update: restarting in " + RestartCountdown + "s"); }

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.JoystickButton1))
            {
                _restartCancelled = true;
                _restartAt = -1f;
                NetLog.Info("Update: restart cancelled by the player");
                return;
            }
            if (Time.realtimeSinceStartup >= _restartAt) Relaunch();
        }

        private void Relaunch()
        {
            _restarting = true;
            var pid = Process.GetCurrentProcess().Id;
            var url = "steam://rungameid/" + SteamAppId;
            var testTarget = Environment.GetEnvironmentVariable("CP99_RESTART_TARGET"); // tests: start this instead of Steam
            if (!string.IsNullOrEmpty(testTarget)) url = testTarget;
            string exe, args;
            if (Application.platform == RuntimePlatform.OSXPlayer)
            {
                exe = "/bin/sh";
                args = "-c \"while kill -0 " + pid + " 2>/dev/null; do sleep 0.5; done; open " + url + "\"";
            }
            else
            {
                // Wait for this game to close, then ask Steam to start it again (keeps Steam features and launch options).
                exe = "powershell.exe";
                args = "-NoProfile -WindowStyle Hidden -Command \"Wait-Process -Id " + pid + " -Timeout 60 -ErrorAction SilentlyContinue; Start-Process '" + url + "'\"";
            }
            NetLog.Info("Update: restarting the game: " + exe + " " + args);
            if (Environment.GetEnvironmentVariable("CP99_RESTART_DRYRUN") == "1")
            {
                NetLog.Info("Update: dry run, not starting the helper");
            }
            else
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
                }
                catch (Exception ex)
                {
                    NetLog.Warn("Update: couldn't restart automatically: " + ex.Message);
                    _restarting = false;
                    _restartCancelled = true; // fall back to "restart the game to use it"
                    return;
                }
            }
            Application.Quit();
        }

        // ------------------------------------------------------------------ popup

        private void Show(Popup kind, string text, float seconds)
        {
            _popup = kind;
            _popupText = text;
            _popupUntil = seconds > 0f ? Time.realtimeSinceStartup + seconds : 0f;
        }

        private string PopupText()
        {
            switch (_popup)
            {
                case Popup.Installed:
                    if (_restarting) return "UltraPuck " + Latest + " installed.\nRestarting...";
                    if (!CanRelaunch || _restartCancelled) return "UltraPuck " + Latest + " installed.\nRestart the game to use it.";
                    if (_restartAt < 0f) return "UltraPuck " + Latest + " installed.\nThe game restarts when you're back on the main menu.";
                    return "UltraPuck " + Latest + " installed.\nRestarting in " + Mathf.CeilToInt(Mathf.Max(0f, _restartAt - Time.realtimeSinceStartup)) +
                        "...   (Esc / B: restart later)";
                default:
                    return _popupText;
            }
        }

        private void OnGUI()
        {
            if (_popup == Popup.None) return;
            if (_popupUntil > 0f && Time.realtimeSinceStartup > _popupUntil) { _popup = Popup.None; return; }
            // "Restart it yourself" only needs to stay up for a while, then the Mods menu still shows it.
            if (_popup == Popup.Installed && (!CanRelaunch || _restartCancelled) && _popupUntil == 0f) _popupUntil = Time.realtimeSinceStartup + 15f;

            var u = Screen.height / 720f;
            if (_bg == null)
            {
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, new Color(0.08f, 0.08f, 0.1f, 0.94f));
                _bg.Apply();
                _title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                _body = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
                _title.normal.textColor = Color.white;
                _body.normal.textColor = new Color(0.88f, 0.9f, 0.93f);
            }
            _title.fontSize = Mathf.RoundToInt(20 * u);
            _body.fontSize = Mathf.RoundToInt(16 * u);

            var w = Mathf.Min(Screen.width - 40f, 560 * u);
            var text = PopupText();
            var bodyH = _body.CalcHeight(new GUIContent(text), w - 32 * u);
            var h = 48 * u + bodyH + 18 * u;
            var r = new Rect((Screen.width - w) / 2f, 24 * u, w, h);
            GUI.DrawTexture(r, _bg);
            var accent = _popup == Popup.Failed ? new Color(0.95f, 0.4f, 0.35f) : new Color(0.45f, 0.8f, 0.5f);
            var old = GUI.color;
            GUI.color = accent;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 4 * u), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(r.x, r.y + 8 * u, r.width, 36 * u), _popup == Popup.Failed ? "UPDATE PROBLEM" : "UPDATE", _title);
            GUI.Label(new Rect(r.x + 16 * u, r.y + 44 * u, r.width - 32 * u, bodyH), text, _body);
        }

        private void Fail(string message, bool popup)
        {
            Status = message;
            NetLog.Warn("Update check: " + message);
            if (popup) Show(Popup.Failed, "Couldn't update to " + Latest + ": " + message.Replace("Update download failed", "download failed") +
                "\nYou can install it from github.com/" + Repo + "/releases", 20f);
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
