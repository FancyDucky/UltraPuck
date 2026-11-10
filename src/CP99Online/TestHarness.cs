using System;
using System.Collections.Generic;
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
            NetLog.Info("TEST MODE: " + _role + " on port " + _port);
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

            if (_role == "host" && !_matchStarted && s.ClientCount > 0 && t > 25f && ProfileManager.Instance != null)
            {
                _matchStarted = true;
                StartTestMatch();
            }

            if (Match.Active && t > _nextLog)
            {
                _nextLog = t + 2f;
                LogWorld();
            }
        }

        private static void StartTestMatch()
        {
            var host = new PlayerData(false, 0, 0, 0) { teamColor = ColorManager.team1Color, account = new Account("HOSTPLAYER", false, 0) };
            var ai = new PlayerData(true, 0, -1, 1) { teamColor = ColorManager.team2Color, account = new Account("BOT", true, 0) };
            PlayerSelectionManager_V2.players = new List<PlayerData> { host, ai };
            ArenaManager.arenaName = "0001_Boring";
            ArenaManager.noSpikes = false;
            GameModeManager.gameMode = GameModeManager.GameMode.SCORE;
            NumGoalsToWinManager.numGoalsToWin = 3;
            ChallengeManager.isInChallenge = false;
            ChallengeManager.currentChallenge = new Challenge(Challenge.ChallengeType.SPARKLETEAM, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, Challenge.ScoreType.HIGHSCORE, new int[3], Challenge.ChallengeType.SPARKLETEAM);
            ChallengeMatch_Manager.challengeMatch = false;
            ArenaCreatorManager.comingFromArenaCreator = false;
            AIDifficultyManager.skill_shooting = new[] { 2, 2 };
            AIDifficultyManager.skill_aggression = new[] { 2, 2 };
            AIDifficultyManager.skill_agility = new[] { 2, 2 };
            AIDifficultyManager.skill_goaltending = new[] { 2, 2 };
            NetLog.Info("TEST: host starting match");
            Application.LoadLevel("Arena3D");
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
        }

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
            nav.MoveDownList(); nav.NextMenu();                  // Team toggle (stays open)
            yield return new WaitForSeconds(1f); Shot(dir, "m3_online_team"); yield return new WaitForSeconds(2f);
            NetSession.Instance.StartHostDirect(27199, "127.0.0.1");
            yield return new WaitForSeconds(1.5f); Shot(dir, "m4_online_host"); yield return new WaitForSeconds(2f);
            NetSession.Instance.Stop("test");
            yield return new WaitForSeconds(0.5f);
            nav.PreviousMenu();
            yield return new WaitForSeconds(1f);

            for (var i = 0; i < 6; i++) nav.MoveDownList();     // Mods
            yield return new WaitForSeconds(0.5f); Shot(dir, "m5_main_mods_selected"); yield return new WaitForSeconds(2f);
            nav.NextMenu();
            yield return new WaitForSeconds(1.5f); Shot(dir, "m6_mods"); yield return new WaitForSeconds(2f);
            nav.NextMenu();                                      // toggle No Boost Slowdown in place
            yield return new WaitForSeconds(1f); Shot(dir, "m7_boost_toggled"); yield return new WaitForSeconds(2f);
            nav.MoveDownList(); nav.NextMenu();                  // cycle FPS limit
            yield return new WaitForSeconds(1f); Shot(dir, "m8_fps_cycled"); yield return new WaitForSeconds(2f);
            nav.PreviousMenu(); yield return new WaitForSeconds(1.5f);
            Shot(dir, "m9_back_main"); yield return new WaitForSeconds(2f);
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
