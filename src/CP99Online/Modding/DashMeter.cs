using System.Collections.Generic;
using CP99Online;
using CP99Online.Net;
using CP99Online.Rounds;
using UnityEngine;

namespace UltraPuck
{
    /// <summary>
    /// A small bar above each player that shows their dash (boost): one segment per dash charge, each filling
    /// up with white as it recharges and pulsing when it's ready. Works with Rounds Mode cards (Double Dash, Quick Recharge).
    /// </summary>
    internal sealed class DashMeter : UltraMod
    {
        internal static DashMeter Instance;
        private GameObject _host;

        public DashMeter() { Instance = this; }

        public override string Id { get { return "ultrapuck.dashmeter"; } }
        public override string Name { get { return "Dash Meter"; } }
        public override string Author { get { return "UltraPuck"; } }
        public override string Version { get { return Plugin.Version; } }
        public override string Description { get { return "A small bar above each player shows when their dash is ready, one segment per charge."; } }

        internal BepInEx.Configuration.ConfigEntry<bool> Always;

        public override void OnLoad()
        {
            Always = AddChoice("ShowWhen", "Show", false, new[] { false, true }, v => v ? "Always" : "While Recharging",
                "While Recharging: the bar appears after a dash and fades once every charge is ready.");
        }

        public override void OnEnable()
        {
            _host = new GameObject("UltraPuck_DashMeter");
            Object.DontDestroyOnLoad(_host);
            _host.AddComponent<DashMeterView>();
        }

        public override void OnDisable()
        {
            if (_host != null) Object.Destroy(_host);
            _host = null;
        }
    }

    /// <summary>
    /// Dash charges for every ball. The host / offline PC reads the real values; online clients work them out
    /// from the host's shove events and each player's cards (both of which they already receive).
    /// </summary>
    internal static class DashTracker
    {
        private const float GameCooldown = 2f;

        private sealed class Remote { public int Max = 1; public float Cooldown = GameCooldown; public float Charges = 1f; public int CardCount = -1; }
        private static readonly Dictionary<int, Remote> _bySlot = new Dictionary<int, Remote>();

        /// <summary>Client: the host says this slot just dashed.</summary>
        internal static void OnRemoteShove(int slot)
        {
            Remote r;
            if (!_bySlot.TryGetValue(slot, out r)) _bySlot[slot] = r = new Remote();
            r.Charges = Mathf.Max(0f, r.Charges - 1f);
        }

        internal static void Clear() { _bySlot.Clear(); }

        /// <summary>Called once per frame by the meter.</summary>
        internal static void Tick(float dt)
        {
            if (!NetSession.ClientInMatch) return;
            for (var slot = 0; slot < Match.Balls.Length; slot++)
            {
                Remote r;
                if (!_bySlot.TryGetValue(slot, out r)) _bySlot[slot] = r = new Remote();
                var cards = slot < CardGame.Seats.Count ? CardGame.Seats[slot].Cards : null;
                var count = cards != null ? cards.Count : 0;
                if (count != r.CardCount)
                {
                    // Same maths as the host (CardEffects.Recompute), which also refills charges on a new card.
                    var stats = new CardStats();
                    if (cards != null)
                        foreach (var id in cards) { var c = Cards.Get(id); if (c != null) try { c.Apply(stats); } catch { } }
                    r.Max = 1 + Mathf.Clamp(stats.ExtraDashes, 0, 9);
                    r.Cooldown = GameCooldown * Mathf.Clamp(stats.DashCooldown, 0.1f, 5f);
                    if (r.CardCount >= 0) r.Charges = r.Max;
                    r.CardCount = count;
                }
                if (r.Charges < r.Max) r.Charges = Mathf.Min(r.Max, r.Charges + dt / r.Cooldown);
            }
        }

        /// <summary>Max charges and current charges (fractional while recharging).</summary>
        internal static bool Get(BallData b, out int max, out float charges)
        {
            max = 1; charges = 1f;
            if (b == null) return false;
            if (NetSession.ClientInMatch)
            {
                Remote r;
                var slot = Match.Slot(b);
                if (slot < 0 || !_bySlot.TryGetValue(slot, out r)) return false;
                max = r.Max; charges = r.Charges;
                return true;
            }
            var d = CardEffects.DashOf(b);
            if (d != null) { max = d.Max; charges = d.Charges; return true; }
            var shove = b.GetComponent<BallShove>();
            if (shove == null) return false;
            charges = Mathf.Clamp01(1f - shove.shoveTimer / GameCooldown);
            return true;
        }
    }

    /// <summary>
    /// Draws a small bar just above each player (LineRenderers that follow the ball): one segment per dash
    /// charge, dark when empty, filling with white as it recharges, with a short pulse when it's ready.
    /// </summary>
    internal sealed class DashMeterView : MonoBehaviour
    {
        private const float BarLength = 0.78f;  // world units, whole bar
        private const float Thickness = 0.09f;
        private const float SegGap = 0.07f;     // between segments
        private const float Above = 0.5f;       // bar height above the ball's centre

        private sealed class Ring
        {
            public GameObject Root;
            public readonly List<LineRenderer> Back = new List<LineRenderer>(), Fill = new List<LineRenderer>();
            public float[] Pulse = new float[0];
            public float[] LastFill = new float[0];
            public float Alpha;
        }

        private readonly Dictionary<BallData, Ring> _rings = new Dictionary<BallData, Ring>();
        private Material _mat;

        private void OnDestroy() { Clear(); }
        private void OnLevelWasLoaded(int level) { Clear(); DashTracker.Clear(); }

        private void Clear()
        {
            foreach (var r in _rings.Values) if (r.Root != null) Destroy(r.Root);
            _rings.Clear();
        }

        private void LateUpdate()
        {
            var gm = Globals.gameManager;
            if (gm == null || gm.teams == null) { if (_rings.Count > 0) Clear(); return; }
            DashTracker.Tick(Time.deltaTime);
            if (_mat == null)
            {
                var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Particles/Alpha Blended") ?? Shader.Find("Unlit/Color");
                _mat = new Material(shader);
            }

            var seen = new HashSet<BallData>();
            foreach (var team in gm.teams)
                foreach (var b in team.members)
                {
                    if (b == null) continue;
                    seen.Add(b);
                    UpdateRing(b, gm);
                }
            var gone = new List<BallData>();
            foreach (var kv in _rings) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var b in gone) { if (_rings[b].Root != null) Destroy(_rings[b].Root); _rings.Remove(b); }
        }

        private void UpdateRing(BallData b, GameManager gm)
        {
            int max; float charges;
            var known = DashTracker.Get(b, out max, out charges);
            var visible = known && b.alive && !b.fallen && !gm.replayManager.replaying;
            Ring ring;
            if (!_rings.TryGetValue(b, out ring)) _rings[b] = ring = new Ring { Root = new GameObject("DashRing") };
            if (ring.Back.Count != max) Build(ring, max);

            var full = charges >= max - 0.001f;
            var always = DashMeter.Instance != null && DashMeter.Instance.Always.Value;
            var target = visible && (always || !full) ? 1f : 0f;
            // Linger a moment after the last charge fills so the "ready" pulse is seen.
            var fade = target > ring.Alpha ? 10f : (ring.Pulse.Length > 0 && Max(ring.Pulse) > 0.01f ? 0f : 2.5f);
            ring.Alpha = Mathf.MoveTowards(ring.Alpha, target, Time.deltaTime * fade);
            ring.Root.SetActive(ring.Alpha > 0.01f && visible);
            if (!ring.Root.activeSelf) return;

            var c = VisualCenter(b);
            var segLen = (BarLength - SegGap * (max - 1)) / max;
            var x0 = c.x - BarLength / 2f;
            var y = c.y + Above;
            for (var i = 0; i < max; i++)
            {
                var fill = Mathf.Clamp01(charges - i); // 1 = this charge is ready
                if (fill >= 1f && ring.LastFill[i] < 1f) ring.Pulse[i] = 1f; // just became ready
                ring.LastFill[i] = fill;
                ring.Pulse[i] = Mathf.MoveTowards(ring.Pulse[i], 0f, Time.deltaTime * 3f);

                var sx = x0 + i * (segLen + SegGap);
                SetLine(ring.Back[i], sx, sx + segLen, y, c.z, Thickness, new Color(0.08f, 0.08f, 0.1f, 0.75f * ring.Alpha));
                // White whether full or filling (a filling segment is slightly dimmer); drawn in front of the background.
                var a = (fill >= 1f ? 1f : 0.8f) * ring.Alpha;
                SetLine(ring.Fill[i], sx, sx + segLen * fill, y, c.z - 0.03f, Thickness * (1f + ring.Pulse[i] * 0.8f), new Color(1f, 1f, 1f, a));
            }
        }

        /// <summary>
        /// The ball's top face as the camera sees it: the camera is tilted, so a ring at the ball's centre depth
        /// would look offset. Use the mesh's bounds (x/y centre, nearest-to-camera z).
        /// </summary>
        private readonly Dictionary<BallData, Renderer> _meshes = new Dictionary<BallData, Renderer>();
        private Vector3 VisualCenter(BallData b)
        {
            Renderer r;
            if (!_meshes.TryGetValue(b, out r) || r == null)
            {
                r = b.ballMesh != null ? b.ballMesh.GetComponentInChildren<Renderer>() : null;
                _meshes[b] = r;
            }
            if (r == null || !r.enabled) return b.transform.position + new Vector3(0f, 0f, -0.05f);
            var bounds = r.bounds;
            return new Vector3(bounds.center.x, bounds.center.y, bounds.min.z - 0.02f);
        }

        private static float Max(float[] a) { var m = 0f; foreach (var v in a) if (v > m) m = v; return m; }

        private void Build(Ring ring, int max)
        {
            foreach (var l in ring.Back) Destroy(l.gameObject);
            foreach (var l in ring.Fill) Destroy(l.gameObject);
            ring.Back.Clear(); ring.Fill.Clear();
            for (var i = 0; i < max; i++)
            {
                ring.Back.Add(NewLine(ring.Root.transform, "back", 0));
                ring.Fill.Add(NewLine(ring.Root.transform, "fill", 1)); // always drawn over the background
            }
            ring.Pulse = new float[max];
            ring.LastFill = new float[max];
            for (var i = 0; i < max; i++) ring.LastFill[i] = 1f;
        }

        private LineRenderer NewLine(Transform parent, string name, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.sharedMaterial = _mat;
            lr.receiveShadows = false;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.sortingOrder = order;
            lr.SetVertexCount(2);
            return lr;
        }

        private static void SetLine(LineRenderer lr, float xFrom, float xTo, float y, float z, float width, Color color)
        {
            if (xTo - xFrom < 0.005f) { lr.enabled = false; return; }
            lr.enabled = true;
            lr.SetWidth(width, width);
            lr.SetColors(color, color);
            lr.SetPosition(0, new Vector3(xFrom, y, z));
            lr.SetPosition(1, new Vector3(xTo, y, z));
        }
    }
}
