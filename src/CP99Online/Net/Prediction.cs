using System.Collections.Generic;
using UnityEngine;

namespace CP99Online.Net
{
    /// <summary>
    /// Client-side prediction for the ball this client controls, so it reacts to the controls straight away instead
    /// of a round trip (plus the snapshot buffer) later.
    ///
    /// The host stays in charge of everything: it simulates every ball as before and sends this client Msg.Own about
    /// its ball (where it is, which of our inputs it has reached, its speed stats). Meanwhile this PC runs that one
    /// ball itself with the game's own movement rules (push = stick x speed, brake, dash, aim) against the arena's
    /// walls, and remembers where it was when each input went out. When Msg.Own says "after input k the ball was at
    /// P", the difference between P and our own position at input k is how far off the prediction is; that error is
    /// eased out over a few frames (or snapped if it's big: a respawn, a goal reset). Hits from other players, bumps,
    /// falls and card effects all happen on the host and arrive this way.
    ///
    /// Not predicted (the ball goes back to following snapshots): while dead or falling, during replays, countdowns
    /// and goal breaks, or when the host's messages stop coming.
    /// </summary>
    internal static class Prediction
    {
        private const float SnapDistance = 2.5f;  // further off than this: jump, don't slide
        private const float CorrectRate = 10f;     // per second: how fast position error is eased out
        private const float VelocityBlend = 0.5f;  // share of a velocity error fixed per host message
        private const float Stale = 0.5f;          // no host message for this long: stop predicting
        private const float ForceModifier = 0.02f; // BallMovement.forceModifier
        private const float SprintSlow = 700f;     // BallSprint: SpeedAddition(-700) while braking
        private const float ChargeSlow = 225f;     // BallHandling: SpeedAddition(-225) while charging

        // what the host last said about our ball
        private static bool _have;
        private static float _recv;
        private static int _body = -1;
        private static uint _ack;
        private static Vector2 _hostPos, _hostVel;
        private static float _hostSpeed, _hostMass = 1f, _dashForce = 250f;
        private static byte _hostFlags;
        private static int _hostCharges;

        // our own run
        private struct Past { public uint Seq; public Vector2 Pos, Vel; public bool Dashed; }
        private static readonly List<Past> _past = new List<Past>();
        private static bool _active;
        private static Vector2 _posErr;
        private static float _zRot;
        private static int _charges;
        private static bool _dashedSinceSend;
        private static float _boostCooldown;

        // how good the prediction is (logged every 10 s while predicting)
        private static float _statErr, _statLast; private static int _statN, _statSnaps;

        internal static void LogStats(float now, bool force = false)
        {
            if (!force && (!_active || now - _statLast < 10f)) return;
            _statLast = now;
            if (_statN > 0) NetLog.Info("Prediction: average error " + (_statErr / _statN).ToString("0.000") + " over " + _statN + " host updates, " + _statSnaps + " snaps");
            _statErr = 0f; _statN = 0; _statSnaps = 0;
        }

        internal const byte FCanMove = 1, FCanShove = 2, FCarrying = 4, FAlive = 8, FFallen = 16, FMesh = 32;

        /// <summary>The slot being predicted right now, or -1.</summary>
        internal static int Body { get { return _active ? _body : -1; } }

        internal static void Reset()
        {
            _why = null;
            Deactivate();
            _have = false; _body = -1; _ack = 0; _past.Clear();
        }

        // ---------------------------------------------------------------- from the host

        /// <summary>Msg.Own: u32 ack, u8 body slot, f32 x, y, vx, vy, f32 speed, f32 mass, f32 dash force, u8 flags, u8 dash charges.</summary>
        internal static void Read(Reader r, float now)
        {
            var ack = r.U32();
            var body = r.U8();
            var pos = new Vector2(r.F32(), r.F32());
            var vel = new Vector2(r.F32(), r.F32());
            var speed = r.F32(); var mass = r.F32(); var dash = r.F32();
            var flags = r.U8(); var charges = r.U8();
            if (_have && (int)(ack - _ack) < 0) return; // older than what we have
            if (_have && body != _body) Deactivate();   // switched to another ball
            _have = true; _recv = now; _body = body; _ack = ack;
            _hostPos = pos; _hostVel = vel; _hostSpeed = speed; _hostMass = Mathf.Max(0.01f, mass); _dashForce = dash;
            _hostFlags = flags; _hostCharges = charges;
            if (!_active) return;

            // where were we when the host had our input `ack`?
            var i = _past.FindIndex(p => p.Seq == ack);
            if (i < 0) return;
            var was = _past[i];
            var rb = Rigidbody(_body);
            if (rb == null) return;
            var err = pos - was.Pos;
            var dv = (vel - was.Vel) * VelocityBlend;
            _statErr += err.magnitude; _statN++;
            if (err.magnitude > SnapDistance) { Shift(err); rb.position += err; _posErr = Vector2.zero; _statSnaps++; }
            else _posErr = err; // replaces the last estimate: past positions already include what was eased out
            rb.velocity += dv;
            for (var k = 0; k < _past.Count; k++) { var p = _past[k]; p.Vel += dv; _past[k] = p; }

            // dashes the host hasn't seen yet still count against the charges it reports
            var pending = 0;
            for (var k = i + 1; k < _past.Count; k++) if (_past[k].Dashed) pending++;
            _charges = _hostCharges - pending;
            _past.RemoveRange(0, i + 1);
        }

        private static void Shift(Vector2 d)
        {
            for (var k = 0; k < _past.Count; k++) { var p = _past[k]; p.Pos += d; _past[k] = p; }
        }

        // ---------------------------------------------------------------- our side

        /// <summary>Called as each input message goes out: remember where we are for that input.</summary>
        internal static void OnInputSent(uint seq)
        {
            if (!_active) return;
            var rb = Rigidbody(_body);
            if (rb == null) return;
            _past.Add(new Past { Seq = seq, Pos = rb.position, Vel = rb.velocity, Dashed = _dashedSinceSend });
            _dashedSinceSend = false;
            if (_past.Count > 240) _past.RemoveAt(0);
        }

        private static string _why;

        private static bool ShouldPredict(GameManager gm, float now)
        {
            _why = !_have ? "no host data" : now - _recv > Stale ? "host data stale" : gm == null ? "no game"
                : (_hostFlags & (FAlive | FMesh)) != (FAlive | FMesh) ? "not alive" : (_hostFlags & FFallen) != 0 ? "fallen"
                : gm.replayManager.replaying ? "replay" : gm.roundOver ? "round over" : gm.matchOver ? "match over"
                : gm.startCountdown > 0f ? "countdown" : Match.Ball(_body) == null ? "no ball" : null;
            return _why == null;
        }

        private static void Activate(float now)
        {
            var ball = Match.Ball(_body);
            var rb = ball.GetComponent<Rigidbody2D>();
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate; // smooth between physics steps
            var col = ball.GetComponent<Collider2D>();
            if (col != null) col.enabled = true; // the arena's walls; other balls and the puck stay ghosts here
            // start from where the host's ball is now, as best we know
            var age = Mathf.Clamp(now - _recv, 0f, 0.25f);
            rb.position = _hostPos + _hostVel * age;
            rb.velocity = _hostVel;
            _zRot = ball.transform.eulerAngles.z;
            _past.Clear(); _posErr = Vector2.zero; _charges = _hostCharges; _dashedSinceSend = false;
            _active = true;
            NetLog.Debug("Prediction on (slot " + _body + ")");
        }

        private static void Deactivate()
        {
            if (!_active) return;
            LogStats(UnityEngine.Time.realtimeSinceStartup, true);
            _active = false;
            var ball = Match.Ball(_body);
            if (ball != null)
            {
                NetSession.MakePuppet(ball.gameObject);
                ball.GetComponent<Rigidbody2D>().interpolation = RigidbodyInterpolation2D.None;
            }
            _past.Clear(); _posErr = Vector2.zero;
            NetLog.Debug("Prediction off (" + (_why ?? "reset") + ")");
        }

        private static Rigidbody2D Rigidbody(int slot)
        {
            var b = Match.Ball(slot);
            return b != null ? b.GetComponent<Rigidbody2D>() : null;
        }

        /// <summary>Physics step: the game's BallMovement push, with this PC's controls and the host's speed stats.</summary>
        internal static void FixedTick(GameManager gm, float now, Vector2 move, bool brake, bool charging)
        {
            if (!ShouldPredict(gm, now)) { Deactivate(); return; }
            if (!_active) Activate(now);
            var rb = Rigidbody(_body);
            if (rb == null) return;
            if (rb.isKinematic) rb.isKinematic = false; // something (a respawn) made it a puppet again
            var carrying = (_hostFlags & FCarrying) != 0;
            var speed = _hostSpeed - (brake && !carrying ? SprintSlow : 0f) - (charging ? ChargeSlow : 0f);
            if (speed < 0f) speed = 0f;
            var force = (_hostFlags & FCanMove) != 0 ? move * speed : -rb.velocity;
            rb.mass = _hostMass;
            if (force.sqrMagnitude > 0f) rb.AddForce(force * ForceModifier);

            // ease out the latest error
            if (_posErr.sqrMagnitude > 1e-8f)
            {
                var c = _posErr * (1f - Mathf.Exp(-Time.fixedDeltaTime * CorrectRate));
                rb.position += c;
                _posErr -= c;
                Shift(c);
            }
        }

        /// <summary>Every frame: aim (BallHandling's rotation rules) and the brake drag (BallSprint), as the game does them per frame.</summary>
        internal static void FrameTick(Vector2 move, Vector2 aim, bool brake)
        {
            if (!_active) return;
            var ball = Match.Ball(_body);
            if (ball == null) return;
            const float turn = 10f;
            if (aim.magnitude > 0.2f) _zRot = Mathf.LerpAngle(_zRot, Mathf.Atan2(aim.y, aim.x) * Mathf.Rad2Deg + 180f, Time.deltaTime * turn);
            else if (move.magnitude > 0.2f) _zRot = Mathf.LerpAngle(_zRot, Mathf.Atan2(move.y, move.x) * Mathf.Rad2Deg - 90f, Time.deltaTime * turn);
            ball.transform.rotation = Quaternion.Euler(0f, 0f, _zRot);
            var rb = ball.GetComponent<Rigidbody2D>();
            if (brake && (_hostFlags & FCarrying) == 0) rb.AddForce(-rb.velocity * 80f * Time.deltaTime);
            if (_boostCooldown > 0f) _boostCooldown -= Time.deltaTime;
        }

        /// <summary>Shoot pressed: without the puck that's a dash (BallShove), so push now if a dash is ready.</summary>
        internal static void OnShootPressed()
        {
            if (!_active || (_hostFlags & FCarrying) != 0 || (_hostFlags & FCanShove) == 0 || _charges < 1) return;
            var ball = Match.Ball(_body);
            if (ball == null) return;
            ball.GetComponent<Rigidbody2D>().AddForce(ball.transform.up * _dashForce);
            _charges--;
            _dashedSinceSend = true;
        }

        /// <summary>Our predicted ball touched a boost pad: its push now (the host plays the effects).</summary>
        internal static bool OnBoost(Boost boost, Collider2D other)
        {
            if (!_active || boost == null || _boostCooldown > 0f || (_hostFlags & FCanMove) == 0) return false;
            var ball = Match.Ball(_body);
            if (ball == null || other.gameObject != ball.gameObject) return false;
            var rb = ball.GetComponent<Rigidbody2D>();
            rb.AddForce(rb.velocity.normalized * boost.boostSpeed);
            _boostCooldown = 0.25f; // BallData.boostTimer
            return true;
        }

        /// <summary>Our predicted ball hit a bumper: its push now (the host plays the effects).</summary>
        internal static bool OnBumper(Bumper bumper, Collision2D hit)
        {
            if (!_active || bumper == null) return false;
            var ball = Match.Ball(_body);
            if (ball == null || hit.gameObject != ball.gameObject) return false;
            ball.GetComponent<Rigidbody2D>().AddForce((ball.transform.position - bumper.transform.position) * bumper.power * 10f);
            return true;
        }

        /// <summary>Snapshots leave this ball alone: this PC is moving it.</summary>
        internal static bool Owns(int slot) { return _active && slot == _body; }
    }
}
