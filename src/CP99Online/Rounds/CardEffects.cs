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

            var weight = Mathf.Clamp(st.Weight, 0.1f, 10f);
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

        /// <summary>Head Start: after a face-off reset, move each player part of the way toward the puck.</summary>
        internal static void AfterFaceOff()
        {
            var gm = Globals.gameManager;
            if (gm == null || gm.puck == null) return;
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
            var weight = Mathf.Clamp(st.Weight, 0.1f, 10f);
            if (!Same(weight, 1f))
            {
                lines.Add(CardText.Stat(1f / weight, "Knockback Taken", false));
                lines.Add(CardText.Stat(weight, "Knockback Given", true));
            }
            if (!Same(st.Size, 1f)) lines.Add(CardText.Stat(Mathf.Clamp(st.Size, 0.3f, 3f), "Size", true, true));
            if (!Same(st.ShotPower, 1f)) lines.Add(CardText.Stat(Mathf.Clamp(st.ShotPower, 0.2f, 5f), "Shot Power", true));
            if (!Same(st.ChargeSpeed, 1f)) lines.Add(CardText.Stat(1f / Mathf.Clamp(st.ChargeSpeed, 0.2f, MaxChargeSpeed), "Charge-up Time", false));
            if (st.OverCharge > 0f) lines.Add(CardText.Good("+" + Mathf.RoundToInt(OverChargeOf(st) * 100f) + "% Over Charge") + "  (hold past full for a harder charged shot)");
            if (st.ExtraDashes > 0) lines.Add(CardText.Good("+" + Mathf.Clamp(st.ExtraDashes, 0, 9) + " Dash Charge" + (st.ExtraDashes > 1 ? "s" : string.Empty)) +
                "  (" + (1 + Mathf.Clamp(st.ExtraDashes, 0, 9)) + " total)");
            if (!Same(st.DashCooldown, 1f))
                lines.Add(CardText.Stat(Mathf.Clamp(st.DashCooldown, 0.1f, 5f), "Dash Recharge", false) +
                    "  (" + (GameDashCooldown * Mathf.Clamp(st.DashCooldown, 0.1f, 5f)).ToString("0.0") + " s per charge, normally 2.0 s)");
            if (!Same(st.PickupRange, 1f)) lines.Add(CardText.Stat(st.PickupRange, "Pick-up Range", true));
            if (st.SpawnCloser > 0f) lines.Add(CardText.Stat(1f - Mathf.Clamp(st.SpawnCloser, 0f, 0.6f), "Face-off Distance", false));
            if (st.OpponentSpeed < 0.999f) lines.Add(CardText.Stat(st.OpponentSpeed, "Opponent Move Speed", false));
            if (pylons > 0) lines.Add(CardText.Good("+" + Mathf.Min(pylons, 4) + " Pylon Goalie" + (pylons > 1 ? "s" : string.Empty)) + "  (your team's net)");
            if (st.PuckGun) lines.Add("Puck Gun: every dash fires a " + CardText.Good("1 s Stun") + " shot");
            if (st.Invisibility) lines.Add("Invisibility: brake to turn " + CardText.Good("invisible for 5 s") + "  (12 s cooldown)");
            if (!Same(st.FriendlyFire, 1f)) lines.Add(CardText.Stat(FriendlyFireOf(st), "Teammate Knockback", true) + "  (dashes and Puck Gun)");
            if (lines.Count == 0) lines.Add("No changes from cards.");
            return lines;
        }

        private static bool Same(float a, float b) { return Mathf.Abs(a - b) < 0.005f; }

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
            if (a != null && CardGame.IsAuthority) __instance.totalSpeed *= a.SpeedFactor;
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
            var w = Mathf.Clamp(a.Stats.Weight, 0.1f, 10f);
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
            var weight = Mathf.Clamp(a.Stats.Weight, 0.1f, 10f);
            if (!Mathf.Approximately(weight, 1f))
            {
                var rb = b.GetComponent<Rigidbody2D>();
                if (rb != null) rb.AddForce(b.transform.up * CardEffects.GameDashForce * (weight - 1f));
            }
            if (a.Stats.PuckGun) CardObjects.FirePuckGun(b);
        }
    }
}
