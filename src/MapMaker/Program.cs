using System;
using System.Linq;
using System.Windows.Forms;

namespace UltraPuckMapMaker
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length >= 2 && args[0] == "--shot")
            {
                // self-test: --shot out.png [map.upmap] [steps...]
                var map = args.Length >= 3 && !args[2].StartsWith("-") && args[2].EndsWith(".upmap", StringComparison.OrdinalIgnoreCase) ? args[2] : null;
                var steps = args.Skip(map != null ? 3 : 2).ToArray();
                Application.Run(new MainForm(map, args[1], steps));
                return;
            }
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
        }
    }
}
