using HarmonyLib;

namespace CP99Online.Patches
{
    /// <summary>
    /// BallShove.Shove() adds SpeedAddition(-700, decay 400/s) against a base speed of 600, which
    /// removes all drive for ~0.25s and takes ~1.75s to recover. It is the only decaying negative
    /// SpeedAddition in the game; the sprint brake and shot-charge slowdown do not decay, so they
    /// are left alone.
    /// </summary>
    [HarmonyPatch(typeof(BallData), nameof(BallData.AddAdditionalSpeed))]
    internal static class NoBoostSlowdownPatch
    {
        private static bool Prefix(SpeedAddition speed)
        {
            return !(Plugin.NoBoostSlowdown.Value && speed.useDecay && speed.speed < 0f);
        }
    }
}
