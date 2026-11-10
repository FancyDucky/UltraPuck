using System;
using System.Collections.Generic;
using System.Linq;
using CP99Online.Net;
using HarmonyLib;
using UnityEngine;

namespace CP99Online
{
    /// <summary>
    /// Adds "Play Online" and "Mods" to the game's own main menu. Everything is built from clones of the
    /// game's menu objects (TextMesh items linked with ListElement, panels that are MenuLists), so it looks,
    /// sounds and navigates (keyboard and every controller) exactly like the built-in menus.
    /// </summary>
    internal static class GameMenu
    {
        internal const float Spacing = 0.667f;

        internal static MenuNavigator Nav { get { return MenuGlobals.menuManager != null ? MenuGlobals.menuManager.menuNavigator : null; } }

        internal static void Build(MenuNavigator nav)
        {
            var main = nav.mainMenu;
            if (main == null) return;
            var slider = main.transform.parent;
            if (slider.Find("CP99_Online") != null) return; // already built

            var optionsEntry = main.transform.Find("Options");
            var optionsPanel = slider.Find("Options");
            if (optionsEntry == null || optionsPanel == null)
            {
                NetLog.Warn("Main menu layout not recognised; in-menu entries skipped (F8 menu still works)");
                return;
            }

            // ---- panels (one level deep: the game's menus don't render a third level of our clones reliably,
            // so settings change in place and the friend list replaces the Play Online items while browsing)
            var online = ModPanel.Create(optionsPanel, "CP99_Online", "Play Online", main, true, OnlineItems, OnlineKey);
            online.OnOpen = () => _browsing = false;
            var mods = ModPanel.Create(optionsPanel, "CP99_Mods", "Mods", main, true, ModsItems, () => "mods");
            online.Rebuild(true);
            mods.Rebuild(true);

            // ---- main menu entries: Play Match, Play Online, Challenge Mode, Arena Creator, Profiles, Options, Mods, Credits, Quit
            var playOnline = CloneEntry(optionsEntry, "CP99_PlayOnline", "Play Online", online.List);
            var modsEntry = CloneEntry(optionsEntry, "CP99_ModsEntry", "Mods", mods.List);

            var order = new List<ListElement>();
            var first = main.defaultElement;
            var e = first;
            do { order.Add(e); e = e.connectedDown; } while (e != null && e != first && order.Count < 32);
            var playIdx = order.FindIndex(x => x.name == "PlayMatch");
            var optIdx = order.FindIndex(x => x.name == "Options");
            order.Insert(optIdx + 1, modsEntry);
            order.Insert(playIdx + 1, playOnline);
            var y0 = order[0].transform.localPosition.y;
            for (var i = 0; i < order.Count; i++)
            {
                var t = order[i].transform;
                t.localPosition = new Vector3(t.localPosition.x, y0 - Spacing * i, t.localPosition.z);
                order[i].connectedDown = order[(i + 1) % order.Count];
                order[i].connectedUp = order[(i + order.Count - 1) % order.Count];
            }
            NetLog.Info("Main menu: added Play Online and Mods");
        }

        private static ListElement CloneEntry(Transform template, string name, string text, MenuList target)
        {
            var go = Clone(template.gameObject, template.parent, name);
            go.GetComponent<TextMesh>().text = text;
            var le = go.GetComponent<ListElement>();
            le.connectedMenu = target;
            le.Deselect();
            return le;
        }

        internal static GameObject Clone(GameObject template, Transform parent, string name)
        {
            var go = (GameObject)UnityEngine.Object.Instantiate(template);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = template.transform.localPosition;
            go.transform.localRotation = template.transform.localRotation;
            go.transform.localScale = template.transform.localScale;
            // The game's translation component would overwrite our English labels.
            foreach (var l in go.GetComponentsInChildren<LocalizedTextMesh>(true)) UnityEngine.Object.DestroyImmediate(l);
            return go;
        }

        // ------------------------------------------------------------------ Play Online

        private static string OnlineKey()
        {
            var s = NetSession.Instance;
            if (s == null) return "none";
            return s.Role + "|" + s.Welcomed + "|" + s.TransportName + "|" + SteamLobby.Available +
                (_browsing && s.Role == Role.None ? "|browse|" + LobbyKey() : string.Empty);
        }

        private static bool _browsing;

        private static List<Item> OnlineItems(ModPanel p)
        {
            var s = NetSession.Instance;
            var items = new List<Item>();
            if (s == null) return items;
            if (_browsing && s.Role == Role.None) return LobbyItems();
            switch (s.Role)
            {
                case Role.None:
                    if (SteamLobby.Available)
                    {
                        items.Add(Item.Action(() => "Host Game", () => s.StartHostSteam()));
                        items.Add(Item.Action(() => "Join a Friend", () => { _browsing = true; SteamLobby.Refresh(); }));
                    }
                    else
                    {
                        items.Add(Item.Action(() => "Host Game (Direct IP)", () => s.StartHostDirect(Plugin.Port.Value, null)));
                    }
                    items.Add(TeamItem(s));
                    items.Add(Item.Action(() => "Controls: " + (Plugin.OnlineController.Value == ControllerChoice.Gamepad ? "Gamepad" : "Keyboard"),
                        () => Plugin.OnlineController.Value = Plugin.OnlineController.Value == ControllerChoice.Gamepad
                            ? ControllerChoice.KeyboardMouse : ControllerChoice.Gamepad));
                    items.Add(Item.Action(() => "More Options (F8)", () => ModMenu.Show(1)));
                    if (!SteamLobby.Available) items.Add(Item.Info(() => "Launch through Steam to host or join friends."));
                    items.Add(Item.Info(() => "Your name: " + NetSession.LocalName()));
                    break;

                case Role.Host:
                    items.Add(Item.Action(() => "Start Match", () => Application.LoadLevel("PlayerSelection")));
                    if (s.TransportName == "Steam") items.Add(Item.Action(() => "Invite Friends", SteamLobby.InviteFriends));
                    items.Add(Item.Action(() => "Stop Hosting", () => s.Stop("Stopped hosting")));
                    items.Add(Item.Action(() => "More Options (F8)", () => ModMenu.Show(1)));
                    items.Add(Item.Info(() => s.ClientCount == 0
                        ? "Waiting for friends to join..."
                        : "Joined: " + string.Join(", ", s.Clients.Select(c => c.Name + " (Team " + (c.Team + 1) + ")").ToArray())));
                    items.Add(Item.Info(() => "Start Match, then pick players and an arena."));
                    break;

                case Role.Client:
                    if (s.Welcomed) items.Add(TeamItem(s));
                    items.Add(Item.Action(() => "Leave Game", () => s.Stop("You left")));
                    items.Add(Item.Info(() => s.Welcomed
                        ? "In " + (string.IsNullOrEmpty(s.HostName) ? "the host" : s.HostName) + "'s game. Waiting for the host to start."
                        : s.Status));
                    items.Add(Item.Info(() => s.Welcomed
                        ? string.Join(", ", s.Lobby.Select(e => e.Name).ToArray())
                        : string.Empty));
                    break;
            }
            return items;
        }

        private static Item TeamItem(NetSession s)
        {
            return Item.Action(() => "Team: " + (Plugin.OnlineTeam.Value + 1), () => s.RequestTeam(1 - Plugin.OnlineTeam.Value));
        }

        private static string LobbyKey()
        {
            return SteamLobby.Lobbies.Count + "|" + string.Join(",", SteamLobby.Lobbies.Select(l => l.Lobby.m_SteamID.ToString()).ToArray());
        }

        private static List<Item> LobbyItems()
        {
            var items = new List<Item>();
            foreach (var l in SteamLobby.Lobbies.ToList())
            {
                var lobby = l;
                items.Add(Item.Action(() => (string.IsNullOrEmpty(lobby.HostName) ? "Unknown host" : lobby.HostName) + " (" + lobby.Members + ")",
                    () => { _browsing = false; SteamLobby.Join(lobby.Lobby); }));
            }
            items.Add(Item.Action(() => "Refresh", SteamLobby.Refresh));
            items.Add(Item.Action(() => "Back", () => _browsing = false));
            items.Add(Item.Info(() => string.IsNullOrEmpty(SteamLobby.BrowserStatus) ? "Friends' games show up here." : SteamLobby.BrowserStatus));
            return items;
        }

        // ------------------------------------------------------------------ Mods

        private static readonly int[] FpsOptions = { 60, 120, 144, 240, 0 };

        private static string FpsName(int v) { return v > 0 ? v.ToString() : "Unlimited"; }

        private static List<Item> ModsItems(ModPanel p)
        {
            return new List<Item>
            {
                Item.Action(() => "No Boost Slowdown: " + (Plugin.NoBoostSlowdown.Value ? "On" : "Off"),
                    () => Plugin.NoBoostSlowdown.Value = !Plugin.NoBoostSlowdown.Value),
                Item.Action(() => "FPS Limit: " + FpsName(Plugin.FrameRateLimit.Value), () =>
                {
                    var i = Array.IndexOf(FpsOptions, Plugin.FrameRateLimit.Value);
                    Plugin.FrameRateLimit.Value = FpsOptions[(i + 1) % FpsOptions.Length];
                }),
                Item.Action(() => "Auto Update: " + (Plugin.AutoUpdate.Value ? "On" : "Off"),
                    () => Plugin.AutoUpdate.Value = !Plugin.AutoUpdate.Value),
                Item.Action(() => "More Options (F8)", () => ModMenu.Show(0)),
                Item.Info(() => "CP99 Online " + Plugin.Version + ": " + Updater.Status),
                Item.Info(() => "Select a setting to change it."),
                Item.Info(() => "No Boost Slowdown: boosting doesn't slow you down afterwards."),
                Item.Info(() => "Online, the host's mechanics settings apply."),
            };
        }
    }

    internal sealed class Item
    {
        public Func<string> Label;
        public Action OnSelect;
        public MenuList Submenu;
        public bool IsInfo, IsDefault;

        public static Item Action(Func<string> label, Action onSelect) { return new Item { Label = label, OnSelect = onSelect }; }
        public static Item Menu(Func<string> label, MenuList sub) { return new Item { Label = label, Submenu = sub }; }
        public static Item Info(Func<string> label) { return new Item { Label = label, IsInfo = true }; }
        public static Item Choice(Func<string> label, Action onSelect, bool current) { return new Item { Label = label, OnSelect = onSelect, IsDefault = current }; }
    }

    /// <summary>A menu panel (clone of a game MenuList) whose items come from code and are rebuilt when state changes.</summary>
    internal sealed class ModPanel : MonoBehaviour
    {
        public MenuList List;
        public Func<ModPanel, List<Item>> ItemsFn;
        public Func<List<Item>> Items { set { ItemsFn = p => value(); } }
        public Func<string> Key;
        public Action OnOpen;
        public readonly Dictionary<string, MenuList> Sub = new Dictionary<string, MenuList>();

        private GameObject _template;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<KeyValuePair<TextMesh, Item>> _labels = new List<KeyValuePair<TextMesh, Item>>();
        private readonly List<ListElement> _elements = new List<ListElement>();
        private string _builtKey;
        private Vector3 _origin;

        internal static ModPanel Create(Transform panelTemplate, string name, string title, MenuList previous, bool stayOpen,
            Func<ModPanel, List<Item>> items, Func<string> key)
        {
            var go = GameMenu.Clone(panelTemplate.gameObject, panelTemplate.parent, name);
            go.SetActive(false);
            var list = go.GetComponent<MenuList>();
            list.previousMenu = previous;
            list.dontCloseOnA = stayOpen;
            list.selectedElement = null;
            foreach (var c in go.GetComponents<MonoBehaviour>())
                if (!(c is MenuList)) UnityEngine.Object.DestroyImmediate(c); // e.g. RumbleDefault-style helpers

            // Keep the title, turn the first item into a hidden template, drop the rest.
            GameObject template = null;
            foreach (Transform child in go.transform.Cast<Transform>().ToList())
            {
                if (child.GetComponent<ListTitle>() != null)
                {
                    var tm = child.GetComponent<TextMesh>();
                    if (tm != null) tm.text = title;
                    continue;
                }
                if (child.GetComponent<ListElement>() != null && template == null && child.GetComponent<TextMesh>().fontSize > 0)
                {
                    template = child.gameObject;
                    continue;
                }
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }
            if (template == null) throw new InvalidOperationException("No item template in " + panelTemplate.name);
            foreach (var c in template.GetComponents<MonoBehaviour>())
                if (!(c is ListElement)) UnityEngine.Object.DestroyImmediate(c);
            template.SetActive(false);

            var panel = go.AddComponent<ModPanel>();
            panel.List = list;
            panel.ItemsFn = items;
            panel.Key = key;
            panel._template = template;
            panel._origin = template.transform.localPosition;
            return panel;
        }

        /// <summary>Called by the game (BroadcastMessage) whenever this panel becomes the current menu.</summary>
        public void InitiliazeMenuList()
        {
            var nav = GameMenu.Nav;
            if (nav == null || nav.selectedMenu != List) return;
            var opening = List.selectedElement == null; // coming from the parent (not back from a child)
            if (opening && OnOpen != null) OnOpen();
            Rebuild(opening);
        }

        private void Update()
        {
            var nav = GameMenu.Nav;
            if (nav != null && nav.selectedMenu == List && Key() != _builtKey) Rebuild(true, true);
            foreach (var kv in _labels)
            {
                var text = kv.Value.Label();
                if (kv.Key.text != text) kv.Key.text = text;
            }
        }

        internal void Rebuild(bool useDefault, bool reset = false)
        {
            var nav = GameMenu.Nav;
            var current = nav != null && nav.selectedMenu == List;
            var keepIndex = current ? Math.Max(0, _elements.IndexOf(nav.selectedElement)) : 0;
            if (current && List.selectedElement != null) keepIndex = Math.Max(0, _elements.IndexOf(List.selectedElement));

            if (Key() != _builtKey || _elements.Count == 0)
            {
                foreach (var o in _objects) if (o != null) Destroy(o);
                _objects.Clear(); _labels.Clear(); _elements.Clear();
                _builtKey = Key();
                var items = ItemsFn(this);
                var y = 0f;
                var defaultIdx = -1;
                var infoStarted = false;
                foreach (var item in items)
                {
                    var go = GameMenu.Clone(_template, transform, item.IsInfo ? "Info" : "Item");
                    go.SetActive(true);
                    var tm = go.GetComponent<TextMesh>();
                    tm.text = item.Label();
                    _objects.Add(go);
                    _labels.Add(new KeyValuePair<TextMesh, Item>(tm, item));
                    if (item.IsInfo)
                    {
                        UnityEngine.Object.DestroyImmediate(go.GetComponent<ListElement>());
                        if (!infoStarted) { infoStarted = true; y -= 0.15f; }
                        go.transform.localScale = _template.transform.localScale * 0.55f;
                        go.transform.localPosition = _origin + new Vector3(0f, y, 0f);
                        tm.color = MenuGlobals.menuManager.darkGreyColor;
                        y -= 0.4f;
                        continue;
                    }
                    go.transform.localPosition = _origin + new Vector3(0f, y, 0f);
                    y -= GameMenu.Spacing;
                    var le = go.GetComponent<ListElement>();
                    le.connectedMenu = item.Submenu;
                    if (item.OnSelect != null) go.AddComponent<ModMenuAction>().OnSelect = item.OnSelect;
                    le.Deselect();
                    if (item.IsDefault) defaultIdx = _elements.Count;
                    _elements.Add(le);
                }
                if (_elements.Count == 0)
                {
                    // Menus need at least one selectable element; use an invisible one.
                    var go = GameMenu.Clone(_template, transform, "Nothing");
                    go.SetActive(true);
                    go.GetComponent<TextMesh>().text = string.Empty;
                    _objects.Add(go);
                    _elements.Add(go.GetComponent<ListElement>());
                }
                for (var i = 0; i < _elements.Count; i++)
                {
                    _elements[i].connectedDown = _elements[(i + 1) % _elements.Count];
                    _elements[i].connectedUp = _elements[(i + _elements.Count - 1) % _elements.Count];
                }
                if (reset) keepIndex = 0;
                if (useDefault && defaultIdx >= 0) keepIndex = defaultIdx;
            }
            else if (useDefault && List.selectedElement == null)
            {
                keepIndex = 0;
                var items = ItemsFn(this).Where(i => !i.IsInfo).ToList();
                for (var i = 0; i < items.Count && i < _elements.Count; i++) if (items[i].IsDefault) keepIndex = i;
            }

            var target = _elements[Mathf.Clamp(keepIndex, 0, _elements.Count - 1)];
            List.defaultElement = target;
            if (current)
            {
                if (nav.selectedElement != null && nav.selectedElement != target) nav.selectedElement.Deselect();
                nav.selectedElement = target;
                target.Select();
            }
        }
    }

    /// <summary>Receives the game's SelectMenuElement message for one of our items.</summary>
    internal sealed class ModMenuAction : MonoBehaviour
    {
        public Action OnSelect;

        public void SelectMenuElement()
        {
            var nav = GameMenu.Nav;
            if (nav == null || nav.selectedElement != GetComponent<ListElement>()) return;
            try { if (OnSelect != null) OnSelect(); }
            catch (Exception ex) { NetLog.Warn("Menu action failed: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(MenuNavigator), "Start")]
    internal static class MenuNavigatorStartPatch
    {
        private static void Postfix(MenuNavigator __instance)
        {
            if (Application.loadedLevelName != "MainMenu") return;
            try { GameMenu.Build(__instance); }
            catch (Exception ex) { NetLog.Warn("Couldn't add main menu entries: " + ex); }
        }
    }

    /// <summary>While the F8 overlay is open, keys typed into it must not also drive the game's menus.</summary>
    [HarmonyPatch(typeof(MenuNavigator), "Update")]
    internal static class MenuNavigatorBlockPatch
    {
        private static bool Prefix() { return !ModMenu.IsOpen; }
    }
}
