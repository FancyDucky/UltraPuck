using System.Collections.Generic;
using UnityEngine;

namespace CP99Online.Net
{
    /// <summary>
    /// On the host, remote players' balls get input names like "NET|3|shoot". The game's ball scripts
    /// are transpiled to call these methods instead of UnityEngine.Input, so those names resolve to
    /// the latest controls received from that player; every other name falls through to Input.
    /// Button presses are sent as counters so short taps between packets are never lost.
    /// </summary>
    internal static class NetInput
    {
        internal sealed class Remote
        {
            public Vector2 Move;
            public Vector2 Aim;
            public byte Held;
            public uint Seq;
            public readonly byte[] DownCount = new byte[3]; // shoot, callpass, charge
            public byte ChargeUpCount;

            private int _frame = -1;
            private readonly byte[] _seenDown = new byte[3];
            private byte _seenUp;
            private readonly bool[] _down = new bool[3];
            private bool _up;

            private void Refresh()
            {
                if (_frame == Time.frameCount) return;
                _frame = Time.frameCount;
                for (var i = 0; i < 3; i++)
                {
                    _down[i] = DownCount[i] != _seenDown[i];
                    _seenDown[i] = DownCount[i];
                }
                _up = ChargeUpCount != _seenUp;
                _seenUp = ChargeUpCount;
            }

            /// <summary>Start edge tracking from the current counters (no phantom presses).</summary>
            public void Sync()
            {
                for (var i = 0; i < 3; i++) _seenDown[i] = DownCount[i];
                _seenUp = ChargeUpCount;
            }

            public bool Down(int i) { Refresh(); return _down[i]; }
            public bool ChargeUp() { Refresh(); return _up; }
            public bool Charging { get { return (Held & Proto.HCharge) != 0; } }
            public bool Sprinting { get { return (Held & Proto.HSprint) != 0; } }
        }

        private const string Prefix = "NET|";
        public static readonly Dictionary<int, Remote> BySlot = new Dictionary<int, Remote>();
        private static readonly Dictionary<string, KeyValuePair<int, string>> Parsed = new Dictionary<string, KeyValuePair<int, string>>();

        /// <summary>Set while a remote ball's BallHandling.Update runs, so host keyboard shortcuts don't leak in.</summary>
        public static bool SuppressKeys;

        public static string Name(int slot, string role) { return Prefix + slot + "|" + role; }

        private static bool Parse(string name, out Remote remote, out string role)
        {
            remote = null; role = null;
            if (name == null || !name.StartsWith(Prefix)) return false;
            KeyValuePair<int, string> kv;
            if (!Parsed.TryGetValue(name, out kv))
            {
                var parts = name.Split('|');
                int slot;
                kv = parts.Length == 3 && int.TryParse(parts[1], out slot)
                    ? new KeyValuePair<int, string>(slot, parts[2])
                    : new KeyValuePair<int, string>(-1, string.Empty);
                Parsed[name] = kv;
            }
            role = kv.Value;
            BySlot.TryGetValue(kv.Key, out remote);
            return true;
        }

        public static bool GetButton(string name)
        {
            Remote r; string role;
            if (!Parse(name, out r, out role)) return Input.GetButton(name);
            if (r == null) return false;
            if (role == "charge") return r.Charging;
            if (role == "sprint") return r.Sprinting;
            return false;
        }

        public static bool GetButtonDown(string name)
        {
            Remote r; string role;
            if (!Parse(name, out r, out role)) return Input.GetButtonDown(name);
            if (r == null) return false;
            if (role == "shoot") return r.Down(0);
            if (role == "callpass") return r.Down(1);
            if (role == "charge") return r.Down(2);
            return false;
        }

        public static bool GetButtonUp(string name)
        {
            Remote r; string role;
            if (!Parse(name, out r, out role)) return Input.GetButtonUp(name);
            if (r == null) return false;
            return role == "charge" && r.ChargeUp();
        }

        public static float GetAxis(string name)
        {
            Remote r; string role;
            if (!Parse(name, out r, out role)) return Input.GetAxis(name);
            if (r == null) return 0f;
            return role == "chargeaxis" && r.Charging ? 1f : 0f;
        }

        public static bool GetKeyDown(KeyCode key)
        {
            return !SuppressKeys && Input.GetKeyDown(key);
        }
    }
}
