using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Steamworks;

namespace CP99Online.Net
{
    /// <summary>
    /// Steam lobbies are only used for discovery and invites; gameplay goes over Steam P2P to the
    /// lobby owner. Lobbies are tagged so only UltraPuck lobbies show up in the browser.
    /// </summary>
    internal static class SteamLobby
    {
        internal struct Found
        {
            public CSteamID Lobby;
            public string HostName;
            public int Members;
            /// <summary>Why it can't be joined (e.g. a different UltraPuck version), or empty.</summary>
            public string Note;
        }

        private const string TagKey = "cp99online";
        private static PropertyInfo _initializedProp;
        private static bool _hooked;
        private static CallResult<LobbyCreated_t> _created;
        private static CallResult<LobbyMatchList_t> _listed;
        private static Callback<GameLobbyJoinRequested_t> _joinRequested;
        private static Callback<LobbyEnter_t> _entered;
        private static Callback<LobbyDataUpdate_t> _lobbyData;

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
            _lobbyData = Callback<LobbyDataUpdate_t>.Create(OnLobbyData);
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
            SteamMatchmaking.SetLobbyData(Current, "ver", Plugin.Version);
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
            FindFriendLobbies();
            // Steam's lobby search only returns public lobbies; ours are friends-only, so this finds nothing today.
            // Kept so public lobbies would show up too if hosting ever offers them.
            SteamMatchmaking.AddRequestLobbyListStringFilter(TagKey, Proto.Version.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            _listed.Set(SteamMatchmaking.RequestLobbyList());
        }

        /// <summary>
        /// Friends-only lobbies never come back from Steam's lobby search, so look at what each friend is playing:
        /// a friend in ClusterPuck 99 who is in a lobby reports its id (the same thing Steam's own "Join game" uses).
        /// The lobby's data then says whether it's an UltraPuck game and which version (OnLobbyData).
        /// </summary>
        private static void FindFriendLobbies()
        {
            try
            {
                var app = SteamUtils.GetAppID();
                var n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                for (var i = 0; i < n; i++)
                {
                    var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                    FriendGameInfo_t game;
                    if (!SteamFriends.GetFriendGamePlayed(friend, out game)) continue;
                    if (game.m_gameID.AppID() != app || !game.m_steamIDLobby.IsValid()) continue;
                    var lobby = game.m_steamIDLobby;
                    if (Lobbies.Any(l => l.Lobby == lobby)) continue;
                    Lobbies.Add(new Found { Lobby = lobby, HostName = SteamFriends.GetFriendPersonaName(friend), Members = 0, Note = "checking..." });
                    SteamMatchmaking.RequestLobbyData(lobby);
                }
                NetLog.Info("Join a Friend: " + Lobbies.Count + " friend lobby(s) found");
            }
            catch (Exception ex) { NetLog.Warn("Join a Friend: couldn't read the friends list: " + ex.Message); }
            UpdateStatus();
        }

        private static void OnLobbyData(LobbyDataUpdate_t data)
        {
            if (data.m_ulSteamIDLobby != data.m_ulSteamIDMember) return; // a member's data, not the lobby's
            var i = Lobbies.FindIndex(l => l.Lobby.m_SteamID == data.m_ulSteamIDLobby);
            if (i < 0) return;
            var f = Lobbies[i];
            var tag = SteamMatchmaking.GetLobbyData(f.Lobby, TagKey);
            if (data.m_bSuccess == 0 || string.IsNullOrEmpty(tag))
            {
                Lobbies.RemoveAt(i); // gone, or not an UltraPuck lobby
                UpdateStatus();
                return;
            }
            var host = SteamMatchmaking.GetLobbyData(f.Lobby, "host");
            if (!string.IsNullOrEmpty(host)) f.HostName = host;
            f.Members = SteamMatchmaking.GetNumLobbyMembers(f.Lobby);
            var ver = SteamMatchmaking.GetLobbyData(f.Lobby, "ver");
            f.Note = tag == Proto.Version.ToString() ? string.Empty
                : "needs UltraPuck " + (string.IsNullOrEmpty(ver) ? "(a different version)" : ver);
            Lobbies[i] = f;
            UpdateStatus();
        }

        private static void OnListed(LobbyMatchList_t result, bool ioFailure)
        {
            if (!ioFailure)
                for (var i = 0; i < result.m_nLobbiesMatching; i++)
                {
                    var id = SteamMatchmaking.GetLobbyByIndex(i);
                    if (Lobbies.Any(l => l.Lobby == id)) continue;
                    Lobbies.Add(new Found
                    {
                        Lobby = id,
                        HostName = SteamMatchmaking.GetLobbyData(id, "host"),
                        Members = SteamMatchmaking.GetNumLobbyMembers(id),
                        Note = string.Empty
                    });
                }
            UpdateStatus();
        }

        private static void UpdateStatus()
        {
            BrowserStatus = Lobbies.Count == 0
                ? "No games found. A friend's game shows up here while they're hosting in UltraPuck."
                : string.Empty;
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
