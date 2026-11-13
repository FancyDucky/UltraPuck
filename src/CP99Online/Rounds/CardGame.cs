using System.Collections.Generic;
using System.Linq;
using CP99Online.Net;
using UltraPuck;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>One player in the card game. Online, seat index = match slot.</summary>
    internal sealed class Seat
    {
        public string Name = "Player";
        public int Team;
        public bool Human;
        /// <summary>Online client id that controls this player (0 = the host's PC, or offline).</summary>
        public byte Owner;
        public BallData Ball;
        public readonly List<string> Cards = new List<string>();
        /// <summary>Card ids offered right now (same list for a whole team in Team Vote).</summary>
        public string[] Offer = new string[0];
        /// <summary>Index into Offer this player picked / voted for, or -1.</summary>
        public int Choice = -1;
        /// <summary>The card added in the last pick (for the reveal), or null.</summary>
        public string Last;
    }

    /// <summary>
    /// Rounds Mode state machine. The host (or the only PC, offline) runs it; online clients receive the
    /// state and only send their picks.
    ///   goal -> instant replay -> [game calls ResetField] -> held: Picking -> Revealing -> ResetField runs.
    /// </summary>
    internal static class CardGame
    {
        internal const float RevealSeconds = 2.5f;
        internal const float InputDelay = 0.75f;

        internal static readonly List<Seat> Seats = new List<Seat>();
        internal static bool PhaseActive, Revealing, TeamMode;
        /// <summary>Seconds left to pick (-1 = no limit). On clients, as of <see cref="TimeStamp"/>.</summary>
        internal static float TimeLeft = -1f;
        internal static float TimeStamp;
        /// <summary>Bumped on every change; the host re-sends the state when it changes.</summary>
        internal static int Version;
        internal static float PhaseStartedAt;

        private static float _deadline, _revealUntil;
        private static bool _bypass;

        internal static bool IsAuthority { get { return !NetSession.ClientInMatch; } }
        private static float Now { get { return Time.realtimeSinceStartup; } }

        internal static float SecondsLeft
        {
            get
            {
                if (!PhaseActive || Revealing || TimeLeft < 0f) return -1f;
                return Mathf.Max(0f, TimeLeft - (Now - TimeStamp));
            }
        }

        /// <summary>The team picking cards this round (the one that was scored on).</summary>
        internal static int ScoredOnTeam;
        private static string _remoteCardsSig;

        internal static void OnNewMatch()
        {
            _remoteCardsSig = null;
            Seats.Clear();
            PhaseActive = Revealing = false;
            TimeLeft = -1f;
            CardEffects.Clear();
            Version++;
        }

        // ------------------------------------------------------------------ holding the field reset

        /// <summary>GameManager.ResetField prefix: true = hold it and start picking cards instead.</summary>
        internal static bool HoldReset(GameManager gm, bool suddenDeath)
        {
            if (_bypass || !IsAuthority || !RoundsMode.Active || PhaseActive) return false;
            if (suddenDeath || gm.matchOver || gm.targetPractice || ChallengeMatch_Manager.challengeMatch) return false;
            if (gm.teamThatJustScored == null) return false; // match start, not a goal
            return BeginPhase(gm);
        }

        /// <summary>GameManager.ResetField postfix (every PC).</summary>
        internal static void AfterReset()
        {
            if (!IsAuthority) return;
            CardEffects.AfterFaceOff();
            CardObjects.ResetPylons();
            foreach (var s in Seats) CardEffects.ApplyBallHooks(s);
        }

        private static bool BeginPhase(GameManager gm)
        {
            BuildSeats(gm);
            if (Seats.Count == 0) return false;
            TeamMode = RoundsMode.ForceTeamVote ?? RoundsMode.Instance.TeamVote.Value;
            var n = Mathf.Clamp(RoundsMode.Instance.Choices.Value, 1, 8);

            foreach (var s in Seats) { s.Choice = -1; s.Offer = new string[0]; s.Last = null; }
            // Cards help the side that got scored on catch up: only they pick.
            var scorer = gm.teams.IndexOf(gm.teamThatJustScored);
            ScoredOnTeam = Seats.Select(s => s.Team).FirstOrDefault(t => t != scorer);
            if (TeamMode)
            {
                foreach (var team in Seats.Where(s => s.Team != scorer).GroupBy(s => s.Team))
                {
                    var offer = Draw(team.First(), n);
                    foreach (var s in team) s.Offer = offer;
                    // A team with no people in it: one bot picks.
                    if (!team.Any(s => s.Human) && offer.Length > 0) team.First().Choice = Random.Range(0, offer.Length);
                }
            }
            else
            {
                foreach (var s in Seats.Where(s => s.Team != scorer))
                {
                    s.Offer = Draw(s, n);
                    if (!s.Human && s.Offer.Length > 0) s.Choice = Random.Range(0, s.Offer.Length);
                }
            }
            if (Seats.All(s => s.Offer.Length == 0)) return false; // nothing left to offer

            PhaseActive = true;
            Revealing = false;
            PhaseStartedAt = Now;
            var secs = RoundsMode.PickSeconds;
            TimeLeft = secs;
            TimeStamp = Now;
            _deadline = secs > 0f ? Now + secs : float.MaxValue;
            Version++;
            NetLog.Info("Rounds: card pick started (" + (TeamMode ? "team vote" : "each player") + ", " + Seats.Count + " players)");
            return true;
        }

        /// <summary>Card debug menu: make sure there are seats before the first card pick.</summary>
        internal static void EnsureSeats(GameManager gm) { if (IsAuthority) BuildSeats(gm); }

        private static void BuildSeats(GameManager gm)
        {
            var balls = new List<BallData>();
            var owners = new List<byte>();
            if (NetSession.HostInMatch)
            {
                for (var i = 0; i < Match.Balls.Length; i++)
                {
                    balls.Add(Match.Balls[i]);
                    owners.Add(Match.Owners != null && i < Match.Owners.Count ? Match.Owners[i] : (byte)0);
                }
            }
            else
            {
                foreach (var t in gm.teams) foreach (var m in t.members) { balls.Add(m); owners.Add(0); }
            }

            // Keep cards from earlier rounds: same balls in the same order for the whole match.
            if (Seats.Count == balls.Count && Seats.Select(s => s.Ball).SequenceEqual(balls)) return;
            Seats.Clear();
            for (var i = 0; i < balls.Count; i++)
            {
                var b = balls[i];
                if (b == null) continue;
                var pd = b.playerData;
                Seats.Add(new Seat
                {
                    Ball = b,
                    Owner = owners[i],
                    Team = Mathf.Max(0, gm.teams.IndexOf(b.team)),
                    Human = pd != null && !pd.AI,
                    Name = pd != null && pd.account != null && !string.IsNullOrEmpty(pd.account.name) ? pd.account.name : "Bot",
                });
            }
        }

        /// <summary>Up to n different random cards the seat may still take (weighted by Rarity).</summary>
        private static string[] Draw(Seat seat, int n)
        {
            var forced = System.Environment.GetEnvironmentVariable("CP99_ROUNDS_CARD"); // tests: offer exactly these cards
            if (!string.IsNullOrEmpty(forced))
            {
                var list = forced.Split(',').Where(id => UltraPuck.Cards.Get(id) != null).ToArray();
                if (list.Length > 0) return list;
            }
            var pool = UltraPuck.Cards.All
                .Where(c => c.Rarity > 0f && RoundsMode.IsCardEnabled(c.Id) && (c.MaxPerPlayer <= 0 || seat.Cards.Count(id => id == c.Id) < c.MaxPerPlayer))
                .ToList();
            var picked = new List<string>();
            while (picked.Count < n && pool.Count > 0)
            {
                var total = pool.Sum(c => c.Rarity);
                var r = Random.value * total;
                var i = 0;
                for (; i < pool.Count - 1; i++) { r -= pool[i].Rarity; if (r <= 0f) break; }
                picked.Add(pool[i].Id);
                pool.RemoveAt(i);
            }
            return picked.ToArray();
        }

        // ------------------------------------------------------------------ picking

        /// <summary>Is this seat picked from this PC?</summary>
        internal static bool IsLocal(Seat s)
        {
            if (!s.Human) return false;
            if (NetSession.ClientInMatch) return s.Owner == NetSession.Instance.MyId && s.Owner != 0;
            return s.Owner == 0;
        }

        /// <summary>Still has to pick (connected person with something offered).</summary>
        private static bool Waiting(Seat s)
        {
            if (!s.Human || s.Offer.Length == 0 || s.Choice >= 0) return false;
            if (s.Owner == 0 || !NetSession.IsHost) return true;
            return NetSession.Instance.Clients.Any(c => c.Id == s.Owner); // left the game: don't wait for them
        }

        internal static int PickedCount { get { return Seats.Count(s => s.Human && s.Offer.Length > 0 && s.Choice >= 0); } }
        internal static int PickerCount { get { return Seats.Count(s => s.Human && s.Offer.Length > 0); } }

        /// <summary>A pick from this PC (offline/host) or from a client (online, <paramref name="from"/> = its id).</summary>
        internal static void Pick(int seat, int index, byte from)
        {
            if (!PhaseActive || Revealing || seat < 0 || seat >= Seats.Count) return;
            var s = Seats[seat];
            if (s.Owner != from || index < 0 || index >= s.Offer.Length || s.Choice == index) return;
            s.Choice = index;
            Version++;
        }

        /// <summary>Called every frame on the host / offline.</summary>
        internal static void Update()
        {
            if (!PhaseActive || !IsAuthority) return;
            var gm = Globals.gameManager;
            if (gm == null) { PhaseActive = false; return; }

            // Nobody moves while cards are being picked.
            foreach (var s in Seats)
                if (s.Ball != null) { s.Ball.canMove = false; s.Ball.canShove = false; }

            if (!RoundsMode.Active && !Revealing) _deadline = 0f; // mod turned off mid-pick: finish now
            if (!Revealing)
            {
                if (!Seats.Any(Waiting) || Now >= _deadline) Resolve();
                return;
            }
            if (Now >= _revealUntil) Finish(gm);
        }

        private static void Resolve()
        {
            if (TeamMode)
            {
                foreach (var team in Seats.GroupBy(s => s.Team))
                {
                    var offer = team.First().Offer;
                    if (offer.Length == 0) continue;
                    var votes = new int[offer.Length];
                    foreach (var s in team) if (s.Choice >= 0 && s.Choice < votes.Length) votes[s.Choice]++;
                    var best = votes.Max();
                    var top = Enumerable.Range(0, votes.Length).Where(i => votes[i] == best).ToList();
                    var win = top[Random.Range(0, top.Count)]; // tie (or no votes): random
                    foreach (var s in team) { s.Cards.Add(offer[win]); s.Last = offer[win]; }
                    NetLog.Info("Rounds: team " + (team.Key + 1) + " gets " + offer[win] + " (votes " + string.Join(",", votes.Select(v => v.ToString()).ToArray()) + ")");
                }
            }
            else
            {
                foreach (var s in Seats)
                {
                    if (s.Offer.Length == 0) continue;
                    var i = s.Choice >= 0 ? s.Choice : Random.Range(0, s.Offer.Length);
                    s.Cards.Add(s.Offer[i]);
                    s.Last = s.Offer[i];
                    NetLog.Info("Rounds: " + s.Name + " gets " + s.Offer[i] + (s.Choice >= 0 ? string.Empty : " (random)"));
                }
            }
            CardEffects.RecomputeAll(true);
            foreach (var s in Seats)
            {
                if (s.Ball != null) NetLog.Info("Rounds: " + s.Name + " cards=[" + string.Join(", ", s.Cards.ToArray()) + "] " + CardEffects.Describe(s.Ball));
            }
            Revealing = true;
            _revealUntil = Now + RevealSeconds;
            Version++;
        }

        private static void Finish(GameManager gm)
        {
            PhaseActive = false;
            Revealing = false;
            foreach (var s in Seats) { s.Offer = new string[0]; s.Choice = -1; }
            Version++;
            _bypass = true;
            try { gm.ResetField(); }
            finally { _bypass = false; }
        }

        // ------------------------------------------------------------------ network (clients)

        /// <summary>Client: replace the state with the host's.</summary>
        internal static void ApplyRemote(bool active, bool revealing, bool team, float timeLeft, List<Seat> seats)
        {
            // Keep a pick this PC just made until the host confirms it.
            var mine = new Dictionary<int, int>();
            for (var i = 0; i < Seats.Count && i < seats.Count; i++)
                if (IsLocal(Seats[i]) && Seats[i].Choice >= 0 && seats[i].Choice < 0 && active && !revealing) mine[i] = Seats[i].Choice;
            if (active && !PhaseActive) PhaseStartedAt = Now;
            Seats.Clear();
            Seats.AddRange(seats);
            foreach (var kv in mine) Seats[kv.Key].Choice = kv.Value;
            for (var i = 0; i < Seats.Count; i++) Seats[i].Ball = Match.Ball(i);
            TeamMode = team;
            // Ball sizes and pylons are drawn on every PC: re-apply when anyone's cards change.
            var sig = string.Join("|", Seats.Select(s => string.Join(",", s.Cards.ToArray())).ToArray());
            if (sig != _remoteCardsSig) { _remoteCardsSig = sig; CardEffects.RecomputeAll(false); }
            PhaseActive = active;
            Revealing = revealing;
            TeamMode = team;
            TimeLeft = timeLeft;
            TimeStamp = Now;
            Version++;
        }

        /// <summary>Client: optimistic local pick (sent to the host by the caller).</summary>
        internal static void PickLocal(int seat, int index)
        {
            if (seat < 0 || seat >= Seats.Count) return;
            Seats[seat].Choice = index;
            Version++;
        }
    }
}
