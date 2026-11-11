using System.Linq;
using CP99Online.Net;
using UnityEngine;

namespace CP99Online
{
    /// <summary>In-game IMGUI overlay. Toggled with Plugin.MenuKey (F8 by default).</summary>
    internal class ModMenu : MonoBehaviour
    {
        private const int WindowId = 0x43503939;
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
            _window = GUILayout.Window(WindowId, _window, DrawWindow, "UltraPuck " + Plugin.Version, _windowStyle);
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
            GUILayout.Label("Mods", _header);
            _modScroll = GUILayout.BeginScrollView(_modScroll, GUILayout.Height(Mathf.Min(320f, 60f + 70f * UltraPuck.ModManager.All.Length)));
            foreach (var m in UltraPuck.ModManager.All)
            {
                GUILayout.BeginHorizontal();
                var on = GUILayout.Toggle(m.Enabled, " " + m.Name, GUILayout.Width(200f));
                if (on != m.Enabled) UltraPuck.ModManager.SetEnabled(m, on);
                GUILayout.Label((string.IsNullOrEmpty(m.Author) ? string.Empty : m.Author + "  ") + "v" + m.Version, _dim);
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(m.Description)) GUILayout.Label(m.Description, _dim);
                if (m.Error != null) GUILayout.Label("Turned off after an error: " + m.Error, _dim);
                foreach (var st in m.Settings)
                {
                    if (st.Page != null)
                    {
                        GUILayout.Label("   " + st.Label, _dim);
                        foreach (var sub in st.Page() ?? new System.Collections.Generic.List<UltraPuck.ModSetting>())
                        {
                            if (sub.IsToggle)
                            {
                                var v = GUILayout.Toggle(sub.Index == 1, "      " + sub.Label);
                                if (v != (sub.Index == 1)) sub.Index = v ? 1 : 0;
                                continue;
                            }
                            GUILayout.BeginHorizontal();
                            GUILayout.Label("      " + sub.Label, GUILayout.Width(140f));
                            for (var i = 0; i < sub.Options.Length; i++)
                                if (GUILayout.Toggle(sub.Index == i, sub.Options[i], GUI.skin.button) && sub.Index != i) sub.Index = i;
                            GUILayout.EndHorizontal();
                        }
                        continue;
                    }
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("   " + st.Label, GUILayout.Width(120f));
                    for (var i = 0; i < st.Options.Length; i++)
                        if (GUILayout.Toggle(st.Index == i, st.Options[i], GUI.skin.button) && st.Index != i) st.Index = i;
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(6f);
            }
            GUILayout.EndScrollView();
            foreach (var err in UltraPuck.ModManager.LoadErrors) GUILayout.Label("Couldn't load " + err, _dim);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open Mods folder"))
                GameMenu.OpenModsFolder();
            GUILayout.EndHorizontal();
            GUILayout.Label("Put mod .dll files in the Mods folder in the game folder and restart. Online play is always on.", _dim);
            if (NetSession.IsClient)
                GUILayout.Label("In someone else's game the host's gameplay mods apply.", _dim);

            GUILayout.Space(8f);
            GUILayout.Label("Updates", _header);
            Plugin.AutoUpdate.Value = GUILayout.Toggle(Plugin.AutoUpdate.Value, " Update automatically from GitHub");
            GUILayout.Label(Updater.Status, _dim);
        }

        private Vector2 _modScroll;

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
            GUILayout.Label("Shown to others while connecting. In matches your profile name is used.", _dim);
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
                    var blocked = !string.IsNullOrEmpty(l.Note);
                    GUILayout.Label((string.IsNullOrEmpty(l.HostName) ? "Unknown host" : l.HostName) +
                        (l.Members > 0 ? "  (" + l.Members + " in lobby)" : string.Empty) + (blocked ? "  - " + l.Note : string.Empty));
                    GUI.enabled = !blocked;
                    if (GUILayout.Button("Join", GUILayout.Width(60f))) SteamLobby.Join(l.Lobby);
                    GUI.enabled = true;
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
                GUILayout.Label(c.Name, GUILayout.Width(250f));
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
            GUILayout.Label("Go to player select (Play Match or Play Online > Go to Player Select). Everyone appears there: " +
                "friends join a box with A, pick their profile and team, then you press Start and choose the arena.", _dim);
            if (GUILayout.Button("Stop hosting")) s.Stop("Stopped hosting");
        }

        private void DrawClient(NetSession s)
        {
            if (s.Welcomed)
            {
                GUILayout.Label("Lobby", _header);
                foreach (var e in s.Lobby)
                    GUILayout.Label(e.Name);
            }
            if (GUILayout.Button("Leave game")) s.Stop("You left");
        }

        private void SavePort()
        {
            int p;
            if (int.TryParse(_portText, out p) && p > 0 && p < 65536) Plugin.Port.Value = p;
            _portText = Plugin.Port.Value.ToString();
        }
    }
}
