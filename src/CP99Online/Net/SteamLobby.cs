using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Steamworks;

namespace CP99Online.Net
{
    /// <summary>
    /// Steam lobbies are only used for discovery and invites; gameplay goes over Steam P2P to the
    /// lobby owner. Lobbies are tagged so only CP99 Online lobbies show up in the browser.
    /// </summary>
    internal static class SteamLobby
    {
        internal struct Found
        {
            public CSteamID Lobby;
            public string HostName;
            public int Members;
        }

        private const string TagKey = "cp99online";
        private static PropertyInfo _initializedProp;
        private static bool _hooked;
        private static CallResult<LobbyCreated_t> _created;
        private static CallResult<LobbyMatchList_t> _listed;
        private static Callback<GameLobbyJoinRequested_t> _joinRequested;
        private static Callback<LobbyEnter_t> _entered;

        public static CSteamID Current = CSteamID.Nil;
        public static readonly List<Found> Lobbies = new List<Found>();
        public static string BrowserStatus = string.Empty;
        private static ulong _pendingJoinFromCommandLine;

        /// <summary>The game's own SteamManager.Initialized (the class is internal).</summary>
        public static bool Available
        {
            get
            {
                try
                {
                    if (_initializedProp == null)
                    {
                        var t = AccessTools.TypeByName("SteamManager");
                        _initializedProp = t.GetProperty("Initialized", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    }
                    return (bool)_initializedProp.GetValue(null, null);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Called every frame from the plugin once the game is running.</summary>
        public static void Tick()
        {
            if (_hooked || !Available) return;
            _hooked = true;
            _created = CallResult<LobbyCreated_t>.Create(OnCreated);
            _listed = CallResult<LobbyMatchList_t>.Create(OnListed);
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            _entered = Callback<LobbyEnter_t>.Create(OnEntered);
            NetLog.Info("Steam ready for online play");

            // Accepting an invite while the game is closed launches it with "+connect_lobby <id>".
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "+connect_lobby") ulong.TryParse(args[i + 1], out _pendingJoinFromCommandLine);
        }

        /// <summary>Joins a lobby from the launch command line once the main menu is up.</summary>
        public static void TryPendingJoin()
        {
            if (_pendingJoinFromCommandLine == 0 || !_hooked) return;
            var id = new CSteamID(_pendingJoinFromCommandLine);
            _pendingJoinFromCommandLine = 0;
            Join(id);
        }

        public static void CreateLobby()
        {
            if (!Available) return;
            Tick();
            _created.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, Proto.MaxClients + 1));
        }

        private static void OnCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                NetLog.Warn("Steam lobby creation failed: " + result.m_eResult);
                return;
            }
            Current = new CSteamID(result.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Current, TagKey, Proto.Version.ToString());
            SteamMatchmaking.SetLobbyData(Current, "host", NetSession.LocalName());
            NetLog.Info("Steam lobby ready - invite friends from the mod menu or Steam overlay");
        }

        public static void InviteFriends()
        {
            if (Current.IsValid()) SteamFriends.ActivateGameOverlayInviteDialog(Current);
        }

        public static void Refresh()
        {
            if (!Available) { BrowserStatus = "Steam isn't available"; return; }
            Tick();
            Lobbies.Clear();
            BrowserStatus = "Searching...";
            SteamMatchmaking.AddRequestLobbyListStringFilter(TagKey, Proto.Version.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            _listed.Set(SteamMatchmaking.RequestLobbyList());
        }

        private static void OnListed(LobbyMatchList_t result, bool ioFailure)
        {
            Lobbies.Clear();
            if (ioFailure) { BrowserStatus = "Search failed"; return; }
            for (var i = 0; i < result.m_nLobbiesMatching; i++)
            {
                var id = SteamMatchmaking.GetLobbyByIndex(i);
                Lobbies.Add(new Found
                {
                    Lobby = id,
                    HostName = SteamMatchmaking.GetLobbyData(id, "host"),
                    Members = SteamMatchmaking.GetNumLobbyMembers(id)
                });
            }
            BrowserStatus = Lobbies.Count == 0 ? "No games found. Friends-only lobbies show up when a friend is hosting." : string.Empty;
        }

        public static void Join(CSteamID lobby)
        {
            if (NetSession.Instance == null || NetSession.Instance.Role != Role.None) return;
            Tick();
            NetLog.Info("Joining Steam lobby " + lobby.m_SteamID);
            SteamMatchmaking.JoinLobby(lobby);
        }

        private static void OnJoinRequested(GameLobbyJoinRequested_t req)
        {
            // Accepted an invite or clicked "Join game" in the Steam friends list.
            if (NetSession.Instance != null && NetSession.Instance.Role != Role.None)
                NetSession.Instance.Stop("Joining a friend's game");
            Join(req.m_steamIDLobby);
        }

        private static void OnEntered(LobbyEnter_t entered)
        {
            var lobby = new CSteamID(entered.m_ulSteamIDLobby);
            Current = lobby;
            var owner = SteamMatchmaking.GetLobbyOwner(lobby);
            if (owner == SteamUser.GetSteamID()) return; // our own hosted lobby
            var session = NetSession.Instance;
            if (session == null || session.Role != Role.None) return;
            session.JoinSteam(owner);
        }

        public static void Leave()
        {
            if (!Current.IsValid()) return;
            try { SteamMatchmaking.LeaveLobby(Current); } catch { }
            Current = CSteamID.Nil;
        }
    }
}
