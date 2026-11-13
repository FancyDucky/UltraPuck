using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CP99Online.Maps;

namespace UltraPuckMapMaker
{
    /// <summary>
    /// Test in game: writes the map and a request into "UltraPuck Maps/Testing". UltraPuck in the game picks the
    /// request up (from the menus or a match) and starts a practice match on the map. Starts the game if needed.
    /// </summary>
    internal static class GameTest
    {
        public static string Folder { get { return Path.Combine(Paths.MapsFolder, "Testing"); } }

        /// <summary>Returns what happened, for the status bar; throws if the map can't be written.</summary>
        public static string Run(MapData map, string controls, int bots, bool launch)
        {
            var game = Paths.GameFolder;
            if (game == null) throw new InvalidOperationException("ClusterPuck 99 wasn't found on this PC.");
            if (!File.Exists(Path.Combine(game, "BepInEx", "plugins", "CP99Online", "CP99Online.dll")))
                throw new InvalidOperationException("UltraPuck isn't installed in the game (" + game + ").");

            Directory.CreateDirectory(Folder);
            var mapPath = Path.Combine(Folder, Paths.SafeFileName(string.IsNullOrEmpty(map.Name) ? "Test" : map.Name) + ".upmap");
            File.WriteAllText(mapPath, map.Write(), new UTF8Encoding(false));

            // the request appears all at once (the game checks for it twice a second)
            var request = Path.Combine(Folder, "request.txt");
            var temp = request + ".tmp";
            File.WriteAllText(temp, "map " + mapPath + "\ncontrols " + controls + "\nbots " + bots + "\n", new UTF8Encoding(false));
            if (File.Exists(request)) File.Delete(request);
            File.Move(temp, request);

            var running = Process.GetProcessesByName("ClusterPuck99");
            if (running.Length > 0)
            {
                if (launch) Front(running[0]);
                return "Sent to the game: it loads the map in a moment.";
            }
            if (!launch) return "Request written (the game isn't running).";
            Process.Start(new ProcessStartInfo(Path.Combine(game, "ClusterPuck99.exe")) { WorkingDirectory = game, UseShellExecute = false });
            return "Starting the game: it goes straight to the map once it has loaded.";
        }

        private static void Front(Process p)
        {
            var h = p.MainWindowHandle;
            if (h == IntPtr.Zero) return;
            if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
            SetForegroundWindow(h);
        }

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    }
}
