using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CP99Online.Maps;

namespace UltraPuckMapMaker
{
    // What the property panel edits. Each wrapper changes the live map; the form commits an undo step afterwards.

    internal enum TileColour { White, LightGrey, DarkGrey, Black, Red, Orange, Yellow, Green, Cyan, Blue, Purple, Brown }

    internal enum PaintKind { Floor, Wall, Hole, RemoveWall }

    internal enum Team { Red = 1, Blue = 2 }

    [DefaultProperty("Name")]
    internal sealed class MapProps
    {
        private readonly MapData _m;
        public MapProps(MapData m) { _m = m; }

        [Category("Arena list"), Description("The name in the game's arena list.")]
        public string Name { get { return _m.Name; } set { _m.Name = value ?? string.Empty; } }

        [Category("Arena list"), Description("The line shown under the arena preview.")]
        public string Tagline { get { return _m.Tagline; } set { _m.Tagline = value ?? string.Empty; } }

        [Category("Arena list"), DisplayName("Min players"), Description("Recommended number of players: at least.")]
        public int MinPlayers { get { return _m.MinPlayers; } set { _m.MinPlayers = Math.Max(1, Math.Min(8, value)); } }

        [Category("Arena list"), DisplayName("Max players"), Description("Recommended number of players: at most.")]
        public int MaxPlayers { get { return _m.MaxPlayers; } set { _m.MaxPlayers = Math.Max(1, Math.Min(8, value)); } }

        [Category("Arena list"), Description("Stars in the arena list: 0 to 3 (4 says \"Good luck\").")]
        public int Difficulty { get { return _m.Difficulty; } set { _m.Difficulty = Math.Max(0, Math.Min(4, value)); } }

        [Category("Look"), Description("The background gradient behind the arena, as numbered in the game's Arena Creator (0 and up).")]
        public int Background { get { return _m.Background; } set { _m.Background = Math.Max(0, Math.Min(20, value)); } }
    }

    internal abstract class ShapeProps
    {
        protected readonly MapShape S;
        protected ShapeProps(MapShape s) { S = s; }

        public static ShapeProps For(MapShape s)
        {
            switch (s.Kind)
            {
                case ShapeKind.Rect: return new RectProps(s);
                case ShapeKind.Circle: return new CircleProps(s);
                case ShapeKind.Arc: return new ArcProps(s);
                case ShapeKind.Tiles: return new TilesProps(s);
                default: return new PolyProps(s);
            }
        }

        [Category("Paint"), Description("Floor, wall, hole (removes floor and walls) or remove wall (keeps the floor).")]
        public PaintKind Paint
        {
            get { return S.Op == MapOp.Floor ? PaintKind.Floor : S.Op == MapOp.Wall ? PaintKind.Wall : S.Op == MapOp.Hole ? PaintKind.Hole : PaintKind.RemoveWall; }
            set { S.Op = value == PaintKind.Floor ? MapOp.Floor : value == PaintKind.Wall ? MapOp.Wall : value == PaintKind.Hole ? MapOp.Hole : MapOp.Unwall; }
        }

        [Category("Paint"), Description("Colour of the floor or wall (holes have none).")]
        public TileColour Colour { get { return (TileColour)S.Color; } set { S.Color = (int)value; } }

        [Category("Symmetry"), DisplayName("Kept side"), Description("Set by the Mirror buttons: only this part of the shape counts.")]
        public string Kept
        {
            get { return S.Keeps.Count == 0 ? "(whole shape)" : string.Join(", ", S.Keeps.Select(k => k.Side.ToString().ToLowerInvariant() + " of " + MapData.Num(k.V)).ToArray()); }
        }
    }

    internal sealed class RectProps : ShapeProps
    {
        public RectProps(MapShape s) : base(s) { }

        [Category("Rectangle"), DisplayName("Left x")] public float X0 { get { return Math.Min(S.P[0], S.P[2]); } set { var x1 = X1; S.P[0] = value; S.P[2] = Math.Max(value, x1); } }
        [Category("Rectangle"), DisplayName("Bottom y")] public float Y0 { get { return Math.Min(S.P[1], S.P[3]); } set { var y1 = Y1; S.P[1] = value; S.P[3] = Math.Max(value, y1); } }
        [Category("Rectangle"), DisplayName("Right x")] public float X1 { get { return Math.Max(S.P[0], S.P[2]); } set { var x0 = X0; S.P[2] = value; S.P[0] = Math.Min(value, x0); } }
        [Category("Rectangle"), DisplayName("Top y")] public float Y1 { get { return Math.Max(S.P[1], S.P[3]); } set { var y0 = Y0; S.P[3] = value; S.P[1] = Math.Min(value, y0); } }

        [Category("Rectangle"), DisplayName("Corner radius"), Description("Rounds the corners (0 = square).")]
        public float Corner
        {
            get { return S.P.Length > 4 ? S.P[4] : 0f; }
            set
            {
                var v = Math.Max(0f, value);
                if (S.P.Length > 4) S.P[4] = v;
                else if (v > 0f) S.P = new[] { S.P[0], S.P[1], S.P[2], S.P[3], v };
            }
        }
    }

    internal sealed class CircleProps : ShapeProps
    {
        public CircleProps(MapShape s) : base(s) { }
        [Category("Circle"), DisplayName("Centre x")] public float X { get { return S.P[0]; } set { S.P[0] = value; } }
        [Category("Circle"), DisplayName("Centre y")] public float Y { get { return S.P[1]; } set { S.P[1] = value; } }
        [Category("Circle")] public float Radius { get { return S.P[2]; } set { S.P[2] = Math.Max(0.05f, value); } }
    }

    internal sealed class ArcProps : ShapeProps
    {
        public ArcProps(MapShape s) : base(s) { }
        [Category("Arc"), DisplayName("Centre x")] public float X { get { return S.P[0]; } set { S.P[0] = value; } }
        [Category("Arc"), DisplayName("Centre y")] public float Y { get { return S.P[1]; } set { S.P[1] = value; } }
        [Category("Arc"), Description("Radius of the middle of the band.")] public float Radius { get { return S.P[2]; } set { S.P[2] = Math.Max(0.05f, value); } }
        [Category("Arc"), DisplayName("Start angle"), Description("Degrees, counter-clockwise from the right.")] public float Start { get { return S.P[3]; } set { S.P[3] = value; } }
        [Category("Arc"), DisplayName("End angle"), Description("Degrees, counter-clockwise from the right.")] public float End { get { return S.P[4]; } set { S.P[4] = value; } }
        [Category("Arc"), Description("Width of the band.")] public float Thickness { get { return S.P[5]; } set { S.P[5] = Math.Max(0.05f, value); } }
    }

    internal sealed class TilesProps : ShapeProps
    {
        public TilesProps(MapShape s) : base(s) { }
        [Category("Tiles"), Description("Painted with the Tile shape, one square at a time. Select and drag to move them all.")]
        public int Count { get { return S.P.Length / 3; } }
    }

    internal sealed class PolyProps : ShapeProps
    {
        public PolyProps(MapShape s) : base(s) { }

        [Category("Polygon"), Description("Corners as x y pairs, e.g. \"0 0  2 0  1 2\".")]
        public string Corners
        {
            get
            {
                var parts = new string[S.P.Length / 2];
                for (var i = 0; i < parts.Length; i++) parts[i] = MapData.Num(S.P[2 * i]) + " " + MapData.Num(S.P[2 * i + 1]);
                return string.Join("   ", parts);
            }
            set
            {
                var t = (value ?? string.Empty).Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 6 || t.Length % 2 != 0) throw new ArgumentException("Give 3 or more corners as x y pairs.");
                S.P = t.Select(v => float.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            }
        }
    }

    internal class ObjectProps
    {
        protected readonly MapObject O;
        protected ObjectProps(MapObject o) { O = o; }

        /// <summary>The panel for an object: goals and spawns have a team, boosts a power.</summary>
        public static ObjectProps For(MapObject o)
        {
            if (o.Kind == ObjKind.Goal || o.Kind == ObjKind.Spawn) return new TeamObjectProps(o);
            if (o.Kind == ObjKind.Boost) return new BoostProps(o);
            return new ObjectProps(o);
        }

        [Category("Object"), Description("What this is.")]
        public string Kind { get { return Editor.Describe(O); } }

        [Category("Position")] public float X { get { return O.X; } set { O.X = value; } }
        [Category("Position")] public float Y { get { return O.Y; } set { O.Y = value; } }
    }

    internal sealed class TeamObjectProps : ObjectProps
    {
        public TeamObjectProps(MapObject o) : base(o) { }

        [Category("Object"), Description("Which team it belongs to. Red defends the red goal.")]
        public Team Team { get { return O.Team == 2 ? Team.Blue : Team.Red; } set { O.Team = (int)value; } }
    }

    internal sealed class BoostProps : ObjectProps
    {
        public BoostProps(MapObject o) : base(o) { }

        [Category("Object"), DisplayName("Power (%)"), Description("How hard it launches you, in % of the game's normal boost pad (100). 0 to 500.")]
        public float Power { get { return O.Power; } set { O.Power = Math.Max(0f, Math.Min(500f, value)); } }
    }
}
