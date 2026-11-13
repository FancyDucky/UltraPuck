using System.Collections.Generic;
using System.Linq;
using CP99Online.Net;
using HarmonyLib;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>
    /// Invisibility card: braking (holding the brake button without the puck) turns the player invisible to the
    /// other team for 5 s. Teammates still see them. Opponents see a 0.8 s fade first.
    ///
    /// The host (or the only PC) notices the brake and tells clients (Msg.CardShot kind 3); every PC then decides
    /// for itself who to hide, from the team of the player(s) at that PC. On a shared screen with players from
    /// both teams the invisible player shows as a faint flicker (they can't be hidden from one side only).
    /// Hiding = turning off the ball's renderers (mesh, symbol, name tag, charge meter...) and its bars.
    /// </summary>
    internal sealed class Invisibility : MonoBehaviour
    {
        internal const float Duration = 5f;
        internal const float Cooldown = 12f; // from when it starts, so 7 s visible between uses
        internal const float FadeTime = 0.8f;

        private sealed class State
        {
            public float Start, Until;
            public readonly HashSet<Renderer> Off = new HashSet<Renderer>();
        }

        private static readonly Dictionary<BallData, State> _active = new Dictionary<BallData, State>();
        private static readonly Dictionary<BallData, float> _ready = new Dictionary<BallData, float>(); // authority: next allowed time

        internal static void Clear()
        {
            foreach (var kv in _active) Show(kv.Value);
            _active.Clear();
            _ready.Clear();
        }

        private void OnLevelWasLoaded(int level) { _active.Clear(); _ready.Clear(); InvisibilityBrakePatch.Reset(); }

        /// <summary>Host / offline: this ball just started braking.</summary>
        internal static void OnBrake(BallData b)
        {
            if (b == null || !CardGame.IsAuthority) return;
            var gm = Globals.gameManager;
            if (CardGame.PhaseActive || gm == null || gm.startCountdown > 0f || Replaying()) return; // not while frozen
            var a = CardEffects.AppliedOf(b);
            if (a == null || !a.Stats.Invisibility) return;
            float next;
            if (_ready.TryGetValue(b, out next) && Time.time < next) return;
            _ready[b] = Time.time + Cooldown;
            Begin(b, Duration);
            var slot = Match.Slot(b);
            if (NetSession.HostInMatch && slot >= 0) NetSession.Instance.HostSendInvisible(slot, Duration);
            NetLog.Info("Rounds: " + (b.playerData != null && b.playerData.account != null ? b.playerData.account.name : "?") + " turned invisible");
        }

        /// <summary>Every PC: start (or restart) invisibility on this ball.</summary>
        internal static void Begin(BallData b, float seconds)
        {
            if (b == null) return;
            State s;
            if (_active.TryGetValue(b, out s)) Show(s);
            _active[b] = new State { Start = Time.time, Until = Time.time + seconds };
        }

        /// <summary>Seconds of invisibility left (every PC).</summary>
        internal static float Remaining(BallData b)
        {
            State s;
            return b != null && _active.TryGetValue(b, out s) ? Mathf.Max(0f, s.Until - Time.time) : 0f;
        }

        // ------------------------------------------------------------------ who sees what

        private enum View { Seen, Hidden, Ghost }

        /// <summary>Teams of the players at this PC (empty = only watching).</summary>
        private static HashSet<TeamData> MyTeams()
        {
            var teams = new HashSet<TeamData>();
            if (NetSession.ClientInMatch)
            {
                var me = Match.Ball(Match.LocalSlot);
                if (me != null) teams.Add(me.team);
                return teams;
            }
            var gm = Globals.gameManager;
            foreach (var t in gm.teams)
                foreach (var m in t.members)
                    if (m != null && m.playerData != null && !m.playerData.AI && !(NetSession.HostInMatch && Match.IsRemote(Match.Slot(m))))
                        teams.Add(m.team);
            return teams;
        }

        private static View ViewOf(BallData b, HashSet<TeamData> mine)
        {
            if (mine.Count == 1 && mine.Contains(b.team)) return View.Seen;
            if (mine.Count == 1) return View.Hidden;
            return View.Ghost; // shared screen with both teams, or a spectator
        }

        /// <summary>Should this PC hide this player's extras (bars) right now?</summary>
        internal static bool HiddenFromMe(BallData b)
        {
            State s;
            if (b == null || !_active.TryGetValue(b, out s) || Replaying()) return false;
            return ViewOf(b, MyTeams()) != View.Seen && Time.time - s.Start >= FadeTime;
        }

        private static bool Replaying()
        {
            var gm = Globals.gameManager;
            return gm != null && gm.replayManager != null && gm.replayManager.replaying;
        }

        // ------------------------------------------------------------------ drawing

        private void LateUpdate()
        {
            if (_active.Count == 0) return;
            var gm = Globals.gameManager;
            if (gm == null) return;
            var mine = MyTeams();
            var replay = Replaying();
            foreach (var kv in _active.ToList())
            {
                var b = kv.Key; var s = kv.Value;
                if (b == null) { _active.Remove(b); continue; }
                if (Time.time >= s.Until || !b.alive) { Show(s); _active.Remove(b); continue; }
                var t = Time.time - s.Start;
                bool visible;
                switch (replay ? View.Seen : ViewOf(b, mine))
                {
                    case View.Seen: visible = true; break;
                    case View.Ghost: visible = Mathf.Repeat(t, 0.6f) < 0.08f; break;
                    default:
                        // fade: flicker with longer and longer gaps until gone
                        visible = t < FadeTime && Mathf.Repeat(t * 14f, 1f) > t / FadeTime;
                        break;
                }
                if (visible) Show(s); else Hide(b, s);
            }
        }

        private static void Hide(BallData b, State s)
        {
            foreach (var r in b.GetComponentsInChildren<Renderer>())
                if (r.enabled) { r.enabled = false; s.Off.Add(r); }
            var tag = b.GetComponent<BallNameTag>();
            if (tag != null && tag.nameTag != null)
            {
                var r = tag.nameTag.GetComponent<Renderer>();
                if (r != null && r.enabled) { r.enabled = false; s.Off.Add(r); }
            }
        }

        private static void Show(State s)
        {
            foreach (var r in s.Off) if (r != null) r.enabled = true;
            s.Off.Clear();
        }
    }

    /// <summary>Invisibility: notice when a player starts braking (BallSprint sets "sprinting" while the brake is held).</summary>
    [HarmonyPatch(typeof(BallSprint), "Update")]
    internal static class InvisibilityBrakePatch
    {
        private static readonly Dictionary<BallSprint, bool> _was = new Dictionary<BallSprint, bool>();

        internal static void Reset() { _was.Clear(); }

        private static void Postfix(BallSprint __instance, bool ___sprinting)
        {
            bool was;
            _was.TryGetValue(__instance, out was);
            _was[__instance] = ___sprinting;
            if (___sprinting && !was) Invisibility.OnBrake(__instance.GetComponent<BallData>());
        }
    }
}
