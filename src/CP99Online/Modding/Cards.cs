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
        /// <summary>Shot strength multiplier.</summary>
        public float ShotPower = 1f;
        /// <summary>Ball weight multiplier (heavier = harder to push around).</summary>
        public float Weight = 1f;
        /// <summary>Dash (boost) recharge time multiplier (0.7 = recharges 30% faster).</summary>
        public float DashCooldown = 1f;
        /// <summary>Extra dash charges on top of the normal one.</summary>
        public int ExtraDashes;
    }

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

    // ------------------------------------------------------------------ built-in cards

    internal sealed class DoubleDashCard : UltraCard
    {
        public override string Id { get { return "ultrapuck.doubledash"; } }
        public override string Name { get { return "Double Dash"; } }
        public override string Description { get { return "One more dash charge. Dash twice before recharging."; } }
        public override Color Color { get { return new Color(0.30f, 0.55f, 0.95f); } }
        public override void Apply(CardStats s) { s.ExtraDashes++; }
    }

    internal sealed class SpeedUpCard : UltraCard
    {
        public override string Id { get { return "ultrapuck.speedup"; } }
        public override string Name { get { return "Speed Up"; } }
        public override string Description { get { return "15% faster movement."; } }
        public override Color Color { get { return new Color(0.30f, 0.80f, 0.40f); } }
        public override void Apply(CardStats s) { s.MoveSpeed *= 1.15f; }
    }

    internal sealed class PowerShotCard : UltraCard
    {
        public override string Id { get { return "ultrapuck.powershot"; } }
        public override string Name { get { return "Power Shot"; } }
        public override string Description { get { return "Your shots are 25% stronger."; } }
        public override Color Color { get { return new Color(0.92f, 0.33f, 0.30f); } }
        public override void Apply(CardStats s) { s.ShotPower *= 1.25f; }
    }

    internal sealed class QuickRechargeCard : UltraCard
    {
        public override string Id { get { return "ultrapuck.quickrecharge"; } }
        public override string Name { get { return "Quick Recharge"; } }
        public override string Description { get { return "Your dash recharges 30% faster."; } }
        public override Color Color { get { return new Color(0.25f, 0.80f, 0.85f); } }
        public override int MaxPerPlayer { get { return 3; } }
        public override void Apply(CardStats s) { s.DashCooldown *= 0.7f; }
    }

    internal sealed class HeavyweightCard : UltraCard
    {
        public override string Id { get { return "ultrapuck.heavyweight"; } }
        public override string Name { get { return "Heavyweight"; } }
        public override string Description { get { return "40% heavier: much harder to push around, but 5% slower."; } }
        public override Color Color { get { return new Color(0.62f, 0.45f, 0.30f); } }
        public override void Apply(CardStats s) { s.Weight *= 1.4f; s.MoveSpeed *= 0.95f; }
    }

    internal sealed class FeatherweightCard : UltraCard
    {
        public override string Id { get { return "ultrapuck.featherweight"; } }
        public override string Name { get { return "Featherweight"; } }
        public override string Description { get { return "25% faster, but 30% lighter and easier to knock around."; } }
        public override Color Color { get { return new Color(0.95f, 0.80f, 0.30f); } }
        public override void Apply(CardStats s) { s.MoveSpeed *= 1.25f; s.Weight *= 0.7f; }
    }
}
