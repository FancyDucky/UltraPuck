using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UltraPuck;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>
    /// Turns each seat's cards into ball stats. The host (or the only PC, offline) runs the game, so it applies
    /// everything; online clients only apply what they need to draw (ball size, pylons).
    /// </summary>
    internal static class CardEffects
    {
        internal sealed class Applied
        {
            public CardStats Stats = new CardStats();
            /// <summary>Multiplies BallData.totalSpeed: card speed x opponents' Slow Poke x weight (see SpeedPatch).</summary>
            public float SpeedFactor = 1f;
        }

        private sealed class Original { public float Strength, Mass, ChargeSpeed; public Vector3 Scale; }

        internal sealed class Dash
        {
            public int Max = 1;
            public float Charges = 1f;
            public float Cooldown = 2f;
        }

        internal const float GameDashCooldown = 2f;  // BallShove.shoveCooldown
        internal const float GameDashForce = 250f;   // BallShove.Shove: AddForce(transform.up * 250)
        private const float GameChargeSpeed = 2f;    // BallHandling.ballChargeSpeed
        private const float GamePuckCap = 90f;       // Puck.FixedUpdate speed limit
        internal const float GameFullCharge = 3f;    // BallHandling.Update: ballCharge = Clamp(.., 0.5, 3)
        internal const float MaxOverCharge = 2f;     // 4 Over Charge cards: +200%, a full blue ring
        private const float MaxChargeSpeed = 20f;    // 4 Over Charge cards double it 4 times (x16)

        /// <summary>Friendly Fire: how hard this player pushes teammates (1 = normal).</summary>
        internal static float FriendlyFireOf(CardStats st) { return Mathf.Clamp(st.FriendlyFire, 0.1f, 10f); }

        /// <summary>Extra push when <paramref name="from"/> hits a teammate (Friendly Fire), else 1.</summary>
        internal static float FriendlyFireMul(BallData from, BallData target)
        {
            if (from == null || target == null || from == target || from.team != target.team) return 1f;
            var a = AppliedOf(from);
            return a != null ? FriendlyFireOf(a.Stats) : 1f;
        }

        internal static float OverChargeOf(CardStats st) { return Mathf.Clamp(st.OverCharge, 0f, MaxOverCharge); }

        /// <summary>Mass multiplier: Weight, and Knockback Taken (0.2 = 5x the mass, so hits push you 80% less).</summary>
        internal static float MassOf(CardStats st) { return Mathf.Clamp(st.Weight / Mathf.Max(0.01f, st.KnockbackTaken), 0.1f, 10f); }

        /// <summary>Dash check push multiplier: Weight and Knockback Given.</summary>
        internal static float GivenOf(CardStats st) { return Mathf.Clamp(st.Weight * st.KnockbackGiven, 0.1f, 10f); }

        // ------------------------------------------------------------------ Friendly Fire: teammate boost

        internal const float TeammateBoost = 1.8f, TeammateBoostSeconds = 2f;
        private static readonly Dictionary<BallData, float> _boostUntil = new Dictionary<BallData, float>();

        /// <summary>Move speed multiplier from a teammate's dash (Friendly Fire), else 1.</summary>
        internal static float BoostFactor(BallData b)
        {
            float until;
            return b != null && _boostUntil.TryGetValue(b, out until) && Time.time < until ? TeammateBoost : 1f;
        }

        /// <summary>Host / offline: <paramref name="from"/>'s dash hit <paramref name="target"/>.</summary>
        internal static void OnDashHit(BallData from, BallData target)
        {
            if (from == null || target == null || from == target || from.team != target.team) return;
            var a = AppliedOf(target);
            if (a == null || !a.Stats.TeammateBoost) return;
            var fresh = BoostFactor(target) <= 1f;
            SetBoost(target, TeammateBoostSeconds);
            if (fresh) Net.NetLog.Info("Friendly Fire: " + target.name + " got a speed boost from a teammate's dash");
        }

        /// <summary>Host / offline: give this ball <paramref name="seconds"/> of Friendly Fire boost (0 = end it), on every PC.</summary>
        private static void SetBoost(BallData b, float seconds)
        {
            if (seconds > 0f) _boostUntil[b] = Time.time + seconds; else _boostUntil.Remove(b);
            CardFx.Boosted(b, seconds);
            var slot = Net.Match.Slot(b);
            if (Net.NetSession.HostInMatch && slot >= 0) Net.NetSession.Instance.HostSendBoost(slot, seconds);
        }

        /// <summary>Host / offline, bot swap: a Friendly Fire boost stays with the spot, so the person who swaps in gets the bot's boost (and the other way round).</summary>
        internal static void SwapBoost(BallData me, BallData bot)
        {
            float mu, bu;
            var now = Time.time;
            var mine = _boostUntil.TryGetValue(me, out mu) && mu > now ? mu - now : 0f;
            var bots = _boostUntil.TryGetValue(bot, out bu) && bu > now ? bu - now : 0f;
            if (mine <= 0f && bots <= 0f) return;
            SetBoost(me, bots);
            SetBoost(bot, mine);
            Net.NetLog.Info("Friendly Fire: the boost moved with the swap (" + bots.ToString("F1") + " s to the player, " + mine.ToString("F1") + " s to the bot)");
        }

        // ------------------------------------------------------------------ Shockwave

        /// <summary>Shockwave reach, in multiples of the hit player's radius (bigger with more copies).</summary>
        internal static float ShockwaveRange(int copies) { return copies <= 0 ? 0f : 3f * (1f + 0.15f * (Mathf.Min(copies, 3) - 1)); }

        /// <summary>Stun on the opponents a shockwave pushes: 0.3 s, +0.1 s per extra copy.</summary>
        internal static float ShockwaveStun(int copies) { return 0.3f + 0.1f * (Mathf.Clamp(copies, 1, 3) - 1); }

        /// <summary>Seconds between one player's shockwaves.</summary>
        internal const float ShockwaveCooldown = 5f;

        private const float ShockwavePush = 0.8f; // x the dash check's own push
        private static readonly Dictionary<BallData, float> _shockAt = new Dictionary<BallData, float>();

        /// <summary>Host / offline: <paramref name="from"/>'s dash hit an opponent: with Shockwave, push the opponents around them away.</summary>
        internal static void Shockwave(BallData from, BallData target, Vector2 attackerVelocity)
        {
            if (from == null || target == null || from == target || from.team == target.team) return;
            var a = AppliedOf(from);
            if (a == null || a.Stats.Shockwave <= 0) return;
            float last;
            if (_shockAt.TryGetValue(from, out last) && Time.time - last < ShockwaveCooldown) return;
            _shockAt[from] = Time.time;
            var stun = ShockwaveStun(a.Stats.Shockwave);
            var gm = Globals.gameManager;
            var center = (Vector2)target.transform.position;
            var radius = Radius(target.GetComponent<Collider2D>()) * ShockwaveRange(a.Stats.Shockwave);
            var speed = Mathf.Max(attackerVelocity.magnitude, 8f);
            var pushed = 0;
            foreach (var t in gm.teams)
                foreach (var b in t.members)
                {
                    if (b == null || b == from || b == target || b.team == from.team || !b.alive || b.fallen) continue;
                    var d = (Vector2)b.transform.position - center;
                    var dist = d.magnitude;
                    if (dist > radius + Radius(b.GetComponent<Collider2D>())) continue;
                    var dir = dist > 0.01f ? d / dist : Random.insideUnitCircle.normalized;
                    var falloff = Mathf.Lerp(1f, 0.5f, Mathf.Clamp01(dist / radius));
                    var rb = b.GetComponent<Rigidbody2D>();
                    if (rb != null) rb.AddForce(dir * speed * 18f * ShockwavePush * falloff * GivenOf(a.Stats));
                    var bp = b.GetComponent<BallParticles>();
                    if (bp != null && bp.checkParticles != null)
                    {
                        bp.checkParticles.transform.rotation = Quaternion.LookRotation(Vector3.forward, dir);
                        bp.checkParticles.transform.position = b.transform.position - (Vector3)(dir * 0.3f);
                        bp.checkParticles.Emit(10);
                    }
                    var v = b.GetComponent<BallVibrator>();
                    if (v != null) v.presetVibrate(rumblePreset.CheckHit);
                    Stun.Briefly(b, stun);
                    pushed++;
                }
            var team = gm.teams.IndexOf(from.team);
            CardFx.ShockwaveRing(center, radius, team, target.transform.position.z);
            CardFx.ShockwaveCooldown(from, ShockwaveCooldown);
            if (Net.NetSession.HostInMatch) Net.NetSession.Instance.HostSendShockwave(center, radius, team, target.transform.position.z, Net.Match.Slot(from), ShockwaveCooldown);
            Net.NetLog.Info("Shockwave from " + from.name + ": pushed " + pushed + " opponent(s) (radius " + radius.ToString("F2") + ")");
        }

        /// <summary>
        /// Over Charge: how far this player's charge may fill (replaces the game's 3). Shot power is the charge itself,
        /// so a charge of 4.5 (one card, held to the end) hits 50% harder than a normal full charge.
        /// </summary>
        internal static float MaxCharge(BallHandling h)
        {
            var a = h != null ? AppliedOf(h.GetComponent<BallData>()) : null;
            return a != null ? GameFullCharge * (1f + OverChargeOf(a.Stats)) : GameFullCharge;
        }

        private static readonly FieldInfo ChargeSpeedField = AccessTools.Field(typeof(BallHandling), "ballChargeSpeed");
        private static readonly Dictionary<BallData, Original> _orig = new Dictionary<BallData, Original>();
        private static readonly Dictionary<BallData, Applied> _applied = new Dictionary<BallData, Applied>();
        private static readonly Dictionary<BallData, Dash> _dash = new Dictionary<BallData, Dash>();

        internal static void Clear()
        {
            _orig.Clear();
            _applied.Clear();
            _dash.Clear();
            _boostUntil.Clear();
            _shockAt.Clear();
            CardFx.Clear();
            CardObjects.Clear();
        }

        internal static CardStats StatsOf(Seat seat)
        {
            var stats = new CardStats();
            foreach (var id in seat.Cards)
            {
                var card = UltraPuck.Cards.Get(id);
                if (card == null) continue;
                try { card.Apply(stats); }
                catch (System.Exception ex) { Plugin.Logger_.LogError("Card " + id + " Apply failed: " + ex); }
            }
            return stats;
        }

        /// <summary>
        /// Apply everyone's cards. <paramref name="full"/> = this PC runs the game (host / offline); clients only
        /// resize balls and place pylons.
        /// </summary>
        internal static void RecomputeAll(bool full)
        {
            var seats = CardGame.Seats;
            var stats = seats.Select(StatsOf).ToList();

            // Team-wide effects. In Team Vote every member gets the team's card, so count it once per team.
            var teamSlow = new Dictionary<int, float>();
            var teamPylons = new Dictionary<int, int>();
            foreach (var t in seats.Select(s => s.Team).Distinct())
            {
                var members = Enumerable.Range(0, seats.Count).Where(i => seats[i].Team == t).Select(i => stats[i]).ToList();
                if (CardGame.TeamMode)
                {
                    teamSlow[t] = members.Min(m => m.OpponentSpeed);
                    teamPylons[t] = members.Max(m => m.Pylons);
                }
                else
                {
                    teamSlow[t] = members.Aggregate(1f, (a, m) => a * m.OpponentSpeed);
                    teamPylons[t] = members.Sum(m => m.Pylons);
                }
            }

            for (var i = 0; i < seats.Count; i++)
            {
                var b = seats[i].Ball;
                if (b == null) continue;
                var enemySlow = teamSlow.Where(kv => kv.Key != seats[i].Team).Aggregate(1f, (a, kv) => a * kv.Value);
                Apply(b, stats[i], enemySlow, full);
            }
            CardObjects.SetPylons(teamPylons);
            if (full) foreach (var s in seats) ApplyBallHooks(s);
        }

        private static void Apply(BallData b, CardStats st, float enemySlow, bool full)
        {
            Original o;
            if (!_orig.TryGetValue(b, out o))
            {
                var h = b.GetComponent<BallHandling>();
                _orig[b] = o = new Original
                {
                    Strength = b.strength, Mass = b.mass, Scale = b.transform.localScale,
                    ChargeSpeed = h != null && ChargeSpeedField != null ? (float)ChargeSpeedField.GetValue(h) : GameChargeSpeed,
                };
            }
            b.transform.localScale = o.Scale * Mathf.Clamp(st.Size, 0.3f, 3f);
            if (!full) return;

            var weight = MassOf(st);
            b.strength = o.Strength * Mathf.Clamp(st.ShotPower, 0.2f, 5f);
            b.mass = o.Mass * weight;
            var handling = b.GetComponent<BallHandling>();
            if (handling != null && ChargeSpeedField != null)
                ChargeSpeedField.SetValue(handling, o.ChargeSpeed * Mathf.Clamp(st.ChargeSpeed, 0.2f, MaxChargeSpeed));

            // Movement is a force divided by mass, so weight is cancelled out here: weight only changes knockback.
            _applied[b] = new Applied { Stats = st, SpeedFactor = Mathf.Clamp(st.MoveSpeed, 0.2f, 4f) * Mathf.Clamp(enemySlow, 0.2f, 1f) * weight };

            if (st.ExtraDashes > 0 || !Mathf.Approximately(st.DashCooldown, 1f))
            {
                Dash d;
                if (!_dash.TryGetValue(b, out d)) _dash[b] = d = new Dash();
                d.Max = 1 + Mathf.Clamp(st.ExtraDashes, 0, 9);
                d.Cooldown = GameDashCooldown * Mathf.Clamp(st.DashCooldown, 0.1f, 5f);
                d.Charges = d.Max; // fresh charges after picking
            }
            else _dash.Remove(b);
        }

        internal static void ApplyBallHooks(Seat seat)
        {
            if (seat.Ball == null) return;
            foreach (var g in seat.Cards.GroupBy(id => id))
            {
                var card = UltraPuck.Cards.Get(g.Key);
                if (card == null) continue;
                try { card.ApplyToBall(seat.Ball, g.Count()); }
                catch (System.Exception ex) { Plugin.Logger_.LogError("Card " + g.Key + " ApplyToBall failed: " + ex); }
            }
        }

        /// <summary>A player's size without cards (pylons use it, so they don't grow with Heavyweight).</summary>
        internal static Vector3 BaseScale(BallData b)
        {
            Original o;
            return b != null && _orig.TryGetValue(b, out o) ? o.Scale : (b != null ? b.transform.localScale : Vector3.one);
        }

        internal static Applied AppliedOf(BallData b)
        {
            Applied a;
            return b != null && _applied.TryGetValue(b, out a) ? a : null;
        }

        internal static Dash DashOf(BallData b)
        {
            Dash d;
            return b != null && _dash.TryGetValue(b, out d) ? d : null;
        }

        // ------------------------------------------------------------------ per frame (host / offline)

        internal static void Tick(float dt)
        {
            foreach (var d in _dash.Values)
                if (d.Charges < d.Max) d.Charges = Mathf.Min(d.Max, d.Charges + dt / d.Cooldown);
            PuckMagnet();
        }

        private static GameObject _lastCarrier;
        private static float _puckFreeSince = -1f;

        /// <summary>Puck Magnet: grab a loose puck from further away (the game only picks it up on contact).</summary>
        private static void PuckMagnet()
        {
            var gm = Globals.gameManager;
            if (gm == null || gm.puck == null || gm.roundOver || gm.replayManager.replaying || gm.startCountdown > 0f) return;
            var puck = gm.puck.GetComponent<Puck>();
            if (puck == null) return;
            var now = Time.time;
            if (puck.carryer != null) { _lastCarrier = puck.carryer; _puckFreeSince = -1f; return; }
            if (_puckFreeSince < 0f) _puckFreeSince = now;
            foreach (var kv in _applied)
            {
                var b = kv.Key;
                if (b == null || kv.Value.Stats.PickupRange <= 1.001f) continue;
                if (b.gameObject == _lastCarrier && now - _puckFreeSince < 0.6f) continue; // don't re-grab your own shot
                if (!b.alive || b.fallen || !b.canCarry || b.carryObject != null) continue;
                var h = b.GetComponent<BallHandling>();
                if (h == null || h.canCarryTimer > 0f) continue;
                var touch = Radius(b.GetComponent<Collider2D>()) + Radius(gm.puck.GetComponent<Collider2D>());
                var dist = Vector2.Distance(b.transform.position, gm.puck.transform.position);
                if (dist > touch * kv.Value.Stats.PickupRange) continue;
                h.Carry(gm.puck);
                Net.NetLog.Debug("Puck Magnet: " + b.name + " grabbed the puck from " + dist.ToString("F2") + " (touch " + touch.ToString("F2") + ")");
                var v = b.GetComponent<BallVibrator>();
                if (v != null) v.presetVibrate(rumblePreset.PickUp);
                return;
            }
        }

        internal static float Radius(Collider2D c)
        {
            if (c == null) return 0.4f;
            var circle = c as CircleCollider2D;
            var s = c.transform.lossyScale;
            if (circle != null) return circle.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y));
            return Mathf.Max(c.bounds.extents.x, c.bounds.extents.y);
        }

        /// <summary>
        /// Head Start: at every face-off, the team with the most Head Start cards starts with the puck (a random player
        /// on it gets it). A tie, including both teams having none, leaves the puck in the middle. In Team Vote
        /// everyone on a team gets the team's card, so it counts once per team there.
        /// </summary>
        private static void HeadStartPuck(GameManager gm)
        {
            var seats = CardGame.Seats;
            if (seats.Count == 0) return;
            var count = new Dictionary<int, int>();
            foreach (var t in seats.Select(s => s.Team).Distinct())
            {
                var per = seats.Where(s => s.Team == t).Select(s => StatsOf(s).HeadStart).ToList();
                count[t] = CardGame.TeamMode ? per.Max() : per.Sum();
            }
            var best = count.Values.Max();
            if (best <= 0 || count.Values.Count(v => v == best) > 1) return;
            var team = count.First(kv => kv.Value == best).Key;
            var players = seats.Where(s => s.Team == team && s.Ball != null && s.Ball.alive && !s.Ball.fallen).Select(s => s.Ball).ToList();
            if (players.Count == 0) return;
            var b = players[Random.Range(0, players.Count)];
            var h = b.GetComponent<BallHandling>();
            if (h == null) return;
            h.Carry(gm.puck);
            Net.NetLog.Info("Head Start: team " + (team + 1) + " (" + best + " vs " + string.Join("/", count.Values.Select(v => v.ToString()).ToArray()) + ") starts with the puck: " + b.name);
        }

        /// <summary>Head Start: after a face-off reset, move each player part of the way toward the puck (SpawnCloser, for mods).</summary>
        internal static void AfterFaceOff()
        {
            var gm = Globals.gameManager;
            if (gm == null || gm.puck == null) return;
            HeadStartPuck(gm);
            var target = gm.puck.transform.position;
            foreach (var kv in _applied)
            {
                var b = kv.Key;
                var f = Mathf.Clamp(kv.Value.Stats.SpawnCloser, 0f, 0.6f);
                if (b == null || f <= 0f) continue;
                var p = b.transform.position;
                var to = Vector3.Lerp(p, new Vector3(target.x, target.y, p.z), f);
                Net.NetLog.Debug("Head Start: moved " + Vector2.Distance(p, to).ToString("F2") + " toward the puck (" + Vector2.Distance(p, target).ToString("F2") + " away)");
                b.transform.position = to;
                var rb = b.GetComponent<Rigidbody2D>();
                if (rb != null) rb.position = to;
            }
        }

        /// <summary>Puck speed limit: the game caps the puck at 90; Power Shot raises it for its owner's shots.</summary>
        internal static float PuckCap()
        {
            var gm = Globals.gameManager;
            var cap = GamePuckCap;
            if (gm != null && gm.puck != null)
            {
                var last = gm.puck.GetComponent<Puck>().lastCarryer;
                var a = last != null ? AppliedOf(last.GetComponent<BallData>()) : null;
                if (a != null) cap = GamePuckCap * Mathf.Max(1f, a.Stats.ShotPower) * (1f + OverChargeOf(a.Stats));
            }
            // Unity's 2D physics has its own speed limit (Physics2D.maxTranslationSpeed, 100 by default) just above the
            // game's 90, so a raised cap alone changes nothing: lift Unity's limit with it, and put it back after.
            if (_baseTranslation < 0f) _baseTranslation = Physics2D.maxTranslationSpeed;
            var want = Mathf.Max(_baseTranslation, cap * 1.1f);
            if (!Mathf.Approximately(Physics2D.maxTranslationSpeed, want)) Physics2D.maxTranslationSpeed = want;
            return cap;
        }
        private static float _baseTranslation = -1f;

        /// <summary>
        /// A player's stats after cards, as changes from normal, for the stats tooltip. Works on every PC (it only
        /// needs everyone's card list), and includes what the other team's cards do to this player (Slow Poke).
        /// </summary>
        internal static List<string> StatLines(int seatIndex)
        {
            var lines = new List<string>();
            var seats = CardGame.Seats;
            if (seatIndex < 0 || seatIndex >= seats.Count) return lines;
            var stats = seats.Select(StatsOf).ToList();
            var me = seats[seatIndex];
            var st = stats[seatIndex];

            // Team-wide effects, counted the same way as RecomputeAll (once per team in Team Vote).
            var enemySlow = 1f;
            var pylons = 0;
            foreach (var t in seats.Select(s => s.Team).Distinct())
            {
                var members = Enumerable.Range(0, seats.Count).Where(i => seats[i].Team == t).Select(i => stats[i]).ToList();
                var slow = CardGame.TeamMode ? members.Min(m => m.OpponentSpeed) : members.Aggregate(1f, (a, m) => a * m.OpponentSpeed);
                if (t != me.Team) enemySlow *= slow;
                else pylons = CardGame.TeamMode ? members.Max(m => m.Pylons) : members.Sum(m => m.Pylons);
            }

            // Same terms and colours as the card texts (UltraPuck.CardText): green helps you, red hurts, gold neutral.
            var speed = Mathf.Clamp(st.MoveSpeed, 0.2f, 4f) * Mathf.Clamp(enemySlow, 0.2f, 1f);
            if (!Same(speed, 1f))
                lines.Add(CardText.Stat(speed, "Move Speed", true) +
                    (enemySlow < 0.999f ? "  (includes " + CardText.Stat(enemySlow, "from opponents' Slow Poke", true) + ")" : string.Empty));
            if (!Same(MassOf(st), 1f)) lines.Add(CardText.Stat(1f / MassOf(st), "Knockback Taken", false));
            if (!Same(GivenOf(st), 1f)) lines.Add(CardText.Stat(GivenOf(st), "Knockback Given", true));
            if (!Same(st.MoveSpeedWithPuck, 1f))
                lines.Add(CardText.Neutral(Pct(Mathf.Clamp(st.MoveSpeed, 0.2f, 4f) * Mathf.Clamp(st.MoveSpeedWithPuck, 0.2f, 4f) * Mathf.Clamp(enemySlow, 0.2f, 1f)) + " Move Speed with the puck"));
            if (st.Shockwave > 0) lines.Add(CardText.Good("Shockwave") + ": your dash hits push away and " + CardText.Good("stun (" + ShockwaveStun(st.Shockwave).ToString("0.0") + " s)") +
                " opponents nearby  (" + ShockwaveRange(st.Shockwave).ToString("0.##") + "x a player's size, " + ShockwaveCooldown.ToString("0") + " s cooldown)");
            if (!Same(st.Size, 1f)) lines.Add(CardText.Stat(Mathf.Clamp(st.Size, 0.3f, 3f), "Size", true, true));
            if (!Same(st.ShotPower, 1f)) lines.Add(CardText.Stat(Mathf.Clamp(st.ShotPower, 0.2f, 5f), "Shot Power", true));
            if (!Same(st.QuickShotPower, 1f)) lines.Add(CardText.Stat(Mathf.Clamp(st.QuickShotPower, 0.2f, 5f), "Quick Shot Power", true));
            if (!Same(st.ChargeSpeed, 1f)) lines.Add(CardText.Stat(1f / Mathf.Clamp(st.ChargeSpeed, 0.2f, MaxChargeSpeed), "Charge-up Time", false));
            if (st.OverCharge > 0f) lines.Add(CardText.Good("+" + Mathf.RoundToInt(OverChargeOf(st) * 100f) + "% Over Charge") + "  (hold past full for a harder charged shot)");
            if (st.ExtraDashes > 0) lines.Add(CardText.Good("+" + Mathf.Clamp(st.ExtraDashes, 0, 9) + " Dash Charge" + (st.ExtraDashes > 1 ? "s" : string.Empty)) +
                "  (" + (1 + Mathf.Clamp(st.ExtraDashes, 0, 9)) + " total)");
            if (!Same(st.DashCooldown, 1f))
                lines.Add(CardText.Stat(1f / Mathf.Clamp(st.DashCooldown, 0.1f, 5f), "Dash Recharge Speed", true) +
                    "  (" + (GameDashCooldown * Mathf.Clamp(st.DashCooldown, 0.1f, 5f)).ToString("0.0") + " s per charge, normally 2.0 s)");
            if (!Same(st.PickupRange, 1f)) lines.Add(CardText.Stat(st.PickupRange, "Pick-up Range", true));
            if (st.SpawnCloser > 0f) lines.Add(CardText.Stat(1f - Mathf.Clamp(st.SpawnCloser, 0f, 0.6f), "Face-off Distance", false));
            if (st.HeadStart > 0) lines.Add(CardText.Good("Head Start x" + st.HeadStart) + "  (the team with the most starts face-offs with the puck)");
            if (st.OpponentSpeed < 0.999f) lines.Add(CardText.Stat(st.OpponentSpeed, "Opponent Move Speed", false));
            if (pylons > 0) lines.Add(CardText.Good("+" + Mathf.Min(pylons, 4) + " Pylon Goalie" + (pylons > 1 ? "s" : string.Empty)) + "  (your team's net)");
            if (st.PuckGun) lines.Add("Puck Gun: brake to fire a " + CardText.Good("1 s Stun") + " shot  (2 s cooldown)");
            if (st.Invisibility) lines.Add("Invisibility: brake to turn " + CardText.Good("invisible for 5 s") + "  (15 s cooldown)");
            if (!Same(st.FriendlyFire, 1f)) lines.Add(CardText.Stat(FriendlyFireOf(st), "Teammate Knockback", true) + "  (dashes and Puck Gun)");
            if (st.TeammateBoost) lines.Add("Friendly Fire: a teammate's dash into you gives " + CardText.Good("+80% Move Speed for 2 s") + "  (bot swaps pass it on)");
            if (lines.Count == 0) lines.Add("No changes from cards.");
            return lines;
        }

        private static bool Same(float a, float b) { return Mathf.Abs(a - b) < 0.005f; }

        private static string Pct(float m) { var p = Mathf.RoundToInt((m - 1f) * 100f); return (p > 0 ? "+" : string.Empty) + p + "%"; }

        internal static string Describe(BallData b)
        {
            var d = DashOf(b);
            var a = AppliedOf(b);
            return "speedFactor=" + (a != null ? a.SpeedFactor.ToString("F2") : "1") + " strength=" + b.strength.ToString("F1") +
                " mass=" + b.mass.ToString("F2") + " scale=" + b.transform.localScale.x.ToString("F2") +
                (d != null ? " dashes=" + d.Max + " cooldown=" + d.Cooldown.ToString("F2") : string.Empty) +
                (a != null && a.Stats.PickupRange > 1.001f ? " pickup=x" + a.Stats.PickupRange.ToString("F1") : string.Empty) +
                (a != null && a.Stats.SpawnCloser > 0f ? " spawnCloser=" + a.Stats.SpawnCloser.ToString("F2") : string.Empty) +
                (a != null && a.Stats.PuckGun ? " puckGun" : string.Empty);
        }
    }

    /// <summary>Card speed, opponents' Slow Poke and weight compensation, on top of the game's own speed maths.</summary>
    [HarmonyPatch(typeof(BallData), "Update")]
    internal static class SpeedPatch
    {
        private static void Postfix(BallData __instance)
        {
            var a = CardEffects.AppliedOf(__instance);
            if (a == null || !CardGame.IsAuthority) return;
            __instance.totalSpeed *= a.SpeedFactor * CardEffects.BoostFactor(__instance) *
                (__instance.carryObject != null ? Mathf.Clamp(a.Stats.MoveSpeedWithPuck, 0.2f, 4f) : 1f);
        }
    }

    /// <summary>Over Charge: let the charge fill past the game's full charge (3) in BallHandling.Update.</summary>
    [HarmonyPatch(typeof(BallHandling), "Update")]
    internal static class OverChargeClampPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var list = code.ToList();
            var clamp = AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp), new[] { typeof(float), typeof(float), typeof(float) });
            var max = AccessTools.Method(typeof(CardEffects), nameof(CardEffects.MaxCharge));
            var hooked = 0;
            for (var i = 1; i < list.Count - 1; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_R4 && list[i].operand is float && Mathf.Approximately((float)list[i].operand, 3f) &&
                    list[i - 1].opcode == OpCodes.Ldc_R4 && list[i - 1].operand is float && Mathf.Approximately((float)list[i - 1].operand, 0.5f) &&
                    list[i + 1].Calls(clamp))
                {
                    var load = new CodeInstruction(OpCodes.Ldarg_0) { labels = list[i].labels, blocks = list[i].blocks };
                    list[i] = new CodeInstruction(OpCodes.Call, max);
                    list.Insert(i, load);
                    hooked++;
                }
            }
            Net.NetLog.Info("Over Charge: charge limit hooked x" + hooked);
            return list;
        }
    }

    /// <summary>Power Shot: raise the puck's speed limit (a hard 90 in Puck.FixedUpdate) for powered shots.</summary>
    [HarmonyPatch(typeof(Puck), "FixedUpdate")]
    internal static class PuckCapPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var cap = AccessTools.Method(typeof(CardEffects), nameof(CardEffects.PuckCap));
            foreach (var ins in code)
            {
                if (ins.opcode == OpCodes.Ldc_R4 && ins.operand is float && Mathf.Approximately((float)ins.operand, 90f))
                    yield return new CodeInstruction(OpCodes.Call, cap) { labels = ins.labels, blocks = ins.blocks };
                else yield return ins;
            }
        }
    }

    /// <summary>
    /// Knockback Given: a dash check pushes the opponent with (attacker velocity x 18) in BallShove's collision
    /// handlers. Multiply that 18 by the attacker's Weight, so heavy players' checks push further.
    /// </summary>
    [HarmonyPatch]
    internal static class CheckForcePatch
    {
        private const float GameCheckForce = 18f;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(BallShove), "OnCollisionEnter2D");
            yield return AccessTools.Method(typeof(BallShove), "OnCollisionStay2D");
        }

        internal static float CheckForce(BallShove shove, Collision2D hit)
        {
            if (!CardGame.IsAuthority) return GameCheckForce;
            var me = shove.GetComponent<BallData>();
            var a = CardEffects.AppliedOf(me);
            if (a == null) return GameCheckForce;
            var w = CardEffects.GivenOf(a.Stats);
            var ff = CardEffects.FriendlyFireMul(me, hit != null && hit.gameObject != null ? hit.gameObject.GetComponent<BallData>() : null);
            if (Mathf.Approximately(w * ff, 1f)) return GameCheckForce;
            Net.NetLog.Debug("Check force x" + w.ToString("F2") + (ff > 1.001f ? " (Friendly Fire x" + ff.ToString("F1") + ")" : string.Empty));
            return GameCheckForce * w * ff;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var force = AccessTools.Method(typeof(CheckForcePatch), nameof(CheckForce));
            var n = 0;
            foreach (var ins in code)
            {
                if (ins.opcode == OpCodes.Ldc_R4 && ins.operand is float && Mathf.Approximately((float)ins.operand, GameCheckForce))
                {
                    n++;
                    yield return new CodeInstruction(OpCodes.Ldarg_0) { labels = ins.labels, blocks = ins.blocks };
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Call, force);
                }
                else yield return ins;
            }
            if (n != 1) Plugin.Logger_.LogWarning("Knockback Given: expected one check force in a BallShove collision handler, found " + n);
            else Plugin.Logger_.LogInfo("Knockback Given: check force hooked");
        }
    }

    /// <summary>
    /// Everything that happens on a dash:
    /// - Dash charges: the game allows one shove every 2 s (BallShove.shoveTimer). With dash cards, allow a shove
    ///   whenever there's a charge and use one up. The game's timer still runs, so a shove's hit window is unchanged.
    /// - Weight: the dash push is a force, so scale it by weight to keep the same dash speed for heavy/light players
    ///   (how hard the dash hits others is CheckForcePatch).
    /// - Puck Gun: fire a shot.
    /// </summary>
    [HarmonyPatch(typeof(BallShove), nameof(BallShove.Shove))]
    internal static class ShovePatch
    {
        // x = game timer before, y = timer handed to the game, z = 1 if dash charges manage this ball
        private static void Prefix(BallShove __instance, out Vector3 __state)
        {
            __state = new Vector3(__instance.shoveTimer, __instance.shoveTimer, 0f);
            if (!CardGame.IsAuthority) return;
            var d = CardEffects.DashOf(__instance.GetComponent<BallData>());
            if (d == null) return;
            if (d.Charges >= 1f) __instance.shoveTimer = 0f;
            else if (__instance.shoveTimer <= 0f) __instance.shoveTimer = 0.0001f; // no charge: block
            __state.y = __instance.shoveTimer;
            __state.z = 1f;
        }

        private static void Postfix(BallShove __instance, Vector3 __state)
        {
            if (!CardGame.IsAuthority) return;
            var b = __instance.GetComponent<BallData>();
            var fired = !Mathf.Approximately(__instance.shoveTimer, __state.y); // the game set its cooldown: it shoved

            if (__state.z > 0f)
            {
                var d = CardEffects.DashOf(b);
                var had = d != null ? d.Charges : 0f;
                if (fired && d != null) d.Charges -= 1f;
                if (!fired) __instance.shoveTimer = __state.x;
                Net.NetLog.Debug("Dash " + (b.playerData != null && b.playerData.account != null ? b.playerData.account.name : "?") +
                    (fired ? " fired" : " blocked") + " charges " + had.ToString("F2") + " -> " + (d != null ? d.Charges : 0f).ToString("F2") +
                    " canShove=" + b.canShove + " carrying=" + (b.carryObject != null) +
                    " replaying=" + Globals.gameManager.replayManager.replaying);
            }
            if (!fired) return;

            var a = CardEffects.AppliedOf(b);
            if (a == null) return;
            var weight = CardEffects.MassOf(a.Stats);
            if (!Mathf.Approximately(weight, 1f))
            {
                var rb = b.GetComponent<Rigidbody2D>();
                if (rb != null) rb.AddForce(b.transform.up * CardEffects.GameDashForce * (weight - 1f));
            }
        }
    }
}
