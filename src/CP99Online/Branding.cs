using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CP99Online
{
    /// <summary>
    /// UltraPuck branding on the main menu:
    /// - The "Cluster Puck 99" logo (a textured quad in a few places: the Press Start screen, the main menu and the
    ///   loading screen) is hidden and replaced by "Ultra / Puck" in the Chakra Petch font. The letters are white images
    ///   embedded in the DLL (Assets/title-*.png, rendered from Chakra Petch, SIL Open Font License), drawn as sprites;
    ///   each letter of "Ultra" is its own sprite so it can be tinted, and the colours flow left to right.
    /// - The mod's version number in the bottom-right corner.
    /// </summary>
    internal sealed class Branding : MonoBehaviour
    {
        private static readonly string[] LogoNames = { "GameTitle", "GameTitlePressStart", "LoadingTitle" };
        private const string VersionFont = "Mission Gothic Light";

        private sealed class Title { public GameObject Root; public Renderer Logo; public SpriteRenderer[] Ultra; public SpriteRenderer Puck; }
        private readonly List<Title> _titles = new List<Title>();
        private float _nextScan;
        private GUIStyle _versionStyle;
        private Font _versionFont;

        private static Texture2D _ultraTex, _puckTex;
        private static Sprite[] _ultraLetters;
        private static Sprite _puck;
        private static bool _loadFailed;

        // Where the original lettering sits inside the logo quad, as fractions of the quad (tuned by screenshot).
        internal static float LetterLeft = 0.08f, LetterTop = 0.125f, LetterHeight = 0.66f;
        private const float LineGap = 0.2f; // space between "Ultra" and "Puck", as a fraction of a line's height

        private void OnLevelWasLoaded(int level) { _titles.Clear(); _nextScan = 0f; }

        private void Update()
        {
            if (Application.loadedLevelName != "MainMenu") return;
            if (Time.realtimeSinceStartup >= _nextScan)
            {
                _nextScan = Time.realtimeSinceStartup + 1f;
                _titles.RemoveAll(t => t.Root == null || t.Logo == null);
                if (LoadSprites())
                    foreach (var r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
                    {
                        if (!LogoNames.Contains(r.name) || !r.gameObject.scene.IsValid()) continue; // skip prefab assets
                        if (_titles.Any(t => t.Logo == r)) continue;
                        _titles.Add(Make(r));
                    }
            }
            var time = Time.realtimeSinceStartup;
            foreach (var t in _titles)
            {
                if (t.Logo.enabled) t.Logo.enabled = false; // keep the old logo hidden
                // The loading screen's logo fades in and out through its material colour (the game's Fader); follow it.
                var m = t.Logo.sharedMaterial;
                var alpha = m != null && m.HasProperty("_Color") ? m.color.a : 1f;
                for (var i = 0; i < t.Ultra.Length; i++)
                {
                    var c = LetterColor(time, i);
                    c.a = alpha;
                    t.Ultra[i].color = c;
                }
                t.Puck.color = new Color(1f, 1f, 1f, alpha);
            }
        }

        /// <summary>
        /// Rainbow for "Ultra": letter i shows the colour letter 0 had a moment ago, so the colours travel rightwards.
        /// </summary>
        internal static Color LetterColor(float time, int i)
        {
            return Hsv(Mathf.Repeat(time * 0.25f - i * 0.09f, 1f), 0.8f, 1f);
        }

        private static bool LoadSprites()
        {
            if (_ultraLetters != null) return true;
            if (_loadFailed) return false;
            _ultraTex = LoadTexture("UltraPuck.title-ultra.png");
            _puckTex = LoadTexture("UltraPuck.title-puck.png");
            if (_ultraTex == null || _puckTex == null)
            {
                _loadFailed = true;
                Net.NetLog.Warn("Branding: title images missing; keeping the original logo");
                return false;
            }
            // Both images are one line tall, so 1 world unit = one line; Make() scales the whole title to fit.
            var ppu = (float)_ultraTex.height;
            _ultraLetters = SplitLetters(_ultraTex).Select(r => Sprite.Create(_ultraTex, r, new Vector2(0f, 1f), ppu)).ToArray();
            _puck = Sprite.Create(_puckTex, new Rect(0f, 0f, _puckTex.width, _puckTex.height), new Vector2(0f, 1f), ppu);
            return true;
        }

        private static Texture2D LoadTexture(string name)
        {
            using (var s = typeof(Branding).Assembly.GetManifestResourceStream(name))
            {
                if (s == null) return null;
                var bytes = new byte[s.Length];
                var read = 0;
                while (read < bytes.Length) { var n = s.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                var tex = new Texture2D(2, 2, TextureFormat.ARGB32, true);
                if (!tex.LoadImage(bytes)) return null;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Trilinear;
                tex.anisoLevel = 4;
                tex.name = name;
                return tex;
            }
        }

        /// <summary>Cuts a word image into letters at the empty columns between them (cuts mid-gap, so no gaps are lost).</summary>
        private static List<Rect> SplitLetters(Texture2D tex)
        {
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            var ink = new bool[w];
            for (var x = 0; x < w; x++)
                for (var y = 0; y < h; y++)
                    if (px[y * w + x].a > 8) { ink[x] = true; break; }
            var runs = new List<int[]>();
            for (var x = 0; x < w; x++)
            {
                if (!ink[x]) continue;
                var s = x;
                while (x < w && ink[x]) x++;
                runs.Add(new[] { s, x });
            }
            var rects = new List<Rect>();
            for (var i = 0; i < runs.Count; i++)
            {
                var left = i == 0 ? 0 : (runs[i - 1][1] + runs[i][0]) / 2;
                var right = i == runs.Count - 1 ? w : (runs[i][1] + runs[i + 1][0]) / 2;
                rects.Add(new Rect(left, 0f, right - left, h));
            }
            return rects;
        }

        private static Title Make(MeshRenderer logo)
        {
            var b = logo.bounds;
            var root = new GameObject("UltraPuckTitle");
            root.layer = logo.gameObject.layer;

            // Lay the title out in "line" units: "Ultra" on top, "Puck" below.
            var letters = new List<SpriteRenderer>();
            var x = 0f;
            foreach (var sp in _ultraLetters)
            {
                var sr = AddSprite(root, sp, new Vector3(x, 0f, 0f), logo);
                letters.Add(sr);
                x += sp.rect.width / sp.pixelsPerUnit;
            }
            var puck = AddSprite(root, _puck, new Vector3(0f, -(1f + LineGap), 0f), logo);

            // Scale to the old logo's lettering and put the top-left corner where its lettering started.
            var k = b.size.y * LetterHeight / (2f + LineGap);
            root.transform.position = new Vector3(b.min.x + b.size.x * LetterLeft, b.max.y - b.size.y * LetterTop, b.center.z - 0.01f);
            root.transform.rotation = logo.transform.rotation;
            root.transform.localScale = Vector3.one * k;
            root.transform.SetParent(logo.transform.parent, true); // moves with the menu from now on
            logo.enabled = false;
            Net.NetLog.Info("Branding: replaced " + logo.name);
            return new Title { Root = root, Logo = logo, Ultra = letters.ToArray(), Puck = puck };
        }

        private static SpriteRenderer AddSprite(GameObject root, Sprite sprite, Vector3 at, Renderer like)
        {
            var go = new GameObject(sprite.texture.name);
            go.layer = root.layer;
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = at;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingLayerID = like.sortingLayerID;
            sr.sortingOrder = like.sortingOrder;
            return sr;
        }

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
                default: return i % 6 == 4 ? new Color(t, p, v) : new Color(v, p, q);
            }
        }
    }
}
