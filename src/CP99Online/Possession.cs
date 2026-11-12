using System.Collections.Generic;
using System.Linq;
using CP99Online.Net;
using HarmonyLib;
using UnityEngine;

namespace CP99Online
{
    /// <summary>
    /// Bot switching, like FIFA/NHL: press X (Xbox) / Square (PlayStation) / E (keyboard) to take over the bot
    /// teammate nearest the puck. The ball you leave is played by the AI until you come back to it.
    ///
    /// Every human has a "home" ball (the one spawned for them) and a "body" (the ball they control right now).
    /// Cards, stats and the ball's symbol stay with each ball; the controlling human's name tag moves with them.
    /// The host (or the only PC) decides switches; online clients ask with a counter in their input message and
    /// get the result as Msg.Possess, which only changes what they draw (name tags, robot markers, mouse aim).
    ///
    /// Input: the body gets the home ball's input names. Online, remote players' names are "NET|homeSlot|..",
    /// which NetInput resolves to that player's controls on any ball, so nothing else in the input path moves.
    /// </summary>
    internal sealed class Possession : MonoBehaviour
    {
        private sealed class Names
        {
            public string X1, Y1, X2, Y2, ChargeAxis, Shoot, ShootAlt, Charge, ChargeAlt, Sprint, SprintAlt, CallPass;

            public static Names Of(BallInput i)
            {
                return new Names
                {
                    X1 = i.axisName_x1, Y1 = i.axisName_y1, X2 = i.axisName_x2, Y2 = i.axisName_y2, ChargeAxis = i.axisName_charge,
                    Shoot = i.buttonName_shoot, ShootAlt = i.buttonName_shootAlt, Charge = i.buttonName_charge, ChargeAlt = i.buttonName_chargeAlt,
                    Sprint = i.buttonName_sprint, SprintAlt = i.buttonName_sprintAlt, CallPass = i.buttonName_callPass,
                };
            }

            public void To(BallInput i)
            {
                i.axisName_x1 = X1; i.axisName_y1 = Y1; i.axisName_x2 = X2; i.axisName_y2 = Y2; i.axisName_charge = ChargeAxis;
                i.buttonName_shoot = Shoot; i.buttonName_shootAlt = ShootAlt; i.buttonName_charge = Charge; i.buttonName_chargeAlt = ChargeAlt;
                i.buttonName_sprint = Sprint; i.buttonName_sprintAlt = SprintAlt; i.buttonName_callPass = CallPass;
            }
        }

        private const float Cooldown = 0.25f;
        private const KeyCode KeyboardKey = KeyCode.E;

        private static readonly Dictionary<BallData, BallData> _body = new Dictionary<BallData, BallData>();       // home -> body (only while away)
        private static readonly Dictionary<BallData, BallData> _controller = new Dictionary<BallData, BallData>(); // body -> home
        private static readonly Dictionary<BallData, Names> _homeNames = new Dictionary<BallData, Names>();       // a human's own input names
        private static readonly Dictionary<BallData, Names> _botNames = new Dictionary<BallData, Names>();        // a bot's names before it was taken
        private static readonly Dictionary<BallData, float> _lastSwitch = new Dictionary<BallData, float>();
        private static Names _anyBotNames;

        /// <summary>Client: switch presses so far (sent in every input message).</summary>
        internal static byte ClientSwitchCount;

        /// <summary>Has anyone switched this match? (Until then everything behaves exactly like the game.)</summary>
        internal static bool Any { get { return _body.Count > 0 || _touched; } }
        private static bool _touched;

        internal static void Clear()
        {
            _body.Clear(); _controller.Clear(); _homeNames.Clear(); _botNames.Clear(); _lastSwitch.Clear();
            _anyBotNames = null; _touched = false;
        }

        private void OnLevelWasLoaded(int level) { Clear(); }

        /// <summary>The ball this human (by home ball) controls right now.</summary>
        internal static BallData BodyOf(BallData home)
        {
            BallData b;
            return home != null && _body.TryGetValue(home, out b) && b != null ? b : home;
        }

        /// <summary>The human (home ball) whose controls drive this ball, or null when the AI plays it.</summary>
        internal static BallData ControllerOf(BallData ball)
        {
            if (ball == null) return null;
            BallData home;
            if (_controller.TryGetValue(ball, out home) && home != null) return home;
            if (_body.ContainsKey(ball)) return null; // a human who is away: the AI plays their ball
            return ball.playerData != null && !ball.playerData.AI ? ball : null;
        }

        /// <summary>Host: the slot whose network input drives this ball (-1 = none / the AI).</summary>
        internal static int InputSlot(BallData ball)
        {
            var c = ControllerOf(ball);
            return c != null ? Match.Slot(c) : -1;
        }

        // ------------------------------------------------------------------ input

        private void Update()
        {
            var gm = Globals.gameManager;
            if (gm == null || gm.teams == null || gm.puck == null) return;
            if (gm.replayManager != null && gm.replayManager.replaying) return;
            if (gm.pauseMenuManager != null && gm.pauseMenuManager.paused) return;
            if (NetSession.IsClient && !NetSession.ClientInMatch) return;

            if (NetSession.ClientInMatch)
            {
                var mine = Match.Ball(Match.LocalSlot);
                if (mine != null && Pressed(Names.Of(mine.GetComponent<BallInput>()))) ClientSwitchCount++;
                return;
            }

            foreach (var t in gm.teams)
                foreach (var home in t.members.ToArray())
                {
                    if (home == null || home.playerData == null || home.playerData.AI) continue;
                    if (NetSession.HostInMatch && Match.IsRemote(Match.Slot(home))) continue; // asks over the network
                    if (Pressed(HomeNames(home))) Request(home);
                }

            if (NetSession.HostInMatch)
                foreach (var kv in NetInput.BySlot.ToList())
                    if (kv.Value != null && kv.Value.SwitchDown()) Request(Match.Ball(kv.Key));
        }

        /// <summary>X on this player's controller (X_P1.. / X_ALL), or E on the keyboard.</summary>
        private static bool Pressed(Names n)
        {
            if (n == null || string.IsNullOrEmpty(n.Shoot)) return false;
            if (n.Shoot == "ShootKeyboard") return Input.GetKeyDown(KeyboardKey);
            string x = null;
            if (n.Shoot.StartsWith("R1_")) x = "X_" + n.Shoot.Substring(3);
            if (x == null) return false;
            try { return Input.GetButtonDown(x); }
            catch { return x == "X_ALL" && Input.GetKeyDown(KeyCode.JoystickButton2); } // no such button: X on any controller
        }

        private static Names HomeNames(BallData home)
        {
            Names n;
            if (!_homeNames.TryGetValue(home, out n)) _homeNames[home] = n = Names.Of(home.GetComponent<BallInput>());
            return n;
        }

        // ------------------------------------------------------------------ switching (host / offline)

        /// <summary>Host / offline: this human (by home ball) pressed switch.</summary>
        internal static void Request(BallData home)
        {
            if (home == null || home.playerData == null || home.playerData.AI || !Rounds.CardGame.IsAuthority) return;
            var gm = Globals.gameManager;
            float last;
            if (_lastSwitch.TryGetValue(home, out last) && Time.time - last < Cooldown) return;
            var body = BodyOf(home);
            if (body.carryObject != null) return; // not while you have the puck
            var puck = (Vector2)gm.puck.transform.position;
            var target = home.team.members
                .Where(m => m != null && m != body && m.alive && !m.fallen && m.playerData != null)
                .Where(m => m == home || (m.playerData.AI && !_controller.ContainsKey(m)))
                .OrderBy(m => Vector2.Distance(m.transform.position, puck))
                .FirstOrDefault();
            if (target == null) return;
            _lastSwitch[home] = Time.time;
            Move(home, target);
            NetLog.Info("Switch: " + Name(home) + " now controls " + (target == home ? "their own player" : "bot " + Name(target)));
            if (NetSession.HostInMatch) NetSession.Instance.HostSendPossession();
        }

        /// <summary>Host: a player left; give their body back to the AI and put their own ball back.</summary>
        internal static void Release(BallData home)
        {
            if (home == null || !_body.ContainsKey(home)) return;
            Move(home, home);
            if (NetSession.HostInMatch) NetSession.Instance.HostSendPossession();
        }

        private static void Move(BallData home, BallData target)
        {
            var names = HomeNames(home);
            var body = BodyOf(home);
            _touched = true;
            if (target != home && !_botNames.ContainsKey(target)) _botNames[target] = Names.Of(target.GetComponent<BallInput>());
            if (_anyBotNames == null && target != home) _anyBotNames = _botNames[target];

            // the ball we leave goes to the AI
            Names leaveAs;
            if (!_botNames.TryGetValue(body, out leaveAs)) leaveAs = _anyBotNames ?? new Names();
            MakeBot(body, leaveAs);
            _controller.Remove(body);

            MakeHuman(target, names);
            if (target == home) _body.Remove(home);
            else { _body[home] = target; _controller[target] = home; }
            RefreshMarkers();
        }

        private static void MakeBot(BallData b, Names names)
        {
            b.AI = true;
            names.To(b.GetComponent<BallInput>());
            var h = b.GetComponent<BallHandling>();
            if (h != null) { h.AICharging = false; if (h.ballCharge > 0f) h.ResetCharge(); }
            var ai = b.GetComponent<BallAI_v2>();
            if (ai != null)
            {
                var t = Globals.gameManager.teams.IndexOf(b.team);
                if (b.playerData != null && !b.playerData.AI && t >= 0 && AIDifficultyManager.skill_shooting != null && t < AIDifficultyManager.skill_shooting.Length)
                {
                    ai.skill_aggression = AIDifficultyManager.skill_aggression[t];
                    ai.skill_agility = AIDifficultyManager.skill_agility[t];
                    ai.skill_goaltending = AIDifficultyManager.skill_goaltending[t];
                    ai.skill_shooting = AIDifficultyManager.skill_shooting[t];
                }
                ai.enabled = true;
            }
        }

        private static void MakeHuman(BallData b, Names names)
        {
            var ai = b.GetComponent<BallAI_v2>();
            if (ai != null) ai.enabled = false;
            b.AI = false;
            var input = b.GetComponent<BallInput>();
            names.To(input);
            input.axis1Input = input.axis2Input = Vector2.zero;
            var h = b.GetComponent<BallHandling>();
            if (h != null) { h.AICharging = false; if (h.ballCharge > 0f) h.ResetCharge(); }
        }

        /// <summary>Robot marker on every ball the AI plays; name tags on every ball (the patch decides the text).</summary>
        private static void RefreshMarkers()
        {
            var gm = Globals.gameManager;
            if (gm == null) return;
            foreach (var t in gm.teams)
                foreach (var b in t.members)
                {
                    if (b == null) continue;
                    if (b.robotIndicator != null) b.robotIndicator.SetActive(ControllerOf(b) == null);
                    var tag = b.GetComponent<BallNameTag>();
                    if (tag != null) { tag.enabled = true; if (tag.nameTag != null) tag.nameTag.gameObject.SetActive(true); }
                }
        }

        // ------------------------------------------------------------------ online

        /// <summary>(home slot, body slot) for every human who is away from their own ball.</summary>
        internal static List<KeyValuePair<int, int>> State()
        {
            return _body.Where(kv => kv.Key != null && kv.Value != null)
                .Select(kv => new KeyValuePair<int, int>(Match.Slot(kv.Key), Match.Slot(kv.Value)))
                .Where(kv => kv.Key >= 0 && kv.Value >= 0).ToList();
        }

        /// <summary>Client: the host's switch state (only changes what this PC draws).</summary>
        internal static void ClientApply(List<KeyValuePair<int, int>> state)
        {
            _body.Clear(); _controller.Clear(); _touched = true;
            foreach (var kv in state)
            {
                var home = Match.Ball(kv.Key);
                var body = Match.Ball(kv.Value);
                if (home == null || body == null || home == body) continue;
                _body[home] = body;
                _controller[body] = home;
            }
            RefreshMarkers();
        }

        private static string Name(BallData b) { return b.playerData != null && b.playerData.account != null ? b.playerData.account.name : "?"; }
    }

    /// <summary>After a switch, the controlling human's name tag is shown over the ball they control.</summary>
    [HarmonyPatch(typeof(BallNameTag), "LateUpdate")]
    internal static class PossessionNameTagPatch
    {
        private static bool Prefix(BallNameTag __instance)
        {
            if (!Possession.Any) return true;
            var tag = __instance.nameTag;
            if (tag == null) return false;
            var b = __instance.GetComponent<BallData>();
            var who = Possession.ControllerOf(b);
            if (who == null || !b.alive || b.fallen || who.playerData == null || who.playerData.account == null) { tag.text = string.Empty; return false; }
            var gm = Globals.gameManager;
            var size = gm != null && gm.gameCam != null ? gm.gameCam.GetComponent<Camera>().orthographicSize : 9f;
            var t = tag.gameObject.transform;
            t.position = __instance.transform.position + Vector3.up * Map.map(size, 7f, 12f, 0.45f, 0.65f) - Vector3.forward;
            t.rotation = Quaternion.identity;
            t.localScale = Vector3.one * Map.map(size, 7f, 12f, 0.1f, 0.17f);
            tag.text = who.playerData.account.name;
            return false;
        }
    }
}
