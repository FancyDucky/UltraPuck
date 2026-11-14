using HarmonyLib;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>
    /// Power Shot: quick shots only. BallHandling.Shoot gets the charge as its power for a charged shot, and is
    /// called with no charge built up for a quick shot (the charge resets inside Shoot), so a quick shot is a Shoot
    /// call while ballCharge is 0.
    /// </summary>
    [HarmonyPatch(typeof(BallHandling), nameof(BallHandling.Shoot))]
    internal static class QuickShotPowerPatch
    {
        private static void Prefix(BallHandling __instance, ref float power)
        {
            if (!CardGame.IsAuthority || __instance.ballCharge > 0f) return;
            var a = CardEffects.AppliedOf(__instance.GetComponent<BallData>());
            if (a != null) power *= Mathf.Clamp(a.Stats.QuickShotPower, 0.2f, 5f);
        }
    }

    /// <summary>Friendly Fire: notice a dash hitting a teammate (the game's own dash-hit window, BallShove).</summary>
    [HarmonyPatch(typeof(BallShove), "OnCollisionEnter2D")]
    internal static class TeammateDashPatch
    {
        private const float DashWindow = 0.25f; // BallShove: shoveTimer > shoveCooldown - 0.25

        private static void Postfix(BallShove __instance, Collision2D collision2D)
        {
            if (!CardGame.IsAuthority || collision2D == null || collision2D.gameObject == null) return;
            var gm = Globals.gameManager;
            if (gm == null || gm.replayManager.replaying) return;
            if (__instance.shoveTimer <= CardEffects.GameDashCooldown - DashWindow) return;
            var target = collision2D.gameObject.GetComponent<BallData>();
            if (target != null) CardEffects.OnDashHit(__instance.GetComponent<BallData>(), target);
        }
    }
}
