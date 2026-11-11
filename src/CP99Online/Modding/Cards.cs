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
        /// <summary>Ball size multiplier (1.5 = 50% bigger).</summary>
        public float Size = 1f;
        /// <summary>How fast charge shots charge up (1.35 = 35% faster).</summary>
        public float ChargeSpeed = 1f;
        /// <summary>Multiplies the movement speed of every opponent (0.9 = opponents 10% slower).</summary>
        public float OpponentSpeed = 1f;
        /// <summary>How far toward the puck you start each face-off (0.15 = 15% closer).</summary>
        public float SpawnCloser;
        /// <summary>Puck pick-up distance multiplier (1.2 = grab it from 20% further).</summary>
        public float PickupRange = 1f;
        /// <summary>Pylons (a solid dummy player) placed in front of your team's net.</summary>
        public int Pylons;
        /// <summary>Dashing fires a shot that knocks out the first opponent it hits (10 s cooldown).</summary>
        public bool PuckGun;
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
            new FasterChargeCard(), new SlowPokeCard(), new BetterSpawnCard(),
            new PylonGoalieCard(), new PuckMagnetCard(), new PuckGunCard(),
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
        public override string Description { get { return "8% bigger and 200% heavier (very hard to knock around), but 10% slower."; } }
        public override Color Color { get { return new Color(0.62f, 0.45f, 0.30f); } }
        public override int MaxPerPlayer { get { return 2; } }
        public override void Apply(CardStats s) { s.Weight *= 3f; s.Size *= 1.08f; s.MoveSpeed *= 0.9f; }
    }

    internal sealed class FeatherweightCard : UltraCard
    {
        public override string Id { get { return "ultrapuck.featherweight"; } }
        public override string Name { get { return "Featherweight"; } }
        public override string Description { get { return "20% faster, but way lighter: you take 250% more knockback."; } }
        public override Color Color { get { return new Color(0.95f, 0.80f, 0.30f); } }
        public override int MaxPerPlayer { get { return 2; } }
        public override void Apply(CardStats s) { s.MoveSpeed *= 1.2f; s.Weight /= 3.5f; } // 3.5x knockback = +250%
    }

    internal sealed class FasterChargeCard : UltraCard
    {
        public override string Id { get { return "ultrapuck.fastercharge"; } }
        public override string Name { get { return "Faster Charge"; } }
        public override string Description { get { return "Charge shots charge up 35% faster."; } }
        public override Color Color { get { return new Color(0.95f, 0.55f, 0.20f); } }
        public override int MaxPerPlayer { get { return 3; } }
        public override void Apply(CardStats s) { s.ChargeSpeed *= 1.35f; }
    }

    internal sealed class SlowPokeCard : UltraCard
    {
        public override string Id { get { return "ultrapuck.slowpoke"; } }
        public override string Name { get { return "Slow Poke"; } }
        public override string Description { get { return "Every opponent moves 10% slower."; } }
        public override Color Color { get { return new Color(0.55f, 0.40f, 0.80f); } }
        public override int MaxPerPlayer { get { return 3; } }
        public override void Apply(CardStats s) { s.OpponentSpeed *= 0.9f; }
    }

    internal sealed class BetterSpawnCard : UltraCard
    {
        public override string Id { get { return "ultrapuck.betterspawn"; } }
        public override string Name { get { return "Better Spawn"; } }
        public override string Description { get { return "You start each face-off 15% closer to the puck."; } }
        public override Color Color { get { return new Color(0.40f, 0.85f, 0.65f); } }
        public override int MaxPerPlayer { get { return 3; } }
        public override void Apply(CardStats s) { s.SpawnCloser += 0.15f; }
    }

    internal sealed class PylonGoalieCard : UltraCard
    {
        public override string Id { get { return "ultrapuck.pylongoalie"; } }
        public override string Name { get { return "Pylon Goalie"; } }
        public override string Description { get { return "A dummy player stands in front of your net. It blocks shots and can be bumped, but can't pick up the puck."; } }
        public override Color Color { get { return new Color(0.70f, 0.72f, 0.76f); } }
        public override int MaxPerPlayer { get { return 2; } }
        public override void Apply(CardStats s) { s.Pylons++; }
    }

    internal sealed class PuckMagnetCard : UltraCard
    {
        public override string Id { get { return "ultrapuck.puckmagnet"; } }
        public override string Name { get { return "Puck Magnet"; } }
        public override string Description { get { return "Grab the puck from 20% further away."; } }
        public override Color Color { get { return new Color(0.90f, 0.35f, 0.65f); } }
        public override int MaxPerPlayer { get { return 2; } }
        public override void Apply(CardStats s) { s.PickupRange *= 1.2f; }
    }

    internal sealed class PuckGunCard : UltraCard
    {
        public override string Id { get { return "ultrapuck.puckgun"; } }
        public override string Name { get { return "Puck Gun"; } }
        public override string Description { get { return "Dashing fires a shot that knocks out the first opponent it hits. 10 second cooldown (bar under you)."; } }
        public override Color Color { get { return new Color(0.80f, 0.20f, 0.25f); } }
        public override int MaxPerPlayer { get { return 1; } }
        public override void Apply(CardStats s) { s.PuckGun = true; }
    }
}
