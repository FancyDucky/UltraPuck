using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using CP99Online;
using UnityEngine;

namespace UltraPuck
{
    /// <summary>
    /// Finds, loads and runs UltraPuck mods: the built-in ones, every UltraMod in the .dll files in the game's
    /// "Mods" folder, and any registered from a BepInEx plugin with <see cref="Register"/>.
    /// </summary>
    public static class ModManager
    {
        private static readonly List<UltraMod> _mods = new List<UltraMod>();
        private static ConfigFile _config;
        private static bool _started;

        /// <summary>Every mod UltraPuck knows about, built-in ones first.</summary>
        public static UltraMod[] All { get { return _mods.ToArray(); } }

        /// <summary>The folder players drop mod .dll files into (the "Mods" folder next to ClusterPuck99.exe).</summary>
        public static string ModsFolder { get { return Path.Combine(Paths.GameRootPath, "Mods"); } }

        /// <summary>Mod .dll files that couldn't be loaded, with the reason.</summary>
        internal static readonly List<string> LoadErrors = new List<string>();

        /// <summary>
        /// Adds a mod from your own code, e.g. from a BepInEx plugin's Awake. Mods in the Mods folder are found
        /// automatically and don't need this.
        /// </summary>
        public static void Register(UltraMod mod)
        {
            if (mod == null) throw new ArgumentNullException("mod");
            if (_mods.Any(m => m.Id == mod.Id))
            {
                Plugin.Logger_.LogWarning("Mod id '" + mod.Id + "' is already used; " + mod.Name + " was skipped.");
                return;
            }
            _mods.Add(mod);
            if (_started) Start(mod);
        }

        /// <summary>Find a mod by id (null if it isn't loaded).</summary>
        public static UltraMod Get(string id) { return _mods.FirstOrDefault(m => m.Id == id); }

        /// <summary>Turn a mod on or off (saved, like doing it in the Mods menu).</summary>
        public static void SetEnabled(UltraMod mod, bool on)
        {
            if (mod == null || mod.Enabled == on) return;
            mod.EnabledEntry.Value = on;
            if (on) mod.Error = null;
            Apply(mod, on);
        }

        /// <summary>Turn a mod on for this session only, without saving it (used by tests).</summary>
        internal static void EnableForSession(UltraMod mod)
        {
            if (mod == null || mod.Enabled) return;
            Apply(mod, true);
        }

        // ------------------------------------------------------------------ startup

        internal static void LoadAll(GameObject host)
        {
            _config = new ConfigFile(Path.Combine(Paths.ConfigPath, "ultrapuck.mods.cfg"), true);
            var builtins = new UltraMod[] { new FpsPlus(), new SmoothMotion(), new BetterBoost(), new DashMeter(), new RoundsMode() };
            foreach (var b in builtins) b.Builtin = true;
            _mods.InsertRange(0, builtins.Where(b => _mods.All(m => m.Id != b.Id)));

            try
            {
                Directory.CreateDirectory(ModsFolder);
                var readme = Path.Combine(ModsFolder, "README.txt");
                if (!File.Exists(readme))
                    File.WriteAllText(readme,
                        "Put UltraPuck mod .dll files in this folder, then restart the game.\r\n" +
                        "Turn them on and off in Main menu > Mods.\r\n" +
                        "Making a mod: https://github.com/" + Updater.Repo + "/blob/main/docs/MAKING-MODS.md\r\n");
                foreach (var file in Directory.GetFiles(ModsFolder, "*.dll").OrderBy(f => f))
                    LoadFile(file);
            }
            catch (Exception ex) { Plugin.Logger_.LogWarning("Mods folder: " + ex.Message); }

            _started = true;
            foreach (var m in _mods.ToList()) Start(m);
            host.AddComponent<ModRunner>();
            Plugin.Logger_.LogInfo("Mods: " + string.Join(", ", _mods.Select(m => m.Name + (m.Enabled ? " (on)" : " (off)")).ToArray()));
        }

        private static void LoadFile(string file)
        {
            var name = Path.GetFileName(file);
            try
            {
                var asm = Assembly.LoadFrom(file);
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                var found = 0;
                foreach (var t in types.Where(t => typeof(UltraMod).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null))
                {
                    var mod = (UltraMod)Activator.CreateInstance(t);
                    mod.Source = name;
                    Register(mod);
                    found++;
                }
                if (found == 0) LoadErrors.Add(name + ": no UltraMod class inside");
            }
            catch (Exception ex)
            {
                LoadErrors.Add(name + ": " + ex.GetType().Name);
                Plugin.Logger_.LogWarning("Couldn't load mod " + name + ": " + ex);
            }
        }

        private static void Start(UltraMod mod)
        {
            try
            {
                mod.Init(_config);
                mod.OnLoad();
            }
            catch (Exception ex)
            {
                Fail(mod, "failed to load", ex);
                return;
            }
            if (mod.EnabledEntry.Value) Apply(mod, true);
        }

        private static void Apply(UltraMod mod, bool on)
        {
            try
            {
                if (on) { mod.Enabled = true; mod.OnEnable(); }
                else { mod.Enabled = false; mod.OnDisable(); }
            }
            catch (Exception ex)
            {
                Fail(mod, on ? "failed to turn on" : "failed to turn off", ex);
            }
        }

        /// <summary>A mod that throws is turned off for this session (its saved setting stays), so the game keeps running.</summary>
        internal static void Fail(UltraMod mod, string what, Exception ex)
        {
            mod.Error = what + ": " + ex.GetType().Name + (string.IsNullOrEmpty(ex.Message) ? string.Empty : " - " + ex.Message);
            Plugin.Logger_.LogError("Mod " + mod.Name + " " + what + ": " + ex);
            if (!mod.Enabled) return;
            mod.Enabled = false;
            try { mod.OnDisable(); } catch { }
        }
    }

    /// <summary>Calls the per-frame hooks of every mod that is on.</summary>
    internal sealed class ModRunner : MonoBehaviour
    {
        private void Update() { Each((m, _) => m.OnUpdate(), "OnUpdate"); }

        private void OnGUI() { Each((m, _) => m.OnGUI(), "OnGUI"); }

        private void OnLevelWasLoaded(int level)
        {
            var scene = Application.loadedLevelName;
            Each((m, _) => m.OnSceneLoaded(scene), "OnSceneLoaded");
        }

        private static void Each(Action<UltraMod, int> call, string what)
        {
            var mods = ModManager.All;
            for (var i = 0; i < mods.Length; i++)
            {
                var m = mods[i];
                if (!m.Enabled) continue;
                try { call(m, i); }
                catch (Exception ex) { ModManager.Fail(m, what + " failed", ex); }
            }
        }
    }
}
