using HarmonyLib;
using UnityEngine;

namespace CP99Online.Patches
{
    internal static class FrameRate
    {
        /// <summary>Applies the FPS+ limit (0 = unlimited), or the game's own 60 when FPS+ is off.</summary>
        public static void Apply()
        {
            var fps = UltraPuck.FpsPlus.Instance;
            var limit = fps != null && fps.Enabled ? Plugin.FrameRateLimit.Value : 60;
            Application.targetFrameRate = limit > 0 ? limit : -1;
        }
    }

    /// <summary>ReplayManager.Awake hard-codes Application.targetFrameRate = 60 in every match.</summary>
    [HarmonyPatch(typeof(ReplayManager), "Awake")]
    internal static class ReplayManagerAwakePatch
    {
        private static void Postfix()
        {
            FrameRate.Apply();
            ReplayCaptureThrottle.Reset();
        }
    }

    /// <summary>
    /// Instant replay records one sample per rendered frame into a 150-slot ring buffer and replays
    /// the last 2.25s, which assumes 60 FPS. Above 60 FPS the buffer would cover too little time, so
    /// samples are limited to 60 Hz and the skipped frames' time is carried into the next sample.
    /// Events logged between samples (shots, checks, boosts) stay in the current slot until it is
    /// captured, so none are lost.
    /// </summary>
    [HarmonyPatch(typeof(ReplayManager), "LateUpdate")]
    internal static class ReplayCaptureThrottle
    {
        private const float Interval = 1f / 60f;
        private static float _accumulated;

        public static void Reset()
        {
            _accumulated = 0f;
        }

        private static bool Prefix(ReplayManager __instance)
        {
            // Online: clients mirror the host's replay instead of running their own.
            if (Net.NetSession.ClientInMatch) return false;
            // Online: turn this frame's replay marks into network events before capture moves on.
            if (Net.NetSession.HostInMatch) Net.NetSession.Instance.HostBeforeReplayCapture(__instance);

            if (__instance.replaying || !__instance.capturing)
            {
                _accumulated = 0f;
                return true;
            }

            _accumulated += Time.deltaTime;
            if (_accumulated < Interval)
            {
                // Mirror the only other thing LateUpdate does when not replaying.
                __instance.instantReplayText.SetActive(false);
                __instance.faderObject.SetActive(false);
                return false;
            }

            // The original adds Time.deltaTime; top it up to the full time since the last sample.
            __instance.currentCaptureTime += _accumulated - Time.deltaTime;
            _accumulated = 0f;
            return true;
        }
    }
}
