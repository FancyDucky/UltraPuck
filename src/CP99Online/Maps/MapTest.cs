using System;
using System.Collections.Generic;
using System.IO;
using CP99Online.Net;
using UnityEngine;

namespace CP99Online.Maps
{
    /// <summary>
    /// "Test in game" from the map maker: it writes the map and a request file into "UltraPuck Maps/Testing"
    /// (and starts the game if it isn't running). This picks the request up, from the menus or a match, and goes
    /// straight into an endless practice match on that map, like the Arena Creator's own test button.
    ///
    /// request.txt, one setting per line:
    ///   map &lt;path to the .upmap&gt;
    ///   controls auto|keyboard|controller
    ///   bots &lt;0-3&gt;   n bots against you and n-1 with you (1: you against one bot; 2: two against two)
    /// </summary>
    internal sealed class MapTest : MonoBehaviour
    {
        internal static string Folder { get { return Path.Combine(MapLibrary.Folder, "Testing"); } }
        internal static string RequestFile { get { return Path.Combine(Folder, "request.txt"); } }

        // scenes where it's safe to leave for the arena (not the intro, loading or sign-in screens)
        private static readonly HashSet<string> Ready = new HashSet<string>
        {
            "MainMenu", "PlayerSelection", "GameSelection", "MatchOver", "Arena3D", "ArenaCreator", "ChallengeMenu",
        };

        private float _next;
        private bool _skippedIntro;

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            string request;
            try
            {
                if (!File.Exists(RequestFile)) return;
                request = File.ReadAllText(RequestFile);
            }
            catch (Exception) { return; } // being written: next time

            var scene = Application.loadedLevelName;
            if (NetSession.IsHost || NetSession.IsClient)
            {
                Consume();
                Toast.Show("Map test: leave the online game first, then press Test in game again.");
                return;
            }
            if (!Ready.Contains(scene) || ProfileManager.Instance == null)
            {
                // a fresh start for a test: skip the logo, like pressing a button there
                if (scene == "PHL_Tag" && !_skippedIntro) { _skippedIntro = true; Application.LoadLevel("LoadMainMenu"); }
                return;
            }
            Consume();
            try { Begin(request); }
            catch (Exception e)
            {
                NetLog.Warn("Map test failed: " + e);
                Toast.Show("Map test: couldn't load the map (" + e.Message + ")");
            }
        }

        private static void Consume()
        {
            try { File.Delete(RequestFile); } catch (Exception) { }
        }

        private static void Begin(string request)
        {
            string path = null, controls = "auto";
            var bots = 0;
            foreach (var raw in request.Split('\n'))
            {
                var line = raw.Trim();
                var sp = line.IndexOf(' ');
                if (sp < 0) continue;
                var key = line.Substring(0, sp); var value = line.Substring(sp + 1).Trim();
                if (key == "map") path = value;
                else if (key == "controls") controls = value;
                else if (key == "bots") int.TryParse(value, out bots);
            }
            if (string.IsNullOrEmpty(path)) throw new Exception("no map in the request");
            var map = MapLibrary.Load(path);
            bots = Math.Max(0, Math.Min(3, bots));

            ArenaCreatorManager.currentArena = MapLibrary.StandIn(map, 9998);
            ArenaCreatorManager.comingFromArenaCreator = false;
            ArenaManager.arenaName = "0000_Custom";
            ArenaManager.noSpikes = false;
            ColorManager.team1Color = TeamColor.RED;
            ColorManager.team2Color = TeamColor.BLUE;

            // keyboard (0) or any controller (-1); auto: a controller if one is plugged in
            int joystick;
            if (controls == "keyboard") joystick = 0;
            else if (controls == "controller") joystick = -1;
            else
            {
                joystick = 0;
                foreach (var n in Input.GetJoystickNames()) if (!string.IsNullOrEmpty(n) && n.Trim().Length > 0) joystick = -1;
            }
            var players = new List<PlayerData>();
            var me = new PlayerData(AI_: false, 0, joystick, 0) { teamColor = ColorManager.team1Color };
            me.account = new Account("Player", AI_: false, 0, 0, 0, 0, 0, 0, 0, 0f);
            players.Add(me);
            string[] names = { "FRANKLIN", "BROAD", "MARKET", "ERIE", "WAYNE", "WALNUT" };
            var nameAt = 0;
            for (var i = 0; i < bots - 1; i++) players.Add(Bot(0, names[nameAt++]));
            for (var i = 0; i < bots; i++) players.Add(Bot(1, names[nameAt++]));
            PlayerSelectionManager_V2.players = players;

            GameModeManager.gameMode = GameModeManager.GameMode.SCORE;
            NumGoalsToWinManager.numGoalsToWin = 99999999; // endless: leave from the pause menu
            ChallengeManager.challengeScore = 0;
            ChallengeMatch_Manager.challengeMatch = false;
            ChallengeManager.isInChallenge = false;
            ChallengeManager.currentChallenge = new Challenge(Challenge.ChallengeType.SPARKLETEAM, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, Challenge.ScoreType.HIGHSCORE, new int[0], Challenge.ChallengeType.SPARKLETEAM);
            if (AIDifficultyManager.skill_shooting == null || AIDifficultyManager.skill_shooting.Length < 2)
            {
                AIDifficultyManager.skill_shooting = new[] { 2, 2 };
                AIDifficultyManager.skill_aggression = new[] { 2, 2 };
                AIDifficultyManager.skill_agility = new[] { 2, 2 };
                AIDifficultyManager.skill_goaltending = new[] { 2, 2 };
            }

            Time.timeScale = 1f;
            NetLog.Info("Map test: \"" + map.Name + "\" from " + path + ", controls " + (joystick == 0 ? "keyboard" : "controller") + ", bots " + bots);
            Application.LoadLevel("Arena3D");
            Toast.Show("Testing \"" + map.Name + "\": pause to leave. Test in game again to reload it.");
        }

        private static PlayerData Bot(int team, string name)
        {
            var p = new PlayerData(AI_: true, 0, -1, team) { teamColor = team == 0 ? ColorManager.team1Color : ColorManager.team2Color };
            p.account = new Account(name, AI_: true, 0, 0, 0, 0, 0, 0, 0, 0f);
            return p;
        }
    }
}
