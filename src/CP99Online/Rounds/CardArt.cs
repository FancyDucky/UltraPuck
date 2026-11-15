using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>
    /// Card pictures, square, by card id. Built-in cards' art is inside the DLL (Assets/cards, made from art/cards
    /// by art/convert-cards.py), so it arrives with every update. Any card, including other mods' cards, can also
    /// have a PNG or JPG named after its id in BepInEx/plugins/CP99Online/CardArt (e.g. yourname.bigshot.png),
    /// which wins over the built-in one. No picture: the card shows a panel in its colour.
    /// </summary>
    internal static class CardArt
    {
        private static readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();

        internal static Texture2D Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Texture2D tex;
            if (_cache.TryGetValue(id, out tex)) return tex;
            try { tex = FromFolder(id) ?? FromDll(id); }
            catch (System.Exception e) { Plugin.Logger_.LogWarning("Card art for " + id + " failed to load: " + e.Message); tex = null; }
            _cache[id] = tex;
            return tex;
        }

        private static Texture2D FromFolder(string id)
        {
            var dir = Path.Combine(Path.GetDirectoryName(typeof(CardArt).Assembly.Location) ?? ".", "CardArt");
            if (!Directory.Exists(dir)) return null;
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg" })
            {
                var path = Path.Combine(dir, id + ext);
                if (File.Exists(path)) return Make(File.ReadAllBytes(path), id);
            }
            return null;
        }

        private static Texture2D FromDll(string id)
        {
            using (var s = typeof(CardArt).Assembly.GetManifestResourceStream("UltraPuck.card." + id + ".jpg"))
            {
                if (s == null) return null;
                var bytes = new byte[s.Length];
                var read = 0;
                while (read < bytes.Length) { var n = s.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                return Make(bytes, id);
            }
        }

        private static Texture2D Make(byte[] bytes, string id)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, true);
            if (!tex.LoadImage(bytes)) return null;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            tex.name = "CardArt " + id;
            return tex;
        }
    }
}
