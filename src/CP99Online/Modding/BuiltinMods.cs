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

    /// <summary>
    /// The game moves balls and the puck in 50 Hz physics steps with Rigidbody2D interpolation off, so at
    /// 60+ FPS they hold still for a frame, then jump (and their name tags with them). Turning interpolation on
    /// draws them smoothly between steps; the simulation itself is unchanged.
    /// Left off during instant replays (which place objects directly) and for online clients (their positions
    /// come from the host and are already smoothed every frame).
    /// </summary>
    internal sealed class SmoothMotion : UltraMod
    {
        private readonly System.Collections.Generic.List<UnityEngine.Rigidbody2D> _bodies = new System.Collections.Generic.List<UnityEngine.Rigidbody2D>();
        private float _nextScan;

        public override string Id { get { return "ultrapuck.smooth"; } }
        public override string Name { get { return "Smooth Motion"; } }
        public override string Author { get { return "UltraPuck"; } }
        public override string Version { get { return Plugin.Version; } }
        public override string Description { get { return "Players, name tags and the puck move smoothly instead of jittering. Visual only."; } }

        public override void OnEnable() { _nextScan = 0f; }

        public override void OnDisable()
        {
            foreach (var rb in _bodies)
                if (rb != null) rb.interpolation = UnityEngine.RigidbodyInterpolation2D.None;
            _bodies.Clear();
        }

        public override void OnSceneLoaded(string sceneName) { _bodies.Clear(); _nextScan = 0f; }

        public override void OnUpdate()
        {
            var gm = Globals.gameManager;
            if (gm == null) return;

            var now = UnityEngine.Time.realtimeSinceStartup;
            if (now >= _nextScan)
            {
                // Balls can be added (respawns, bots), so look again now and then.
                _nextScan = now + 0.5f;
                _bodies.RemoveAll(rb => rb == null);
                foreach (var ball in UnityEngine.Object.FindObjectsOfType<BallData>())
                    Track(ball.GetComponent<UnityEngine.Rigidbody2D>());
                if (gm.puck != null) Track(gm.puck.GetComponent<UnityEngine.Rigidbody2D>());
            }

            var replaying = gm.replayManager != null && gm.replayManager.replaying;
            var mode = replaying || CP99Online.Net.NetSession.ClientInMatch
                ? UnityEngine.RigidbodyInterpolation2D.None
                : UnityEngine.RigidbodyInterpolation2D.Interpolate;
            foreach (var rb in _bodies)
                if (rb != null && rb.interpolation != mode) rb.interpolation = mode;
        }

        private void Track(UnityEngine.Rigidbody2D rb)
        {
            if (rb != null && !_bodies.Contains(rb)) _bodies.Add(rb);
        }
    }

    /// <summary>
    /// Card picks between goals: after each goal (that doesn't end the match) the game holds before the
    /// field reset and every player picks one of a few random cards (or each team votes for one).
    /// The logic lives in CP99Online.Rounds; online, the host's settings decide.
    /// </summary>
    internal sealed class RoundsMode : UltraMod
    {
        internal static RoundsMode Instance;
        internal static readonly string[] PickTimes = { "15s", "30s", "60s", "No Limit" };

        public RoundsMode() { Instance = this; }

        public override string Id { get { return "ultrapuck.rounds"; } }
        public override string Name { get { return "Rounds Mode"; } }
        public override string Author { get { return "UltraPuck"; } }
        public override string Version { get { return Plugin.Version; } }
        public override string Description { get { return "After each goal everyone picks a card that powers them up for the rest of the match."; } }
        public override bool EnabledByDefault { get { return false; } }

        internal BepInEx.Configuration.ConfigEntry<bool> TeamVote;
        internal BepInEx.Configuration.ConfigEntry<int> Choices;
        internal BepInEx.Configuration.ConfigEntry<string> PickTime;
        internal BepInEx.Configuration.ConfigEntry<bool> ShowDuringPlay;

        public override void OnLoad()
        {
            TeamVote = AddChoice("TeamVote", "Picking", false, new[] { false, true }, v => v ? "Team Vote" : "Each Player",
                "Each Player: everyone gets their own card. Team Vote: each team votes for one card that the whole team gets (ties are random).");
            Choices = AddChoice("Choices", "Cards Offered", 3, new[] { 3, 4, 5 });
            PickTime = AddChoice("PickTime", "Pick Time", "30s", PickTimes,
                description: "How long players have to pick. Anyone who hasn't picked gets a random card.");
            ShowDuringPlay = AddToggle("ShowDuringPlay", "Cards During Play", true,
                "Show everyone's cards in the lower corner during play (hover for details).");
        }

        public override void OnEnable() { }
        public override void OnDisable() { }

        internal static bool Active { get { return Instance != null && Instance.Enabled; } }

        /// <summary>Test override for the Picking setting (not saved).</summary>
        internal static bool? ForceTeamVote;

        /// <summary>Seconds to pick, or -1 for no limit.</summary>
        internal static float PickSeconds
        {
            get
            {
                var v = Instance != null ? Instance.PickTime.Value : "30s";
                int s;
                return v.EndsWith("s") && int.TryParse(v.TrimEnd('s'), out s) ? s : -1f;
            }
        }
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
