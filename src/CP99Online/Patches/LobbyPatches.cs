using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CP99Online.Net;
using HarmonyLib;
using UnityEngine;

namespace CP99Online.Patches
{
    /// <summary>Client: pressing A/Space on player selection asks the host for a box instead of taking one locally.</summary>
    [HarmonyPatch(typeof(PlayerSelectionManager_V2), nameof(PlayerSelectionManager_V2.SignInPlayer), typeof(int))]
    internal static class LobbySignInPatch
    {
        private static bool Prefix(int controllerNumber)
        {
            if (!NetSession.ClientLobbyActive) return true;
            return NetSession.Instance.ClientRequestBox(controllerNumber);
        }
    }

    /// <summary>
    /// Going back to player selection: the game re-signs last match's players into boxes. Online players are
    /// handled by the lobby instead (host re-assigns them, clients get their box back from the host).
    /// </summary>
    [HarmonyPatch(typeof(PlayerSelectionManager_V2), "Start")]
    internal static class PlayerSelectionStartPatch
    {
        private static void Prefix()
        {
            var players = PlayerSelectionManager_V2.players;
            if (players != null)
            {
                if (NetSession.IsClient)
                    PlayerSelectionManager_V2.players = null;
                else
                {
                    players.RemoveAll(p => Match.OnlinePlayers.Contains(p) || NetSession.OwnerFromId(p.joystickId) >= 0);
                    if (players.Count == 0) PlayerSelectionManager_V2.players = null;
                }
            }
            Match.OnlinePlayers.Clear();
        }

        private static void Postfix()
        {
            if (NetSession.IsHost) NetSession.Instance.HostOnPlayerSelectionStart();
        }
    }

    /// <summary>
    /// The options panel (Start to continue, AI bots, difficulty) belongs to the host. On clients its input is
    /// muted and it shows the host's values, so a client can never start the match on its own.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSelectionOptions_Manager), "Update")]
    internal static class LobbyOptionsPatch
    {
        private static void Prefix(PlayerSelectionOptions_Manager __instance)
        {
            if (NetSession.ClientLobbyActive) NetSession.ClientApplyMirroredOptions(__instance);
        }

        private static void Postfix(PlayerSelectionOptions_Manager __instance)
        {
            if (NetSession.ClientLobbyActive) NetSession.ClientApplyMirroredOptions(__instance);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            foreach (var ins in code)
            {
                var m = ins.operand as MethodInfo;
                if (ins.opcode == OpCodes.Call && m != null && m.DeclaringType == typeof(Input))
                {
                    var replacement = typeof(LobbyInput).GetMethod(m.Name, m.GetParameters().Select(p => p.ParameterType).ToArray());
                    if (replacement != null) ins.operand = replacement;
                }
                yield return ins;
            }
        }
    }

    /// <summary>Arena / mode select belongs to the host: a client's own menu keys do nothing there (it shows the host's choices).</summary>
    [HarmonyPatch(typeof(MenuNavigator), "Update")]
    internal static class LobbyNavigatorInputPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            foreach (var ins in code)
            {
                var m = ins.operand as MethodInfo;
                if (ins.opcode == OpCodes.Call && m != null && m.DeclaringType == typeof(Input))
                {
                    var replacement = typeof(NavInput).GetMethod(m.Name, m.GetParameters().Select(p => p.ParameterType).ToArray());
                    if (replacement != null) ins.operand = replacement;
                }
                yield return ins;
            }
        }
    }

    /// <summary>Only the host loads the arena from arena select; clients follow its StartMatch message.</summary>
    [HarmonyPatch(typeof(MenuLoadSceneAsync), "OnEnable")]
    internal static class LobbyLoadScenePatch
    {
        private static bool Prefix() { return !NetSession.ClientInGameSelection; }
    }

    internal static class NavInput
    {
        private static bool Muted { get { return NetSession.ClientInGameSelection; } }
        public static bool GetButtonDown(string n) { return !Muted && Input.GetButtonDown(n); }
        public static bool GetButtonUp(string n) { return !Muted && Input.GetButtonUp(n); }
        public static bool GetButton(string n) { return !Muted && Input.GetButton(n); }
        public static float GetAxisRaw(string n) { return Muted ? 0f : Input.GetAxisRaw(n); }
        public static float GetAxis(string n) { return Muted ? 0f : Input.GetAxis(n); }
        public static bool GetKeyDown(KeyCode k) { return !Muted && Input.GetKeyDown(k); }
        public static bool GetKeyUp(KeyCode k) { return !Muted && Input.GetKeyUp(k); }
        public static bool GetKey(KeyCode k) { return !Muted && Input.GetKey(k); }
    }

    /// <summary>UnityEngine.Input stand-ins that read nothing while a client is in the online lobby.</summary>
    internal static class LobbyInput
    {
        private static bool Muted { get { return NetSession.ClientLobbyActive; } }
        public static bool GetButtonDown(string n) { return !Muted && Input.GetButtonDown(n); }
        public static bool GetButtonUp(string n) { return !Muted && Input.GetButtonUp(n); }
        public static bool GetButton(string n) { return !Muted && Input.GetButton(n); }
        public static float GetAxisRaw(string n) { return Muted ? 0f : Input.GetAxisRaw(n); }
        public static float GetAxis(string n) { return Muted ? 0f : Input.GetAxis(n); }
        public static bool GetKeyDown(KeyCode k) { return !Muted && Input.GetKeyDown(k); }
        public static bool GetKeyUp(KeyCode k) { return !Muted && Input.GetKeyUp(k); }
        public static bool GetKey(KeyCode k) { return !Muted && Input.GetKey(k); }
    }
}
