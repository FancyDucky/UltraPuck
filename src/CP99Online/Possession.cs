using System.Collections.Generic;
using System.Linq;
using CP99Online.Net;
using UnityEngine;

namespace CP99Online
{
    /// <summary>
    /// Bot swapping: press X (Xbox) / Square (PlayStation) / E (keyboard) and you trade places with a bot teammate:
    /// you jump to where it was (facing its way, with its speed) and it jumps to where you were. You always keep
    /// your own player. The first press picks the bot nearest the puck; pressing again soon after moves on to the
    /// next bot, so with several bots you can cycle through them. If the bot you swap with has the puck, the puck
    /// comes with the swap: you end up holding it.
    ///
    /// When a bot gets the puck on a team with people on it, one of those people (at random) swaps with it
    /// automatically, so a person always ends up with the puck.
    ///
    /// The host (or the only PC) decides swaps; positions reach clients in the normal snapshots. Online clients
    /// ask with a counter in their input message.
    /// </summary>
    internal sealed class Possession : MonoBehaviour
    {
        private const float Cooldown = 0.25f;
        private const float CycleWindow = 3f; // presses this close together move on to the next bot
        private const KeyCode KeyboardKey = KeyCode.E;

        private sealed class Cycle { public float At; public List<BallData> Order = new List<BallData>(); public int Next; }
        private static readonly Dictionary<BallData, Cycle> _cycle = new Dictionary<BallData, Cycle>();
        private static readonly Dictionary<BallData, float> _lastSwap = new Dictionary<BallData, float>();
        private static BallData _lastCarrier;

        /// <summary>Client: swap presses so far by this PC's player (by slot), sent in every input message.</summary>
        internal static readonly Dictionary<int, byte> ClientSwitchCounts = new Dictionary<int, byte>();

        internal static byte ClientSwitchCountOf(int slot)
        {
            byte n;
            return ClientSwitchCounts.TryGetValue(slot, out n) ? n : (byte)0;
        }

        internal static void ClientPressSwitch(int slot) { ClientSwitchCounts[slot] = (byte)(ClientSwitchCountOf(slot) + 1); }

        internal static void Clear() { _cycle.Clear(); _lastSwap.Clear(); _lastCarrier = null; }

        private void OnLevelWasLoaded(int level) { Clear(); }

        // Everyone keeps their own player now, so these are simple (kept for the input and mouse-aim code).
        internal static BallData BodyOf(BallData home) { return home; }
        internal static BallData ControllerOf(BallData ball) { return ball != null && ball.playerData != null && !ball.playerData.AI ? ball : null; }
        internal static int InputSlot(BallData ball) { var c = ControllerOf(ball); return c != null ? Match.Slot(c) : -1; }

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
                foreach (var slot in Match.LocalSlots)
                {
                    var mine = Match.Ball(slot);
                    if (mine != null && Pressed(mine.GetComponent<BallInput>())) ClientPressSwitch(slot);
                }
                return;
            }

            foreach (var t in gm.teams)
                foreach (var me in t.members.ToArray())
                {
                    if (me == null || me.playerData == null || me.playerData.AI) continue;
                    if (NetSession.HostInMatch && Match.IsRemote(Match.Slot(me))) continue; // asks over the network
                    if (Pressed(me.GetComponent<BallInput>())) Request(me);
                }

            if (NetSession.HostInMatch)
                foreach (var kv in NetInput.BySlot.ToList())
                    if (kv.Value != null && kv.Value.SwitchDown()) Request(Match.Ball(kv.Key));

            AutoSwap(gm);
        }

        /// <summary>X on this player's controller (X_P1.. / X_ALL), or E on the keyboard.</summary>
        private static bool Pressed(BallInput i)
        {
            if (i == null || string.IsNullOrEmpty(i.buttonName_shoot)) return false;
            if (i.buttonName_shoot == "ShootKeyboard") return Input.GetKeyDown(KeyboardKey);
            if (!i.buttonName_shoot.StartsWith("R1_")) return false;
            var x = "X_" + i.buttonName_shoot.Substring(3);
            try { return Input.GetButtonDown(x); }
            catch { return x == "X_ALL" && Input.GetKeyDown(KeyCode.JoystickButton2); } // no such button: X on any controller
        }

        private static readonly bool NoAutoTest = System.Environment.GetEnvironmentVariable("CP99_TEST_NOAUTO") == "1"; // tests with an idle host player

        /// <summary>A bot on a team with people on it just got the puck: one of those people swaps with it (and gets the puck).</summary>
        private static void AutoSwap(GameManager gm)
        {
            if (NoAutoTest) return;
            var p = gm.puck.GetComponent<Puck>();
            var carrier = p != null && p.carryer != null ? p.carryer.GetComponent<BallData>() : null;
            if (carrier == _lastCarrier) return;
            _lastCarrier = carrier;
            if (carrier == null || carrier.playerData == null || !carrier.playerData.AI || Rounds.CardGame.PhaseActive || carrier.team == null) return;
            if (gm.startCountdown > 0f || gm.roundOver) return;
            var people = carrier.team.members.Where(m => m != null && m.playerData != null && !m.playerData.AI && m.alive && !m.fallen).ToList();
            if (NetSession.HostInMatch) people.RemoveAll(h => Match.IsRemote(Match.Slot(h)) && !NetInput.BySlot.ContainsKey(Match.Slot(h))); // left the game
            if (people.Count == 0) return;
            var me = people[Random.Range(0, people.Count)];
            _lastSwap[me] = Time.time;
            Swap(me, carrier);
            NetLog.Info("Swap: bot " + Name(carrier) + " got the puck; " + Name(me) + " swaps places with it");
        }

        // ------------------------------------------------------------------ swapping (host / offline)

        /// <summary>Host / offline: this person pressed swap.</summary>
        internal static void Request(BallData me)
        {
            if (me == null || me.playerData == null || me.playerData.AI || !Rounds.CardGame.IsAuthority) return;
            var gm = Globals.gameManager;
            if (gm == null || gm.startCountdown > 0f || gm.roundOver || Rounds.CardGame.PhaseActive) return;
            if (!me.alive || me.fallen || me.carryObject != null) return; // not while you have the puck
            var now = Time.time;
            float last;
            if (_lastSwap.TryGetValue(me, out last) && now - last < Cooldown) return;

            var bots = me.team.members.Where(m => m != null && m != me && m.alive && !m.fallen && m.playerData != null && m.playerData.AI).ToList();
            if (bots.Count == 0) return;
            Cycle c;
            if (!_cycle.TryGetValue(me, out c) || now - c.At > CycleWindow || c.Order.Count == 0 || c.Order.Any(b => !bots.Contains(b)) || bots.Any(b => !c.Order.Contains(b)))
            {
                // a fresh cycle: nearest the puck first
                var puck = (Vector2)gm.puck.transform.position;
                _cycle[me] = c = new Cycle { Order = bots.OrderBy(b => Vector2.Distance(b.transform.position, puck)).ToList() };
            }
            var target = c.Order[c.Next % c.Order.Count];
            c.Next++;
            c.At = now;
            _lastSwap[me] = now;
            Swap(me, target);
            NetLog.Info("Swap: " + Name(me) + " swapped places with bot " + Name(target) + (c.Order.Count > 1 ? " (" + ((c.Next - 1) % c.Order.Count + 1) + " of " + c.Order.Count + ")" : string.Empty));
        }

        /// <summary>Host: a player left (nothing to undo any more: everyone keeps their own player).</summary>
        internal static void Release(BallData home) { }

        /// <summary>Trade places (position, facing, speed); the puck goes to the person if the bot had it.</summary>
        private static void Swap(BallData me, BallData bot)
        {
            var mr = me.GetComponent<Rigidbody2D>();
            var br = bot.GetComponent<Rigidbody2D>();
            Vector3 mp = me.transform.position, bp = bot.transform.position;
            Quaternion mq = me.transform.rotation, bq = bot.transform.rotation;
            var mv = mr != null ? mr.velocity : Vector2.zero;
            var bv = br != null ? br.velocity : Vector2.zero;

            var puck = bot.carryObject;
            if (puck != null)
            {
                var bh = bot.GetComponent<BallHandling>();
                if (bh != null) bh.Drop(puck, 0.5f, false); // the bot can't grab it straight back
            }

            Place(me, mr, new Vector3(bp.x, bp.y, mp.z), bq, bv);
            Place(bot, br, new Vector3(mp.x, mp.y, bp.z), mq, mv);
            Rounds.CardEffects.SwapBoost(me, bot); // a Friendly Fire boost comes with the bot's spot

            if (puck != null)
            {
                var mh = me.GetComponent<BallHandling>();
                if (mh != null) mh.Carry(puck);
            }
        }

        // BallHandling turns the ball to this angle every frame, so it has to move with the swap too
        private static readonly HarmonyLib.AccessTools.FieldRef<BallHandling, float> ZRot =
            HarmonyLib.AccessTools.FieldRefAccess<BallHandling, float>("currentZRot");

        private static void Place(BallData b, Rigidbody2D rb, Vector3 pos, Quaternion rot, Vector2 vel)
        {
            b.transform.position = pos;
            b.transform.rotation = rot;
            if (rb != null) { rb.position = pos; rb.rotation = rot.eulerAngles.z; rb.velocity = vel; }
            var h = b.GetComponent<BallHandling>();
            if (h != null) ZRot(h) = rot.eulerAngles.z;
        }

        private static string Name(BallData b) { return b.playerData != null && b.playerData.account != null ? b.playerData.account.name : "?"; }

        // ------------------------------------------------------------------ online (no longer used: positions travel in snapshots)

        internal static List<KeyValuePair<int, int>> State() { return new List<KeyValuePair<int, int>>(); }
        internal static void ClientApply(List<KeyValuePair<int, int>> state) { }
    }
}
