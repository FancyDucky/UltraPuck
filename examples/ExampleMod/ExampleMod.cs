using HarmonyLib;
using UltraPuck;
using UnityEngine;

namespace ExampleMod
{
    /// <summary>
    /// A small overlay that shows your FPS and the goals scored since the game started.
    /// Shows the three parts of an UltraPuck mod: the mod class, settings, and a Harmony patch.
    /// </summary>
    public class StatsOverlayMod : UltraMod
    {
        public override string Id { get { return "example.statsoverlay"; } }
        public override string Name { get { return "Stats Overlay"; } }
        public override string Author { get { return "UltraPuck"; } }
        public override string Version { get { return "1.0"; } }
        public override string Description { get { return "Shows your FPS and the goals scored this session in a corner of the screen."; } }
        public override bool EnabledByDefault { get { return false; } }

        // Settings appear on the mod's page in Main menu > Mods and are saved automatically.
        private BepInEx.Configuration.ConfigEntry<bool> _showFps;
        private BepInEx.Configuration.ConfigEntry<string> _corner;

        internal static int Goals;
        private float _fps;

        public override void OnLoad()
        {
            _showFps = AddToggle("ShowFps", "Show FPS", true);
            _corner = AddChoice("Corner", "Corner", "Top Left", new[] { "Top Left", "Top Right", "Bottom Left", "Bottom Right" });
        }

        // OnEnable/OnDisable are inherited: they apply and remove the [HarmonyPatch] classes in this .dll
        // (GoalPatch below), so the patch only runs while the mod is on.

        public override void OnUpdate()
        {
            if (Time.unscaledDeltaTime > 0f) _fps = Mathf.Lerp(_fps, 1f / Time.unscaledDeltaTime, 0.05f);
        }

        public override void OnGUI()
        {
            var text = (_showFps.Value ? Mathf.RoundToInt(_fps) + " FPS   " : string.Empty) + "Goals this session: " + Goals;
            var size = new Vector2(260f, 24f);
            var right = _corner.Value.EndsWith("Right");
            var bottom = _corner.Value.StartsWith("Bottom");
            var rect = new Rect(right ? Screen.width - size.x - 10f : 10f, bottom ? Screen.height - size.y - 10f : 10f, size.x, size.y);
            GUI.Box(rect, text);
        }
    }

    /// <summary>Runs after the game's own GameManager.ScoreGoal (see the game's code with a decompiler such as dnSpy or ILSpy).</summary>
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ScoreGoal))]
    internal static class GoalPatch
    {
        private static void Postfix()
        {
            StatsOverlayMod.Goals++;
        }
    }
}
