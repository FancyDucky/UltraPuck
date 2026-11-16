using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UltraPuck
{
    /// <summary>
    /// What a player's cards add up to. Every card the player owns gets to change it (see UltraCard.Apply),
    /// then UltraPuck applies the result to the player's ball. Multipliers start at 1.
    /// </summary>
    public sealed class CardStats
    {
        /// <summary>Movement speed multiplier (1.15 = 15% faster).</summary>
        public float MoveSpeed = 1f;
        /// <summary>Shot strength multiplier (every shot).</summary>
        public float ShotPower = 1f;
        /// <summary>Quick shot strength multiplier (shots fired without charging; 1.5 = 50% harder).</summary>
        public float QuickShotPower = 1f;
        /// <summary>Weight multiplier. Heavier = pushed less by hits (Knockback Taken = 1 / Weight) and your dash checks push
        /// others further (Knockback Given = Weight). Doesn't change your speed.</summary>
        public float Weight = 1f;
        /// <summary>Knockback from hits and bumps you receive (0.2 = 80% less). On top of Weight.</summary>
        public float KnockbackTaken = 1f;
        /// <summary>How far your dash checks push others (1.8 = 80% further). On top of Weight.</summary>
        public float KnockbackGiven = 1f;
        /// <summary>Extra move speed multiplier while you carry the puck, on top of MoveSpeed (0.88 with MoveSpeed 1.25 = +10% with the puck).</summary>
        public float MoveSpeedWithPuck = 1f;
        /// <summary>Shockwave copies: your dash hits on opponents slow them and every opponent near them (30% for 2 s, +5% and +0.5 s per extra copy) and push the others away.</summary>
        public int Shockwave;
        /// <summary>Dash (boost) recharge time multiplier (0.5 = takes half as long).</summary>
        public float DashCooldown = 1f;
        /// <summary>Extra dash charges on top of the normal one.</summary>
        public int ExtraDashes;
        /// <summary>Ball size multiplier (1.5 = 50% bigger).</summary>
        public float Size = 1f;
        /// <summary>How fast a charge shot fills up (1.35 = 35% faster). Shot Power is how hard the shot is.</summary>
        public float ChargeSpeed = 1f;
        /// <summary>How far a charge shot can fill past full, as a fraction of a full charge (0.5 = up to 50% more power).</summary>
        public float OverCharge;
        /// <summary>Multiplies the movement speed of every opponent (0.9 = opponents 10% slower).</summary>
        public float OpponentSpeed = 1f;
        /// <summary>How far toward the puck you start each face-off (0.2 = 20% closer).</summary>
        public float SpawnCloser;
        /// <summary>Head Start copies: at every face-off, the team with the most starts with the puck (a tie: nobody).</summary>
        public int HeadStart;
        /// <summary>Pylons (a solid dummy player) placed in front of your team's net.</summary>
        public int Pylons;
        /// <summary>Braking fires a shot (2 s cooldown) that stuns the first player it hits (teammates too) for 1 s and
        /// knocks them back, or knocks the puck away.</summary>
        public bool PuckGun;
        /// <summary>How hard your dash checks and Puck Gun shots push teammates (2.5 = +150%).</summary>
        public float FriendlyFire = 1f;
        /// <summary>Braking turns you invisible to the other team for 5 s (15 s cooldown).</summary>
        public bool Invisibility;
        /// <summary>A teammate dashing into you gives you +80% move speed for 2 s.</summary>
        public bool TeammateBoost;
    }

    /// <summary>How strong a card is, shown as the colour of its border (None: no tier border).</summary>
    public enum CardTier { None, S, A, B, C, D, F }

    /// <summary>
    /// A card for Rounds Mode. After each goal, players pick one of a few random cards; the card stays with
    /// them for the rest of the match. Register cards with <see cref="Cards.Register"/> (e.g. in your mod's OnLoad).
    /// </summary>
    public abstract class UltraCard
    {
        /// <summary>Unique id, e.g. "yourname.bigshot". Must be the same on every player's PC.</summary>
        public abstract string Id { get; }

        /// <summary>Short name shown on the card (about 16 characters fit).</summary>
        public abstract string Name { get; }

        /// <summary>One or two short sentences shown on the card.</summary>
        public abstract string Description { get; }

        /// <summary>The card's accent colour.</summary>
        public virtual Color Color { get { return new Color(0.35f, 0.55f, 0.85f); } }

        /// <summary>How many times one player can own this card (0 = no limit).</summary>
        public virtual int MaxPerPlayer { get { return 0; } }

        /// <summary>How often the card is offered compared to others (1 = normal, 0.5 = half as often).</summary>
        public virtual float Rarity { get { return 1f; } }

        /// <summary>How strong the card is: its border colour on the card select screen (S best, F worst).</summary>
        public virtual CardTier Tier { get { return CardTier.None; } }

        /// <summary>Whether the card starts switched on in Mods > Rounds Mode > Toggle Cards.</summary>
        public virtual bool OnByDefault { get { return true; } }

        /// <summary>Can this card be offered to a player whose team has <paramref name="teamSize"/> players (bots included)?</summary>
        public virtual bool CanOffer(int teamSize) { return true; }

        /// <summary>Change the player's stats. Called once per copy of the card the player owns.</summary>
        public virtual void Apply(CardStats stats) { }

        /// <summary>
        /// For effects that aren't a stat: called with the player's ball after their stats were applied
        /// (when they get a card, and after every field reset). <paramref name="copies"/> = how many they own.
        /// Runs on the host (or the only PC, offline): online, the host runs the game for everyone.
        /// </summary>
        public virtual void ApplyToBall(BallData ball, int copies) { }
    }

    /// <summary>All cards Rounds Mode can offer.</summary>
    public static class Cards
    {
        private static readonly List<UltraCard> _all = new List<UltraCard>
        {
            new DoubleDashCard(), new SpeedUpCard(), new PowerShotCard(),
            new QuickRechargeCard(), new HeavyweightCard(), new FeatherweightCard(),
            new OverChargeCard(), new SlowPokeCard(), new HeadStartCard(),
            new PylonGoalieCard(), new PuckGunCard(),
            new FriendlyFireCard(), new InvisibilityCard(), new ShockwaveCard(),
        };

        /// <summary>Every registered card, built-in ones first.</summary>
        public static UltraCard[] All { get { return _all.ToArray(); } }

        /// <summary>Add a card to the pool. Ids must be unique.</summary>
        public static void Register(UltraCard card)
        {
            if (card == null) throw new ArgumentNullException("card");
            if (_all.Any(c => c.Id == card.Id)) return;
            _all.Add(card);
        }

        /// <summary>Find a card by id (null if it isn't registered on this PC).</summary>
        public static UltraCard Get(string id) { return _all.FirstOrDefault(c => c.Id == id); }
    }

    /// <summary>
    /// Coloured text for card descriptions and stats, so every card uses the same terms and colours:
    /// green = good for you, red = bad for you, gold = neutral (neither good nor bad).
    /// Standard terms: Move Speed, Knockback Taken, Knockback Given, Size, Shot Power, Charge-up Speed, Dash Charges, Dash Recharge,
    /// Pick-up Range, Face-off Distance, Opponent Move Speed, Stun, Pylon Goalie.
    /// </summary>
    public static class CardText
    {
        public const string GoodColor = "#7DDC7D", BadColor = "#FF7B7B", NeutralColor = "#FFD35A";

        public static string Good(string text) { return "<color=" + GoodColor + ">" + text + "</color>"; }
        public static string Bad(string text) { return "<color=" + BadColor + ">" + text + "</color>"; }
        public static string Neutral(string text) { return "<color=" + NeutralColor + ">" + text + "</color>"; }

        /// <summary>
        /// A percentage modifier like "+15% Move Speed", coloured by whether it helps you.
        /// <paramref name="multiplier"/> 1.15 = +15%; <paramref name="higherIsBetter"/> false for things like Knockback Taken.
        /// Pass <paramref name="neutral"/> for things that are neither (Size).
        /// </summary>
        public static string Stat(float multiplier, string term, bool higherIsBetter, bool neutral = false)
        {
            var p = UnityEngine.Mathf.RoundToInt((multiplier - 1f) * 100f);
            var text = (p > 0 ? "+" : string.Empty) + p + "% " + term;
            if (neutral || p == 0) return Neutral(text);
            return (p > 0) == higherIsBetter ? Good(text) : Bad(text);
        }

        /// <summary>Removes colour tags (for places that show plain text).</summary>
        public static string Plain(string text) { return System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, "</?color[^>]*>", string.Empty); }
    }

    // ------------------------------------------------------------------ built-in cards

    internal sealed class DoubleDashCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.S; } }
        public override string Id { get { return "ultrapuck.doubledash"; } }
        public override string Name { get { return "Double Dash"; } }
        public override string Description { get { return CardText.Good("+1 Dash Charge") + "\nDash twice before recharging."; } }
        public override Color Color { get { return new Color(0.30f, 0.55f, 0.95f); } }
        public override void Apply(CardStats s) { s.ExtraDashes++; }
    }

    internal sealed class SpeedUpCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.A; } }
        public override string Id { get { return "ultrapuck.speedup"; } }
        public override string Name { get { return "Speed Up"; } }
        public override string Description { get { return CardText.Stat(1.15f, "Move Speed", true); } }
        public override Color Color { get { return new Color(0.30f, 0.80f, 0.40f); } }
        public override void Apply(CardStats s) { s.MoveSpeed *= 1.15f; }
    }

    internal sealed class PowerShotCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.S; } }
        public override string Id { get { return "ultrapuck.powershot"; } }
        public override string Name { get { return "Power Shot"; } }
        public override string Description { get { return CardText.Stat(1.5f, "Quick Shot Power", true) + "\nUncharged shots only."; } }
        public override Color Color { get { return new Color(0.92f, 0.33f, 0.30f); } }
        public override void Apply(CardStats s) { s.QuickShotPower *= 1.5f; }
    }

    internal sealed class QuickRechargeCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.A; } }
        public override string Id { get { return "ultrapuck.quickrecharge"; } }
        public override string Name { get { return "Quick Recharge"; } }
        public override string Description { get { return CardText.Stat(1.6f, "Dash Recharge Speed", true); } }
        public override Color Color { get { return new Color(0.25f, 0.80f, 0.85f); } }
        public override int MaxPerPlayer { get { return 3; } }
        public override void Apply(CardStats s) { s.DashCooldown /= 1.6f; }
    }

    internal sealed class HeavyweightCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.B; } }
        public override string Id { get { return "ultrapuck.heavyweight"; } }
        public override string Name { get { return "Heavyweight"; } }
        public override string Description { get { return CardText.Stat(1.8f, "Knockback Given", true) + "\n" + CardText.Stat(0.2f, "Knockback Taken", false) + "\n" + CardText.Stat(1.13f, "Size", true, true); } }
        public override Color Color { get { return new Color(0.62f, 0.45f, 0.30f); } }
        public override int MaxPerPlayer { get { return 3; } }
        public override void Apply(CardStats s) { s.KnockbackGiven *= 1.8f; s.KnockbackTaken *= 0.2f; s.Size *= 1.13f; }
    }

    internal sealed class FeatherweightCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.B; } }
        public override string Id { get { return "ultrapuck.featherweight"; } }
        public override string Name { get { return "Featherweight"; } }
        public override string Description { get { return CardText.Stat(1.25f, "Move Speed", true) + " " + CardText.Neutral("(+10% with the puck)") + "\n" + CardText.Stat(0.87f, "Size", true, true) + "\n" +
            CardText.Stat(1.8f, "Knockback Taken", false) + "\n" + CardText.Stat(0.2f, "Knockback Given", true); } }
        public override Color Color { get { return new Color(0.95f, 0.80f, 0.30f); } }
        public override int MaxPerPlayer { get { return 2; } }
        public override void Apply(CardStats s) { s.MoveSpeed *= 1.25f; s.MoveSpeedWithPuck *= 1.10f / 1.25f; s.Size *= 0.87f; s.KnockbackTaken *= 1.8f; s.KnockbackGiven *= 0.2f; }
    }

    /// <summary>
    /// Over Charge: each card halves the time to a full charge shot, and lets the charge keep filling past full for
    /// another 50% of the meter (drawn orange, then blue over it). A fully over-charged shot hits 50% harder per card.
    /// </summary>
    internal sealed class OverChargeCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.A; } }
        public override string Id { get { return "ultrapuck.fastercharge"; } } // the old Faster Charge id, so saved settings carry over
        public override string Name { get { return "Over Charge"; } }
        public override string Description { get { return CardText.Stat(0.5f, "Charge-up Time", false) + "\n" + CardText.Good("+50% Over Charge") +
            "\nHold past full for a harder shot."; } }
        public override Color Color { get { return new Color(0.95f, 0.55f, 0.20f); } }
        public override int MaxPerPlayer { get { return 4; } }
        public override void Apply(CardStats s) { s.ChargeSpeed *= 2f; s.OverCharge += 0.5f; }
    }

    internal sealed class SlowPokeCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.F; } }
        public override string Id { get { return "ultrapuck.slowpoke"; } }
        public override string Name { get { return "Slow Poke"; } }
        public override string Description { get { return CardText.Stat(0.9f, "Opponent Move Speed", false); } }
        public override Color Color { get { return new Color(0.55f, 0.40f, 0.80f); } }
        public override int MaxPerPlayer { get { return 3; } }
        public override void Apply(CardStats s) { s.OpponentSpeed *= 0.9f; }
    }

    internal sealed class HeadStartCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.B; } }
        public override string Id { get { return "ultrapuck.betterspawn"; } } // id kept so saved Toggle Cards settings still apply
        public override string Name { get { return "Head Start"; } }
        public override string Description { get { return CardText.Good("Your team starts face-offs with the puck") + "\nMost copies wins; a tie: it starts in the middle."; } }
        public override Color Color { get { return new Color(0.40f, 0.85f, 0.65f); } }
        public override int MaxPerPlayer { get { return 3; } }
        public override void Apply(CardStats s) { s.HeadStart++; }
    }

    internal sealed class PylonGoalieCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.F; } }
        public override string Id { get { return "ultrapuck.pylongoalie"; } }
        public override string Name { get { return "Pylon Goalie"; } }
        public override string Description { get { return CardText.Good("+1 Pylon Goalie") + "\nA dummy at your net that blocks shots."; } }
        public override Color Color { get { return new Color(0.70f, 0.72f, 0.76f); } }
        public override int MaxPerPlayer { get { return 2; } }
        public override void Apply(CardStats s) { s.Pylons++; }
    }

    internal sealed class PuckGunCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.S; } }
        public override string Id { get { return "ultrapuck.puckgun"; } }
        public override string Name { get { return "Puck Gun"; } }
        public override string Description { get { return "Brake to fire a shot: the first player it hits gets a " + CardText.Good("1 s Stun") + ". " + CardText.Neutral("2 s cooldown") + "."; } }
        public override Color Color { get { return new Color(0.80f, 0.20f, 0.25f); } }
        public override int MaxPerPlayer { get { return 1; } }
        public override void Apply(CardStats s) { s.PuckGun = true; }
    }

    internal sealed class InvisibilityCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.A; } }
        public override string Id { get { return "ultrapuck.invisibility"; } }
        public override string Name { get { return "Invisibility"; } }
        public override string Description { get { return "Brake to turn " + CardText.Good("invisible for 5 s") + " to opponents. " + CardText.Neutral("15 s cooldown") + "."; } }
        public override Color Color { get { return new Color(0.55f, 0.75f, 0.95f); } }
        public override int MaxPerPlayer { get { return 1; } }
        public override bool OnByDefault { get { return false; } } // being reworked
        public override void Apply(CardStats s) { s.Invisibility = true; }
    }

    internal sealed class FriendlyFireCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.A; } }
        public override string Id { get { return "ultrapuck.friendlyfire"; } }
        public override string Name { get { return "Friendly Fire"; } }
        public override string Description { get { return "A teammate dashing into you gives " + CardText.Good("+80% Move Speed for 2 s") + "."; } }
        public override Color Color { get { return new Color(1.00f, 0.45f, 0.35f); } }
        public override int MaxPerPlayer { get { return 1; } }
        public override bool CanOffer(int teamSize) { return teamSize >= 2; }
        public override void Apply(CardStats s) { s.TeammateBoost = true; }
    }

    internal sealed class ShockwaveCard : UltraCard
    {
        public override CardTier Tier { get { return CardTier.A; } }
        public override string Id { get { return "ultrapuck.shockwave"; } }
        public override string Name { get { return "Shockwave"; } }
        public override string Description { get { return "Dash into an opponent to " + CardText.Good("slow them and everyone near them by 30% for 2 s") + " and " + CardText.Good("push the others away") + ". " + CardText.Neutral("7 s cooldown") + "."; } }
        public override Color Color { get { return new Color(0.35f, 0.65f, 1.00f); } }
        public override int MaxPerPlayer { get { return 3; } }
        public override void Apply(CardStats s) { s.Shockwave++; }
    }
}
