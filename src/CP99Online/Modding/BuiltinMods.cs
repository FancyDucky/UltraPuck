using CP99Online;

namespace UltraPuck
{
    // The built-in mods' patches live in CP99Online.Patches and check Enabled, so these override
    // OnEnable/OnDisable instead of patching the whole UltraPuck assembly.

    /// <summary>Raises the game's 60 FPS cap.</summary>
    internal sealed class FpsPlus : UltraMod
    {
        internal static FpsPlus Instance;
        private static readonly int[] Limits = { 60, 120, 144, 240, 0 };

        public FpsPlus() { Instance = this; }

        public override string Id { get { return "ultrapuck.fps"; } }
        public override string Name { get { return "FPS+"; } }
        public override string Author { get { return "UltraPuck"; } }
        public override string Version { get { return Plugin.Version; } }
        public override string Description { get { return "Raises the game's 60 FPS cap."; } }

        public override void OnLoad()
        {
            AddChoice(Plugin.FrameRateLimit, "FPS Limit", Limits, v => v > 0 ? v.ToString() : "Unlimited");
        }

        public override void OnEnable() { CP99Online.Patches.FrameRate.Apply(); }
        public override void OnDisable() { CP99Online.Patches.FrameRate.Apply(); }
    }

    /// <summary>Removes the slowdown after a boost/shove.</summary>
    internal sealed class BetterBoost : UltraMod
    {
        internal static BetterBoost Instance;

        public BetterBoost() { Instance = this; }

        public override string Id { get { return "ultrapuck.boost"; } }
        public override string Name { get { return "Better Boost"; } }
        public override string Author { get { return "UltraPuck"; } }
        public override string Version { get { return Plugin.Version; } }
        public override string Description { get { return "No slowdown after you boost. Online, the host's choice applies."; } }

        // Carries over the old "No Boost Slowdown" setting the first time.
        public override bool EnabledByDefault { get { return Plugin.NoBoostSlowdown.Value; } }

        public override void OnEnable() { }
        public override void OnDisable() { }

        internal static bool Active { get { return Instance != null && Instance.Enabled; } }
    }
}
