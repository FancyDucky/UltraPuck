using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CP99Online.Net;
using UnityEngine;

namespace CP99Online
{
    /// <summary>
    /// Hands-free local test of online play, enabled only by environment variables:
    ///   CP99_TEST=host    host on 127.0.0.1 and start a match as soon as a client joins
    ///   CP99_TEST=client  join 127.0.0.1 and drive the ball with synthetic input
    ///   CP99_TEST_PORT    UDP port (default 27099)
    /// Does nothing in normal play.
    /// </summary>
    internal class TestHarness : MonoBehaviour
    {
        private string _role;
        private int _port = 27099;
        private bool _connected, _matchStarted;
        private float _nextLog;

        internal static bool DriveInput;

        private void Start()
        {
            _role = Environment.GetEnvironmentVariable("CP99_TEST");
            if (string.IsNullOrEmpty(_role)) { enabled = false; return; }
            int p;
            if (int.TryParse(Environment.GetEnvironmentVariable("CP99_TEST_PORT"), out p)) _port = p;
            DriveInput = _role == "client";
            _rounds = Environment.GetEnvironmentVariable("CP99_ROUNDS");
            if (!string.IsNullOrEmpty(_rounds) && _role == "host")
            {
                UltraPuck.ModManager.EnableForSession(UltraPuck.RoundsMode.Instance); // this session only, not saved
                UltraPuck.RoundsMode.ForceTeamVote = _rounds == "team";
            }
            NetLog.Info("TEST MODE: " + _role + " on port " + _port + (string.IsNullOrEmpty(_rounds) ? string.Empty : " rounds=" + _rounds));
        }

        private void Update()
        {
            var s = NetSession.Instance;
            if (s == null) return;
            var t = Time.realtimeSinceStartup;

            if (!_connected && t > 6f)
            {
                _connected = true;
                if (_role == "host") s.StartHostDirect(_port, "127.0.0.1");
                else s.JoinDirect("127.0.0.1", _port);
            }

            LobbyStep(s, t);
            if (!string.IsNullOrEmpty(_rounds)) RoundsStep(t);

            if (Match.Active && t > _nextLog)
            {
                _nextLog = t + 2f;
                LogWorld();
            }
        }

        // ---- Rounds Mode: 3 goals to win so there are card picks; pick automatically after a few seconds
        private string _rounds;
        private bool _goalsSet;
        private int _phasesSeen;
        private bool _inPhase;

        private void RoundsStep(float t)
        {
            if (_role == "host" && Match.Active && !_goalsSet) { _goalsSet = true; NumGoalsToWinManager.numGoalsToWin = 3; NetLog.Info("TEST rounds: goals to win = 3"); }
            if (!Match.Active) { _goalsSet = false; }
            var cg = Rounds.CardGame.PhaseActive && !Rounds.CardGame.Revealing;
            if (cg && !_inPhase) { _inPhase = true; _phasesSeen++; NetLog.Info("TEST rounds: card screen " + _phasesSeen + " shown"); }
            if (!cg) { _inPhase = false; return; }
            var wait = _role == "host" ? 5f : 7f;
            if (Time.realtimeSinceStartup - Rounds.CardGame.PhaseStartedAt < wait) return;
            for (var i = 0; i < Rounds.CardGame.Seats.Count; i++)
            {
                var seat = Rounds.CardGame.Seats[i];
                if (!Rounds.CardGame.IsLocal(seat) || seat.Choice >= 0 || seat.Offer.Length == 0) continue;
                var pick = _phasesSeen % seat.Offer.Length;
                NetLog.Info("TEST rounds: " + seat.Name + " picks " + seat.Offer[pick]);
                if (_role == "host") Rounds.CardGame.Pick(i, pick, 0);
                else { Rounds.CardGame.PickLocal(i, pick); NetSession.Instance.ClientSendCardPick(i, pick); }
            }
        }

        // ---- shared player selection walkthrough
        private int _step;
        private float _stepAt;

        private void Next(float t, string what) { _step++; _stepAt = t; NetLog.Info("TEST step " + _step + ": " + what); }

        private void LobbyStep(NetSession s, float t)
        {
            var scene = Application.loadedLevelName;
            if (_role == "host")
            {
                if (_step == 0 && s.ClientCount > 0 && scene == "MainMenu" && t > 25f && ProfileManager.Instance != null)
                {
                    Application.LoadLevel("PlayerSelection");
                    Next(t, "host opens player selection");
                }
                else if (_step == 1 && NetSession.InPlayerSelection && t - _stepAt > 4f)
                {
                    SignInLocal(MenuGlobals.menuManager.playerSelectionManager_V2, 0, "HOSTPLAYER", 1, 0);
                    Next(t, "host player signed in on team 1");
                }
                else if (_step == 2 && t - _stepAt > 1f && s.Clients.Any(c => c.BoxSlot >= 0 && c.BoxTeam == 1 &&
                         c.BoxStateValue == (int)PlayerBox_V2.PlayerBoxState.SIGNEDIN))
                {
                    var o = MenuGlobals.menuManager.playerSelectionOptions_Manager;
                    o.numTeam0AIScroller.Scroll(1);
                    Next(t, "client is on team 2; host adds a bot to team 1 (screenshot time)");
                }
                else if (_step == 3 && t - _stepAt > 6f)
                {
                    MenuGlobals.menuManager.playerSelectionOptions_Manager.optionsOpened = true;
                    Next(t, "host opens the options panel");
                }
                else if (_step == 4 && t - _stepAt > 3f)
                {
                    var o = MenuGlobals.menuManager.playerSelectionOptions_Manager;
                    o.ConfirmPlayers();
                    NetLog.Info("TEST players: " + string.Join(", ", PlayerSelectionManager_V2.players.Select(p =>
                        p.account.name + "/team" + p.team + "/joy" + p.joystickNum + "/" + p.joystickId).ToArray()));
                    o.GoToLevelSelection();
                    Next(t, "host confirms players -> GameSelection");
                }
                else if (_step == 5 && scene == "GameSelection" && t - _stepAt > 5f)
                {
                    NumGoalsToWinManager.numGoalsToWin = 3;
                    GameTimerManager.gameLength = 60f;
                    var nav = MenuGlobals.menuManager.menuNavigator;
                    nav.MoveDownList(); nav.MoveDownList();
                    Next(t, "host highlights arena 3 (" + NavText() + "), goals 3, 1:00");
                }
                else if (_step >= 6 && _step < 25 && scene == "GameSelection" && t - _stepAt > 4f)
                {
                    var nav = MenuGlobals.menuManager.menuNavigator;
                    if (nav.selectedMenu != null && nav.selectedMenu.name == "GameMode" && GameModeManager.gameMode != GameModeManager.GameMode.SCORE)
                    {
                        nav.MoveDownList();
                        Next(t, "host moves to " + NavText() + " mode=" + GameModeManager.gameMode);
                        return;
                    }
                    nav.NextMenu();
                    Next(t, "host presses A -> " + NavText());
                }
                else if (_step >= 6 && _step < 30 && scene == "MatchOver" && t - _stepAt > 8f)
                {
                    _step = 30;
                    Application.LoadLevel("PlayerSelection");
                    Next(t, "host leaves results for player selection");
                }
                else if (_step == 31 && NetSession.InPlayerSelection && t - _stepAt > 3f)
                {
                    NetLog.Info("TEST host boxes after match: " + Boxes());
                    Next(t, "checked boxes");
                }
            }
            else
            {
                if (_step == 0 && NetSession.InPlayerSelection)
                    Next(t, "client followed the host to player selection");
                else if (_step == 1 && t - _stepAt > 6f)
                {
                    MenuGlobals.menuManager.playerSelectionManager_V2.SignInPlayer(0); // goes to the host as a box request
                    Next(t, "client presses join");
                }
                else if (_step == 2)
                {
                    var mgr = MenuGlobals.menuManager.playerSelectionManager_V2;
                    var mine = mgr.playerBoxes.FirstOrDefault(b => b.boxActive && !NetSession.IsNetBox(b));
                    if (mine != null && t - _stepAt > 1f)
                    {
                        mine.playerAccount = new Account("CLIENTPLAYER", false, 3);
                        mine.ChangeStateTo(PlayerBox_V2.PlayerBoxState.SIGNEDIN);
                        mine.ChangeTeamsTo(1);
                        Next(t, "client got box " + (Array.IndexOf(mgr.playerBoxes, mine) + 1) + ", picked a profile and team 2");
                    }
                }
                else if (_step == 3 && scene == "GameSelection")
                    Next(t, "client followed the host to GameSelection");
                else if (_step == 4 && scene == "GameSelection" && t - _stepAt > 2f)
                {
                    _stepAt = t;
                    NetLog.Info("TEST client shows " + NavText() + " goals=" + NumGoalsToWinManager.numGoalsToWin + " length=" + GameTimerManager.gameLength);
                }
                else if (_step == 4 && scene == "PlayerSelection" && NetSession.InPlayerSelection)
                    Next(t, "client back in player selection");
                else if (_step == 5 && t - _stepAt > 5f)
                {
                    NetLog.Info("TEST client boxes after match: " + Boxes());
                    Next(t, "checked boxes");
                }
            }
        }

        private static string NavText()
        {
            var nav = MenuGlobals.menuManager.menuNavigator;
            var tm = nav.selectedElement != null ? nav.selectedElement.GetComponent<TextMesh>() : null;
            return (nav.selectedMenu != null ? nav.selectedMenu.name : "-") + " / \"" + (tm != null ? tm.text : "?") + "\"";
        }

        private static string Boxes()
        {
            return string.Join(" | ", MenuGlobals.menuManager.playerSelectionManager_V2.playerBoxes.Where(b => b.boxActive).Select(b =>
                (b.playerAccount != null ? b.playerAccount.name : "?") + " team" + b.currentTeam + " " + b.currentState + (NetSession.IsNetBox(b) ? " (remote)" : " (local)")).ToArray());
        }

        private static void SignInLocal(PlayerSelectionManager_V2 mgr, int controller, string name, int symbol, int team)
        {
            mgr.SignInPlayer(controller);
            var box = mgr.playerBoxes.First(b => b.boxActive && b.controllerNumber == controller);
            box.playerAccount = new Account(name, false, symbol);
            box.ChangeStateTo(PlayerBox_V2.PlayerBoxState.SIGNEDIN);
            box.ChangeTeamsTo(team);
        }

        private static void SetupTestArena()
        {
            ArenaManager.arenaName = "0001_Boring";
            ArenaManager.noSpikes = false;
            GameModeManager.gameMode = GameModeManager.GameMode.SCORE;
            NumGoalsToWinManager.numGoalsToWin = 3;
            ChallengeManager.isInChallenge = false;
            ChallengeMatch_Manager.challengeMatch = false;
            ArenaCreatorManager.comingFromArenaCreator = false;
            AIDifficultyManager.skill_shooting = new[] { 2, 2 };
            AIDifficultyManager.skill_aggression = new[] { 2, 2 };
            AIDifficultyManager.skill_agility = new[] { 2, 2 };
            AIDifficultyManager.skill_goaltending = new[] { 2, 2 };
        }

        private static void LogWorld()
        {
            var gm = Globals.gameManager;
            if (gm == null) return;
            var sb = new StringBuilder("WORLD score=" + gm.teams[0].score + "-" + gm.teams[1].score +
                " countdown=" + gm.startCountdown.ToString("F1") + " puck=" + Fmt(gm.puck.transform.position));
            for (var i = 0; i < Match.Balls.Length; i++)
            {
                var b = Match.Balls[i];
                sb.Append(" | s" + i + " " + b.playerData.account.name + " " + Fmt(b.transform.position) + (b.alive ? "" : " DEAD"));
            }
            NetLog.Info(sb.ToString());
            if (_diagCount++ < 40)
            {
                var d = new StringBuilder("DIAG fixedDeltaTime=" + Time.fixedDeltaTime + " maxDelta=" + Time.maximumDeltaTime +
                    " targetFps=" + Application.targetFrameRate + " vSync=" + QualitySettings.vSyncCount +
                    " replaying=" + (gm.replayManager != null && gm.replayManager.replaying) +
                    " puckInterp=" + gm.puck.GetComponent<Rigidbody2D>().interpolation);
                foreach (var b in Match.Balls)
                {
                    var rb = b.GetComponent<Rigidbody2D>();
                    d.Append(" | " + b.playerData.account.name + " interp=" + (rb != null ? rb.interpolation.ToString() : "none") +
                        " kinematic=" + (rb != null && rb.isKinematic));
                }
                var cam = gm.gameCam != null ? gm.gameCam.GetComponent<ArenaCamera>() : null;
                d.Append(" | camera=" + (cam != null ? "ArenaCamera follow=" + cam.followSpeed + " target=" + (cam.trackingObject ? cam.trackingObject.name : "-") : "other"));
                NetLog.Info(d.ToString());
            }
        }

        private static int _diagCount;

        private static string Fmt(Vector3 v) { return "(" + v.x.ToString("F2") + "," + v.y.ToString("F2") + ")"; }
    }

    /// <summary>
    /// Test instances are started without Steam, and the game's save system needs Steam, so
    /// AccountManager.SaveAccounts throws at match end. Skip saving only in test mode.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(AccountManager), nameof(AccountManager.SaveAccounts))]
    internal static class TestSkipSavePatch
    {
        private static readonly bool InTest = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CP99_TEST"));
        private static bool Prefix() { return !InTest; }
    }
}

namespace CP99Online
{
    /// <summary>
    /// Hands-free walk through the in-game menu entries (env CP99_MENUTEST=1, screenshots to CP99_SHOTDIR).
    /// Drives the game's own MenuNavigator from code: no key presses. Does nothing in normal play.
    /// </summary>
    internal class MenuTest : MonoBehaviour
    {
        private static readonly bool Enabled = Environment.GetEnvironmentVariable("CP99_MENUTEST") == "1";
        private bool _started;

        private void Update()
        {
            if (!Enabled || _started || Application.loadedLevelName != "MainMenu" || Time.realtimeSinceStartup < 14f) return;
            if (MenuGlobals.menuManager == null) return;
            _started = true;
            StartCoroutine(Run());
        }

        private static System.Collections.IEnumerator Run()
        {
            var dir = Environment.GetEnvironmentVariable("CP99_SHOTDIR") ?? ".";
            var nav = MenuGlobals.menuManager.menuNavigator;
            var ss = FindObjectOfType<StartScreen>();
            if (ss != null) ss.SendMessage("SkipLocalization");
            yield return new WaitForSeconds(2f);
            Shot(dir, "m1_main"); yield return new WaitForSeconds(2f);

            nav.MoveDownList(); nav.NextMenu();                  // Play Online
            yield return new WaitForSeconds(1.5f); Shot(dir, "m2_online_idle"); yield return new WaitForSeconds(2f);
            NetSession.Instance.StartHostDirect(27199, "127.0.0.1");
            yield return new WaitForSeconds(1.5f); Shot(dir, "m4_online_host"); yield return new WaitForSeconds(2f);
            NetSession.Instance.Stop("test");
            yield return new WaitForSeconds(0.5f);
            nav.PreviousMenu();
            yield return new WaitForSeconds(1f);

            for (var i = 0; i < 6; i++) nav.MoveDownList();     // Mods
            yield return new WaitForSeconds(0.5f); Shot(dir, "m5_main_mods_selected"); yield return new WaitForSeconds(2f);
            var fpsBefore = Plugin.FrameRateLimit.Value;
            var overlay = UltraPuck.ModManager.Get("example.statsoverlay");
            var overlayBefore = overlay != null && overlay.Enabled;
            nav.NextMenu();
            yield return new WaitForSeconds(1.5f); Shot(dir, "m6_mods_list"); yield return new WaitForSeconds(2f);
            nav.NextMenu();                                      // open FPS+ page
            yield return new WaitForSeconds(1.5f); Shot(dir, "m7_fps_page"); yield return new WaitForSeconds(2f);
            nav.MoveDownList(); nav.NextMenu();                  // cycle FPS limit
            yield return new WaitForSeconds(1f); Shot(dir, "m8_fps_cycled"); yield return new WaitForSeconds(2f);
            nav.MoveUpList(); nav.NextMenu();                    // FPS+ off
            yield return new WaitForSeconds(1f); Shot(dir, "m9_fps_off"); NetLog.Info("MENUTEST targetFrameRate=" + Application.targetFrameRate); yield return new WaitForSeconds(2f);
            nav.NextMenu();                                      // FPS+ back on
            yield return new WaitForSeconds(1f);
            nav.PreviousMenu();                                  // B: back to the list, not the main menu
            yield return new WaitForSeconds(1.5f); Shot(dir, "m10_back_to_list"); yield return new WaitForSeconds(2f);
            nav.MoveDownList(); nav.MoveDownList(); nav.NextMenu(); // third mod's page
            yield return new WaitForSeconds(1.5f); Shot(dir, "m11_third_page"); yield return new WaitForSeconds(2f);
            nav.NextMenu();                                      // toggle it
            yield return new WaitForSeconds(1.5f); Shot(dir, "m12_third_toggled"); yield return new WaitForSeconds(2f);
            nav.PreviousMenu(); yield return new WaitForSeconds(1f);
            nav.PreviousMenu(); yield return new WaitForSeconds(1.5f);
            Shot(dir, "m13_back_main"); yield return new WaitForSeconds(2f);
            Plugin.FrameRateLimit.Value = fpsBefore;
            if (overlay != null) UltraPuck.ModManager.SetEnabled(overlay, overlayBefore);
            NetLog.Info("MENUTEST done");
        }

        private static string Path(Transform t) { return t.parent == null ? t.name : Path(t.parent) + "/" + t.name; }

        private static void Shot(string dir, string name)
        {
            var nav = MenuGlobals.menuManager.menuNavigator;
            NetLog.Info("MENUTEST " + name + " menu=" + (nav.selectedMenu ? nav.selectedMenu.name : "-") +
                " sel=" + (nav.selectedElement ? nav.selectedElement.GetComponent<TextMesh>().text : "-"));
            Application.CaptureScreenshot(System.IO.Path.Combine(dir, name + ".png"));
        }
    }
}
