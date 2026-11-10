using System.Linq;
using CP99Online.Net;
using UnityEngine;

namespace CP99Online
{
    /// <summary>In-game IMGUI overlay. Toggled with Plugin.MenuKey (F8 by default).</summary>
    internal class ModMenu : MonoBehaviour
    {
        private const int WindowId = 0x43503939;
        private static readonly int[] FpsOptions = { 60, 120, 144, 240, 0 };
        private static readonly string[] Tabs = { "Mods", "Online" };

        private static ModMenu _instance;
        internal static bool IsOpen { get { return _instance != null && _instance._open; } }

        /// <summary>Open the overlay on a tab (0 = Mods, 1 = Online). Used by the in-game menu entries.</summary>
        internal static void Show(int tab)
        {
            if (_instance == null) return;
            _instance._tab = tab;
            _instance.SetOpen(true);
        }

        private void Awake() { _instance = this; }

        private bool _open;
        private bool _cursorVisibleBefore;
        private int _tab;
        private Rect _window = new Rect(40f, 40f, 440f, 10f);
        private GUIStyle _windowStyle, _header, _dim;
        private string _portText;
        private Vector2 _scroll;

        private void Start()
        {
            _portText = Plugin.Port.Value.ToString();
            if (Plugin.OpenMenuOnStart.Value) SetOpen(true);
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("CP99_TEST")))
            {
                _tab = 1; // local test runs: show the Online tab for screenshots
                SetOpen(true);
            }
        }

        private const float HoldToOpen = 0.8f;
        private float _backHeld;
        private bool _backFired;

        private void Update()
        {
            if (Input.GetKeyDown(Plugin.MenuKey.Value)) SetOpen(!_open);

            // Gamepad (Steam Deck has no F8): hold View/Back to open or close the menu.
            if (BackHeld())
            {
                _backHeld += Time.unscaledDeltaTime;
                if (!_backFired && _backHeld >= HoldToOpen) { _backFired = true; SetOpen(!_open); }
            }
            else { _backHeld = 0f; _backFired = false; }
        }

        private static bool _backAxisMissing;

        private static bool BackHeld()
        {
            if (_backAxisMissing) return false;
            try { return Input.GetButton("Back_ALL"); }
            catch (System.ArgumentException) { _backAxisMissing = true; return false; }
        }

        private void SetOpen(bool open)
        {
            if (open == _open) return;
            _open = open;
            if (open) _cursorVisibleBefore = Cursor.visible;
            else Cursor.visible = _cursorVisibleBefore;
        }

        private void LateUpdate()
        {
            // The game hides the cursor in several scenes, so keep reasserting it while open.
            if (_open) Cursor.visible = true;
        }

        private void EnsureStyles()
        {
            if (_windowStyle != null) return;
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, new Color(0.11f, 0.12f, 0.15f, 0.97f));
            tex.Apply();
            _windowStyle = new GUIStyle(GUI.skin.window) { padding = new RectOffset(12, 12, 26, 12) };
            _windowStyle.normal.background = tex;
            _windowStyle.onNormal.background = tex;
            _header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _dim = new GUIStyle(GUI.skin.label) { wordWrap = true };
            _dim.normal.textColor = new Color(0.75f, 0.78f, 0.82f);
        }

        private void OnGUI()
        {
            if (!_open) return;
            EnsureStyles();
            // Steam Deck: small 1280x800 screen used by touch, so draw everything bigger.
            var scale = Plugin.IsSteamDeck ? Mathf.Max(1f, Screen.height / 560f) : 1f;
            var oldMatrix = GUI.matrix;
            if (scale > 1f) GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            _window.height = 10f; // let GUILayout size it to the content
            _window = GUILayout.Window(WindowId, _window, DrawWindow, "CP99 Online " + Plugin.Version, _windowStyle);
            GUI.matrix = oldMatrix;
        }

        private void DrawWindow(int id)
        {
            _tab = GUILayout.Toolbar(_tab, Tabs);
            GUILayout.Space(6f);
            if (_tab == 0) DrawMods(); else DrawOnline();

            GUILayout.Space(8f);
            if (GUILayout.Button("Close (" + Plugin.MenuKey.Value + " or hold View/Back)")) SetOpen(false);
            if (Plugin.IsSteamDeck) GUILayout.Label("Steam Deck: tap the screen to use this menu. STEAM + X opens the keyboard.", _dim);
            GUI.DragWindow();
        }

        // ------------------------------------------------------------------ Mods tab

        private void DrawMods()
        {
            GUILayout.Label("Mechanics", _header);
            Plugin.NoBoostSlowdown.Value = GUILayout.Toggle(Plugin.NoBoostSlowdown.Value, " No slowdown after boost");
            if (NetSession.IsClient)
                GUILayout.Label("In someone else's game the host's mechanics settings apply.", _dim);

            GUILayout.Space(8f);
            GUILayout.Label("Updates", _header);
            Plugin.AutoUpdate.Value = GUILayout.Toggle(Plugin.AutoUpdate.Value, " Update automatically from GitHub");
            GUILayout.Label(Updater.Status, _dim);

            GUILayout.Space(8f);
            var limit = Plugin.FrameRateLimit.Value;
            GUILayout.Label("FPS limit: " + (limit > 0 ? limit.ToString() : "Unlimited"), _header);
            GUILayout.BeginHorizontal();
            foreach (var option in FpsOptions)
            {
                var label = option > 0 ? option.ToString() : "Off";
                if (GUILayout.Toggle(limit == option, label, GUI.skin.button) && limit != option)
                    Plugin.FrameRateLimit.Value = option;
            }
            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------------ Online tab

        private void DrawOnline()
        {
            var s = NetSession.Instance;
            if (s == null) return;

            GUILayout.Label(s.Status, _dim);
            GUILayout.Space(4f);

            if (s.Role == Role.None) DrawProfile(true);

            switch (s.Role)
            {
                case Role.None: DrawIdle(s); break;
                case Role.Host: DrawHost(s); break;
                case Role.Client: DrawClient(s); break;
            }
        }

        private void DrawProfile(bool editable)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(70f));
            GUI.enabled = editable;
            Plugin.PlayerName.Value = GUILayout.TextField(Plugin.PlayerName.Value, 16);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (string.IsNullOrEmpty(Plugin.PlayerName.Value))
                GUILayout.Label("Blank = your Steam name", _dim);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Controls", GUILayout.Width(70f));
            var gamepad = Plugin.OnlineController.Value == ControllerChoice.Gamepad;
            if (GUILayout.Toggle(!gamepad, "Keyboard + mouse", GUI.skin.button)) Plugin.OnlineController.Value = ControllerChoice.KeyboardMouse;
            if (GUILayout.Toggle(gamepad, "Gamepad", GUI.skin.button)) Plugin.OnlineController.Value = ControllerChoice.Gamepad;
            GUILayout.EndHorizontal();
            GUILayout.Label("Used when you join a game. As host, pick controls in the normal player selection.", _dim);
            GUILayout.Space(6f);
        }

        private void DrawIdle(NetSession s)
        {
            var steam = SteamLobby.Available;

            GUILayout.Label("Host a game", _header);
            GUILayout.BeginHorizontal();
            GUI.enabled = steam;
            if (GUILayout.Button("Host with Steam")) s.StartHostSteam();
            GUI.enabled = true;
            if (GUILayout.Button("Host direct IP")) { SavePort(); s.StartHostDirect(Plugin.Port.Value, null); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("UDP port", GUILayout.Width(70f));
            _portText = GUILayout.TextField(_portText, 5, GUILayout.Width(70f));
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label("Join a game", _header);
            DrawTeamPicker(s);
            GUILayout.BeginHorizontal();
            Plugin.LastAddress.Value = GUILayout.TextField(Plugin.LastAddress.Value, 64);
            if (GUILayout.Button("Join IP", GUILayout.Width(80f)))
            {
                SavePort();
                s.JoinDirect(Plugin.LastAddress.Value.Trim(), Plugin.Port.Value);
            }
            GUILayout.EndHorizontal();

            GUI.enabled = steam;
            if (GUILayout.Button(steam ? "Find friends' Steam games" : "Steam games (launch through Steam)")) SteamLobby.Refresh();
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(SteamLobby.BrowserStatus)) GUILayout.Label(SteamLobby.BrowserStatus, _dim);
            if (SteamLobby.Lobbies.Count > 0)
            {
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Mathf.Min(140f, 32f * SteamLobby.Lobbies.Count + 8f)));
                foreach (var l in SteamLobby.Lobbies.ToList())
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label((string.IsNullOrEmpty(l.HostName) ? "Unknown host" : l.HostName) + "  (" + l.Members + " in lobby)");
                    if (GUILayout.Button("Join", GUILayout.Width(60f))) SteamLobby.Join(l.Lobby);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }
            GUILayout.Label("Friends can also join through the Steam overlay or friends list once you host with Steam.", _dim);
        }

        private void DrawHost(NetSession s)
        {
            GUILayout.Label("Online players (" + s.ClientCount + "/" + Proto.MaxClients + ")", _header);
            if (s.ClientCount == 0) GUILayout.Label("Nobody has joined yet.", _dim);
            foreach (var c in s.Clients.ToList())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(c.Name, GUILayout.Width(150f));
                if (GUILayout.Toggle(c.Team == 0, "Team 1", GUI.skin.button) && c.Team != 0) s.SetClientTeam(c.Id, 0);
                if (GUILayout.Toggle(c.Team == 1, "Team 2", GUI.skin.button) && c.Team != 1) s.SetClientTeam(c.Id, 1);
                if (GUILayout.Button("Kick", GUILayout.Width(50f))) s.Kick(c.Id);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6f);
            if (s.TransportName == "Steam")
            {
                if (GUILayout.Button("Invite Steam friends")) SteamLobby.InviteFriends();
            }
            else
            {
                GUILayout.Label("Friends join with your public IP and UDP port " + Plugin.Port.Value +
                    ". Forward that port on your router, or use Steam hosting instead.", _dim);
            }
            if (s.WaitingForClients) GUILayout.Label("Waiting for everyone to load the arena...", _dim);
            GUILayout.Label("Start a match the normal way (Play, pick your local players, choose an arena). " +
                "Online players are added to their teams automatically.", _dim);
            if (GUILayout.Button("Stop hosting")) s.Stop("Stopped hosting");
        }

        private void DrawClient(NetSession s)
        {
            if (s.Welcomed)
            {
                GUILayout.Label("Lobby", _header);
                foreach (var e in s.Lobby)
                    GUILayout.Label(e.Name + (e.Team == 255 ? string.Empty : "  - Team " + (e.Team + 1)));
                GUILayout.Space(4f);
                DrawTeamPicker(s);
            }
            if (GUILayout.Button("Leave game")) s.Stop("You left");
        }

        private static void DrawTeamPicker(NetSession s)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Team", GUILayout.Width(70f));
            var team = Plugin.OnlineTeam.Value;
            if (GUILayout.Toggle(team == 0, "Team 1", GUI.skin.button) && team != 0) s.RequestTeam(0);
            if (GUILayout.Toggle(team == 1, "Team 2", GUI.skin.button) && team != 1) s.RequestTeam(1);
            GUILayout.EndHorizontal();
        }

        private void SavePort()
        {
            int p;
            if (int.TryParse(_portText, out p) && p > 0 && p < 65536) Plugin.Port.Value = p;
            _portText = Plugin.Port.Value.ToString();
        }
    }
}
