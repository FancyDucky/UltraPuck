using System.Collections.Generic;
using UnityEngine;

namespace UltraPuck
{
    /// <summary>
    /// A small segmented bar that follows a player (LineRenderers in the world): dark background, a fill that grows
    /// as it recharges, a short pulse when a segment is ready, and a fade-out once everything is full.
    /// Used by the Dash Meter (above the player) and the Puck Gun cooldown (below).
    /// </summary>
    internal sealed class PlayerBar
    {
        private const float SegGap = 0.07f;

        private readonly GameObject _root;
        private readonly List<LineRenderer> _back = new List<LineRenderer>(), _fill = new List<LineRenderer>();
        private float[] _pulse = new float[0], _last = new float[0];
        private float _alpha;

        internal PlayerBar(string name) { _root = new GameObject(name); }

        internal void Destroy() { if (_root != null) Object.Destroy(_root); }

        /// <param name="value">How full: 0..segments (e.g. 1.4 of 2 dash charges).</param>
        /// <param name="offsetY">Height above (+) or below (-) the player's centre.</param>
        /// <param name="always">Stay visible when full (otherwise fade out after the "ready" pulse).</param>
        internal void Set(BallData b, int segments, float value, bool visible, bool always, Color filling, Color ready,
            float offsetY, float length, float thickness)
        {
            if (_back.Count != segments) Build(segments);
            if (CP99Online.Rounds.Invisibility.HiddenFromMe(b)) visible = false; // no bars giving away an invisible player
            var full = value >= segments - 0.001f;
            var target = visible && (always || !full) ? 1f : 0f;
            var fade = target > _alpha ? 10f : (Max(_pulse) > 0.01f ? 0f : 2.5f); // stay up while the pulse plays
            _alpha = Mathf.MoveTowards(_alpha, target, Time.deltaTime * fade);
            _root.SetActive(_alpha > 0.01f && visible);
            if (!_root.activeSelf) return;

            var c = VisualCenter(b);
            var segLen = (length - SegGap * (segments - 1)) / segments;
            var x0 = c.x - length / 2f;
            var y = c.y + offsetY;
            for (var i = 0; i < segments; i++)
            {
                var fill = Mathf.Clamp01(value - i);
                if (fill >= 1f && _last[i] < 1f) _pulse[i] = 1f; // just became ready
                _last[i] = fill;
                _pulse[i] = Mathf.MoveTowards(_pulse[i], 0f, Time.deltaTime * 3f);

                var sx = x0 + i * (segLen + SegGap);
                SetLine(_back[i], sx, sx + segLen, y, c.z, thickness, new Color(0.08f, 0.08f, 0.1f, 0.75f * _alpha));
                var col = fill >= 1f ? Color.Lerp(ready, Color.white, _pulse[i] * 0.6f) : filling;
                SetLine(_fill[i], sx, sx + segLen * fill, y, c.z - 0.03f, thickness * (1f + _pulse[i] * 0.8f),
                    new Color(col.r, col.g, col.b, col.a * _alpha));
            }
        }

        private void Build(int segments)
        {
            foreach (var l in _back) Object.Destroy(l.gameObject);
            foreach (var l in _fill) Object.Destroy(l.gameObject);
            _back.Clear(); _fill.Clear();
            for (var i = 0; i < segments; i++)
            {
                _back.Add(NewLine("back", 0));
                _fill.Add(NewLine("fill", 1)); // always drawn over the background
            }
            _pulse = new float[segments];
            _last = new float[segments];
            for (var i = 0; i < segments; i++) _last[i] = 1f;
        }

        private LineRenderer NewLine(string name, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.sharedMaterial = SharedMaterial;
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

        private static float Max(float[] a) { var m = 0f; foreach (var v in a) if (v > m) m = v; return m; }

        // ------------------------------------------------------------------ shared

        private static Material _mat;

        private static Material SharedMaterial
        {
            get
            {
                if (_mat == null)
                {
                    var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Particles/Alpha Blended") ?? Shader.Find("Unlit/Color");
                    _mat = new Material(shader);
                }
                return _mat;
            }
        }

        private static readonly Dictionary<BallData, Renderer> _meshes = new Dictionary<BallData, Renderer>();

        /// <summary>
        /// The ball's top face as the camera sees it: the camera is tilted, so something at the ball's centre depth
        /// would look offset. Uses the mesh's bounds (x/y centre, nearest-to-camera z).
        /// </summary>
        internal static Vector3 VisualCenter(BallData b)
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

        /// <summary>Forget cached meshes (new scene).</summary>
        internal static void ClearCache() { _meshes.Clear(); }
    }

    /// <summary>
    /// Draws a <see cref="PlayerBar"/> for every player a source says to, and cleans up players that are gone.
    /// </summary>
    internal sealed class PlayerBars
    {
        private readonly string _name;
        private readonly Dictionary<BallData, PlayerBar> _bars = new Dictionary<BallData, PlayerBar>();

        internal PlayerBars(string name) { _name = name; }

        internal PlayerBar For(BallData b)
        {
            PlayerBar bar;
            if (!_bars.TryGetValue(b, out bar)) _bars[b] = bar = new PlayerBar(_name);
            return bar;
        }

        /// <summary>Remove bars for players not in <paramref name="seen"/>.</summary>
        internal void Prune(HashSet<BallData> seen)
        {
            var gone = new List<BallData>();
            foreach (var kv in _bars) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var b in gone) { _bars[b].Destroy(); _bars.Remove(b); }
        }

        internal void Clear()
        {
            foreach (var b in _bars.Values) b.Destroy();
            _bars.Clear();
        }

        internal int Count { get { return _bars.Count; } }
    }
}
