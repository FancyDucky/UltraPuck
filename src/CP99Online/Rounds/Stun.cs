using System.Collections.Generic;
using CP99Online.Net;
using HarmonyLib;
using UltraPuck;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>
    /// Puck Gun hits: the player is stunned for a second (no movement push, no dash, no shooting or charging,
    /// they drop the puck) but keeps sliding, and gets knocked back hard. The host (or the only PC) runs it;
    /// clients are told so they can draw the stun bar.
    /// </summary>
    internal static class Stun
    {
        internal const float Duration = 1f;
        /// <summary>Knockback impulse: 0.75x a dash (the game's dash is a 250 force for one 0.02 s physics step = 5).</summary>
        private const float KnockbackImpulse = 0.75f * CardEffects.GameDashForce * 0.02f;

        private sealed class State { public float Until; public Vector2 From; }
        private static readonly Dictionary<BallData, State> _stunned = new Dictionary<BallData, State>();
        private static readonly Dictionary<int, float> _clientUntil = new Dictionary<int, float>(); // by slot

        internal static void Clear() { _stunned.Clear(); _clientUntil.Clear(); }

        /// <summary>Host / offline: a Puck Gun shot from <paramref name="shooter"/> hit this player, travelling in <paramref name="dir"/>.</summary>
        internal static void Hit(BallData b, Vector2 dir, BallData shooter)
        {
            var push = KnockbackImpulse * CardEffects.FriendlyFireMul(shooter, b);
            var gm = Globals.gameManager;
            if (b.carryObject != null) b.GetComponent<BallHandling>().Drop(b.carryObject, Duration);
            var rb = b.GetComponent<Rigidbody2D>();
            if (rb != null) rb.AddForce(dir * push, ForceMode2D.Impulse);
            _stunned[b] = new State { Until = Time.time + Duration, From = b.transform.position };
            var sm = gm != null ? gm.soundManager : null;
            if (sm != null && sm.thrustHitSound != null) sm.CreateSound(sm.thrustHitSound, 2f, 0.6f, 0.8f);
            var slot = Match.Slot(b);
            if (NetSession.HostInMatch && slot >= 0) NetSession.Instance.HostSendStun(slot, Duration);
            NetLog.Info("Rounds: Puck Gun stunned " + Name(b) + " (knockback impulse " + push.ToString("F2") + ")");
        }

        /// <summary>Client: the host says this slot was stunned.</summary>
        internal static void ClientStunned(int slot, float seconds) { _clientUntil[slot] = Time.time + seconds; }

        /// <summary>Host / offline: is this player stunned right now? (Also ends stuns that ran out.)</summary>
        internal static bool IsStunned(BallData b)
        {
            State s;
            if (b == null || !CardGame.IsAuthority || !_stunned.TryGetValue(b, out s)) return false;
            if (Time.time < s.Until) return true;
            NetLog.Debug("Stun on " + Name(b) + " ended; slid " + Vector2.Distance(s.From, b.transform.position).ToString("F2") + " units");
            _stunned.Remove(b);
            return false;
        }

        /// <summary>Seconds of stun left (every PC).</summary>
        internal static float Remaining(BallData b)
        {
            if (b == null) return 0f;
            if (NetSession.ClientInMatch)
            {
                float t;
                return _clientUntil.TryGetValue(Match.Slot(b), out t) ? Mathf.Max(0f, t - Time.time) : 0f;
            }
            State s;
            return _stunned.TryGetValue(b, out s) ? Mathf.Max(0f, s.Until - Time.time) : 0f;
        }

        private static string Name(BallData b) { return b.playerData != null && b.playerData.account != null ? b.playerData.account.name : "?"; }
    }

    // While stunned: no movement push (the ball keeps sliding), no shooting/charging/aiming, no dash.

    [HarmonyPatch(typeof(BallMovement), "FixedUpdate")]
    internal static class StunMovementPatch
    {
        private static bool Prefix(BallMovement __instance) { return !Stun.IsStunned(__instance.GetComponent<BallData>()); }
    }

    [HarmonyPatch(typeof(BallHandling), "Update")]
    internal static class StunHandlingPatch
    {
        private static bool Prefix(BallHandling __instance) { return !Stun.IsStunned(__instance.GetComponent<BallData>()); }
    }

    [HarmonyPatch(typeof(BallShove), nameof(BallShove.Shove))]
    internal static class StunShovePatch
    {
        private static bool Prefix(BallShove __instance) { return !Stun.IsStunned(__instance.GetComponent<BallData>()); }
    }

    /// <summary>A yellow bar under a stunned player that fills back up over the stun (every PC).</summary>
    internal sealed class StunBarView : MonoBehaviour
    {
        private static readonly Color Filling = new Color(1f, 0.85f, 0.25f, 0.85f);
        private static readonly Color Ready = new Color(1f, 0.92f, 0.45f, 1f);
        private readonly PlayerBars _bars = new PlayerBars("StunBar");

        private void OnLevelWasLoaded(int level) { _bars.Clear(); }

        private void LateUpdate()
        {
            var gm = Globals.gameManager;
            if (gm == null || gm.teams == null) { if (_bars.Count > 0) _bars.Clear(); return; }
            var seen = new HashSet<BallData>();
            foreach (var team in gm.teams)
                foreach (var b in team.members)
                {
                    if (b == null) continue;
                    var left = Stun.Remaining(b);
                    if (left <= 0f && !seen.Contains(b) && !_active.Contains(b)) continue;
                    seen.Add(b);
                    if (left > 0f) _active.Add(b);
                    var visible = b.alive && !b.fallen && !gm.replayManager.replaying;
                    _bars.For(b).Set(b, 1, 1f - left / Stun.Duration, visible, false, Filling, Ready, -0.5f, 0.78f, 0.09f);
                }
            _bars.Prune(seen);
            _active.IntersectWith(seen);
        }

        // Players whose bar is still fading out after the stun ended.
        private readonly HashSet<BallData> _active = new HashSet<BallData>();
    }
}
