using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CP99Online.Net;
using HarmonyLib;
using UnityEngine;

namespace CP99Online.Patches
{
    // ------------------------------------------------------------------ match setup

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.NewGame))]
    internal static class NewGamePatch
    {
        private static void Prefix()
        {
            TestHarness.ApplyTestMap();
            if (NetSession.IsHost) NetSession.Instance.HostOnNewGame();
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.SpawnPlayers))]
    internal static class SpawnPlayersPatch
    {
        private static void Prefix(GameManager __instance, out int[] __state)
        {
            __state = new int[__instance.teams.Count];
            for (var i = 0; i < __state.Length; i++) __state[i] = __instance.teams[i].members.Count;
        }

        private static void Postfix(GameManager __instance, int[] __state)
        {
            if (NetSession.IsHost && Match.Players != null && Match.Players == PlayerSelectionManager_V2.players)
                NetSession.Instance.HostOnSpawned(__instance, __state);
            else if (NetSession.IsClient && Match.ClientPending)
                NetSession.Instance.ClientOnSpawned(__instance, __state);
        }
    }

    // ------------------------------------------------------------------ host: remote input

    [HarmonyPatch(typeof(BallInput), "Update")]
    internal static class BallInputPatch
    {
        private static bool Prefix(BallInput __instance)
        {
            if (!NetSession.HostInMatch) return true;
            var slot = Possession.InputSlot(__instance.GetComponent<BallData>()); // whoever controls it (bot switching)
            if (!Match.IsRemote(slot)) return true;
            NetInput.Remote r;
            if (NetInput.BySlot.TryGetValue(slot, out r))
            {
                __instance.axis1Input = Vector2.ClampMagnitude(r.Move, 1f);
                __instance.axis2Input = Vector2.ClampMagnitude(r.Aim, 1f);
            }
            else
            {
                __instance.axis1Input = Vector2.zero;
                __instance.axis2Input = Vector2.zero;
            }
            return false;
        }
    }

    /// <summary>Routes the ball scripts' Input calls through NetInput (pass-through for local names).</summary>
    [HarmonyPatch]
    internal static class InputRedirectPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(BallHandling), "Update");
            yield return AccessTools.Method(typeof(BallShove), "Update");
            yield return AccessTools.Method(typeof(BallSprint), "Update");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                var m = ins.operand as MethodInfo;
                if (ins.opcode == OpCodes.Call && m != null && m.DeclaringType == typeof(Input))
                {
                    var ps = m.GetParameters();
                    MethodInfo replacement = null;
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                        replacement = AccessTools.Method(typeof(NetInput), m.Name, new[] { typeof(string) });
                    else if (m.Name == "GetKeyDown" && ps.Length == 1 && ps[0].ParameterType == typeof(KeyCode))
                        replacement = AccessTools.Method(typeof(NetInput), "GetKeyDown", new[] { typeof(KeyCode) });
                    if (replacement != null) ins.operand = replacement;
                }
                yield return ins;
            }
        }
    }

    [HarmonyPatch(typeof(BallHandling), "Update")]
    internal static class SuppressHostKeysPatch
    {
        private static void Prefix(BallHandling __instance)
        {
            NetInput.SuppressKeys = NetSession.HostInMatch && Match.IsRemote(Possession.InputSlot(__instance.GetComponent<BallData>()));
        }

        private static void Postfix() { NetInput.SuppressKeys = false; }
    }

    // ------------------------------------------------------------------ host: broadcast moments

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ScoreGoal))]
    internal static class ScoreGoalPatch
    {
        private static void Postfix(GameManager __instance, TeamData otherTeam, Goal.GoalType goalType)
        {
            if (NetSession.HostInMatch) NetSession.Instance.HostOnScoreGoal(__instance, otherTeam, goalType);
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ResetField))]
    internal static class ResetFieldPatch
    {
        /// <summary>Rounds Mode holds the reset after a goal while cards are picked; it calls ResetField again when done.</summary>
        private static bool Prefix(GameManager __instance, bool suddenDeath_, out bool __state)
        {
            __state = Rounds.CardGame.HoldReset(__instance, suddenDeath_);
            return !__state;
        }

        private static void Postfix(bool suddenDeath_, bool __state)
        {
            if (__state) return; // held: nothing was reset
            if (NetSession.HostInMatch) NetSession.Instance.HostOnResetField(suddenDeath_);
            Rounds.CardGame.AfterReset();
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.NewGame))]
    internal static class CardsNewGamePatch
    {
        private static void Prefix() { Rounds.CardGame.OnNewMatch(); }
    }

    [HarmonyPatch(typeof(GUI_Timer), nameof(GUI_Timer.SuddenDeath))]
    internal static class SuddenDeathPatch
    {
        private static void Prefix(out bool __state)
        {
            __state = NetSession.HostInMatch;
            if (__state) NetSession.Instance.HostOnSuddenDeath(true);
        }

        private static void Postfix(bool __state)
        {
            if (__state) NetSession.Instance.HostOnSuddenDeath(false);
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.GoToMatchOverScreen))]
    internal static class MatchOverPatch
    {
        private static bool Prefix(GameManager __instance)
        {
            if (NetSession.ClientInMatch) return false; // the host's MatchEnd message moves clients on
            if (NetSession.HostInMatch) NetSession.Instance.HostOnMatchOver(__instance);
            return true;
        }
    }

    [HarmonyPatch(typeof(GUI_Timer), nameof(GUI_Timer.GoToMatchOverScreen))]
    internal static class TimerMatchOverPatch
    {
        private static bool Prefix()
        {
            if (NetSession.ClientInMatch) return false;
            if (NetSession.HostInMatch) NetSession.Instance.HostOnMatchOver(Globals.gameManager);
            return true;
        }
    }

    [HarmonyPatch(typeof(BallDeathRespawn), nameof(BallDeathRespawn.Kill))]
    internal static class KillPatch
    {
        private static void Postfix(BallDeathRespawn __instance, bool bloodSplatter)
        {
            if (NetSession.HostInMatch) NetSession.Instance.HostOnKill(__instance, bloodSplatter);
        }
    }

    [HarmonyPatch(typeof(BallHandling), "PingBall")]
    internal static class PingPatch
    {
        private static void Postfix(BallHandling __instance)
        {
            if (NetSession.HostInMatch) NetSession.Instance.HostOnPing(__instance);
        }
    }

    // ------------------------------------------------------------------ client: host is authoritative

    [HarmonyPatch(typeof(Goal), "OnTriggerStay2D")]
    internal static class ClientGoalGuard { private static bool Prefix() { return !NetSession.ClientInMatch; } }

    [HarmonyPatch(typeof(Boost), "OnTriggerEnter2D")]
    internal static class ClientBoostGuard { private static bool Prefix() { return !NetSession.ClientInMatch; } }

    [HarmonyPatch(typeof(Spikes), "OnCollisionEnter2D")]
    internal static class ClientSpikesGuard { private static bool Prefix() { return !NetSession.ClientInMatch; } }

    [HarmonyPatch(typeof(Bumper), "OnCollisionEnter2D")]
    internal static class ClientBumperGuard { private static bool Prefix() { return !NetSession.ClientInMatch; } }

    [HarmonyPatch(typeof(Puck), "FixedUpdate")]
    internal static class ClientPuckGuard { private static bool Prefix() { return !NetSession.ClientInMatch; } }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.EndRound))]
    internal static class ClientEndRoundGuard { private static bool Prefix() { return !NetSession.ClientInMatch; } }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.EndMatch))]
    internal static class ClientEndMatchGuard { private static bool Prefix() { return !NetSession.ClientInMatch; } }

    [HarmonyPatch(typeof(GUI_Timer), nameof(GUI_Timer.EndGame))]
    internal static class ClientEndGameGuard { private static bool Prefix() { return !NetSession.ClientInMatch; } }

    /// <summary>Respawn turns physics back on; clients keep balls as puppets.</summary>
    [HarmonyPatch(typeof(BallDeathRespawn), nameof(BallDeathRespawn.Respawn))]
    internal static class ClientRespawnPatch
    {
        private static void Postfix(BallDeathRespawn __instance)
        {
            if (NetSession.ClientInMatch) NetSession.MakePuppet(__instance.gameObject);
        }
    }

    /// <summary>The clock shows the host's time and never runs out locally.</summary>
    [HarmonyPatch(typeof(GUI_Timer), "Update")]
    internal static class ClientTimerPatch
    {
        private static void Prefix(GUI_Timer __instance)
        {
            if (NetSession.ClientInMatch) __instance.timeLeft = Mathf.Max(NetSession.HostTimeLeft, 0.25f);
        }
    }
}
