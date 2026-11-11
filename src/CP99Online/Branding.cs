using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CP99Online
{
    /// <summary>
    /// UltraPuck branding on the main menu:
    /// - The "Cluster Puck 99" logo (a textured quad in a few places: the Press Start screen, the main menu and the
    ///   loading screen) is hidden and replaced by "Ultra Puck" text in the game's own logo-style font (Fairview).
    ///   "Ultra" cycles through rainbow colours, moving left to right.
    /// - The mod's version number in the bottom-right corner.
    /// </summary>
    internal sealed class Branding : MonoBehaviour
    {
        private static readonly string[] LogoNames = { "GameTitle", "GameTitlePressStart", "LoadingTitle" };
        private const string TitleFont = "Fairview_Regular";
        private const string VersionFont = "Mission Gothic Light";

        private sealed class Title { public TextMesh Text; public Renderer Logo; }
        private readonly List<Title> _titles = new List<Title>();
        private float _nextScan;
        private GUIStyle _versionStyle;
        private Font _versionFont;

        private void OnLevelWasLoaded(int level) { _titles.Clear(); _nextScan = 0f; }

        private void Update()
        {
            if (Application.loadedLevelName != "MainMenu") return;
            if (Time.realtimeSinceStartup >= _nextScan)
            {
                _nextScan = Time.realtimeSinceStartup + 1f;
                _titles.RemoveAll(t => t.Text == null || t.Logo == null);
                foreach (var r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
                {
                    if (!LogoNames.Contains(r.name) || !r.gameObject.scene.IsValid()) continue; // skip prefab assets
                    if (_titles.Any(t => t.Logo == r)) continue;
                    var made = Make(r);
                    if (made != null) _titles.Add(made);
                }
            }
            var text = TitleText(Time.realtimeSinceStartup);
            foreach (var t in _titles)
            {
                if (t.Logo.enabled) t.Logo.enabled = false; // keep the old logo hidden
                t.Text.text = text;
            }
        }

        /// <summary>"Ultra" in a rainbow that flows left to right, "Puck" in white.</summary>
        internal static string TitleText(float time)
        {
            const string ultra = "Ultra";
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < ultra.Length; i++)
            {
                // The colour at letter i is the colour letter 0 had a moment ago, so the colours travel rightwards.
                var hue = Mathf.Repeat(time * 0.25f - i * 0.09f, 1f);
                sb.Append("<color=#").Append(Hex(Hsv(hue, 0.8f, 1f))).Append('>').Append(ultra[i]).Append("</color>");
            }
            return sb.Append("\nPuck").ToString();
        }

        private static Title Make(MeshRenderer logo)
        {
            var font = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name == TitleFont);
            if (font == null) { Net.NetLog.Warn("Branding: font " + TitleFont + " not found; keeping the original logo"); return null; }

            // The logo picture's letters fill roughly this part of its quad (measured from screenshots).
            var b = logo.bounds;
            var go = new GameObject("UltraPuckTitle");
            go.transform.SetParent(logo.transform.parent, false);
            go.transform.rotation = logo.transform.rotation;
            go.layer = logo.gameObject.layer;
            var tm = go.AddComponent<TextMesh>();
            tm.font = font;
            tm.fontSize = 120;
            tm.characterSize = 1f;
            tm.fontStyle = FontStyle.Bold;
            tm.lineSpacing = 0.82f;
            tm.anchor = TextAnchor.UpperLeft;
            tm.alignment = TextAlignment.Left;
            tm.richText = true;
            tm.color = Color.white;
            tm.text = TitleText(0f);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = font.material;

            // Scale the text so its height matches the logo's lettering, then line up the top-left corners.
            go.transform.localScale = Vector3.one;
            var tb = mr.bounds;
            var wantHeight = b.size.y * LetterHeight;
            var k = tb.size.y > 0.0001f ? wantHeight / tb.size.y : 0.01f;
            go.transform.localScale = Vector3.one * k;
            var top = new Vector3(b.min.x + b.size.x * LetterLeft, b.max.y - b.size.y * LetterTop, b.center.z - 0.01f);
            go.transform.position = top;
            // Text meshes put the line's ascent above the anchor; nudge so the visible letters start at "top".
            var nb = mr.bounds;
            go.transform.position += new Vector3(top.x - nb.min.x, top.y - nb.max.y, 0f);
            logo.enabled = false;
            Net.NetLog.Info("Branding: replaced " + logo.name + " (logo " + b.size.x.ToString("F2") + "x" + b.size.y.ToString("F2") + ")");
            return new Title { Text = tm, Logo = logo };
        }

        // Where the original lettering sits inside the logo quad, as fractions of the quad (tuned by screenshot).
        internal static float LetterLeft = 0.08f, LetterTop = 0.125f, LetterHeight = 0.66f;

        private void OnGUI()
        {
            if (Application.loadedLevelName != "MainMenu") return;
            if (_versionStyle == null)
            {
                _versionFont = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name == VersionFont);
                _versionStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerRight };
                if (_versionFont != null) _versionStyle.font = _versionFont;
                _versionStyle.normal.textColor = new Color(1f, 1f, 1f, 0.8f);
            }
            _versionStyle.fontSize = Mathf.Max(12, Mathf.RoundToInt(Screen.height * 0.032f));
            var pad = Screen.height * 0.025f;
            GUI.Label(new Rect(0f, 0f, Screen.width - pad, Screen.height - pad), "UltraPuck v" + Plugin.Version, _versionStyle);
        }

        private static Color Hsv(float h, float s, float v)
        {
            h = Mathf.Repeat(h, 1f) * 6f;
            var i = Mathf.FloorToInt(h);
            var f = h - i;
            float p = v * (1f - s), q = v * (1f - s * f), t = v * (1f - s * (1f - f));
            switch (i % 6)
            {
                case 0: return new Color(v, t, p);
                case 1: return new Color(q, v, p);
                case 2: return new Color(p, v, t);
                case 3: return new Color(p, q, v);
                case 4: return new Color(t, p, v);
                default: return new Color(v, p, q);
            }
        }

        private static string Hex(Color c)
        {
            return ((int)(c.r * 255f)).ToString("X2") + ((int)(c.g * 255f)).ToString("X2") + ((int)(c.b * 255f)).ToString("X2");
        }
    }
}
