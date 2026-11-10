using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CP99Online.Net
{
    /// <summary>
    /// Shared player selection. Every one of the 8 player boxes is free, owned by the host (0) or owned by a
    /// client (its id). The owner runs its box for real (own controller, own local profiles); everyone else
    /// shows it as a "phone controller" box with no input, mirroring the name/symbol/team the owner reports.
    /// The host hands out slots, keeps the master table and tells clients which menu scene to be in.
    /// </summary>
    internal sealed partial class NetSession
    {
        internal const int Boxes = 8;
        internal const string NetIdPrefix = "NET:";
        private static readonly string[] LobbyScenes = { "MainMenu", "PlayerSelection", "GameSelection" };

        // ---- shared helpers

        internal static bool InPlayerSelection
        {
            get
            {
                return Application.loadedLevelName == "PlayerSelection" && MenuGlobals.menuManager != null &&
                       MenuGlobals.menuManager.playerSelectionManager_V2 != null;
            }
        }

        private static PlayerSelectionManager_V2 SelMgr { get { return MenuGlobals.menuManager.playerSelectionManager_V2; } }
        private static PlayerSelectionOptions_Manager SelOptions { get { return MenuGlobals.menuManager.playerSelectionOptions_Manager; } }

        internal static bool IsNetBox(PlayerBox_V2 box)
        {
            return box.controllerNumber == 9 && box.controllerId != null && box.controllerId.StartsWith(NetIdPrefix);
        }

        internal static int OwnerFromId(string joystickId)
        {
            int id;
            if (joystickId == null || !joystickId.StartsWith(NetIdPrefix)) return -1;
            return int.TryParse(joystickId.Substring(NetIdPrefix.Length), out id) ? id : -1;
        }

        /// <summary>Remote boxes read their buttons from a phone-controller object; give them one that never presses anything.</summary>
        private static void EnsurePhone(string id)
        {
            if (SmartphoneManager.connectedSmartphones == null) SmartphoneManager.connectedSmartphones = new List<SmartphoneController>();
            if (!SmartphoneManager.connectedSmartphones.Any(p => p.controllerId == id))
                SmartphoneManager.connectedSmartphones.Add(new SmartphoneController(id, true));
        }

        /// <summary>Shows another player's box: in progress ("JOINING...", no team) or signed in with their profile and team.</summary>
        private static void MirrorBox(PlayerBox_V2 box, int owner, int state, int team, string name, int symbol)
        {
            if (owner < 0)
            {
                if (box.boxActive && IsNetBox(box)) box.SignOut();
                return;
            }
            var id = NetIdPrefix + owner;
            EnsurePhone(id);
            if (!box.boxActive || box.controllerId != id || box.controllerNumber != 9) box.SignIn(id);
            var ready = state == (int)PlayerBox_V2.PlayerBoxState.SIGNEDIN;
            var shownName = ready ? (string.IsNullOrEmpty(name) ? "PLAYER" : name) : "JOINING...";
            var shownSymbol = ready ? symbol : 0;
            var shownTeam = ready ? team : -1;
            if (box.currentState != PlayerBox_V2.PlayerBoxState.SIGNEDIN || box.playerAccount == null ||
                box.playerAccount.name != shownName || box.playerAccount.symbol != shownSymbol)
            {
                box.playerAccount = new Account(shownName, false, shownSymbol);
                box.ChangeStateTo(PlayerBox_V2.PlayerBoxState.SIGNEDIN);
            }
            if (box.currentTeam != shownTeam) box.ChangeTeamsTo(shownTeam);
        }

        private static void SyncScroller(PlayerSelectionOptions_Scroller s, int value)
        {
            if (s != null && s.currentValue != value) s.Scroll(value - s.currentValue);
        }

        // ---------------------------------------------------------------- host

        private string _tableSig;
        private float _tableSentAt;

        /// <summary>Called from HostUpdate.</summary>
        private void LobbyHostUpdate()
        {
            if (_clients.Count == 0) return;
            if (Application.loadedLevelName == "GameSelection") { HostSendNav(); return; }
            if (!InPlayerSelection) return;
            var boxes = SelMgr.playerBoxes;
            foreach (var ci in _clients.Values)
                if (ci.BoxSlot >= 0 && ci.BoxSlot < boxes.Length)
                    MirrorBox(boxes[ci.BoxSlot], ci.Id, ci.BoxStateValue, ci.BoxTeam, ci.BoxName, ci.BoxSymbol);

            WriteTable();
            var sig = System.Convert.ToBase64String(_msg.Buffer, 0, _msg.Length);
            if (sig != _tableSig || Now - _tableSentAt > 2f)
            {
                _tableSig = sig;
                _tableSentAt = Now;
                BroadcastReliable();
            }
        }

        private void WriteTable()
        {
            var boxes = SelMgr.playerBoxes;
            Begin(Msg.BoxTable);
            for (var i = 0; i < Boxes; i++)
            {
                var box = i < boxes.Length ? boxes[i] : null;
                if (box == null || !box.boxActive) { _msg.U8(255); _msg.U8(0); _msg.U8(0); _msg.Str(string.Empty); _msg.U8(0); continue; }
                if (IsNetBox(box))
                {
                    var ci = _clients.Values.FirstOrDefault(c => c.BoxSlot == i);
                    if (ci == null) { _msg.U8(255); _msg.U8(0); _msg.U8(0); _msg.Str(string.Empty); _msg.U8(0); continue; }
                    _msg.U8(ci.Id); _msg.U8((byte)ci.BoxStateValue); _msg.U8((byte)(ci.BoxTeam + 1)); _msg.Str(ci.BoxName ?? string.Empty); _msg.U8((byte)ci.BoxSymbol);
                    continue;
                }
                var signed = box.currentState == PlayerBox_V2.PlayerBoxState.SIGNEDIN && box.playerAccount != null;
                _msg.U8(0);
                _msg.U8((byte)box.currentState);
                _msg.U8((byte)(box.currentTeam + 1));
                _msg.Str(signed ? box.playerAccount.name : string.Empty);
                _msg.U8((byte)(signed ? box.playerAccount.symbol : 0));
            }
            var o = SelOptions;
            _msg.Bool(o.optionsOpened);
            _msg.U8((byte)o.numTeam0AIScroller.currentValue);
            _msg.U8((byte)o.numTeam1AIScroller.currentValue);
            _msg.U8((byte)o.team0AIDifficultyScroller.currentValue);
            _msg.U8((byte)o.team1AIDifficultyScroller.currentValue);
        }

        private void HostHandleBoxRequest(ClientInfo ci, int controller)
        {
            if (!InPlayerSelection || ci.BoxSlot >= 0) return;
            var mgr = SelMgr;
            mgr.SetCurrentPlayerBox();
            var slot = mgr.currentPlayerBox;
            if (slot >= Boxes) { NetLog.Info("No free player box for " + ci.Name); return; }
            AssignBox(ci, slot, false);
            NetLog.Info(ci.Name + " joined player box " + (slot + 1));
        }

        private void AssignBox(ClientInfo ci, int slot, bool restore)
        {
            ci.BoxSlot = slot;
            if (!restore)
            {
                ci.BoxStateValue = (int)PlayerBox_V2.PlayerBoxState.CHOOSEACCOUNT;
                ci.BoxTeam = -1;
            }
            MirrorBox(SelMgr.playerBoxes[slot], ci.Id, ci.BoxStateValue, ci.BoxTeam, ci.BoxName, ci.BoxSymbol);
            SelMgr.SetCurrentPlayerBox();
            Begin(Msg.BoxAssign);
            _msg.U8((byte)slot);
            _msg.Bool(restore);
            ci.Conn.SendReliable(_msg, Now);
        }

        private void HostHandleBoxState(ClientInfo ci, Reader r)
        {
            var slot = r.U8();
            var state = r.U8();
            var team = r.U8() - 1;
            var name = Clean(r.Str());
            var symbol = r.U8();
            if (slot != ci.BoxSlot) return;
            if (state == (int)PlayerBox_V2.PlayerBoxState.BLANK)
            {
                FreeBox(ci);
                return;
            }
            ci.BoxStateValue = state;
            ci.BoxTeam = team;
            ci.BoxName = name;
            ci.BoxSymbol = symbol;
        }

        private void FreeBox(ClientInfo ci)
        {
            if (ci.BoxSlot < 0) return;
            if (InPlayerSelection)
            {
                var box = SelMgr.playerBoxes[ci.BoxSlot];
                if (IsNetBox(box)) box.SignOut();
            }
            NetLog.Info(ci.Name + " left player box " + (ci.BoxSlot + 1));
            ci.BoxSlot = -1;
            ci.InLastMatch = false;
        }

        /// <summary>PlayerSelectionManager_V2.Start postfix (host): puts last match's online players back in boxes.</summary>
        internal void HostOnPlayerSelectionStart()
        {
            _tableSig = null;
            foreach (var ci in _clients.Values) ci.BoxSlot = -1;
            foreach (var ci in Clients.Where(c => c.InLastMatch).ToList())
            {
                SelMgr.SetCurrentPlayerBox();
                var slot = SelMgr.currentPlayerBox;
                if (slot >= Boxes) break;
                ci.BoxStateValue = (int)PlayerBox_V2.PlayerBoxState.SIGNEDIN;
                AssignBox(ci, slot, true);
            }
        }

        /// <summary>Host OnLevelWasLoaded: menu scenes are shared, so clients follow.</summary>
        private void HostOnSceneLoaded(string scene)
        {
            if (!LobbyScenes.Contains(scene)) return;
            WriteScene(scene);
            BroadcastReliable();
        }

        /// <summary>Scene name, plus the confirmed roster for arena select (its team list is drawn from it).</summary>
        private void WriteScene(string scene)
        {
            Begin(Msg.Scene);
            _msg.Str(scene);
            var players = scene == "GameSelection" ? PlayerSelectionManager_V2.players : null;
            var n = players == null ? 0 : Mathf.Min(players.Count, 16);
            _msg.U8(n);
            for (var i = 0; i < n; i++)
            {
                var p = players[i];
                _msg.U8(p.team);
                _msg.Bool(p.AI);
                _msg.Str(p.account != null ? p.account.name : "PLAYER");
                _msg.U8(p.account != null ? p.account.symbol : 0);
            }
        }

        /// <summary>A newly welcomed client goes to whichever menu the host is in.</summary>
        private void HostSendSceneTo(ClientInfo ci)
        {
            var scene = Application.loadedLevelName;
            if (!LobbyScenes.Contains(scene)) return;
            WriteScene(scene);
            ci.Conn.SendReliable(_msg, Now);
            _tableSig = null;
        }

        // ---------------------------------------------------------------- client

        private int _mySlot = -1;
        private bool _boxRequested;
        private int _myController;
        private Account _myAccount;
        private int _myTeam = -1;
        private string _myBoxSig;
        private bool _hasMirrorOptions;
        private bool _mirrorOptionsOpened;
        private readonly int[] _mirrorAi = new int[4];

        internal static bool ClientLobbyActive { get { return IsClient && Instance.Welcomed && !Match.Active; } }
        internal static bool MirroredOptionsOpened { get { return Instance != null && Instance._mirrorOptionsOpened; } }

        /// <summary>The controller this client joined player selection with (for its ball in the match).</summary>
        internal int MatchJoystick { get { return _mySlot >= 0 || _myAccount != null ? _myController : Plugin.LocalJoystick(); } }

        /// <summary>PlayerSelectionManager_V2.SignInPlayer prefix (client): ask the host for a box instead.</summary>
        internal bool ClientRequestBox(int controller)
        {
            if (_mySlot >= 0 || _boxRequested) return false; // one player per online client
            _boxRequested = true;
            _myController = controller;
            _myAccount = null;
            Begin(Msg.BoxReq);
            _msg.U8((byte)controller);
            _conns.Values.First().SendReliable(_msg, Now);
            return false;
        }

        private void ClientHandleBoxAssign(Reader r)
        {
            var slot = r.U8();
            var restore = r.Bool();
            _boxRequested = false;
            if (!InPlayerSelection) return;
            var box = SelMgr.playerBoxes[slot];
            _mySlot = slot;
            _myBoxSig = null;
            box.SignIn(_myController);
            if (restore && _myAccount != null)
            {
                box.playerAccount = _myAccount;
                box.ChangeStateTo(PlayerBox_V2.PlayerBoxState.SIGNEDIN);
                box.ChangeTeamsTo(_myTeam);
            }
            SelMgr.SetCurrentPlayerBox();
            NetLog.Info("Got player box " + (slot + 1) + (restore ? " (kept from last match)" : string.Empty));
        }

        private void ClientHandleBoxTable(Reader r)
        {
            var entries = new int[Boxes, 4];
            var names = new string[Boxes];
            for (var i = 0; i < Boxes; i++)
            {
                entries[i, 0] = r.U8();
                entries[i, 1] = r.U8();
                entries[i, 2] = r.U8() - 1;
                names[i] = r.Str();
                entries[i, 3] = r.U8();
            }
            _mirrorOptionsOpened = r.Bool();
            for (var i = 0; i < 4; i++) _mirrorAi[i] = r.U8();
            _hasMirrorOptions = true;
            if (!InPlayerSelection) return;

            var boxes = SelMgr.playerBoxes;
            for (var i = 0; i < Boxes && i < boxes.Length; i++)
            {
                if (i == _mySlot) continue;
                var owner = entries[i, 0] == 255 ? -1 : entries[i, 0];
                if (owner == _myId) owner = -1; // a box the host still lists as ours but we no longer own
                MirrorBox(boxes[i], owner, entries[i, 1], entries[i, 2], names[i], entries[i, 3]);
            }
            SelMgr.SetCurrentPlayerBox();
        }

        /// <summary>Called from ClientUpdate: report our own box to the host when it changes.</summary>
        private void LobbyClientUpdate()
        {
            if (Application.loadedLevelName == "GameSelection") { ClientApplyNav(); return; }
            if (!InPlayerSelection || _mySlot < 0) return;
            var box = SelMgr.playerBoxes[_mySlot];
            if (!box.boxActive || IsNetBox(box))
            {
                SendMyBox(PlayerBox_V2.PlayerBoxState.BLANK, -1, string.Empty, 0);
                _mySlot = -1;
                _myAccount = null;
                return;
            }
            var signed = box.currentState == PlayerBox_V2.PlayerBoxState.SIGNEDIN && box.playerAccount != null;
            if (signed) { _myAccount = box.playerAccount; _myTeam = box.currentTeam; }
            var name = signed ? box.playerAccount.name : string.Empty;
            var symbol = signed ? box.playerAccount.symbol : 0;
            var sig = (int)box.currentState + "|" + box.currentTeam + "|" + name + "|" + symbol;
            if (sig == _myBoxSig) return;
            _myBoxSig = sig;
            SendMyBox(box.currentState, box.currentTeam, name, symbol);
        }

        private void SendMyBox(PlayerBox_V2.PlayerBoxState state, int team, string name, int symbol)
        {
            Begin(Msg.BoxState);
            _msg.U8((byte)_mySlot);
            _msg.U8((byte)state);
            _msg.U8((byte)(team + 1));
            _msg.Str(name ?? string.Empty);
            _msg.U8((byte)symbol);
            _conns.Values.First().SendReliable(_msg, Now);
        }

        /// <summary>Options panel (AI bots, difficulty) belongs to the host; clients show the host's values.</summary>
        internal static void ClientApplyMirroredOptions(PlayerSelectionOptions_Manager o)
        {
            var s = Instance;
            if (s == null || !s._hasMirrorOptions) return;
            o.optionsOpened = s._mirrorOptionsOpened;
            SyncScroller(o.numTeam0AIScroller, s._mirrorAi[0]);
            SyncScroller(o.numTeam1AIScroller, s._mirrorAi[1]);
            SyncScroller(o.team0AIDifficultyScroller, s._mirrorAi[2]);
            SyncScroller(o.team1AIDifficultyScroller, s._mirrorAi[3]);
        }

        private void ClientHandleScene(Reader r)
        {
            var scene = r.Str();
            int n = r.U8();
            var roster = new List<PlayerData>();
            for (var i = 0; i < n; i++)
            {
                var team = r.U8();
                var ai = r.Bool();
                var name = r.Str();
                var symbol = r.U8();
                roster.Add(new PlayerData(ai, 0, ai ? -1 : 0, team)
                {
                    teamColor = team == 0 ? ColorManager.team1Color : ColorManager.team2Color,
                    account = new Account(name, ai, symbol)
                });
            }
            if (Match.Active || Match.ClientPending) return;
            if (scene == "GameSelection") PlayerSelectionManager_V2.players = roster;
            if (Application.loadedLevelName == scene) return;
            NetLog.Info("Following the host to " + scene);
            Application.LoadLevel(scene);
        }

        /// <summary>Client OnLevelWasLoaded.</summary>
        private void ClientOnSceneLoaded(string scene)
        {
            _mySlot = -1;
            _boxRequested = false;
            _hasMirrorOptions = false;
            _myBoxSig = null;
            _hasNav = false;
        }

        // ---------------------------------------------------------------- arena / mode select (GameSelection)
        // The host drives the game's MenuNavigator; clients show the same menu and highlighted item.
        // Objects are addressed by root name + child indices, which match on every machine for the
        // built-in arena list.

        private string _navSig;
        private float _navSentAt;
        private string _navMenu, _navElement;
        private int _navGoals;
        private float _navLength;
        private bool _hasNav;

        internal static bool ClientInGameSelection { get { return ClientLobbyActive && Application.loadedLevelName == "GameSelection"; } }

        private void HostSendNav()
        {
            var nav = MenuGlobals.menuManager != null ? MenuGlobals.menuManager.menuNavigator : null;
            if (nav == null || nav.selectedMenu == null || nav.selectedElement == null) return;
            var m = PathOf(nav.selectedMenu.transform);
            var e = PathOf(nav.selectedElement.transform);
            var sig = m + "|" + e + "|" + NumGoalsToWinManager.numGoalsToWin + "|" + GameTimerManager.gameLength;
            if (sig == _navSig && Now - _navSentAt < 2f) return;
            _navSig = sig;
            _navSentAt = Now;
            Begin(Msg.Nav);
            _msg.Str(m);
            _msg.Str(e);
            _msg.I32(NumGoalsToWinManager.numGoalsToWin);
            _msg.F32(GameTimerManager.gameLength);
            BroadcastReliable();
        }

        private void ClientApplyNav()
        {
            if (!_hasNav || MenuGlobals.menuManager == null) return;
            var nav = MenuGlobals.menuManager.menuNavigator;
            if (nav == null) return;

            if (NumGoalsToWinManager.numGoalsToWin != _navGoals || GameTimerManager.gameLength != _navLength)
            {
                NumGoalsToWinManager.numGoalsToWin = _navGoals;
                GameTimerManager.gameLength = _navLength;
                foreach (var g in UnityEngine.Object.FindObjectsOfType<NumGoalsToWinManager>()) g.RefreshTextMesh();
                foreach (var g in UnityEngine.Object.FindObjectsOfType<GameTimerManager>()) g.RefreshTextMesh();
            }

            var menuT = Resolve(_navMenu);
            var elT = Resolve(_navElement);
            var menu = menuT != null ? menuT.GetComponent<MenuList>() : null;
            var el = elT != null ? elT.GetComponent<ListElement>() : null;
            if (menu == null || el == null) return;
            if (menu.GetComponentInChildren<MenuLoadSceneAsync>(true) != null) return; // the host is starting the match

            if (nav.selectedMenu != menu)
            {
                var newChain = Chain(menu);
                foreach (var m in Chain(nav.selectedMenu))
                    if (!newChain.Contains(m)) m.gameObject.SetActive(false);
                for (var i = 0; i < newChain.Count; i++)
                {
                    if (!newChain[i].gameObject.activeSelf) newChain[i].gameObject.SetActive(true);
                    if (i + 1 < newChain.Count)
                    {
                        var opener = newChain[i].GetComponentsInChildren<ListElement>(true).FirstOrDefault(x => x.connectedMenu == newChain[i + 1]);
                        if (opener != null) newChain[i].selectedElement = opener;
                    }
                }
                menu.selectedElement = null;
                if (nav.selectedElement != null) nav.selectedElement.Deselect();
                nav.selectedMenu = menu;
                nav.selectedElement = el;
                el.Select();
                if (nav.bButtonGraphic != null) nav.bButtonGraphic.SetActive(menu.previousMenu != null);
                menu.gameObject.BroadcastMessage("InitiliazeMenuList", SendMessageOptions.DontRequireReceiver);
            }
            if (nav.selectedElement != el)
            {
                if (nav.selectedElement != null) nav.selectedElement.Deselect();
                nav.selectedElement = el;
                el.Select();
            }
        }

        private static List<MenuList> Chain(MenuList menu)
        {
            var chain = new List<MenuList>();
            for (var m = menu; m != null && chain.Count < 16; m = m.previousMenu) chain.Insert(0, m);
            return chain;
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            while (t.parent != null) { parts.Insert(0, t.GetSiblingIndex().ToString()); t = t.parent; }
            parts.Insert(0, t.name);
            return string.Join("/", parts.ToArray());
        }

        private static Transform Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Split('/');
            Transform t = null;
            foreach (var root in UnityEngine.Object.FindObjectsOfType<Transform>())
                if (root.parent == null && root.name == parts[0]) { t = root; break; }
            for (var i = 1; t != null && i < parts.Length; i++)
            {
                int idx;
                if (!int.TryParse(parts[i], out idx) || idx >= t.childCount) return null;
                t = t.GetChild(idx);
            }
            return t;
        }
    }
}
