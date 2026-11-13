using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CP99Online.Maps
{
    // Plain C# (no Unity) so the Windows map maker can share this file.

    /// <summary>A 2D point in map units (1 unit = 1 Arena Creator tile). Origin is the map centre, y is up.</summary>
    internal struct V2
    {
        public float X, Y;
        public V2(float x, float y) { X = x; Y = y; }
        public static V2 operator +(V2 a, V2 b) { return new V2(a.X + b.X, a.Y + b.Y); }
        public static V2 operator -(V2 a, V2 b) { return new V2(a.X - b.X, a.Y - b.Y); }
        public static V2 operator *(V2 a, float s) { return new V2(a.X * s, a.Y * s); }
        public float Length { get { return (float)Math.Sqrt(X * X + Y * Y); } }
        public static float Dot(V2 a, V2 b) { return a.X * b.X + a.Y * b.Y; }
        public override string ToString() { return X.ToString("0.###", CultureInfo.InvariantCulture) + "," + Y.ToString("0.###", CultureInfo.InvariantCulture); }
    }

    /// <summary>What a shape does to the map, applied in file order (later shapes paint over earlier ones).</summary>
    internal enum MapOp
    {
        Floor,  // paints floor of a colour (removes walls there)
        Wall,   // paints wall of a colour (walls always have floor under them)
        Hole,   // removes floor and walls
        Unwall, // removes walls, keeps the floor
    }

    internal enum ShapeKind { Rect, Circle, Poly, Arc }

    /// <summary>Which side of a line a shape is limited to (used by mirroring): x &lt;= V, x &gt;= V, y &lt;= V, y &gt;= V.</summary>
    internal enum KeepSide { Left, Right, Below, Above }

    internal struct Keep
    {
        public KeepSide Side;
        public float V;
        public Keep(KeepSide side, float v) { Side = side; V = v; }
    }

    internal sealed class MapShape
    {
        public MapOp Op;
        public int Color; // 0-11, the Arena Creator palette (ignored for Hole / Unwall)
        public ShapeKind Kind;
        /// <summary>
        /// Rect: x0 y0 x1 y1 [corner radius]. Circle: cx cy r. Poly: x y x y ... (3+ points).
        /// Arc: cx cy radius startDeg endDeg thickness (a band along the circle, counter-clockwise from start to end, round ends).
        /// </summary>
        public float[] P;
        /// <summary>Only the part of the shape on these sides counts (written as "keep left 0" etc.).</summary>
        public List<Keep> Keeps = new List<Keep>();

        public MapShape Clone() { return new MapShape { Op = Op, Color = Color, Kind = Kind, P = (float[])P.Clone(), Keeps = new List<Keep>(Keeps) }; }
    }

    internal enum ObjKind { Goal, Spawn, Boost, Bumper, Spike }

    internal sealed class MapObject
    {
        public ObjKind Kind;
        public int Team; // Goal / Spawn: 1 or 2
        public float X, Y;
        public float Angle; // degrees (reserved; the game's objects are round)
        public MapObject Clone() { return (MapObject)MemberwiseClone(); }
    }

    /// <summary>
    /// An UltraPuck map (.upmap). Text, one item per line:
    /// <code>
    /// UltraPuck map 1
    /// name My Arena
    /// tagline Short description
    /// background 1
    /// players 2 4
    /// difficulty 1
    /// floor 0 rect -10 -6 10 6
    /// wall 3 arc 0 0 6 0 180 1
    /// hole circle 0 0 1.5
    /// goal 1 -9 0
    /// spawn 2 4 0
    /// boost 0 3
    /// </code>
    /// </summary>
    internal sealed class MapData
    {
        public const string Header = "UltraPuck map";
        public const int FormatVersion = 1;
        public const int Colors = 12;

        public string Name = "New map";
        public string Tagline = string.Empty;
        public int Background = 1;
        public int MinPlayers = 2, MaxPlayers = 4;
        public int Difficulty = 1;
        public readonly List<MapShape> Shapes = new List<MapShape>();
        public readonly List<MapObject> Objects = new List<MapObject>();

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------ text

        public string Write()
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append(' ').Append(FormatVersion).Append('\n');
            sb.Append("name ").Append(Clean(Name)).Append('\n');
            sb.Append("tagline ").Append(Clean(Tagline)).Append('\n');
            sb.Append("background ").Append(Background).Append('\n');
            sb.Append("players ").Append(MinPlayers).Append(' ').Append(MaxPlayers).Append('\n');
            sb.Append("difficulty ").Append(Difficulty).Append('\n');
            foreach (var s in Shapes)
            {
                sb.Append(OpName(s.Op));
                if (s.Op == MapOp.Floor || s.Op == MapOp.Wall) sb.Append(' ').Append(s.Color);
                sb.Append(' ').Append(s.Kind.ToString().ToLowerInvariant());
                foreach (var f in s.P) sb.Append(' ').Append(Num(f));
                foreach (var k in s.Keeps) sb.Append(" keep ").Append(k.Side.ToString().ToLowerInvariant()).Append(' ').Append(Num(k.V));
                sb.Append('\n');
            }
            foreach (var o in Objects)
            {
                sb.Append(o.Kind.ToString().ToLowerInvariant());
                if (o.Kind == ObjKind.Goal || o.Kind == ObjKind.Spawn) sb.Append(' ').Append(o.Team);
                sb.Append(' ').Append(Num(o.X)).Append(' ').Append(Num(o.Y));
                if (o.Angle != 0f) sb.Append(' ').Append(Num(o.Angle));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string Clean(string s) { return (s ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim(); }
        internal static string Num(float f) { return Math.Round(f, 4).ToString("0.####", Inv); }

        private static string OpName(MapOp op)
        {
            switch (op)
            {
                case MapOp.Floor: return "floor";
                case MapOp.Wall: return "wall";
                case MapOp.Hole: return "hole";
                default: return "unwall";
            }
        }

        /// <summary>Reads a map. Throws FormatException with the line number on bad input.</summary>
        public static MapData Parse(string text)
        {
            var m = new MapData();
            var lines = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            var sawHeader = false;
            for (var n = 0; n < lines.Length; n++)
            {
                var line = lines[n].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                try
                {
                    if (!sawHeader)
                    {
                        if (!line.StartsWith(Header, StringComparison.Ordinal)) throw new FormatException("not an UltraPuck map");
                        var ver = int.Parse(line.Substring(Header.Length).Trim(), Inv);
                        if (ver > FormatVersion) throw new FormatException("made with a newer UltraPuck (map version " + ver + ")");
                        sawHeader = true;
                        continue;
                    }
                    var sp = line.IndexOf(' ');
                    var key = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
                    var rest = sp < 0 ? string.Empty : line.Substring(sp + 1).Trim();
                    var t = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    switch (key)
                    {
                        case "name": m.Name = rest; break;
                        case "tagline": m.Tagline = rest; break;
                        case "background": m.Background = int.Parse(t[0], Inv); break;
                        case "players": m.MinPlayers = int.Parse(t[0], Inv); m.MaxPlayers = int.Parse(t[1], Inv); break;
                        case "difficulty": m.Difficulty = int.Parse(t[0], Inv); break;
                        case "floor": m.Shapes.Add(ReadShape(MapOp.Floor, t, true)); break;
                        case "wall": m.Shapes.Add(ReadShape(MapOp.Wall, t, true)); break;
                        case "hole": m.Shapes.Add(ReadShape(MapOp.Hole, t, false)); break;
                        case "unwall": m.Shapes.Add(ReadShape(MapOp.Unwall, t, false)); break;
                        case "goal": m.Objects.Add(ReadObj(ObjKind.Goal, t, true)); break;
                        case "spawn": m.Objects.Add(ReadObj(ObjKind.Spawn, t, true)); break;
                        case "boost": m.Objects.Add(ReadObj(ObjKind.Boost, t, false)); break;
                        case "bumper": m.Objects.Add(ReadObj(ObjKind.Bumper, t, false)); break;
                        case "spike": m.Objects.Add(ReadObj(ObjKind.Spike, t, false)); break;
                        default: break; // unknown lines are skipped, so newer files still mostly load
                    }
                }
                catch (FormatException e) { throw new FormatException("line " + (n + 1) + ": " + e.Message); }
                catch (Exception e) when (e is IndexOutOfRangeException || e is OverflowException || e is ArgumentException)
                {
                    throw new FormatException("line " + (n + 1) + ": can't read \"" + line + "\"");
                }
            }
            if (!sawHeader) throw new FormatException("not an UltraPuck map");
            return m;
        }

        private static MapShape ReadShape(MapOp op, string[] t, bool colored)
        {
            var i = 0;
            var s = new MapShape { Op = op };
            if (colored) s.Color = Math.Max(0, Math.Min(Colors - 1, int.Parse(t[i++], Inv)));
            switch (t[i++].ToLowerInvariant())
            {
                case "rect": s.Kind = ShapeKind.Rect; break;
                case "circle": s.Kind = ShapeKind.Circle; break;
                case "poly": s.Kind = ShapeKind.Poly; break;
                case "arc": s.Kind = ShapeKind.Arc; break;
                default: throw new FormatException("unknown shape \"" + t[i - 1] + "\"");
            }
            var p = new List<float>();
            for (; i < t.Length && t[i].ToLowerInvariant() != "keep"; i++) p.Add(float.Parse(t[i], NumberStyles.Float, Inv));
            s.P = p.ToArray();
            while (i + 2 < t.Length && t[i].ToLowerInvariant() == "keep")
            {
                KeepSide side;
                switch (t[i + 1].ToLowerInvariant())
                {
                    case "left": side = KeepSide.Left; break;
                    case "right": side = KeepSide.Right; break;
                    case "below": side = KeepSide.Below; break;
                    case "above": side = KeepSide.Above; break;
                    default: throw new FormatException("unknown keep side \"" + t[i + 1] + "\"");
                }
                s.Keeps.Add(new Keep(side, float.Parse(t[i + 2], NumberStyles.Float, Inv)));
                i += 3;
            }
            var need = s.Kind == ShapeKind.Rect ? 4 : s.Kind == ShapeKind.Circle ? 3 : s.Kind == ShapeKind.Arc ? 6 : 6;
            if (s.P.Length < need || (s.Kind == ShapeKind.Poly && s.P.Length % 2 != 0)) throw new FormatException("not enough numbers for a " + s.Kind.ToString().ToLowerInvariant());
            return s;
        }

        private static MapObject ReadObj(ObjKind kind, string[] t, bool team)
        {
            var i = 0;
            var o = new MapObject { Kind = kind };
            if (team) o.Team = int.Parse(t[i++], Inv) == 2 ? 2 : 1;
            o.X = float.Parse(t[i++], NumberStyles.Float, Inv);
            o.Y = float.Parse(t[i++], NumberStyles.Float, Inv);
            if (i < t.Length) o.Angle = float.Parse(t[i], NumberStyles.Float, Inv);
            return o;
        }

        /// <summary>Stable fingerprint of the map contents (FNV-1a over the written text).</summary>
        public static uint Hash(string text)
        {
            uint h = 2166136261;
            foreach (var ch in text) { h ^= ch; h *= 16777619; }
            return h;
        }

        // ------------------------------------------------------------------ symmetry

        /// <summary>
        /// Makes the map symmetric: the <paramref name="source"/> half (of the line x = 0 or y = 0) is kept and copied,
        /// mirrored, over the other half. Shapes stay exact (each copy is limited to its own half with a "keep" line).
        /// Goals and spawns change team when mirrored across the line between the teams.
        /// </summary>
        public void Mirror(KeepSide source)
        {
            var acrossX = source == KeepSide.Left || source == KeepSide.Right; // mirror line is x = 0
            var other = Opposite(source);
            var kept = new List<MapShape>();
            var copies = new List<MapShape>();
            foreach (var s in Shapes)
            {
                var a = s.Clone();
                a.Keeps.Add(new Keep(source, 0f));
                if (Empty(MapGeometry.Bounds(a))) continue;
                kept.Add(a);
                var b = Flip(s, acrossX);
                b.Keeps.Add(new Keep(other, 0f));
                copies.Add(b);
            }
            Shapes.Clear();
            Shapes.AddRange(kept);
            Shapes.AddRange(copies);

            var swap = acrossX ? GoalsSideBySide() : !GoalsSideBySide();
            var objs = new List<MapObject>();
            const float eps = 0.01f;
            foreach (var o in Objects)
            {
                var c = acrossX ? o.X : o.Y;
                var onSource = source == KeepSide.Left || source == KeepSide.Below ? c < -eps : c > eps;
                if (Math.Abs(c) <= eps) { objs.Add(o); continue; }
                if (!onSource) continue;
                objs.Add(o);
                var m = o.Clone();
                if (acrossX) m.X = -m.X; else m.Y = -m.Y;
                if (swap && (m.Kind == ObjKind.Goal || m.Kind == ObjKind.Spawn)) m.Team = m.Team == 1 ? 2 : 1;
                objs.Add(m);
            }
            Objects.Clear();
            Objects.AddRange(objs);
        }

        private static bool Empty(Box b) { return b.X1 - b.X0 < 1e-4f || b.Y1 - b.Y0 < 1e-4f; }

        internal static KeepSide Opposite(KeepSide s)
        {
            switch (s)
            {
                case KeepSide.Left: return KeepSide.Right;
                case KeepSide.Right: return KeepSide.Left;
                case KeepSide.Below: return KeepSide.Above;
                default: return KeepSide.Below;
            }
        }

        /// <summary>A shape mirrored across x = 0 (acrossX) or y = 0.</summary>
        internal static MapShape Flip(MapShape s, bool acrossX)
        {
            var m = s.Clone();
            var p = m.P;
            switch (m.Kind)
            {
                case ShapeKind.Rect:
                    if (acrossX) { var x0 = p[0]; p[0] = -p[2]; p[2] = -x0; }
                    else { var y0 = p[1]; p[1] = -p[3]; p[3] = -y0; }
                    break;
                case ShapeKind.Circle:
                    if (acrossX) p[0] = -p[0]; else p[1] = -p[1];
                    break;
                case ShapeKind.Arc:
                {
                    float a0 = p[3], a1 = p[4];
                    if (acrossX) { p[0] = -p[0]; p[3] = 180f - a1; p[4] = 180f - a0; }
                    else { p[1] = -p[1]; p[3] = -a1; p[4] = -a0; }
                    break;
                }
                default:
                    for (var i = 0; i + 1 < p.Length; i += 2) { if (acrossX) p[i] = -p[i]; else p[i + 1] = -p[i + 1]; }
                    break;
            }
            for (var i = 0; i < m.Keeps.Count; i++)
            {
                var k = m.Keeps[i];
                var flips = acrossX ? (k.Side == KeepSide.Left || k.Side == KeepSide.Right) : (k.Side == KeepSide.Below || k.Side == KeepSide.Above);
                if (flips) m.Keeps[i] = new Keep(Opposite(k.Side), -k.V);
            }
            return m;
        }

        /// <summary>Are the teams' goals left and right of each other (else above and below)?</summary>
        private bool GoalsSideBySide()
        {
            float x1 = 0, y1 = 0, x2 = 0, y2 = 0; int n1 = 0, n2 = 0;
            foreach (var o in Objects)
            {
                if (o.Kind != ObjKind.Goal) continue;
                if (o.Team == 1) { x1 += o.X; y1 += o.Y; n1++; } else { x2 += o.X; y2 += o.Y; n2++; }
            }
            if (n1 > 0 && n2 > 0) return Math.Abs(x1 / n1 - x2 / n2) >= Math.Abs(y1 / n1 - y2 / n2);
            if (n1 > 0) return Math.Abs(x1) >= Math.Abs(y1);
            if (n2 > 0) return Math.Abs(x2) >= Math.Abs(y2);
            return true;
        }
    }
}
