using CP99Online;
using CP99Online.Net;
using HarmonyLib;
using UnityEngine;

namespace UltraPuck
{
    /// <summary>
    /// While you charge a shot the camera pulls back (and pans) until the goal you're shooting at is in view, and
    /// comes back after you shoot. A full charge shows the goal; charging past full (Over Charge) adds nothing.
    /// Capped by the Max Zoom Out setting. Visual only, on this PC.
    /// </summary>
    internal sealed class ChargeZoom : UltraMod
    {
        internal static ChargeZoom Instance;
        private static readonly int[] Caps = { 150, 200, 250, 300, 400, 0 };

        public ChargeZoom() { Instance = this; }

        public override string Id { get { return "ultrapuck.chargezoom"; } }
        public override string Name { get { return "Charge Zoom"; } }
        public override string Author { get { return "UltraPuck"; } }
        public override string Version { get { return Plugin.Version; } }
        public override string Description { get { return "While you charge a shot, the camera pulls back until the goal you're aiming at is in view. Over Charge doesn't zoom further. Visual only."; } }

        internal BepInEx.Configuration.ConfigEntry<int> MaxZoom;

        public override void OnLoad()
        {
            MaxZoom = AddChoice("MaxZoom", "Max Zoom Out", 250, Caps, v => v > 0 ? v + "%" : "No Limit",
                "How far out the camera may go to show the goal, as a size of the normal view (250% = two and a half times as much map).");
        }

        // the patch below checks Active (built-in mods don't patch on enable)
        public override void OnEnable() { }
        public override void OnDisable() { }

        internal static bool Active { get { return Instance != null && Instance.Enabled; } }
    }

    /// <summary>
    /// GameCamera.Update eases its two cameras (a flat one and the 3D one) toward a framing of the players every
    /// frame, starting from where they are. This works out the framing the game is heading for and the one that
    /// also takes in the target goal, adds the difference (times how far the charge is) after the game's update,
    /// and takes it off again before the next one, so the game's own framing never drifts.
    /// </summary>
    [HarmonyPatch(typeof(GameCamera), "Update")]
    internal static class ChargeZoomPatch
    {
        private const float FullCharge = 3f;   // the game's full charge (BallHandling clamps 0.5 to 3; Over Charge goes past)
        private const float MinCharge = 0.5f;
        private const float GoalMargin = 1.5f; // room around the goal
        private const float MinExtra = 1.1f;   // even with the goal already in view, a little zoom so charging feels like something

        private static float _zoom;            // 0 to 1, eased
        private static Vector3 _goal;          // the goal being framed (kept while zooming back in)
        private static float _ortho;           // what was added last frame: flat camera size,
        private static Vector3 _pos;           // flat camera position,
        private static Vector3 _persp;         // 3D camera position
        private static GameCamera _cam;
        private static readonly bool TestCycle = System.Environment.GetEnvironmentVariable("CP99_TEST_CHARGEZOOM") == "1";

        private static void Prefix(GameCamera __instance)
        {
            if (_cam != __instance) { _cam = __instance; _ortho = 0f; _pos = _persp = Vector3.zero; _zoom = 0f; return; }
            var c = __instance.GetComponent<Camera>();
            if (c != null) c.orthographicSize -= _ortho;
            __instance.transform.position -= _pos;
            if (__instance.perspectiveCamera != null) __instance.perspectiveCamera.transform.position -= _persp;
            _ortho = 0f; _pos = _persp = Vector3.zero;
        }

        private static void Postfix(GameCamera __instance)
        {
            var gm = Globals.gameManager;
            var normalPlay = ChargeZoom.Active && gm != null && !gm.targetPractice && !gm.obstacleCourse && !gm.matchOver
                && gm.startCountdown <= 0f && (gm.replayManager == null || !gm.replayManager.replaying) && gm.puck != null;
            var charger = normalPlay ? LocalCharger(gm) : null;
            var target = charger != null ? Mathf.Clamp01((Charge(charger) - MinCharge) / (FullCharge - MinCharge)) : 0f;
            if (charger != null && target > 0f)
            {
                var team = charger.playerData != null ? charger.playerData.team : 0;
                var node = team >= 0 && team < gm.teams.Count ? gm.teams[team].otherGoalNode : null;
                if (node != null) _goal = node.transform.position;
            }
            // out over under a second as the charge builds, back in a little slower after the shot
            _zoom = Mathf.MoveTowards(_zoom, target, Time.deltaTime * (target > _zoom ? 3f : 1.6f));
            if (!normalPlay || _zoom <= 0.0001f) return;
            var t = _zoom * _zoom * (3f - 2f * _zoom); // smooth start and end

            // the game's framing: everyone the camera follows, and the loose puck
            Vector2 lo = new Vector2(float.PositiveInfinity, float.PositiveInfinity), hi = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (var tm in gm.teams)
                foreach (var b in tm.members)
                    if (b != null && b.visibleByCamera) Grow(ref lo, ref hi, b.transform.position, 0f);
            var puck = gm.puck.GetComponent<Puck>();
            if (puck != null && puck.carryer == null) Grow(ref lo, ref hi, gm.puck.transform.position, 0f);
            if (float.IsInfinity(lo.x)) return;
            var aspect = (float)Screen.height / Screen.width;
            var num = Extent(lo, hi, aspect);
            var size = Size(num);
            var centre = (lo + hi) * 0.5f;

            // ... and with the goal in it, as far as the cap allows
            Vector2 glo = lo, ghi = hi;
            Grow(ref glo, ref ghi, _goal, GoalMargin);
            var gnum = Extent(glo, ghi, aspect);
            var cap = ChargeZoom.Instance.MaxZoom.Value;
            var gsize = Mathf.Max(Size(gnum), size * MinExtra);
            if (cap > 0) gsize = Mathf.Min(gsize, size * cap / 100f);
            var gcentre = (glo + ghi) * 0.5f;
            var full = Size(gnum);
            if (gsize < full) // capped: lean toward the goal only as far as the bigger view allows
                gcentre = Vector2.Lerp(centre, gcentre, Mathf.Clamp01((gsize - size) / Mathf.Max(0.01f, full - size)));

            var cam = __instance.GetComponent<Camera>();
            var shift = (Vector3)((gcentre - centre) * t);
            if (cam != null)
            {
                _ortho = (gsize - size) * t;
                cam.orthographicSize += _ortho;
            }
            _pos = shift;
            __instance.transform.position += _pos;
            var pc = __instance.perspectiveCamera;
            if (pc != null)
            {
                // the 3D camera sits Persp (negative) along its view direction from the middle: further back by the
                // same ratio the flat view grew
                _persp = shift + pc.transform.forward * (Persp(num) * (gsize / size - 1f) * t);
                pc.transform.position += _persp;
            }
        }

        // the game's own framing rules (GameCamera.Update)
        private static float Extent(Vector2 lo, Vector2 hi, float aspect) { return Mathf.Max(Mathf.Abs(hi.x - lo.x) * aspect, Mathf.Abs(hi.y - lo.y)); }
        private static float Size(float num) { return Mathf.Max(7f, Map.map(num, 5f, 15f, 7f, 12f)); }
        private static float Persp(float num) { return Map.map(Mathf.Max(num, 6.5f), 5f, 15f, -32f, -55f); }

        private static void Grow(ref Vector2 lo, ref Vector2 hi, Vector3 p, float margin)
        {
            lo.x = Mathf.Min(lo.x, p.x - margin); lo.y = Mathf.Min(lo.y, p.y - margin);
            hi.x = Mathf.Max(hi.x, p.x + margin); hi.y = Mathf.Max(hi.y, p.y + margin);
        }

        /// <summary>The player on this PC charging hardest (not bots, not other PCs' players online), or null.</summary>
        private static BallData LocalCharger(GameManager gm)
        {
            BallData best = null; var bestCharge = 0f;
            if (NetSession.IsClient)
            {
                var mine = Match.Ball(Match.LocalSlot);
                return mine != null && (TestCycle || Charge(mine) > 0f) ? mine : null;
            }
            foreach (var team in gm.teams)
                foreach (var b in team.members)
                {
                    if (b == null || b.AI) continue;
                    if (Match.Active) { var slot = Match.Slot(b); if (slot >= 0 && Match.IsRemote(slot)) continue; }
                    var c = Charge(b);
                    if (TestCycle && best == null) { best = b; bestCharge = c; }
                    if (c > bestCharge) { best = b; bestCharge = c; }
                }
            return best;
        }

        private static float Charge(BallData b)
        {
            if (TestCycle) return Time.timeSinceLevelLoad % 8f < 4f ? 0f : 99f; // test: off 4 s, a (Over) charge 4 s
            var h = b.GetComponent<BallHandling>();
            return h != null ? h.ballCharge : 0f;
        }
    }
}
