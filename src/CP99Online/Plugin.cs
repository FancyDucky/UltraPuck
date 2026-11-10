using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using CP99Online.Net;
using HarmonyLib;
using UnityEngine;

namespace CP99Online
{
    public enum ControllerChoice { KeyboardMouse, Gamepad }

    [BepInPlugin(Guid, "CP99 Online", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "cp99.online";
        public const string Version = "0.3.0";

        internal static ManualLogSource Logger_;
        internal static ConfigEntry<bool> NoBoostSlowdown;
        internal static ConfigEntry<KeyCode> MenuKey;
        internal static ConfigEntry<bool> OpenMenuOnStart;
        internal static ConfigEntry<int> FrameRateLimit;
        internal static ConfigEntry<string> PlayerName;
        internal static ConfigEntry<int> OnlineTeam;
        internal static ConfigEntry<int> Port;
        internal static ConfigEntry<string> LastAddress;
        internal static ConfigEntry<ControllerChoice> OnlineController;
        internal static ConfigEntry<bool> DeckDefaultsApplied;

        /// <summary>Steam sets SteamDeck=1 on the Deck, and Proton passes it through to the game.</summary>
        internal static readonly bool IsSteamDeck =
            System.Environment.GetEnvironmentVariable("SteamDeck") == "1" ||
            System.Environment.GetEnvironmentVariable("CP99_FAKE_DECK") == "1";

        private void Awake()
        {
            Logger_ = Logger;

            NoBoostSlowdown = Config.Bind("Mechanics", "NoBoostSlowdown", true,
                "Remove the temporary loss of drive after using the boost/shove.");
            MenuKey = Config.Bind("General", "MenuKey", KeyCode.F8,
                "Key that opens and closes the mod menu.");
            FrameRateLimit = Config.Bind("Display", "FrameRateLimit", 0,
                "Maximum frames per second. 0 = unlimited (the game normally caps at 60). " +
                "If FPS still stops at your monitor's refresh rate, turn off VSync in your GPU settings.");
            FrameRateLimit.SettingChanged += (s, e) => Patches.FrameRate.Apply();
            PlayerName = Config.Bind("Online", "PlayerName", string.Empty,
                "Name shown to other players. Blank = your Steam name.");
            OnlineTeam = Config.Bind("Online", "Team", 1, "Preferred team when joining (0 = team 1, 1 = team 2).");
            Port = Config.Bind("Online", "Port", 27099, "UDP port for direct-IP hosting/joining.");
            LastAddress = Config.Bind("Online", "LastAddress", "127.0.0.1", "Last address joined by direct IP.");
            OnlineController = Config.Bind("Online", "Controller", ControllerChoice.KeyboardMouse,
                "What you play with when you join someone else's game.");
            OpenMenuOnStart = Config.Bind("Debug", "OpenMenuOnStart", false,
                "Open the mod menu as soon as the game starts (used for testing).");
            DeckDefaultsApplied = Config.Bind("General", "SteamDeckDefaultsApplied", false,
                "Set automatically the first time the mod runs on a Steam Deck (switches online controls to Gamepad).");

            if (IsSteamDeck && !DeckDefaultsApplied.Value)
            {
                OnlineController.Value = ControllerChoice.Gamepad;
                DeckDefaultsApplied.Value = true;
                Logger.LogInfo("Steam Deck detected: online controls set to Gamepad");
            }

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);

            var patched = 0;
            foreach (var method in harmony.GetPatchedMethods())
            {
                patched++;
                Logger.LogDebug("Patched " + method.DeclaringType.Name + "." + method.Name);
            }
            Logger.LogInfo("CP99 Online " + Version + " loaded. Patches=" + patched + " NoBoostSlowdown=" + NoBoostSlowdown.Value);
        }

        // BepInEx runs Awake very early on this Unity version; touching Application or adding
        // components there crashed the player. Start runs after engine init.
        private void Start()
        {
            // Online play: the host must keep simulating while alt-tabbed.
            Application.runInBackground = true;
            Patches.FrameRate.Apply();
            NetLog.Init(Path.GetDirectoryName(Info.Location));
            gameObject.AddComponent<NetSession>();
            gameObject.AddComponent<ModMenu>();
            gameObject.AddComponent<TestHarness>();
            SceneDump.Dir = Path.GetDirectoryName(Info.Location);
            gameObject.AddComponent<SceneDump>();
            gameObject.AddComponent<MenuTest>();
            Logger.LogInfo("Mod menu ready (" + MenuKey.Value + " or hold the gamepad View/Back button)" +
                (IsSteamDeck ? " - Steam Deck" : string.Empty));
        }

        private void Update()
        {
            // Give the game's own SteamManager time to start before touching Steam.
            if (Time.realtimeSinceStartup > 8f) SteamLobby.Tick();
            if (Application.loadedLevelName == "MainMenu") SteamLobby.TryPendingJoin();
        }

        internal static int LocalJoystick()
        {
            // 0 = keyboard + mouse names, -1 = "any gamepad" names (see GameManager.SpawnPlayers).
            return OnlineController.Value == ControllerChoice.Gamepad ? -1 : 0;
        }
    }
}
