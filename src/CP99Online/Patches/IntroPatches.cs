using CP99Online.Net;
using HarmonyLib;
using UnityEngine;

namespace CP99Online.Patches
{
    /// <summary>
    /// Skip the opening (SkipIntro, on by default):
    /// - the studio logo scene goes straight on to the menus, as the game's own skip does;
    /// - the "Press A to Begin" title screen and the language picker after it are passed, so the main menu opens with
    ///   Play selected, in the language the game already has (turn SkipIntro off to pick another).
    /// </summary>
    internal sealed class IntroSkip : MonoBehaviour
    {
        private bool _logoDone, _titleDone;
        private int _menuFrame = -1;
        private float _menuTime;

        internal static void LogSaveData(string when)
        {
            var dm = DataManager.Instance;
            if (dm == null) { NetLog.Info("Save data at " + when + ": no DataManager"); return; }
            var t = Traverse.Create(dm);
            var data = t.Field("saveData").GetValue<System.Collections.Generic.Dictionary<string, string>>();
            NetLog.Info("Save data at " + when + ": initialized " + t.Field("initialized").GetValue<bool>() + ", processing " + dm.processing
                + ", keys " + (data != null ? data.Count : -1) + ", language " + dm.GetString("LocalizationLanguage", "?") + ", file " + t.Field("saveDirectory").GetValue<string>());
        }

        private static bool SaveDataReady()
        {
            var dm = DataManager.Instance;
            if (dm == null || dm.processing) return false;
            if (ProfileManager.Instance == null || ProfileManager.Instance.processing) return false;
            return Traverse.Create(dm).Field("initialized").GetValue<bool>();
        }

        private void Update()
        {
            if (_logoDone && _titleDone) return;
            var scene = Application.loadedLevelName;
            if (!_logoDone)
            {
                if (scene == "PHL_Tag")
                {
                    _logoDone = true;
                    if (Plugin.SkipIntro.Value) { NetLog.Info("Skipping the intro"); Application.LoadLevel("LoadMainMenu"); }
                }
                else if (scene == "MainMenu" || Time.unscaledTime > 30f) _logoDone = true;
            }
            if (!_titleDone && scene == "MainMenu")
            {
                // a couple of frames in, so the title screen has set itself up, and once the save data has loaded
                // (the game reads it at startup; at most 1 s more, in case it never does)
                if (_menuFrame < 0) { _menuFrame = Time.frameCount; _menuTime = Time.unscaledTime; LogSaveData("main menu"); return; }
                if (Time.frameCount < _menuFrame + 2) return;
                if (!SaveDataReady() && Time.unscaledTime - _menuTime < 1f) return;
                _titleDone = true;
                SkipTitle();
            }
            else if (!_titleDone && Time.unscaledTime > 60f) _titleDone = true;
        }

        private static void SkipTitle()
        {
            if (!Plugin.SkipIntro.Value) return;
            var ss = FindObjectOfType<StartScreen>();
            if (ss == null || !ss.viewingStartMenu) // not on the title screen
            {
                NetLog.Info("Title screen: " + (ss == null ? "not found" : "not showing (start menu " + ss.viewingStartMenu + ", start button " + ss.viewingStartButton + ")"));
                return;
            }
            if (ProfileManager.Instance == null) { NetLog.Info("Title screen kept: profiles not ready"); return; }
            // the language picker that follows "Press A" is skipped too: the game keeps the language it has
            NetLog.Info("Skipping the title screen (language " + (DataManager.Instance != null ? DataManager.Instance.GetString("LocalizationLanguage", "?")
                + ", picked " + DataManager.Instance.GetString("SelectedLanguage", "0") : "?") + ")");
            ss.CancelInvoke("FadeOutPlayerSupport"); // else A would count as "start" again in a few seconds
            StartScreen.pressable = true;
            ProfileManager.Instance.associatedGamepadIDX = 0u; // what pressing A sets
            ss.SendMessage("SkipLocalization");
            // with Play Match picked, as when you come back to the menu from a match
            if (ss.navigator != null && ss.playMatchButton != null)
            {
                var play = ss.playMatchButton.GetComponent<ListElement>();
                if (play != null)
                {
                    if (ss.navigator.selectedElement != null && ss.navigator.selectedElement != play) ss.navigator.selectedElement.Deselect();
                    ss.navigator.selectedElement = play;
                    play.Select();
                }
            }
        }
    }
}
