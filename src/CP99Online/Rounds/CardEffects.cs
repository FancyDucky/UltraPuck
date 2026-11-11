using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UltraPuck;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>Turns a seat's cards into ball stats. Runs on the host / offline only (it simulates every ball).</summary>
    internal static class CardEffects
    {
        private sealed class Original { public float Speed, Strength, Mass; }

        internal sealed class Dash
        {
            public int Max = 1;
            public float Charges = 1f;
            public float Cooldown = 2f;
        }

        private const float GameDashCooldown = 2f; // BallShove.shoveCooldown

        private static readonly Dictionary<BallData, Original> _orig = new Dictionary<BallData, Original>();
        private static readonly Dictionary<BallData, Dash> _dash = new Dictionary<BallData, Dash>();

        internal static void Clear()
        {
            _orig.Clear();
            _dash.Clear();
        }

        internal static void Recompute(Seat seat)
        {
            var b = seat.Ball;
            if (b == null) return;
            Original o;
            if (!_orig.TryGetValue(b, out o)) _orig[b] = o = new Original { Speed = b.baseSpeed, Strength = b.strength, Mass = b.mass };

            var stats = new CardStats();
            foreach (var id in seat.Cards)
            {
                var card = UltraPuck.Cards.Get(id);
                if (card == null) continue;
                try { card.Apply(stats); }
                catch (System.Exception ex) { Plugin.Logger_.LogError("Card " + id + " Apply failed: " + ex); }
            }
            b.baseSpeed = o.Speed * Mathf.Clamp(stats.MoveSpeed, 0.2f, 4f);
            b.strength = o.Strength * Mathf.Clamp(stats.ShotPower, 0.2f, 5f);
            b.mass = o.Mass * Mathf.Clamp(stats.Weight, 0.2f, 5f);

            if (stats.ExtraDashes > 0 || !Mathf.Approximately(stats.DashCooldown, 1f))
            {
                Dash d;
                if (!_dash.TryGetValue(b, out d)) _dash[b] = d = new Dash();
                d.Max = 1 + Mathf.Clamp(stats.ExtraDashes, 0, 9);
                d.Cooldown = GameDashCooldown * Mathf.Clamp(stats.DashCooldown, 0.1f, 5f);
                d.Charges = d.Max; // fresh charges after picking
            }
            else _dash.Remove(b);

            ApplyBallHooks(seat);
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

        internal static Dash DashOf(BallData b)
        {
            Dash d;
            return b != null && _dash.TryGetValue(b, out d) ? d : null;
        }

        /// <summary>Recharge dash charges (called every frame).</summary>
        internal static void Tick(float dt)
        {
            foreach (var d in _dash.Values)
                if (d.Charges < d.Max) d.Charges = Mathf.Min(d.Max, d.Charges + dt / d.Cooldown);
        }

        internal static string Describe(BallData b)
        {
            var d = DashOf(b);
            return "speed=" + b.baseSpeed.ToString("F0") + " strength=" + b.strength.ToString("F1") + " mass=" + b.mass.ToString("F2") +
                (d != null ? " dashes=" + d.Max + " cooldown=" + d.Cooldown.ToString("F2") : string.Empty);
        }
    }

    /// <summary>
    /// Dash charges: the game allows one shove every 2 s (BallShove.shoveTimer). For a ball with dash cards,
    /// let it shove whenever it has a charge, and use up a charge when it does. The game's own timer still
    /// runs, so a shove's "hit" window works as usual.
    /// </summary>
    [HarmonyPatch(typeof(BallShove), nameof(BallShove.Shove))]
    internal static class DashChargesPatch
    {
        private static void Prefix(BallShove __instance, out float __state)
        {
            __state = -1f;
            if (!CardGame.IsAuthority) return;
            var d = CardEffects.DashOf(__instance.GetComponent<BallData>());
            if (d == null) return;
            __state = __instance.shoveTimer;
            if (d.Charges >= 1f) __instance.shoveTimer = 0f;
            else if (__instance.shoveTimer <= 0f) __instance.shoveTimer = 0.0001f; // no charge: block
        }

        private static void Postfix(BallShove __instance, float __state)
        {
            if (__state < 0f) return;
            var d = CardEffects.DashOf(__instance.GetComponent<BallData>());
            if (d == null) return;
            var fired = d.Charges >= 1f && __instance.shoveTimer > 0f; // the game set its cooldown: it shoved
            if (fired) d.Charges -= 1f;
            else __instance.shoveTimer = __state;
        }
    }
}
