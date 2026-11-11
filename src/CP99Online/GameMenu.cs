using System;
using System.Collections.Generic;
using System.Linq;
using CP99Online.Net;
using HarmonyLib;
using UltraPuck;
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
            var mods = ModPanel.Create(optionsPanel, "CP99_Mods", "Mods", main, true, ModsItems, ModsKey);
            mods.OnOpen = () => { _modPage = null; _lastMod = null; _subPage = null; _lastSub = null; _listPage = 0; };
            ModsPanel = mods;
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
                    if (!SteamLobby.Available) items.Add(Item.Info(() => "Launch through Steam to host or join friends."));
                    break;

                case Role.Host:
                    items.Add(Item.Action(() => "Go to Player Select", () => Application.LoadLevel("PlayerSelection")));
                    if (s.TransportName == "Steam") items.Add(Item.Action(() => "Invite Friends", SteamLobby.InviteFriends));
                    items.Add(Item.Action(() => "Stop Hosting", () => s.Stop("Stopped hosting")));
                    items.Add(Item.Info(() => s.ClientCount == 0
                        ? "Waiting for friends to join..."
                        : "Joined: " + string.Join(", ", s.Clients.Select(c => c.Name).ToArray())));
                    break;

                case Role.Client:
                    items.Add(Item.Action(() => "Leave Game", () => s.Stop("You left")));
                    items.Add(Item.Info(() => s.Welcomed
                        ? "In " + (string.IsNullOrEmpty(s.HostName) ? "the host" : s.HostName) + "'s game. You'll follow the host to player select."
                        : s.Status));
                    items.Add(Item.Info(() => s.Welcomed
                        ? string.Join(", ", s.Lobby.Select(e => e.Name).ToArray())
                        : string.Empty));
                    break;
            }
            return items;
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

        // ------------------------------------------------------------------ Mods (mod manager)
        // One panel with two views: the mod list, and one mod's page (on/off + its settings). B on a mod's
        // page goes back to the list (MenuNavigatorBackPatch).

        internal static ModPanel ModsPanel;
        private static UltraMod _modPage, _lastMod;
        private static int _listPage;
        private const int ModsPerPage = 5;
        private const int InfoChars = 46;

        // A mod setting that opens its own page (UltraMod.AddPage), e.g. Rounds Mode > Toggle Cards.
        private static ModSetting _subPage, _lastSub;
        private static int _subPageNum;
        private const int SettingsPerPage = 5;

        private static string ModsKey()
        {
            if (_modPage != null && _subPage != null) return "sub|" + _modPage.Id + "|" + _subPage.Label + "|" + _subPageNum;
            return _modPage != null ? "mod|" + _modPage.Id : "list|" + _listPage + "|" + ModManager.All.Length;
        }

        internal static bool ModPageOpen { get { return _modPage != null; } }

        /// <summary>Back: from a settings page to its mod's page, from a mod's page to the mod list.</summary>
        internal static void CloseModPage()
        {
            if (_subPage != null) { _lastSub = _subPage; _subPage = null; return; }
            _lastMod = _modPage;
            _lastSub = null;
            _modPage = null;
        }

        private static string OnOff(UltraMod m)
        {
            return m.Error != null && !m.Enabled ? "Error" : m.Enabled ? "On" : "Off";
        }

        private static List<Item> ModsItems(ModPanel p)
        {
            if (_modPage != null && _subPage != null) return SubPageItems(_subPage);
            return _modPage != null ? ModPageItems(_modPage) : ModListItems();
        }

        private static List<Item> ModListItems()
        {
            var mods = ModManager.All;
            var pages = Math.Max(1, (mods.Length + ModsPerPage - 1) / ModsPerPage);
            _listPage = Mathf.Clamp(_listPage, 0, pages - 1);
            var items = new List<Item>();
            foreach (var mod in mods.Skip(_listPage * ModsPerPage).Take(ModsPerPage))
            {
                var m = mod;
                items.Add(Item.Choice(() => m.Name + ": " + OnOff(m), () => _modPage = m, m == _lastMod));
            }
            if (pages > 1)
                items.Add(Item.Action(() => "More Mods (" + (_listPage + 1) + "/" + pages + ")", () => _listPage = (_listPage + 1) % pages));
            items.Add(Item.Action(() => "Auto Update: " + (Plugin.AutoUpdate.Value ? "On" : "Off"),
                () => Plugin.AutoUpdate.Value = !Plugin.AutoUpdate.Value));
            items.Add(Item.Action(() => "Open Mods Folder", OpenModsFolder));
            items.Add(Item.Action(() => "More Options (F8)", () => ModMenu.Show(0)));
            items.Add(Item.Info(() => "UltraPuck " + Plugin.Version + ": " + Updater.Status));
            items.Add(Item.Info(() => "Select a mod to turn it on or off. Online play is always on."));
            items.Add(Item.Info(() => ModManager.LoadErrors.Count == 0
                ? "Add mods: put their .dll in the game's Mods folder, then restart."
                : ModManager.LoadErrors.Count + " mod file(s) couldn't load: " + string.Join(", ", ModManager.LoadErrors.ToArray())));
            return items;
        }

        private static List<Item> ModPageItems(UltraMod m)
        {
            var items = new List<Item>
            {
                Item.Action(() => m.Name + ": " + OnOff(m), () => ModManager.SetEnabled(m, !m.Enabled)),
            };
            foreach (var setting in m.Settings)
            {
                var st = setting;
                if (st.Page != null) items.Add(Item.Choice(() => st.Label + " >", () => { _subPage = st; _subPageNum = 0; }, st == _lastSub));
                else items.Add(Item.Action(() => st.Label + ": " + st.ValueText, st.Next));
            }
            items.Add(Item.Action(() => "Back", CloseModPage));
            var by = (string.IsNullOrEmpty(m.Author) ? string.Empty : "by " + m.Author + ", ") + "version " + m.Version +
                (m.Builtin ? " (built in)" : " (" + m.Source + ")");
            foreach (var line in Wrap(m.Description)) { var l = line; items.Add(Item.Info(() => l)); }
            items.Add(Item.Info(() => by));
            items.Add(Item.Info(() => m.Error != null ? "Turned off after an error: " + Clip(m.Error) : string.Empty));
            return items;
        }

        private static List<Item> SubPageItems(ModSetting page)
        {
            List<ModSetting> list;
            try { list = (page.Page() ?? new List<ModSetting>()).ToList(); }
            catch (Exception ex) { NetLog.Warn("Settings page " + page.Label + ": " + ex.Message); list = new List<ModSetting>(); }
            var pages = Math.Max(1, (list.Count + SettingsPerPage - 1) / SettingsPerPage);
            _subPageNum = Mathf.Clamp(_subPageNum, 0, pages - 1);

            var items = new List<Item>();
            foreach (var setting in list.Skip(_subPageNum * SettingsPerPage).Take(SettingsPerPage))
            {
                var st = setting;
                items.Add(Item.Action(() => st.Label + ": " + st.ValueText, st.Next));
            }
            if (pages > 1)
                items.Add(Item.Action(() => "More (" + (_subPageNum + 1) + "/" + pages + ")", () => _subPageNum = (_subPageNum + 1) % pages));
            if (list.Count > 1 && list.All(s => s.IsToggle))
            {
                items.Add(Item.Action(() => "Turn All On", () => { foreach (var s in list) s.Index = 1; }));
                items.Add(Item.Action(() => "Turn All Off", () => { foreach (var s in list) s.Index = 0; }));
            }
            items.Add(Item.Action(() => "Back", CloseModPage));
            foreach (var line in Wrap(page.Description)) { var l = line; items.Add(Item.Info(() => l)); }
            if (list.All(s => s.IsToggle))
                items.Add(Item.Info(() => list.Count(s => s.Index == 1) + " of " + list.Count + " on"));
            return items;
        }

        internal static void OpenModsFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(ModManager.ModsFolder);
                Application.OpenURL(new Uri(ModManager.ModsFolder).AbsoluteUri); // file:///C:/... or file:///Users/...
            }
            catch (Exception ex) { NetLog.Warn("Open Mods folder: " + ex.Message); }
        }

        private static string Clip(string s) { return s.Length > InfoChars + 20 ? s.Substring(0, InfoChars + 17) + "..." : s; }

        /// <summary>TextMesh doesn't wrap, so split long descriptions into short info lines (at most 3).</summary>
        private static List<string> Wrap(string text)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            var line = string.Empty;
            foreach (var word in text.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > InfoChars) { lines.Add(line); line = word; }
                else line = line.Length == 0 ? word : line + " " + word;
            }
            if (line.Length > 0) lines.Add(line);
            if (lines.Count > 3) { lines.RemoveRange(3, lines.Count - 3); lines[2] += "..."; }
            return lines;
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

    /// <summary>B on a mod's page goes back to the mod list instead of leaving the Mods menu.</summary>
    [HarmonyPatch(typeof(MenuNavigator), nameof(MenuNavigator.PreviousMenu))]
    internal static class MenuNavigatorBackPatch
    {
        private static bool Prefix(MenuNavigator __instance)
        {
            var panel = GameMenu.ModsPanel;
            if (panel == null || __instance.selectedMenu != panel.List || !GameMenu.ModPageOpen) return true;
            GameMenu.CloseModPage();
            __instance.prevMenuSound.Play();
            return false;
        }
    }

    /// <summary>While the F8 overlay is open, keys typed into it must not also drive the game's menus.</summary>
    [HarmonyPatch(typeof(MenuNavigator), "Update")]
    internal static class MenuNavigatorBlockPatch
    {
        private static bool Prefix() { return !ModMenu.IsOpen; }
    }
}
