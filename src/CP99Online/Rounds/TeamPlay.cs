using System.Collections.Generic;
using System.Linq;
using CP99Online.Net;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>
    /// Team Play (host / offline): watches the puck for passes. A pass is the puck leaving one player (shot or
    /// dropped) and the next player to pick it up being a teammate, with no opponent touching the loose puck on the
    /// way. A player handing it straight over (a bot swap) doesn't count, and neither does picking up your own shot.
    /// With Team Play the passer's cooldowns are restored; with Team Play II, everyone on the passer's team.
    /// </summary>
    internal static class TeamPlay
    {
        private static BallData _last;   // who carried the puck last frame
        private static BallData _passer; // whose pass is in the air (null = none, or an opponent touched it)

        internal static void Clear() { _last = null; _passer = null; }

        internal static void Tick()
        {
            var gm = Globals.gameManager;
            if (gm == null || gm.puck == null || gm.teams == null) return;
            if (gm.startCountdown > 0f || gm.roundOver || CardGame.PhaseActive || gm.replayManager != null && gm.replayManager.replaying)
            {
                Clear();
                return;
            }
            var puck = gm.puck.GetComponent<Puck>();
            var carrier = puck != null && puck.carryer != null ? puck.carryer.GetComponent<BallData>() : null;
            if (carrier != _last)
            {
                if (carrier == null) _passer = _last; // shot or dropped: a pass may be on its way
                else
                {
                    if (_passer != null && carrier != _passer && carrier.team == _passer.team) Passed(_passer, carrier);
                    _passer = null;
                }
                _last = carrier;
            }
            if (_passer == null || carrier != null) return;

            // an opponent touching the loose puck cancels the pass
            var pr = CardEffects.Radius(gm.puck.GetComponent<Collider2D>());
            var at = (Vector2)gm.puck.transform.position;
            foreach (var t in gm.teams)
            {
                if (t == _passer.team) continue;
                foreach (var b in t.members)
                {
                    if (b == null || !b.alive || b.fallen) continue;
                    if (Vector2.Distance(b.transform.position, at) <= CardEffects.Radius(b.GetComponent<Collider2D>()) + pr + 0.05f)
                    {
                        NetLog.Debug("Team Play: " + Name(b) + " touched " + Name(_passer) + "'s pass");
                        _passer = null;
                        return;
                    }
                }
            }
        }

        private static void Passed(BallData from, BallData to)
        {
            var a = CardEffects.AppliedOf(from);
            if (a == null || !a.Stats.PassRefresh) return;
            var who = a.Stats.PassRefreshTeam && from.team != null
                ? from.team.members.Where(m => m != null && m.alive && !m.fallen).ToList()
                : new List<BallData> { from };
            var slots = new List<int>();
            foreach (var b in who)
            {
                CardEffects.RestoreCooldowns(b);
                var slot = Match.Slot(b);
                if (slot >= 0) slots.Add(slot);
            }
            if (NetSession.HostInMatch) NetSession.Instance.HostSendRestored(slots);
            NetLog.Info("Team Play: " + Name(from) + " passed to " + Name(to) + ", cooldowns restored for " + string.Join(", ", who.Select(Name).ToArray()) + DashState(from));
        }

        private static string DashState(BallData b)
        {
            var d = CardEffects.DashOf(b);
            if (d != null) return " (dash charges " + d.Charges.ToString("F2") + "/" + d.Max + ")";
            var shove = b.GetComponent<BallShove>();
            return shove != null ? " (dash timer " + shove.shoveTimer.ToString("F2") + ")" : string.Empty;
        }

        private static string Name(BallData b) { return b != null && b.playerData != null && b.playerData.account != null ? b.playerData.account.name : "?"; }
    }
}
