using System.Collections.Generic;
using UnityEngine;

namespace CP99Online
{
    /// <summary>Small notifications at the top of the screen ("Sam joined"), shown in every scene, fading out after a few seconds.</summary>
    internal sealed class Toast : MonoBehaviour
    {
        private const float Life = 4f, Fade = 0.6f;
        private const int MaxShown = 4;

        private sealed class Entry { public string Text; public float Born; }

        private static readonly List<Entry> _entries = new List<Entry>();
        private GUIStyle _style;
        private Texture2D _bg;

        internal static void Show(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _entries.Add(new Entry { Text = text, Born = Time.realtimeSinceStartup });
            if (_entries.Count > MaxShown) _entries.RemoveAt(0);
            Net.NetLog.Info("Notification: " + text);
        }

        private void OnGUI()
        {
            if (_entries.Count == 0) return;
            var now = Time.realtimeSinceStartup;
            _entries.RemoveAll(e => now - e.Born > Life);
            if (_entries.Count == 0) return;

            if (_style == null)
            {
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, new Color(0.08f, 0.08f, 0.09f, 0.82f));
                _bg.Apply();
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
                _style.normal.background = _bg;
                _style.normal.textColor = Color.white;
            }
            _style.fontSize = Mathf.Max(14, Mathf.RoundToInt(Screen.height / 38f));
            _style.padding = new RectOffset(_style.fontSize, _style.fontSize, _style.fontSize / 3, _style.fontSize / 3);

            var old = GUI.color;
            var y = Screen.height * 0.03f;
            foreach (var e in _entries)
            {
                var age = now - e.Born;
                var a = Mathf.Clamp01(Mathf.Min(age / 0.15f, (Life - age) / Fade));
                GUI.color = new Color(1f, 1f, 1f, a);
                var content = new GUIContent(e.Text);
                var size = _style.CalcSize(content);
                GUI.Label(new Rect((Screen.width - size.x) / 2f, y, size.x, size.y), content, _style);
                y += size.y + 4f;
            }
            GUI.color = old;
        }
    }
}
