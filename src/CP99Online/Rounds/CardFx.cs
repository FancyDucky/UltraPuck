using System.Collections.Generic;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>
    /// Card visuals drawn on every PC:
    /// - Shockwave: a ring that bursts out from the player who was hit (the host sends it to clients).
    /// - Friendly Fire: the team-coloured respawn sparkles keep coming off a player while their speed boost lasts.
    /// - Shockwave cooldown: an orange bar under the player while their Shockwave recharges (stacked under any
    ///   stun / Invisibility / Puck Gun bar so they never overlap).
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
            _slowRings.Clear();
            _shock.Clear();
            _shockBars.Clear();
            _boost.Clear();
        }

        private void OnLevelWasLoaded(int level) { _slowRings.Clear(); _boost.Clear(); _shock.Clear(); _shockBars.Clear(); }

        private static float _shotAt = -1f, _shotAt2 = -1f;
        private static int _shots;

        private void LateUpdate() { ShockBars(); } // after the stun / Invisibility bars have placed themselves

        private void Update()
        {
            if (_shotAt > 0f && Time.unscaledTime >= _shotAt) { _shotAt = -1f; TestHarness.Shot("fx_event_" + (++_shots) + "_" + (Net.NetSession.ClientInMatch ? "client" : "host")); }
            if (_shotAt2 > 0f && Time.unscaledTime >= _shotAt2) { _shotAt2 = -1f; TestHarness.Shot("fx_cooldown_" + (Net.NetSession.ClientInMatch ? "client" : "host")); }
            Boosts();
            Slows();
        }

        // ------------------------------------------------------------------ Shockwave slow
        // A slowed player gives off blue sparkles (the game's blue-team respawn effect) until the slow wears off.

        private sealed class SlowRing { public float Until, Next; }
        private static readonly Dictionary<BallData, SlowRing> _slowRings = new Dictionary<BallData, SlowRing>();
        private const float SlowSparkleEvery = 0.25f;
        private const int BlueSparkles = 0; // TeamColor.BLUE

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
            if (gm == null || gm.particleManager == null || gm.particleManager.respawnParticlesPrefabs == null ||
                gm.particleManager.respawnParticlesPrefabs.Length <= BlueSparkles) return;
            var hide = gm.replayManager != null && gm.replayManager.replaying || CardGame.PhaseActive;
            var now = Time.time;
            _done.Clear();
            foreach (var kv in _slowRings)
            {
                var b = kv.Key;
                var s = kv.Value;
                if (b == null || now >= s.Until) { _done.Add(b); continue; }
                if (now < s.Next) continue;
                s.Next = now + SlowSparkleEvery;
                if (hide || !b.alive || b.fallen || Invisibility.IsInvisible(b)) continue;
                var fx = Instantiate(gm.particleManager.respawnParticlesPrefabs[BlueSparkles], b.transform.position + Vector3.back / 2f, Quaternion.identity) as GameObject;
                if (fx == null) continue;
                fx.transform.SetParent(b.transform, true);
                Destroy(fx, 2f);
            }
            foreach (var b in _done) _slowRings.Remove(b);
        }

        // ------------------------------------------------------------------ Shockwave cooldown bar
        // An orange bar under the player that fills up while their Shockwave recharges, pulses when it's ready, then
        // fades. It goes under the stun bar and the Invisibility / Puck Gun bar when those are showing.

        private sealed class ShockCooldown { public float Start, Length; }
        private static readonly Dictionary<BallData, ShockCooldown> _shock = new Dictionary<BallData, ShockCooldown>(); // every PC
        private static readonly UltraPuck.PlayerBars _shockBars = new UltraPuck.PlayerBars("ShockwaveBar");
        private static readonly Color ShockFilling = new Color(1f, 0.55f, 0.15f, 0.9f);
        private static readonly Color ShockReady = new Color(1f, 0.7f, 0.3f, 1f);

        /// <summary>Every PC: this ball's Shockwave went off and is recharging for <paramref name="seconds"/>.</summary>
        internal static void ShockwaveCooldown(BallData b, float seconds)
        {
            if (b == null || seconds <= 0f) return;
            _shock[b] = new ShockCooldown { Start = Time.time, Length = seconds };
            if (TestHarness.FxShots) _shotAt2 = Time.unscaledTime + 2.5f;
        }

        private static readonly HashSet<BallData> _shockSeen = new HashSet<BallData>();

        private static void ShockBars()
        {
            if (_shock.Count == 0 && _shockBars.Count == 0) return;
            var gm = Globals.gameManager;
            if (gm == null || gm.teams == null) { _shock.Clear(); _shockBars.Clear(); return; }
            var hide = gm.replayManager != null && gm.replayManager.replaying || CardGame.PhaseActive;
            _shockSeen.Clear();
            _done.Clear();
            foreach (var kv in _shock)
            {
                var b = kv.Key;
                var t = Time.time - kv.Value.Start;
                if (b == null || t > kv.Value.Length + 1.5f) { _done.Add(b); continue; } // ready and faded out
                _shockSeen.Add(b);
                var visible = !hide && b.alive && !b.fallen;
                var y = UltraPuck.PlayerBars.BelowY(b, "StunBar", "InvisibilityBar");
                _shockBars.For(b).Set(b, 1, Mathf.Clamp01(t / kv.Value.Length), visible, false, ShockFilling, ShockReady, y, 0.78f, 0.09f);
            }
            foreach (var b in _done) _shock.Remove(b);
            _shockBars.Prune(_shockSeen);
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
    }
}
