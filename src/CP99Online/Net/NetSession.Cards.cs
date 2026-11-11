using System.Collections.Generic;
using System.Linq;
using CP99Online.Rounds;

namespace CP99Online.Net
{
    // Rounds Mode online: the host runs the card game and sends its whole state whenever it changes
    // (and every second while picking, for the timer). Clients only send their own picks.
    //
    // CardState: u8 flags (1 active, 2 revealing, 4 team vote), f32 seconds left (-1 = no limit),
    //            u8 n ids, n x str card id   (a table; cards below are indexes into it)
    //            u8 seats, per seat: str name, u8 team, u8 human, u8 owner, u8 choice+1,
    //                                u8 offers + offer indexes, u8 cards + card indexes, u8 last+1
    // CardPick:  u8 seat, u8 offer index
    internal sealed partial class NetSession
    {
        internal byte MyId { get { return _myId; } }

        private int _cardsSentVersion = -1;
        private float _cardsSentAt;

        private void HostCardsUpdate()
        {
            if (!HostInMatch || _clients.Count == 0) return;
            var changed = CardGame.Version != _cardsSentVersion;
            if (!changed && !(CardGame.PhaseActive && Now - _cardsSentAt > 1f)) return;
            if (!changed && !CardGame.PhaseActive) return;
            _cardsSentVersion = CardGame.Version;
            _cardsSentAt = Now;
            WriteCards();
            BroadcastReliable();
        }

        private void WriteCards()
        {
            var seats = CardGame.Seats;
            var ids = seats.SelectMany(s => s.Offer.Concat(s.Cards)).Distinct().Take(255).ToList();
            var index = new Dictionary<string, int>();
            for (var i = 0; i < ids.Count; i++) index[ids[i]] = i;

            Begin(Msg.CardState);
            _msg.U8((CardGame.PhaseActive ? 1 : 0) | (CardGame.Revealing ? 2 : 0) | (CardGame.TeamMode ? 4 : 0));
            _msg.F32(CardGame.SecondsLeft);
            _msg.U8(ids.Count);
            foreach (var id in ids) _msg.Str(id);
            _msg.U8(seats.Count);
            foreach (var s in seats)
            {
                _msg.Str(s.Name);
                _msg.U8(s.Team);
                _msg.Bool(s.Human);
                _msg.U8(s.Owner);
                _msg.U8(s.Choice + 1);
                WriteIds(s.Offer, index);
                WriteIds(s.Cards.Take(255).ToArray(), index);
                int last;
                _msg.U8(s.Last != null && index.TryGetValue(s.Last, out last) ? last + 1 : 0);
            }
        }

        private void WriteIds(IList<string> list, Dictionary<string, int> index)
        {
            var known = list.Where(index.ContainsKey).ToList();
            _msg.U8(known.Count);
            foreach (var id in known) _msg.U8(index[id]);
        }

        private void ClientHandleCards(Reader r)
        {
            var flags = r.U8();
            var timeLeft = r.F32();
            var ids = new string[r.U8()];
            for (var i = 0; i < ids.Length; i++) ids[i] = r.Str();
            var seats = new List<Seat>();
            int n = r.U8();
            for (var i = 0; i < n; i++)
            {
                var s = new Seat { Name = r.Str(), Team = r.U8(), Human = r.Bool(), Owner = r.U8(), Choice = r.U8() - 1 };
                s.Offer = ReadIds(r, ids);
                s.Cards.AddRange(ReadIds(r, ids));
                int last = r.U8();
                s.Last = last > 0 && last <= ids.Length ? ids[last - 1] : null;
                seats.Add(s);
            }
            CardGame.ApplyRemote((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, timeLeft, seats);
        }

        private static string[] ReadIds(Reader r, string[] ids)
        {
            var list = new string[r.U8()];
            for (var i = 0; i < list.Length; i++)
            {
                int k = r.U8();
                list[i] = k < ids.Length ? ids[k] : "?";
            }
            return list;
        }

        /// <summary>Client: send this PC's pick for a seat it controls.</summary>
        internal void ClientSendCardPick(int seat, int index)
        {
            if (!IsClient || _conns.Count == 0) return;
            Begin(Msg.CardPick);
            _msg.U8(seat);
            _msg.U8(index);
            _conns.Values.First().SendReliable(_msg, Now);
        }

        private void HostHandleCardPick(ClientInfo ci, Reader r)
        {
            int seat = r.U8();
            int index = r.U8();
            CardGame.Pick(seat, index, ci.Id);
        }
    }
}
