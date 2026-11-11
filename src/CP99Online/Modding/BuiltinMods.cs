using System.Collections.Generic;
using System.Linq;
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
            if (Probe) RunProbe();
        }

        // Tests (CP99_SMOOTH_PROBE=1): count frames where a moving ball didn't move on screen (a stall = jitter).
        private static readonly bool Probe = System.Environment.GetEnvironmentVariable("CP99_SMOOTH_PROBE") == "1";
        private readonly System.Collections.Generic.Dictionary<UnityEngine.Rigidbody2D, UnityEngine.Vector3> _last = new System.Collections.Generic.Dictionary<UnityEngine.Rigidbody2D, UnityEngine.Vector3>();
        private readonly System.Collections.Generic.Dictionary<UnityEngine.Rigidbody2D, int[]> _counts = new System.Collections.Generic.Dictionary<UnityEngine.Rigidbody2D, int[]>();
        private float _nextReport;

        private void RunProbe()
        {
            foreach (var rb in _bodies)
            {
                if (rb == null) continue;
                var p = rb.transform.position;
                UnityEngine.Vector3 was;
                if (_last.TryGetValue(rb, out was) && rb.velocity.magnitude > 1f && UnityEngine.Time.deltaTime > 0f)
                {
                    int[] c;
                    if (!_counts.TryGetValue(rb, out c)) _counts[rb] = c = new int[3];
                    c[0]++;
                    if ((p - was).sqrMagnitude < 1e-8f) c[1]++;
                    // on-screen speed this frame vs the body's speed: off by more than 40% = an uneven step
                    var seen = (p - was).magnitude / UnityEngine.Time.deltaTime;
                    if (UnityEngine.Mathf.Abs(seen - rb.velocity.magnitude) > 0.4f * rb.velocity.magnitude) c[2]++;
                }
                _last[rb] = p;
            }
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextReport) return;
            _nextReport = now + 5f;
            foreach (var kv in _counts)
            {
                if (kv.Key == null || kv.Value[0] < 20) continue;
                var b = kv.Key.GetComponent<BallData>();
                CP99Online.Net.NetLog.Info("SMOOTH probe " + (b != null && b.playerData != null && b.playerData.account != null ? b.playerData.account.name : kv.Key.name) +
                    (b != null ? (b.AI ? " (bot)" : " (human)") : " (puck)") +
                    " moving frames=" + kv.Value[0] + " stalled=" + kv.Value[1] + " uneven=" + kv.Value[2] + " fps=" + (1f / UnityEngine.Time.smoothDeltaTime).ToString("F0") +
                    " interp=" + kv.Key.interpolation);
            }
            _counts.Clear();
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
            ShowDuringPlay = AddChoice("ShowDuringPlay", "Corner Card List", true, new[] { true, false }, v => v ? "Shown" : "Hidden",
                "The list of everyone's cards in the lower-left corner during play (hover a card for details). " +
                "Tap Back/Select or press Tab in a match to hide or show it.");
            AddPage("Toggle Cards", () => Cards.All.Select(c => ModSetting.Toggle(c.Name,
                () => CardEnabled(c.Id), v => CardEntry(c.Id).Value = v, CardText.Plain(c.Description))).ToList<ModSetting>(),
                "Choose which cards can be offered. Online, the host's choice applies.");
        }

        private readonly Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>> _cardEntries = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>();

        private BepInEx.Configuration.ConfigEntry<bool> CardEntry(string id)
        {
            BepInEx.Configuration.ConfigEntry<bool> e;
            if (!_cardEntries.TryGetValue(id, out e))
                _cardEntries[id] = e = Config.Bind(Section, "Card." + id, true, "Whether the card " + id + " can be offered.");
            return e;
        }

        private bool CardEnabled(string id) { return CardEntry(id).Value; }

        /// <summary>Can this card be offered? (Mods > Rounds Mode > Toggle Cards.)</summary>
        internal static bool IsCardEnabled(string id) { return Instance == null || Instance.Config == null || Instance.CardEnabled(id); }

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
