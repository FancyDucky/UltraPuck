using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace UltraPuck
{
    /// <summary>
    /// Base class for an UltraPuck mod. Make a class that inherits from this, build it into a .dll and drop the
    /// .dll into the game's "Mods" folder. It then shows up in Main menu > Mods, where players can turn it on
    /// and off and change its settings.
    ///
    /// By default, turning the mod on applies every [HarmonyPatch] class in your .dll and turning it off removes
    /// them again, so most mods only need patches plus a Name. See docs/MAKING-MODS.md.
    /// </summary>
    public abstract class UltraMod
    {
        /// <summary>Unique id, e.g. "yourname.bigpuck". Used to save the mod's on/off state and settings.</summary>
        public abstract string Id { get; }

        /// <summary>Name shown in the Mods menu. Keep it short (about 20 characters).</summary>
        public abstract string Name { get; }

        public virtual string Author { get { return string.Empty; } }
        public virtual string Version { get { return "1.0"; } }

        /// <summary>One short sentence shown on the mod's page.</summary>
        public virtual string Description { get { return string.Empty; } }

        /// <summary>Whether the mod starts turned on the first time a player installs it.</summary>
        public virtual bool EnabledByDefault { get { return true; } }

        /// <summary>True while the mod is turned on.</summary>
        public bool Enabled { get; internal set; }

        /// <summary>Set when the mod threw an error and was turned off.</summary>
        public string Error { get; internal set; }

        /// <summary>The settings this mod added with AddToggle/AddChoice, in order.</summary>
        public IList<ModSetting> Settings { get { return _settings.AsReadOnly(); } }

        /// <summary>Harmony instance for this mod (its patches are removed when the mod is turned off).</summary>
        protected Harmony Harmony { get; private set; }

        /// <summary>Write to the BepInEx log (BepInEx/LogOutput.log).</summary>
        protected ManualLogSource Log { get; private set; }

        /// <summary>This mod's section of BepInEx/config/ultrapuck.mods.cfg, for anything beyond AddToggle/AddChoice.</summary>
        protected ConfigFile Config { get; private set; }

        internal ConfigEntry<bool> EnabledEntry;
        internal bool Builtin;
        internal string Source = "built in";
        private readonly List<ModSetting> _settings = new List<ModSetting>();

        internal void Init(ConfigFile config)
        {
            Config = config;
            Harmony = new Harmony("ultrapuck.mod." + Id);
            Log = Logger.CreateLogSource(Name);
            EnabledEntry = config.Bind(Section, "Enabled", EnabledByDefault, "Whether " + Name + " is turned on.");
        }

        internal string Section { get { return "Mod: " + Id; } }

        // ------------------------------------------------------------------ things to override

        /// <summary>Called once when the game starts, whether or not the mod is on. Add your settings here.</summary>
        public virtual void OnLoad() { }

        /// <summary>Called when the mod is turned on (also at startup if it is on). Default: applies the .dll's Harmony patches.</summary>
        public virtual void OnEnable() { Harmony.PatchAll(GetType().Assembly); }

        /// <summary>Called when the mod is turned off. Default: removes this mod's Harmony patches.</summary>
        public virtual void OnDisable() { Harmony.UnpatchSelf(); }

        /// <summary>Called every frame while the mod is on.</summary>
        public virtual void OnUpdate() { }

        /// <summary>Called for drawing IMGUI (GUI/GUILayout) while the mod is on.</summary>
        public virtual void OnGUI() { }

        /// <summary>Called after a scene loads while the mod is on (e.g. "MainMenu", "PlayerSelection", "GameSelection", or an arena).</summary>
        public virtual void OnSceneLoaded(string sceneName) { }

        // ------------------------------------------------------------------ settings

        /// <summary>Adds an On/Off setting to the mod's page. Read it with .Value.</summary>
        protected ConfigEntry<bool> AddToggle(string key, string label, bool defaultValue, string description = "")
        {
            return AddChoice(Config.Bind(Section, key, defaultValue, description), label, new[] { false, true }, v => v ? "On" : "Off", description);
        }

        /// <summary>Adds a setting that cycles through <paramref name="options"/> each time it is selected.</summary>
        protected ConfigEntry<T> AddChoice<T>(string key, string label, T defaultValue, T[] options, Func<T, string> format = null, string description = "")
        {
            return AddChoice(Config.Bind(Section, key, defaultValue, description), label, options, format, description);
        }

        /// <summary>Shows an existing config entry on the mod's page as a setting that cycles through <paramref name="options"/>.</summary>
        protected ConfigEntry<T> AddChoice<T>(ConfigEntry<T> entry, string label, T[] options, Func<T, string> format = null, string description = "")
        {
            if (options == null || options.Length == 0) throw new ArgumentException("options must not be empty");
            _settings.Add(new ModSetting(label, description,
                options.Select(o => format != null ? format(o) : Convert.ToString(o)).ToArray(),
                () => Math.Max(0, Array.IndexOf(options, entry.Value)),
                i => entry.Value = options[i]));
            return entry;
        }

        /// <summary>
        /// Adds an item that opens a page of more settings (e.g. one On/Off per card). The list is asked for each
        /// time the page opens, so it can change. Build entries with <see cref="ModSetting.Toggle"/>.
        /// </summary>
        protected ModSetting AddPage(string label, Func<IList<ModSetting>> settings, string description = "")
        {
            var page = ModSetting.MakePage(label, description, settings);
            _settings.Add(page);
            return page;
        }
    }

    /// <summary>A setting shown on a mod's page: a label and a value that cycles through a list of options.</summary>
    public sealed class ModSetting
    {
        public string Label { get; private set; }
        public string Description { get; private set; }
        public string[] Options { get; private set; }

        private readonly Func<int> _get;
        private readonly Action<int> _set;

        internal ModSetting(string label, string description, string[] options, Func<int> get, Action<int> set)
        {
            Label = label;
            Description = description;
            Options = options;
            _get = get;
            _set = set;
        }

        public int Index
        {
            get { return _get(); }
            set { _set(((value % Options.Length) + Options.Length) % Options.Length); }
        }

        public string ValueText { get { return Options[Index]; } }

        public void Next() { Index = Index + 1; }

        /// <summary>For a page item (see UltraMod.AddPage): the settings shown on that page. Null for normal settings.</summary>
        public Func<IList<ModSetting>> Page { get; private set; }

        internal static ModSetting MakePage(string label, string description, Func<IList<ModSetting>> page)
        {
            return new ModSetting(label, description, new[] { string.Empty }, () => 0, i => { }) { Page = page };
        }

        /// <summary>An On/Off setting backed by your own get/set (for pages built with UltraMod.AddPage).</summary>
        public static ModSetting Toggle(string label, Func<bool> get, Action<bool> set, string description = "")
        {
            return new ModSetting(label, description, new[] { "Off", "On" }, () => get() ? 1 : 0, i => set(i == 1));
        }

        /// <summary>True for a two-option Off/On setting.</summary>
        internal bool IsToggle { get { return Page == null && Options.Length == 2 && Options[0] == "Off" && Options[1] == "On"; } }
    }
}
