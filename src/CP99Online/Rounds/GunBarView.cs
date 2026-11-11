using System.Collections.Generic;
using CP99Online.Net;
using UltraPuck;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>
    /// Puck Gun cooldown: a red bar under each player who has the card. It fills over the 10 s cooldown, pulses
    /// when the gun is ready and then fades out. Every PC draws it (clients track cooldowns from the host's shots).
    /// </summary>
    internal sealed class GunBarView : MonoBehaviour
    {
        private static readonly Color Filling = new Color(0.85f, 0.3f, 0.32f, 0.75f);
        private static readonly Color Ready = new Color(1f, 0.35f, 0.35f, 1f);

        private readonly PlayerBars _bars = new PlayerBars("PuckGunBar");

        private void OnLevelWasLoaded(int level) { _bars.Clear(); }

        private void LateUpdate()
        {
            var gm = Globals.gameManager;
            if (gm == null || gm.teams == null || CardGame.Seats.Count == 0) { if (_bars.Count > 0) _bars.Clear(); return; }
            var seen = new HashSet<BallData>();
            foreach (var team in gm.teams)
                foreach (var b in team.members)
                {
                    if (b == null || !HasGun(b)) continue;
                    seen.Add(b);
                    var ready = 1f - CardObjects.GunCooldownLeft(b) / CardObjects.GunCooldown;
                    var visible = b.alive && !b.fallen && !gm.replayManager.replaying;
                    _bars.For(b).Set(b, 1, ready, visible, false, Filling, Ready, -0.5f, 0.78f, 0.09f);
                }
            _bars.Prune(seen);
        }

        private static bool HasGun(BallData b)
        {
            if (!NetSession.ClientInMatch)
            {
                var a = CardEffects.AppliedOf(b);
                return a != null && a.Stats.PuckGun;
            }
            var slot = Match.Slot(b);
            return slot >= 0 && slot < CardGame.Seats.Count && CardEffects.StatsOf(CardGame.Seats[slot]).PuckGun;
        }
    }
}
