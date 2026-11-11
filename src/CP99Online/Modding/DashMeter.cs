using System.Collections.Generic;
using CP99Online;
using CP99Online.Net;
using CP99Online.Rounds;
using UnityEngine;

namespace UltraPuck
{
    /// <summary>
    /// A small bar above each player that shows their dash (boost): one segment per dash charge, each filling
    /// up with white as it recharges and pulsing when it's ready. Works with Rounds Mode cards (Double Dash, Quick Recharge).
    /// </summary>
    internal sealed class DashMeter : UltraMod
    {
        internal static DashMeter Instance;
        private GameObject _host;

        public DashMeter() { Instance = this; }

        public override string Id { get { return "ultrapuck.dashmeter"; } }
        public override string Name { get { return "Dash Meter"; } }
        public override string Author { get { return "UltraPuck"; } }
        public override string Version { get { return Plugin.Version; } }
        public override string Description { get { return "A small bar above each player shows when their dash is ready, one segment per charge."; } }

        internal BepInEx.Configuration.ConfigEntry<bool> Always;

        public override void OnLoad()
        {
            Always = AddChoice("ShowWhen", "Show", false, new[] { false, true }, v => v ? "Always" : "While Recharging",
                "While Recharging: the bar appears after a dash and fades once every charge is ready.");
        }

        public override void OnEnable()
        {
            _host = new GameObject("UltraPuck_DashMeter");
            Object.DontDestroyOnLoad(_host);
            _host.AddComponent<DashMeterView>();
        }

        public override void OnDisable()
        {
            if (_host != null) Object.Destroy(_host);
            _host = null;
        }
    }

    /// <summary>
    /// Dash charges for every ball. The host / offline PC reads the real values; online clients work them out
    /// from the host's shove events and each player's cards (both of which they already receive).
    /// </summary>
    internal static class DashTracker
    {
        private const float GameCooldown = 2f;

        private sealed class Remote { public int Max = 1; public float Cooldown = GameCooldown; public float Charges = 1f; public int CardCount = -1; }
        private static readonly Dictionary<int, Remote> _bySlot = new Dictionary<int, Remote>();

        /// <summary>Client: the host says this slot just dashed.</summary>
        internal static void OnRemoteShove(int slot)
        {
            Remote r;
            if (!_bySlot.TryGetValue(slot, out r)) _bySlot[slot] = r = new Remote();
            r.Charges = Mathf.Max(0f, r.Charges - 1f);
        }

        internal static void Clear() { _bySlot.Clear(); }

        /// <summary>Called once per frame by the meter.</summary>
        internal static void Tick(float dt)
        {
            if (!NetSession.ClientInMatch) return;
            for (var slot = 0; slot < Match.Balls.Length; slot++)
            {
                Remote r;
                if (!_bySlot.TryGetValue(slot, out r)) _bySlot[slot] = r = new Remote();
                var cards = slot < CardGame.Seats.Count ? CardGame.Seats[slot].Cards : null;
                var count = cards != null ? cards.Count : 0;
                if (count != r.CardCount)
                {
                    // Same maths as the host (CardEffects.Recompute), which also refills charges on a new card.
                    var stats = new CardStats();
                    if (cards != null)
                        foreach (var id in cards) { var c = Cards.Get(id); if (c != null) try { c.Apply(stats); } catch { } }
                    r.Max = 1 + Mathf.Clamp(stats.ExtraDashes, 0, 9);
                    r.Cooldown = GameCooldown * Mathf.Clamp(stats.DashCooldown, 0.1f, 5f);
                    if (r.CardCount >= 0) r.Charges = r.Max;
                    r.CardCount = count;
                }
                if (r.Charges < r.Max) r.Charges = Mathf.Min(r.Max, r.Charges + dt / r.Cooldown);
            }
        }

        /// <summary>Max charges and current charges (fractional while recharging).</summary>
        internal static bool Get(BallData b, out int max, out float charges)
        {
            max = 1; charges = 1f;
            if (b == null) return false;
            if (NetSession.ClientInMatch)
            {
                Remote r;
                var slot = Match.Slot(b);
                if (slot < 0 || !_bySlot.TryGetValue(slot, out r)) return false;
                max = r.Max; charges = r.Charges;
                return true;
            }
            var d = CardEffects.DashOf(b);
            if (d != null) { max = d.Max; charges = d.Charges; return true; }
            var shove = b.GetComponent<BallShove>();
            if (shove == null) return false;
            charges = Mathf.Clamp01(1f - shove.shoveTimer / GameCooldown);
            return true;
        }
    }

    /// <summary>A small bar just above each player: one segment per dash charge (see PlayerBar).</summary>
    internal sealed class DashMeterView : MonoBehaviour
    {
        private readonly PlayerBars _bars = new PlayerBars("DashBar");

        private void OnDestroy() { _bars.Clear(); }
        private void OnLevelWasLoaded(int level) { _bars.Clear(); DashTracker.Clear(); PlayerBar.ClearCache(); }

        private void LateUpdate()
        {
            var gm = Globals.gameManager;
            if (gm == null || gm.teams == null) { if (_bars.Count > 0) _bars.Clear(); return; }
            DashTracker.Tick(Time.deltaTime);
            var always = DashMeter.Instance != null && DashMeter.Instance.Always.Value;
            var seen = new HashSet<BallData>();
            foreach (var team in gm.teams)
                foreach (var b in team.members)
                {
                    if (b == null) continue;
                    seen.Add(b);
                    int max; float charges;
                    var known = DashTracker.Get(b, out max, out charges);
                    var visible = known && b.alive && !b.fallen && !gm.replayManager.replaying;
                    _bars.For(b).Set(b, Mathf.Max(1, max), charges, visible, always,
                        new Color(1f, 1f, 1f, 0.8f), Color.white, 0.5f, 0.78f, 0.09f);
                }
            _bars.Prune(seen);
        }
    }
}
