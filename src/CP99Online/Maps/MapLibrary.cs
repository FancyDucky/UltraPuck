using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CP99Online.Net;

namespace CP99Online.Maps
{
    /// <summary>
    /// UltraPuck maps on disk ("UltraPuck Maps" in the game folder) and how they plug into the game's custom arenas.
    /// The game plays custom arenas as "0000_Custom" with ArenaCreatorManager.currentArena set; for a .upmap that is a
    /// small empty stand-in CustomArena, and our CustomArenaSpawner patches build the real map from the MapData behind
    /// it. Old Arena Creator maps go through the same builder (converted from tiles), so they also work online.
    /// </summary>
    internal static class MapLibrary
    {
        internal const string Extension = ".upmap";

        internal static string Folder { get { return Path.Combine(BepInEx.Paths.GameRootPath, "UltraPuck Maps"); } }
        internal static string DownloadFolder { get { return Path.Combine(Folder, "Downloaded"); } }
        internal static string OldMapsFolder { get { return Path.Combine(Folder, "From Arena Creator"); } }

        private static readonly Dictionary<CustomArena, MapData> _maps = new Dictionary<CustomArena, MapData>();
        /// <summary>Converted tile arenas, with a fingerprint of their tiles (the Arena Creator edits arenas in place).</summary>
        private static readonly Dictionary<CustomArena, KeyValuePair<uint, MapData>> _converted = new Dictionary<CustomArena, KeyValuePair<uint, MapData>>();

        /// <summary>Client: the map the host sent for the next match.</summary>
        internal static MapData Incoming;

        /// <summary>The map to build for a custom arena: ours, or the old tile arena converted.</summary>
        internal static MapData MapFor(CustomArena arena)
        {
            if (arena == null) return null;
            MapData m;
            if (_maps.TryGetValue(arena, out m)) return m;
            if (arena.tileArray == null || TestHarness.OriginalTiles) return null;
            var sig = Fingerprint(arena);
            KeyValuePair<uint, MapData> c;
            if (_converted.TryGetValue(arena, out c) && c.Key == sig) return c.Value;
            m = FromTiles(arena);
            if (_converted.Count > 64) _converted.Clear();
            _converted[arena] = new KeyValuePair<uint, MapData>(sig, m);
            return m;
        }

        private static uint Fingerprint(CustomArena a)
        {
            uint h = 2166136261;
            var t = a.tileArray;
            foreach (var tile in t)
            {
                var v = tile == null ? 255u : (uint)tile.type | (uint)tile.color << 8 | (uint)tile.direction << 16;
                h = (h ^ v) * 16777619;
            }
            return h ^ (uint)a.gradient ^ (uint)(a.arenaName ?? string.Empty).GetHashCode();
        }

        /// <summary>A stand-in CustomArena for a map (what the level list and GameManager pass around).</summary>
        internal static CustomArena StandIn(MapData map, int index)
        {
            var a = new CustomArena(3, index)
            {
                arenaName = map.Name,
                arenaTagline = map.Tagline,
                gradient = map.Background,
                minPlayers = map.MinPlayers,
                maxPlayers = map.MaxPlayers,
                difficulty = map.Difficulty,
                published = true,
            };
            for (var x = 0; x < 3; x++)
                for (var y = 0; y < 3; y++)
                    a.tileArray[x, y] = new ArenaTile(ArenaTile.TileType.EMPTY, ArenaTile.TileColor.WHITE, 0, ArenaTile.Direction.RIGHT);
            _maps[a] = map;
            return a;
        }

        // ------------------------------------------------------------------ files

        internal static MapData Load(string path)
        {
            return MapData.Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        /// <summary>Maps in the main folder, sorted by name (bad files are logged and skipped).</summary>
        internal static List<MapData> LoadFolder()
        {
            var list = new List<MapData>();
            try
            {
                if (!Directory.Exists(Folder)) Directory.CreateDirectory(Folder);
                var files = Directory.GetFiles(Folder, "*" + Extension);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    try { list.Add(Load(f)); }
                    catch (Exception e) { NetLog.Warn("Maps: can't read " + Path.GetFileName(f) + ": " + e.Message); }
                }
            }
            catch (Exception e) { NetLog.Warn("Maps: can't read the maps folder: " + e.Message); }
            return list;
        }

        internal static string SafeFileName(string name)
        {
            var sb = new StringBuilder();
            foreach (var ch in (name ?? string.Empty).Trim())
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
            var s = sb.ToString().Trim('.', ' ');
            return s.Length == 0 ? "Map" : s;
        }

        /// <summary>Writes the map into a folder (overwriting a file of the same name only if it changed).</summary>
        internal static string Save(MapData map, string folder)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, SafeFileName(map.Name) + Extension);
            var text = map.Write();
            if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != text)
                File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        // ------------------------------------------------------------------ the game's level list

        /// <summary>LevelList.ListLevels (via transpiler): our maps go first in the "Custom" section.</summary>
        internal static void AddToLevelList(List<CustomArena> list)
        {
            try
            {
                ExportOldMaps();
                var maps = LoadFolder();
                for (var i = 0; i < maps.Count; i++) list.Insert(i, StandIn(maps[i], 9000 + i));
                if (maps.Count > 0) NetLog.Info("Maps: " + maps.Count + " UltraPuck map(s) in the arena list");
            }
            catch (Exception e) { NetLog.Warn("Maps: listing failed: " + e); }
        }

        /// <summary>Copies the game's own Arena Creator maps out as .upmap files, so the map maker can open them.</summary>
        private static void ExportOldMaps()
        {
            var dm = DataManager.Instance;
            var mm = MenuGlobals.menuManager;
            if (dm == null || mm == null || mm.customArenaSaveLoad == null) return;
            int num;
            if (!int.TryParse(dm.GetString("NumCustomLevels", "0"), out num)) return;
            var n = 0;
            for (var i = 0; i <= num; i++)
            {
                if (!dm.HasKey("CustomLevel" + i)) continue;
                try
                {
                    var arena = mm.customArenaSaveLoad.LoadArena(i);
                    Save(FromTiles(arena), OldMapsFolder);
                    n++;
                }
                catch (Exception e) { NetLog.Warn("Maps: can't export Arena Creator map " + i + ": " + e.Message); }
            }
            if (n > 0) NetLog.Info("Maps: " + n + " Arena Creator map(s) copied to " + OldMapsFolder);
        }

        // ------------------------------------------------------------------ old tile maps

        /// <summary>Converts an Arena Creator tile map. Tile (x, y) covers x - size/2 +- 0.5, y - size/2 +- 0.5.</summary>
        internal static MapData FromTiles(CustomArena a)
        {
            var m = new MapData
            {
                Name = a.arenaName,
                Tagline = a.arenaTagline,
                Background = a.gradient,
                MinPlayers = a.minPlayers,
                MaxPlayers = a.maxPlayers,
                Difficulty = a.difficulty,
            };
            var t = a.tileArray;
            var n = t.GetLength(0);
            var half = n / 2f;
            Func<int, float> x0 = x => x - half - 0.5f;
            Func<int, float> y0 = y => y - half - 0.5f;

            // full floor tiles (incl. tiles with an object, and ground under a diagonal wall), then walls, as row runs
            for (var pass = 0; pass < 2; pass++)
            {
                for (var y = 1; y < n - 1; y++)
                {
                    var start = -1; var color = -1;
                    for (var x = 1; x <= n - 1; x++)
                    {
                        var tile = x < n - 1 ? t[x, y] : null;
                        var c = tile != null && (pass == 0 ? FullFloor(tile.type) : tile.type == ArenaTile.TileType.WALL) ? (int)tile.color : -1;
                        if (c == color) continue;
                        if (color >= 0)
                            m.Shapes.Add(new MapShape
                            {
                                Op = pass == 0 ? MapOp.Floor : MapOp.Wall, Color = color, Kind = ShapeKind.Rect,
                                P = new[] { x0(start), y0(y), x0(x), y0(y) + 1f },
                            });
                        start = x; color = c;
                    }
                }
            }
            // diagonals
            for (var y = 1; y < n - 1; y++)
            {
                for (var x = 1; x < n - 1; x++)
                {
                    var tile = t[x, y];
                    if (tile == null) continue;
                    var ty = tile.type;
                    MapOp op;
                    if (ty == ArenaTile.TileType.ANGLEGROUND) op = MapOp.Floor;
                    else if (ty == ArenaTile.TileType.ANGLEWALL || ty == ArenaTile.TileType.ANGLEWALLANDGROUND) op = MapOp.Wall;
                    else continue;
                    m.Shapes.Add(new MapShape { Op = op, Color = (int)tile.color, Kind = ShapeKind.Poly, P = Triangle(tile.direction, x0(x), y0(y)) });
                }
            }
            // objects
            for (var y = 1; y < n - 1; y++)
            {
                for (var x = 1; x < n - 1; x++)
                {
                    var tile = t[x, y];
                    if (tile == null) continue;
                    var o = new MapObject { X = x - half, Y = y - half };
                    switch (tile.type)
                    {
                        case ArenaTile.TileType.GOAL1: o.Kind = ObjKind.Goal; o.Team = 1; break;
                        case ArenaTile.TileType.GOAL2: o.Kind = ObjKind.Goal; o.Team = 2; break;
                        case ArenaTile.TileType.SPAWN1: o.Kind = ObjKind.Spawn; o.Team = 1; break;
                        case ArenaTile.TileType.SPAWN2: o.Kind = ObjKind.Spawn; o.Team = 2; break;
                        case ArenaTile.TileType.SPEEDBOOST: o.Kind = ObjKind.Boost; break;
                        case ArenaTile.TileType.BUMPER: o.Kind = ObjKind.Bumper; break;
                        case ArenaTile.TileType.SPIKE: o.Kind = ObjKind.Spike; break;
                        default: continue;
                    }
                    m.Objects.Add(o);
                }
            }
            return m;
        }

        private static bool FullFloor(ArenaTile.TileType t)
        {
            switch (t)
            {
                case ArenaTile.TileType.GROUND:
                case ArenaTile.TileType.ANGLEWALLANDGROUND:
                case ArenaTile.TileType.SPIKE:
                case ArenaTile.TileType.BUMPER:
                case ArenaTile.TileType.SPEEDBOOST:
                case ArenaTile.TileType.GOAL1:
                case ArenaTile.TileType.GOAL2:
                case ArenaTile.TileType.SPAWN1:
                case ArenaTile.TileType.SPAWN2:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The filled half of a diagonal tile (matches CustomArenaSpawner's triangles).</summary>
        private static float[] Triangle(ArenaTile.Direction d, float x, float y)
        {
            float[] uv;
            switch (d)
            {
                case ArenaTile.Direction.LEFT: uv = new float[] { 0, 1, 1, 1, 1, 0 }; break;
                case ArenaTile.Direction.UP: uv = new float[] { 0, 0, 1, 1, 1, 0 }; break;
                case ArenaTile.Direction.RIGHT: uv = new float[] { 0, 0, 0, 1, 1, 0 }; break;
                default: uv = new float[] { 0, 0, 0, 1, 1, 1 }; break;
            }
            for (var i = 0; i < 6; i += 2) { uv[i] += x; uv[i + 1] += y; }
            return uv;
        }
    }
}
