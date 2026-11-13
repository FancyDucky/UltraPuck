using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace UltraPuckMapMaker
{
    /// <summary>Finds the game's "UltraPuck Maps" folder.</summary>
    internal static class Paths
    {
        private const string GameExe = "ClusterPuck99.exe";
        private static string _game;

        /// <summary>The ClusterPuck 99 folder, or null if it can't be found.</summary>
        public static string GameFolder
        {
            get
            {
                if (_game == null) _game = Find() ?? string.Empty;
                return _game.Length > 0 ? _game : null;
            }
        }

        public static string MapsFolder
        {
            get
            {
                var g = GameFolder;
                return g != null ? Path.Combine(g, "UltraPuck Maps")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UltraPuck Maps");
            }
        }

        public static string OldMapsFolder { get { return Path.Combine(MapsFolder, "From Arena Creator"); } }

        private static string Find()
        {
            // 1. installed in the game folder: <game>\UltraPuck Map Maker\UltraPuck Map Maker.exe
            for (var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, GameExe))) return d.FullName;
            // 2. Steam libraries
            try
            {
                var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (steam != null)
                {
                    steam = steam.Replace('/', '\\');
                    var hit = InLibrary(steam);
                    if (hit != null) return hit;
                    var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf, Encoding.UTF8), "\"path\"\\s+\"([^\"]+)\""))
                        {
                            hit = InLibrary(m.Groups[1].Value.Replace(@"\\", @"\"));
                            if (hit != null) return hit;
                        }
                }
            }
            catch { }
            var def = @"C:\Program Files (x86)\Steam\steamapps\common\ClusterPuck 99";
            return File.Exists(Path.Combine(def, GameExe)) ? def : null;
        }

        private static string InLibrary(string library)
        {
            var dir = Path.Combine(library, @"steamapps\common\ClusterPuck 99");
            return File.Exists(Path.Combine(dir, GameExe)) ? dir : null;
        }

        public static string SafeFileName(string name)
        {
            var sb = new StringBuilder();
            foreach (var ch in (name ?? string.Empty).Trim())
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
            var s = sb.ToString().Trim('.', ' ');
            return s.Length == 0 ? "Map" : s;
        }
    }
}
