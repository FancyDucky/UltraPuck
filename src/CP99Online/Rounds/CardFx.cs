using System.Collections.Generic;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>
    /// Card visuals drawn on every PC:
    /// - Shockwave: a ring that bursts out from the player who was hit (the host sends it to clients).
    /// - Friendly Fire: the team-coloured respawn sparkles keep coming off a player while their speed boost lasts.
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
            _boost.Clear();
            foreach (var r in _rings.Values) if (r != null) Destroy(r.gameObject);
            _rings.Clear();
        }

        private void OnLevelWasLoaded(int level) { _boost.Clear(); _rings.Clear(); _magnetAt = 0f; }

        private static float _shotAt = -1f;
        private static int _shots;

        private void Update()
        {
            if (_shotAt > 0f && Time.unscaledTime >= _shotAt) { _shotAt = -1f; TestHarness.Shot("fx_event_" + (++_shots) + "_" + (Net.NetSession.ClientInMatch ? "client" : "host")); }
            Boosts();
            Magnets();
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
                // the reach Puck Magnet grabs from: the puck's centre within (player + puck radius) x range
                var r = (CardEffects.Radius(b.GetComponent<Collider2D>()) + puckR) * kv.Value;
                var p = b.transform.position;
                lr.transform.position = new Vector3(p.x, p.y, p.z + 0.2f); // just under the ball
                lr.transform.localScale = new Vector3(r, r, 1f);
                var w = 0.08f / Mathf.Max(0.01f, r);
                lr.SetWidth(w, w);
            }
        }
    }
}
