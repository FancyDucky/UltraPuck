using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace CP99Online.Patches
{
    /// <summary>
    /// PlayStation pads (DualSense, DualShock 4) without Steam Input. The game's input settings only know the Xbox
    /// layout, and Windows gives a PlayStation pad a different one: its buttons come in another order (Square is
    /// button 0, where the game expects A), its right stick is on axes the game never reads, and its triggers rest
    /// at -1 on the axes the game uses for aiming. So for every joystick Unity names as a PlayStation pad, this
    /// reads the pad directly (winmm, the plain Windows joystick API) and answers the game's Xbox-named inputs
    /// ("A_P2", "Joystick2X_P2", "Start_ALL"...) from it the way an Xbox pad would. With Steam Input on, Steam
    /// already shows the game an Xbox pad, Unity doesn't name a PlayStation pad, and nothing here changes.
    /// </summary>
    internal static class PadRemap
    {
        // ------------------------------------------------------------------ winmm

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct JoyCaps
        {
            public ushort wMid, wPid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax, wNumButtons, wPeriodMin, wPeriodMax,
                wRmin, wRmax, wUmin, wUmax, wVmin, wVmax, wCaps, wMaxAxes, wNumAxes, wMaxButtons;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JoyInfoEx
        {
            public uint dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos, dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
        }

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int joyGetDevCapsW(IntPtr id, ref JoyCaps caps, int size);
        [DllImport("winmm.dll")] private static extern int joyGetPosEx(int id, ref JoyInfoEx info);

        private const ushort Sony = 0x054C;
        private const uint ReturnAll = 0xFF | 0x200; // every axis, buttons, continuous POV

        // ------------------------------------------------------------------ pads

        private sealed class Pad
        {
            public int Id; // winmm id
            public JoyCaps Caps;
            public float LX, LY, RX, RY, PovX, PovY;
            public uint Buttons, Prev;
            public int Frame = -1;
        }

        // DirectInput order on PlayStation pads: 0 Square, 1 Cross, 2 Circle, 3 Triangle, 4 L1, 5 R1, 6 L2, 7 R2,
        // 8 Create/Share, 9 Options, 10 L3, 11 R3, 12 PS, 13 touchpad
        private static readonly Dictionary<string, int> ButtonOf = new Dictionary<string, int>
        {
            { "A", 1 }, { "B", 2 }, { "X", 0 }, { "Y", 3 }, { "L1", 4 }, { "R1", 5 },
            { "Back", 8 }, { "Start", 9 }, { "Lstick", 10 },
        };

        private static readonly Pad[] ByJoystick = new Pad[9]; // Unity joystick 1..8 -> pad (null: not a PlayStation pad)
        private static bool _any;
        private static float _nextScan;
        private static bool _failed;
        private static string _lastLayout;
        private static readonly bool TestLog = Environment.GetEnvironmentVariable("CP99_TEST_PAD") == "1";

        private static void Scan()
        {
            if (_failed || Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 2f;
            try
            {
                var names = Input.GetJoystickNames();
                var sony = new List<Pad>();
                for (var id = 0; id < 16; id++)
                {
                    var caps = new JoyCaps();
                    if (joyGetDevCapsW((IntPtr)id, ref caps, Marshal.SizeOf(typeof(JoyCaps))) != 0 || caps.wMid != Sony) continue;
                    var probe = new JoyInfoEx { dwSize = (uint)Marshal.SizeOf(typeof(JoyInfoEx)), dwFlags = ReturnAll };
                    if (joyGetPosEx(id, ref probe) != 0) continue; // not plugged in
                    sony.Add(new Pad { Id = id, Caps = caps });
                }

                var k = 0;
                _any = false;
                var layout = string.Empty;
                for (var j = 1; j <= 8; j++)
                {
                    Pad pad = null;
                    if (j <= names.Length && LooksPlayStation(names[j - 1]) && k < sony.Count)
                    {
                        pad = sony[k++];
                        var old = ByJoystick[j];
                        if (old != null && old.Id == pad.Id) pad = old; // keep its button history
                        layout += " " + j + "=\"" + names[j - 1] + "\"->pad" + pad.Id;
                    }
                    ByJoystick[j] = pad;
                    _any |= pad != null;
                }
                if (layout != _lastLayout)
                {
                    _lastLayout = layout;
                    if (layout.Length > 0) Plugin.Logger_.LogInfo("PlayStation pads read directly (no Steam Input):" + layout);
                }
                if (TestLog && _any)
                    Plugin.Logger_.LogInfo("Pad test: " + string.Join(" ", new[] { "Joystick1X_P1", "Joystick1Y_P1", "Joystick2X_P1", "Joystick2Y_P1", "Joystick2X_ALL", "Joystick2Y_ALL", "Horizontal_ALL" }
                        .Select(n => n + "=" + Input.GetAxis(n).ToString("0.00")).ToArray()) + " A_P1=" + Input.GetButton("A_P1") + " A_ALL=" + Input.GetButton("A_ALL") + " Start_ALL=" + Input.GetButton("Start_ALL"));
            }
            catch (Exception e)
            {
                _failed = true; _any = false;
                Plugin.Logger_.LogWarning("PlayStation pad support off: " + e.Message);
            }
        }

        private static bool LooksPlayStation(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.ToLowerInvariant();
            return n.Contains("wireless controller") || n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("sony") || n.Contains("playstation");
        }

        private static float Norm(uint v, uint min, uint max)
        {
            if (max <= min) return 0f;
            var f = (v - (float)min) / (max - min) * 2f - 1f;
            return Mathf.Abs(f) < 0.08f ? 0f : Mathf.Clamp(f, -1f, 1f);
        }

        private static Pad Read(int joystick)
        {
            var pad = joystick >= 1 && joystick <= 8 ? ByJoystick[joystick] : null;
            if (pad == null || pad.Frame == Time.frameCount) return pad;
            pad.Frame = Time.frameCount;
            pad.Prev = pad.Buttons;
            var info = new JoyInfoEx { dwSize = (uint)Marshal.SizeOf(typeof(JoyInfoEx)), dwFlags = ReturnAll };
            if (joyGetPosEx(pad.Id, ref info) != 0) { pad.Buttons = 0; pad.LX = pad.LY = pad.RX = pad.RY = pad.PovX = pad.PovY = 0f; return pad; }
            var c = pad.Caps;
            pad.LX = Norm(info.dwXpos, c.wXmin, c.wXmax);
            pad.LY = Norm(info.dwYpos, c.wYmin, c.wYmax);
            pad.RX = Norm(info.dwZpos, c.wZmin, c.wZmax);
            pad.RY = Norm(info.dwRpos, c.wRmin, c.wRmax);
            pad.Buttons = info.dwButtons;
            if (info.dwPOV > 36000) pad.PovX = pad.PovY = 0f;
            else
            {
                var a = info.dwPOV / 100f * Mathf.Deg2Rad; // 0 = up, clockwise
                pad.PovX = Mathf.Round(Mathf.Sin(a));
                pad.PovY = Mathf.Round(Mathf.Cos(a)); // Xbox d-pad axis: up is +1
            }
            return pad;
        }

        // ------------------------------------------------------------------ names

        private struct Parsed { public string Role; public int Joy; public bool Known; } // Joy 0: _ALL

        private static readonly Dictionary<string, Parsed> Names = new Dictionary<string, Parsed>();

        private static Parsed Parse(string name)
        {
            Parsed p;
            if (name == null) return default(Parsed);
            if (Names.TryGetValue(name, out p)) return p;
            p = default(Parsed);
            var n = name;
            var dpad = n.EndsWith("_DPAD") || n.EndsWith("_DPAD_W10");
            if (dpad) n = n.Substring(0, n.IndexOf("_DPAD", StringComparison.Ordinal));
            var us = n.LastIndexOf('_');
            if (us > 0)
            {
                var role = n.Substring(0, us);
                var who = n.Substring(us + 1);
                int joy = -1;
                if (who == "ALL") joy = 0;
                else if (who.Length == 2 && who[0] == 'P' && who[1] >= '1' && who[1] <= '8') joy = who[1] - '0';
                if (role == "Horizontal") role = "Joystick1X";
                if (role == "Vertical") role = "Joystick1Y";
                if (dpad) role += "_DPAD";
                if (joy >= 0 && (ButtonOf.ContainsKey(role) || role.StartsWith("Joystick", StringComparison.Ordinal)))
                    p = new Parsed { Role = role, Joy = joy, Known = true };
            }
            Names[name] = p;
            return p;
        }

        private static float PadAxis(Pad pad, string role)
        {
            switch (role)
            {
                case "Joystick1X": return pad.LX;
                case "Joystick1Y": return pad.LY;
                case "Joystick2X": return pad.RY; // the game reads the Xbox 5th axis here (right stick up/down)
                case "Joystick2Y": return pad.RX; // and the 4th (right stick left/right)
                case "Joystick1X_DPAD": return pad.PovX;
                case "Joystick1Y_DPAD": return pad.PovY;
                default: return 0f;
            }
        }

        private enum Edge { Held, Down, Up }

        private static bool PadButton(Pad pad, string role, Edge edge)
        {
            int b;
            if (!ButtonOf.TryGetValue(role, out b)) return false;
            var bit = 1u << b;
            var now = (pad.Buttons & bit) != 0;
            var was = (pad.Prev & bit) != 0;
            return edge == Edge.Held ? now : edge == Edge.Down ? now && !was : !now && was;
        }

        /// <summary>The Xbox button number the game's input settings use for this name.</summary>
        private static readonly Dictionary<string, int> XboxButton = new Dictionary<string, int>
        {
            { "A", 0 }, { "B", 1 }, { "X", 2 }, { "Y", 3 }, { "L1", 4 }, { "R1", 5 }, { "Back", 6 }, { "Start", 7 }, { "Lstick", 8 },
        };

        private static bool OtherPadsButton(string role, Edge edge)
        {
            // "_ALL" on every joystick that isn't a PlayStation pad, the way the input settings define it
            int xb;
            if (!XboxButton.TryGetValue(role, out xb)) return false;
            for (var j = 1; j <= 8; j++)
            {
                if (ByJoystick[j] != null) continue;
                var key = (KeyCode)((int)KeyCode.Joystick1Button0 + (j - 1) * 20 + xb);
                if (edge == Edge.Held ? Input.GetKey(key) : edge == Edge.Down ? Input.GetKeyDown(key) : Input.GetKeyUp(key)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ patches

        private static bool _inside; // reading the per-player axes for "_ALL" goes through the patched method again

        private static void AxisPostfix(string __0, ref float __result)
        {
            if (_inside) return;
            Scan();
            if (!_any) return;
            var p = Parse(__0);
            if (!p.Known || ButtonOf.ContainsKey(p.Role)) return;
            if (p.Joy > 0)
            {
                var pad = Read(p.Joy);
                if (pad != null) __result = PadAxis(pad, p.Role);
                return;
            }
            // _ALL: the strongest of every joystick, with PlayStation pads read the Xbox way
            var best = 0f;
            _inside = true;
            try
            {
                var perPlayer = p.Role.EndsWith("_DPAD") ? p.Role.Replace("_DPAD", "_P{0}_DPAD") : p.Role + "_P{0}";
                for (var j = 1; j <= 8; j++)
                {
                    var pad = Read(j);
                    float v;
                    if (pad != null) v = PadAxis(pad, p.Role);
                    else
                    {
                        try { v = Input.GetAxis(string.Format(perPlayer, j)); } catch { v = 0f; }
                    }
                    if (Mathf.Abs(v) > Mathf.Abs(best)) best = v;
                }
            }
            finally { _inside = false; }
            __result = best;
        }

        private static void Button(string buttonName, ref bool __result, Edge edge)
        {
            if (_inside) return;
            Scan();
            if (!_any) return;
            var p = Parse(buttonName);
            if (!p.Known || !ButtonOf.ContainsKey(p.Role)) return;
            if (p.Joy > 0)
            {
                var pad = Read(p.Joy);
                if (pad != null) __result = PadButton(pad, p.Role, edge);
                return;
            }
            var any = OtherPadsButton(p.Role, edge);
            for (var j = 1; j <= 8 && !any; j++)
            {
                var pad = Read(j);
                if (pad != null && PadButton(pad, p.Role, edge)) any = true;
            }
            __result = any;
        }

        private static void ButtonPostfix(string __0, ref bool __result) { Button(__0, ref __result, Edge.Held); }
        private static void ButtonDownPostfix(string __0, ref bool __result) { Button(__0, ref __result, Edge.Down); }
        private static void ButtonUpPostfix(string __0, ref bool __result) { Button(__0, ref __result, Edge.Up); }

        /// <summary>Patch Unity's Input methods (each on its own, so one failing leaves the rest of the mod alone).</summary>
        internal static void Install(Harmony harmony)
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer) return;
            Patch(harmony, "GetAxis", "AxisPostfix");
            Patch(harmony, "GetAxisRaw", "AxisPostfix");
            Patch(harmony, "GetButton", "ButtonPostfix");
            Patch(harmony, "GetButtonDown", "ButtonDownPostfix");
            Patch(harmony, "GetButtonUp", "ButtonUpPostfix");
        }

        private static void Patch(Harmony harmony, string method, string postfix)
        {
            try
            {
                var target = AccessTools.Method(typeof(Input), method, new[] { typeof(string) });
                harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(PadRemap), postfix)));
            }
            catch (Exception e)
            {
                Plugin.Logger_.LogWarning("PlayStation pad support: couldn't hook Input." + method + ": " + e.Message);
            }
        }
    }
}
