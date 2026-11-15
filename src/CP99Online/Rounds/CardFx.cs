using System.Collections.Generic;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>
    /// Card visuals drawn on every PC:
    /// - Shockwave: a ring that bursts out from the player who was hit (the host sends it to clients).
    /// - Friendly Fire: the team-coloured respawn sparkles keep coming off a player while their speed boost lasts.
    /// - Shockwave ready: a yellow dot in the middle of the player while their Shockwave can go off.
    /// - Puck Magnet: a faint circle around the player showing how far away they grab a loose puck.
    /// </summary>
    internal sealed class CardFx : MonoBehaviour
    {
        // ------------------------------------------------------------------ shared

        private static Material _lineMat;

        private static Material LineMat()
        {
            if (_lineMat != null) return _lineMat;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Particles/Alpha Blended");
            _lineMat = new Material(shader);
            return _lineMat;
        }

        private const int Segments = 64;

        private static LineRenderer MakeCircle(string name)
        {
            var go = new GameObject(name);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.material = LineMat();
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.SetVertexCount(Segments + 1);
            for (var i = 0; i <= Segments; i++)
            {
                var a = i * Mathf.PI * 2f / Segments;
                lr.SetPosition(i, new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f)); // unit circle; scale sets the radius
            }
            return lr;
        }

        private static Color TeamColor(int team)
        {
            var gm = Globals.gameManager;
            if (gm != null && team >= 0 && team < gm.teams.Count)
            {
                try { return ColorManager.GetColor(gm.teams[team].teamColor); } catch { }
            }
            return new Color(0.5f, 0.75f, 1f);
        }

        internal static void Clear()
        {
            foreach (var s in _slowRings.Values) if (s.Line != null) Destroy(s.Line.gameObject);
            _slowRings.Clear();
            ClearDots();
            _boost.Clear();
            foreach (var r in _rings.Values) if (r != null) Destroy(r.gameObject);
            _rings.Clear();
        }

        private void OnLevelWasLoaded(int level) { _slowRings.Clear(); _boost.Clear(); _rings.Clear(); _dots.Clear(); _readyAt.Clear(); _shockHolders.Clear(); _holdersAt = 0f; _magnetAt = 0f; }

        private static float _shotAt = -1f, _shotAt2 = -1f, _shotAt3 = -1f;
        private static int _shots;

        private void Update()
        {
            if (_shotAt > 0f && Time.unscaledTime >= _shotAt) { _shotAt = -1f; TestHarness.Shot("fx_event_" + (++_shots) + "_" + (Net.NetSession.ClientInMatch ? "client" : "host")); }
            if (_shotAt2 > 0f && Time.unscaledTime >= _shotAt2) { _shotAt2 = -1f; TestHarness.Shot("fx_cooldown_" + (Net.NetSession.ClientInMatch ? "client" : "host")); }
            if (_shotAt3 > 0f && Time.unscaledTime >= _shotAt3) { _shotAt3 = -1f; TestHarness.Shot("fx_ready_" + (Net.NetSession.ClientInMatch ? "client" : "host")); }
            Boosts();
            Magnets();
            Dots();
            Slows();
        }

        // ------------------------------------------------------------------ Shockwave slow
        // A slowed player gets an icy ring around them that pulses slowly until the slow wears off.

        private sealed class SlowRing { public float Until; public LineRenderer Line; }
        private static readonly Dictionary<BallData, SlowRing> _slowRings = new Dictionary<BallData, SlowRing>();
        private static readonly Color SlowColor = new Color(0.55f, 0.85f, 1f);

        /// <summary>Every PC: this ball is slowed for <paramref name="seconds"/> (a Shockwave hit).</summary>
        internal static void Slowed(BallData b, float seconds)
        {
            if (b == null) return;
            SlowRing s;
            if (!_slowRings.TryGetValue(b, out s)) _slowRings[b] = s = new SlowRing();
            s.Until = Time.time + seconds;
        }

        private static void Slows()
        {
            if (_slowRings.Count == 0) return;
            var gm = Globals.gameManager;
            var hide = gm == null || gm.replayManager != null && gm.replayManager.replaying || CardGame.PhaseActive;
            _done.Clear();
            foreach (var kv in _slowRings)
            {
                var b = kv.Key;
                var s = kv.Value;
                var left = s.Until - Time.time;
                if (b == null || left <= 0f) { _done.Add(b); continue; }
                if (s.Line == null) s.Line = MakeCircle("UltraPuck_Slowed");
                var show = !hide && b.alive && !b.fallen && !Invisibility.IsInvisible(b);
                s.Line.enabled = show;
                if (!show) continue;
                var r = CardEffects.Radius(b.GetComponent<Collider2D>()) * (1.25f + 0.08f * Mathf.Sin(Time.time * 5f));
                var p = b.transform.position;
                s.Line.transform.position = new Vector3(p.x, p.y, p.z - 0.05f);
                s.Line.transform.localScale = new Vector3(r, r, 1f);
                s.Line.SetWidth(0.09f, 0.09f);
                var col = new Color(SlowColor.r, SlowColor.g, SlowColor.b, Mathf.Clamp01(left / 0.5f) * 0.9f); // fades out at the end
                s.Line.SetColors(col, col);
            }
            foreach (var b in _done)
            {
                SlowRing s;
                if (_slowRings.TryGetValue(b, out s) && s.Line != null) Destroy(s.Line.gameObject);
                _slowRings.Remove(b);
            }
        }

        // ------------------------------------------------------------------ Shockwave ready dot
        // Players with Shockwave get a yellow dot in the middle of their ball while it's ready; it goes away for the cooldown.

        private static readonly Dictionary<BallData, float> _readyAt = new Dictionary<BallData, float>(); // every PC: when the cooldown ends
        private static readonly Dictionary<BallData, LineRenderer> _dots = new Dictionary<BallData, LineRenderer>();
        private static readonly HashSet<BallData> _shockHolders = new HashSet<BallData>();
        private static float _holdersAt;
        private static readonly Color DotColor = new Color(1f, 0.85f, 0.15f);
        private static Material _dotMat;

        /// <summary>Every PC: this ball's Shockwave went off and is recharging for <paramref name="seconds"/>.</summary>
        internal static void ShockwaveCooldown(BallData b, float seconds)
        {
            if (b == null || seconds <= 0f) return;
            _readyAt[b] = Time.time + seconds;
            if (TestHarness.FxShots) _shotAt2 = Time.unscaledTime + 2.5f;
        }

        private static void ClearDots()
        {
            foreach (var d in _dots.Values) if (d != null) Destroy(d.gameObject);
            _dots.Clear();
            _readyAt.Clear();
            _shockHolders.Clear();
            _holdersAt = 0f;
        }

        private static void Dots()
        {
            if (Time.unscaledTime >= _holdersAt)
            {
                _holdersAt = Time.unscaledTime + 0.5f;
                _shockHolders.Clear();
                foreach (var s in CardGame.Seats)
                    if (s.Ball != null && CardEffects.StatsOf(s).Shockwave > 0) _shockHolders.Add(s.Ball);
                _done.Clear();
                foreach (var kv in _dots) if (kv.Key == null || !_shockHolders.Contains(kv.Key)) _done.Add(kv.Key);
                foreach (var b in _done) { var d = _dots[b]; if (d != null) Destroy(d.gameObject); _dots.Remove(b); }
            }
            if (_shockHolders.Count == 0) return;
            var gm = Globals.gameManager;
            var hide = gm == null || gm.replayManager != null && gm.replayManager.replaying || CardGame.PhaseActive;
            foreach (var b in _shockHolders)
            {
                if (b == null) continue;
                LineRenderer dot;
                if (!_dots.TryGetValue(b, out dot) || dot == null)
                {
                    dot = MakeCircle("UltraPuck_ShockwaveReady");
                    if (_dotMat == null) { _dotMat = new Material(LineMat()) { renderQueue = 3500 }; } // drawn after the ball's symbol
                    dot.material = _dotMat;
                    dot.SetColors(DotColor, DotColor);
                    _dots[b] = dot;
                    if (TestHarness.FxShots) _shotAt3 = Time.unscaledTime + 3f;
                }
                float ready;
                var cooling = _readyAt.TryGetValue(b, out ready) && Time.time < ready;
                var show = !hide && !cooling && b.alive && !b.fallen && !Invisibility.IsInvisible(b);
                dot.enabled = show;
                if (!show) continue;
                // a filled disc: a ring as wide as its radius, on top of the ball (towards the camera)
                var R = CardEffects.Radius(b.GetComponent<Collider2D>());
                var r = R * 0.16f;
                var p = b.transform.position;
                // on the top of the ball's model (the face the camera sees), nudged toward the camera
                var cam = Camera.main;
                var top = new Vector3(p.x, p.y, p.z - R);
                var mesh = b.ballMesh != null ? b.ballMesh.GetComponentInChildren<Renderer>() : null;
                if (mesh != null) { var bb = mesh.bounds; top = new Vector3(bb.center.x, bb.center.y, bb.min.z); }
                var toCam = cam != null ? (cam.transform.position - top).normalized : Vector3.back;
                dot.transform.position = top + toCam * 0.12f; // in front of the symbol painted on the ball
                if (cam != null) dot.transform.rotation = Quaternion.LookRotation(-toCam, Vector3.up);
                dot.transform.localScale = new Vector3(r, r, 1f);
                dot.SetWidth(2f * r, 2f * r); // world units: the ring covers 0..2r, a solid dot of radius 2r
            }
        }

        // ------------------------------------------------------------------ Shockwave

        /// <summary>Every PC: a shockwave went off here.</summary>
        internal static void ShockwaveRing(Vector2 at, float radius, int team, float z)
        {
            var gm = Globals.gameManager;
            if (gm == null || radius <= 0f) return;
            var c = TeamColor(team);
            var pos = new Vector3(at.x, at.y, z - 0.05f);
            // a thick team-coloured ring, a thin white one just behind it, and a quick flash in the middle
            Spawn(pos, radius, Color.Lerp(c, Color.white, 0.2f), 0f, 0.45f, 0.5f, 1f);
            Spawn(pos, radius * 0.8f, Color.white, 0.07f, 0.38f, 0.16f, 1f);
            Spawn(pos, radius * 0.4f, Color.Lerp(c, Color.white, 0.6f), 0f, 0.2f, 1.1f, 0.6f);
            if (TestHarness.FxShots) _shotAt = Time.unscaledTime + 0.12f;
            try
            {
                var sm = gm.soundManager;
                if (sm != null && sm.thrustHitSound != null) sm.CreateSound(sm.thrustHitSound, 2f, 0.8f, 0.6f);
            }
            catch { }
        }

        private static void Spawn(Vector3 pos, float radius, Color color, float delay, float duration, float width, float alpha)
        {
            var lr = MakeCircle("UltraPuck_Shockwave");
            lr.transform.position = pos;
            lr.enabled = delay <= 0f;
            var w = lr.gameObject.AddComponent<Wave>();
            w.Line = lr; w.Radius = radius; w.Color = color; w.Delay = delay; w.Duration = duration; w.Width = width; w.Alpha = alpha;
            w.Apply(0f);
        }

        private sealed class Wave : MonoBehaviour
        {
            public LineRenderer Line;
            public float Radius, Delay, Duration, Width, Alpha;
            public Color Color;
            private float _t;

            public void Apply(float k)
            {
                var ease = 1f - (1f - k) * (1f - k) * (1f - k); // fast out, slowing down
                var r = Mathf.Lerp(Radius * 0.15f, Radius, ease);
                Line.transform.localScale = new Vector3(r, r, 1f);
                var width = Width * (1f - 0.75f * k);
                Line.SetWidth(width / Mathf.Max(0.01f, r), width / Mathf.Max(0.01f, r)); // the scale multiplies the width too
                var col = new Color(Color.r, Color.g, Color.b, Alpha * (1f - k * k));
                Line.SetColors(col, col);
            }

            private void Update()
            {
                _t += Time.deltaTime;
                if (_t < Delay) return;
                Line.enabled = true;
                var k = Mathf.Clamp01((_t - Delay) / Duration);
                Apply(k);
                if (k >= 1f) Destroy(gameObject);
            }
        }

        // ------------------------------------------------------------------ Friendly Fire

        private const float SparkleEvery = 0.3f;
        private sealed class Boost { public float Until, Next; }
        private static readonly Dictionary<BallData, Boost> _boost = new Dictionary<BallData, Boost>();

        /// <summary>Every PC: this ball's Friendly Fire boost started (or restarted).</summary>
        internal static void Boosted(BallData b, float seconds)
        {
            if (b == null) return;
            if (seconds <= 0f) { _boost.Remove(b); return; }
            Boost s;
            if (!_boost.TryGetValue(b, out s)) _boost[b] = s = new Boost();
            s.Until = Time.time + seconds;
            if (TestHarness.FxShots) _shotAt = Time.unscaledTime + 1f;
        }

        private static readonly List<BallData> _done = new List<BallData>();

        private static void Boosts()
        {
            if (_boost.Count == 0) return;
            var gm = Globals.gameManager;
            if (gm == null || gm.particleManager == null || gm.particleManager.respawnParticlesPrefabs == null) return;
            var now = Time.time;
            _done.Clear();
            foreach (var kv in _boost)
            {
                var b = kv.Key;
                if (b == null || now >= kv.Value.Until) { _done.Add(b); continue; }
                if (now < kv.Value.Next) continue;
                kv.Value.Next = now + SparkleEvery;
                if (!b.alive || b.fallen || Invisibility.IsInvisible(b) || b.team == null) continue;
                var i = (int)b.team.teamColor;
                if (i < 0 || i >= gm.particleManager.respawnParticlesPrefabs.Length) continue;
                var fx = Instantiate(gm.particleManager.respawnParticlesPrefabs[i], b.transform.position + Vector3.back / 2f, Quaternion.identity) as GameObject;
                if (fx == null) continue;
                fx.transform.SetParent(b.transform, true);
                Destroy(fx, 2f);
            }
            foreach (var b in _done) _boost.Remove(b);
        }

        // ------------------------------------------------------------------ Puck Magnet

        private static readonly Dictionary<BallData, LineRenderer> _rings = new Dictionary<BallData, LineRenderer>();
        private static readonly Dictionary<BallData, float> _range = new Dictionary<BallData, float>();
        private static float _magnetAt;

        private static void Magnets()
        {
            var gm = Globals.gameManager;
            if (Time.unscaledTime >= _magnetAt)
            {
                _magnetAt = Time.unscaledTime + 0.5f;
                _range.Clear();
                foreach (var s in CardGame.Seats)
                {
                    if (s.Ball == null) continue;
                    var st = CardEffects.StatsOf(s);
                    if (st.PickupRange > 1.001f) _range[s.Ball] = st.PickupRange;
                }
                _done.Clear();
                foreach (var kv in _rings) if (kv.Key == null || !_range.ContainsKey(kv.Key)) _done.Add(kv.Key);
                foreach (var b in _done) { var lr = _rings[b]; if (lr != null) Destroy(lr.gameObject); _rings.Remove(b); }
            }
            if (_range.Count == 0 || gm == null || gm.puck == null) return;

            var puckR = CardEffects.Radius(gm.puck.GetComponent<Collider2D>());
            var hide = gm.replayManager != null && gm.replayManager.replaying || CardGame.PhaseActive;
            foreach (var kv in _range)
            {
                var b = kv.Key;
                if (b == null) continue;
                LineRenderer lr;
                if (!_rings.TryGetValue(b, out lr) || lr == null)
                {
                    lr = MakeCircle("UltraPuck_MagnetRange");
                    var c = b.team != null ? TeamColor(gm.teams.IndexOf(b.team)) : Color.white;
                    var col = new Color(c.r, c.g, c.b, 0.55f);
                    lr.SetColors(col, col);
                    _rings[b] = lr;
                }
                var show = !hide && b.alive && !b.fallen && b.carryObject == null && !Invisibility.IsInvisible(b);
                lr.enabled = show;
                if (!show) continue;
                // exactly where Puck Magnet grabs: the puck is grabbed once its centre is within (player + puck radius)
                // x range of the player's centre, i.e. as soon as the puck's edge reaches this ring
                var r = (CardEffects.Radius(b.GetComponent<Collider2D>()) + puckR) * kv.Value - puckR;
                var p = b.transform.position;
                lr.transform.position = new Vector3(p.x, p.y, p.z + 0.2f); // just under the ball
                lr.transform.localScale = new Vector3(r, r, 1f);
                lr.SetWidth(0.06f, 0.06f); // a thin line, in world units
            }
        }
    }
}
