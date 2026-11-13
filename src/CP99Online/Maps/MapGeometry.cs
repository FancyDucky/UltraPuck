using System;
using System.Collections.Generic;

namespace CP99Online.Maps
{
    // Plain C# (no Unity) so the Windows map maker can draw the same thing the game builds.

    internal struct V3
    {
        public float X, Y, Z;
        public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    /// <summary>A triangle or quad of the arena mesh, already wound for the game's top-down camera.</summary>
    internal struct Face
    {
        public int Sub;   // submesh: 0-11 floor colours, 12-23 wall colours (same as the game's CustomArenaSpawner)
        public int Count; // 3 or 4
        public V3 A, B, C, D;
        public V2 UA, UB, UC, UD;
    }

    /// <summary>An axis-aligned box in map units (collider pieces).</summary>
    internal struct Box
    {
        public float X0, Y0, X1, Y1;
        public Box(float x0, float y0, float x1, float y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
    }

    /// <summary>
    /// Turns a MapData into arena geometry. Every shape is a signed distance field; shapes paint over each other in
    /// order (union / subtract). The fields are sampled on a fine grid and traced with marching squares, so straight
    /// edges stay straight and curves come out smooth. Outputs mesh faces (game look: floor top at z 0, sides down to
    /// z 1; wall tops at z -1), outlines for edge colliders and box pieces for filled colliders.
    /// </summary>
    internal sealed class MapGeometry
    {
        public const float Cell = 0.125f;
        private const float Big = 1e6f;
        private const float Cull = 4 * Cell;   // how far past a shape's box its field still matters
        private const float Margin = 3f;       // space around the map (forcefield ring is 2 units)
        private const int ForcefieldCells = 16; // 2 units

        public readonly List<Face> Faces = new List<Face>();
        /// <summary>Closed outlines of all floor (incl. under walls): the forcefield the puck bounces off.</summary>
        public readonly List<List<V2>> FloorOutlines = new List<List<V2>>();
        /// <summary>Closed outlines of the walls: what players and the puck bounce off.</summary>
        public readonly List<List<V2>> WallOutlines = new List<List<V2>>();
        /// <summary>Boxes covering the floor (incl. under walls): "Ground" for falling checks.</summary>
        public readonly List<Box> GroundBoxes = new List<Box>();
        /// <summary>Boxes filling the inside of walls, a little in from the edge (stops anything getting inside).</summary>
        public readonly List<Box> WallFill = new List<Box>();
        /// <summary>Boxes in a ring around the floor, a little out from the edge (keeps the puck in).</summary>
        public readonly List<Box> ForcefieldFill = new List<Box>();

        public float MinX, MinY, MaxX, MaxY; // map extent (floor and walls)

        private int _w, _h;    // samples
        private float _bx, _by; // sample (i, j) is at _bx + (i + OX) * Cell, _by + (j + OY) * Cell
        // Off the half-unit grid (edges never land on samples), and OY != OX so 45 degree diagonals miss them too.
        private const float OX = 0.5f, OY = 0.3f;

        private List<MapShape> _shapes;
        private Box[] _bounds;

        private float[] _floorT, _wallT;
        private readonly float[][] _floorC = new float[MapData.Colors][];
        private readonly float[][] _wallC = new float[MapData.Colors][];

        public static MapGeometry Build(MapData map)
        {
            var g = new MapGeometry();
            g.Run(map);
            return g;
        }

        private float SX(int i) { return _bx + (i + OX) * Cell; }
        private float SY(int j) { return _by + (j + OY) * Cell; }

        private void Run(MapData map)
        {
            // ---- extent
            MinX = MinY = float.MaxValue; MaxX = MaxY = float.MinValue;
            foreach (var s in map.Shapes)
            {
                if (s.Op != MapOp.Floor && s.Op != MapOp.Wall) continue;
                var b = Bounds(s);
                MinX = Math.Min(MinX, b.X0); MinY = Math.Min(MinY, b.Y0);
                MaxX = Math.Max(MaxX, b.X1); MaxY = Math.Max(MaxY, b.Y1);
            }
            if (MinX > MaxX) { MinX = MinY = -1; MaxX = MaxY = 1; }
            _bx = (float)Math.Floor((MinX - Margin) * 2f) / 2f;
            _by = (float)Math.Floor((MinY - Margin) * 2f) / 2f;
            _w = (int)Math.Ceiling((MaxX + Margin - _bx) / Cell);
            _h = (int)Math.Ceiling((MaxY + Margin - _by) / Cell);
            var n = _w * _h;
            if (n > 4000000) throw new InvalidOperationException("map is too big (" + (MaxX - MinX) + " x " + (MaxY - MinY) + ")");

            // ---- fields
            _floorT = Filled(n); _wallT = Filled(n);
            var usedFloor = new bool[MapData.Colors];
            var usedWall = new bool[MapData.Colors];
            foreach (var s in map.Shapes)
            {
                if (s.Op == MapOp.Floor) usedFloor[s.Color] = true;
                if (s.Op == MapOp.Wall) { usedWall[s.Color] = true; usedFloor[s.Color] = true; }
            }
            for (var c = 0; c < MapData.Colors; c++)
            {
                if (usedFloor[c]) _floorC[c] = Filled(n);
                if (usedWall[c]) _wallC[c] = Filled(n);
            }
            _shapes = map.Shapes;
            _bounds = new Box[_shapes.Count];
            for (var i = 0; i < _shapes.Count; i++) _bounds[i] = Bounds(_shapes[i]);
            foreach (var s in map.Shapes) Paint(s);

            // ---- mesh
            for (var c = 0; c < MapData.Colors; c++)
            {
                if (_floorC[c] != null) { NewField(); March(_floorC[c], c, false); }
                if (_wallC[c] != null) { NewField(); March(_wallC[c], c + 12, true); }
            }

            FillCracks(_floorC, _floorT, 0, 0f);
            FillCracks(_wallC, _wallT, 12, -1f);

            // ---- colliders
            NewField();
            Outlines(_floorT, 0, -1, FloorOutlines);
            NewField();
            Outlines(_wallT, 1, -1, WallOutlines);
            NewField();
            var ground = new bool[n];
            var wallIn = new bool[n];
            for (var i = 0; i < n; i++) { ground[i] = _floorT[i] < 0f; wallIn[i] = _wallT[i] < -Cell; }
            Boxes(ground, GroundBoxes);
            Boxes(wallIn, WallFill);
            Boxes(Ring(ground), ForcefieldFill);
        }

        private static float[] Filled(int n)
        {
            var a = new float[n];
            for (var i = 0; i < n; i++) a[i] = Big;
            return a;
        }

        // ------------------------------------------------------------------ shapes

        internal static Box Bounds(MapShape s)
        {
            var b = RawBounds(s);
            foreach (var k in s.Keeps)
            {
                switch (k.Side)
                {
                    case KeepSide.Left: b.X1 = Math.Min(b.X1, k.V); break;
                    case KeepSide.Right: b.X0 = Math.Max(b.X0, k.V); break;
                    case KeepSide.Below: b.Y1 = Math.Min(b.Y1, k.V); break;
                    case KeepSide.Above: b.Y0 = Math.Max(b.Y0, k.V); break;
                }
            }
            if (b.X1 < b.X0) b.X1 = b.X0;
            if (b.Y1 < b.Y0) b.Y1 = b.Y0;
            return b;
        }

        private static Box RawBounds(MapShape s)
        {
            var p = s.P;
            switch (s.Kind)
            {
                case ShapeKind.Rect:
                    return new Box(Math.Min(p[0], p[2]), Math.Min(p[1], p[3]), Math.Max(p[0], p[2]), Math.Max(p[1], p[3]));
                case ShapeKind.Circle:
                    return new Box(p[0] - p[2], p[1] - p[2], p[0] + p[2], p[1] + p[2]);
                case ShapeKind.Arc:
                {
                    float r = Math.Abs(p[2]), half = Math.Abs(p[5]) / 2f;
                    double a0 = Math.Min(p[3], p[4]), sweep = Math.Abs(p[4] - p[3]);
                    if (sweep >= 360.0) return new Box(p[0] - r - half, p[1] - r - half, p[0] + r + half, p[1] + r + half);
                    // just the part of the circle the arc covers (a shallow arc has a huge radius): its ends and
                    // any of the four extreme points inside the sweep
                    var b = new Box(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                    for (var i = 0; i < 6; i++)
                    {
                        double a;
                        if (i == 0) a = a0;
                        else if (i == 1) a = a0 + sweep;
                        else
                        {
                            a = 90.0 * (i - 2);
                            if (((a - a0) % 360.0 + 360.0) % 360.0 > sweep) continue;
                        }
                        var x = p[0] + r * (float)Math.Cos(a * Math.PI / 180.0);
                        var y = p[1] + r * (float)Math.Sin(a * Math.PI / 180.0);
                        b.X0 = Math.Min(b.X0, x); b.X1 = Math.Max(b.X1, x);
                        b.Y0 = Math.Min(b.Y0, y); b.Y1 = Math.Max(b.Y1, y);
                    }
                    b.X0 -= half; b.Y0 -= half; b.X1 += half; b.Y1 += half;
                    return b;
                }
                case ShapeKind.Tiles:
                {
                    var b = new Box(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                    for (var i = 0; i + 2 < p.Length; i += 3)
                    {
                        b.X0 = Math.Min(b.X0, p[i]); b.X1 = Math.Max(b.X1, p[i] + 1f);
                        b.Y0 = Math.Min(b.Y0, p[i + 1]); b.Y1 = Math.Max(b.Y1, p[i + 1] + 1f);
                    }
                    return b;
                }
                default:
                {
                    var b = new Box(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                    for (var i = 0; i + 1 < p.Length; i += 2)
                    {
                        b.X0 = Math.Min(b.X0, p[i]); b.X1 = Math.Max(b.X1, p[i]);
                        b.Y0 = Math.Min(b.Y0, p[i + 1]); b.Y1 = Math.Max(b.Y1, p[i + 1]);
                    }
                    return b;
                }
            }
        }

        /// <summary>Signed distance from (x, y) to the shape: negative inside.</summary>
        internal static float Distance(MapShape s, float x, float y)
        {
            var d = RawDistance(s, x, y);
            foreach (var k in s.Keeps)
            {
                switch (k.Side)
                {
                    case KeepSide.Left: d = Math.Max(d, x - k.V); break;
                    case KeepSide.Right: d = Math.Max(d, k.V - x); break;
                    case KeepSide.Below: d = Math.Max(d, y - k.V); break;
                    case KeepSide.Above: d = Math.Max(d, k.V - y); break;
                }
            }
            return d;
        }

        private static float RawDistance(MapShape s, float x, float y)
        {
            var p = s.P;
            switch (s.Kind)
            {
                case ShapeKind.Rect:
                {
                    var cx = (p[0] + p[2]) / 2f; var cy = (p[1] + p[3]) / 2f;
                    var hx = Math.Abs(p[2] - p[0]) / 2f; var hy = Math.Abs(p[3] - p[1]) / 2f;
                    var r = p.Length > 4 ? Math.Max(0f, Math.Min(p[4], Math.Min(hx, hy))) : 0f;
                    var qx = Math.Abs(x - cx) - hx + r; var qy = Math.Abs(y - cy) - hy + r;
                    var ox = Math.Max(qx, 0f); var oy = Math.Max(qy, 0f);
                    return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0f) - r;
                }
                case ShapeKind.Circle:
                {
                    var dx = x - p[0]; var dy = y - p[1];
                    return (float)Math.Sqrt(dx * dx + dy * dy) - Math.Abs(p[2]);
                }
                case ShapeKind.Arc:
                {
                    float cx = p[0], cy = p[1], r = Math.Abs(p[2]), half = Math.Abs(p[5]) / 2f;
                    var sweep = p[4] - p[3];
                    var dx = x - cx; var dy = y - cy;
                    var dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (Math.Abs(sweep) >= 360f) return Math.Abs(dist - r) - half;
                    double a0 = p[3], a1 = p[4];
                    if (sweep < 0) { var t = a0; a0 = a1; a1 = t; sweep = -sweep; }
                    var ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                    var rel = ((ang - a0) % 360.0 + 360.0) % 360.0;
                    if (rel <= sweep) return Math.Abs(dist - r) - half;
                    // past an end: distance to the nearer end point (round ends)
                    var e0x = cx + r * (float)Math.Cos(a0 * Math.PI / 180.0); var e0y = cy + r * (float)Math.Sin(a0 * Math.PI / 180.0);
                    var e1x = cx + r * (float)Math.Cos(a1 * Math.PI / 180.0); var e1y = cy + r * (float)Math.Sin(a1 * Math.PI / 180.0);
                    var d0 = (float)Math.Sqrt((x - e0x) * (x - e0x) + (y - e0y) * (y - e0y));
                    var d1 = (float)Math.Sqrt((x - e1x) * (x - e1x) + (y - e1y) * (y - e1y));
                    return Math.Min(d0, d1) - half;
                }
                case ShapeKind.Tiles:
                    return TileSet.Of(p).Distance(x, y);
                default:
                {
                    // polygon (Inigo Quilez's sdPolygon)
                    var cnt = p.Length / 2;
                    var d = (x - p[0]) * (x - p[0]) + (y - p[1]) * (y - p[1]);
                    var sign = 1f;
                    for (int i = 0, j = cnt - 1; i < cnt; j = i, i++)
                    {
                        float vix = p[2 * i], viy = p[2 * i + 1], vjx = p[2 * j], vjy = p[2 * j + 1];
                        float ex = vjx - vix, ey = vjy - viy, wx = x - vix, wy = y - viy;
                        var ee = ex * ex + ey * ey;
                        var t = ee > 0f ? Math.Max(0f, Math.Min(1f, (wx * ex + wy * ey) / ee)) : 0f;
                        float bx = wx - ex * t, by = wy - ey * t;
                        d = Math.Min(d, bx * bx + by * by);
                        bool c1 = y >= viy, c2 = y < vjy, c3 = ex * wy > ey * wx;
                        if ((c1 && c2 && c3) || (!c1 && !c2 && !c3)) sign = -sign;
                    }
                    return sign * (float)Math.Sqrt(d);
                }
            }
        }

        /// <summary>
        /// A Tiles shape, indexed by square: the signed distance to the edge of all its tiles together (exact within
        /// two tiles of the edge, clamped beyond, which is all the outlines and colliders need).
        /// </summary>
        private sealed class TileSet
        {
            private const int Reach = 2;
            private const float Far = Reach + 0.5f;
            private readonly Dictionary<long, int> _cells = new Dictionary<long, int>();
            private readonly float _ox, _oy; // the tiles' grid offset (their squares start at whole numbers + this)

            private static readonly Dictionary<float[], TileSet> Cache = new Dictionary<float[], TileSet>();

            internal static TileSet Of(float[] p)
            {
                TileSet t;
                lock (Cache)
                {
                    if (Cache.TryGetValue(p, out t)) return t;
                    if (Cache.Count > 64) Cache.Clear();
                    t = new TileSet(p);
                    Cache[p] = t;
                }
                return t;
            }

            private TileSet(float[] p)
            {
                if (p.Length >= 3) { _ox = p[0] - (float)Math.Floor(p[0]); _oy = p[1] - (float)Math.Floor(p[1]); }
                for (var i = 0; i + 2 < p.Length; i += 3)
                {
                    var cx = (int)Math.Round(p[i] - _ox); var cy = (int)Math.Round(p[i + 1] - _oy);
                    _cells[Key(cx, cy)] = Math.Max(0, Math.Min(4, (int)p[i + 2])); // a later tile on the same square wins
                }
            }

            private static long Key(int x, int y) { return ((long)x << 32) ^ (uint)y; }

            private int At(int x, int y) { int t; return _cells.TryGetValue(Key(x, y), out t) ? t : -1; }

            internal float Distance(float x, float y)
            {
                var u = x - _ox; var v = y - _oy;
                int ci = (int)Math.Floor(u), cj = (int)Math.Floor(v);
                var inside = Covers(At(ci, cj), u - ci, v - cj);
                var best = Far;
                for (var dj = -Reach; dj <= Reach; dj++)
                    for (var di = -Reach; di <= Reach; di++)
                    {
                        int i = ci + di, j = cj + dj;
                        var t = At(i, j);
                        // inside: the nearest part of a square that isn't covered; outside: the nearest covered part
                        int part;
                        if (inside) { if (t == 0) continue; part = t < 0 ? 0 : Complement(t); }
                        else { if (t < 0) continue; part = t; }
                        best = Math.Min(best, PartDistance(part, u - i, v - j));
                    }
                return inside ? -best : best;
            }

            private static int Complement(int t) { return t == 1 ? 3 : t == 3 ? 1 : t == 2 ? 4 : 2; }

            /// <summary>Is the local point (0..1 in the square) in this tile? -1 = no tile.</summary>
            private static bool Covers(int t, float u, float v)
            {
                switch (t)
                {
                    case 0: return true;
                    case 1: return u + v <= 1f;
                    case 2: return u >= v;
                    case 3: return u + v >= 1f;
                    case 4: return v >= u;
                    default: return false;
                }
            }

            // corners of each part, square 0..1: full, then the four triangles
            private static readonly float[][] Parts =
            {
                new[] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f },
                new[] { 0f, 0f, 1f, 0f, 0f, 1f },
                new[] { 0f, 0f, 1f, 0f, 1f, 1f },
                new[] { 1f, 0f, 1f, 1f, 0f, 1f },
                new[] { 0f, 0f, 1f, 1f, 0f, 1f },
            };

            /// <summary>Distance from a local point to a part (0 if it's in it).</summary>
            private static float PartDistance(int part, float u, float v)
            {
                if (Covers(part, u, v) && u >= 0f && u <= 1f && v >= 0f && v <= 1f) return 0f;
                var c = Parts[part];
                var n = c.Length / 2;
                var d = float.MaxValue;
                for (int i = 0, j = n - 1; i < n; j = i, i++)
                {
                    float ax = c[2 * j], ay = c[2 * j + 1], ex = c[2 * i] - ax, ey = c[2 * i + 1] - ay;
                    float wx = u - ax, wy = v - ay;
                    var t = Math.Max(0f, Math.Min(1f, (wx * ex + wy * ey) / (ex * ex + ey * ey)));
                    float bx = wx - ex * t, by = wy - ey * t;
                    d = Math.Min(d, bx * bx + by * by);
                }
                return (float)Math.Sqrt(d);
            }
        }

        private void Paint(MapShape s)
        {
            var b = Bounds(s);
            var i0 = Math.Max(0, (int)Math.Floor((b.X0 - Cull - _bx) / Cell));
            var i1 = Math.Min(_w - 1, (int)Math.Ceiling((b.X1 + Cull - _bx) / Cell));
            var j0 = Math.Max(0, (int)Math.Floor((b.Y0 - Cull - _by) / Cell));
            var j1 = Math.Min(_h - 1, (int)Math.Ceiling((b.Y1 + Cull - _by) / Cell));
            for (var j = j0; j <= j1; j++)
            {
                var y = SY(j);
                for (var i = i0; i <= i1; i++)
                {
                    var k = j * _w + i;
                    var d = Distance(s, SX(i), y);
                    switch (s.Op)
                    {
                        case MapOp.Floor:
                            Add(_floorC, s.Color, k, d); _floorT[k] = Math.Min(_floorT[k], d);
                            Cut(_wallC, k, d); _wallT[k] = Math.Max(_wallT[k], -d);
                            break;
                        case MapOp.Wall:
                            Add(_floorC, s.Color, k, d); _floorT[k] = Math.Min(_floorT[k], d);
                            Add(_wallC, s.Color, k, d); _wallT[k] = Math.Min(_wallT[k], d);
                            break;
                        case MapOp.Hole:
                            Cut(_floorC, k, d); _floorT[k] = Math.Max(_floorT[k], -d);
                            Cut(_wallC, k, d); _wallT[k] = Math.Max(_wallT[k], -d);
                            break;
                        case MapOp.Unwall:
                            Cut(_wallC, k, d); _wallT[k] = Math.Max(_wallT[k], -d);
                            break;
                    }
                }
            }
        }

        /// <summary>This colour gains the shape; every other colour loses it.</summary>
        private static void Add(float[][] fields, int color, int k, float d)
        {
            for (var c = 0; c < fields.Length; c++)
            {
                var f = fields[c];
                if (f == null) continue;
                f[k] = c == color ? Math.Min(f[k], d) : Math.Max(f[k], -d);
            }
        }

        private static void Cut(float[][] fields, int k, float d)
        {
            for (var c = 0; c < fields.Length; c++)
                if (fields[c] != null) fields[c][k] = Math.Max(fields[c][k], -d);
        }

        // ------------------------------------------------------------------ marching squares

        private struct Pt
        {
            public float X, Y;
            public int Key; // -1 = cell corner, -2 = sharp corner of the outline inside the cell, else the grid edge this crossing is on
        }

        private int HKey(int i, int j) { return (j * _w + i) * 2; }     // edge (i,j)-(i+1,j)
        private int VKey(int i, int j) { return (j * _w + i) * 2 + 1; } // edge (i,j)-(i,j+1)

        /// <summary>
        /// Calls back each cell's inside polygon (counter-clockwise) for cells that are partly inside, and returns
        /// which cells are fully inside. Polygon edges between two crossing points are the outline, inside on the left.
        /// </summary>
        private bool[] Cells(float[] f, int layer, int color, Action<List<Pt>> partial)
        {
            var full = new bool[(_w - 1) * (_h - 1)];
            var poly = new List<Pt>(8);
            var cx = new float[4]; var cy = new float[4]; var v = new float[4]; var key = new int[4];
            for (var j = 0; j < _h - 1; j++)
            {
                for (var i = 0; i < _w - 1; i++)
                {
                    v[0] = f[j * _w + i]; v[1] = f[j * _w + i + 1]; v[2] = f[(j + 1) * _w + i + 1]; v[3] = f[(j + 1) * _w + i];
                    var mask = (v[0] < 0f ? 1 : 0) | (v[1] < 0f ? 2 : 0) | (v[2] < 0f ? 4 : 0) | (v[3] < 0f ? 8 : 0);
                    if (mask == 0) continue;
                    if (mask == 15) { full[j * (_w - 1) + i] = true; continue; }
                    cx[0] = SX(i); cy[0] = SY(j); cx[1] = SX(i + 1); cy[1] = cy[0];
                    cx[2] = cx[1]; cy[2] = SY(j + 1); cx[3] = cx[0]; cy[3] = cy[2];
                    key[0] = HKey(i, j); key[1] = VKey(i + 1, j); key[2] = HKey(i, j + 1); key[3] = VKey(i, j);

                    var saddle = mask == 5 || mask == 10;
                    if (saddle && (v[0] + v[1] + v[2] + v[3]) / 4f >= 0f)
                    {
                        // two separate corners
                        var a = mask == 5 ? 0 : 1;
                        for (var t = 0; t < 2; t++, a += 2)
                        {
                            poly.Clear();
                            var prev = (a + 3) % 4;
                            poly.Add(new Pt { X = cx[a], Y = cy[a], Key = -1 });
                            poly.Add(Exact(cx, cy, v, key, a, (a + 1) % 4, a, layer, color));
                            poly.Add(Exact(cx, cy, v, key, prev, a, prev, layer, color));
                            Sharpen(poly, layer, color, cx[0], cy[0]);
                            partial(poly);
                        }
                        continue;
                    }
                    poly.Clear();
                    for (var k = 0; k < 4; k++)
                    {
                        var k2 = (k + 1) % 4;
                        if (v[k] < 0f) poly.Add(new Pt { X = cx[k], Y = cy[k], Key = -1 });
                        if ((v[k] < 0f) != (v[k2] < 0f)) poly.Add(Exact(cx, cy, v, key, k, k2, k, layer, color));
                    }
                    if (!saddle) Sharpen(poly, layer, color, cx[0], cy[0]);
                    partial(poly);
                }
            }
            return full;
        }

        // ------------------------------------------------------------------ sharp corners

        private readonly Dictionary<int, V2> _grad = new Dictionary<int, V2>(); // per field: outline normal at a crossing
        private readonly Dictionary<int, Pt> _cross = new Dictionary<int, Pt>(); // per field: exact crossing on a grid edge

        private void NewField() { _grad.Clear(); _cross.Clear(); }

        /// <summary>
        /// Where the outline crosses a grid edge. Straight interpolation of the samples is off near corners (the
        /// distance there is to the other side), so refine it on the exact shapes. Same answer from both cells.
        /// </summary>
        private Pt Exact(float[] cx, float[] cy, float[] v, int[] key, int a, int b, int edge, int layer, int color)
        {
            Pt p;
            if (_cross.TryGetValue(key[edge], out p)) return p;
            // always search from the lower-left end, so the result doesn't depend on which cell asks first
            float ax = cx[a], ay = cy[a], bx = cx[b], by = cy[b], va = v[a], vb = v[b];
            if (bx < ax || by < ay) { var t0 = ax; ax = bx; bx = t0; t0 = ay; ay = by; by = t0; t0 = va; va = vb; vb = t0; }
            float lo = 0f, hi = 1f, flo = va, fhi = vb;
            var t = flo / (flo - fhi);
            var side = 0;
            for (var it = 0; it < 12; it++)
            {
                var f = Eval(layer, color, ax + (bx - ax) * t, ay + (by - ay) * t);
                if (Math.Abs(f) < 1e-5f) break;
                if ((f < 0f) == (flo < 0f))
                {
                    lo = t; flo = f;
                    if (side == -1) fhi /= 2f;
                    side = -1;
                }
                else
                {
                    hi = t; fhi = f;
                    if (side == 1) flo /= 2f;
                    side = 1;
                }
                if (hi - lo < 1e-5f) break;
                t = lo + (hi - lo) * flo / (flo - fhi); // Illinois false position
            }
            p = new Pt { X = ax + (bx - ax) * t, Y = ay + (by - ay) * t, Key = key[edge] };
            _cross[key[edge]] = p;
            return p;
        }

        /// <summary>
        /// Marching squares cuts corners off. Where the outline turns inside a cell, find the true corner: the meeting
        /// point of the outline's tangents at the two crossings (from the exact shapes), and put it between them.
        /// </summary>
        private void Sharpen(List<Pt> poly, int layer, int color, float x0, float y0)
        {
            for (var k = 0; k < poly.Count; k++)
            {
                var a = poly[k]; var b = poly[(k + 1) % poly.Count];
                if (a.Key < 0 || b.Key < 0) continue;
                var na = Normal(a, layer, color); var nb = Normal(b, layer, color);
                var det = na.X * nb.Y - na.Y * nb.X;
                if (Math.Abs(det) < 0.25f) return; // nearly straight
                var ca = na.X * a.X + na.Y * a.Y; var cb = nb.X * b.X + nb.Y * b.Y;
                var fx = (ca * nb.Y - na.Y * cb) / det; var fy = (na.X * cb - ca * nb.X) / det;
                const float tol = 1e-4f;
                if (fx < x0 - tol || fx > x0 + Cell + tol || fy < y0 - tol || fy > y0 + Cell + tol) return;
                // the corner must stick out past the cut (inside is on the left of a -> b)
                var ex = b.X - a.X; var ey = b.Y - a.Y;
                if (ex * (fy - a.Y) - ey * (fx - a.X) > 0f) return;
                poly.Insert(k + 1, new Pt { X = fx, Y = fy, Key = -2 });
                return; // one corner per piece
            }
        }

        private V2 Normal(Pt p, int layer, int color)
        {
            V2 n;
            if (_grad.TryGetValue(p.Key, out n)) return n;
            const float e = 1e-3f;
            var gx = Eval(layer, color, p.X + e, p.Y) - Eval(layer, color, p.X - e, p.Y);
            var gy = Eval(layer, color, p.X, p.Y + e) - Eval(layer, color, p.X, p.Y - e);
            var len = (float)Math.Sqrt(gx * gx + gy * gy);
            n = len > 0f ? new V2(gx / len, gy / len) : new V2(0f, 0f);
            _grad[p.Key] = n;
            return n;
        }

        /// <summary>One field at any point: layer 0 floor / 1 wall, color -1 = all colours together.</summary>
        private float Eval(int layer, int color, float x, float y)
        {
            var v = Big;
            for (var i = 0; i < _shapes.Count; i++)
            {
                var b = _bounds[i];
                if (x < b.X0 - Cull || x > b.X1 + Cull || y < b.Y0 - Cull || y > b.Y1 + Cull) continue;
                var s = _shapes[i];
                var d = Distance(s, x, y);
                var mine = color < 0 || s.Color == color;
                switch (s.Op)
                {
                    case MapOp.Floor: v = layer == 0 ? (mine ? Math.Min(v, d) : Math.Max(v, -d)) : Math.Max(v, -d); break;
                    case MapOp.Wall: v = mine ? Math.Min(v, d) : Math.Max(v, -d); break;
                    case MapOp.Hole: v = Math.Max(v, -d); break;
                    case MapOp.Unwall: if (layer == 1) v = Math.Max(v, -d); break;
                }
            }
            return v;
        }

        /// <summary>Triangulates a small simple counter-clockwise polygon (ear clipping); triangles come out counter-clockwise.</summary>
        private static void EarClip(List<Pt> poly, Action<Pt, Pt, Pt> tri)
        {
            if (poly.Count == 3) { tri(poly[0], poly[1], poly[2]); return; }
            var idx = new List<int>();
            for (var i = 0; i < poly.Count; i++) idx.Add(i);
            var guard = 0;
            while (idx.Count > 3 && guard++ < 64)
            {
                var clipped = false;
                for (var i = 0; i < idx.Count; i++)
                {
                    int ia = (i + idx.Count - 1) % idx.Count, ic = (i + 1) % idx.Count;
                    var a = poly[idx[ia]]; var b = poly[idx[i]]; var c = poly[idx[ic]];
                    var cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                    if (cross <= 1e-9f) continue; // reflex or flat
                    var inside = false;
                    for (var j = 0; j < idx.Count && !inside; j++)
                    {
                        if (j == i || j == ia || j == ic) continue;
                        inside = InTri(poly[idx[j]], a, b, c);
                    }
                    if (inside) continue;
                    tri(a, b, c);
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            if (idx.Count == 3) tri(poly[idx[0]], poly[idx[1]], poly[idx[2]]);
            else for (var k = 1; k + 1 < idx.Count; k++) tri(poly[idx[0]], poly[idx[k]], poly[idx[k + 1]]); // fallback: fan
        }

        private static bool InTri(Pt p, Pt a, Pt b, Pt c)
        {
            var d1 = (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
            var d2 = (p.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (p.Y - c.Y);
            var d3 = (p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y);
            var neg = d1 < 0 || d2 < 0 || d3 < 0; var pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        private static Pt Cross(float[] cx, float[] cy, float[] v, int[] key, int a, int b, int edge)
        {
            var t = v[a] / (v[a] - v[b]);
            return new Pt { X = cx[a] + (cx[b] - cx[a]) * t, Y = cy[a] + (cy[b] - cy[a]) * t, Key = key[edge] };
        }

        /// <summary>Mesh faces for one colour of floor or wall.</summary>
        private void March(float[] f, int sub, bool wall)
        {
            float top = wall ? -1f : 0f, bottom = wall ? 0f : 1f;
            var total = wall ? _wallT : _floorT;
            var full = Cells(f, wall ? 1 : 0, sub % 12, poly =>
            {
                // top, wound clockwise for the camera
                EarClip(poly, (a, b, c) => Tri(sub, a, c, b, top));
                // sides along the outline, unless another colour continues on the other side
                for (var k = 0; k < poly.Count; k++)
                {
                    var a = poly[k]; var b = poly[(k + 1) % poly.Count];
                    if (a.Key == -1 || b.Key == -1) continue;
                    float ex = b.X - a.X, ey = b.Y - a.Y;
                    var len = (float)Math.Sqrt(ex * ex + ey * ey);
                    if (len < 1e-5f) continue;
                    float mx = (a.X + b.X) / 2f + ey / len * Cell * 0.5f, my = (a.Y + b.Y) / 2f - ex / len * Cell * 0.5f;
                    if (Sample(total, mx, my) < 0f) continue;
                    Side(sub, a.X, a.Y, b.X, b.Y, top, bottom);
                }
            });
            // fully inside cells, merged into rectangles
            var rects = new List<int[]>();
            Merge(full, _w - 1, _h - 1, rects);
            foreach (var r in rects)
            {
                float x0 = SX(r[0]), y0 = SY(r[1]), x1 = SX(r[2] + 1), y1 = SY(r[3] + 1);
                Faces.Add(new Face
                {
                    Sub = sub, Count = 4,
                    A = new V3(x0, y1, top), B = new V3(x1, y1, top), C = new V3(x1, y0, top), D = new V3(x0, y0, top),
                    UA = new V2(x0, y1), UB = new V2(x1, y1), UC = new V2(x1, y0), UD = new V2(x0, y0),
                });
            }
        }

        /// <summary>
        /// Where colours meet, each colour's corner gets slightly rounded by the sampling, which would leave pinholes
        /// (e.g. where four tiles of two colours meet). Under those cells, draw the whole layer's shape a hair lower in
        /// the colour that's there.
        /// </summary>
        private void FillCracks(float[][] colors, float[] total, int subBase, float top)
        {
            var z = top + 0.01f;
            var poly = new List<Pt>(8);
            for (var j = 0; j < _h - 1; j++)
            {
                for (var i = 0; i < _w - 1; i++)
                {
                    int k0 = j * _w + i, k1 = k0 + 1, k2 = k0 + _w + 1, k3 = k0 + _w;
                    if (total[k0] >= 0f && total[k1] >= 0f && total[k2] >= 0f && total[k3] >= 0f) continue;
                    var present = 0; var best = -1; var bestV = float.MaxValue;
                    for (var c = 0; c < colors.Length; c++)
                    {
                        var f = colors[c];
                        if (f == null) continue;
                        if (f[k0] < 0f || f[k1] < 0f || f[k2] < 0f || f[k3] < 0f) present++;
                        var v = f[k0] + f[k1] + f[k2] + f[k3];
                        if (v < bestV) { bestV = v; best = c; }
                    }
                    if (present < 2 || best < 0) continue;
                    poly.Clear();
                    CellPoly(total, i, j, poly);
                    for (var k = 1; k + 1 < poly.Count; k++) Tri(subBase + best, poly[0], poly[k + 1], poly[k], z);
                }
            }
        }

        /// <summary>The inside part of one cell as a single convex polygon (saddles taken as connected).</summary>
        private void CellPoly(float[] f, int i, int j, List<Pt> poly)
        {
            var cx = new[] { SX(i), SX(i + 1), SX(i + 1), SX(i) };
            var cy = new[] { SY(j), SY(j), SY(j + 1), SY(j + 1) };
            var v = new[] { f[j * _w + i], f[j * _w + i + 1], f[(j + 1) * _w + i + 1], f[(j + 1) * _w + i] };
            var key = new[] { HKey(i, j), VKey(i + 1, j), HKey(i, j + 1), VKey(i, j) };
            for (var k = 0; k < 4; k++)
            {
                var k2 = (k + 1) % 4;
                if (v[k] < 0f) poly.Add(new Pt { X = cx[k], Y = cy[k], Key = -1 });
                if ((v[k] < 0f) != (v[k2] < 0f)) poly.Add(Cross(cx, cy, v, key, k, k2, k));
            }
        }

        private void Tri(int sub, Pt a, Pt b, Pt c, float z)
        {
            Faces.Add(new Face
            {
                Sub = sub, Count = 3,
                A = new V3(a.X, a.Y, z), B = new V3(b.X, b.Y, z), C = new V3(c.X, c.Y, z),
                UA = new V2(a.X, a.Y), UB = new V2(b.X, b.Y), UC = new V2(c.X, c.Y),
            });
        }

        private void Side(int sub, float ax, float ay, float bx, float by, float top, float bottom)
        {
            // inside is on the left of a -> b, so this faces outwards (same winding as the game's sides)
            float dx = bx - ax, dy = by - ay;
            var len = (float)Math.Sqrt(dx * dx + dy * dy);
            float ua = (ax * dx + ay * dy) / len, ub = ua + len;
            Faces.Add(new Face
            {
                Sub = sub, Count = 4,
                A = new V3(ax, ay, top), B = new V3(bx, by, top), C = new V3(bx, by, bottom), D = new V3(ax, ay, bottom),
                UA = new V2(ua, top), UB = new V2(ub, top), UC = new V2(ub, bottom), UD = new V2(ua, bottom),
            });
        }

        /// <summary>
        /// Points for the bots' pathfinding: a grid over the floor, clear of edges and walls, about <paramref name="max"/>
        /// of them at most. Returns the spacing used.
        /// </summary>
        public float PathNodes(int max, List<V2> output)
        {
            const float clear = 0.7f;
            var floorCells = 0;
            foreach (var v in _floorT) if (v < 0f) floorCells++;
            var area = floorCells * Cell * Cell;
            var spacing = Math.Max(2.5f, (float)Math.Sqrt(area / Math.Max(1, max)));
            for (var pass = 0; pass < 2; pass++)
            {
                output.Clear();
                var off = pass * spacing / 2f; // second try: shifted half a step, if the first grid missed narrow spots badly
                var x0 = (float)Math.Ceiling((MinX - off) / spacing) * spacing + off;
                var y0 = (float)Math.Ceiling((MinY - off) / spacing) * spacing + off;
                for (var y = y0; y <= MaxY; y += spacing)
                    for (var x = x0; x <= MaxX; x += spacing)
                        if (Clearance(x, y) >= clear) output.Add(new V2(x, y));
                if (output.Count >= 4) break;
            }
            return spacing;
        }

        private int[] _clear; // cells to the nearest spot that isn't open floor (no floor, or wall)

        /// <summary>Roughly how far (map units) a point is from the floor's edge or a wall.</summary>
        private float Clearance(float x, float y)
        {
            if (_clear == null)
            {
                var n = _w * _h;
                _clear = new int[n];
                var q = new Queue<int>();
                for (var k = 0; k < n; k++)
                {
                    if (_floorT[k] >= 0f || _wallT[k] < 0f) { _clear[k] = 0; q.Enqueue(k); }
                    else _clear[k] = int.MaxValue;
                }
                while (q.Count > 0)
                {
                    var k = q.Dequeue();
                    var d = _clear[k] + 1;
                    int i = k % _w, j = k / _w;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int ni = i + dx, nj = j + dy;
                            if (ni < 0 || nj < 0 || ni >= _w || nj >= _h) continue;
                            var nk = nj * _w + ni;
                            if (_clear[nk] <= d) continue;
                            _clear[nk] = d;
                            q.Enqueue(nk);
                        }
                }
            }
            var ci = (int)Math.Round((x - _bx) / Cell - OX); var cj = (int)Math.Round((y - _by) / Cell - OY);
            if (ci < 0 || cj < 0 || ci >= _w || cj >= _h) return 0f;
            return _clear[cj * _w + ci] * Cell;
        }

        /// <summary>Closed outlines of one colour of floor or wall (the map maker fills these).</summary>
        public List<List<V2>> ColorOutlines(bool wall, int color)
        {
            var list = new List<List<V2>>();
            var f = wall ? _wallC[color] : _floorC[color];
            if (f == null) return list;
            NewField();
            Outlines(f, wall ? 1 : 0, color, list);
            NewField();
            return list;
        }

        /// <summary>Signed distance-ish to the floor edge (negative on the floor, incl. under walls).</summary>
        public float FloorDistance(float x, float y) { return Sample(_floorT, x, y); }
        /// <summary>Signed distance-ish to the walls (negative inside a wall).</summary>
        public float WallDistance(float x, float y) { return Sample(_wallT, x, y); }

        /// <summary>Bilinear sample of a field at a point.</summary>
        private float Sample(float[] f, float x, float y)
        {
            var fx = (x - _bx) / Cell - OX; var fy = (y - _by) / Cell - OY;
            var i = (int)Math.Floor(fx); var j = (int)Math.Floor(fy);
            if (i < 0 || j < 0 || i >= _w - 1 || j >= _h - 1) return Big;
            float tx = fx - i, ty = fy - j;
            var a = f[j * _w + i] + (f[j * _w + i + 1] - f[j * _w + i]) * tx;
            var b = f[(j + 1) * _w + i] + (f[(j + 1) * _w + i + 1] - f[(j + 1) * _w + i]) * tx;
            return a + (b - a) * ty;
        }

        // ------------------------------------------------------------------ outlines

        private void Outlines(float[] f, int layer, int color, List<List<V2>> output)
        {
            var next = new Dictionary<int, int>();   // segment start edge -> end edge
            var pos = new Dictionary<int, V2>();
            var mid = new Dictionary<int, V2>();     // sharp corner after this crossing
            Cells(f, layer, color, poly =>
            {
                for (var k = 0; k < poly.Count; k++)
                {
                    var a = poly[k]; var b = poly[(k + 1) % poly.Count];
                    if (a.Key < 0) continue;
                    if (b.Key == -2)
                    {
                        mid[a.Key] = new V2(b.X, b.Y);
                        b = poly[(k + 2) % poly.Count];
                    }
                    if (b.Key < 0) continue;
                    next[a.Key] = b.Key;
                    pos[a.Key] = new V2(a.X, a.Y);
                    pos[b.Key] = new V2(b.X, b.Y);
                }
            });
            // Fully inside runs have no crossings, so every outline comes from partial cells.
            var used = new HashSet<int>();
            foreach (var start in next.Keys)
            {
                if (used.Contains(start)) continue;
                var loop = new List<V2>();
                var k = start;
                while (!used.Contains(k))
                {
                    used.Add(k);
                    loop.Add(pos[k]);
                    V2 corner;
                    if (mid.TryGetValue(k, out corner)) loop.Add(corner);
                    int nk;
                    if (!next.TryGetValue(k, out nk)) break;
                    k = nk;
                }
                Simplify(loop);
                if (loop.Count >= 3) output.Add(loop);
            }
        }

        /// <summary>Drops points that lie on a straight line between their neighbours.</summary>
        private static void Simplify(List<V2> loop)
        {
            var changed = true;
            while (changed && loop.Count > 3)
            {
                changed = false;
                for (var i = 0; i < loop.Count && loop.Count > 3; i++)
                {
                    var a = loop[(i + loop.Count - 1) % loop.Count]; var b = loop[i]; var c = loop[(i + 1) % loop.Count];
                    var ab = b - a; var bc = c - b;
                    var cross = ab.X * bc.Y - ab.Y * bc.X;
                    if (Math.Abs(cross) < 1e-5f && V2.Dot(ab, bc) >= 0f) { loop.RemoveAt(i); i--; changed = true; }
                }
            }
        }

        // ------------------------------------------------------------------ boxes

        /// <summary>Pixels (one per sample) merged into rectangles.</summary>
        private void Boxes(bool[] mask, List<Box> output)
        {
            var rects = new List<int[]>();
            Merge(mask, _w, _h, rects);
            foreach (var r in rects)
                output.Add(new Box(SX(r[0]) - Cell / 2f, SY(r[1]) - Cell / 2f, SX(r[2]) + Cell / 2f, SY(r[3]) + Cell / 2f));
        }

        /// <summary>Greedy rectangle merge: {i0, j0, i1, j1} inclusive.</summary>
        private static void Merge(bool[] mask, int w, int h, List<int[]> output)
        {
            var used = new bool[mask.Length];
            for (var j = 0; j < h; j++)
            {
                for (var i = 0; i < w; i++)
                {
                    var k = j * w + i;
                    if (!mask[k] || used[k]) continue;
                    var i1 = i;
                    while (i1 + 1 < w && mask[j * w + i1 + 1] && !used[j * w + i1 + 1]) i1++;
                    var j1 = j;
                    while (j1 + 1 < h)
                    {
                        var ok = true;
                        for (var x = i; x <= i1 && ok; x++) ok = mask[(j1 + 1) * w + x] && !used[(j1 + 1) * w + x];
                        if (!ok) break;
                        j1++;
                    }
                    for (var y = j; y <= j1; y++)
                        for (var x = i; x <= i1; x++) used[y * w + x] = true;
                    output.Add(new[] { i, j, i1, j1 });
                }
            }
        }

        /// <summary>Samples outside the floor, clear of its edge, within 2 units of it.</summary>
        private bool[] Ring(bool[] ground)
        {
            var dist = new int[ground.Length];
            var q = new Queue<int>();
            for (var k = 0; k < ground.Length; k++)
            {
                if (ground[k]) { dist[k] = 0; q.Enqueue(k); }
                else dist[k] = int.MaxValue;
            }
            while (q.Count > 0)
            {
                var k = q.Dequeue();
                var d = dist[k] + 1;
                if (d > ForcefieldCells) continue;
                int i = k % _w, j = k / _w;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int ni = i + dx, nj = j + dy;
                        if (ni < 0 || nj < 0 || ni >= _w || nj >= _h) continue;
                        var nk = nj * _w + ni;
                        if (dist[nk] <= d) continue;
                        dist[nk] = d;
                        q.Enqueue(nk);
                    }
            }
            var ring = new bool[ground.Length];
            for (var k = 0; k < ground.Length; k++)
                ring[k] = !ground[k] && dist[k] <= ForcefieldCells && _floorT[k] > Cell;
            return ring;
        }
    }
}
